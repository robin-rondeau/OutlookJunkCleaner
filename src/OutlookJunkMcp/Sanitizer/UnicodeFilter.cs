using System.Globalization;
using System.Text;

namespace OutlookJunkMcp.Sanitizer;

/// <summary>
/// Code-point hygiene shared by EmailSanitizer (untrusted email content) and ReasonValidator
/// (untrusted reason strings). Strips classes that have no place in plain text the LLM is going
/// to read: format characters (zero-width, bidi overrides, the Tag block), private-use code
/// points, unmatched surrogates, and control characters except TAB/LF/CR.
/// </summary>
public static class UnicodeFilter
{
    public static bool ShouldStrip(Rune r)
    {
        var v = r.Value;
        var cat = Rune.GetUnicodeCategory(r);
        return cat switch
        {
            UnicodeCategory.Format => true,
            UnicodeCategory.PrivateUse => true,
            UnicodeCategory.Surrogate => true,
            UnicodeCategory.Control => v != 0x09 && v != 0x0A && v != 0x0D,
            _ => false,
        };
    }

    public static string Clean(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var normalized = input.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (!ShouldStrip(rune)) sb.Append(rune.ToString());
        }
        return sb.ToString();
    }
}
