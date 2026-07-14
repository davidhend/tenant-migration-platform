using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.DTOs;

public record CreateTenantRequest(
    string DisplayName,
    string TenantId,
    TenantRole Role,
    string AppClientId,
    AuthMethod AuthMethod,
    string? ClientSecret
);

public record UpdateTenantRequest(
    string? DisplayName,
    bool? AdminConsentGranted
);

public record UpdateCredentialsRequest(
    AuthMethod AuthMethod,
    string? AppClientId,
    string? ClientSecret,
    string? ClientCertificateBase64,
    string? ClientCertificatePassword,
    string? ClientCertificateThumbprint
);

public record VerifyConnectionResponse(
    bool Success,
    string Message,
    string? OrganizationName = null,
    DateTime? VerifiedAt = null);
