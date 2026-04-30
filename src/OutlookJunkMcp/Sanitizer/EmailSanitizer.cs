using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace OutlookJunkMcp.Sanitizer;

/// <summary>
/// Server-side email content sanitizer. Converts HTML bodies to plain text, drops
/// scripts/styles/hidden subtrees, extracts image alt-text and anchor links into structured
/// fields, and unicode-strips every textual surface. Length-caps are applied at the end.
///
/// Sanitization belongs at the trust boundary so every consumer of get_message benefits —
/// including interactive Claude Code review, not just the cron agent host.
/// </summary>
public sealed class EmailSanitizer
{
    public const int MaxBodyChars = 8000;
    public const int MaxImagesEntries = 32;
    public const int MaxImageAltChars = 200;
    public const int MaxLinksEntries = 64;
    public const int MaxLinkVisibleChars = 100;
    public const int MaxLinkHrefChars = 500;
    public const int MaxSubjectChars = 300;
    public const int MaxSenderChars = 320;
    public const int MaxShortPreviewChars = 1000;

    private static readonly Regex MultiNewline = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex SpaceTabRun = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex DomainLike = new(
        @"\b([a-z0-9][a-z0-9-]{0,62}\.(?:com|org|net|io|co|edu|gov|ca|uk|de|fr|ai|app|dev))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> AlwaysDropTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "meta", "title", "noscript", "template", "iframe", "object", "embed",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "li", "tr", "blockquote", "pre", "section", "article", "header", "footer",
        "h1", "h2", "h3", "h4", "h5", "h6", "td", "th",
    };

    public SanitizedBody SanitizeBody(string? rawBody, bool isHtml)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return new SanitizedBody("", [], [], false, 0);
        }

        string text;
        var images = new List<ImageInfo>();
        var links = new List<LinkInfo>();

        if (isHtml || LooksLikeHtml(rawBody))
        {
            var doc = new HtmlDocument
            {
                OptionAutoCloseOnEnd = true,
                OptionFixNestedTags = true,
            };
            doc.LoadHtml(rawBody);
            var sb = new StringBuilder();
            Walk(doc.DocumentNode, sb, images, links);
            text = sb.ToString();
        }
        else
        {
            text = rawBody;
        }

        text = UnicodeFilter.Clean(text);
        text = NormalizeWhitespace(text);

        var originalLength = text.Length;
        var truncated = false;
        if (text.Length > MaxBodyChars)
        {
            text = text[..MaxBodyChars] + $"\n[...truncated; original was {originalLength} chars]";
            truncated = true;
        }

        if (images.Count > MaxImagesEntries)
        {
            images = images.Take(MaxImagesEntries).ToList();
        }
        if (links.Count > MaxLinksEntries)
        {
            links = links.Take(MaxLinksEntries).ToList();
        }

        return new SanitizedBody(text, images, links, truncated, originalLength);
    }

    public string SanitizeShortText(string? input, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var cleaned = UnicodeFilter.Clean(input);
        cleaned = cleaned.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        cleaned = SpaceTabRun.Replace(cleaned, " ").Trim();
        if (cleaned.Length > maxChars) cleaned = cleaned[..maxChars] + "…";
        return cleaned;
    }

    private void Walk(HtmlNode node, StringBuilder sb, List<ImageInfo> images, List<LinkInfo> links)
    {
        switch (node.NodeType)
        {
            case HtmlNodeType.Comment:
                return;
            case HtmlNodeType.Text:
                sb.Append(HtmlEntity.DeEntitize(((HtmlTextNode)node).Text));
                return;
            case HtmlNodeType.Document:
            case HtmlNodeType.Element:
                ProcessElement(node, sb, images, links);
                return;
        }
    }

    private void ProcessElement(HtmlNode node, StringBuilder sb, List<ImageInfo> images, List<LinkInfo> links)
    {
        var name = node.Name ?? "";

        if (AlwaysDropTags.Contains(name)) return;
        if (HasHidingStyle(node)) return;

        switch (name.ToLowerInvariant())
        {
            case "img":
            {
                var alt = node.GetAttributeValue("alt", "");
                if (!string.IsNullOrWhiteSpace(alt))
                {
                    var cleaned = SanitizeShortText(HtmlEntity.DeEntitize(alt), MaxImageAltChars);
                    if (cleaned.Length > 0 && images.Count < MaxImagesEntries)
                    {
                        images.Add(new ImageInfo(cleaned));
                    }
                }
                return;
            }
            case "a":
            {
                var href = HtmlEntity.DeEntitize(node.GetAttributeValue("href", ""));
                var visStart = sb.Length;
                foreach (var c in node.ChildNodes) Walk(c, sb, images, links);
                var visText = sb.ToString(visStart, sb.Length - visStart);
                if (!string.IsNullOrWhiteSpace(href))
                {
                    var visClean = SanitizeShortText(visText, MaxLinkVisibleChars);
                    var hrefClean = SanitizeShortText(href, MaxLinkHrefChars);
                    var hostMismatch = HasHostMismatch(visClean, hrefClean);
                    if (links.Count < MaxLinksEntries)
                    {
                        links.Add(new LinkInfo(visClean, hrefClean, hostMismatch));
                    }
                }
                return;
            }
            case "br":
                sb.Append('\n');
                return;
        }

        var isBlock = BlockTags.Contains(name);

        foreach (var c in node.ChildNodes)
        {
            Walk(c, sb, images, links);
        }

        if (isBlock && sb.Length > 0 && sb[sb.Length - 1] != '\n')
        {
            sb.Append('\n');
        }
    }

    private static bool HasHidingStyle(HtmlNode node)
    {
        var style = node.GetAttributeValue("style", "");
        if (string.IsNullOrEmpty(style)) return false;
        var compact = style.Replace(" ", "").ToLowerInvariant();
        return compact.Contains("display:none")
            || compact.Contains("visibility:hidden")
            || compact.Contains("font-size:0")
            || compact.Contains("opacity:0");
    }

    internal static bool HasHostMismatch(string visibleText, string href)
    {
        if (string.IsNullOrWhiteSpace(visibleText) || string.IsNullOrWhiteSpace(href)) return false;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return false;
        var hrefHost = uri.Host.ToLowerInvariant();
        if (string.IsNullOrEmpty(hrefHost)) return false;
        foreach (Match m in DomainLike.Matches(visibleText))
        {
            var visibleHost = m.Groups[1].Value.ToLowerInvariant();
            if (!hrefHost.EndsWith(visibleHost, StringComparison.Ordinal)
                && !visibleHost.EndsWith(hrefHost, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool LooksLikeHtml(string s)
    {
        // Cheap heuristic: presence of a closing tag pattern. Graph almost always sets
        // body.contentType, but we hedge for plain-text bodies that contain HTML fragments.
        return s.Contains("</", StringComparison.Ordinal) || s.Contains("<br", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWhitespace(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = SpaceTabRun.Replace(text, " ");
        text = MultiNewline.Replace(text, "\n\n");
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].Trim();
        }
        return string.Join('\n', lines).Trim();
    }
}

public sealed record SanitizedBody(
    string Body,
    IReadOnlyList<ImageInfo> Images,
    IReadOnlyList<LinkInfo> Links,
    bool Truncated,
    int OriginalLength);

public sealed record ImageInfo(string Alt);

public sealed record LinkInfo(string VisibleText, string Href, bool HostMismatchHint);
