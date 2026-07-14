namespace MigrationPlatform.Api.Models;

/// <summary>
/// Phases of a domain cutover job. The job pauses at DNS-dependent phases
/// (AwaitingDnsVerification, AwaitingMxUpdate) and requires admin confirmation to continue.
/// </summary>
public enum DomainCutoverPhase
{
    /// <summary>Job created, waiting for admin to start.</summary>
    Created,
    /// <summary>Renaming source-tenant users off the domain to .onmicrosoft.com.</summary>
    CleaningSource,
    /// <summary>Force-deleting the domain from the source tenant.</summary>
    RemovingDomain,
    /// <summary>Waiting for Microsoft to release the domain (polling POST /domains on target).</summary>
    WaitingForRelease,
    /// <summary>Domain added to target tenant; waiting for admin to add DNS TXT record.</summary>
    AwaitingDnsVerification,
    /// <summary>Polling domain verification on the target tenant.</summary>
    VerifyingDomain,
    /// <summary>Reassigning UPNs and mailbox SMTP addresses to the domain in the target tenant.</summary>
    AssigningUsers,
    /// <summary>Users assigned; waiting for admin to update MX/SPF/DKIM/autodiscover DNS records.</summary>
    AwaitingMxUpdate,
    /// <summary>Domain cutover completed successfully.</summary>
    Completed,
    /// <summary>Domain cutover failed — see ErrorMessage for details.</summary>
    Failed,
}

/// <summary>
/// Represents a domain cutover job that moves a custom domain from the source tenant
/// to the target tenant and reassigns it to migrated users.
/// </summary>
public class DomainCutoverJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public MigrationProject? Project { get; set; }

    /// <summary>The custom domain being moved, e.g. "fabrikam.com".</summary>
    public string DomainName { get; set; } = string.Empty;

    public DomainCutoverPhase Phase { get; set; } = DomainCutoverPhase.Created;

    /// <summary>Total users in the target tenant that will be reassigned to this domain.</summary>
    public int TotalUsers { get; set; }
    /// <summary>Users successfully reassigned so far.</summary>
    public int CompletedUsers { get; set; }
    /// <summary>Users that failed reassignment.</summary>
    public int FailedUsers { get; set; }

    /// <summary>
    /// DNS TXT verification record value required by the target tenant.
    /// Populated after the domain is added to the target (e.g. "MS=msXXXXXXXX").
    /// </summary>
    public string? DnsVerificationRecord { get; set; }

    /// <summary>
    /// Target MX record value to display to the admin after domain verification.
    /// E.g. "fabrikam-com.mail.protection.outlook.com".
    /// </summary>
    public string? TargetMxRecord { get; set; }

    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastUpdatedAt { get; set; }
}
