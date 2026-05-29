using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Functions;

public class PropertiesFunction(
    ILogger<PropertiesFunction> logger,
    AppDbContext db)
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    [Function("PropertiesCreate")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "properties")] HttpRequestData req,
        FunctionContext context,
        CancellationToken ct)
    {
        if (!TryGetTenantId(context, out Guid tenantId))
            return await Unauthorized(req, "Authentication required.");

        if (!TryGetUserId(context, out Guid userId))
            return await Unauthorized(req, "Authentication required.");

        PropertyRequest? body = await req.ReadFromJsonAsync<PropertyRequest>(ct);

        HttpResponseData? validationError = ValidatePropertyRequest(req, body);
        if (validationError is not null)
            return validationError;

        Property property = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = body!.Name!,
            AddressLine1 = body.AddressLine1,
            City = body.City,
            State = body.State,
            PostalCode = body.PostalCode,
            CountryCode = body.CountryCode ?? "US",
            Units = body.Units ?? 1,
            OwnerName = body.OwnerName,
            OwnerEmail = body.OwnerEmail,
            ManagementStartDate = body.ManagementStartDate,
            Timezone = body.Timezone ?? "America/New_York",
            CurrencyCode = body.CurrencyCode ?? "USD",
            Notes = body.Notes,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        db.Properties.Add(property);

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Action = "property.create",
            EntityType = "Property",
            EntityId = property.Id,
            IpAddress = GetClientIp(req),
            UserAgent = GetUserAgent(req),
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Property {PropertyId} created for tenant {TenantId}", property.Id, tenantId);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(SerializeProperty(property));
        return response;
    }

    [Function("PropertiesGetById")]
    public async Task<HttpResponseData> GetById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "properties/{id}")] HttpRequestData req,
        FunctionContext context,
        string id,
        CancellationToken ct)
    {
        if (!TryGetTenantId(context, out Guid tenantId))
            return await Unauthorized(req, "Authentication required.");

        if (!Guid.TryParse(id, out Guid propertyId))
            return await ProblemDetails(req, HttpStatusCode.BadRequest, "Invalid property ID format.");

        Property? property = await db.Properties
            .FirstOrDefaultAsync(p => p.Id == propertyId && p.TenantId == tenantId && p.DeletedAt == null, ct);

        if (property is null)
            return await ProblemDetails(req, HttpStatusCode.NotFound, "Property not found.");

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(SerializeProperty(property));
        return response;
    }

    [Function("PropertiesUpdate")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "properties/{id}")] HttpRequestData req,
        FunctionContext context,
        string id,
        CancellationToken ct)
    {
        if (!TryGetTenantId(context, out Guid tenantId))
            return await Unauthorized(req, "Authentication required.");

        if (!TryGetUserId(context, out Guid userId))
            return await Unauthorized(req, "Authentication required.");

        if (!Guid.TryParse(id, out Guid propertyId))
            return await ProblemDetails(req, HttpStatusCode.BadRequest, "Invalid property ID format.");

        PropertyRequest? body = await req.ReadFromJsonAsync<PropertyRequest>(ct);

        HttpResponseData? validationError = ValidatePropertyRequest(req, body);
        if (validationError is not null)
            return validationError;

        Property? property = await db.Properties
            .FirstOrDefaultAsync(p => p.Id == propertyId && p.TenantId == tenantId && p.DeletedAt == null, ct);

        if (property is null)
            return await ProblemDetails(req, HttpStatusCode.NotFound, "Property not found.");

        property.Name = body!.Name!;
        property.AddressLine1 = body.AddressLine1;
        property.City = body.City;
        property.State = body.State;
        property.PostalCode = body.PostalCode;
        property.CountryCode = body.CountryCode ?? property.CountryCode;
        property.Units = body.Units ?? property.Units;
        property.OwnerName = body.OwnerName;
        property.OwnerEmail = body.OwnerEmail;
        property.ManagementStartDate = body.ManagementStartDate;
        property.Timezone = body.Timezone ?? property.Timezone;
        property.CurrencyCode = body.CurrencyCode ?? property.CurrencyCode;
        property.Notes = body.Notes;
        property.UpdatedAt = DateTimeOffset.UtcNow;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Action = "property.update",
            EntityType = "Property",
            EntityId = property.Id,
            IpAddress = GetClientIp(req),
            UserAgent = GetUserAgent(req),
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Property {PropertyId} updated for tenant {TenantId}", property.Id, tenantId);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(SerializeProperty(property));
        return response;
    }

    [Function("PropertiesDelete")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "properties/{id}")] HttpRequestData req,
        FunctionContext context,
        string id,
        CancellationToken ct)
    {
        if (!TryGetTenantId(context, out Guid tenantId))
            return await Unauthorized(req, "Authentication required.");

        if (!TryGetUserId(context, out Guid userId))
            return await Unauthorized(req, "Authentication required.");

        if (!Guid.TryParse(id, out Guid propertyId))
            return await ProblemDetails(req, HttpStatusCode.BadRequest, "Invalid property ID format.");

        Property? property = await db.Properties
            .FirstOrDefaultAsync(p => p.Id == propertyId && p.TenantId == tenantId && p.DeletedAt == null, ct);

        if (property is null)
            return await ProblemDetails(req, HttpStatusCode.NotFound, "Property not found.");

        property.DeletedAt = DateTimeOffset.UtcNow;
        property.UpdatedAt = DateTimeOffset.UtcNow;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Action = "property.delete",
            EntityType = "Property",
            EntityId = property.Id,
            IpAddress = GetClientIp(req),
            UserAgent = GetUserAgent(req),
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Property {PropertyId} soft-deleted for tenant {TenantId}", property.Id, tenantId);

        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    private HttpResponseData? ValidatePropertyRequest(HttpRequestData req, PropertyRequest? body)
    {
        if (body is null)
            return ProblemDetailsSync(req, HttpStatusCode.BadRequest, "Request body is required.", "name", "Request body is required.");

        List<ValidationError> errors = new();

        if (string.IsNullOrWhiteSpace(body.Name))
            errors.Add(new ValidationError("name", "Name is required."));

        if (body.OwnerEmail is not null && !EmailRegex.IsMatch(body.OwnerEmail))
            errors.Add(new ValidationError("ownerEmail", "Owner email must be a valid email address."));

        if (body.Units is not null && body.Units < 1)
            errors.Add(new ValidationError("units", "Units must be at least 1."));

        if (body.ManagementStartDate is not null && body.ManagementStartDate > DateOnly.FromDateTime(DateTime.UtcNow))
            errors.Add(new ValidationError("managementStartDate", "Management start date must not be in the future."));

        if (errors.Count > 0)
            return ValidationProblemDetailsSync(req, errors);

        return null;
    }

    private static HttpResponseData ProblemDetailsSync(HttpRequestData req, HttpStatusCode status, string detail, string field, string message)
    {
        HttpResponseData response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/problem+json");
        int statusCode = (int)status;
        response.WriteString(
            $"{{\"type\":\"about:blank\",\"title\":\"{EscapeJson(status.ToString())}\",\"status\":{statusCode},\"detail\":\"{EscapeJson(detail)}\"}}");
        return response;
    }

    private static HttpResponseData ValidationProblemDetailsSync(HttpRequestData req, List<ValidationError> errors)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.BadRequest);
        response.Headers.Add("Content-Type", "application/problem+json");
        System.Text.StringBuilder sb = new();
        sb.Append("{\"type\":\"about:blank\",\"title\":\"Validation failed\",\"status\":400,\"detail\":\"One or more validation errors occurred.\",\"errors\":{");
        bool first = true;
        foreach (ValidationError error in errors)
        {
            if (!first) sb.Append(',');
            sb.Append($"\"{EscapeJson(error.Field)}\":[\"{EscapeJson(error.Message)}\"]");
            first = false;
        }
        sb.Append("}}");
        response.WriteString(sb.ToString());
        return response;
    }

    private static async Task<HttpResponseData> ProblemDetails(HttpRequestData req, HttpStatusCode status, string detail)
    {
        HttpResponseData response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/problem+json");
        int statusCode = (int)status;
        await response.WriteStringAsync(
            $"{{\"type\":\"about:blank\",\"title\":\"{EscapeJson(status.ToString())}\",\"status\":{statusCode},\"detail\":\"{EscapeJson(detail)}\"}}");
        return response;
    }

    private static async Task<HttpResponseData> Unauthorized(HttpRequestData req, string message)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.Unauthorized);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync($"{{\"error\":\"{EscapeJson(message)}\"}}");
        return response;
    }

    private static string SerializeProperty(Property p)
    {
        System.Text.StringBuilder sb = new();
        sb.Append('{');
        sb.Append($"\"id\":\"{p.Id}\",");
        sb.Append($"\"tenantId\":\"{p.TenantId}\",");
        sb.Append($"\"name\":\"{EscapeJson(p.Name)}\",");
        sb.Append($"\"addressLine1\":{JsonStringOrNull(p.AddressLine1)},");
        sb.Append($"\"city\":{JsonStringOrNull(p.City)},");
        sb.Append($"\"state\":{JsonStringOrNull(p.State)},");
        sb.Append($"\"postalCode\":{JsonStringOrNull(p.PostalCode)},");
        sb.Append($"\"countryCode\":\"{EscapeJson(p.CountryCode)}\",");
        sb.Append($"\"units\":{p.Units},");
        sb.Append($"\"ownerName\":{JsonStringOrNull(p.OwnerName)},");
        sb.Append($"\"ownerEmail\":{JsonStringOrNull(p.OwnerEmail)},");
        sb.Append($"\"managementStartDate\":{JsonDateOrNull(p.ManagementStartDate)},");
        sb.Append($"\"timezone\":\"{EscapeJson(p.Timezone)}\",");
        sb.Append($"\"currencyCode\":\"{EscapeJson(p.CurrencyCode)}\",");
        sb.Append($"\"notes\":{JsonStringOrNull(p.Notes)},");
        sb.Append($"\"createdAt\":\"{p.CreatedAt:O}\",");
        sb.Append($"\"updatedAt\":\"{p.UpdatedAt:O}\"");
        sb.Append('}');
        return sb.ToString();
    }

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string JsonStringOrNull(string? value)
        => value is null ? "null" : $"\"{EscapeJson(value)}\"";

    private static string JsonDateOrNull(DateOnly? value)
        => value is null ? "null" : $"\"{value:yyyy-MM-dd}\"";

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

    private sealed record PropertyRequest(
        string? Name,
        string? AddressLine1,
        string? City,
        string? State,
        string? PostalCode,
        string? CountryCode,
        int? Units,
        string? OwnerName,
        string? OwnerEmail,
        DateOnly? ManagementStartDate,
        string? Timezone,
        string? CurrencyCode,
        string? Notes);

    private sealed record ValidationError(string Field, string Message);
}
