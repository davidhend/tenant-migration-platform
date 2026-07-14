namespace MigrationPlatform.Api.Models;

public enum ValidationCheckType { Mailbox, OneDrive, SharePoint, User }
public enum ValidationOutcome { Pass, Fail, Warning }

/// <summary>
/// A single post-migration validation check verifying that a migrated object
/// is accessible in the target tenant.
/// </summary>
public class ValidationCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public ValidationRun? Run { get; set; }

    public ValidationCheckType CheckType { get; set; }

    /// <summary>Source identifier — UPN for mailboxes, URL for SharePoint/OneDrive.</summary>
    public string SourceReference { get; set; } = string.Empty;

    /// <summary>Target identifier after domain transformation / migration.</summary>
    public string TargetReference { get; set; } = string.Empty;

    public ValidationOutcome Outcome { get; set; }

    /// <summary>Populated when Outcome is Fail or Warning.</summary>
    public string? ErrorMessage { get; set; }

    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}
