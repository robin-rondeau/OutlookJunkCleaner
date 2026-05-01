using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OutlookJunkAgent;

/// <summary>
/// Loads and validates the structured sender lists from senders.json. The file is the user-
/// editable surface for trusted (will-not-be-confident-junk) and known-junk sender domains. It
/// is concatenated into the system prompt by RubricLoader; today the LLM does the matching, but
/// the format is structured so a future deterministic pre-filter (review item A4) can consume
/// the same file without parsing markdown.
///
/// Tolerant: malformed entries are skipped with a warning rather than failing the run, since
/// this file is hand-edited.
/// </summary>
public static class SendersStore
{
    private static readonly Regex DomainShape = new(
        @"^[a-z0-9]([a-z0-9\-]{0,62}\.)+[a-z]{2,24}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<SendersConfig> LoadAsync(string path, ILogger log, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            log.LogInformation("senders.json not found at {Path}; continuing with no structured sender lists.", path);
            return SendersConfig.Empty;
        }

        FileShape? raw;
        try
        {
            await using var stream = File.OpenRead(path);
            raw = await JsonSerializer.DeserializeAsync<FileShape>(stream, JsonOpts, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "senders.json is not valid JSON; ignoring file. Fix the syntax to re-enable.");
            return SendersConfig.Empty;
        }

        if (raw is null) return SendersConfig.Empty;

        var trusted = NormalizeAndValidate(raw.Trusted, "trusted", log);
        var junk = NormalizeAndValidate(raw.Junk, "junk", log);

        log.LogInformation(
            "senders.json loaded: trusted={T} junk={J}",
            trusted.Count, junk.Count);

        return new SendersConfig(trusted, junk);
    }

    private static IReadOnlyList<SenderEntry> NormalizeAndValidate(
        List<RawEntry>? raw, string section, ILogger log)
    {
        if (raw is null || raw.Count == 0) return Array.Empty<SenderEntry>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<SenderEntry>(raw.Count);
        foreach (var entry in raw)
        {
            var d = entry.Domain?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(d))
            {
                log.LogWarning("senders.json: skipping {Section} entry with empty domain.", section);
                continue;
            }
            if (!DomainShape.IsMatch(d))
            {
                log.LogWarning("senders.json: skipping {Section} entry '{Domain}' — does not look like a domain.", section, d);
                continue;
            }
            if (!seen.Add(d))
            {
                log.LogWarning("senders.json: skipping duplicate {Section} entry '{Domain}'.", section, d);
                continue;
            }
            var note = (entry.Note ?? "").Trim();
            if (note.Length > 200) note = note[..200];
            output.Add(new SenderEntry(d, note));
        }
        return output;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class FileShape
    {
        [JsonPropertyName("trusted")] public List<RawEntry>? Trusted { get; set; }
        [JsonPropertyName("junk")] public List<RawEntry>? Junk { get; set; }
    }

    private sealed class RawEntry
    {
        [JsonPropertyName("domain")] public string? Domain { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}

public sealed record SenderEntry(string Domain, string Note);

public sealed record SendersConfig(
    IReadOnlyList<SenderEntry> Trusted,
    IReadOnlyList<SenderEntry> Junk)
{
    public static readonly SendersConfig Empty = new(
        Array.Empty<SenderEntry>(),
        Array.Empty<SenderEntry>());
}
