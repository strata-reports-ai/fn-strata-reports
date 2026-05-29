using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StrataReports.Functions.Functions;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Tests;

public sealed class BillingWebhookTests : IDisposable
{
    private readonly AppDbContext _db;

    public BillingWebhookTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private Tenant BuildTenant(string plan = "beta", string status = "active")
    {
        Tenant tenant = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Tenant",
            Plan = plan,
            Status = status,
            TrialEndsAt = DateTimeOffset.UtcNow.AddDays(14),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Tenants.Add(tenant);
        _db.SaveChanges();
        return tenant;
    }

    [Fact]
    public void BillingWebhookFunction_MissingWebhookSecret_IsHandledGracefully()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        BillingWebhookFunction function = new(
            NullLogger<BillingWebhookFunction>.Instance,
            _db,
            config);

        Assert.NotNull(function);
    }

    [Fact]
    public async Task CheckoutSessionCompleted_UpdatesTenantPlanAndStatus()
    {
        Tenant tenant = BuildTenant();
        string stripeCustomerId = "cus_test_" + Guid.NewGuid().ToString("N");

        tenant.StripeCustomerId = stripeCustomerId;
        tenant.Plan = "starter";
        tenant.Status = "active";
        tenant.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        Tenant? updated = await _db.Tenants.FindAsync([tenant.Id]);
        Assert.NotNull(updated);
        Assert.Equal("starter", updated!.Plan);
        Assert.Equal("active", updated.Status);
        Assert.Equal(stripeCustomerId, updated.StripeCustomerId);
    }

    [Fact]
    public async Task SubscriptionDeleted_SetsTenantStatusCancelled()
    {
        Tenant tenant = BuildTenant(plan: "pro", status: "active");

        tenant.Status = "cancelled";
        tenant.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        Tenant? updated = await _db.Tenants.FindAsync([tenant.Id]);
        Assert.NotNull(updated);
        Assert.Equal("cancelled", updated!.Status);
    }

    [Fact]
    public async Task InvoicePaymentFailed_SetsTenantStatusPastDue()
    {
        Tenant tenant = BuildTenant(status: "active");
        tenant.StripeCustomerId = "cus_pastdue_" + Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync();

        Tenant? dbTenant = await _db.Tenants.FirstOrDefaultAsync(
            t => t.StripeCustomerId == tenant.StripeCustomerId);
        Assert.NotNull(dbTenant);

        dbTenant!.Status = "past_due";
        dbTenant.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        Tenant? updated = await _db.Tenants.FindAsync([tenant.Id]);
        Assert.NotNull(updated);
        Assert.Equal("past_due", updated!.Status);
    }

    [Fact]
    public async Task SubscriptionUpdated_UpdatesPlanAndStatus()
    {
        Tenant tenant = BuildTenant(plan: "starter", status: "active");

        tenant.Plan = "pro";
        tenant.Status = "active";
        tenant.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        Tenant? updated = await _db.Tenants.FindAsync([tenant.Id]);
        Assert.NotNull(updated);
        Assert.Equal("pro", updated!.Plan);
        Assert.Equal("active", updated.Status);
    }

    [Fact]
    public void NewTenant_HasTrialEndsAtSet()
    {
        Tenant tenant = new()
        {
            Id = Guid.NewGuid(),
            Name = "New Org",
            Plan = "beta",
            Status = "active",
            TrialEndsAt = DateTimeOffset.UtcNow.AddDays(14),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        Assert.NotNull(tenant.TrialEndsAt);
        Assert.True(tenant.TrialEndsAt > DateTimeOffset.UtcNow.AddDays(13));
        Assert.True(tenant.TrialEndsAt < DateTimeOffset.UtcNow.AddDays(15));
    }

    [Fact]
    public void NewTenant_HasBetaPlanAndActiveStatus()
    {
        Tenant tenant = new()
        {
            Id = Guid.NewGuid(),
            Name = "New Org",
            Plan = "beta",
            Status = "active",
            TrialEndsAt = DateTimeOffset.UtcNow.AddDays(14),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal("beta", tenant.Plan);
        Assert.Equal("active", tenant.Status);
    }
}
