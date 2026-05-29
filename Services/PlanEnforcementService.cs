using Microsoft.EntityFrameworkCore;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Services;

public enum EnforcementResult
{
    Allowed,
    PlanLimitExceeded,
    TrialExpired,
    PastDueGracePeriodExceeded,
}

public sealed record EnforcementOutcome(
    EnforcementResult Result,
    string? Type = null,
    string? Detail = null,
    int? PlanLimit = null);

public interface IPlanEnforcementService
{
    Task<EnforcementOutcome> CheckPropertyCreateAsync(Guid tenantId, CancellationToken ct);
    Task<EnforcementOutcome> CheckReportGenerateAsync(Guid tenantId, CancellationToken ct);
}

public class PlanEnforcementService(AppDbContext db) : IPlanEnforcementService
{
    private static readonly TimeSpan PastDueGracePeriod = TimeSpan.FromDays(7);

    private static readonly Dictionary<string, int> PropertyLimits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["starter"] = 5,
        ["pro"] = 20,
        ["scale"] = 50,
        ["beta"] = 5,
    };

    private static readonly Dictionary<string, int?> ReportQuarterlyLimits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["starter"] = 4,
        ["pro"] = 20,
        ["scale"] = null,
        ["beta"] = 4,
    };

    public async Task<EnforcementOutcome> CheckPropertyCreateAsync(Guid tenantId, CancellationToken ct)
    {
        Tenant? tenant = await db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null)
            return new EnforcementOutcome(EnforcementResult.Allowed);

        EnforcementOutcome? readOnlyOutcome = CheckReadOnlyMode(tenant);
        if (readOnlyOutcome is not null)
            return readOnlyOutcome;

        int limit = PropertyLimits.TryGetValue(tenant.Plan, out int planLimit) ? planLimit : 5;

        int activePropertyCount = await db.Properties
            .CountAsync(p => p.TenantId == tenantId && p.DeletedAt == null, ct);

        if (activePropertyCount >= limit)
        {
            return new EnforcementOutcome(
                EnforcementResult.PlanLimitExceeded,
                Type: "plan_limit_exceeded",
                Detail: $"Your {tenant.Plan} plan allows up to {limit} properties. Please upgrade to add more.",
                PlanLimit: limit);
        }

        return new EnforcementOutcome(EnforcementResult.Allowed);
    }

    public async Task<EnforcementOutcome> CheckReportGenerateAsync(Guid tenantId, CancellationToken ct)
    {
        Tenant? tenant = await db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null)
            return new EnforcementOutcome(EnforcementResult.Allowed);

        EnforcementOutcome? readOnlyOutcome = CheckReadOnlyMode(tenant);
        if (readOnlyOutcome is not null)
            return readOnlyOutcome;

        if (!ReportQuarterlyLimits.TryGetValue(tenant.Plan, out int? quota))
            quota = 4;

        if (quota is null)
            return new EnforcementOutcome(EnforcementResult.Allowed);

        DateOnly quarterStart = GetCurrentQuarterStart();
        DateOnly quarterEnd = GetCurrentQuarterEnd();

        int reportsThisQuarter = await db.Reports
            .CountAsync(r =>
                r.TenantId == tenantId &&
                r.CreatedAt >= new DateTimeOffset(quarterStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) &&
                r.CreatedAt < new DateTimeOffset(quarterEnd.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                ct);

        if (reportsThisQuarter >= quota.Value)
        {
            return new EnforcementOutcome(
                EnforcementResult.PlanLimitExceeded,
                Type: "plan_limit_exceeded",
                Detail: $"Your {tenant.Plan} plan allows up to {quota.Value} reports per quarter. Please upgrade to generate more.",
                PlanLimit: quota.Value);
        }

        return new EnforcementOutcome(EnforcementResult.Allowed);
    }

    private static EnforcementOutcome? CheckReadOnlyMode(Tenant tenant)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (tenant.Status != "active" && tenant.Status != "past_due")
        {
            if (tenant.TrialEndsAt.HasValue && tenant.TrialEndsAt.Value < now)
            {
                return new EnforcementOutcome(
                    EnforcementResult.TrialExpired,
                    Type: "trial_expired",
                    Detail: "Your trial has expired. Please subscribe to continue creating properties and reports.");
            }
        }

        if (tenant.TrialEndsAt.HasValue && tenant.TrialEndsAt.Value < now && tenant.Status == "active")
        {
            bool hasActiveSubscription = !string.IsNullOrEmpty(tenant.StripeCustomerId);
            if (!hasActiveSubscription)
            {
                return new EnforcementOutcome(
                    EnforcementResult.TrialExpired,
                    Type: "trial_expired",
                    Detail: "Your trial has expired. Please subscribe to continue creating properties and reports.");
            }
        }

        if (tenant.Status == "past_due")
        {
            DateTimeOffset gracePeriodEnd = tenant.UpdatedAt + PastDueGracePeriod;
            if (now > gracePeriodEnd)
            {
                return new EnforcementOutcome(
                    EnforcementResult.PastDueGracePeriodExceeded,
                    Type: "trial_expired",
                    Detail: "Your account has an outstanding payment. Please update your billing information to continue.");
            }
        }

        if (tenant.Status == "cancelled")
        {
            return new EnforcementOutcome(
                EnforcementResult.TrialExpired,
                Type: "trial_expired",
                Detail: "Your subscription has been cancelled. Please resubscribe to continue.");
        }

        return null;
    }

    private static DateOnly GetCurrentQuarterStart()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int quarterMonth = ((now.Month - 1) / 3) * 3 + 1;
        return new DateOnly(now.Year, quarterMonth, 1);
    }

    private static DateOnly GetCurrentQuarterEnd()
    {
        DateOnly start = GetCurrentQuarterStart();
        return start.AddMonths(3).AddDays(-1);
    }
}
