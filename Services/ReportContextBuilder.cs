using System.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Services;

public class InsufficientDataException(string message) : Exception(message);

public interface IReportContextBuilder
{
    Task<ReportContextDto> BuildReportContextAsync(
        Guid propertyId,
        DateOnly periodStart,
        DateOnly periodEnd,
        Guid tenantId,
        CancellationToken ct = default);
}

public class ReportContextBuilder(
    AppDbContext db,
    IDbConnectionFactory connectionFactory,
    ILogger<ReportContextBuilder> logger) : IReportContextBuilder
{
    public async Task<ReportContextDto> BuildReportContextAsync(
        Guid propertyId,
        DateOnly periodStart,
        DateOnly periodEnd,
        Guid tenantId,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Building report context for property {PropertyId}, period {PeriodStart}–{PeriodEnd}",
            propertyId, periodStart, periodEnd);

        Property property = await db.Properties
            .AsNoTracking()
            .Where(p => p.Id == propertyId && p.TenantId == tenantId && p.DeletedAt == null)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Property {propertyId} not found for tenant {tenantId}.");

        using IDbConnection conn = await connectionFactory.OpenAsync(tenantId, ct);

        var priorStart = periodStart.AddDays(-90);
        var priorEnd = periodStart.AddDays(-1);

        var parameters = new
        {
            tenantId,
            propertyId,
            periodStart,
            periodEnd,
            priorStart,
            priorEnd
        };

        var (revenueSummary, revenueExists) = await BuildRevenueSummaryAsync(conn, parameters, periodStart, periodEnd, ct);
        var (expenseSummary, expenseExists) = await BuildExpenseSummaryAsync(conn, parameters, ct);

        if (!revenueExists && !expenseExists)
        {
            throw new InsufficientDataException(
                $"No revenue or expense data found for property {property.Name} " +
                $"in the period {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}. " +
                "Please ensure imports have been processed before generating a report.");
        }

        TaskSummaryDto taskSummary = await BuildTaskSummaryAsync(conn, parameters, ct);
        ReviewSummaryDto reviewSummary = await BuildReviewSummaryAsync(conn, parameters, ct);
        InspectionSummaryDto inspectionSummary = await BuildInspectionSummaryAsync(conn, parameters, ct);
        IReadOnlyList<FlaggedIssueDto> flaggedIssues = await BuildFlaggedIssuesAsync(conn, parameters, ct);

        PropertyMetadataDto propertyMeta = new(
            property.Id,
            property.Name,
            property.AddressLine1,
            property.City,
            property.State,
            property.PostalCode,
            property.CountryCode,
            property.Units,
            property.OwnerName,
            property.Timezone,
            property.CurrencyCode);

        return new ReportContextDto(
            propertyMeta,
            revenueSummary,
            expenseSummary,
            taskSummary,
            reviewSummary,
            inspectionSummary,
            flaggedIssues);
    }

    private static async Task<(RevenueSummaryDto Summary, bool HasData)> BuildRevenueSummaryAsync(
        IDbConnection conn,
        object parameters,
        DateOnly periodStart,
        DateOnly periodEnd,
        CancellationToken ct)
    {
        const string totalsSql = """
            SELECT
                COUNT(*)                                        AS booking_count,
                COALESCE(SUM(gross_revenue), 0)                AS total_gross,
                COALESCE(SUM(net_revenue), 0)                  AS total_net,
                COALESCE(SUM(nights), 0)                       AS total_nights
            FROM revenue_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND checkin_date >= @periodStart
              AND checkin_date <= @periodEnd
            """;

        const string priorTotalSql = """
            SELECT COALESCE(SUM(gross_revenue), 0)
            FROM revenue_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND checkin_date >= @priorStart
              AND checkin_date <= @priorEnd
            """;

        const string byMonthSql = """
            SELECT
                EXTRACT(YEAR  FROM checkin_date)::int          AS year,
                EXTRACT(MONTH FROM checkin_date)::int          AS month,
                COALESCE(SUM(gross_revenue), 0)                AS total,
                COALESCE(SUM(net_revenue), 0)                  AS net_revenue,
                COALESCE(SUM(nights), 0)                       AS nights
            FROM revenue_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND checkin_date >= @periodStart
              AND checkin_date <= @periodEnd
            GROUP BY year, month
            ORDER BY year, month
            """;

        const string byPlatformSql = """
            SELECT
                platform,
                COALESCE(SUM(gross_revenue), 0)                AS total,
                COALESCE(SUM(net_revenue), 0)                  AS net_revenue,
                COUNT(*)                                        AS booking_count,
                COALESCE(SUM(nights), 0)                       AS nights
            FROM revenue_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND checkin_date >= @periodStart
              AND checkin_date <= @periodEnd
            GROUP BY platform
            ORDER BY total DESC
            """;

        dynamic totals = (await conn.QueryAsync(totalsSql, parameters)).First();
        int bookingCount = (int)(long)totals.booking_count;
        decimal totalGross = (decimal)totals.total_gross;
        int totalNights = (int)(long)totals.total_nights;

        bool hasData = bookingCount > 0;

        decimal priorTotal = await conn.ExecuteScalarAsync<decimal>(priorTotalSql, parameters);

        IEnumerable<dynamic> byMonthRows = await conn.QueryAsync(byMonthSql, parameters);
        IEnumerable<dynamic> byPlatformRows = await conn.QueryAsync(byPlatformSql, parameters);

        int periodDays = periodEnd.DayNumber - periodStart.DayNumber + 1;
        decimal occupancyRate = periodDays > 0 && totalNights > 0
            ? Math.Round((decimal)totalNights / periodDays * 100, 2)
            : 0m;
        decimal adr = bookingCount > 0 && totalNights > 0
            ? Math.Round(totalGross / totalNights, 2)
            : 0m;
        decimal revPar = periodDays > 0
            ? Math.Round(totalGross / periodDays, 2)
            : 0m;

        decimal? priorQuarterDelta = priorTotal > 0
            ? Math.Round(totalGross - priorTotal, 2)
            : null;

        List<MonthlyRevenueDto> byMonth = byMonthRows.Select(r => new MonthlyRevenueDto(
            (int)r.year,
            (int)r.month,
            (decimal)r.total,
            (decimal)r.net_revenue,
            (int)(long)r.nights)).ToList();

        List<PlatformRevenueDto> byPlatform = byPlatformRows.Select(r => new PlatformRevenueDto(
            (string)r.platform,
            (decimal)r.total,
            (decimal)r.net_revenue,
            (int)(long)r.booking_count,
            (int)(long)r.nights)).ToList();

        RevenueSummaryDto summary = new(
            totalGross,
            occupancyRate,
            adr,
            revPar,
            byMonth,
            byPlatform,
            priorTotal > 0 ? priorTotal : null,
            priorQuarterDelta);

        return (summary, hasData);
    }

    private static async Task<(ExpenseSummaryDto Summary, bool HasData)> BuildExpenseSummaryAsync(
        IDbConnection conn,
        object parameters,
        CancellationToken ct)
    {
        const string totalsSql = """
            SELECT
                COUNT(*)                            AS expense_count,
                COALESCE(SUM(amount), 0)            AS total
            FROM expense_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND expense_date >= @periodStart
              AND expense_date <= @periodEnd
            """;

        const string priorTotalSql = """
            SELECT COALESCE(SUM(amount), 0)
            FROM expense_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND expense_date >= @priorStart
              AND expense_date <= @priorEnd
            """;

        const string byCategorySql = """
            SELECT
                category,
                COALESCE(SUM(amount), 0)            AS total,
                COUNT(*)                            AS count
            FROM expense_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND expense_date >= @periodStart
              AND expense_date <= @periodEnd
            GROUP BY category
            ORDER BY total DESC
            """;

        const string byMonthSql = """
            SELECT
                EXTRACT(YEAR  FROM expense_date)::int   AS year,
                EXTRACT(MONTH FROM expense_date)::int   AS month,
                COALESCE(SUM(amount), 0)                AS total,
                COUNT(*)                                AS count
            FROM expense_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND expense_date >= @periodStart
              AND expense_date <= @periodEnd
            GROUP BY year, month
            ORDER BY year, month
            """;

        dynamic totals = (await conn.QueryAsync(totalsSql, parameters)).First();
        int expenseCount = (int)(long)totals.expense_count;
        decimal total = (decimal)totals.total;

        bool hasData = expenseCount > 0;

        decimal priorTotal = await conn.ExecuteScalarAsync<decimal>(priorTotalSql, parameters);

        IEnumerable<dynamic> byCategoryRows = await conn.QueryAsync(byCategorySql, parameters);
        IEnumerable<dynamic> byMonthRows = await conn.QueryAsync(byMonthSql, parameters);

        decimal? priorQuarterDelta = priorTotal > 0
            ? Math.Round(total - priorTotal, 2)
            : null;

        List<CategoryExpenseDto> byCategory = byCategoryRows.Select(r => new CategoryExpenseDto(
            (string)r.category,
            (decimal)r.total,
            (int)(long)r.count)).ToList();

        List<MonthlyExpenseDto> byMonth = byMonthRows.Select(r => new MonthlyExpenseDto(
            (int)r.year,
            (int)r.month,
            (decimal)r.total,
            (int)(long)r.count)).ToList();

        ExpenseSummaryDto summary = new(
            total,
            -total,
            byCategory,
            byMonth,
            priorTotal > 0 ? priorTotal : null,
            priorQuarterDelta);

        return (summary, hasData);
    }

    private static async Task<TaskSummaryDto> BuildTaskSummaryAsync(
        IDbConnection conn,
        object parameters,
        CancellationToken ct)
    {
        const string sql = """
            SELECT
                COUNT(*)                                                                                        AS total,
                COUNT(*) FILTER (WHERE status = 'completed')                                                   AS completed_count,
                COUNT(*) FILTER (WHERE status IN ('missed', 'cancelled') OR
                                       (scheduled_at IS NOT NULL AND completed_at IS NULL
                                        AND scheduled_at < NOW()))                                             AS missed_count,
                COUNT(*) FILTER (WHERE completed_at IS NOT NULL AND scheduled_at IS NOT NULL
                                   AND completed_at > scheduled_at)                                            AS late_count,
                COUNT(*) FILTER (WHERE completed_at IS NOT NULL AND scheduled_at IS NOT NULL
                                   AND completed_at <= scheduled_at)                                           AS on_time_count
            FROM task_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND scheduled_at >= @periodStart::timestamp
              AND scheduled_at <= @periodEnd::timestamp + interval '1 day' - interval '1 second'
            """;

        dynamic row = (await conn.QueryAsync(sql, parameters)).First();
        long total = (long)row.total;
        long completedCount = (long)row.completed_count;
        long missedCount = (long)row.missed_count;
        long lateCount = (long)row.late_count;
        long onTimeCount = (long)row.on_time_count;

        decimal completionRate = total > 0
            ? Math.Round((decimal)completedCount / total * 100, 2)
            : 0m;
        decimal onTimeRate = completedCount > 0
            ? Math.Round((decimal)onTimeCount / completedCount * 100, 2)
            : 0m;

        return new TaskSummaryDto(
            (int)total,
            completionRate,
            onTimeRate,
            (int)missedCount,
            (int)lateCount);
    }

    private static async Task<ReviewSummaryDto> BuildReviewSummaryAsync(
        IDbConnection conn,
        object parameters,
        CancellationToken ct)
    {
        const string totalsSql = """
            SELECT
                COUNT(*)                                    AS count,
                COALESCE(AVG(rating), 0)                   AS avg_rating,
                COUNT(*) FILTER (WHERE rating >= 1 AND rating < 2)  AS star1,
                COUNT(*) FILTER (WHERE rating >= 2 AND rating < 3)  AS star2,
                COUNT(*) FILTER (WHERE rating >= 3 AND rating < 4)  AS star3,
                COUNT(*) FILTER (WHERE rating >= 4 AND rating < 5)  AS star4,
                COUNT(*) FILTER (WHERE rating >= 5)                  AS star5
            FROM review_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND review_date >= @periodStart
              AND review_date <= @periodEnd
            """;

        const string priorAvgSql = """
            SELECT COALESCE(AVG(rating), 0)
            FROM review_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND review_date >= @priorStart
              AND review_date <= @priorEnd
            """;

        const string priorCountSql = """
            SELECT COUNT(*)
            FROM review_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND review_date >= @priorStart
              AND review_date <= @priorEnd
            """;

        const string snippetsSql = """
            SELECT platform, review_date, rating, review_text
            FROM review_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND review_date >= @periodStart
              AND review_date <= @periodEnd
            ORDER BY review_date DESC
            LIMIT 20
            """;

        dynamic totals = (await conn.QueryAsync(totalsSql, parameters)).First();
        long count = (long)totals.count;
        decimal avgRating = count > 0 ? Math.Round((decimal)totals.avg_rating, 2) : 0m;

        long priorCount = await conn.ExecuteScalarAsync<long>(priorCountSql, parameters);
        decimal priorAvg = priorCount > 0
            ? Math.Round(await conn.ExecuteScalarAsync<decimal>(priorAvgSql, parameters), 2)
            : 0m;

        IEnumerable<dynamic> snippetRows = await conn.QueryAsync(snippetsSql, parameters);

        Dictionary<int, int> distribution = new()
        {
            [1] = (int)(long)totals.star1,
            [2] = (int)(long)totals.star2,
            [3] = (int)(long)totals.star3,
            [4] = (int)(long)totals.star4,
            [5] = (int)(long)totals.star5
        };

        List<ReviewSnippetDto> snippets = snippetRows.Select(r => new ReviewSnippetDto(
            (string)r.platform,
            (DateOnly)r.review_date,
            (decimal)r.rating,
            (string?)r.review_text)).ToList();

        decimal? priorQuarterDelta = priorCount > 0
            ? Math.Round(avgRating - priorAvg, 2)
            : null;

        return new ReviewSummaryDto(
            avgRating,
            (int)count,
            distribution,
            snippets,
            priorCount > 0 ? priorAvg : null,
            priorQuarterDelta);
    }

    private static async Task<InspectionSummaryDto> BuildInspectionSummaryAsync(
        IDbConnection conn,
        object parameters,
        CancellationToken ct)
    {
        const string sql = """
            SELECT
                COUNT(*)                                AS count,
                AVG(score)                              AS avg_score,
                COALESCE(SUM(issues_found), 0)          AS total_issues_found,
                COALESCE(SUM(issues_resolved), 0)       AS total_issues_resolved
            FROM inspection_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND inspection_date >= @periodStart
              AND inspection_date <= @periodEnd
            """;

        dynamic row = (await conn.QueryAsync(sql, parameters)).First();
        long count = (long)row.count;
        decimal? avgScore = count > 0 ? (decimal?)row.avg_score : null;

        return new InspectionSummaryDto(
            (int)count,
            avgScore.HasValue ? Math.Round(avgScore.Value, 2) : null,
            (int)(long)row.total_issues_found,
            (int)(long)row.total_issues_resolved);
    }

    private static async Task<IReadOnlyList<FlaggedIssueDto>> BuildFlaggedIssuesAsync(
        IDbConnection conn,
        object parameters,
        CancellationToken ct)
    {
        List<FlaggedIssueDto> issues = new();

        const string missedTasksSql = """
            SELECT id, task_type, scheduled_at
            FROM task_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND scheduled_at >= @periodStart::timestamp
              AND scheduled_at <= @periodEnd::timestamp + interval '1 day' - interval '1 second'
              AND (status = 'missed'
                   OR (scheduled_at IS NOT NULL AND completed_at IS NULL
                       AND scheduled_at < NOW()))
            ORDER BY scheduled_at
            """;

        IEnumerable<dynamic> missedTasks = await conn.QueryAsync(missedTasksSql, parameters);
        foreach (dynamic task in missedTasks)
        {
            issues.Add(new FlaggedIssueDto(
                "MissedTask",
                $"Task '{task.task_type}' scheduled at {task.scheduled_at:yyyy-MM-dd HH:mm} was not completed.",
                (Guid)task.id));
        }

        const string lowReviewsSql = """
            SELECT id, platform, review_date, rating
            FROM review_records
            WHERE tenant_id = @tenantId
              AND property_id = @propertyId
              AND review_date >= @periodStart
              AND review_date <= @periodEnd
              AND rating < 3.0
            ORDER BY review_date
            """;

        IEnumerable<dynamic> lowReviews = await conn.QueryAsync(lowReviewsSql, parameters);
        foreach (dynamic review in lowReviews)
        {
            issues.Add(new FlaggedIssueDto(
                "LowRating",
                $"Review on {review.review_date:yyyy-MM-dd} via {review.platform} received a low rating of {review.rating:F1}.",
                (Guid)review.id));
        }

        const string anomalousExpensesSql = """
            WITH category_stats AS (
                SELECT
                    category,
                    PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY amount)    AS median_amount,
                    STDDEV_POP(amount)                                      AS stddev_amount
                FROM expense_records
                WHERE tenant_id = @tenantId
                  AND property_id = @propertyId
                  AND expense_date >= @periodStart
                  AND expense_date <= @periodEnd
                GROUP BY category
                HAVING COUNT(*) >= 2
            )
            SELECT e.id, e.category, e.amount, e.expense_date, e.description,
                   cs.median_amount, cs.stddev_amount
            FROM expense_records e
            JOIN category_stats cs ON cs.category = e.category
            WHERE e.tenant_id = @tenantId
              AND e.property_id = @propertyId
              AND e.expense_date >= @periodStart
              AND e.expense_date <= @periodEnd
              AND cs.stddev_amount > 0
              AND e.amount > cs.median_amount + 2 * cs.stddev_amount
            ORDER BY e.expense_date
            """;

        IEnumerable<dynamic> anomalousExpenses = await conn.QueryAsync(anomalousExpensesSql, parameters);
        foreach (dynamic expense in anomalousExpenses)
        {
            decimal median = (decimal)expense.median_amount;
            decimal stddev = (decimal)expense.stddev_amount;
            decimal amount = (decimal)expense.amount;
            issues.Add(new FlaggedIssueDto(
                "AnomalousExpense",
                $"Expense of {amount:C} in category '{expense.category}' on {expense.expense_date:yyyy-MM-dd} " +
                $"exceeds 2 standard deviations above the category median ({median:C}, σ={stddev:C}).",
                (Guid)expense.id));
        }

        return issues;
    }
}
