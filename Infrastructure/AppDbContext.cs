using Microsoft.EntityFrameworkCore;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Import> Imports => Set<Import>();
    public DbSet<RevenueRecord> RevenueRecords => Set<RevenueRecord>();
    public DbSet<ExpenseRecord> ExpenseRecords => Set<ExpenseRecord>();
    public DbSet<TaskRecord> TaskRecords => Set<TaskRecord>();
    public DbSet<ReviewRecord> ReviewRecords => Set<ReviewRecord>();
    public DbSet<InspectionRecord> InspectionRecords => Set<InspectionRecord>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>().HasIndex(u => u.EntraObjectId).IsUnique();
        modelBuilder.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
        modelBuilder.Entity<RefreshToken>().HasIndex(r => r.Jti).IsUnique();
        modelBuilder.Entity<RefreshToken>().HasIndex(r => new { r.UserId, r.RevokedAt });
        modelBuilder.Entity<Property>().HasIndex(p => new { p.TenantId, p.DeletedAt });
        modelBuilder.Entity<RevenueRecord>().HasAlternateKey(r => new { r.TenantId, r.PropertyId, r.BookingExternalId });
        modelBuilder.Entity<RevenueRecord>().HasIndex(r => new { r.TenantId, r.PropertyId, r.CheckinDate });
        modelBuilder.Entity<ExpenseRecord>().HasIndex(e => new { e.TenantId, e.PropertyId, e.ExpenseDate });
        modelBuilder.Entity<TaskRecord>().HasIndex(t => new { t.TenantId, t.PropertyId, t.ScheduledAt });
        modelBuilder.Entity<ReviewRecord>().HasIndex(r => new { r.TenantId, r.PropertyId, r.ReviewDate });
        modelBuilder.Entity<Report>().HasAlternateKey(r => new { r.TenantId, r.PropertyId, r.ReportType, r.PeriodStart, r.PeriodEnd });
        modelBuilder.Entity<Import>().HasIndex(i => new { i.TenantId, i.Status, i.CreatedAt });
    }
}
