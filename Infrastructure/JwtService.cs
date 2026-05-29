using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace StrataReports.Functions.Infrastructure;

public interface IJwtService
{
    string GenerateAccessToken(Guid userId, Guid tenantId, string email, string role);
    (string token, Guid jti) GenerateRefreshToken(Guid userId, Guid tenantId);
    ClaimsPrincipal? ValidateAccessToken(string token);
    ClaimsPrincipal? ValidateRefreshToken(string token);
}

public class JwtService(IConfiguration configuration) : IJwtService
{
    private string Secret => configuration["JWT_SECRET"]
        ?? throw new InvalidOperationException("JWT_SECRET configuration is required");

    private static readonly TimeSpan AccessTokenTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshTokenTtl = TimeSpan.FromDays(7);

    public string GenerateAccessToken(Guid userId, Guid tenantId, string email, string role)
    {
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(Secret));
        SigningCredentials creds = new(key, SecurityAlgorithms.HmacSha256);

        Claim[] claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("role", role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        ];

        JwtSecurityToken tokenDescriptor = new(
            issuer: "strata-reports",
            audience: "strata-reports",
            claims: claims,
            expires: DateTime.UtcNow.Add(AccessTokenTtl),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);
    }

    public (string token, Guid jti) GenerateRefreshToken(Guid userId, Guid tenantId)
    {
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(Secret));
        SigningCredentials creds = new(key, SecurityAlgorithms.HmacSha256);

        Guid jti = Guid.NewGuid();

        Claim[] claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, jti.ToString()),
            new Claim("token_type", "refresh"),
        ];

        JwtSecurityToken tokenDescriptor = new(
            issuer: "strata-reports",
            audience: "strata-reports",
            claims: claims,
            expires: DateTime.UtcNow.Add(RefreshTokenTtl),
            signingCredentials: creds);

        string token = new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);
        return (token, jti);
    }

    public ClaimsPrincipal? ValidateAccessToken(string token)
    {
        return ValidateToken(token, requireRefreshType: false);
    }

    public ClaimsPrincipal? ValidateRefreshToken(string token)
    {
        return ValidateToken(token, requireRefreshType: true);
    }

    private ClaimsPrincipal? ValidateToken(string token, bool requireRefreshType)
    {
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(Secret));
        JwtSecurityTokenHandler handler = new();

        TokenValidationParameters validationParams = new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateIssuer = true,
            ValidIssuer = "strata-reports",
            ValidateAudience = true,
            ValidAudience = "strata-reports",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        try
        {
            ClaimsPrincipal principal = handler.ValidateToken(token, validationParams, out _);

            if (requireRefreshType)
            {
                string? tokenType = principal.FindFirstValue("token_type");
                if (tokenType != "refresh")
                    return null;
            }

            return principal;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
    }
}
