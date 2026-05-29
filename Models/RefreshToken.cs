namespace StrataReports.Functions.Models;

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid Jti { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
