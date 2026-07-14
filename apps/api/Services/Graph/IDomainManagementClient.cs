namespace MigrationPlatform.Api.Services.Graph;

/// <summary>
/// Graph API operations for domain cutover: list references, force-delete,
/// add to tenant, retrieve verification records, verify, and update user UPNs.
/// </summary>
public interface IDomainManagementClient
{
    /// <summary>Returns the count of directory objects referencing the domain.</summary>
    Task<int> GetDomainReferenceCountAsync(
        Microsoft.Graph.GraphServiceClient client, string domainName, CancellationToken ct);

    /// <summary>
    /// Force-deletes the domain from the tenant, auto-renaming all references
    /// to the fallback .onmicrosoft.com domain. Disables no user accounts.
    /// </summary>
    Task ForceDeleteDomainAsync(
        Microsoft.Graph.GraphServiceClient client, string domainName, CancellationToken ct);

    /// <summary>
    /// Adds a domain to the tenant. Returns true if added, false if it already exists.
    /// Throws if Microsoft has not yet released the domain from the source tenant.
    /// </summary>
    Task<bool> AddDomainAsync(
        Microsoft.Graph.GraphServiceClient client, string domainName, CancellationToken ct);

    /// <summary>
    /// Returns the DNS TXT verification record value the admin must add (e.g. "MS=msXXXXXXXX").
    /// Returns null if no TXT record is found.
    /// </summary>
    Task<string?> GetVerificationTxtRecordAsync(
        Microsoft.Graph.GraphServiceClient client, string domainName, CancellationToken ct);

    /// <summary>
    /// Verifies domain ownership after the admin has added the DNS record.
    /// Returns true if verified, false if verification failed (DNS not propagated yet).
    /// </summary>
    Task<bool> VerifyDomainAsync(
        Microsoft.Graph.GraphServiceClient client, string domainName, CancellationToken ct);

    /// <summary>
    /// Updates a user's UPN via Graph PATCH /users/{objectId}.
    /// </summary>
    Task UpdateUserUpnAsync(
        Microsoft.Graph.GraphServiceClient client, string objectId, string newUpn, CancellationToken ct);
}
