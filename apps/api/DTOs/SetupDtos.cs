namespace MigrationPlatform.Api.DTOs;

/// <summary>
/// Guided pre-setup plan for a project's tenant pair. Every value the admin
/// needs (consent URLs, filled-in bootstrap scripts, config gaps) is computed
/// server-side from the tenant rows + appsettings so the UI renders exactly
/// what remains to be done. Live verification is delegated to the existing
/// diagnostics endpoint referenced by <see cref="SetupTenantInfo.VerifyEndpoint"/>.
/// </summary>
public sealed class SetupPlanResponse
{
    public Guid ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public SetupTenantInfo SourceTenant { get; set; } = new();
    public SetupTenantInfo TargetTenant { get; set; } = new();

    /// <summary>Client ID of the cross-tenant mailbox migration app (Platform:CrossTenantMigration:AppId), when configured.</summary>
    public string? MigrationAppId { get; set; }

    /// <summary>True when Platform:CrossTenantMigration:ClientSecret is set. The secret itself is never returned.</summary>
    public bool ClientSecretConfigured { get; set; }

    public List<SetupStep> Steps { get; set; } = new();
    public DateTime GeneratedAt { get; set; }
}

public sealed class SetupTenantInfo
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string AadTenantId { get; set; } = "";
    public string? OnMicrosoftDomain { get; set; }
    public string AppClientId { get; set; } = "";

    /// <summary>True when a credential source (certificate blob or client secret) is present on the tenant row.</summary>
    public bool CredentialConfigured { get; set; }

    /// <summary>Relative API path to POST for live prerequisite verification of this tenant.</summary>
    public string VerifyEndpoint { get; set; } = "";
}

/// <summary>
/// One actionable pre-setup step. <c>Kind</c> drives the UI affordance:
/// link → open-in-new-tab button, script → copy-to-clipboard code block,
/// config → appsettings gap badge, info → irreducible manual step.
/// </summary>
public sealed class SetupStep
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>exchange | spo | entra | azure</summary>
    public string Category { get; set; } = "";

    /// <summary>sourceAdmin | targetAdmin | either</summary>
    public string Audience { get; set; } = "";

    /// <summary>link | script | config | info</summary>
    public string Kind { get; set; } = "";

    /// <summary>unknown | pending | done</summary>
    public string Status { get; set; } = "";

    public string Detail { get; set; } = "";
    public string? ActionUrl { get; set; }
    public string? Script { get; set; }
}
