using Microsoft.EntityFrameworkCore;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;
using StrataReports.Functions.Services;

namespace StrataReports.Functions.Tests;

public sealed class PlanEnforcementServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PlanEnforcementService _sut;

    public PlanEnforcementServiceTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _sut = new PlanEnforcementService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Tenant BuildTenant(
        string plan = "starter",
        string status = "active",
        DateTimeOffset? trialEndsAt = null,
        string? stripeCustomerId = null,
        DateTimeOffset? updatedAt = null)
    {
        Tenant tenant = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Tenant",
            Plan = plan,
            Status = status,
            TrialEndsAt = trialEndsAt ?? DateTimeOffset.UtcNow.AddDays(14),
            StripeCustomerId = stripeCustomerId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        };
        _db.Tenants.Add(tenant);
        _db.SaveChanges();
        return tenant;
    }

    private void AddProperties(Guid tenantId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _db.Properties.Add(new Property
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = $"Property {i}",
                CountryCode = "US",
                Timezone = "America/New_York",
                CurrencyCode = "USD",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        _db.SaveChanges();
    }

    private void AddReports(Guid tenantId, int count)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int quarterMonth = ((now.Month - 1) / 3) * 3 + 1;
        DateTimeOffset quarterStart = new(now.Year, quarterMonth, 1, 0, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < count; i++)
        {
            Guid propertyId = Guid.NewGuid();
            _db.Reports.Add(new Report
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PropertyId = propertyId,
                ReportType = "quarterly",
                PeriodStart = DateOnly.FromDateTime(quarterStart.DateTime),
                PeriodEnd = DateOnly.FromDateTime(quarterStart.AddMonths(3).AddDays(-1).DateTime),
                Status = "succeeded",
                GeneratedByUserId = Guid.NewGuid(),
                CreatedAt = quarterStart.AddDays(i),
                UpdatedAt = quarterStart.AddDays(i),
            });
        }
        _db.SaveChanges();
    }

    [Fact]
    public async Task CheckPropertyCreate_StarterPlan_UnderLimit_ReturnsAllowed()
    {
        Tenant tenant = BuildTenant(plan: "starter");
        AddProperties(tenant.Id, 4);

        EnforcementOutcome result = await _sut.CheckPropertyCreateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.Allowed, result.Result);
    }

    [Fact]
    public async Task CheckPropertyCreate_StarterPlan_AtLimit_ReturnsPlanLimitExceeded()
    {
        Tenant tenant = BuildTenant(plan: "starter");
        AddProperties(tenant.Id, 5);

        EnforcementOutcome result = await _sut.CheckPropertyCreateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.PlanLimitExceeded, result.Result);
        Assert.Equal("plan_limit_exceeded", result.Type);
        Assert.Equal(5, result.PlanLimit);
    }

    [Fact]
    public async Task CheckPropertyCreate_ProPlan_Limit20_ReturnsAllowed()
    {
        Tenant tenant = BuildTenant(plan: "pro");
        AddProperties(tenant.Id, 19);

        EnforcementOutcome result = await _sut.CheckPropertyCreateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.Allowed, result.Result);
    }

    [Fact]
    public async Task CheckPropertyCreate_ProPlan_AtLimit_ReturnsPlanLimitExceeded()
    {
        Tenant tenant = BuildTenant(plan: "pro");
        AddProperties(tenant.Id, 20);

        EnforcementOutcome result = await _sut.CheckPropertyCreateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.PlanLimitExceeded, result.Result);
        Assert.Equal(20, result.PlanLimit);
    }

    [Fact]
    public async Task CheckPropertyCreate_ScalePlan_AtLimit_ReturnsAllowed()
    {
        Tenant tenant = BuildTenant(plan: "scale");
        AddProperties(tenant.Id, 50);

        EnforcementOutcome result = await _sut.CheckPropertyCreateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.PlanLimitExceeded, result.Result);
        Assert.Equal(50, result.PlanLimit);
    }

    [Fact]
    public async Task CheckPropertyCreate_TrialExpired_NoSubscription_ReturnsTrialExpired()
    {
        Tenant tenant = BuildTenant(
            plan: "beta",
            status: "active",
            trialEndsAt: DateTimeOffset.UtcNow.AddDays(-1),
            stripeCustomerId: null);

        EnforcementOutcome result = await _sut.CheckPropertyCreateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.TrialExpired, result.Result);
        Assert.Equal("trial_expired", result.Type);
    }

    [Fact]
    public async Task CheckPropertyCreate_TrialExpired_HasSubscription_ReturnsAllowed()
    {
        Tenant tenant = BuildTenant(
            plan: "starter",
            status: "active",
            trialEndsAt: DateTimeOffset.UtcNow.AddDays(-1),
            stripeCustomerId: "cus_test123");

        EnforcementOutcome result = await _sut.CheckPropertyCreateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.Allowed, result.Result);
    }

    [Fact]
    public async Task CheckPropertyCreate_Cancelled_ReturnsTrialExpired()
    {
        Tenant tenant = BuildTenant(status: "cancelled");

        EnforcementOutcome result = await _sut.CheckPropertyCreateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.TrialExpired, result.Result);
        Assert.Equal("trial_expired", result.Type);
    }

    [Fact]
    public async Task CheckPropertyCreate_PastDue_WithinGracePeriod_ReturnsAllowed()
    {
        Tenant tenant = BuildTenant(
            status: "past_due",
            updatedAt: DateTimeOffset.UtcNow.AddDays(-3));

        EnforcementOutcome result = await _sut.CheckPropertyCreateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.Allowed, result.Result);
    }

    [Fact]
    public async Task CheckPropertyCreate_PastDue_GracePeriodExceeded_ReturnsBlocked()
    {
        Tenant tenant = BuildTenant(
            status: "past_due",
            updatedAt: DateTimeOffset.UtcNow.AddDays(-8));

        EnforcementOutcome result = await _sut.CheckPropertyCreateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.PastDueGracePeriodExceeded, result.Result);
        Assert.Equal("trial_expired", result.Type);
    }

    [Fact]
    public async Task CheckReportGenerate_StarterPlan_UnderQuota_ReturnsAllowed()
    {
        Tenant tenant = BuildTenant(plan: "starter");
        AddReports(tenant.Id, 3);

        EnforcementOutcome result = await _sut.CheckReportGenerateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.Allowed, result.Result);
    }

    [Fact]
    public async Task CheckReportGenerate_StarterPlan_AtQuota_ReturnsPlanLimitExceeded()
    {
        Tenant tenant = BuildTenant(plan: "starter");
        AddReports(tenant.Id, 4);

        EnforcementOutcome result = await _sut.CheckReportGenerateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.PlanLimitExceeded, result.Result);
        Assert.Equal("plan_limit_exceeded", result.Type);
        Assert.Equal(4, result.PlanLimit);
    }

    [Fact]
    public async Task CheckReportGenerate_ScalePlan_UnlimitedQuota_ReturnsAllowed()
    {
        Tenant tenant = BuildTenant(plan: "scale");
        AddReports(tenant.Id, 100);

        EnforcementOutcome result = await _sut.CheckReportGenerateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.Allowed, result.Result);
    }

    [Fact]
    public async Task CheckReportGenerate_ProPlan_AtQuota_ReturnsPlanLimitExceeded()
    {
        Tenant tenant = BuildTenant(plan: "pro");
        AddReports(tenant.Id, 20);

        EnforcementOutcome result = await _sut.CheckReportGenerateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.PlanLimitExceeded, result.Result);
        Assert.Equal(20, result.PlanLimit);
    }

    [Fact]
    public async Task CheckReportGenerate_TrialExpired_ReturnsTrialExpired()
    {
        Tenant tenant = BuildTenant(
            plan: "beta",
            status: "active",
            trialEndsAt: DateTimeOffset.UtcNow.AddDays(-1),
            stripeCustomerId: null);

        EnforcementOutcome result = await _sut.CheckReportGenerateAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(EnforcementResult.TrialExpired, result.Result);
    }
}
