using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace MigrationPlatform.Api.Services.Graph;

public sealed class DomainManagementClient : IDomainManagementClient
{
    private readonly ILogger<DomainManagementClient> _logger;

    public DomainManagementClient(ILogger<DomainManagementClient> logger) => _logger = logger;

    public async Task<int> GetDomainReferenceCountAsync(
        GraphServiceClient client, string domainName, CancellationToken ct)
    {
        var refs = await client.Domains[domainName].DomainNameReferences
            .GetAsync(r => r.QueryParameters.Top = 999, ct);

        var count = refs?.Value?.Count ?? 0;
        _logger.LogInformation("Graph: domain '{Domain}' has {Count} directory object references.", domainName, count);
        return count;
    }

    public async Task ForceDeleteDomainAsync(
        GraphServiceClient client, string domainName, CancellationToken ct)
    {
        _logger.LogInformation("Graph: force-deleting domain '{Domain}'.", domainName);

        await client.Domains[domainName].ForceDelete
            .PostAsync(new Microsoft.Graph.Domains.Item.ForceDelete.ForceDeletePostRequestBody
            {
                DisableUserAccounts = false,
            }, cancellationToken: ct);

        _logger.LogInformation("Graph: domain '{Domain}' force-deleted successfully.", domainName);
    }

    public async Task<bool> AddDomainAsync(
        GraphServiceClient client, string domainName, CancellationToken ct)
    {
        _logger.LogInformation("Graph: adding domain '{Domain}' to target tenant.", domainName);

        try
        {
            await client.Domains.PostAsync(new Domain { Id = domainName }, cancellationToken: ct);
            _logger.LogInformation("Graph: domain '{Domain}' added to target tenant.", domainName);
            return true;
        }
        catch (ODataError ex) when (
            ex.ResponseStatusCode == 409 ||
            (ex.Error?.Message?.Contains("already exist", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            _logger.LogInformation("Graph: domain '{Domain}' already exists in target tenant.", domainName);
            return false;
        }
    }

    public async Task<string?> GetVerificationTxtRecordAsync(
        GraphServiceClient client, string domainName, CancellationToken ct)
    {
        var records = await client.Domains[domainName].VerificationDnsRecords
            .GetAsync(cancellationToken: ct);

        if (records?.Value is null) return null;

        foreach (var record in records.Value)
        {
            if (record is DomainDnsTxtRecord txt && txt.Text is not null)
            {
                _logger.LogInformation(
                    "Graph: domain '{Domain}' verification TXT record: {Value}.",
                    domainName, txt.Text);
                return txt.Text;
            }
        }

        _logger.LogWarning("Graph: no TXT verification record found for domain '{Domain}'.", domainName);
        return null;
    }

    public async Task<bool> VerifyDomainAsync(
        GraphServiceClient client, string domainName, CancellationToken ct)
    {
        _logger.LogInformation("Graph: verifying domain '{Domain}'.", domainName);

        try
        {
            var result = await client.Domains[domainName].Verify
                .PostAsync(cancellationToken: ct);

            var verified = result?.IsVerified ?? false;
            _logger.LogInformation("Graph: domain '{Domain}' verify result: isVerified={Verified}.", domainName, verified);
            return verified;
        }
        catch (ODataError ex)
        {
            _logger.LogWarning(ex,
                "Graph: domain '{Domain}' verification failed: {Message}.",
                domainName, ex.Error?.Message);
            return false;
        }
    }

    public async Task UpdateUserUpnAsync(
        GraphServiceClient client, string objectId, string newUpn, CancellationToken ct)
    {
        await client.Users[objectId].PatchAsync(new User
        {
            UserPrincipalName = newUpn,
        }, cancellationToken: ct);

        _logger.LogDebug("Graph: updated UPN for user {ObjectId} → {NewUpn}.", objectId, newUpn);
    }
}
