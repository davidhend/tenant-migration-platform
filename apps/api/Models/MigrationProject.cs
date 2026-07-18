namespace MigrationPlatform.Api.Models;

public enum ProjectStatus { Draft, Active, Paused, Completed }

/// <summary>
/// How migrated identities relate to the TARGET tenant's directory model.
/// <see cref="CloudOnly"/> (default) is the platform's classic behavior;
/// <see cref="Hybrid"/> additionally offers the on-prem AD handoff kit and a
/// directory-sync validation check for Entra Connect targets. The migration
/// flow itself is identical in both modes.
/// </summary>
public enum TargetDirectoryMode { CloudOnly, Hybrid }

public class MigrationProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid SourceTenantId { get; set; }
    public Guid TargetTenantId { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Draft;
    public TargetDirectoryMode TargetDirectoryMode { get; set; } = TargetDirectoryMode.CloudOnly;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties — populated by EF Core Include() in the repository layer
    public Tenant? SourceTenant { get; set; }
    public Tenant? TargetTenant { get; set; }
}
