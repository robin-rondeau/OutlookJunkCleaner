using OutlookJunkAgent.Drivers;

namespace OutlookJunkAgent;

/// <summary>
/// Deterministic pre-filter that runs before the LLM. If a message matches a domain on the
/// user's trusted list it is short-circuited to <c>not_junk</c> (routed to Triage); if it
/// matches the known-junk list it is short-circuited to <c>confident_junk</c>. Both bypass
/// the LLM entirely — saving the per-message API cost and removing non-determinism for
/// patterns the user has already labelled.
///
/// Trust order: trusted list takes precedence over junk list if a domain ends up on both,
/// because the cost of a false confident_junk (auto-delete in Phase B) is higher than the cost
/// of a false rescue-to-Triage (user sees one extra message). The LLM remains the fallback for
/// everything that doesn't match either list.
///
/// Subdomain matching: sender domain matches a list entry if it equals it exactly, OR if it
/// ends with <c>"." + listEntry</c>. So <c>mail.linkedin.com</c> matches list entry
/// <c>linkedin.com</c>, but <c>evil-linkedin.com</c> does not.
/// </summary>
public sealed class HeuristicClassifier
{
    private readonly SendersConfig _senders;

    public HeuristicClassifier(SendersConfig senders)
    {
        _senders = senders;
    }

    public HeuristicDecision? Classify(MessageContent message)
    {
        var domain = (message.SenderDomain ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(domain) || domain == "<none>") return null;

        foreach (var entry in _senders.Trusted)
        {
            if (DomainMatches(domain, entry.Domain))
            {
                return new HeuristicDecision(
                    Action: ClassificationAction.NotJunk,
                    Reason: $"trusted-sender list match: {entry.Domain}",
                    HeuristicId: "trusted-list");
            }
        }

        foreach (var entry in _senders.Junk)
        {
            if (DomainMatches(domain, entry.Domain))
            {
                return new HeuristicDecision(
                    Action: ClassificationAction.ConfidentJunk,
                    Reason: $"known-junk-sender list match: {entry.Domain}",
                    HeuristicId: "junk-list");
            }
        }

        return null;
    }

    private static bool DomainMatches(string senderDomain, string listEntry)
    {
        return senderDomain == listEntry
            || senderDomain.EndsWith("." + listEntry, StringComparison.Ordinal);
    }
}

public sealed record HeuristicDecision(
    ClassificationAction Action,
    string Reason,
    string HeuristicId);
