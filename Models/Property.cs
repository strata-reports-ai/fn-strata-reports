namespace StrataReports.Functions.Models;

public class Property
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public string? AddressLine1 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string CountryCode { get; set; } = "US";
    public int Units { get; set; } = 1;
    public string? OwnerName { get; set; }
    public string? OwnerEmail { get; set; }
    public DateOnly? ManagementStartDate { get; set; }
    public string Timezone { get; set; } = "America/New_York";
    public string CurrencyCode { get; set; } = "USD";
    public string? ExternalPmsId { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
