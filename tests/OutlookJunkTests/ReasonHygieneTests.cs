using OutlookJunkAgent;
using OutlookJunkMcp.Tools;
using Xunit;

namespace OutlookJunkTests;

/// <summary>
/// Tests for both reason cleaners. The host-side <see cref="ReasonHygiene"/> is ASCII-strict
/// (the LLM has no business emitting non-ASCII reasons); the server-side <see cref="ReasonValidator"/>
/// preserves UTF-8 since interactive callers may emit accented or non-Latin reasons. Both must:
/// strip control characters, neutralise CRLF (so a malicious reason cannot inject log lines),
/// collapse whitespace, and cap length.
/// </summary>
public class ReasonHygieneTests
{
    // ===== Agent-side: ReasonHygiene (ASCII-strict) ============================================

    [Fact]
    public void AgentSideReplacesNonAsciiWithQuestionMark()
    {
        var cleaned = ReasonHygiene.Clean("café résumé 🚀 done");
        Assert.DoesNotContain("é", cleaned);
        Assert.DoesNotContain("🚀", cleaned);
        Assert.Contains("?", cleaned);
        Assert.Contains("done", cleaned);
    }

    [Fact]
    public void AgentSideReplacesCrlfWithSpace()
    {
        var cleaned = ReasonHygiene.Clean("first line\r\nsecond line\rthird\nfourth");
        Assert.DoesNotContain("\r", cleaned);
        Assert.DoesNotContain("\n", cleaned);
        Assert.Contains("first line", cleaned);
        Assert.Contains("fourth", cleaned);
    }

    [Fact]
    public void AgentSideStripsAnsiEscapeSequences()
    {
        // ANSI escape begins with U+001B (ESC, control char). The cleaner replaces every byte
        // outside printable ASCII with '?', so the escape introducer becomes '?' and the rest
        // (e.g. "[31m") survives literally — which is fine: the LLM does not interpret it.
        // The point is that no actual escape sequence makes it through.
        var cleaned = ReasonHygiene.Clean("good[31mevil[0mtext");
        Assert.DoesNotContain("", cleaned);
        Assert.Contains("good", cleaned);
        Assert.Contains("text", cleaned);
    }

    [Fact]
    public void AgentSideCollapsesRunsOfWhitespace()
    {
        var cleaned = ReasonHygiene.Clean("a  b\t\tc\n\nd");
        Assert.Equal("a b c d", cleaned);
    }

    [Fact]
    public void AgentSideCapsLengthAt200WithEllipsis()
    {
        var input = new string('x', 250);
        var cleaned = ReasonHygiene.Clean(input);
        Assert.True(cleaned.Length <= ReasonHygiene.MaxLength + "...".Length);
        Assert.EndsWith("...", cleaned);
    }

    [Fact]
    public void AgentSideEmptyInputReturnsPlaceholder()
    {
        Assert.Equal("(no reason supplied)", ReasonHygiene.Clean(null));
        Assert.Equal("(no reason supplied)", ReasonHygiene.Clean(""));
        Assert.Equal("(no reason supplied)", ReasonHygiene.Clean("   \t\r\n  "));
    }

    [Fact]
    public void AgentSidePreservesPrintableAscii()
    {
        var cleaned = ReasonHygiene.Clean("lottery-style subject; SPF fail; unknown sender");
        Assert.Equal("lottery-style subject; SPF fail; unknown sender", cleaned);
    }

    // ===== Server-side: ReasonValidator (UTF-8 preserving, control-class stripping) ===========

    [Fact]
    public void ServerSidePreservesUnicodeAccents()
    {
        var cleaned = ReasonValidator.Clean("café résumé done");
        Assert.Contains("café", cleaned);
        Assert.Contains("résumé", cleaned);
    }

    [Fact]
    public void ServerSideStripsZeroWidthSpace()
    {
        // Same prompt-injection class as the email body: zero-width chars must be removed
        // before they get logged.
        var cleaned = ReasonValidator.Clean("link​edin click here");
        Assert.DoesNotContain("​", cleaned);
        Assert.Contains("linkedin", cleaned);
    }

    [Fact]
    public void ServerSideStripsBidiOverrides()
    {
        var cleaned = ReasonValidator.Clean("evil‮audit-bypass");
        Assert.DoesNotContain("‮", cleaned);
    }

    [Fact]
    public void ServerSideReplacesCrlfWithSpace()
    {
        var cleaned = ReasonValidator.Clean("AUDIT injected\r\nFAKE LOG LINE");
        Assert.DoesNotContain("\r", cleaned);
        Assert.DoesNotContain("\n", cleaned);
        Assert.Contains("AUDIT injected", cleaned);
        Assert.Contains("FAKE LOG LINE", cleaned);
    }

    [Fact]
    public void ServerSideCollapsesWhitespace()
    {
        var cleaned = ReasonValidator.Clean("a  b\t\tc\n\nd");
        Assert.Equal("a b c d", cleaned);
    }

    [Fact]
    public void ServerSideCapsLengthAt200WithEllipsis()
    {
        var input = new string('x', 250);
        var cleaned = ReasonValidator.Clean(input);
        // Server uses U+2026 ellipsis (one char), unlike agent's three-dot "..."
        Assert.True(cleaned.Length <= ReasonValidator.MaxLength + 1);
        Assert.EndsWith("…", cleaned);
    }

    [Fact]
    public void ServerSideEmptyInputReturnsPlaceholder()
    {
        Assert.Equal("(no reason supplied)", ReasonValidator.Clean(null));
        Assert.Equal("(no reason supplied)", ReasonValidator.Clean(""));
        Assert.Equal("(no reason supplied)", ReasonValidator.Clean("   \t\r\n  "));
    }
}
