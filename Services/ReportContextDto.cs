namespace StrataReports.Functions.Services;

public record ReportContextDto(
    PropertyMetadataDto Property,
    RevenueSummaryDto Revenue,
    ExpenseSummaryDto Expense,
    TaskSummaryDto Tasks,
    ReviewSummaryDto Reviews,
    InspectionSummaryDto Inspections,
    IReadOnlyList<FlaggedIssueDto> FlaggedIssues
);

public record PropertyMetadataDto(
    Guid Id,
    string Name,
    string? AddressLine1,
    string? City,
    string? State,
    string? PostalCode,
    string CountryCode,
    int Units,
    string? OwnerName,
    string Timezone,
    string CurrencyCode
);

public record RevenueSummaryDto(
    decimal Total,
    decimal OccupancyRate,
    decimal Adr,
    decimal RevPar,
    IReadOnlyList<MonthlyRevenueDto> ByMonth,
    IReadOnlyList<PlatformRevenueDto> ByPlatform,
    decimal? PriorQuarterTotal,
    decimal? PriorQuarterDelta
);

public record MonthlyRevenueDto(
    int Year,
    int Month,
    decimal Total,
    decimal NetRevenue,
    int Nights
);

public record PlatformRevenueDto(
    string Platform,
    decimal Total,
    decimal NetRevenue,
    int BookingCount,
    int Nights
);

public record ExpenseSummaryDto(
    decimal Total,
    decimal NetToOwner,
    IReadOnlyList<CategoryExpenseDto> ByCategory,
    IReadOnlyList<MonthlyExpenseDto> ByMonth,
    decimal? PriorQuarterTotal,
    decimal? PriorQuarterDelta
);

public record CategoryExpenseDto(
    string Category,
    decimal Total,
    int Count
);

public record MonthlyExpenseDto(
    int Year,
    int Month,
    decimal Total,
    int Count
);

public record TaskSummaryDto(
    int Total,
    decimal CompletionRate,
    decimal OnTimeRate,
    int MissedCount,
    int LateCount
);

public record ReviewSummaryDto(
    decimal AverageRating,
    int Count,
    IReadOnlyDictionary<int, int> Distribution,
    IReadOnlyList<ReviewSnippetDto> RecentReviews,
    decimal? PriorQuarterAverageRating,
    decimal? PriorQuarterDelta
);

public record ReviewSnippetDto(
    string Platform,
    DateOnly ReviewDate,
    decimal Rating,
    string? ReviewText
);

public record InspectionSummaryDto(
    int Count,
    decimal? AverageScore,
    int TotalIssuesFound,
    int TotalIssuesResolved
);

public record FlaggedIssueDto(
    string IssueType,
    string Description,
    Guid? RelatedRecordId
);
