namespace StrataReports.Functions.Models;

public class InspectionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ImportId { get; set; }
    public DateOnly InspectionDate { get; set; }
    public string? Inspector { get; set; }
    public decimal? Score { get; set; }
    public int? IssuesFound { get; set; }
    public int? IssuesResolved { get; set; }
    public string? Notes { get; set; }
    public string? ExternalRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
