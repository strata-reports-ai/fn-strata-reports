namespace StrataReports.Functions.Models;

public class AppUser
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string EntraObjectId { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public string Role { get; set; } = "owner";
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
