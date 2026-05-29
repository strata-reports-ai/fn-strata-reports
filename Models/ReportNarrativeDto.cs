using System.Text.Json.Serialization;

namespace StrataReports.Functions.Models;

public sealed record ReportNarrativeDto(
    [property: JsonPropertyName("executive_summary")] string ExecutiveSummary,
    [property: JsonPropertyName("revenue_narrative")] string RevenueNarrative,
    [property: JsonPropertyName("guest_feedback_narrative")] string GuestFeedbackNarrative,
    [property: JsonPropertyName("operational_narrative")] string OperationalNarrative,
    [property: JsonPropertyName("issues_and_resolutions")] string IssuesAndResolutions,
    [property: JsonPropertyName("recommendations")] string Recommendations,
    [property: JsonPropertyName("manager_note")] string ManagerNote);

public sealed record NarrativeReportContextDto(
    Guid ReportId,
    Guid TenantId,
    Guid PropertyId,
    string PropertyName,
    string? OwnerName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal TotalGrossRevenue,
    decimal TotalNetRevenue,
    int TotalBookings,
    int TotalNights,
    decimal AverageNightlyRate,
    decimal OccupancyRate,
    IReadOnlyList<NarrativeRevenueByPlatformDto> RevenueByPlatform,
    decimal TotalExpenses,
    IReadOnlyList<NarrativeExpenseByCategoryDto> ExpensesByCategory,
    decimal AverageGuestRating,
    int TotalReviews,
    IReadOnlyList<NarrativeReviewSummaryDto> ReviewSamples,
    int TasksScheduled,
    int TasksCompleted,
    IReadOnlyList<NarrativeInspectionSummaryDto> Inspections);

public sealed record NarrativeRevenueByPlatformDto(
    string Platform,
    decimal GrossRevenue,
    int Bookings);

public sealed record NarrativeExpenseByCategoryDto(
    string Category,
    decimal Amount);

public sealed record NarrativeReviewSummaryDto(
    string Platform,
    DateOnly ReviewDate,
    decimal Rating,
    string? ReviewText);

public sealed record NarrativeInspectionSummaryDto(
    DateOnly InspectionDate,
    decimal? Score,
    int? IssuesFound,
    int? IssuesResolved,
    string? Notes);
