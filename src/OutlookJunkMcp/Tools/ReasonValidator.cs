using System.Text;
using System.Text.RegularExpressions;
using OutlookJunkMcp.Sanitizer;

namespace OutlookJunkMcp.Tools;

/// <summary>
/// Server-side reason-string hygiene. Applied at the entry of every mutating tool so the audit
/// log is protected against control-character injection, runaway lengths, and zero-width / bidi
/// shenanigans regardless of who the caller is (cron host, interactive Claude Code, future
/// agent driver).
///
/// UTF-8 is preserved (interactive callers may have non-English reasons); the host-side
/// ReasonHygiene applies a stricter ASCII-only policy on top, since LLM-emitted reasons should
/// be short English.
/// </summary>
public static class ReasonValidator
{
    public const int MaxLength = 200;
    private const string Empty = "(no reason supplied)";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Empty;
        var stripped = UnicodeFilter.Clean(raw);
        var sb = new StringBuilder(stripped.Length);
        foreach (var ch in stripped)
        {
            sb.Append(ch is '\r' or '\n' or '\t' ? ' ' : ch);
        }
        var collapsed = Whitespace.Replace(sb.ToString(), " ").Trim();
        if (collapsed.Length == 0) return Empty;
        if (collapsed.Length > MaxLength)
        {
            collapsed = collapsed[..MaxLength].TrimEnd() + "…";
        }
        return collapsed;
    }
}
