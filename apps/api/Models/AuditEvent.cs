namespace MigrationPlatform.Api.Models;

public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = "system";
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public Guid? ProjectId { get; set; }
    public string Outcome { get; set; } = "success";
    public string? Details { get; set; }
}
