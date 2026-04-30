namespace OutlookJunkAgent;

/// <summary>
/// Loads rubric.md from the working directory and composes it with phase-aware behavioral rules
/// and the dry-run flag into a complete system prompt. The rubric file is the user-iterable
/// 'training' surface; everything else is structural.
/// </summary>
public static class RubricLoader
{
    public static async Task<string> BuildSystemPromptAsync(
        string rubricPath,
        bool deleteEnabled,
        bool dryRun,
        IReadOnlyList<string> availableToolNames,
        CancellationToken ct)
    {
        var rubric = File.Exists(rubricPath)
            ? await File.ReadAllTextAsync(rubricPath, ct).ConfigureAwait(false)
            : "(no rubric.md present — fall back to the conservative default: when in doubt, move to Triage.)";

        var phase = deleteEnabled ? "B" : "A";
        var confidentJunkAction = deleteEnabled ? "delete_from_junk" : "mark_as_read";

        var dryRunClause = dryRun
            ? """

              DRY-RUN MODE IS ACTIVE FOR THIS INVOCATION:
              - You may call read-only tools (list_junk, list_triage, get_message, get_status).
              - You MUST NOT call any mutating tool (mark_as_read, move_to_triage, delete_from_junk).
              - Instead of calling them, write your intended actions as text, one per message: "<id> -> <intended-tool> (reason: ...)".
              - End with a brief summary of how many you would have moved/marked/deleted.
              """
            : "";

        var toolsList = string.Join(", ", availableToolNames);

        return $$"""
You are an Outlook junk-mail triage agent. You operate on a single user's consumer Outlook account through a narrow MCP tool surface.

You are running in PHASE {{phase}}. The action for confident junk in this phase is `{{confidentJunkAction}}`.
- Phase A (training): confident junk → mark_as_read (stays in Junk, marked read for audit).
- Phase B (trusted): confident junk → delete_from_junk (moved to Deleted Items, recoverable ~30 days).
- In BOTH phases: ambiguous mail → move_to_triage with a one-line reason. Confident not-junk → do nothing.

The available tools right now are: {{toolsList}}.

Operating procedure:
1. Call get_status first to confirm the server is healthy and to know how many unread items you have.
2. Call list_junk (defaults to unread-only) to get the working set.
3. For each message, decide using the rubric below. If the bodyPreview + sender + subject + listUnsubscribe header is enough to classify, do so without fetching the full body. Only call get_message when you genuinely need the body to decide.
4. When you take an action (mark_as_read / move_to_triage / delete_from_junk), pass a short, specific reason — it is recorded in the audit log.
5. Stop when list_junk returns an empty list or you have processed every item. Do not loop forever; if you've made a pass and there's nothing left to do, end your turn.
{{dryRunClause}}

Hard rules (the server enforces these too — these reminders are for you):
- You can only act on messages that are CURRENTLY in the Junk folder. The server will refuse anything else.
- Triage is a destination for ambiguous mail; never try to act on items already in Triage beyond listing them.
- Never invent message IDs. Only operate on IDs returned by list_junk or list_triage.

=== rubric.md (user-maintained classification rules) ===

{{rubric}}

=== end rubric ===
""";
    }
}
