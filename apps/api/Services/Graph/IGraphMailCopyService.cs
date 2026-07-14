using Microsoft.Graph;

namespace MigrationPlatform.Api.Services.Graph;

public record MailCopyProgress(
    int FoldersCopied,
    int TotalFolders,
    int MessagesCopied,
    int TotalMessages);

/// <summary>
/// Outcome of a full-mailbox copy. <paramref name="MessagesFailed"/> counts messages
/// that were skipped after retries; <paramref name="AttachmentsSkipped"/> counts
/// item/reference attachments that cannot round-trip via Graph. A non-zero value in
/// either means the copy is complete-with-gaps — callers must surface that rather
/// than reporting a clean sync.
/// </summary>
public record MailCopyResult(
    int MessagesCopied,
    int TotalMessages,
    int MessagesFailed,
    int AttachmentsSkipped,
    string? FirstError);

public interface IGraphMailCopyService
{
    Task<MailCopyResult> CopyUserMailAsync(
        GraphServiceClient sourceClient,
        GraphServiceClient targetClient,
        string sourceUserId,
        string targetUserId,
        string? targetFolderName,
        Action<MailCopyProgress> onProgress,
        CancellationToken ct);
}
