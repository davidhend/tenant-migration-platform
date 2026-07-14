namespace MigrationPlatform.Api.Services.Spo;

/// <summary>Result returned after starting a cross-tenant user content move.</summary>
/// <param name="JobId">An opaque identifier we assign (the cmdlet does not return one; we use the source UPN).</param>
/// <param name="Status">Initial status string from the cmdlet, typically "Scheduled" or "NotStarted".</param>
public record SpoMigrationJobResult(string JobId, string Status);

/// <summary>
/// Current state of a cross-tenant user content move, as reported by
/// <c>Get-SPOCrossTenantUserContentMoveState</c>.
/// Status values: NotStarted, Scheduled, ReadyToTrigger, InProgress, Success, Rescheduled, Failed.
/// </summary>
public record SpoMigrationJobStatus(
    string JobId,
    string Status,
    int ProgressPercent,
    string? ErrorMessage);

/// <summary>
/// App-only certificate credentials required for <c>Connect-SPOService -ClientId ... -CertificatePath ...</c>.
/// </summary>
/// <param name="TenantId">Entra tenant GUID.</param>
/// <param name="ClientId">Application (client) ID of the SPO app registration.</param>
/// <param name="CertificatePfxBase64">Base64-encoded PFX bytes. The client will write this to a temp file and delete it after the call.</param>
/// <param name="CertificatePassword">PFX password (may be null/empty if the PFX has none).</param>
/// <param name="KeyVaultCertificateName">
/// Name of the Key Vault secret holding the same PFX. When set and
/// <c>Azure:Automation:UseKeyVaultCertificate</c> is true, the runbook fetches
/// the certificate from Key Vault via managed identity instead of receiving the
/// PFX bytes as a portal-visible job parameter.
/// </param>
public record SpoPowerShellCredentials(
    string TenantId,
    string ClientId,
    string CertificatePfxBase64,
    string? CertificatePassword,
    string? KeyVaultCertificateName = null)
{
    /// <summary>
    /// The Key Vault secret name under which <see cref="Services.KeyVault.KeyVaultCredentialService"/>
    /// stores a platform tenant's app-only certificate (see its <c>SecretNames</c> convention).
    /// </summary>
    public static string DefaultKeyVaultCertificateName(Guid platformTenantId) =>
        $"tenant-{platformTenantId:N}-cert";
}

/// <summary>Result of a cross-tenant compatibility preflight check.</summary>
/// <param name="IsCompatible">True when the relationship status is Compatible or Warning.</param>
/// <param name="Status">Raw status string from the cmdlet (e.g. "Compatible", "NotEstablished").</param>
/// <param name="ErrorMessage">Non-null when the check itself failed (runbook error, RBAC denied, etc.).</param>
public record SpoCompatibilityResult(
    bool IsCompatible,
    string? Status,
    string? ErrorMessage);

/// <summary>
/// Outcome of automatically establishing the SPO MnA cross-tenant relationship
/// (<c>Set-SPOCrossTenantRelationship</c> run on both sides, verified with
/// <c>Test-SPOCrossTenantRelationship</c> and a final compatibility re-check).
/// </summary>
/// <param name="IsEstablished">True when the post-establishment compatibility check passed.</param>
/// <param name="CompatibilityStatus">Compatibility status after the attempt (e.g. "Compatible").</param>
/// <param name="TargetSideTestStatus"><c>Test-SPOCrossTenantRelationship</c> result on the target side (expected "GoodToProceed").</param>
/// <param name="SourceSideTestStatus"><c>Test-SPOCrossTenantRelationship</c> result on the source side (expected "GoodToProceed").</param>
/// <param name="ErrorMessage">Non-null when any step failed.</param>
public record SpoRelationshipResult(
    bool IsEstablished,
    string? CompatibilityStatus,
    string? TargetSideTestStatus,
    string? SourceSideTestStatus,
    string? ErrorMessage);
