namespace MigrationPlatform.Api.Models;

public enum MappingStatus { Mapped, Unmapped, Conflict, Skipped }
public enum MappingSource { Auto, Manual, Csv }

public class IdentityMap
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string SourceUpn { get; set; } = string.Empty;
    public string? TargetUpn { get; set; }
    public MappingStatus Status { get; set; } = MappingStatus.Unmapped;
    public string? ConflictReason { get; set; }
    public MappingSource MappingSource { get; set; } = MappingSource.Auto;
}
