using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Functions;

public class ImportsFunction(
    ILogger<ImportsFunction> logger,
    AppDbContext db,
    IBlobService blobService,
    IQueueService queueService)
{
    private static readonly HashSet<string> ValidImportTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "revenue", "expenses", "tasks", "reviews", "inspections",
    };

    private static readonly HashSet<string> ValidExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv",
    };

    private const long MaxFileSizeBytes = 10 * 1024 * 1024;
    private const string CsvProcessQueue = "csv-process";

    [Function("ImportsGetUploadUrl")]
    public async Task<HttpResponseData> GetUploadUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "imports/upload-url")] HttpRequestData req,
        FunctionContext context,
        CancellationToken ct)
    {
        if (!TryGetTenantId(context, out Guid tenantId))
            return await Unauthorized(req, "Authentication required.");

        if (!TryGetUserId(context, out Guid userId))
            return await Unauthorized(req, "Authentication required.");

        UploadUrlRequest? body = await req.ReadFromJsonAsync<UploadUrlRequest>(ct);
        if (body is null)
            return await BadRequest(req, "Request body is required.");

        if (!Guid.TryParse(body.PropertyId, out Guid propertyId))
            return await BadRequest(req, "propertyId must be a valid UUID.");

        if (string.IsNullOrWhiteSpace(body.ImportType) || !ValidImportTypes.Contains(body.ImportType))
            return await BadRequest(req, "importType must be one of: revenue, expenses, tasks, reviews, inspections.");

        if (string.IsNullOrWhiteSpace(body.Filename))
            return await BadRequest(req, "filename is required.");

        string extension = Path.GetExtension(body.Filename);
        if (!ValidExtensions.Contains(extension))
            return await BadRequest(req, "Only .csv or .tsv files are accepted.");

        if (req.Headers.TryGetValues("Content-Length", out IEnumerable<string>? clValues))
        {
            if (long.TryParse(clValues.FirstOrDefault(), out long contentLength) && contentLength > MaxFileSizeBytes)
                return await BadRequest(req, "File exceeds the 10 MB size limit.");
        }

        bool propertyBelongsToTenant = await db.Properties
            .AnyAsync(p => p.Id == propertyId && p.TenantId == tenantId, ct);
        if (!propertyBelongsToTenant)
            return await NotFound(req, "Property not found.");

        string sanitizedFilename = SanitizeFilename(body.Filename);
        Guid importId = Guid.NewGuid();
        string blobPath = $"{tenantId}/{propertyId}/{body.ImportType.ToLowerInvariant()}/{importId}/{sanitizedFilename}";

        Import import = new()
        {
            Id = importId,
            TenantId = tenantId,
            PropertyId = propertyId,
            ImportType = body.ImportType.ToLowerInvariant(),
            SourceFilename = body.Filename,
            BlobPath = blobPath,
            Status = "pending",
            UploadedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        db.Imports.Add(import);
        await db.SaveChangesAsync(ct);

        Uri sasUrl = blobService.GenerateSasUploadUrl(blobPath, TimeSpan.FromMinutes(5));

        logger.LogInformation("Created import {ImportId} for tenant {TenantId}", importId, tenantId);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            $"{{\"importId\":\"{importId}\",\"uploadUrl\":{EscapeJson(sasUrl.ToString())},\"blobPath\":{EscapeJson(blobPath)}}}");
        return response;
    }

    [Function("ImportsConfirm")]
    public async Task<HttpResponseData> Confirm(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "imports/{importId}/confirm")] HttpRequestData req,
        string importId,
        FunctionContext context,
        CancellationToken ct)
    {
        if (!TryGetTenantId(context, out Guid tenantId))
            return await Unauthorized(req, "Authentication required.");

        if (!Guid.TryParse(importId, out Guid importGuid))
            return await BadRequest(req, "importId must be a valid UUID.");

        Import? import = await db.Imports
            .FirstOrDefaultAsync(i => i.Id == importGuid && i.TenantId == tenantId, ct);

        if (import is null)
            return await NotFound(req, "Import not found.");

        if (import.Status != "pending")
            return await BadRequest(req, $"Import is already in status '{import.Status}'.");

        bool exists = await blobService.BlobExistsAsync(import.BlobPath, ct);
        if (!exists)
            return await BadRequest(req, "Uploaded file not found in storage. Please retry the upload.");

        long blobSize = await blobService.GetBlobSizeAsync(import.BlobPath, ct);
        if (blobSize > MaxFileSizeBytes)
            return await BadRequest(req, "File exceeds the 10 MB size limit.");

        import.Status = "processing";
        import.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        string queueMessage = JsonSerializer.Serialize(new
        {
            importId = import.Id,
            tenantId = import.TenantId,
            propertyId = import.PropertyId,
            importType = import.ImportType,
            blobPath = import.BlobPath,
        });

        await queueService.EnqueueAsync(CsvProcessQueue, queueMessage, ct);

        logger.LogInformation("Import {ImportId} confirmed and enqueued for processing", import.Id);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            $"{{\"importId\":\"{import.Id}\",\"status\":\"processing\"}}");
        return response;
    }

    [Function("ImportsGet")]
    public async Task<HttpResponseData> GetImport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "imports/{importId}")] HttpRequestData req,
        string importId,
        FunctionContext context,
        CancellationToken ct)
    {
        if (!TryGetTenantId(context, out Guid tenantId))
            return await Unauthorized(req, "Authentication required.");

        if (!Guid.TryParse(importId, out Guid importGuid))
            return await BadRequest(req, "importId must be a valid UUID.");

        Import? import = await db.Imports
            .FirstOrDefaultAsync(i => i.Id == importGuid && i.TenantId == tenantId, ct);

        if (import is null)
            return await NotFound(req, "Import not found.");

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            $"{{\"importId\":\"{import.Id}\"," +
            $"\"status\":{EscapeJson(import.Status)}," +
            $"\"importType\":{EscapeJson(import.ImportType)}," +
            $"\"sourceFilename\":{EscapeJson(import.SourceFilename)}," +
            $"\"blobPath\":{EscapeJson(import.BlobPath)}," +
            $"\"propertyId\":{JsonGuidOrNull(import.PropertyId)}," +
            $"\"recordsTotal\":{JsonIntOrNull(import.RecordsTotal)}," +
            $"\"recordsImported\":{JsonIntOrNull(import.RecordsImported)}," +
            $"\"recordsSkipped\":{JsonIntOrNull(import.RecordsSkipped)}," +
            $"\"errorSummary\":{JsonStringOrNull(import.ErrorSummary)}," +
            $"\"createdAt\":\"{import.CreatedAt:O}\"," +
            $"\"updatedAt\":\"{import.UpdatedAt:O}\"}}");
        return response;
    }

    private static string SanitizeFilename(string filename)
    {
        string name = Path.GetFileName(filename);
        name = Regex.Replace(name, @"[^\w\.\-]", "_");
        if (name.Length > 200)
            name = name[..200];
        return name;
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

    private static string JsonGuidOrNull(Guid? value)
        => value is null ? "null" : $"\"{value}\"";

    private static string JsonIntOrNull(int? value)
        => value is null ? "null" : value.ToString()!;

    private sealed record UploadUrlRequest(
        string? PropertyId,
        string? ImportType,
        string? Filename);
}
