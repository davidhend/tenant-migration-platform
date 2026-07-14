using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.AssignLicense;
using Microsoft.Kiota.Abstractions;

namespace MigrationPlatform.Api.Services.Graph;

public sealed class LicenseCheckService : ILicenseCheckService
{
    private readonly ILogger<LicenseCheckService> _logger;

    // Service plan name prefixes Microsoft uses for the relevant workloads. We
    // match by prefix because there are many SKUs (E3, E5, Business Premium,
    // F3, etc.) and the plan names share common stems.
    private static readonly string[] OneDrivePlanPrefixes =
    {
        "SHAREPOINT", // SHAREPOINTSTANDARD, SHAREPOINTENTERPRISE, SHAREPOINTWAC, SHAREPOINTDESKLESS, SHAREPOINTONLINE_*
        "ONEDRIVE",   // ONEDRIVESTANDARD, ONEDRIVEENTERPRISE
    };

    private static readonly string[] ExchangePlanPrefixes =
    {
        "EXCHANGE",   // EXCHANGE_S_STANDARD, EXCHANGE_S_ENTERPRISE, EXCHANGE_S_DESKLESS, EXCHANGE_S_FOUNDATION
    };

    public LicenseCheckService(ILogger<LicenseCheckService> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<LicenseCheckResult>> CheckOneDriveLicensesAsync(
        GraphServiceClient client, IEnumerable<string> upns, CancellationToken ct) =>
        CheckAsync(client, upns, OneDrivePlanPrefixes, "SharePoint Online / OneDrive for Business", ct);

    public Task<IReadOnlyList<LicenseCheckResult>> CheckExchangeLicensesAsync(
        GraphServiceClient client, IEnumerable<string> upns, CancellationToken ct) =>
        CheckAsync(client, upns, ExchangePlanPrefixes, "Exchange Online", ct);

    // ProvisioningStatus values that indicate the license IS assigned and the workload
    // is — or will shortly be — usable. "Success" is fully provisioned; "PendingInput",
    // "PendingProvisioning", and "PendingActivation" are transient states that appear
    // for freshly-licensed users (esp. Microsoft 365 Business Basic on EXCHANGE_S_STANDARD)
    // and resolve to Success without any action required. Treating them as failures
    // blocks legitimate migrations on the first attempt after license assignment.
    // Only "Disabled" (admin-disabled service plan) and "Error" are real failures.
    private static readonly HashSet<string> AcceptableProvisioningStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Success",
            "PendingInput",
            "PendingProvisioning",
            "PendingActivation",
        };

    public async Task<CrossTenantLicenseEnsureResult> EnsureCrossTenantMigrationLicensesAsync(
        GraphServiceClient client,
        IEnumerable<string> upns,
        string defaultUsageLocation,
        CancellationToken ct)
    {
        var assigned = new List<string>();
        var alreadyLicensed = new List<string>();
        var failed = new List<LicenseAssignmentFailure>();
        var usageLocationsSet = new List<string>();

        var wanted = upns
            .Select(u => u?.Trim())
            .Where(u => !string.IsNullOrEmpty(u))
            .Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ── Locate the Cross Tenant User Data Migration SKU ─────────────────
        // Part numbers observed in the wild vary by channel/vintage
        // ("Cross_tenant_user_data_migration", "CROSSTENANTUSERDATAMIGRATION",
        // CSP-prefixed forms, …) — normalize away separators and substring-match.
        SubscribedSku? sku = null;
        int seatsAvailable = 0;
        try
        {
            var skus = await client.SubscribedSkus.GetAsync(cancellationToken: ct);
            var matches = skus?.Value?
                .Where(s => IsCrossTenantMigrationSku(s.SkuPartNumber)
                            && s.SkuId is not null
                            && !string.Equals(s.CapabilityStatus, "Suspended", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? new List<SubscribedSku>();

            // Prefer a match that still has seats; fall back to the first match so
            // "SKU exists but the pool is exhausted" is reported accurately.
            sku = matches.FirstOrDefault(s => AvailableSeats(s) > 0) ?? matches.FirstOrDefault();
            if (sku is not null) seatsAvailable = AvailableSeats(sku);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "License auto-assign: reading subscribedSkus failed — the app registration may be missing Organization.Read.All/Directory.Read.All.");
            return new CrossTenantLicenseEnsureResult(
                false, null, null, 0, wanted.Count, assigned, alreadyLicensed,
                wanted.Select(u => new LicenseAssignmentFailure(u, $"Could not read tenant SKUs: {ex.Message}")).ToList(),
                usageLocationsSet);
        }

        if (sku?.SkuId is null)
        {
            _logger.LogWarning(
                "License auto-assign: no 'Cross Tenant User Data Migration' SKU found on the tenant — purchase seats or assign manually.");
            return new CrossTenantLicenseEnsureResult(
                false, null, null, 0, wanted.Count, assigned, alreadyLicensed, failed, usageLocationsSet);
        }

        var skuId = sku.SkuId.Value;

        foreach (var upn in wanted)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var user = await client.Users[upn].GetAsync(cfg =>
                {
                    cfg.QueryParameters.Select = ["id", "userPrincipalName", "usageLocation", "assignedLicenses"];
                }, ct);

                if (user?.Id is null)
                {
                    failed.Add(new LicenseAssignmentFailure(upn, "User not found."));
                    continue;
                }

                if (user.AssignedLicenses?.Any(l => l.SkuId == skuId) == true)
                {
                    alreadyLicensed.Add(upn);
                    continue;
                }

                if (seatsAvailable - assigned.Count <= 0)
                {
                    failed.Add(new LicenseAssignmentFailure(upn,
                        $"No seats available on SKU '{sku.SkuPartNumber}' ({seatsAvailable} available, more needed)."));
                    continue;
                }

                // Graph refuses assignLicense for users without a usageLocation.
                if (string.IsNullOrWhiteSpace(user.UsageLocation))
                {
                    await client.Users[user.Id].PatchAsync(
                        new User { UsageLocation = defaultUsageLocation }, cancellationToken: ct);
                    usageLocationsSet.Add(upn);
                }

                // Assign ONLY the Cross Tenant User Data Migration SKU — it is a
                // standalone add-on with no Exchange service plan, so it is safe on a
                // target user whose MailUser stub hasn't been provisioned yet. Never
                // bundle other SKUs here: an Exchange-bearing license applied before
                // the MailUser carries the source ExchangeGuid provisions a brand-new
                // mailbox and permanently breaks the migration target (CLAUDE.md #6).
                await client.Users[user.Id].AssignLicense.PostAsync(new AssignLicensePostRequestBody
                {
                    AddLicenses = new List<AssignedLicense> { new() { SkuId = skuId } },
                    RemoveLicenses = new List<Guid?>(),
                }, cancellationToken: ct);

                assigned.Add(upn);
                _logger.LogInformation(
                    "License auto-assign: assigned '{Sku}' to {Upn}.", sku.SkuPartNumber, upn);
            }
            catch (ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                failed.Add(new LicenseAssignmentFailure(upn, "User not found."));
            }
            catch (ApiException ex) when (ex.ResponseStatusCode == 403)
            {
                failed.Add(new LicenseAssignmentFailure(upn,
                    "Access denied assigning the license. Grant the app registration the User.ReadWrite.All application permission with admin consent."));
            }
            catch (Exception ex)
            {
                failed.Add(new LicenseAssignmentFailure(upn, ex.Message));
            }
        }

        return new CrossTenantLicenseEnsureResult(
            true, skuId.ToString(), sku.SkuPartNumber, seatsAvailable,
            wanted.Count - alreadyLicensed.Count,
            assigned, alreadyLicensed, failed, usageLocationsSet);
    }

    /// <summary>
    /// Matches the Cross Tenant User Data Migration SKU across the part-number
    /// variants observed in the wild (underscored, upper-cased, CSP-prefixed) by
    /// stripping separators and substring-matching.
    /// </summary>
    internal static bool IsCrossTenantMigrationSku(string? skuPartNumber) =>
        NormalizeSkuPart(skuPartNumber).Contains("CROSSTENANTUSERDATAMIGRATION");

    internal static string NormalizeSkuPart(string? part) =>
        string.IsNullOrEmpty(part)
            ? string.Empty
            : new string(part.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static int AvailableSeats(SubscribedSku s) =>
        Math.Max(0, (s.PrepaidUnits?.Enabled ?? 0) + (s.PrepaidUnits?.Warning ?? 0) - (s.ConsumedUnits ?? 0));

    private async Task<IReadOnlyList<LicenseCheckResult>> CheckAsync(
        GraphServiceClient client,
        IEnumerable<string> upns,
        string[] requiredPrefixes,
        string workloadLabel,
        CancellationToken ct)
    {
        var results = new List<LicenseCheckResult>();
        foreach (var raw in upns)
        {
            ct.ThrowIfCancellationRequested();
            var upn = raw?.Trim();
            if (string.IsNullOrEmpty(upn)) continue;

            try
            {
                var details = await client.Users[upn].LicenseDetails.GetAsync(cancellationToken: ct);
                var plans = details?.Value?
                    .SelectMany(d => d.ServicePlans ?? new List<Microsoft.Graph.Models.ServicePlanInfo>())
                    .ToList() ?? new();

                var matchingPlans = plans
                    .Where(sp => !string.IsNullOrEmpty(sp.ServicePlanName) &&
                                 requiredPrefixes.Any(p => sp.ServicePlanName!.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                var acceptable = matchingPlans.FirstOrDefault(sp =>
                    !string.IsNullOrEmpty(sp.ProvisioningStatus) &&
                    AcceptableProvisioningStatuses.Contains(sp.ProvisioningStatus!));

                if (acceptable is not null)
                {
                    if (!string.Equals(acceptable.ProvisioningStatus, "Success", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "License check: {Upn} {Workload} plan '{Plan}' is in transient state '{Status}' — accepting as licensed.",
                            upn, workloadLabel, acceptable.ServicePlanName, acceptable.ProvisioningStatus);
                    }
                    results.Add(new LicenseCheckResult(upn, true, null));
                }
                else
                {
                    var disabledMatch = matchingPlans.FirstOrDefault();
                    var reason = disabledMatch is not null
                        ? $"{workloadLabel} service plan '{disabledMatch.ServicePlanName}' is present but disabled (status: {disabledMatch.ProvisioningStatus ?? "unknown"})."
                        : $"{workloadLabel} service plan not found on any assigned license.";

                    results.Add(new LicenseCheckResult(upn, false, reason));
                }
            }
            catch (ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                results.Add(new LicenseCheckResult(upn, false, "User not found in target tenant."));
            }
            catch (ApiException ex) when (ex.ResponseStatusCode == 403)
            {
                _logger.LogWarning(
                    "License check for {Upn} returned 403 — the app registration is missing the User.Read.All or Directory.Read.All permission.",
                    upn);
                results.Add(new LicenseCheckResult(upn, false,
                    "Access denied reading license details. Grant the target tenant app registration the User.Read.All permission."));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "License check failed for {Upn}.", upn);
                results.Add(new LicenseCheckResult(upn, false, $"License check failed: {ex.Message}"));
            }
        }
        return results;
    }
}
