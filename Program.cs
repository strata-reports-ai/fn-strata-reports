using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using Serilog;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Middleware;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();
builder.Logging.AddSerilog(Log.Logger, dispose: true);

builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .UseAzureMonitorExporter();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration["ConnectionStrings:Database"]));

builder.Services.AddSingleton<IDbConnectionFactory>(
    new NpgsqlConnectionFactory(builder.Configuration["ConnectionStrings:Database"]!));

builder.Services.AddSingleton<IJwtService, JwtService>();

// TODO: replace with real email service when ACS/Postmark is configured
builder.Services.AddSingleton<IEmailService, NoOpEmailService>();

builder.UseMiddleware<RateLimitMiddleware>();
builder.UseMiddleware<TenantMiddleware>();

var app = builder.Build();

using (IServiceScope scope = app.Services.CreateScope())
{
    AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
