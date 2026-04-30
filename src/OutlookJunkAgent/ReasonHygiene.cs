using System.Text;
using System.Text.RegularExpressions;

namespace OutlookJunkAgent;

/// <summary>
/// Host-side reason cleaner. The LLM emits reasons that should be short, English, ASCII-printable
/// — anything else is suspicious or a mistake. Stricter than the server-side ReasonValidator
/// (which preserves UTF-8 for interactive callers) because LLM-generated text never legitimately
/// needs anything outside the printable ASCII range. Defense-in-depth: the server will also clean.
/// </summary>
public static class ReasonHygiene
{
    public const int MaxLength = 200;
    private const string Empty = "(no reason supplied)";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Empty;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is '\r' or '\n' or '\t')
            {
                sb.Append(' ');
            }
            else if (ch >= 0x20 && ch <= 0x7E)
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('?');
            }
        }

        var collapsed = Whitespace.Replace(sb.ToString(), " ").Trim();
        if (collapsed.Length == 0) return Empty;
        if (collapsed.Length > MaxLength)
        {
            collapsed = collapsed[..MaxLength].TrimEnd() + "...";
        }
        return collapsed;
    }
}
