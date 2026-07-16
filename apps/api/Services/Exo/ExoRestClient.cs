using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;

namespace MigrationPlatform.Api.Services.Exo;

/// <summary>
/// Production implementation of <see cref="IExoRestClient"/>.
/// Uses the Exchange Online adminapi/beta REST API authenticated via
/// <c>https://outlook.office365.com/.default</c> scope (app-only).
///
/// OData endpoints (MailboxStatistics, Mailbox) are used for read-only mailbox queries.
/// Migration operations (Get/New-MigrationEndpoint, Get/New-MigrationBatch, etc.) use
/// the EXO cmdlet REST API via <c>POST /adminapi/beta/{orgId}/InvokeCommand</c>, which
/// is the same REST backend that the Exchange Online PowerShell v3 module uses.
/// The OData surface does NOT expose migration resources as direct segments.
/// </summary>
public sealed partial class ExoRestClient : IExoRestClient
{
    private static readonly string[] ExoScopes = ["https://outlook.office365.com/.default"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExoRestClient> _logger;

    public ExoRestClient(IHttpClientFactory httpClientFactory, ILogger<ExoRestClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ── IExoRestClient (OData endpoints — these work) ──────────────────────

    public async Task<ExoMailboxStats?> GetMailboxStatisticsAsync(
        string aadTenantId, string upn, TokenCredential credential, CancellationToken ct)
    {
        var client = await CreateAuthenticatedClientAsync(credential, ct);
        var url = $"https://outlook.office365.com/adminapi/beta/{aadTenantId}/MailboxStatistics('{Uri.EscapeDataString(upn)}')";

        var response = await client.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60;
            _logger.LogWarning("EXO: rate-limited on GetMailboxStatistics for {Upn}. Retry-After: {RetryAfter}s.", upn, retryAfter);
            return null;
        }

        await EnsureSuccessAsync(response, "GetMailboxStatistics", ct);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var itemCount = root.TryGetProperty("ItemCount", out var ic) ? ic.GetInt64() : 0L;

        long totalBytes = 0;
        if (root.TryGetProperty("TotalItemSize", out var tis) && tis.ValueKind == JsonValueKind.String)
        {
            var sizeStr = tis.GetString() ?? string.Empty;
            var match = BytesRegex().Match(sizeStr);
            if (match.Success && long.TryParse(match.Groups[1].Value, out var parsed))
                totalBytes = parsed;
        }
        else if (root.TryGetProperty("TotalItemSizeInBytes", out var tisb))
        {
            totalBytes = tisb.GetInt64();
        }

        DateTime? lastLogon = null;
        if (root.TryGetProperty("LastLogonTime", out var llt) &&
            llt.ValueKind != JsonValueKind.Null &&
            llt.TryGetDateTime(out var dt))
        {
            lastLogon = dt;
        }

        return new ExoMailboxStats(itemCount, totalBytes, lastLogon);
    }

    public async Task<ExoArchiveInfo> GetMailboxArchiveInfoAsync(
        string aadTenantId, string upn, TokenCredential credential, CancellationToken ct)
    {
        var client = await CreateAuthenticatedClientAsync(credential, ct);
        var url = $"https://outlook.office365.com/adminapi/beta/{aadTenantId}/Mailbox('{Uri.EscapeDataString(upn)}')" +
                  "?$select=ArchiveStatus,ArchiveName,ArchiveSize";

        var response = await client.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ExoArchiveInfo(false, 0);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60;
            _logger.LogWarning("EXO: rate-limited on GetMailboxArchiveInfo for {Upn}. Retry-After: {RetryAfter}s.", upn, retryAfter);
            return new ExoArchiveInfo(false, 0);
        }

        await EnsureSuccessAsync(response, "GetMailboxArchiveInfo", ct);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        bool hasArchive = false;
        if (root.TryGetProperty("ArchiveStatus", out var archiveStatus) &&
            archiveStatus.ValueKind == JsonValueKind.String)
        {
            var statusStr = archiveStatus.GetString();
            hasArchive = !string.IsNullOrEmpty(statusStr) &&
                         !string.Equals(statusStr, "None", StringComparison.OrdinalIgnoreCase);
        }

        long archiveBytes = 0;
        if (root.TryGetProperty("ArchiveSize", out var archiveSize) &&
            archiveSize.ValueKind == JsonValueKind.String)
        {
            var sizeStr = archiveSize.GetString() ?? string.Empty;
            var match = BytesRegex().Match(sizeStr);
            if (match.Success && long.TryParse(match.Groups[1].Value, out var parsed))
                archiveBytes = parsed;
        }

        return new ExoArchiveInfo(hasArchive, archiveBytes);
    }

    // ── IExoRestClient (InvokeCommand-based — migration operations) ────────

    public async Task<string?> FindCrossTenantMigrationEndpointAsync(
        string aadTenantId, TokenCredential credential, CancellationToken ct,
        string? remoteTenant = null)
    {
        var endpoint = await FindCrossTenantMigrationEndpointElementAsync(aadTenantId, credential, ct, remoteTenant);
        return endpoint is { } ep && ep.TryGetProperty("Identity", out var identity)
            ? identity.GetString()
            : null;
    }

    private async Task<JsonElement?> FindCrossTenantMigrationEndpointElementAsync(
        string aadTenantId, TokenCredential credential, CancellationToken ct,
        string? remoteTenant = null)
    {
        JsonElement[] results;
        try
        {
            results = await InvokeCommandAsync(
                aadTenantId,
                "Get-MigrationEndpoint",
                new Dictionary<string, object>(),
                credential,
                ct);
        }
        catch (InvalidOperationException ex) when (IsExoNotFound(ex))
        {
            return null;
        }

        foreach (var endpoint in results)
        {
            // Check EndpointType — cross-tenant endpoints are "ExchangeRemoteMove"
            var epType = endpoint.TryGetProperty("EndpointType", out var et) ? et.GetString() : null;
            if (!string.Equals(epType, "ExchangeRemoteMove", StringComparison.OrdinalIgnoreCase))
                continue;

            // Multiple tenant pairs mean multiple ExchangeRemoteMove endpoints — a
            // first-of-type match can hand back an endpoint pointed at the wrong tenant.
            if (!string.IsNullOrWhiteSpace(remoteTenant))
            {
                var epRemote = GetStringProp(endpoint, "RemoteTenant");
                if (!string.Equals(epRemote, remoteTenant, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            return endpoint;
        }

        return null;
    }

    public async Task<ExoBatchCreationResult> CreateMigrationBatchAsync(
        string aadTenantId,
        string batchName,
        string targetDeliveryDomain,
        string endpointIdentity,
        IEnumerable<string> migrationIdentities,
        TokenCredential credential,
        CancellationToken ct)
    {
        // Build CSV content: header + one email per line. For a cross-tenant
        // onboarding remote move, these are the TARGET MailUser addresses — MRS
        // resolves each row in the target org and requires a MailUser.
        var csvBuilder = new StringBuilder("EmailAddress\n");
        foreach (var upn in migrationIdentities)
            csvBuilder.AppendLine(upn);
        var csvBytes = Encoding.UTF8.GetBytes(csvBuilder.ToString());
        var csvBase64 = Convert.ToBase64String(csvBytes);

        var parameters = new Dictionary<string, object>
        {
            ["Name"] = batchName,
            ["SourceEndpoint"] = endpointIdentity,
            ["TargetDeliveryDomain"] = targetDeliveryDomain,
            ["CSVData"] = csvBase64,
        };

        JsonElement[] results;
        try
        {
            results = await InvokeCommandAsync(
                aadTenantId, "New-MigrationBatch", parameters, credential, ct);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // Stale batch from a prior failed attempt — remove it, then retry create.
            _logger.LogWarning(
                "EXO: Migration batch '{BatchName}' already exists in tenant {TenantId} — removing and recreating.",
                batchName, aadTenantId);
            await InvokeCommandAsync(
                aadTenantId,
                "Remove-MigrationBatch",
                new Dictionary<string, object> { ["Identity"] = batchName, ["Confirm"] = false },
                credential,
                ct);
            // EXO needs a few seconds to flush the deletion across MRS backends before recreate succeeds.
            await Task.Delay(TimeSpan.FromSeconds(8), ct);
            results = await InvokeCommandAsync(
                aadTenantId, "New-MigrationBatch", parameters, credential, ct);
        }

        if (results.Length == 0)
            return new ExoBatchCreationResult(batchName, "Unknown");

        var root = results[0];
        var batchId = root.TryGetProperty("Identity", out var id) ? id.GetString() ?? batchName : batchName;
        var status = root.TryGetProperty("Status", out var st) ? st.GetString() ?? "Unknown" : "Unknown";

        _logger.LogInformation(
            "EXO: Created migration batch '{BatchId}' in tenant {TenantId}. Status: {Status}.",
            batchId, aadTenantId, status);

        // Start the batch (New-MigrationBatch creates it in Stopped state)
        try
        {
            await InvokeCommandAsync(
                aadTenantId,
                "Start-MigrationBatch",
                new Dictionary<string, object> { ["Identity"] = batchId },
                credential,
                ct);

            _logger.LogInformation("EXO: Started migration batch '{BatchId}'.", batchId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EXO: Failed to auto-start migration batch '{BatchId}'. " +
                "It may need to be started manually via Start-MigrationBatch.", batchId);
        }

        return new ExoBatchCreationResult(batchId, status);
    }

    public async Task<ExoBatchStatus?> GetMigrationBatchAsync(
        string aadTenantId, string exoBatchId, TokenCredential credential, CancellationToken ct)
    {
        JsonElement[] results;
        try
        {
            results = await InvokeCommandAsync(
                aadTenantId,
                "Get-MigrationBatch",
                new Dictionary<string, object> { ["Identity"] = exoBatchId },
                credential,
                ct);
        }
        catch (InvalidOperationException ex) when (IsExoNotFound(ex))
        {
            return null;
        }

        if (results.Length == 0) return null;

        var root = results[0];
        var status = GetStringProp(root, "Status") ?? "Unknown";

        // EXO returns counts as nested objects or direct properties depending on version
        var synced = GetIntProp(root, "SyncedCount");
        var finalized = GetIntProp(root, "FinalizedCount");
        var failed = GetIntProp(root, "FailedCount");
        var total = GetIntProp(root, "TotalCount");

        return new ExoBatchStatus(status, synced, finalized, failed, total);
    }

    public async Task RemoveMigrationBatchAsync(
        string aadTenantId, string exoBatchId, TokenCredential credential, CancellationToken ct)
    {
        await InvokeCommandAsync(
            aadTenantId,
            "Remove-MigrationBatch",
            new Dictionary<string, object> { ["Identity"] = exoBatchId, ["Confirm"] = false },
            credential,
            ct);

        _logger.LogInformation("EXO: Removed migration batch '{BatchId}' from tenant {TenantId}.", exoBatchId, aadTenantId);
    }

    public async Task RemoveMoveRequestAsync(
        string aadTenantId, string identity, TokenCredential credential, CancellationToken ct)
    {
        try
        {
            await InvokeCommandAsync(
                aadTenantId,
                "Remove-MoveRequest",
                new Dictionary<string, object> { ["Identity"] = identity, ["Confirm"] = false },
                credential,
                ct);
            _logger.LogInformation(
                "EXO: Removed MoveRequest for '{Identity}' in tenant {TenantId}.", identity, aadTenantId);
        }
        catch (InvalidOperationException ex) when (IsExoNotFound(ex))
        {
            _logger.LogDebug(
                "EXO: No MoveRequest exists for '{Identity}' in tenant {TenantId} — nothing to remove.",
                identity, aadTenantId);
        }
    }

    public async Task RemoveMailUserAsync(
        string aadTenantId, string identity, TokenCredential credential, CancellationToken ct)
    {
        try
        {
            await InvokeCommandAsync(
                aadTenantId,
                "Remove-MailUser",
                new Dictionary<string, object> { ["Identity"] = identity, ["Confirm"] = false },
                credential,
                ct);
            _logger.LogInformation(
                "EXO: Removed MailUser '{Identity}' in tenant {TenantId}.", identity, aadTenantId);
        }
        catch (InvalidOperationException ex) when (IsExoNotFound(ex))
        {
            _logger.LogDebug(
                "EXO: MailUser '{Identity}' not present in tenant {TenantId} — nothing to remove.",
                identity, aadTenantId);
        }
    }

    public async Task<IReadOnlyList<string>> GetSoftDeletedMailUsersByExternalEmailAsync(
        string aadTenantId,
        IEnumerable<string> externalEmailAddresses,
        TokenCredential credential,
        CancellationToken ct)
    {
        // Normalize inputs: strip any leading "SMTP:" / "smtp:" prefix and lowercase for compare.
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in externalEmailAddresses)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();
            if (s.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
            wanted.Add(s);
        }
        if (wanted.Count == 0) return Array.Empty<string>();

        JsonElement[] results;
        try
        {
            results = await InvokeCommandAsync(
                aadTenantId,
                "Get-MailUser",
                new Dictionary<string, object>
                {
                    ["SoftDeletedMailUser"] = true,
                    ["ResultSize"]          = "Unlimited",
                },
                credential,
                ct);
        }
        catch (InvalidOperationException ex) when (IsExoNotFound(ex))
        {
            return Array.Empty<string>();
        }

        var matches = new List<string>();
        foreach (var mu in results)
        {
            var ext = GetStringProp(mu, "ExternalEmailAddress");
            if (string.IsNullOrWhiteSpace(ext)) continue;
            var normalized = ext.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? ext.Substring(5) : ext;
            if (!wanted.Contains(normalized)) continue;

            // Prefer DistinguishedName / ExchangeObjectId for unique addressing of soft-deleted
            // entries (multiple soft-deletes can share the same UPN). Fall back to Identity.
            var id = GetStringProp(mu, "DistinguishedName")
                  ?? GetStringProp(mu, "ExchangeObjectId")
                  ?? GetStringProp(mu, "Identity")
                  ?? GetStringProp(mu, "Name");
            if (!string.IsNullOrWhiteSpace(id)) matches.Add(id!);
        }

        _logger.LogInformation(
            "EXO: Found {Count} soft-deleted MailUser(s) in tenant {TenantId} matching {WantedCount} external SMTP address(es).",
            matches.Count, aadTenantId, wanted.Count);

        return matches;
    }

    public async Task PurgeSoftDeletedMailUserAsync(
        string aadTenantId, string identity, TokenCredential credential, CancellationToken ct)
    {
        try
        {
            await InvokeCommandAsync(
                aadTenantId,
                "Remove-MailUser",
                new Dictionary<string, object>
                {
                    ["Identity"]         = identity,
                    ["PermanentlyDelete"] = true,
                    ["Confirm"]          = false,
                },
                credential,
                ct);
            _logger.LogInformation(
                "EXO: Permanently purged soft-deleted MailUser '{Identity}' in tenant {TenantId}.",
                identity, aadTenantId);
        }
        catch (InvalidOperationException ex) when (IsExoNotFound(ex))
        {
            _logger.LogDebug(
                "EXO: Soft-deleted MailUser '{Identity}' no longer present in tenant {TenantId} — nothing to purge.",
                identity, aadTenantId);
        }
    }

    public async Task CompleteMigrationBatchAsync(
        string aadTenantId, string exoBatchId, TokenCredential credential, CancellationToken ct)
    {
        await InvokeCommandAsync(
            aadTenantId,
            "Complete-MigrationBatch",
            new Dictionary<string, object> { ["Identity"] = exoBatchId },
            credential,
            ct);

        _logger.LogInformation("EXO: Completed migration batch '{BatchId}' in tenant {TenantId}.", exoBatchId, aadTenantId);
    }

    public async Task<IReadOnlyList<ExoMigrationUser>> GetMigrationUsersAsync(
        string aadTenantId, string exoBatchId, TokenCredential credential, CancellationToken ct)
    {
        JsonElement[] results;
        try
        {
            results = await InvokeCommandAsync(
                aadTenantId,
                "Get-MigrationUser",
                new Dictionary<string, object> { ["BatchId"] = exoBatchId },
                credential,
                ct);
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<ExoMigrationUser>();
        }

        var list = new List<ExoMigrationUser>();
        foreach (var user in results)
        {
            // Get-MigrationUser rows carry the address as Identity/MailboxEmailAddress
            // (both the target UPN for cross-tenant batches); EmailAddress is absent
            // on the REST surface but kept first in case other shapes include it.
            var email = GetStringProp(user, "EmailAddress")
                ?? GetStringProp(user, "MailboxEmailAddress")
                ?? GetStringProp(user, "Identity")
                ?? string.Empty;
            var status = GetStringProp(user, "Status") ?? string.Empty;
            // Get-MigrationUser exposes the failure reason as ErrorSummary; ErrorMessage
            // is usually absent on this cmdlet. Check both so per-user errors reach the UI.
            var error = GetStringProp(user, "ErrorSummary") ?? GetStringProp(user, "ErrorMessage");
            list.Add(new ExoMigrationUser(email, status, error));
        }

        return list;
    }

    public async Task<bool> EnsureOrganizationRelationshipAsync(
        string aadTenantId,
        string partnerDomain,
        string name,
        string mailboxMoveCapability,
        TokenCredential credential,
        CancellationToken ct,
        string? oauthApplicationId = null,
        string? mailboxMovePublishedScopes = null,
        string? partnerTenantId = null)
    {
        // Check if a relationship targeting this partner already exists — match on
        // the domain OR the partner tenant GUID (either may be in DomainNames).
        JsonElement? existingRel = null;
        string? existingName = null;
        try
        {
            var existing = await InvokeCommandAsync(
                aadTenantId,
                "Get-OrganizationRelationship",
                new Dictionary<string, object>(),
                credential,
                ct);

            bool IsPartnerValue(string? v) =>
                string.Equals(v, partnerDomain, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(partnerTenantId) &&
                 string.Equals(v, partnerTenantId, StringComparison.OrdinalIgnoreCase));

            foreach (var rel in existing)
            {
                if (!rel.TryGetProperty("DomainNames", out var domains))
                    continue;

                bool matches = false;
                if (domains.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in domains.EnumerateArray())
                    {
                        if (IsPartnerValue(d.GetString()))
                        {
                            matches = true;
                            break;
                        }
                    }
                }
                else if (domains.ValueKind == JsonValueKind.String && IsPartnerValue(domains.GetString()))
                {
                    matches = true;
                }

                if (matches)
                {
                    existingRel = rel;
                    existingName = GetStringProp(rel, "Name");
                    break;
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex,
                "EXO: Get-OrganizationRelationship returned error for tenant {TenantId} — " +
                "proceeding with create attempt.", aadTenantId);
        }

        if (existingRel.HasValue && existingName is not null)
        {
            await PatchOrganizationRelationshipAuthAsync(
                aadTenantId, existingName, existingRel.Value,
                oauthApplicationId, mailboxMovePublishedScopes, mailboxMoveCapability, credential, ct,
                partnerTenantId);
            return false;
        }

        // Create the organization relationship. Microsoft's current cross-tenant
        // mailbox doc puts the partner TENANT GUID in DomainNames ("the GUID and
        // not the tenant domain name") — stamp both GUID and domain so MRS's
        // GUID-keyed lookup and the platform's domain-based matching both work.
        var domainNames = string.IsNullOrWhiteSpace(partnerTenantId)
            ? new[] { partnerDomain }
            : new[] { partnerTenantId, partnerDomain };
        var newParams = new Dictionary<string, object>
        {
            ["Name"] = name,
            ["DomainNames"] = domainNames,
            ["MailboxMoveEnabled"] = true,
            ["MailboxMoveCapability"] = mailboxMoveCapability,
        };
        if (!string.IsNullOrWhiteSpace(oauthApplicationId))
            newParams["OAuthApplicationId"] = oauthApplicationId;
        if (!string.IsNullOrWhiteSpace(mailboxMovePublishedScopes))
            newParams["MailboxMovePublishedScopes"] = new[] { mailboxMovePublishedScopes };

        try
        {
            await InvokeCommandAsync(aadTenantId, "New-OrganizationRelationship", newParams, credential, ct);
            _logger.LogInformation(
                "EXO: Created organization relationship '{Name}' (capability: {Capability}, oauthApp: {HasOAuth}, scopes: {HasScopes}) targeting '{Domain}' in tenant {TenantId}.",
                name, mailboxMoveCapability,
                !string.IsNullOrWhiteSpace(oauthApplicationId),
                !string.IsNullOrWhiteSpace(mailboxMovePublishedScopes),
                partnerDomain, aadTenantId);
            return true;
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("conflicting object", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "EXO: Organization relationship '{Name}' already exists in tenant {TenantId} " +
                "(confirmed via error response). Patching auth fields if supplied.",
                name, aadTenantId);
            await PatchOrganizationRelationshipAuthAsync(
                aadTenantId, name, default,
                oauthApplicationId, mailboxMovePublishedScopes, mailboxMoveCapability, credential, ct,
                partnerTenantId);
            return false;
        }
    }

    /// <summary>
    /// Patches OAuthApplicationId, MailboxMovePublishedScopes, MailboxMoveEnabled, and
    /// MailboxMoveCapability on an existing org relationship when they are missing or stale.
    /// A relationship left over from earlier experiments (e.g. CTIM) may match on domain but
    /// have moves disabled or the wrong capability — MRS then fails with no useful error.
    /// No-op when the existing values already match. Errors are logged and swallowed —
    /// the OrgRel is otherwise usable.
    /// </summary>
    private async Task PatchOrganizationRelationshipAuthAsync(
        string aadTenantId,
        string relName,
        JsonElement existingRel,
        string? oauthApplicationId,
        string? mailboxMovePublishedScopes,
        string? mailboxMoveCapability,
        TokenCredential credential,
        CancellationToken ct,
        string? partnerTenantId = null)
    {
        var setParams = new Dictionary<string, object> { ["Identity"] = relName };
        var willPatch = false;

        // Ensure the partner tenant GUID is present in DomainNames (required by
        // Microsoft's current cross-tenant mailbox doc; older platform versions
        // stamped only the domain). Set-OrganizationRelationship REPLACES the
        // multivalue, so read-modify-write from the existing relationship; skip
        // when the existing values are not visible (existingRel == default).
        if (!string.IsNullOrWhiteSpace(partnerTenantId) &&
            existingRel.ValueKind == JsonValueKind.Object &&
            existingRel.TryGetProperty("DomainNames", out var existingDomains) &&
            existingDomains.ValueKind == JsonValueKind.Array)
        {
            var current = existingDomains.EnumerateArray()
                .Select(d => d.GetString())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d!)
                .ToList();
            if (!current.Contains(partnerTenantId, StringComparer.OrdinalIgnoreCase))
            {
                current.Insert(0, partnerTenantId);
                setParams["DomainNames"] = current.ToArray();
                willPatch = true;
            }
        }

        // Verify MailboxMoveEnabled — a matching relationship with moves disabled silently
        // breaks MRS. When we can't see the current value (existingRel == default), stamp it.
        var moveEnabled = existingRel.ValueKind == JsonValueKind.Object &&
                          existingRel.TryGetProperty("MailboxMoveEnabled", out var mme) &&
                          mme.ValueKind == JsonValueKind.True;
        if (!moveEnabled)
        {
            setParams["MailboxMoveEnabled"] = true;
            willPatch = true;
        }

        // Verify MailboxMoveCapability matches the direction we need.
        if (!string.IsNullOrWhiteSpace(mailboxMoveCapability))
        {
            var currentCapability = existingRel.ValueKind == JsonValueKind.Object
                ? GetStringProp(existingRel, "MailboxMoveCapability")
                : null;
            if (!string.Equals(currentCapability, mailboxMoveCapability, StringComparison.OrdinalIgnoreCase))
            {
                setParams["MailboxMoveCapability"] = mailboxMoveCapability;
                willPatch = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(oauthApplicationId))
        {
            var current = existingRel.ValueKind == JsonValueKind.Object
                ? GetStringProp(existingRel, "OAuthApplicationId")
                : null;
            if (!string.Equals(current, oauthApplicationId, StringComparison.OrdinalIgnoreCase))
            {
                setParams["OAuthApplicationId"] = oauthApplicationId;
                willPatch = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(mailboxMovePublishedScopes))
        {
            var hasScope = false;
            if (existingRel.ValueKind == JsonValueKind.Object &&
                existingRel.TryGetProperty("MailboxMovePublishedScopes", out var scopes) &&
                scopes.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in scopes.EnumerateArray())
                {
                    if (string.Equals(s.GetString(), mailboxMovePublishedScopes, StringComparison.OrdinalIgnoreCase))
                    {
                        hasScope = true;
                        break;
                    }
                }
            }
            if (!hasScope)
            {
                setParams["MailboxMovePublishedScopes"] = new[] { mailboxMovePublishedScopes };
                willPatch = true;
            }
        }

        if (!willPatch)
        {
            _logger.LogDebug(
                "EXO: Org relationship '{Name}' in tenant {TenantId} already has expected auth fields — no patch.",
                relName, aadTenantId);
            return;
        }

        try
        {
            await InvokeCommandAsync(aadTenantId, "Set-OrganizationRelationship", setParams, credential, ct);
            _logger.LogInformation(
                "EXO: Patched org relationship '{Name}' in tenant {TenantId} — set {Fields}.",
                relName, aadTenantId, string.Join(",", setParams.Keys.Where(k => k != "Identity")));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "EXO: Failed to patch org relationship '{Name}' in tenant {TenantId}. " +
                "Cross-tenant migration may fail authentication — review the relationship manually.",
                relName, aadTenantId);
        }
    }

    public async Task<(string Identity, bool WasCreated)> EnsureMigrationEndpointAsync(
        string aadTenantId,
        string targetTenantDomain,
        TokenCredential credential,
        CancellationToken ct,
        string? applicationId = null,
        string? clientSecret = null)
    {
        // Return the existing endpoint identity if one already exists FOR THIS remote tenant.
        var existingEndpoint = await FindCrossTenantMigrationEndpointElementAsync(
            aadTenantId, credential, ct, remoteTenant: targetTenantDomain);
        if (existingEndpoint is { } existingEp)
        {
            var existing = existingEp.TryGetProperty("Identity", out var idProp) ? idProp.GetString() : null;

            // An endpoint left over from a previous setup can reference a DIFFERENT
            // app than the configured migration app. MRS then authenticates as the
            // endpoint's app while the org relationships expect the configured one,
            // and the source ProxyService rejects every move with a bare "Access is
            // denied" (confirmed live 2026-07-16: a stale endpoint surviving a tenant
            // teardown cost three identical failed batches). The credential cannot be
            // patched over EXO REST, so fail fast with the manual recreate script.
            var epAppId = GetStringProp(existingEp, "ApplicationId");
            if (!string.IsNullOrWhiteSpace(applicationId) &&
                !string.IsNullOrWhiteSpace(epAppId) &&
                !string.Equals(epAppId, applicationId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Migration endpoint '{existing}' in tenant {aadTenantId} references ApplicationId " +
                    $"{epAppId}, but the configured cross-tenant migration app is {applicationId}. MRS would " +
                    "authenticate as the endpoint's app and the source tenant would reject every move with " +
                    "\"Access is denied\". Recreate the endpoint on the target tenant with the current app: " +
                    $"Remove-MigrationBatch on any plat-* batches, then Remove-MigrationEndpoint -Identity '{existing}'; " +
                    $"New-MigrationEndpoint -Name '{existing}' -ExchangeRemoteMove -RemoteTenant {targetTenantDomain} " +
                    $"-RemoteServer outlook.office.com -ApplicationId {applicationId} -Credentials " +
                    $"(New-Object PSCredential -ArgumentList {applicationId}, (ConvertTo-SecureString <current secret> -AsPlainText -Force)).");
            }

            // NOTE: an existing endpoint can carry a STALE client secret — MRS then
            // fails deep in the move with AADSTS7000215 (Invalid client secret) even
            // though the platform's configured secret is valid. The credential cannot
            // be refreshed in place over EXO REST (Set-MigrationEndpoint rejects the
            // PSCredential wire shape with a 500 cast error), so surface an actionable
            // warning rather than silently trusting a possibly-broken credential. The
            // operator fix is to recreate the endpoint with the current secret.
            // Each named placeholder occurrence consumes one positional arg, so
            // repeated names ({Identity}/{RemoteTenant}) each need their own arg —
            // a mismatch throws "Index must be < the size of the argument list".
            _logger.LogInformation(
                "EXO: Reusing existing cross-tenant migration endpoint '{Identity}' for '{RemoteTenant}' in tenant {TenantId}. " +
                "If moves fail with AADSTS7000215 (Invalid client secret), its stored secret is stale — recreate it: " +
                "Remove-MigrationEndpoint -Identity '{Identity2}'; New-MigrationEndpoint -Name '{Identity3}' -ExchangeRemoteMove " +
                "-RemoteTenant {RemoteTenant2} -RemoteServer outlook.office.com -ApplicationId {AppId} " +
                "-Credentials (New-Object PSCredential (AppId),(ConvertTo-SecureString (secret-VALUE) -AsPlainText -Force)).",
                existing, targetTenantDomain, aadTenantId, existing, existing, targetTenantDomain, applicationId ?? "<AppId>");
            return (existing ?? string.Empty, false);
        }

        // Endpoint names must be unique per tenant; suffix with the remote tenant so
        // multiple tenant pairs can each have their own endpoint.
        var endpointName = $"CrossTenantMigration-{targetTenantDomain.Split('.')[0]}";

        // ApplicationId must match the OAuthApplicationId stamped on both org relationships.
        // Without it, New-MigrationEndpoint creates an endpoint that cannot authenticate
        // cross-tenant MRS requests and every migration batch fails with a 400 from MRS.
        if (string.IsNullOrWhiteSpace(applicationId))
            _logger.LogWarning(
                "EXO: EnsureMigrationEndpointAsync called without ApplicationId for tenant {TenantId}. " +
                "New-MigrationEndpoint will be created without -ApplicationId — migration batches may fail " +
                "if the org relationships use a custom OAuthApplicationId. Set Platform:CrossTenantMigration:AppId.",
                aadTenantId);

        // Current MS infrastructure requires the endpoint to carry -Credentials (PSCredential
        // of AppId + client secret). An endpoint created without credentials passes creation
        // but every batch against it fails (AADSTS70011 / MRS 400) — refuse instead.
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException(
                $"No cross-tenant migration endpoint exists in tenant {aadTenantId} for remote tenant " +
                $"'{targetTenantDomain}', and Platform:CrossTenantMigration:ClientSecret is not configured, " +
                "so one cannot be created. Either add a client secret for the cross-tenant Mailbox Migration " +
                "app registration to configuration, or create the endpoint manually on the target tenant: " +
                $"New-MigrationEndpoint -Name CrossTenantEndpoint -ExchangeRemoteMove -RemoteTenant {targetTenantDomain} " +
                "-RemoteServer outlook.office.com -ApplicationId <AppId> -Credentials (New-Object PSCredential " +
                "-ArgumentList <AppId>, (ConvertTo-SecureString <secret> -AsPlainText -Force)).");

        try
        {
            var endpointParams = new Dictionary<string, object>
            {
                ["Name"]             = endpointName,
                ["RemoteTenant"]     = targetTenantDomain,
                ["RemoteServer"]     = "outlook.office.com",
                ["ExchangeRemoteMove"] = true,
                ["SkipVerification"] = true,
                // PSCredential wire shape for InvokeCommand: UserName + Password fields.
                ["Credentials"]      = new Dictionary<string, object>
                {
                    ["UserName"] = applicationId ?? string.Empty,
                    ["Password"] = clientSecret,
                },
            };

            if (!string.IsNullOrWhiteSpace(applicationId))
                endpointParams["ApplicationId"] = applicationId;

            await InvokeCommandAsync(aadTenantId, "New-MigrationEndpoint", endpointParams, credential, ct);

            _logger.LogInformation(
                "EXO: Created cross-tenant migration endpoint '{Identity}' (appId: {AppId}) in tenant {TenantId}.",
                endpointName, applicationId ?? "(none)", aadTenantId);
            return (endpointName, true);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "EXO: Migration endpoint '{Name}' already exists in tenant {TenantId}. Treating as existing.",
                endpointName, aadTenantId);
            return (endpointName, false);
        }
        catch (Exception ex) when (
            ex.Message.Contains("argument transformation", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("PSCredential", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("SecureString", StringComparison.OrdinalIgnoreCase) ||
            // EXO's InvokeCommand REST layer can't materialize a -Credentials
            // PSCredential — the {UserName,Password} wire shape 500s with
            // "Unable to cast object of type 'JObject' to type 'System.String'".
            // A PSCredential is a client-side PowerShell type; it fundamentally
            // cannot be created over REST, so the endpoint must be made manually.
            ex.Message.Contains("cast object", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Internal server error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The cross-tenant migration endpoint cannot be created automatically — EXO's REST " +
                "InvokeCommand surface cannot pass the -Credentials PSCredential (a client-side type). " +
                "Create it once, manually, on the TARGET tenant in EXO PowerShell (it is then reused): " +
                $"New-MigrationEndpoint -Name {endpointName} -ExchangeRemoteMove -RemoteTenant {targetTenantDomain} " +
                "-RemoteServer outlook.office.com -ApplicationId <AppId> -Credentials (New-Object PSCredential " +
                "-ArgumentList <AppId>, (ConvertTo-SecureString <client-secret-VALUE> -AsPlainText -Force)). " +
                "Use the current, valid client secret VALUE (not the ID). Then retry the batch.");
        }
    }

    // ── Enable-MailUser ──────────────────────────────────────────────────────

    public async Task EnableMailUserAsync(
        string aadTenantId,
        string identity,
        string externalEmailAddress,
        TokenCredential credential,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "EXO: Enable-MailUser -Identity '{Identity}' -ExternalEmailAddress '{ExternalEmail}' in tenant {TenantId}.",
            identity, externalEmailAddress, aadTenantId);

        var parameters = new Dictionary<string, object>
        {
            ["Identity"] = identity,
            ["ExternalEmailAddress"] = externalEmailAddress,
        };

        await InvokeCommandAsync(aadTenantId, "Enable-MailUser", parameters, credential, ct);

        _logger.LogInformation(
            "EXO: Enable-MailUser succeeded for '{Identity}' in tenant {TenantId}.",
            identity, aadTenantId);
    }

    // ── Set-Mailbox (primary SMTP) ─────────────────────────────────────────

    public async Task SetMailboxPrimarySmtpAsync(
        string aadTenantId,
        string identity,
        string primarySmtpAddress,
        TokenCredential credential,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "EXO: Set-Mailbox -Identity '{Identity}' -WindowsEmailAddress '{PrimarySmtp}' in tenant {TenantId}.",
            identity, primarySmtpAddress, aadTenantId);

        var parameters = new Dictionary<string, object>
        {
            ["Identity"] = identity,
            ["WindowsEmailAddress"] = primarySmtpAddress,
        };

        await InvokeCommandAsync(aadTenantId, "Set-Mailbox", parameters, credential, ct);

        _logger.LogInformation(
            "EXO: Set-Mailbox primary SMTP succeeded for '{Identity}' → '{PrimarySmtp}' in tenant {TenantId}.",
            identity, primarySmtpAddress, aadTenantId);
    }

    // ── Source mailbox attribute capture ───────────────────────────────────

    public async Task<ExoMailboxAttributes?> GetMailboxAttributesAsync(
        string aadTenantId, string upn, TokenCredential credential, CancellationToken ct)
    {
        JsonElement[] results;
        try
        {
            results = await InvokeCommandAsync(
                aadTenantId,
                "Get-Mailbox",
                new Dictionary<string, object> { ["Identity"] = upn },
                credential,
                ct);
        }
        catch (InvalidOperationException ex) when (IsExoNotFound(ex))
        {
            return null;
        }

        if (results.Length == 0) return null;
        var mb = results[0];

        var primarySmtp  = GetStringProp(mb, "PrimarySmtpAddress") ?? string.Empty;
        var displayName  = GetStringProp(mb, "DisplayName") ?? primarySmtp;
        var alias        = GetStringProp(mb, "Alias") ?? string.Empty;
        var legacyDn     = GetStringProp(mb, "LegacyExchangeDN") ?? string.Empty;

        Guid exchangeGuid = Guid.Empty;
        if (mb.TryGetProperty("ExchangeGuid", out var eg))
        {
            var raw = eg.ValueKind == JsonValueKind.String
                ? eg.GetString()
                : (eg.TryGetProperty("Guid", out var inner) ? inner.GetString() : null);
            if (!string.IsNullOrWhiteSpace(raw)) Guid.TryParse(raw, out exchangeGuid);
        }

        Guid archiveGuid = Guid.Empty;
        if (mb.TryGetProperty("ArchiveGuid", out var ag))
        {
            var raw = ag.ValueKind == JsonValueKind.String
                ? ag.GetString()
                : (ag.TryGetProperty("Guid", out var inner) ? inner.GetString() : null);
            if (!string.IsNullOrWhiteSpace(raw)) Guid.TryParse(raw, out archiveGuid);
        }

        var x500 = new List<string>();
        if (mb.TryGetProperty("EmailAddresses", out var addrs) && addrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in addrs.EnumerateArray())
            {
                var s = a.ValueKind == JsonValueKind.String
                    ? a.GetString()
                    : (a.TryGetProperty("ProxyAddressString", out var ps) ? ps.GetString() : null);
                if (!string.IsNullOrWhiteSpace(s) && s.StartsWith("x500:", StringComparison.OrdinalIgnoreCase))
                    x500.Add(s);
            }
        }

        return new ExoMailboxAttributes(
            primarySmtp, exchangeGuid, archiveGuid, legacyDn, x500, displayName, alias);
    }

    // ── Target MailUser provisioning ───────────────────────────────────────

    public async Task EnsureTargetMailUserAsync(
        string aadTenantId,
        string targetUpn,
        ExoMailboxAttributes sourceAttributes,
        string targetRoutingDomain,
        TokenCredential credential,
        CancellationToken ct)
    {
        if (sourceAttributes.ExchangeGuid == Guid.Empty)
            throw new InvalidOperationException(
                $"Cannot provision target MailUser for '{targetUpn}': source ExchangeGuid is empty.");
        if (string.IsNullOrWhiteSpace(sourceAttributes.LegacyExchangeDN))
            throw new InvalidOperationException(
                $"Cannot provision target MailUser for '{targetUpn}': source LegacyExchangeDN is empty.");

        // Probe for an existing MailUser at this UPN — if present, we patch instead of creating.
        var existing = await TryGetMailUserAsync(aadTenantId, targetUpn, credential, ct);
        if (existing is null)
        {
            // A MailUser at the UPN is reusable (patched below), but ANY OTHER
            // recipient holding the target address — a different person's mailbox,
            // a group, a contact — makes New-MailUser fail with a raw "proxy
            // address ... is already being used" error (hit live 2026-07-16 when
            // auto-map paired a source user with an unrelated target user sharing
            // the same UPN). Probe Get-Recipient so the collision surfaces as an
            // actionable message instead.
            var conflicting = await TryGetRecipientAsync(aadTenantId, targetUpn, credential, ct);
            if (conflicting is { } conflict)
            {
                var recipientType = GetStringProp(conflict, "RecipientTypeDetails") ?? "recipient";
                var displayName   = GetStringProp(conflict, "DisplayName") ?? targetUpn;
                throw new InvalidOperationException(
                    $"Target UPN '{targetUpn}' is already in use by an existing {recipientType} " +
                    $"('{displayName}') in the target tenant. If that is a different person, remap the " +
                    "source user to an unused target UPN in the project's Identity Map and recreate the " +
                    "batch. If it is a leftover from a previous migration attempt, remove it — including " +
                    "soft-deleted objects: Get-MailUser -SoftDeletedMailUser | Remove-MailUser -PermanentlyDelete — and retry.");
            }

            // Cloud New-MailUser requires a Windows Live ID (-MicrosoftOnlineServicesID)
            // to create the backing directory object; that parameter set also mandates
            // -Password. The target user does not pre-exist here, so both are supplied.
            // The password is a throwaway (the MailUser is a routing stub with no
            // interactive sign-in; the real identity binding is the copied ExchangeGuid
            // stamped by Set-MailUser next). Sent as a plain string over InvokeCommand —
            // EXO's REST layer coerces it to the SecureString the cmdlet expects.
            var newParams = new Dictionary<string, object>
            {
                ["MicrosoftOnlineServicesID"] = targetUpn,
                ["Password"]                  = GenerateStubPassword(),
                ["ExternalEmailAddress"]      = sourceAttributes.PrimarySmtpAddress,
                ["PrimarySmtpAddress"]        = targetUpn,
                ["Name"]                      = sourceAttributes.DisplayName,
                ["DisplayName"]               = sourceAttributes.DisplayName,
                ["Alias"]                     = string.IsNullOrWhiteSpace(sourceAttributes.Alias)
                                                    ? targetUpn.Split('@')[0]
                                                    : sourceAttributes.Alias,
            };
            await InvokeCommandAsync(aadTenantId, "New-MailUser", newParams, credential, ct);
            _logger.LogInformation(
                "EXO: Created target MailUser '{TargetUpn}' (External: {ExternalEmail}) in tenant {TenantId}.",
                targetUpn, sourceAttributes.PrimarySmtpAddress, aadTenantId);
        }
        else
        {
            _logger.LogInformation(
                "EXO: Target MailUser '{TargetUpn}' already exists in tenant {TenantId} — patching attributes.",
                targetUpn, aadTenantId);
        }

        // Stamp ExchangeGuid + LegacyExchangeDN-derived x500 (and any source x500s) on the MailUser.
        // We do this even on the create path because New-MailUser does not accept ExchangeGuid directly.
        //
        // EmailAddresses wire shape via REST InvokeCommand: EXO rejects both the {"Add": [...]}
        // hashtable form (ParameterTransformationException — JProperty not convertible to ProxyAddress)
        // AND the "@x500:..." prefix form (silently malformed proxy). The only reliable shape is a
        // plain string array that REPLACES the entire collection. To avoid wiping the primary SMTP
        // that New-MailUser auto-generated, we read the current EmailAddresses first and append.
        var x500Address = $"x500:{sourceAttributes.LegacyExchangeDN}";

        // Read-after-write lag: a freshly-created MailUser is not immediately
        // visible to Get-MailUser (directory → EXO replication takes seconds to a
        // minute). Poll with backoff before giving up rather than failing the
        // whole prep on the first 404.
        JsonElement? readBack = await TryGetMailUserAsync(aadTenantId, targetUpn, credential, ct);
        if (readBack is null)
        {
            var delays = new[] { 3, 5, 8, 13, 21, 21, 21 }; // ~92s total
            foreach (var seconds in delays)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                readBack = await TryGetMailUserAsync(aadTenantId, targetUpn, credential, ct);
                if (readBack is not null)
                {
                    _logger.LogInformation(
                        "EXO: Target MailUser '{TargetUpn}' became readable after provisioning lag.", targetUpn);
                    break;
                }
            }
        }
        var current = readBack
            ?? throw new InvalidOperationException(
                $"Target MailUser '{targetUpn}' was created but did not become readable within the " +
                "provisioning wait window. Re-run the batch — replication usually completes shortly.");

        var addresses = new List<string>();
        if (current.TryGetProperty("EmailAddresses", out var existingAddrs) &&
            existingAddrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in existingAddrs.EnumerateArray())
            {
                var s = a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                if (!string.IsNullOrWhiteSpace(s)) addresses.Add(s!);
            }
        }
        // Append the LegacyExchangeDN-derived x500, then any extra source x500s, deduped.
        if (!addresses.Any(a => string.Equals(a, x500Address, StringComparison.OrdinalIgnoreCase)))
            addresses.Add(x500Address);
        foreach (var addr in sourceAttributes.X500Addresses)
        {
            if (!addresses.Any(a => string.Equals(a, addr, StringComparison.OrdinalIgnoreCase)))
                addresses.Add(addr);
        }

        // MRS validates the target stub against the batch's TargetDeliveryDomain
        // (<tenant>.mail.onmicrosoft.com): the stub MUST have a corresponding
        // `smtp:<local>@<targetRoutingDomain>` secondary proxy or the move fails —
        // MigrationCSVRowValidationException 0x80070057 at Initialization, or
        // "The target mailbox doesn't have an SMTP proxy matching '<domain>'" during
        // the move. New-MailUser does not generate this proxy automatically for
        // MailUsers (Email Address Policy doesn't apply), so add it explicitly.
        // The routing domain comes from the TENANT, not the UPN — a target UPN on a
        // custom domain (user@contoso.com) still needs the MOERA proxy.
        var upnParts = targetUpn.Split('@');
        if (upnParts.Length == 2 && !string.IsNullOrWhiteSpace(targetRoutingDomain))
        {
            var routingProxy = $"smtp:{upnParts[0]}@{targetRoutingDomain}";
            if (!addresses.Any(a => string.Equals(a, routingProxy, StringComparison.OrdinalIgnoreCase)))
                addresses.Add(routingProxy);
        }

        var setParams = new Dictionary<string, object>
        {
            ["Identity"]         = targetUpn,
            ["EmailAddresses"]   = addresses.ToArray(),
            ["ExchangeGuid"]     = sourceAttributes.ExchangeGuid.ToString(),
            ["CustomAttribute1"] = "Cross-Tenant-Migration",
        };
        if (sourceAttributes.ArchiveGuid != Guid.Empty)
            setParams["ArchiveGuid"] = sourceAttributes.ArchiveGuid.ToString();

        await InvokeCommandAsync(aadTenantId, "Set-MailUser", setParams, credential, ct);

        _logger.LogInformation(
            "EXO: Stamped MailUser '{TargetUpn}' with ExchangeGuid {ExchangeGuid} + {X500Count} x500 proxy(s).",
            targetUpn, sourceAttributes.ExchangeGuid, sourceAttributes.X500Addresses.Count + 1);
    }

    /// <summary>
    /// Random password for the throwaway backing directory object of a target
    /// MailUser stub. The stub never signs in interactively — identity is bound
    /// via the copied ExchangeGuid — so the value only needs to satisfy Entra
    /// complexity (upper, lower, digit, symbol, length).
    /// </summary>
    private static string GenerateStubPassword()
    {
        Span<byte> bytes = stackalloc byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return "Aa1!" + Convert.ToBase64String(bytes).Replace('/', '_').Replace('+', '-');
    }

    /// <summary>Probe any recipient type at an address (Get-Recipient); null when none exists.</summary>
    private async Task<JsonElement?> TryGetRecipientAsync(
        string aadTenantId, string identity, TokenCredential credential, CancellationToken ct)
    {
        try
        {
            var results = await InvokeCommandAsync(
                aadTenantId,
                "Get-Recipient",
                new Dictionary<string, object> { ["Identity"] = identity },
                credential,
                ct);
            return results.Length == 0 ? null : results[0];
        }
        catch (InvalidOperationException ex) when (IsExoNotFound(ex))
        {
            return null;
        }
    }

    private async Task<JsonElement?> TryGetMailUserAsync(
        string aadTenantId, string identity, TokenCredential credential, CancellationToken ct)
    {
        try
        {
            var results = await InvokeCommandAsync(
                aadTenantId,
                "Get-MailUser",
                new Dictionary<string, object> { ["Identity"] = identity },
                credential,
                ct);
            return results.Length == 0 ? null : results[0];
        }
        catch (InvalidOperationException ex) when (IsExoNotFound(ex))
        {
            return null;
        }
    }

    // ── Scope distribution group ───────────────────────────────────────────

    public async Task<string> EnsureScopeDistributionGroupAsync(
        string aadTenantId,
        string groupName,
        string primarySmtpAddress,
        TokenCredential credential,
        CancellationToken ct)
    {
        try
        {
            var existing = await InvokeCommandAsync(
                aadTenantId,
                "Get-DistributionGroup",
                new Dictionary<string, object> { ["Identity"] = groupName },
                credential,
                ct);

            if (existing.Length > 0)
            {
                _logger.LogInformation(
                    "EXO: Scope distribution group '{GroupName}' already exists in tenant {TenantId}.",
                    groupName, aadTenantId);
                return GetStringProp(existing[0], "Name") ?? groupName;
            }
        }
        catch (InvalidOperationException ex) when (IsExoNotFound(ex))
        {
            // Falls through to create.
        }

        try
        {
            await InvokeCommandAsync(
                aadTenantId,
                "New-DistributionGroup",
                new Dictionary<string, object>
                {
                    ["Name"]               = groupName,
                    ["Type"]               = "Security",
                    ["PrimarySmtpAddress"] = primarySmtpAddress,
                },
                credential,
                ct);

            _logger.LogInformation(
                "EXO: Created scope distribution group '{GroupName}' ({Smtp}) in tenant {TenantId}.",
                groupName, primarySmtpAddress, aadTenantId);

            // EXO directory replication: a freshly-created DG is not immediately visible on every
            // backend server. Subsequent Add-DistributionGroupMember calls can hit a different
            // backend and 404 with ManagementObjectNotFoundException. Poll Get-DistributionGroup
            // until it returns the new object so callers don't have to retry.
            for (var attempt = 1; attempt <= 12; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                try
                {
                    var probe = await InvokeCommandAsync(
                        aadTenantId,
                        "Get-DistributionGroup",
                        new Dictionary<string, object> { ["Identity"] = groupName },
                        credential,
                        ct);
                    if (probe.Length > 0)
                    {
                        _logger.LogInformation(
                            "EXO: Scope DG '{GroupName}' replicated and visible after {Attempts} probe(s).",
                            groupName, attempt);
                        return groupName;
                    }
                }
                catch (InvalidOperationException ex) when (IsExoNotFound(ex))
                {
                    _logger.LogDebug(
                        "EXO: Scope DG '{GroupName}' not yet visible (attempt {Attempt}/12) — waiting for replication.",
                        groupName, attempt);
                }
            }

            _logger.LogWarning(
                "EXO: Scope DG '{GroupName}' was created but did not become visible within 60s. " +
                "Proceeding anyway — Add-DistributionGroupMember may still succeed if replication finishes mid-flight.",
                groupName);
            return groupName;
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "EXO: Scope distribution group '{GroupName}' already exists in tenant {TenantId} (race or duplicate name).",
                groupName, aadTenantId);
            return groupName;
        }
    }

    public async Task AddDistributionGroupMemberAsync(
        string aadTenantId,
        string groupIdentity,
        string memberUpn,
        TokenCredential credential,
        CancellationToken ct)
    {
        try
        {
            await InvokeCommandAsync(
                aadTenantId,
                "Add-DistributionGroupMember",
                new Dictionary<string, object>
                {
                    ["Identity"]                       = groupIdentity,
                    ["Member"]                         = memberUpn,
                    // Required for app-only auth: without it EXO rejects with
                    // "This operation can only be performed by a manager of the group"
                    // because the calling SP isn't listed in the DG's ManagedBy.
                    ["BypassSecurityGroupManagerCheck"] = true,
                },
                credential,
                ct);

            _logger.LogInformation(
                "EXO: Added '{Member}' to scope group '{Group}' in tenant {TenantId}.",
                memberUpn, groupIdentity, aadTenantId);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("already a member", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "EXO: '{Member}' is already a member of '{Group}' in tenant {TenantId} — no-op.",
                memberUpn, groupIdentity, aadTenantId);
        }
    }

    // ── Raw InvokeCommand for diagnostics ───────────────────────────────────

    public async Task<ExoRawInvokeResult> InvokeCommandRawAsync(
        string aadTenantId,
        string cmdletName,
        Dictionary<string, object> parameters,
        TokenCredential credential,
        CancellationToken ct)
    {
        var (client, tokenClaims) = await CreateAuthenticatedClientWithDiagAsync(credential, ct);
        var url = $"https://outlook.office365.com/adminapi/beta/{aadTenantId}/InvokeCommand";

        var payload = new
        {
            CmdletInput = new
            {
                CmdletName = cmdletName,
                Parameters = parameters,
            }
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
        });

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content, ct);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
        var responseBody = Encoding.UTF8.GetString(responseBytes).TrimEnd('\0');

        var hexPreview = ToHexPreview(responseBytes, 256);
        var textPreview = string.IsNullOrEmpty(responseBody)
            ? null
            : (responseBody.Length > 600 ? responseBody[..600] + "...(truncated)" : responseBody);

        var exceptionType = response.Headers.TryGetValues("X-ExceptionType", out var et)
            ? string.Join(",", et) : null;
        var requestId = response.Headers.TryGetValues("Request-Id", out var rid)
            ? string.Join(",", rid) : null;
        var wwwAuth = response.StatusCode == HttpStatusCode.Unauthorized
            ? response.Headers.WwwAuthenticate.ToString()
            : null;

        var parsedError = !response.IsSuccessStatusCode
            ? (ExtractInvokeCommandError(responseBody) ?? ExtractExoErrorMessage(responseBody))
            : null;

        var results = Array.Empty<JsonElement>();
        if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(responseBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                if (root.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    var arr = new JsonElement[values.GetArrayLength()];
                    int i = 0;
                    foreach (var item in values.EnumerateArray())
                        arr[i++] = item.Clone();
                    results = arr;
                }
                else
                {
                    results = new[] { root.Clone() };
                }
            }
            catch (JsonException) { /* leave results empty */ }
        }

        return new ExoRawInvokeResult(
            CmdletName: cmdletName,
            HttpStatus: (int)response.StatusCode,
            ReasonPhrase: response.ReasonPhrase,
            BodyByteLength: responseBytes.Length,
            BodyHexPreview: hexPreview,
            BodyTextPreview: textPreview,
            XExceptionType: exceptionType,
            RequestId: requestId,
            WwwAuthenticate: string.IsNullOrWhiteSpace(wwwAuth) ? null : wwwAuth,
            ParsedErrorMessage: parsedError,
            IsSuccess: response.IsSuccessStatusCode,
            Results: results,
            TokenClaims: tokenClaims);
    }

    // ── InvokeCommand — EXO Cmdlet REST API ────────────────────────────────

    /// <summary>
    /// Invokes an Exchange Online PowerShell cmdlet via the REST-based InvokeCommand
    /// endpoint. This is the same API that the Exchange Online PowerShell v3 module
    /// uses under the hood, and supports all cmdlets including migration operations
    /// that are NOT available as OData resources.
    /// </summary>
    /// <returns>An array of result objects from the cmdlet output.</returns>
    private async Task<JsonElement[]> InvokeCommandAsync(
        string aadTenantId,
        string cmdletName,
        Dictionary<string, object> parameters,
        TokenCredential credential,
        CancellationToken ct)
    {
        var (client, tokenClaims) = await CreateAuthenticatedClientWithDiagAsync(credential, ct);
        var url = $"https://outlook.office365.com/adminapi/beta/{aadTenantId}/InvokeCommand";

        var payload = new
        {
            CmdletInput = new
            {
                CmdletName = cmdletName,
                Parameters = parameters,
            }
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null, // preserve PascalCase
        });

        _logger.LogInformation("EXO InvokeCommand REQUEST: {CmdletName} on tenant {TenantId}. " +
            "TokenClaims: {TokenClaims}. Body: {Body}",
            cmdletName, aadTenantId, tokenClaims, json);

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content, ct);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
        var responseBody = Encoding.UTF8.GetString(responseBytes).TrimEnd('\0');

        _logger.LogDebug("EXO InvokeCommand response: {StatusCode} — {Body}",
            (int)response.StatusCode, responseBody);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60;
            throw new InvalidOperationException(
                $"EXO rate-limited on {cmdletName}. Retry after {retryAfter} seconds.");
        }

        if (!response.IsSuccessStatusCode)
        {
            // Capture every header (response + content) so the failure mode is observable.
            var allHeaders = new StringBuilder();
            foreach (var h in response.Headers)
                allHeaders.Append('[').Append(h.Key).Append("=").Append(string.Join(",", h.Value)).Append("] ");
            foreach (var h in response.Content.Headers)
                allHeaders.Append('[').Append(h.Key).Append("=").Append(string.Join(",", h.Value)).Append("] ");

            var hexPreview = ToHexPreview(responseBytes, 256);
            _logger.LogWarning(
                "EXO InvokeCommand FAILED: {CmdletName} → {Status} {Reason}. " +
                "Headers: {Headers}. BodyLen: {Len}. BodyHex: {Hex}. BodyText: {Text}",
                cmdletName, (int)response.StatusCode, response.ReasonPhrase,
                allHeaders.ToString(), responseBytes.Length, hexPreview, responseBody);

            // Pull the X-ExceptionType header (if any) — for 403s with NUL-padded bodies
            // EXO surfaces the real reason there (e.g. CmdletAccessDeniedException). Keep
            // the surfaced exception message short — full diagnostics are in the warning log.
            var exceptionType = response.Headers.TryGetValues("X-ExceptionType", out var et)
                ? string.Join(",", et)
                : null;
            var requestId = response.Headers.TryGetValues("Request-Id", out var rid)
                ? string.Join(",", rid)
                : null;
            var errorMsg = ExtractInvokeCommandError(responseBody)
                ?? ExtractExoErrorMessage(responseBody)
                ?? (response.StatusCode == HttpStatusCode.Forbidden
                        ? $"EXO {cmdletName} HTTP 403. X-ExceptionType={exceptionType ?? "(none)"} " +
                          $"Request-Id={requestId ?? "(none)"} BodyLen={responseBytes.Length}"
                        : $"EXO {cmdletName} failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase} " +
                          $"(X-ExceptionType={exceptionType ?? "(none)"})");

            // Log WWW-Authenticate on 401
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var wwwAuth = response.Headers.WwwAuthenticate.ToString();
                if (!string.IsNullOrWhiteSpace(wwwAuth))
                    _logger.LogWarning("EXO 401 WWW-Authenticate: {WwwAuthenticate}", wwwAuth);
            }

            _logger.LogWarning("EXO InvokeCommand error: {CmdletName} → {Error}", cmdletName, errorMsg);
            throw new InvalidOperationException(errorMsg);
        }

        // Parse the response — InvokeCommand returns { "value": [...] } or { "@odata.context": ..., "value": [...] }
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // Check for cmdlet-level errors in the response
            if (root.TryGetProperty("error", out var errorObj))
            {
                var msg = errorObj.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                throw new InvalidOperationException($"EXO {cmdletName}: {msg}");
            }

            if (root.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                var results = new JsonElement[values.GetArrayLength()];
                int i = 0;
                foreach (var item in values.EnumerateArray())
                    results[i++] = item.Clone();
                return results;
            }

            // Some cmdlets return the object directly (not wrapped in "value")
            return [root.Clone()];
        }
        catch (JsonException)
        {
            _logger.LogWarning("EXO InvokeCommand: could not parse response for {CmdletName}. Body: {Body}",
                cmdletName, responseBody);
            return [];
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the error message from an InvokeCommand error response.
    /// InvokeCommand errors may include <c>{"error":{"code":"...","message":"..."}}</c>
    /// or <c>{"ErrorRecords":[{"Message":"..."}]}</c>.
    /// </summary>
    private static string? ExtractInvokeCommandError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Shape 1: { "error": { "message": "...", "details": [{ "message": "..." }] } }
            // Prefer error.details[0].message — the top-level message is often the generic
            // "Error executing cmdlet", while the detail carries the real ManagementObjectNotFoundException
            // text that callers (e.g. EnsureScopeDistributionGroupAsync) match on.
            if (root.TryGetProperty("error", out var errorObj))
            {
                if (errorObj.TryGetProperty("details", out var details) &&
                    details.ValueKind == JsonValueKind.Array && details.GetArrayLength() > 0)
                {
                    var first = details[0];
                    if (first.TryGetProperty("message", out var detailMsg))
                    {
                        var detailText = detailMsg.GetString();
                        if (!string.IsNullOrWhiteSpace(detailText))
                        {
                            var pipeIdx = detailText.LastIndexOf('|');
                            return pipeIdx >= 0 && pipeIdx < detailText.Length - 1
                                ? detailText[(pipeIdx + 1)..]
                                : detailText;
                        }
                    }
                }
                if (errorObj.TryGetProperty("message", out var msgProp))
                    return msgProp.GetString();
            }

            // Shape 2: { "ErrorRecords": [{ "Message": "..." }] }
            if (root.TryGetProperty("ErrorRecords", out var records) &&
                records.ValueKind == JsonValueKind.Array &&
                records.GetArrayLength() > 0)
            {
                var first = records[0];
                if (first.TryGetProperty("Message", out var recMsg))
                    return recMsg.GetString();
                if (first.TryGetProperty("ErrorMessage", out var errMsg))
                    return errMsg.GetString();
            }

            // Shape 3: flat { "Message": "..." }
            if (root.TryGetProperty("Message", out var msg))
                return msg.GetString();
        }
        catch { /* not JSON */ }
        return null;
    }

    /// <summary>
    /// Extracts the human-readable error message from an EXO REST API JSON error body.
    /// EXO uses two shapes: <c>{"error":{"message":"..."}}</c> and <c>{"Message":"..."}</c>.
    /// Returns <see langword="null"/> if the body is empty, not JSON, or has no message property.
    /// </summary>
    private static string? ExtractExoErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var errorObj))
            {
                // Prefer the detailed message from error.details[0].message (contains the actual exception)
                if (errorObj.TryGetProperty("details", out var details) &&
                    details.ValueKind == JsonValueKind.Array && details.GetArrayLength() > 0)
                {
                    var first = details[0];
                    if (first.TryGetProperty("message", out var detailMsg))
                    {
                        var detailText = detailMsg.GetString();
                        if (!string.IsNullOrWhiteSpace(detailText))
                        {
                            // Strip the "|ExceptionType|" prefix if present
                            var pipeIdx = detailText.LastIndexOf('|');
                            if (pipeIdx >= 0 && pipeIdx < detailText.Length - 1)
                                return detailText[(pipeIdx + 1)..];
                            return detailText;
                        }
                    }
                }
                if (errorObj.TryGetProperty("message", out var msgProp))
                    return msgProp.GetString();
            }
            if (root.TryGetProperty("Message", out var msg))
                return msg.GetString();
        }
        catch { /* not JSON */ }
        return null;
    }

    // EXO surfaces "object missing" through several phrasings depending on cmdlet:
    // "not found", "couldn't find", "couldn't be found", "no results", or the underlying
    // ManagementObjectNotFoundException type name. Centralized so the next phrasing variant
    // doesn't silently break the missing-object → create fallback paths.
    private static bool IsExoNotFound(Exception ex)
    {
        var m = ex.Message;
        if (string.IsNullOrEmpty(m)) return false;
        return m.Contains("not found",                       StringComparison.OrdinalIgnoreCase)
            || m.Contains("couldn't find",                   StringComparison.OrdinalIgnoreCase)
            || m.Contains("couldn't be found",               StringComparison.OrdinalIgnoreCase)
            || m.Contains("can't be found",                  StringComparison.OrdinalIgnoreCase)
            || m.Contains("cannot be found",                 StringComparison.OrdinalIgnoreCase)
            || m.Contains("no results",                      StringComparison.OrdinalIgnoreCase)
            || m.Contains("ManagementObjectNotFoundException", StringComparison.Ordinal);
    }

    private static string? GetStringProp(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static int GetIntProp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return 0;
        if (prop.ValueKind == JsonValueKind.Number) return prop.GetInt32();
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var v)) return v;
        return 0;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(TokenCredential credential, CancellationToken ct)
    {
        var (client, _) = await CreateAuthenticatedClientWithDiagAsync(credential, ct);
        return client;
    }

    private async Task<(HttpClient Client, string TokenClaims)> CreateAuthenticatedClientWithDiagAsync(
        TokenCredential credential, CancellationToken ct)
    {
        var tokenResult = await credential.GetTokenAsync(
            new TokenRequestContext(ExoScopes), ct);

        var client = _httpClientFactory.CreateClient("exo");
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);

        return (client, DescribeAccessToken(tokenResult.Token));
    }

    /// <summary>
    /// Decodes the JWT payload (no signature validation — we just want claims for diagnostics)
    /// and returns a compact string with tid/appid/aud/roles. Returns "(unparsable)" on failure.
    /// </summary>
    private static string DescribeAccessToken(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return "(invalid jwt)";
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            string? Get(string n) => root.TryGetProperty(n, out var v) ? v.ToString() : null;
            return $"tid={Get("tid")} appid={Get("appid")} aud={Get("aud")} iss={Get("iss")} " +
                   $"roles={Get("roles")} exp={Get("exp")}";
        }
        catch (Exception ex)
        {
            return $"(parse error: {ex.Message})";
        }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string ToHexPreview(byte[] bytes, int max)
    {
        if (bytes.Length == 0) return "(empty)";
        var sb = new StringBuilder();
        var n = Math.Min(bytes.Length, max);
        for (int i = 0; i < n; i++) sb.AppendFormat("{0:x2}", bytes[i]);
        if (bytes.Length > max) sb.Append("...(truncated)");
        return sb.ToString();
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = string.Empty;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch { /* ignore read failure */ }

        await EnsureSuccessWithBodyAsync(response, body, operation);
    }

    /// <summary>
    /// Variant of <see cref="EnsureSuccessAsync"/> for callers that have already
    /// read the response body (to avoid consuming the stream twice).
    /// </summary>
    private Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, string body, string operation)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;

        // Log WWW-Authenticate header on 401 — it contains the specific auth failure reason.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            var wwwAuth = response.Headers.WwwAuthenticate.ToString();
            if (!string.IsNullOrWhiteSpace(wwwAuth))
                _logger.LogWarning("EXO 401 WWW-Authenticate: {WwwAuthenticate}", wwwAuth);
            else
                _logger.LogWarning("EXO 401 on {Operation} — no WWW-Authenticate header. " +
                    "Ensure the service principal is registered in Exchange Online via New-ServicePrincipal " +
                    "and that Exchange.ManageAsApp permission has admin consent.", operation);
        }

        var errorMessage = ExtractExoErrorMessage(body);
        var message = errorMessage ?? $"EXO {operation} failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
        _logger.LogWarning("EXO API error in {Operation}: {Message}", operation, message);
        throw new InvalidOperationException(message);
    }

    // Compiled regex to parse bytes from EXO size strings like "2.5 GB (2,684,354,648 bytes)"
    [GeneratedRegex(@"\((\d[\d,]*) bytes\)", RegexOptions.Compiled)]
    private static partial Regex BytesRegex();
}
