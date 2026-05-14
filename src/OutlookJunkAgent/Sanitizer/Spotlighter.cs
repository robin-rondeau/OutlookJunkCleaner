using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OutlookJunkAgent.Sanitizer;

/// <summary>
/// Wraps an email payload in run-randomised delimiters and emits the spotlighted text the LLM
/// will see as its user-message content. The system prompt warns the LLM that anything inside
/// the markers is data, not instructions.
///
/// The 16-hex run token is generated fresh per agent process so a static injection corpus
/// cannot pre-bake the delimiter string. Inside the payload, any literal "EMAIL_BEGIN-" /
/// "EMAIL_END-" prefix (with any hex tail) is replaced with "[delim]" defensively.
/// </summary>
public sealed class Spotlighter
{
    private readonly string _runToken;
    // Case-insensitive: the system-prompt markers are uppercase and the LLM only treats
    // uppercase as authoritative, but we scrub any case-variation defensively so a mixed-case
    // marker in the body cannot slip through if a future model relaxes its case-sensitivity.
    private static readonly Regex DelimLike = new(
        @"EMAIL_(BEGIN|END)-[a-fA-F0-9]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Spotlighter() : this(GenerateRunToken()) { }

    public Spotlighter(string runToken)
    {
        _runToken = runToken;
    }

    public string RunToken => _runToken;
    public string BeginMarker => $"EMAIL_BEGIN-{_runToken}";
    public string EndMarker => $"EMAIL_END-{_runToken}";

    public string Wrap(MessageContent details)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BeginMarker);
        sb.AppendLine($"sender:           {Escape(details.Sender)}");
        sb.AppendLine($"sender-domain:    {Escape(details.SenderDomain)}");
        sb.AppendLine($"subject:          {Escape(details.Subject)}");
        sb.AppendLine($"received-at:      {details.ReceivedAt:o}");
        sb.AppendLine($"folder:           {Escape(details.Folder)}");
        sb.AppendLine($"is-read:          {details.IsRead}");
        sb.AppendLine($"has-attachments:  {details.HasAttachments}");
        sb.AppendLine($"list-unsubscribe: {Escape(details.ListUnsubscribe ?? "<none>")}");

        var authResults = details.RelevantHeaders
            .FirstOrDefault(h => string.Equals(h.Name, "Authentication-Results", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        sb.AppendLine($"auth-results:     {Escape(authResults ?? "<none>")}");

        var messageId = details.RelevantHeaders
            .FirstOrDefault(h => string.Equals(h.Name, "Message-ID", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        sb.AppendLine($"message-id:       {Escape(messageId ?? "<none>")}");

        // Microsoft EOP's spam-confidence level (SCL). Integer; 5+ means EOP classified the
        // message as spam upstream, 7+ is high-confidence spam, -1 is trusted (skipped
        // filtering). We pass the raw value through so the rubric can refer to it directly.
        var scl = details.RelevantHeaders
            .FirstOrDefault(h => string.Equals(h.Name, "X-MS-Exchange-Organization-SCL", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        sb.AppendLine($"microsoft-spam-confidence: {Escape(scl?.Trim() ?? "<none>")}");

        var replyTo = details.RelevantHeaders
            .FirstOrDefault(h => string.Equals(h.Name, "Reply-To", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (!string.IsNullOrEmpty(replyTo))
        {
            sb.AppendLine($"reply-to:         {Escape(replyTo)}");
        }

        // RFC 5322 Sender: header is only emitted when present. Absent ⇒ the From: author
        // is also the agent of submission, which is the common case and not worth a noise
        // line. When present, the rubric should compare it against sender-domain: above to
        // detect relayed mail (calendar invites, mailing-list relays).
        var senderHeader = details.RelevantHeaders
            .FirstOrDefault(h => string.Equals(h.Name, "Sender", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (!string.IsNullOrEmpty(senderHeader))
        {
            sb.AppendLine($"sender-header:    {Escape(senderHeader)}");
        }

        // RFC 3834 Auto-Submitted: only emitted when present. Real human-to-human mail
        // never sets this; anything non-empty (auto-generated, auto-replied,
        // auto-notified) is a "machine wrote this" signal.
        var autoSubmitted = details.RelevantHeaders
            .FirstOrDefault(h => string.Equals(h.Name, "Auto-Submitted", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (!string.IsNullOrEmpty(autoSubmitted))
        {
            sb.AppendLine($"auto-submitted:   {Escape(autoSubmitted.Trim())}");
        }

        if (details.Images.Count > 0)
        {
            sb.AppendLine($"images ({details.Images.Count}):");
            foreach (var img in details.Images)
            {
                sb.AppendLine($"  - alt: {Escape(img.Alt)}");
            }
        }

        if (details.Links.Count > 0)
        {
            sb.AppendLine($"links ({details.Links.Count}):");
            foreach (var lnk in details.Links)
            {
                var flag = lnk.HostMismatchHint ? "  [HOST-MISMATCH]" : "";
                sb.AppendLine($"  - text: {Escape(lnk.VisibleText)}");
                sb.AppendLine($"    href: {Escape(lnk.Href)}{flag}");
            }
        }

        sb.AppendLine("body:");
        sb.AppendLine(Escape(details.Body));
        if (details.BodyTruncated)
        {
            sb.AppendLine("[body was truncated by sanitizer]");
        }
        sb.AppendLine(EndMarker);
        return sb.ToString();
    }

    private string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return DelimLike.Replace(s, "[delim]");
    }

    private static string GenerateRunToken()
    {
        var bytes = new byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
