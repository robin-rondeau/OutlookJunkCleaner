namespace OutlookJunkAgent.Drivers;

/// <summary>
/// Provider-agnostic abstraction for one-shot junk-mail classification. The host iterates over
/// messages and calls ClassifyAsync once per message; each call gets a fresh LLM context so
/// message N's content cannot influence message N+1. To swap providers, implement a new driver
/// and wire it up in DriverFactory. The MCP server, rubric, and Phase A/B state stay unchanged.
///
/// Note: the LLM no longer chooses tools. The host translates ClassificationResult.Action into
/// the appropriate MCP tool call (mark_as_read / move_to_triage / delete_from_junk). This is
/// the structural enforcement of one-message-per-iteration isolation and removes free-form
/// tool-call surface from the prompt-injection attack surface.
/// </summary>
public interface IAgentDriver
{
    Task<ClassificationResult> ClassifyAsync(ClassificationRequest request, CancellationToken ct);
}

public sealed record ClassificationRequest(
    string SystemPrompt,
    string SpotlightedEmail,
    DateTimeOffset? Deadline = null);

public sealed record ClassificationResult(
    ClassificationAction Action,
    double Confidence,
    string Reason,
    string? RawText);

public enum ClassificationAction
{
    ConfidentJunk,
    Ambiguous,
    NotJunk,
}
