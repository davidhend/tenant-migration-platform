using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Services.Graph;

/// <summary>
/// Result returned when a Graph synchronisation job is submitted.
/// Stores a composite key <c>{servicePrincipalObjectId}/{jobId}</c> so later
/// operations can reach the same SP without an additional lookup.
/// </summary>
public record GraphSyncJobResult(string CompositeJobId);

/// <summary>Current state of a Graph synchronisation job as reported by the API.</summary>
public record GraphSyncJobStatus(
    string Status,
    int SyncedCount,
    int FailedCount,
    string? ErrorMessage);

/// <summary>Result of a single user's on-demand provisioning attempt.</summary>
public record ProvisionedUserResult(
    string UserObjectId,
    string Status,
    string? ErrorMessage,
    string? TargetObjectId = null);

/// <summary>Result of a provisionOnDemand call containing per-user outcomes.</summary>
public record ProvisionOnDemandResult(IReadOnlyList<ProvisionedUserResult> Users);

/// <summary>
/// Drives Entra ID cross-tenant user synchronization via the Microsoft Graph
/// Synchronization API
/// (<c>/v1.0/servicePrincipals/{spId}/synchronization/jobs</c>).
/// The provisioning application must already be registered in the SOURCE
/// tenant and granted <c>Synchronization.ReadWrite.All</c> +
/// <c>Application.Read.All</c> + <c>User.Read.All</c> with admin consent.
/// </summary>
public interface IGraphSyncClient
{
    /// <summary>
    /// Locates (and starts) the Azure2Azure cross-tenant synchronization job
    /// in <paramref name="sourceTenant"/>. Returns a composite job reference.
    /// If <paramref name="appClientId"/> is non-null we resolve the SP by
    /// <c>appId eq</c> filter; otherwise we scan SPs for one already hosting
    /// the job (the path most users hit, since the wizard creates the app for them).
    /// </summary>
    Task<GraphSyncJobResult> StartSyncJobAsync(
        Tenant sourceTenant,
        string? appClientId,
        CancellationToken ct);

    /// <summary>
    /// Polls Graph for current job state. Returns null on 404.
    /// </summary>
    Task<GraphSyncJobStatus?> GetSyncJobStatusAsync(
        Tenant sourceTenant,
        string compositeJobId,
        CancellationToken ct);

    /// <summary>Pauses the sync job (operator stop request).</summary>
    Task StopSyncJobAsync(
        Tenant sourceTenant,
        string compositeJobId,
        CancellationToken ct);

    /// <summary>Pauses the sync job (operator complete request).</summary>
    Task CompleteSyncJobAsync(
        Tenant sourceTenant,
        string compositeJobId,
        CancellationToken ct);

    /// <summary>
    /// Retrieves and caches the synchronization rule ID from the job schema.
    /// Required as a parameter for <see cref="ProvisionOnDemandAsync"/>.
    /// </summary>
    Task<string> GetSyncJobSchemaRuleIdAsync(
        Tenant sourceTenant,
        string compositeJobId,
        CancellationToken ct);

    /// <summary>
    /// Provisions up to 5 specific users on demand against a running sync job,
    /// bypassing scoping filters.
    /// </summary>
    Task<ProvisionOnDemandResult> ProvisionOnDemandAsync(
        Tenant sourceTenant,
        string compositeJobId,
        string syncRuleId,
        IReadOnlyList<string> userObjectIds,
        CancellationToken ct);

    /// <summary>
    /// Resolves a user's Entra object ID from their UPN. Returns null if not found.
    /// </summary>
    Task<string?> ResolveUserObjectIdAsync(
        Tenant sourceTenant,
        string userPrincipalName,
        CancellationToken ct);
}

/// <summary>
/// Production implementation of <see cref="IGraphSyncClient"/>. Uses the
/// existing <see cref="IGraphClientFactory"/> + <see cref="IKeyVaultCredentialService"/>
/// stack to authenticate as the source tenant.
/// </summary>
public sealed class GraphSyncClient : IGraphSyncClient
{
    /// <summary>
    /// Template ID prefix for Entra cross-tenant provisioning jobs. Matches
    /// "Azure2Azure" (GA), "Azure2AzureProvisioning" / "Azure2Azure_Preview"
    /// (gallery preview vintages) — case-insensitive prefix match.
    /// </summary>
    private const string CrossTenantSyncTemplateIdPrefix = "Azure2Azure";

    /// <summary>
    /// Template ID written when creating a NEW sync job. The GA "Azure2Azure"
    /// template works against any cross-tenant sync SP; preview-only IDs are
    /// readable but not creatable.
    /// </summary>
    private const string CrossTenantSyncCreateTemplateId = "Azure2Azure";

    /// <summary>
    /// Cap how many SPs we scan when locating the sync job. CTS apps are
    /// typically created early in tenant lifetime so a few hundred is plenty.
    /// </summary>
    private const int MaxServicePrincipalsScanned = 500;

    private static bool IsCrossTenantSyncTemplate(string? templateId) =>
        !string.IsNullOrEmpty(templateId) &&
        templateId.StartsWith(CrossTenantSyncTemplateIdPrefix, StringComparison.OrdinalIgnoreCase);

    private readonly IGraphClientFactory _graphFactory;
    private readonly IKeyVaultCredentialService _keyVault;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GraphSyncClient> _logger;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _ruleIdCache = new();

    public GraphSyncClient(
        IGraphClientFactory graphFactory,
        IKeyVaultCredentialService keyVault,
        IConfiguration configuration,
        ILogger<GraphSyncClient> logger)
    {
        _graphFactory = graphFactory;
        _keyVault = keyVault;
        _configuration = configuration;
        _logger = logger;
    }

    // ── IGraphSyncClient ─────────────────────────────────────────────────────

    public async Task<GraphSyncJobResult> StartSyncJobAsync(
        Tenant sourceTenant,
        string? appClientId,
        CancellationToken ct)
    {
        var client = await CreateClientAsync(sourceTenant, ct);

        // Resolve the SP that hosts the Azure2Azure job.
        string spId;
        SynchronizationJob? job = null;

        var pinnedSpId = _configuration["Platform:CrossTenantSync:SourceServicePrincipalObjectId"];

        if (!string.IsNullOrWhiteSpace(appClientId))
        {
            spId = await GetServicePrincipalObjectIdAsync(client, appClientId, ct);
            job = await TryFindAzure2AzureJobOnSpAsync(client, spId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(pinnedSpId))
        {
            _logger.LogInformation(
                "Using pinned cross-tenant sync SP {SpId} (Platform:CrossTenantSync:SourceServicePrincipalObjectId).",
                pinnedSpId);
            spId = pinnedSpId;
            job = await TryFindAzure2AzureJobOnSpAsync(client, spId, ct);
        }
        else
        {
            var found = await FindFirstAzure2AzureSpAsync(client, ct)
                ?? throw new InvalidOperationException(
                    "No service principal in the source tenant has a cross-tenant sync job (template prefix 'Azure2Azure*'). " +
                    "Either run the cross-tenant sync wizard in Entra ID → Cross-tenant synchronization, or pin the existing app via " +
                    "Platform:CrossTenantSync:SourceServicePrincipalObjectId in appsettings.json.");
            spId = found.SpId;
            job = found.Job;
        }

        // No job yet on this SP — create one.
        if (job is null)
        {
            _logger.LogInformation(
                "Creating Azure2Azure sync job on SP {SpId}.", spId);
            try
            {
                job = await client.ServicePrincipals[spId].Synchronization.Jobs.PostAsync(
                    new SynchronizationJob { TemplateId = CrossTenantSyncCreateTemplateId }, null, ct);
            }
            catch (ODataError ex) when (
                ex.Error?.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true ||
                ex.Error?.Code?.Equals("AlreadyExists", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Replication lag, or job lives on a different (older) SP.
                _logger.LogInformation(
                    "Sync job already exists on SP {SpId} — waiting briefly and re-fetching.", spId);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                job = await TryFindAzure2AzureJobOnSpAsync(client, spId, ct);
            }
            catch (ODataError ex)
            {
                throw new InvalidOperationException(
                    $"Graph error creating sync job on SP '{spId}': {ex.Error?.Message ?? ex.Message}", ex);
            }
        }

        if (job?.Id is null)
            throw new InvalidOperationException(
                $"Could not retrieve a synchronization job on SP '{spId}'. The Azure2Azure job " +
                "likely lives on a different enterprise application — open Azure Portal → " +
                "Enterprise Applications, find the original cross-tenant sync app, and configure " +
                "its Application (client) ID on the source tenant.");

        _logger.LogInformation("Starting sync job {JobId} on SP {SpId}.", job.Id, spId);
        try
        {
            await client.ServicePrincipals[spId].Synchronization.Jobs[job.Id].Start.PostAsync(null, ct);
        }
        catch (ODataError ex)
        {
            // Already running is not an error — it's the steady state we want.
            if (ex.Error?.Message?.Contains("already running", StringComparison.OrdinalIgnoreCase) != true
                && ex.Error?.Code?.Equals("AlreadyStarted", StringComparison.OrdinalIgnoreCase) != true)
            {
                throw new InvalidOperationException(
                    $"Graph error starting sync job '{job.Id}' on SP '{spId}': {ex.Error?.Message ?? ex.Message}", ex);
            }
        }

        return new GraphSyncJobResult($"{spId}/{job.Id}");
    }

    public async Task<GraphSyncJobStatus?> GetSyncJobStatusAsync(
        Tenant sourceTenant,
        string compositeJobId,
        CancellationToken ct)
    {
        var (spId, jobId) = ParseCompositeJobId(compositeJobId);
        var client = await CreateClientAsync(sourceTenant, ct);

        SynchronizationJob? job;
        try
        {
            job = await client.ServicePrincipals[spId].Synchronization.Jobs[jobId].GetAsync(null, ct);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == (int)System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (job is null) return null;

        var statusCode = job.Status?.Code?.ToString() ?? "Unknown";
        var synced = (int)(job.Status?.LastSuccessfulExecution?.CountExported ?? 0L);
        var failed = (int)(job.Status?.LastExecution?.CountEscrowed ?? 0L);
        var errorMessage = job.Status?.LastExecution?.Error?.Message;

        return new GraphSyncJobStatus(statusCode, synced, failed, errorMessage);
    }

    public async Task StopSyncJobAsync(Tenant sourceTenant, string compositeJobId, CancellationToken ct)
        => await PauseAsync(sourceTenant, compositeJobId, "stop", ct);

    public async Task CompleteSyncJobAsync(Tenant sourceTenant, string compositeJobId, CancellationToken ct)
        => await PauseAsync(sourceTenant, compositeJobId, "complete", ct);

    public async Task<string> GetSyncJobSchemaRuleIdAsync(
        Tenant sourceTenant,
        string compositeJobId,
        CancellationToken ct)
    {
        if (_ruleIdCache.TryGetValue(compositeJobId, out var cached))
            return cached;

        var (spId, jobId) = ParseCompositeJobId(compositeJobId);
        var client = await CreateClientAsync(sourceTenant, ct);

        var schema = await client.ServicePrincipals[spId].Synchronization.Jobs[jobId].Schema.GetAsync(null, ct);
        var ruleId = schema?.SynchronizationRules?.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException(
                $"No synchronization rules in the job schema for {compositeJobId}.");

        _ruleIdCache.TryAdd(compositeJobId, ruleId);
        return ruleId;
    }

    public async Task<ProvisionOnDemandResult> ProvisionOnDemandAsync(
        Tenant sourceTenant,
        string compositeJobId,
        string syncRuleId,
        IReadOnlyList<string> userObjectIds,
        CancellationToken ct)
    {
        if (userObjectIds.Count > 5)
            throw new ArgumentException("provisionOnDemand supports a maximum of 5 users per call.", nameof(userObjectIds));

        var (spId, jobId) = ParseCompositeJobId(compositeJobId);
        var client = await CreateClientAsync(sourceTenant, ct);

        var subjects = userObjectIds.Select(id => new SynchronizationJobSubject
        {
            ObjectId = id,
            ObjectTypeName = "User",
        }).ToList();

        var body = new Microsoft.Graph.ServicePrincipals.Item.Synchronization.Jobs.Item.ProvisionOnDemand.ProvisionOnDemandPostRequestBody
        {
            Parameters = [
                new SynchronizationJobApplicationParameters
                {
                    RuleId = syncRuleId,
                    Subjects = subjects,
                }
            ],
        };

        _logger.LogInformation(
            "Calling provisionOnDemand for {Count} user(s) on job {JobId}.",
            userObjectIds.Count, compositeJobId);

        var response = await client.ServicePrincipals[spId].Synchronization.Jobs[jobId]
            .ProvisionOnDemand.PostAsync(body, null, ct);

        var responseValue = response?.Value;
        var results = new List<ProvisionedUserResult>();

        if (string.IsNullOrWhiteSpace(responseValue))
        {
            // 200 with no body = accepted. Treat as success.
            foreach (var id in userObjectIds)
                results.Add(new ProvisionedUserResult(id, "Success", null));
            return new ProvisionOnDemandResult(results);
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(responseValue);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                // provisionOnDemand may return multiple log entries per user (restore + update).
                // Group by sourceIdentity.id; latest entry wins; prefer non-null targetId.
                var groups = new Dictionary<string, (string Status, string? Error, string? TargetId)>();
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    var sourceId = ReadJsonPath(entry, "sourceIdentity", "id");
                    var targetId = ReadJsonPath(entry, "targetIdentity", "id");
                    var status = ReadJsonPath(entry, "statusInfo", "status") ?? "Unknown";

                    string? reason = null;
                    if (status != "Success" && entry.TryGetProperty("provisioningSteps", out var steps)
                        && steps.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var step in steps.EnumerateArray())
                        {
                            if (step.TryGetProperty("status", out var ss) && ss.GetString() != "Success"
                                && step.TryGetProperty("description", out var desc))
                            {
                                reason = desc.GetString();
                                break;
                            }
                        }
                    }

                    if (sourceId is null) continue;
                    if (groups.TryGetValue(sourceId, out var existing))
                        groups[sourceId] = (status, reason ?? existing.Error, targetId ?? existing.TargetId);
                    else
                        groups[sourceId] = (status, reason, targetId);
                }

                foreach (var id in userObjectIds)
                {
                    if (groups.TryGetValue(id, out var g))
                        results.Add(new ProvisionedUserResult(id, g.Status, g.Error, g.TargetId));
                    else
                        ParseResponseFallbackForId(responseValue, id, results);
                }
            }
            else
            {
                foreach (var id in userObjectIds)
                    ParseResponseFallbackForId(responseValue, id, results);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            foreach (var id in userObjectIds)
                ParseResponseFallbackForId(responseValue, id, results);
        }

        _logger.LogInformation(
            "provisionOnDemand completed for job {JobId}: {Success} succeeded, {Failed} failed/skipped.",
            compositeJobId,
            results.Count(r => r.Status == "Success"),
            results.Count(r => r.Status != "Success"));

        return new ProvisionOnDemandResult(results);
    }

    public async Task<string?> ResolveUserObjectIdAsync(
        Tenant sourceTenant,
        string userPrincipalName,
        CancellationToken ct)
    {
        var client = await CreateClientAsync(sourceTenant, ct);
        var result = await client.Users.GetAsync(req =>
        {
            req.QueryParameters.Filter = $"userPrincipalName eq '{userPrincipalName}'";
            req.QueryParameters.Select = ["id"];
        }, ct);
        return result?.Value?.FirstOrDefault()?.Id;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<GraphServiceClient> CreateClientAsync(Tenant tenant, CancellationToken ct)
    {
        var (cert, certPw, secret) = await _keyVault.LoadCredentialsAsync(tenant.Id, ct);
        return _graphFactory.CreateForTenant(tenant, cert, certPw, secret);
    }

    private async Task<SynchronizationJob?> TryFindAzure2AzureJobOnSpAsync(
        GraphServiceClient client, string spId, CancellationToken ct)
    {
        // Explicit $select required — Graph silently returns an empty Value
        // collection without it in some tenants/SP configurations even when
        // jobs exist.
        var jobs = await client.ServicePrincipals[spId].Synchronization.Jobs.GetAsync(req =>
        {
            req.QueryParameters.Select = ["id", "templateId", "status", "schedule"];
        }, ct);
        return jobs?.Value?.FirstOrDefault(j => IsCrossTenantSyncTemplate(j.TemplateId))
            ?? jobs?.Value?.FirstOrDefault();
    }

    private async Task<(string SpId, SynchronizationJob Job)?> FindFirstAzure2AzureSpAsync(
        GraphServiceClient client, CancellationToken ct)
    {
        // Scan all SPs (no tag filter — the wizard creates apps with varying
        // tags). Cap at MaxServicePrincipalsScanned to keep it bounded.
        var page = await client.ServicePrincipals.GetAsync(req =>
        {
            req.QueryParameters.Select = ["id", "displayName", "appId"];
            req.QueryParameters.Top = 100;
        }, ct);

        int probed = 0;
        while (page?.Value is not null && probed < MaxServicePrincipalsScanned)
        {
            foreach (var sp in page.Value)
            {
                if (string.IsNullOrWhiteSpace(sp.Id) || probed >= MaxServicePrincipalsScanned) break;
                probed++;
                try
                {
                    var match = await TryFindAzure2AzureJobOnSpAsync(client, sp.Id, ct);
                    if (match is not null)
                    {
                        _logger.LogInformation(
                            "Discovery: Azure2Azure job {JobId} on SP '{Name}' ({SpId}).",
                            match.Id, sp.DisplayName, sp.Id);
                        return (sp.Id, match);
                    }
                }
                catch (ODataError) { /* SP doesn't host synchronization — skip */ }
            }

            if (string.IsNullOrEmpty(page.OdataNextLink) || probed >= MaxServicePrincipalsScanned) break;
            page = await client.ServicePrincipals.WithUrl(page.OdataNextLink).GetAsync(null, ct);
        }
        return null;
    }

    private async Task<string> GetServicePrincipalObjectIdAsync(
        GraphServiceClient client, string appClientId, CancellationToken ct)
    {
        var result = await client.ServicePrincipals.GetAsync(req =>
        {
            req.QueryParameters.Filter = $"appId eq '{appClientId}'";
            req.QueryParameters.Select = ["id", "displayName", "appId"];
        }, ct);

        var sp = result?.Value?.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No service principal found in the source tenant for app client ID '{appClientId}'.");
        if (sp.Id is null)
            throw new InvalidOperationException(
                $"Service principal for app '{appClientId}' has no object ID.");
        return sp.Id;
    }

    private async Task PauseAsync(Tenant sourceTenant, string compositeJobId, string verb, CancellationToken ct)
    {
        var (spId, jobId) = ParseCompositeJobId(compositeJobId);
        var client = await CreateClientAsync(sourceTenant, ct);
        _logger.LogInformation("Pausing sync job {JobId} on SP {SpId} ({Verb} requested).", jobId, spId, verb);
        try
        {
            await client.ServicePrincipals[spId].Synchronization.Jobs[jobId].Pause.PostAsync(null, ct);
        }
        catch (ODataError ex)
        {
            throw new InvalidOperationException(
                $"Graph error pausing sync job '{jobId}' ({verb}): {ex.Error?.Message ?? ex.Message}", ex);
        }
    }

    private static (string spId, string jobId) ParseCompositeJobId(string compositeJobId)
    {
        var idx = compositeJobId.IndexOf('/');
        if (idx < 0)
            throw new ArgumentException(
                $"Invalid composite job ID '{compositeJobId}'. Expected format: '{{spObjectId}}/{{jobId}}'.",
                nameof(compositeJobId));
        return (compositeJobId[..idx], compositeJobId[(idx + 1)..]);
    }

    private static string? ReadJsonPath(System.Text.Json.JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != System.Text.Json.JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
                return null;
            current = next;
        }
        return current.ValueKind == System.Text.Json.JsonValueKind.String ? current.GetString() : null;
    }

    /// <summary>
    /// Per-user fallback when the response can't be JSON-parsed or doesn't contain
    /// an entry for this source ID. Scans the raw string for status markers and
    /// returns the most pessimistic outcome.
    /// </summary>
    private static void ParseResponseFallbackForId(
        string responseValue, string userObjectId, List<ProvisionedUserResult> results)
    {
        var hasFailure = responseValue.Contains("\"status\":\"Failure\"", StringComparison.OrdinalIgnoreCase);
        var hasSkipped = responseValue.Contains("\"status\":\"Skipped\"", StringComparison.OrdinalIgnoreCase);

        string? reason = null;
        if (hasFailure || hasSkipped)
        {
            var descIdx = responseValue.IndexOf("\"description\":\"", StringComparison.Ordinal);
            if (descIdx >= 0)
            {
                var start = descIdx + "\"description\":\"".Length;
                var end = responseValue.IndexOf("\"", start, StringComparison.Ordinal);
                if (end > start)
                    reason = responseValue[start..end].Replace("\\r\\n", " ").Trim();
            }
        }

        if (hasFailure)
            results.Add(new ProvisionedUserResult(userObjectId, "Failure", reason ?? "Provisioning failed."));
        else if (hasSkipped)
            results.Add(new ProvisionedUserResult(userObjectId, "Skipped", reason ?? "User was skipped by the Graph provisioning engine."));
        else
            results.Add(new ProvisionedUserResult(userObjectId, "Success", null));
    }
}
