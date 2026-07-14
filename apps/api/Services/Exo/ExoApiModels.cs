namespace MigrationPlatform.Api.Services.Exo;

/// <summary>Statistics for a single Exchange Online mailbox.</summary>
public record ExoMailboxStats(
    long ItemCount,
    long TotalItemSizeBytes,
    DateTime? LastLogonTime);

/// <summary>Archive mailbox presence and size for a single Exchange Online mailbox.</summary>
public record ExoArchiveInfo(
    bool HasArchive,
    long ArchiveSizeBytes);

/// <summary>Aggregate status of an EXO migration batch.</summary>
public record ExoBatchStatus(
    string Status,           // EXO batch status string e.g. "Syncing", "Completed"
    int SyncedCount,
    int FinalizedCount,
    int FailedCount,
    int TotalCount);

/// <summary>Per-user migration state within an EXO migration batch.</summary>
public record ExoMigrationUser(
    string EmailAddress,
    string Status,           // "Syncing", "Synced", "Failed", etc.
    string? Error);

/// <summary>Result returned after successfully creating an EXO migration batch.</summary>
public record ExoBatchCreationResult(
    string BatchId,
    string Status);

/// <summary>
/// Attributes captured from a source mailbox needed to provision a matching
/// MailUser stub on the target tenant for native cross-tenant MRS moves.
/// LegacyExchangeDN is stamped on the target as <c>x500:&lt;value&gt;</c> so
/// inbound mail and Outlook auto-complete continue resolving after migration.
/// </summary>
public record ExoMailboxAttributes(
    string PrimarySmtpAddress,
    Guid ExchangeGuid,
    Guid ArchiveGuid,
    string LegacyExchangeDN,
    IReadOnlyList<string> X500Addresses,
    string DisplayName,
    string Alias);

/// <summary>
/// Raw response from a diagnostic InvokeCommand call — gives the caller everything
/// needed to render a self-diagnosing report: HTTP status, headers, response body
/// (with NUL-padding fingerprinting), and parsed result objects (when JSON).
/// </summary>
public record ExoRawInvokeResult(
    string CmdletName,
    int HttpStatus,
    string? ReasonPhrase,
    int BodyByteLength,
    string BodyHexPreview,
    string? BodyTextPreview,
    string? XExceptionType,
    string? RequestId,
    string? WwwAuthenticate,
    string? ParsedErrorMessage,
    bool IsSuccess,
    System.Text.Json.JsonElement[] Results,
    string TokenClaims);
