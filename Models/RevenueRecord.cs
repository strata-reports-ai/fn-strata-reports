namespace StrataReports.Functions.Models;

public class RevenueRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ImportId { get; set; }
    public required string BookingExternalId { get; set; }
    public required string Platform { get; set; }
    public DateOnly CheckinDate { get; set; }
    public DateOnly CheckoutDate { get; set; }
    public int Nights { get; set; }
    public decimal GrossRevenue { get; set; }
    public decimal CleaningFee { get; set; }
    public decimal PlatformFee { get; set; }
    public decimal HostFee { get; set; }
    public decimal NetRevenue { get; set; }
    public string? GuestNameHash { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public DateTimeOffset CreatedAt { get; set; }
}
