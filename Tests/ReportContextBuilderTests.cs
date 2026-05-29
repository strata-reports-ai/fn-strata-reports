using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;
using StrataReports.Functions.Services;

namespace StrataReports.Functions.Tests;

public class ReportContextBuilderTests
{
    private static AppDbContext BuildInMemoryDb(Guid tenantId, Guid propertyId, bool includeProperty = true)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        AppDbContext db = new(options);

        if (includeProperty)
        {
            db.Properties.Add(new Property
            {
                Id = propertyId,
                TenantId = tenantId,
                Name = "Test Property",
                Units = 1,
                CountryCode = "AU",
                Timezone = "Australia/Sydney",
                CurrencyCode = "AUD",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }

        return db;
    }

    [Fact]
    public async Task BuildReportContextAsync_PropertyNotFound_ThrowsInvalidOperation()
    {
        Guid tenantId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();

        AppDbContext db = BuildInMemoryDb(tenantId, propertyId, includeProperty: false);
        Mock<IDbConnectionFactory> connFactoryMock = new();

        ReportContextBuilder builder = new(
            db, connFactoryMock.Object,
            NullLogger<ReportContextBuilder>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.BuildReportContextAsync(
                propertyId, new DateOnly(2025, 1, 1), new DateOnly(2025, 3, 31), tenantId));
    }

    [Fact]
    public async Task BuildReportContextAsync_NoData_ThrowsInsufficientDataException()
    {
        Guid tenantId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();

        AppDbContext db = BuildInMemoryDb(tenantId, propertyId);

        string? connString = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (string.IsNullOrEmpty(connString))
            return;

        NpgsqlConnectionFactory factory = new(connString);

        ReportContextBuilder builder = new(
            db, factory,
            NullLogger<ReportContextBuilder>.Instance);

        await Assert.ThrowsAsync<InsufficientDataException>(() =>
            builder.BuildReportContextAsync(
                propertyId, new DateOnly(2025, 1, 1), new DateOnly(2025, 3, 31), tenantId));
    }

    [Fact]
    public async Task BuildReportContextAsync_EmptyPeriod_ReturnsZeroSummaries()
    {
        string? connString = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (string.IsNullOrEmpty(connString))
            return;

        Guid tenantId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();

        await using NpgsqlConnection setupConn = new(connString);
        await setupConn.OpenAsync();

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = $"SET LOCAL app.current_tenant_id = '{tenantId}'";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO properties (id, tenant_id, name, units, country_code, timezone, currency_code, created_at, updated_at)
                VALUES (@id, @tenantId, 'Integration Test Property', 1, 'AU', 'Australia/Sydney', 'AUD', NOW(), NOW())
                """;
            cmd.Parameters.AddWithValue("id", propertyId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            AppDbContext db = BuildInMemoryDb(tenantId, propertyId);
            NpgsqlConnectionFactory factory = new(connString);

            ReportContextBuilder builder = new(
                db, factory,
                NullLogger<ReportContextBuilder>.Instance);

            DateOnly periodStart = new(2025, 1, 1);
            DateOnly periodEnd = new(2025, 3, 31);

            await Assert.ThrowsAsync<InsufficientDataException>(() =>
                builder.BuildReportContextAsync(propertyId, periodStart, periodEnd, tenantId));
        }
        finally
        {
            await using NpgsqlCommand cleanup = setupConn.CreateCommand();
            cleanup.CommandText = "DELETE FROM properties WHERE id = @id";
            cleanup.Parameters.AddWithValue("id", propertyId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task BuildReportContextAsync_WithData_ReturnsPopulatedContext()
    {
        string? connString = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (string.IsNullOrEmpty(connString))
            return;

        Guid tenantId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid importId = Guid.NewGuid();

        await using NpgsqlConnection setupConn = new(connString);
        await setupConn.OpenAsync();

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = $"SET LOCAL app.current_tenant_id = '{tenantId}'";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO properties (id, tenant_id, name, units, country_code, timezone, currency_code, created_at, updated_at)
                VALUES (@id, @tenantId, 'Integration Test Property', 1, 'AU', 'Australia/Sydney', 'AUD', NOW(), NOW())
                """;
            cmd.Parameters.AddWithValue("id", propertyId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO imports (id, tenant_id, property_id, import_type, status, created_at, updated_at)
                VALUES (@id, @tenantId, @propertyId, 'revenue', 'completed', NOW(), NOW())
                """;
            cmd.Parameters.AddWithValue("id", importId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("propertyId", propertyId);
            await cmd.ExecuteNonQueryAsync();
        }

        for (int i = 0; i < 10; i++)
        {
            await using NpgsqlCommand cmd = setupConn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO revenue_records
                    (id, tenant_id, property_id, import_id, booking_external_id, platform,
                     checkin_date, checkout_date, nights, gross_revenue, cleaning_fee,
                     platform_fee, host_fee, net_revenue, currency_code, created_at)
                VALUES
                    (gen_random_uuid(), @tenantId, @propertyId, @importId,
                     @extId, 'Airbnb',
                     @checkin, @checkout, 3, 900.00, 50.00, 100.00, 50.00, 700.00, 'AUD', NOW())
                """;
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("propertyId", propertyId);
            cmd.Parameters.AddWithValue("importId", importId);
            cmd.Parameters.AddWithValue("extId", $"BK-{i:D4}");
            cmd.Parameters.AddWithValue("checkin", new DateOnly(2025, 1, 1).AddDays(i * 7));
            cmd.Parameters.AddWithValue("checkout", new DateOnly(2025, 1, 4).AddDays(i * 7));
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            AppDbContext db = BuildInMemoryDb(tenantId, propertyId);
            NpgsqlConnectionFactory factory = new(connString);

            ReportContextBuilder builder = new(
                db, factory,
                NullLogger<ReportContextBuilder>.Instance);

            DateOnly periodStart = new(2025, 1, 1);
            DateOnly periodEnd = new(2025, 3, 31);

            ReportContextDto context = await builder.BuildReportContextAsync(
                propertyId, periodStart, periodEnd, tenantId);

            Assert.NotNull(context);
            Assert.Equal(propertyId, context.Property.Id);
            Assert.True(context.Revenue.Total > 0);
            Assert.True(context.Revenue.OccupancyRate >= 0);
            Assert.Equal(0m, context.Expense.Total);
            Assert.Equal(0, context.Tasks.Total);
            Assert.Equal(0, context.Reviews.Count);
            Assert.Equal(0, context.Inspections.Count);
            Assert.Null(context.Revenue.PriorQuarterTotal);
            Assert.Null(context.Revenue.PriorQuarterDelta);
        }
        finally
        {
            await using NpgsqlCommand cleanup = setupConn.CreateCommand();
            cleanup.CommandText = "DELETE FROM revenue_records WHERE property_id = @id; DELETE FROM imports WHERE property_id = @id; DELETE FROM properties WHERE id = @id";
            cleanup.Parameters.AddWithValue("id", propertyId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task BuildReportContextAsync_PriorQuarterNoData_DeltaIsNull()
    {
        string? connString = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (string.IsNullOrEmpty(connString))
            return;

        Guid tenantId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid importId = Guid.NewGuid();

        await using NpgsqlConnection setupConn = new(connString);
        await setupConn.OpenAsync();

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = $"SET LOCAL app.current_tenant_id = '{tenantId}'";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO properties (id, tenant_id, name, units, country_code, timezone, currency_code, created_at, updated_at)
                VALUES (@id, @tenantId, 'Delta Test Property', 1, 'AU', 'Australia/Sydney', 'AUD', NOW(), NOW())
                """;
            cmd.Parameters.AddWithValue("id", propertyId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO imports (id, tenant_id, property_id, import_type, status, created_at, updated_at)
                VALUES (@id, @tenantId, @propertyId, 'revenue', 'completed', NOW(), NOW())
                """;
            cmd.Parameters.AddWithValue("id", importId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("propertyId", propertyId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO revenue_records
                    (id, tenant_id, property_id, import_id, booking_external_id, platform,
                     checkin_date, checkout_date, nights, gross_revenue, cleaning_fee,
                     platform_fee, host_fee, net_revenue, currency_code, created_at)
                VALUES
                    (gen_random_uuid(), @tenantId, @propertyId, @importId,
                     'BK-0001', 'Airbnb',
                     '2025-01-05', '2025-01-08', 3, 900.00, 50.00, 100.00, 50.00, 700.00, 'AUD', NOW())
                """;
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("propertyId", propertyId);
            cmd.Parameters.AddWithValue("importId", importId);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            AppDbContext db = BuildInMemoryDb(tenantId, propertyId);
            NpgsqlConnectionFactory factory = new(connString);
            ReportContextBuilder builder = new(db, factory, NullLogger<ReportContextBuilder>.Instance);

            DateOnly periodStart = new(2025, 1, 1);
            DateOnly periodEnd = new(2025, 3, 31);

            ReportContextDto context = await builder.BuildReportContextAsync(
                propertyId, periodStart, periodEnd, tenantId);

            Assert.Null(context.Revenue.PriorQuarterTotal);
            Assert.Null(context.Revenue.PriorQuarterDelta);
            Assert.Null(context.Reviews.PriorQuarterAverageRating);
            Assert.Null(context.Reviews.PriorQuarterDelta);
        }
        finally
        {
            await using NpgsqlCommand cleanup = setupConn.CreateCommand();
            cleanup.CommandText = "DELETE FROM revenue_records WHERE property_id = @id; DELETE FROM imports WHERE property_id = @id; DELETE FROM properties WHERE id = @id";
            cleanup.Parameters.AddWithValue("id", propertyId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task BuildReportContextAsync_PartialData_OnlyRevenue_DoesNotThrow()
    {
        string? connString = Environment.GetEnvironmentVariable("TEST_DATABASE_URL");
        if (string.IsNullOrEmpty(connString))
            return;

        Guid tenantId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid importId = Guid.NewGuid();

        await using NpgsqlConnection setupConn = new(connString);
        await setupConn.OpenAsync();

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = $"SET LOCAL app.current_tenant_id = '{tenantId}'";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO properties (id, tenant_id, name, units, country_code, timezone, currency_code, created_at, updated_at)
                VALUES (@id, @tenantId, 'Partial Test Property', 1, 'AU', 'Australia/Sydney', 'AUD', NOW(), NOW())
                """;
            cmd.Parameters.AddWithValue("id", propertyId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO imports (id, tenant_id, property_id, import_type, status, created_at, updated_at)
                VALUES (@id, @tenantId, @propertyId, 'revenue', 'completed', NOW(), NOW())
                """;
            cmd.Parameters.AddWithValue("id", importId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("propertyId", propertyId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO revenue_records
                    (id, tenant_id, property_id, import_id, booking_external_id, platform,
                     checkin_date, checkout_date, nights, gross_revenue, cleaning_fee,
                     platform_fee, host_fee, net_revenue, currency_code, created_at)
                VALUES
                    (gen_random_uuid(), @tenantId, @propertyId, @importId,
                     'BK-PARTIAL', 'Vrbo',
                     '2025-02-01', '2025-02-05', 4, 1200.00, 60.00, 120.00, 60.00, 960.00, 'AUD', NOW())
                """;
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("propertyId", propertyId);
            cmd.Parameters.AddWithValue("importId", importId);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            AppDbContext db = BuildInMemoryDb(tenantId, propertyId);
            NpgsqlConnectionFactory factory = new(connString);
            ReportContextBuilder builder = new(db, factory, NullLogger<ReportContextBuilder>.Instance);

            DateOnly periodStart = new(2025, 1, 1);
            DateOnly periodEnd = new(2025, 3, 31);

            ReportContextDto context = await builder.BuildReportContextAsync(
                propertyId, periodStart, periodEnd, tenantId);

            Assert.True(context.Revenue.Total > 0);
            Assert.Equal(0m, context.Expense.Total);
            Assert.Empty(context.FlaggedIssues);
        }
        finally
        {
            await using NpgsqlCommand cleanup = setupConn.CreateCommand();
            cleanup.CommandText = "DELETE FROM revenue_records WHERE property_id = @id; DELETE FROM imports WHERE property_id = @id; DELETE FROM properties WHERE id = @id";
            cleanup.Parameters.AddWithValue("id", propertyId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
