using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Functions;

public class TenantFunction(
    ILogger<TenantFunction> logger,
    AppDbContext db)
{
    private static readonly HashSet<string> AllowedCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "USD", "CAD", "GBP", "EUR", "AUD",
    };

    private static readonly HashSet<string> IanaTimezones = BuildIanaTimezones();

    [Function("TenantGet")]
    public async Task<HttpResponseData> GetTenant(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tenant")] HttpRequestData req,
        FunctionContext context,
        CancellationToken ct)
    {
        if (!TryGetTenantId(context, out Guid tenantId))
            return await Unauthorized(req, "Authentication required.");

        Tenant? tenant = await db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null)
            return await NotFound(req, "Tenant not found.");

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            $"{{\"id\":\"{tenant.Id}\",\"name\":{EscapeJson(tenant.Name)},\"defaultTimezone\":{JsonStringOrNull(tenant.DefaultTimezone)},\"defaultCurrency\":{JsonStringOrNull(tenant.DefaultCurrency)},\"plan\":{EscapeJson(tenant.Plan)},\"status\":{EscapeJson(tenant.Status)}}}");
        return response;
    }

    [Function("TenantPatch")]
    public async Task<HttpResponseData> PatchTenant(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "tenant")] HttpRequestData req,
        FunctionContext context,
        CancellationToken ct)
    {
        if (!TryGetTenantId(context, out Guid tenantId))
            return await Unauthorized(req, "Authentication required.");

        if (!TryGetRole(context, out string role) || role != "owner")
            return await Forbidden(req, "Only tenant owners can update tenant settings.");

        PatchTenantRequest? body = await req.ReadFromJsonAsync<PatchTenantRequest>(ct);
        if (body is null)
            return await BadRequest(req, "Request body is required.");

        Tenant? tenant = await db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null)
            return await NotFound(req, "Tenant not found.");

        bool changed = false;

        if (body.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return await BadRequest(req, "Tenant name cannot be empty.");
            tenant.Name = body.Name;
            changed = true;
        }

        if (body.DefaultTimezone is not null)
        {
            if (!IanaTimezones.Contains(body.DefaultTimezone))
                return await BadRequest(req, $"Invalid IANA timezone: '{body.DefaultTimezone}'.");
            tenant.DefaultTimezone = body.DefaultTimezone;
            changed = true;
        }

        if (body.DefaultCurrency is not null)
        {
            if (!AllowedCurrencies.Contains(body.DefaultCurrency))
                return await BadRequest(req, $"Unsupported currency '{body.DefaultCurrency}'. Supported: USD, CAD, GBP, EUR, AUD.");
            tenant.DefaultCurrency = body.DefaultCurrency.ToUpperInvariant();
            changed = true;
        }

        if (!changed)
            return await BadRequest(req, "No changes provided.");

        tenant.UpdatedAt = DateTimeOffset.UtcNow;

        if (!TryGetUserId(context, out Guid userId))
            userId = Guid.Empty;

        AuditLog auditEntry = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId == Guid.Empty ? null : userId,
            Action = "tenant.update",
            EntityType = "Tenant",
            EntityId = tenantId,
            IpAddress = GetClientIp(req),
            UserAgent = GetUserAgent(req),
            OccurredAt = DateTimeOffset.UtcNow,
        };
        db.AuditLogs.Add(auditEntry);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Tenant settings updated for tenant {TenantId}", tenantId);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync("{\"message\":\"Tenant settings updated successfully.\"}");
        return response;
    }

    private static bool TryGetTenantId(FunctionContext context, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        if (!context.Items.TryGetValue("TenantId", out object? obj))
            return false;
        if (obj is Guid g)
        {
            tenantId = g;
            return true;
        }
        return Guid.TryParse(obj?.ToString(), out tenantId);
    }

    private static bool TryGetUserId(FunctionContext context, out Guid userId)
    {
        userId = Guid.Empty;
        if (!context.Items.TryGetValue("UserId", out object? obj))
            return false;
        return Guid.TryParse(obj?.ToString(), out userId);
    }

    private static bool TryGetRole(FunctionContext context, out string role)
    {
        role = string.Empty;
        if (!context.Items.TryGetValue("ClaimsPrincipal", out object? obj))
            return false;
        System.Security.Claims.ClaimsPrincipal? principal = obj as System.Security.Claims.ClaimsPrincipal;
        string? r = principal?.FindFirst("role")?.Value;
        if (r is null) return false;
        role = r;
        return true;
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

    private static async Task<HttpResponseData> Forbidden(HttpRequestData req, string message)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.Forbidden);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync($"{{\"error\":{EscapeJson(message)}}}");
        return response;
    }

    private static async Task<HttpResponseData> NotFound(HttpRequestData req, string message)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.NotFound);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync($"{{\"error\":{EscapeJson(message)}}}");
        return response;
    }

    private static string EscapeJson(string value)
        => JsonSerializer.Serialize(value);

    private static string JsonStringOrNull(string? value)
        => value is null ? "null" : EscapeJson(value);

    private static HashSet<string> BuildIanaTimezones()
    {
        HashSet<string> zones = new(StringComparer.Ordinal);
        foreach (TimeZoneInfo tz in TimeZoneInfo.GetSystemTimeZones())
            zones.Add(tz.Id);

        zones.Add("America/New_York");
        zones.Add("America/Chicago");
        zones.Add("America/Denver");
        zones.Add("America/Los_Angeles");
        zones.Add("America/Toronto");
        zones.Add("America/Vancouver");
        zones.Add("Europe/London");
        zones.Add("Europe/Paris");
        zones.Add("Europe/Berlin");
        zones.Add("Australia/Sydney");
        zones.Add("Australia/Melbourne");
        zones.Add("Australia/Brisbane");
        zones.Add("Pacific/Auckland");
        zones.Add("UTC");

        return zones;
    }

    private sealed record PatchTenantRequest(
        string? Name,
        string? DefaultTimezone,
        string? DefaultCurrency);
}
