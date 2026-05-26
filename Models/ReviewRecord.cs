namespace StrataReports.Functions.Models;

public class ReviewRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ImportId { get; set; }
    public required string Platform { get; set; }
    public DateOnly ReviewDate { get; set; }
    public decimal Rating { get; set; }
    public string? ReviewText { get; set; }
    public string? GuestNameHash { get; set; }
    public string? ResponseText { get; set; }
    public string? ExternalRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
