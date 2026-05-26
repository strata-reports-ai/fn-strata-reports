namespace StrataReports.Functions.Models;

public class TaskRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ImportId { get; set; }
    public required string TaskType { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string Status { get; set; }
    public string? Assignee { get; set; }
    public string? Notes { get; set; }
    public string? ExternalRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
