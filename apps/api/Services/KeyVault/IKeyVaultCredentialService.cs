namespace MigrationPlatform.Api.Services.KeyVault;

/// <summary>
/// Stores and retrieves tenant credentials (certificate PFX bytes and client
/// secrets) from Azure Key Vault. When Key Vault is disabled the implementation
/// is a no-op that returns all nulls, preserving the existing database-backed
/// credential path without any behavioural change.
/// </summary>
/// <remarks>
/// Secret naming convention (Key Vault names: alphanumeric + hyphens, max 127 chars):
/// <list type="bullet">
///   <item>Certificate PFX (base64): <c>tenant-{tenantId:N}-cert</c></item>
///   <item>Certificate password:     <c>tenant-{tenantId:N}-cert-password</c></item>
///   <item>Client secret:            <c>tenant-{tenantId:N}-secret</c></item>
/// </list>
/// The <c>:N</c> format removes hyphens from the GUID so the secret name stays
/// within Key Vault's alphanumeric-plus-hyphens constraint.
/// </remarks>
public interface IKeyVaultCredentialService
{
    /// <summary>
    /// Indicates whether Key Vault storage is active. When <see langword="false"/>
    /// all methods are no-ops and the database credential columns remain the
    /// source of truth.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Writes non-null credential values to Key Vault. Null arguments are
    /// skipped — the corresponding secrets are left unchanged (or absent if
    /// they have never been stored).
    /// </summary>
    /// <param name="tenantId">The platform tenant record identifier.</param>
    /// <param name="certificateBase64">Base64-encoded PFX/PEM bytes, or <see langword="null"/> to skip.</param>
    /// <param name="certificatePassword">PFX password, or <see langword="null"/> to skip.</param>
    /// <param name="clientSecret">Plain-text client secret, or <see langword="null"/> to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StoreCredentialsAsync(
        Guid tenantId,
        string? certificateBase64,
        string? certificatePassword,
        string? clientSecret,
        CancellationToken ct = default);

    /// <summary>
    /// Loads credential values from Key Vault for the specified tenant.
    /// Returns <see langword="null"/> for any value that has not been stored.
    /// </summary>
    /// <returns>
    /// A tuple of <c>(CertificateBase64, CertificatePassword, ClientSecret)</c>.
    /// Each member is <see langword="null"/> when the corresponding Key Vault
    /// secret does not exist or Key Vault is disabled.
    /// </returns>
    Task<(string? CertificateBase64, string? CertificatePassword, string? ClientSecret)>
        LoadCredentialsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Schedules all three credential secrets for deletion in Key Vault.
    /// 404 responses (secret was never stored) are silently ignored.
    /// </summary>
    Task DeleteCredentialsAsync(Guid tenantId, CancellationToken ct = default);
}
