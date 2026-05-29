using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

        HttpResponseData? validationError = await ValidatePropertyRequest(req, body);
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
        await response.WriteStringAsync(JsonSerializer.Serialize(ToDto(property), JsonOptions));
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
        await response.WriteStringAsync(JsonSerializer.Serialize(ToDto(property), JsonOptions));
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

        HttpResponseData? validationError = await ValidatePropertyRequest(req, body);
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
        await response.WriteStringAsync(JsonSerializer.Serialize(ToDto(property), JsonOptions));
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

    private async Task<HttpResponseData?> ValidatePropertyRequest(HttpRequestData req, PropertyRequest? body)
    {
        if (body is null)
            return await ValidationProblemDetails(req, new List<ValidationError> { new("name", "Request body is required.") });

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
            return await ValidationProblemDetails(req, errors);

        return null;
    }

    private static async Task<HttpResponseData> ValidationProblemDetails(HttpRequestData req, List<ValidationError> errors)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.BadRequest);
        response.Headers.Add("Content-Type", "application/problem+json");
        Dictionary<string, string[]> errorDict = errors.ToDictionary(e => e.Field, e => new[] { e.Message });
        object payload = new
        {
            type = "about:blank",
            title = "Validation failed",
            status = 400,
            detail = "One or more validation errors occurred.",
            errors = errorDict,
        };
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }

    private static async Task<HttpResponseData> ProblemDetails(HttpRequestData req, HttpStatusCode status, string detail)
    {
        HttpResponseData response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/problem+json");
        object payload = new
        {
            type = "about:blank",
            title = status.ToString(),
            status = (int)status,
            detail,
        };
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }

    private static async Task<HttpResponseData> Unauthorized(HttpRequestData req, string message)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.Unauthorized);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, JsonOptions));
        return response;
    }

    private static PropertyDto ToDto(Property p) => new(
        p.Id,
        p.TenantId,
        p.Name,
        p.AddressLine1,
        p.City,
        p.State,
        p.PostalCode,
        p.CountryCode,
        p.Units,
        p.OwnerName,
        p.OwnerEmail,
        p.ManagementStartDate,
        p.Timezone,
        p.CurrencyCode,
        p.Notes,
        p.CreatedAt,
        p.UpdatedAt);

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

    private sealed record PropertyDto(
        Guid Id,
        Guid TenantId,
        string Name,
        string? AddressLine1,
        string? City,
        string? State,
        string? PostalCode,
        string CountryCode,
        int Units,
        string? OwnerName,
        string? OwnerEmail,
        DateOnly? ManagementStartDate,
        string Timezone,
        string CurrencyCode,
        string? Notes,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record ValidationError(string Field, string Message);
}
