namespace MigrationPlatform.Api.Models;

public enum ProjectStatus { Draft, Active, Paused, Completed }

public class MigrationProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid SourceTenantId { get; set; }
    public Guid TargetTenantId { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties — populated by EF Core Include() in the repository layer
    public Tenant? SourceTenant { get; set; }
    public Tenant? TargetTenant { get; set; }
}
