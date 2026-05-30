namespace StrataReports.Functions.Models;

public class ExpenseRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ImportId { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public required string Category { get; set; }
    public string? Vendor { get; set; }
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public string? ExternalRef { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
