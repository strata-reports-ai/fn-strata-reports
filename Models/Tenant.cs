namespace StrataReports.Functions.Models;

public class Tenant
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? StripeCustomerId { get; set; }
    public string Plan { get; set; } = "beta";
    public string Status { get; set; } = "active";
    public DateTimeOffset? TrialEndsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
