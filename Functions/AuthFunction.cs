using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Functions;

public class AuthFunction(
    ILogger<AuthFunction> logger,
    AppDbContext db,
    IJwtService jwtService,
    IEmailService emailService)
{
    [Function("AuthRegister")]
    public async Task<HttpResponseData> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/register")] HttpRequestData req,
        CancellationToken ct)
    {
        RegisterRequest? body = await req.ReadFromJsonAsync<RegisterRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
            return await BadRequest(req, "Email and password are required.");

        if (body.Password.Length < 8)
            return await BadRequest(req, "Password must be at least 8 characters.");

        bool emailExists = await db.Users.AnyAsync(u => u.Email == body.Email.ToLowerInvariant(), ct);
        if (emailExists)
            return await BadRequest(req, "An account with this email already exists.");

        Tenant tenant = new()
        {
            Id = Guid.NewGuid(),
            Name = body.OrganisationName ?? body.Email,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        string verificationToken = GenerateSecureToken();

        AppUser user = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EntraObjectId = $"local:{Guid.NewGuid()}",
            Email = body.Email.ToLowerInvariant(),
            DisplayName = body.DisplayName,
            PasswordHash = HashPassword(body.Password),
            IsEmailVerified = false,
            EmailVerificationToken = verificationToken,
            EmailVerificationTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        db.Tenants.Add(tenant);
        db.Users.Add(user);

        AuditLog auditEntry = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            UserId = user.Id,
            Action = "register",
            IpAddress = GetClientIp(req),
            UserAgent = GetUserAgent(req),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        db.AuditLogs.Add(auditEntry);

        await db.Database.ExecuteSqlRawAsync("SET app.current_tenant_id = {0}", tenant.Id.ToString());
        await db.SaveChangesAsync(ct);

        await emailService.SendVerificationEmailAsync(user.Email, verificationToken, ct);

        logger.LogInformation("New user registered: {UserId} tenant: {TenantId}", user.Id, tenant.Id);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            $"{{\"message\":\"Registration successful. Please verify your email.\",\"redirectTo\":\"/onboarding/welcome\"}}");
        return response;
    }

    [Function("AuthLogin")]
    public async Task<HttpResponseData> Login(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequestData req,
        CancellationToken ct)
    {
        LoginRequest? body = await req.ReadFromJsonAsync<LoginRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
            return await BadRequest(req, "Email and password are required.");

        AppUser? user = await db.Users.FirstOrDefaultAsync(
            u => u.Email == body.Email.ToLowerInvariant(), ct);

        // Always run VerifyPassword (even when user is null) to prevent timing-based user enumeration.
        string hashToVerify = user?.PasswordHash ?? DummyPasswordHash;
        bool passwordValid = VerifyPassword(body.Password, hashToVerify);

        if (user is null || string.IsNullOrEmpty(user.PasswordHash) || !passwordValid)
        {
            await db.Database.ExecuteSqlRawAsync("SET app.current_tenant_id = '00000000-0000-0000-0000-000000000000'");
            await WriteAuditLog(db, null, null, "login_failed", GetClientIp(req), GetUserAgent(req), ct);
            return await Unauthorized(req, "Invalid email or password.");
        }

        if (!user.IsEmailVerified)
            return await BadRequest(req, "Please verify your email address before logging in.");

        user.LastLoginAt = DateTimeOffset.UtcNow;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        string accessToken = jwtService.GenerateAccessToken(user.Id, user.TenantId, user.Email, user.Role);
        (string refreshTokenValue, Guid jti) = jwtService.GenerateRefreshToken(user.Id, user.TenantId);

        RefreshToken refreshToken = new()
        {
            Id = Guid.NewGuid(),
            Jti = jti,
            UserId = user.Id,
            TenantId = user.TenantId,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.RefreshTokens.Add(refreshToken);

        AuditLog auditEntry = new()
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            UserId = user.Id,
            Action = "login",
            IpAddress = GetClientIp(req),
            UserAgent = GetUserAgent(req),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        db.AuditLogs.Add(auditEntry);

        await db.Database.ExecuteSqlRawAsync("SET app.current_tenant_id = {0}", user.TenantId.ToString());
        await db.SaveChangesAsync(ct);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        SetAuthCookies(response, accessToken, refreshTokenValue);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            $"{{\"message\":\"Login successful.\",\"redirectTo\":\"/onboarding/welcome\"}}");
        return response;
    }

    [Function("AuthLogout")]
    public async Task<HttpResponseData> Logout(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/logout")] HttpRequestData req,
        CancellationToken ct)
    {
        string? refreshTokenValue = GetCookieValue(req, "refresh_token");

        if (!string.IsNullOrEmpty(refreshTokenValue))
        {
            ClaimsPrincipal? principal = jwtService.ValidateRefreshToken(refreshTokenValue);
            if (principal is not null)
            {
                string? jtiClaim = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
                string? subClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? principal.FindFirstValue("sub");

                if (Guid.TryParse(jtiClaim, out Guid jti))
                {
                    RefreshToken? storedToken = await db.RefreshTokens
                        .FirstOrDefaultAsync(r => r.Jti == jti && r.RevokedAt == null, ct);

                    if (storedToken is not null)
                    {
                        storedToken.RevokedAt = DateTimeOffset.UtcNow;

                        Guid? userId = Guid.TryParse(subClaim, out Guid uid) ? uid : null;
                        AppUser? user = userId.HasValue
                            ? await db.Users.FindAsync([userId.Value], ct)
                            : null;

                        AuditLog auditEntry = new()
                        {
                            Id = Guid.NewGuid(),
                            TenantId = user?.TenantId,
                            UserId = user?.Id,
                            Action = "logout",
                            IpAddress = GetClientIp(req),
                            UserAgent = GetUserAgent(req),
                            OccurredAt = DateTimeOffset.UtcNow,
                        };
                        db.AuditLogs.Add(auditEntry);

                        await db.SaveChangesAsync(ct);
                    }
                }
            }
        }

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        ClearAuthCookies(response);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync("{\"message\":\"Logged out successfully.\"}");
        return response;
    }

    [Function("AuthForgotPassword")]
    public async Task<HttpResponseData> ForgotPassword(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/forgot-password")] HttpRequestData req,
        CancellationToken ct)
    {
        ForgotPasswordRequest? body = await req.ReadFromJsonAsync<ForgotPasswordRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Email))
        {
            HttpResponseData ok200 = req.CreateResponse(HttpStatusCode.OK);
            ok200.Headers.Add("Content-Type", "application/json");
            await ok200.WriteStringAsync(
                "{\"message\":\"If that email address is registered, a reset link has been sent.\"}");
            return ok200;
        }

        string normalizedEmail = body.Email.ToLowerInvariant();
        AppUser? user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

        if (user is not null)
        {
            string plainToken = GenerateSecureToken();
            string tokenHash = HashToken(plainToken);

            PasswordResetToken resetToken = new()
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = tokenHash,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.PasswordResetTokens.Add(resetToken);

            AuditLog auditEntry = new()
            {
                Id = Guid.NewGuid(),
                TenantId = user.TenantId,
                UserId = user.Id,
                Action = "password_reset_requested",
                IpAddress = GetClientIp(req),
                UserAgent = GetUserAgent(req),
                OccurredAt = DateTimeOffset.UtcNow,
            };
            db.AuditLogs.Add(auditEntry);

            await db.Database.ExecuteSqlRawAsync("SET app.current_tenant_id = {0}", user.TenantId.ToString());
            await db.SaveChangesAsync(ct);

            await emailService.SendPasswordResetEmailAsync(user.Email, plainToken, ct);

            logger.LogInformation("Password reset requested for user {UserId}", user.Id);
        }

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            "{\"message\":\"If that email address is registered, a reset link has been sent.\"}");
        return response;
    }

    [Function("AuthResetPassword")]
    public async Task<HttpResponseData> ResetPassword(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/reset-password")] HttpRequestData req,
        CancellationToken ct)
    {
        ConfirmResetPasswordRequest? body = await req.ReadFromJsonAsync<ConfirmResetPasswordRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.NewPassword))
            return await ProblemDetails(req, HttpStatusCode.BadRequest, "Token and new password are required.");

        if (body.NewPassword.Length < 12)
            return await ProblemDetails(req, HttpStatusCode.BadRequest, "Password must be at least 12 characters.");

        string tokenHash = HashToken(body.Token);

        PasswordResetToken? resetToken = await db.PasswordResetTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

        if (resetToken is null || resetToken.UsedAt is not null)
            return await ProblemDetails(req, HttpStatusCode.BadRequest, "Invalid or already-used reset token.");

        if (resetToken.ExpiresAt < DateTimeOffset.UtcNow)
            return await ProblemDetails(req, HttpStatusCode.BadRequest, "Reset token has expired. Please request a new password reset link.");

        AppUser? user = await db.Users.FindAsync([resetToken.UserId], ct);
        if (user is null)
            return await ProblemDetails(req, HttpStatusCode.BadRequest, "User not found.");

        resetToken.UsedAt = DateTimeOffset.UtcNow;
        user.PasswordHash = HashPassword(body.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE refresh_tokens SET revoked_at = NOW() WHERE user_id = {0} AND revoked_at IS NULL",
            user.Id);

        AuditLog auditEntry = new()
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            UserId = user.Id,
            Action = "password_reset_completed",
            IpAddress = GetClientIp(req),
            UserAgent = GetUserAgent(req),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        db.AuditLogs.Add(auditEntry);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Password reset completed for user {UserId}", user.Id);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync("{\"message\":\"Password reset successfully. You can now log in.\"}");
        return response;
    }

    [Function("AuthVerifyEmail")]
    public async Task<HttpResponseData> VerifyEmail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/verify-email")] HttpRequestData req,
        CancellationToken ct)
    {
        System.Collections.Specialized.NameValueCollection query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        string? token = query["token"];

        if (string.IsNullOrWhiteSpace(token))
            return await BadRequest(req, "Verification token is required.");

        AppUser? user = await db.Users.FirstOrDefaultAsync(
            u => u.EmailVerificationToken == token, ct);

        if (user is null)
            return await BadRequest(req, "Invalid verification token.");

        if (user.EmailVerificationTokenExpiresAt < DateTimeOffset.UtcNow)
            return await BadRequest(req, "Verification token has expired. Please register again.");

        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        user.EmailVerificationTokenExpiresAt = null;

        if (user.PendingEmail is not null)
        {
            user.Email = user.PendingEmail;
            user.PendingEmail = null;
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;

        await db.Database.ExecuteSqlRawAsync("SET app.current_tenant_id = {0}", user.TenantId.ToString());
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Email verified for user {UserId}", user.Id);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync("{\"message\":\"Email verified successfully. You can now log in.\"}");
        return response;
    }

    [Function("AuthRefresh")]
    public async Task<HttpResponseData> Refresh(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/refresh")] HttpRequestData req,
        CancellationToken ct)
    {
        string? refreshTokenValue = GetCookieValue(req, "refresh_token");

        if (string.IsNullOrEmpty(refreshTokenValue))
            return await Unauthorized(req, "Refresh token is required.");

        ClaimsPrincipal? principal = jwtService.ValidateRefreshToken(refreshTokenValue);
        if (principal is null)
            return await Unauthorized(req, "Invalid or expired refresh token.");

        string? jtiClaim = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti);
        string? subClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");

        if (!Guid.TryParse(jtiClaim, out Guid jti) || !Guid.TryParse(subClaim, out Guid userId))
            return await Unauthorized(req, "Invalid token claims.");

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct);

        int revoked = await db.Database.ExecuteSqlRawAsync(
            "UPDATE refresh_tokens SET revoked_at = NOW() WHERE jti = {0} AND revoked_at IS NULL AND expires_at > NOW()",
            jti);

        if (revoked == 0)
        {
            await tx.RollbackAsync(ct);
            return await Unauthorized(req, "Refresh token has been revoked or expired.");
        }

        AppUser? user = await db.Users.FindAsync([userId], ct);
        if (user is null)
        {
            await tx.RollbackAsync(ct);
            return await Unauthorized(req, "User not found.");
        }

        string newAccessToken = jwtService.GenerateAccessToken(user.Id, user.TenantId, user.Email, user.Role);
        (string newRefreshTokenValue, Guid newJti) = jwtService.GenerateRefreshToken(user.Id, user.TenantId);

        RefreshToken newRefreshToken = new()
        {
            Id = Guid.NewGuid(),
            Jti = newJti,
            UserId = user.Id,
            TenantId = user.TenantId,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.RefreshTokens.Add(newRefreshToken);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        SetAuthCookies(response, newAccessToken, newRefreshTokenValue);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync("{\"message\":\"Token refreshed.\"}");
        return response;
    }

    private static void SetAuthCookies(HttpResponseData response, string accessToken, string refreshToken)
    {
        string accessCookie = $"access_token={accessToken}; HttpOnly; Secure; SameSite=Lax; Path=/; Max-Age=900";
        string refreshCookie = $"refresh_token={refreshToken}; HttpOnly; Secure; SameSite=Lax; Path=/api/auth/refresh; Max-Age=604800";
        response.Headers.Add("Set-Cookie", accessCookie);
        response.Headers.Add("Set-Cookie", refreshCookie);
    }

    private static void ClearAuthCookies(HttpResponseData response)
    {
        response.Headers.Add("Set-Cookie", "access_token=; HttpOnly; Secure; SameSite=Lax; Path=/; Max-Age=0");
        response.Headers.Add("Set-Cookie", "refresh_token=; HttpOnly; Secure; SameSite=Lax; Path=/api/auth/refresh; Max-Age=0");
    }

    private static string? GetCookieValue(HttpRequestData req, string name)
    {
        if (req.Cookies is null) return null;
        foreach (IHttpCookie cookie in req.Cookies)
        {
            if (cookie.Name == name) return cookie.Value;
        }
        return null;
    }

    private static string GetClientIp(HttpRequestData req)
    {
        // X-Azure-ClientIP is set by Azure Front Door / APIM and cannot be spoofed by clients.
        // Prefer this over X-Forwarded-For which is attacker-controlled.
        if (req.Headers.TryGetValues("X-Azure-ClientIP", out IEnumerable<string>? azureIp))
        {
            string? ip = azureIp.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(ip))
                return ip.Trim();
        }

        // Fallback: X-Forwarded-For is not trusted for security decisions because clients
        // can set arbitrary values. Only use it as a last resort for audit logging.
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

    private static async Task WriteAuditLog(AppDbContext db, Guid? tenantId, Guid? userId,
        string action, string? ip, string? userAgent, CancellationToken ct)
    {
        AuditLog entry = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Action = action,
            IpAddress = ip,
            UserAgent = userAgent,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string message)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.BadRequest);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync($"{{\"error\":{EscapeJson(message)}}}");
        return response;
    }

    private static async Task<HttpResponseData> Unauthorized(HttpRequestData req, string message)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.Unauthorized);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync($"{{\"error\":{EscapeJson(message)}}}");
        return response;
    }

    private static async Task<HttpResponseData> ProblemDetails(HttpRequestData req, HttpStatusCode status, string detail)
    {
        HttpResponseData response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/problem+json");
        string statusCode = (int)status + "";
        await response.WriteStringAsync(
            $"{{\"type\":\"about:blank\",\"title\":{EscapeJson(status.ToString())},\"status\":{statusCode},\"detail\":{EscapeJson(detail)}}}");
        return response;
    }

    private static string EscapeJson(string value)
        => JsonSerializer.Serialize(value);

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

    // A pre-computed PBKDF2 hash used when the user does not exist, so that the
    // password verification step always runs and response time is constant regardless
    // of whether the email is registered (prevents timing-based user enumeration).
    private const string DummyPasswordHash =
        "AAAAAAAAAAAAAAAAAAAAAA==:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private static string GenerateSecureToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }

    private static string HashToken(string token)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record RegisterRequest(
        string? Email,
        string? Password,
        string? DisplayName,
        string? OrganisationName);

    private sealed record LoginRequest(string? Email, string? Password);

    private sealed record ForgotPasswordRequest(string? Email);

    private sealed record ConfirmResetPasswordRequest(string? Token, string? NewPassword);
}
