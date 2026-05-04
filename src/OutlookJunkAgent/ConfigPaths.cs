using System.Security.Cryptography;

namespace OutlookJunkAgent;

/// <summary>
/// Resolves the on-disk paths for <c>rubric.md</c> and <c>senders.json</c>. Both files are
/// concatenated into the LLM system prompt, so a write to either is equivalent to
/// reprogramming the classifier — including in Phase B, where misclassification means a
/// silently-deleted message. Defending those files matters.
///
/// Preferred location: <c>%LocalAppData%\OutlookJunkAgent\</c> on Windows, or the equivalent
/// per-user directory on Linux/macOS via <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
/// LocalAppData inherits the user's profile ACLs, which are tighter than whatever ACLs the
/// scheduled task's working directory happens to carry — so tampering needs user-token-class
/// access rather than just project-directory write access.
///
/// Fallback: the working directory. Convenient during dev and a soft migration path for users
/// who haven't moved the files yet. The chosen path and a SHA-256 hash of its contents are
/// logged on every run, so silent edits are visible in the audit trail.
/// </summary>
public static class ConfigPaths
{
    public static string SecureDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OutlookJunkAgent");

    public static (string Path, bool Exists) ResolveRubric(string workingDir)
        => Resolve(workingDir, "rubric.md");

    public static (string Path, bool Exists) ResolveSenders(string workingDir)
        => Resolve(workingDir, "senders.json");

    private static (string Path, bool Exists) Resolve(string workingDir, string filename)
    {
        var secure = Path.Combine(SecureDir, filename);
        if (File.Exists(secure)) return (secure, true);
        var fallback = Path.Combine(workingDir, filename);
        return (fallback, File.Exists(fallback));
    }

    /// <summary>
    /// Returns the first 16 hex characters of the file's SHA-256, or "(missing)" if the file
    /// does not exist. Short prefix is plenty for visual change-detection — collision space is
    /// 2^64 — and keeps the run-summary line readable.
    /// </summary>
    public static string Sha256Short(string path)
    {
        if (!File.Exists(path)) return "(missing)";
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }
}
