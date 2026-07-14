namespace MigrationPlatform.Api.Models;

public enum UserMigrationEntryStatus { Queued, Provisioning, Provisioned, Failed, Skipped }

/// <summary>
/// A single user within a <see cref="UserMigrationBatch"/>. The target user is
/// created as a member account in the target tenant via Graph <c>POST /users</c>.
/// </summary>
public class UserMigrationEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public UserMigrationBatch? Batch { get; set; }
    public string SourceUpn { get; set; } = string.Empty;
    public string TargetUpn { get; set; } = string.Empty;

    /// <summary>
    /// Object ID of the user created in the target tenant by Graph <c>POST /users</c>.
    /// Cached so retries can PATCH the same object rather than trying to create again.
    /// </summary>
    public string? TargetObjectId { get; set; }

    public UserMigrationEntryStatus Status { get; set; } = UserMigrationEntryStatus.Queued;
    public string? ErrorMessage { get; set; }
    public DateTime? LastUpdated { get; set; }
}
