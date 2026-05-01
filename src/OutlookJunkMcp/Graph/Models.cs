using OutlookJunkMcp.Sanitizer;

namespace OutlookJunkMcp.Graph;

public sealed record JunkMessageInfo(
    string Id,
    string Sender,
    string Subject,
    DateTimeOffset ReceivedAt,
    bool IsRead,
    bool HasAttachments,
    string BodyPreview,
    string? ListUnsubscribe);

public sealed record MessageDetails(
    string Id,
    string Folder,
    string Sender,
    string SenderDomain,
    string Subject,
    DateTimeOffset ReceivedAt,
    bool IsRead,
    bool HasAttachments,
    string Body,
    bool BodyTruncated,
    IReadOnlyList<ImageInfo> Images,
    IReadOnlyList<LinkInfo> Links,
    string? ListUnsubscribe,
    IReadOnlyList<HeaderEntry> RelevantHeaders);

public sealed record HeaderEntry(string Name, string Value);

public sealed record MutationResult(string Id, string Action, string Outcome, string Reason);

public sealed record StatusInfo(
    int JunkCount,
    int JunkUnreadCount,
    int TriageCount,
    bool DeleteEnabled,
    IReadOnlyList<string> AllowedFolders);

public sealed record ClassificationLookupEntry(string Id, string Location);
