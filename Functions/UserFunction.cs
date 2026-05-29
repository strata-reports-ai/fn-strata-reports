using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Functions;

public class UserFunction(
    ILogger<UserFunction> logger,
    AppDbContext db,
    IEmailService emailService)
{
    [Function("UsersGetMe")]
    public async Task<HttpResponseData> GetMe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users/me")] HttpRequestData req,
        FunctionContext context,
        CancellationToken ct)
    {
        if (!TryGetUserId(context, out Guid userId))
            return await Unauthorized(req, "Authentication required.");

        AppUser? user = await db.Users.FindAsync([userId], ct);
        if (user is null)
            return await NotFound(req, "User not found.");

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            $"{{\"id\":\"{user.Id}\",\"email\":\"{EscapeJson(user.Email)}\",\"displayName\":{JsonStringOrNull(user.DisplayName)},\"role\":\"{EscapeJson(user.Role)}\",\"isEmailVerified\":{user.IsEmailVerified.ToString().ToLowerInvariant()}}}");
        return response;
    }

    [Function("UsersPatchMe")]
    public async Task<HttpResponseData> PatchMe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "users/me")] HttpRequestData req,
        FunctionContext context,
        CancellationToken ct)
    {
        if (!TryGetUserId(context, out Guid userId))
            return await Unauthorized(req, "Authentication required.");

        PatchMeRequest? body = await req.ReadFromJsonAsync<PatchMeRequest>(ct);
        if (body is null)
            return await BadRequest(req, "Request body is required.");

        AppUser? user = await db.Users.FindAsync([userId], ct);
        if (user is null)
            return await NotFound(req, "User not found.");

        bool changed = false;

        if (body.DisplayName is not null)
        {
            user.DisplayName = body.DisplayName;
            changed = true;
        }

        if (body.NewEmail is not null)
        {
            string normalizedNewEmail = body.NewEmail.ToLowerInvariant();
            if (normalizedNewEmail != user.Email)
            {
                bool emailTaken = await db.Users.AnyAsync(u => u.Email == normalizedNewEmail && u.Id != user.Id, ct);
                if (emailTaken)
                    return await BadRequest(req, "That email address is already in use.");

                string verificationToken = GenerateSecureToken();
                user.PendingEmail = normalizedNewEmail;
                user.EmailVerificationToken = verificationToken;
                user.EmailVerificationTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(24);

                await emailService.SendVerificationEmailAsync(normalizedNewEmail, verificationToken, ct);

                logger.LogInformation("Email change requested for user {UserId} to {NewEmail}", user.Id, normalizedNewEmail);
                changed = true;
            }
        }

        if (body.CurrentPassword is not null && body.NewPassword is not null)
        {
            if (body.NewPassword.Length < 12)
                return await BadRequest(req, "New password must be at least 12 characters.");

            string hashToVerify = user.PasswordHash ?? DummyPasswordHash;
            if (!VerifyPassword(body.CurrentPassword, hashToVerify))
                return await BadRequest(req, "Current password is incorrect.");

            user.PasswordHash = HashPassword(body.NewPassword);
            changed = true;
        }

        if (!changed)
            return await BadRequest(req, "No changes provided.");

        user.UpdatedAt = DateTimeOffset.UtcNow;

        AuditLog auditEntry = new()
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            UserId = user.Id,
            Action = "user.update",
            EntityType = "AppUser",
            EntityId = user.Id,
            IpAddress = GetClientIp(req),
            UserAgent = GetUserAgent(req),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        db.AuditLogs.Add(auditEntry);

        await db.SaveChangesAsync(ct);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync("{\"message\":\"Profile updated successfully.\"}");
        return response;
    }

    private static bool TryGetUserId(FunctionContext context, out Guid userId)
    {
        userId = Guid.Empty;
        if (!context.Items.TryGetValue("UserId", out object? userIdObj))
            return false;
        return Guid.TryParse(userIdObj?.ToString(), out userId);
    }

    private static string? GetClientIp(HttpRequestData req)
    {
        if (req.Headers.TryGetValues("X-Azure-ClientIP", out IEnumerable<string>? azureIp))
        {
            string? ip = azureIp.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(ip))
                return ip.Trim();
        }
        if (req.Headers.TryGetValues("X-Forwarded-For", out IEnumerable<string>? values))
        {
            string? first = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
                return first.Split(',')[0].Trim();
        }
        return "unknown";
    }

    private static string GetUserAgent(HttpRequestData req)
    {
        if (req.Headers.TryGetValues("User-Agent", out IEnumerable<string>? values))
            return values.FirstOrDefault() ?? "unknown";
        return "unknown";
    }

    private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string message)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.BadRequest);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync($"{{\"error\":\"{EscapeJson(message)}\"}}");
        return response;
    }

    private static async Task<HttpResponseData> Unauthorized(HttpRequestData req, string message)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.Unauthorized);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync($"{{\"error\":\"{EscapeJson(message)}\"}}");
        return response;
    }

    private static async Task<HttpResponseData> NotFound(HttpRequestData req, string message)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.NotFound);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync($"{{\"error\":\"{EscapeJson(message)}\"}}");
        return response;
    }

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string JsonStringOrNull(string? value)
        => value is null ? "null" : $"\"{EscapeJson(value)}\"";

    private static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        string[] parts = storedHash.Split(':');
        if (parts.Length != 2) return false;
        byte[] salt = Convert.FromBase64String(parts[0]);
        byte[] expectedHash = Convert.FromBase64String(parts[1]);
        byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static string GenerateSecureToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }

    private const string DummyPasswordHash =
        "AAAAAAAAAAAAAAAAAAAAAA==:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private sealed record PatchMeRequest(
        string? DisplayName,
        string? NewEmail,
        string? CurrentPassword,
        string? NewPassword);
}
