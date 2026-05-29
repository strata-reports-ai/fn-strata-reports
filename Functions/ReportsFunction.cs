using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;
using StrataReports.Functions.Services;

namespace StrataReports.Functions.Functions;

public class ReportsFunction(
    ILogger<ReportsFunction> logger,
    AppDbContext db,
    INarrativeGeneratorService narrativeGenerator,
    IPdfRenderService pdfRenderService,
    IReportContextBuilder reportContextBuilder,
    IQueueService queueService,
    IPlanEnforcementService planEnforcement)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Function("ReportsGenerate")]
    public async Task<HttpResponseData> Generate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "reports/generate")] HttpRequestData req,
        FunctionContext context,
        CancellationToken ct)
    {
        if (!TryGetTenantId(context, out Guid tenantId))
            return await Unauthorized(req, "Authentication required.");

        if (!TryGetUserId(context, out Guid userId))
            return await Unauthorized(req, "Authentication required.");

        EnforcementOutcome enforcement = await planEnforcement.CheckReportGenerateAsync(tenantId, ct);
        if (enforcement.Result != EnforcementResult.Allowed)
            return await PaymentRequired(req, enforcement.Type!, enforcement.Detail!, enforcement.PlanLimit);

        GenerateReportRequest? body = await req.ReadFromJsonAsync<GenerateReportRequest>(ct);
        if (body is null)
            return await BadRequest(req, "Request body is required.");

        if (!Guid.TryParse(body.PropertyId, out Guid propertyId))
            return await BadRequest(req, "propertyId must be a valid UUID.");

        if (!DateOnly.TryParse(body.PeriodStart, out DateOnly periodStart))
            return await BadRequest(req, "periodStart must be a valid date (yyyy-MM-dd).");

        if (!DateOnly.TryParse(body.PeriodEnd, out DateOnly periodEnd))
            return await BadRequest(req, "periodEnd must be a valid date (yyyy-MM-dd).");

        if (periodEnd < periodStart)
            return await BadRequest(req, "periodEnd must be on or after periodStart.");

        bool propertyExists = await db.Properties
            .AnyAsync(p => p.Id == propertyId && p.TenantId == tenantId && p.DeletedAt == null, ct);
        if (!propertyExists)
            return await NotFound(req, "Property not found.");

        string reportType = body.ReportType ?? "quarterly";

        bool alreadyExists = await db.Reports
            .AnyAsync(r => r.TenantId == tenantId && r.PropertyId == propertyId
                && r.ReportType == reportType && r.PeriodStart == periodStart && r.PeriodEnd == periodEnd, ct);
        if (alreadyExists)
            return await Conflict(req, "A report for this property and period already exists.");

        Report report = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PropertyId = propertyId,
            ReportType = reportType,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Status = "queued",
            GeneratedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);

        string message = JsonSerializer.Serialize(new ReportGenerateMessage(report.Id, tenantId, propertyId));
        await queueService.EnqueueAsync("report-generate", message, ct);

        logger.LogInformation(
            "Report {ReportId} queued for tenant {TenantId} property {PropertyId}",
            report.Id, tenantId, propertyId);

        HttpResponseData response = req.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            JsonSerializer.Serialize(new { reportId = report.Id, status = report.Status }, JsonOptions));
        return response;
    }

    [Function("ReportsGenerateNarrative")]
    public async Task Run(
        [QueueTrigger("report-generate", Connection = "AzureWebJobsStorage")] string messageJson,
        CancellationToken ct)
    {
        long startTs = Stopwatch.GetTimestamp();

        ReportGenerateMessage? message = JsonSerializer.Deserialize<ReportGenerateMessage>(
            messageJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (message is null)
        {
            logger.LogError("Failed to deserialize report-generate queue message: {Raw}", messageJson);
            return;
        }

        logger.LogInformation(
            "Generating narrative for report {ReportId} tenant={TenantId} property={PropertyId}",
            message.ReportId, message.TenantId, message.PropertyId);

        await db.Database.ExecuteSqlRawAsync(
            "SET app.current_tenant_id = {0}", message.TenantId.ToString(), ct);

        Report? report = await db.Reports.FindAsync([message.ReportId], ct);
        if (report is null)
        {
            logger.LogError("Report {ReportId} not found", message.ReportId);
            return;
        }

        report.Status = "processing";
        report.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        NarrativeReportContextDto? context;
        try
        {
            context = await BuildReportContextAsync(report, ct);
        }
        catch (InvalidOperationException ex)
        {
            await FailReportAsync(report, $"Failed to build report context: {ex.Message}", ct);
            return;
        }

        NarrativeGenerationResult result = await narrativeGenerator.GenerateAsync(context, ct);

        int latencyMs = (int)Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;

        report.AiModel = result.ModelUsed;
        report.AiInputTokens = result.InputTokens > 0 ? result.InputTokens : null;
        report.AiOutputTokens = result.OutputTokens > 0 ? result.OutputTokens : null;
        report.AiCostUsd = result.CostUsd > 0 ? result.CostUsd : null;
        report.GenerationMs = latencyMs;
        report.UpdatedAt = DateTimeOffset.UtcNow;

        if (!result.Success)
        {
            report.Status = "failed";
            report.ErrorMessage = result.ErrorMessage;
            await db.SaveChangesAsync(ct);

            logger.LogError(
                "report.narrative_failed reportId={ReportId} model={Model} error={Error}",
                report.Id, result.ModelUsed, result.ErrorMessage);
            return;
        }

        report.JsonPayload = JsonSerializer.Serialize(result.Narrative, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
        report.ErrorMessage = null;
        report.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "report.narrative_generated reportId={ReportId} model={Model} inputTokens={InputTokens} " +
            "outputTokens={OutputTokens} costUsd={CostUsd} latencyMs={LatencyMs}",
            report.Id, result.ModelUsed, result.InputTokens, result.OutputTokens,
            result.CostUsd, latencyMs);

        ReportContextDto? reportContext;
        try
        {
            reportContext = await reportContextBuilder.BuildReportContextAsync(
                report.PropertyId, report.PeriodStart, report.PeriodEnd, report.TenantId, ct);
        }
        catch (InvalidOperationException ex)
        {
            await FailReportAsync(report, $"Failed to build PDF context: {ex.Message}", ct);
            return;
        }

        try
        {
            PdfRenderResult pdfResult = await pdfRenderService.RenderAndUploadAsync(
                result.Narrative!, reportContext, report.TenantId, report.Id, ct);

            report.PdfBlobPath = pdfResult.BlobPath;
            report.Status = "succeeded";
            report.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "report.pdf_rendered reportId={ReportId} blobPath={BlobPath}",
                report.Id, pdfResult.BlobPath);
        }
        catch (ReportRenderException ex)
        {
            await FailReportAsync(report, $"PDF render failed: {ex.Message}", ct);
        }
    }

    private async Task<NarrativeReportContextDto> BuildReportContextAsync(Report report, CancellationToken ct)
    {
        Property? property = await db.Properties
            .FirstOrDefaultAsync(p => p.Id == report.PropertyId && p.TenantId == report.TenantId, ct);

        if (property is null)
            throw new InvalidOperationException($"Property {report.PropertyId} not found for tenant {report.TenantId}");

        DateOnly start = report.PeriodStart;
        DateOnly end = report.PeriodEnd;

        List<RevenueRecord> revenues = await db.RevenueRecords
            .Where(r => r.TenantId == report.TenantId
                && r.PropertyId == report.PropertyId
                && r.CheckinDate >= start
                && r.CheckinDate <= end)
            .ToListAsync(ct);

        List<ExpenseRecord> expenses = await db.ExpenseRecords
            .Where(e => e.TenantId == report.TenantId
                && e.PropertyId == report.PropertyId
                && e.ExpenseDate >= start
                && e.ExpenseDate <= end)
            .ToListAsync(ct);

        List<ReviewRecord> reviews = await db.ReviewRecords
            .Where(r => r.TenantId == report.TenantId
                && r.PropertyId == report.PropertyId
                && r.ReviewDate >= start
                && r.ReviewDate <= end)
            .OrderByDescending(r => r.ReviewDate)
            .ToListAsync(ct);

        List<TaskRecord> tasks = await db.TaskRecords
            .Where(t => t.TenantId == report.TenantId
                && t.PropertyId == report.PropertyId
                && t.ScheduledAt >= start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                && t.ScheduledAt <= end.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc))
            .ToListAsync(ct);

        List<InspectionRecord> inspections = await db.InspectionRecords
            .Where(i => i.TenantId == report.TenantId
                && i.PropertyId == report.PropertyId
                && i.InspectionDate >= start
                && i.InspectionDate <= end)
            .OrderBy(i => i.InspectionDate)
            .ToListAsync(ct);

        decimal totalGross = revenues.Sum(r => r.GrossRevenue);
        decimal totalNet = revenues.Sum(r => r.NetRevenue);
        int totalNights = revenues.Sum(r => r.Nights);
        int totalBookings = revenues.Count;
        decimal avgNightly = totalBookings > 0 && totalNights > 0
            ? totalGross / totalNights
            : 0m;

        int periodDays = end.DayNumber - start.DayNumber + 1;
        decimal occupancyRate = periodDays > 0
            ? Math.Round((decimal)totalNights / periodDays * 100m, 1)
            : 0m;

        IReadOnlyList<NarrativeRevenueByPlatformDto> revenueByPlatform = revenues
            .GroupBy(r => r.Platform)
            .Select(g => new NarrativeRevenueByPlatformDto(g.Key, g.Sum(r => r.GrossRevenue), g.Count()))
            .OrderByDescending(p => p.GrossRevenue)
            .ToList();

        decimal totalExpenses = expenses.Sum(e => e.Amount);
        IReadOnlyList<NarrativeExpenseByCategoryDto> expensesByCategory = expenses
            .GroupBy(e => e.Category)
            .Select(g => new NarrativeExpenseByCategoryDto(g.Key, g.Sum(e => e.Amount)))
            .OrderByDescending(e => e.Amount)
            .ToList();

        decimal avgRating = reviews.Count > 0 ? reviews.Average(r => r.Rating) : 0m;
        avgRating = Math.Round(avgRating, 2);

        IReadOnlyList<NarrativeReviewSummaryDto> reviewSamples = reviews
            .Select(r => new NarrativeReviewSummaryDto(r.Platform, r.ReviewDate, r.Rating, r.ReviewText))
            .ToList();

        IReadOnlyList<NarrativeInspectionSummaryDto> inspectionSummaries = inspections
            .Select(i => new NarrativeInspectionSummaryDto(
                i.InspectionDate, i.Score, i.IssuesFound, i.IssuesResolved, i.Notes))
            .ToList();

        return new NarrativeReportContextDto(
            ReportId: report.Id,
            TenantId: report.TenantId,
            PropertyId: report.PropertyId,
            PropertyName: property.Name,
            OwnerName: property.OwnerName,
            PeriodStart: start,
            PeriodEnd: end,
            TotalGrossRevenue: totalGross,
            TotalNetRevenue: totalNet,
            TotalBookings: totalBookings,
            TotalNights: totalNights,
            AverageNightlyRate: avgNightly,
            OccupancyRate: occupancyRate,
            RevenueByPlatform: revenueByPlatform,
            TotalExpenses: totalExpenses,
            ExpensesByCategory: expensesByCategory,
            AverageGuestRating: avgRating,
            TotalReviews: reviews.Count,
            ReviewSamples: reviewSamples,
            TasksScheduled: tasks.Count,
            TasksCompleted: tasks.Count(t => t.Status == "completed"),
            Inspections: inspectionSummaries);
    }

    private async Task FailReportAsync(Report report, string error, CancellationToken ct)
    {
        report.Status = "failed";
        report.ErrorMessage = error;
        report.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogError("Report {ReportId} failed: {Error}", report.Id, error);
    }

    private sealed record ReportGenerateMessage(
        Guid ReportId,
        Guid TenantId,
        Guid PropertyId);

    private sealed record GenerateReportRequest(
        string? PropertyId,
        string? PeriodStart,
        string? PeriodEnd,
        string? ReportType);

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

    private static async Task<HttpResponseData> Unauthorized(HttpRequestData req, string message)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.Unauthorized);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, JsonOptions));
        return response;
    }

    private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string detail)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.BadRequest);
        response.Headers.Add("Content-Type", "application/problem+json");
        object payload = new
        {
            type = "about:blank",
            title = "Bad Request",
            status = 400,
            detail,
        };
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }

    private static async Task<HttpResponseData> NotFound(HttpRequestData req, string detail)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.NotFound);
        response.Headers.Add("Content-Type", "application/problem+json");
        object payload = new
        {
            type = "about:blank",
            title = "Not Found",
            status = 404,
            detail,
        };
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }

    private static async Task<HttpResponseData> Conflict(HttpRequestData req, string detail)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.Conflict);
        response.Headers.Add("Content-Type", "application/problem+json");
        object payload = new
        {
            type = "about:blank",
            title = "Conflict",
            status = 409,
            detail,
        };
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }

    private static async Task<HttpResponseData> PaymentRequired(
        HttpRequestData req, string type, string detail, int? planLimit)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.PaymentRequired);
        response.Headers.Add("Content-Type", "application/problem+json");
        object payload = planLimit.HasValue
            ? new { type, detail, planLimit = planLimit.Value }
            : (object)new { type, detail };
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
