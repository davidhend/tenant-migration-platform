namespace MigrationPlatform.Api.Models;

public enum ScanType { Full, Users, Mailboxes, SharePoint, OneDrive, Domains }
public enum ScanStatus { Queued, Running, Completed, Failed }

public class ScanSummary
{
    public int UserCount { get; set; }
    public int GroupCount { get; set; }
    public int MailboxCount { get; set; }
    public double MailboxTotalSizeGb { get; set; }
    public int SiteCount { get; set; }
    public int OneDriveCount { get; set; }
    public int DomainCount { get; set; }
    public int BlockerCount { get; set; }
    public int WarningCount { get; set; }
    public int ReadinessScore { get; set; }
}

public class Scan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ProjectId { get; set; }
    public ScanType ScanType { get; set; }
    public ScanStatus Status { get; set; } = ScanStatus.Queued;
    public int Progress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public ScanSummary? Summary { get; set; }
}

public class ScannedUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScanId { get; set; }
    public string SourceObjectId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Upn { get; set; } = string.Empty;
    public bool AccountEnabled { get; set; }

    /// <summary>True when the user is synced from on-prem AD (Entra Connect).</summary>
    public bool DirectorySynced { get; set; }

    public List<string> Licenses { get; set; } = [];
    public bool HasMailbox { get; set; }
    public double MailboxSizeGb { get; set; }
    public string? MailboxType { get; set; }
    public double OneDriveSizeGb { get; set; }
    public bool MfaEnabled { get; set; }

    /// <summary>
    /// All SMTP addresses on the source mailbox / MailUser (primary + aliases),
    /// in the raw Graph form (e.g. <c>SMTP:user@source.com</c>, <c>smtp:alias@source.com</c>).
    /// Stamped onto the target user's <c>proxyAddresses</c> by the DirectGraph
    /// migration path so mail to source-domain addresses still routes after
    /// cutover. Native MRS copies these automatically and ignores this field.
    /// </summary>
    public List<string> ProxyAddresses { get; set; } = [];
}

public class ScannedGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScanId { get; set; }
    public string SourceObjectId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GroupType { get; set; } = string.Empty;
    public bool MailEnabled { get; set; }
    public bool SecurityEnabled { get; set; }
    public int MemberCount { get; set; }
}

public class ScannedMailbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScanId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string PrimarySmtpAddress { get; set; } = string.Empty;
    public string MailboxType { get; set; } = "UserMailbox";
    public double SizeGb { get; set; }
    public int ItemCount { get; set; }
    public DateTime? LastLogonTime { get; set; }
    public bool HasArchive { get; set; }
    public double? ArchiveSizeGb { get; set; }
}

public class ScannedSite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScanId { get; set; }
    public string SiteUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public double StorageUsedGb { get; set; }
    public double StorageQuotaGb { get; set; }
    public List<string> Owners { get; set; } = [];
    public DateTime? LastActivityDate { get; set; }
    public bool HasUniquePermissions { get; set; }
    public int SubsiteCount { get; set; }
}

public class ScannedOneDrive
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScanId { get; set; }
    public string OwnerUpn { get; set; } = string.Empty;
    public string OwnerDisplayName { get; set; } = string.Empty;
    public string DriveUrl { get; set; } = string.Empty;
    public double StorageUsedGb { get; set; }
    public double StorageQuotaGb { get; set; }
    public DateTime? LastModified { get; set; }
    public int FileCount { get; set; }
}

public class ScannedDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScanId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsVerified { get; set; }
    public int UserCount { get; set; }
}

public enum IssueSeverity { Blocker, Warning, Info }

public class ScanIssue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScanId { get; set; }
    public IssueSeverity Severity { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int AffectedObjectCount { get; set; }
    public List<string> RemediationSteps { get; set; } = [];
}
