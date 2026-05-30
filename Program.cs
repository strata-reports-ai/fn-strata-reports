using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using Serilog;
using Stripe;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Middleware;
using StrataReports.Functions.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();
builder.Logging.AddSerilog(Log.Logger, dispose: true);

builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .WithMetrics(metrics => metrics.AddMeter("StrataReports.Functions"))
    .UseAzureMonitorExporter();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration["ConnectionStrings:Database"]));

builder.Services.AddSingleton<IDbConnectionFactory>(
    new NpgsqlConnectionFactory(builder.Configuration["ConnectionStrings:Database"]!));

builder.Services.AddSingleton<IJwtService, JwtService>();

// TODO: replace with real email service when ACS/Postmark is configured
builder.Services.AddSingleton<IEmailService, NoOpEmailService>();

builder.Services.AddScoped<IReportContextBuilder, ReportContextBuilder>();

builder.Services.AddSingleton<IBlobService, BlobService>();
builder.Services.AddSingleton<IQueueService, QueueService>();

builder.Services.AddHttpClient("Anthropic", (sp, client) =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/v1/");
    string? apiKey = builder.Configuration["ANTHROPIC_API_KEY"];
    if (!string.IsNullOrEmpty(apiKey))
    {
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }
});

builder.Services.AddHttpClient("OpenAi", (sp, client) =>
{
    client.BaseAddress = new Uri("https://api.openai.com/v1/");
    string? apiKey = builder.Configuration["OPENAI_API_KEY"];
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
});

builder.Services.AddScoped<INarrativeGeneratorService, NarrativeGeneratorService>();
builder.Services.AddScoped<IPdfRenderService, PdfRenderService>();

builder.Services.AddScoped<IPlanEnforcementService, PlanEnforcementService>();

string? stripeWebhookSecret = builder.Configuration["Stripe__WebhookSecret"];
if (!string.IsNullOrEmpty(stripeWebhookSecret))
    StripeConfiguration.ApiKey = builder.Configuration["Stripe__ApiKey"] ?? string.Empty;

builder.UseMiddleware<RateLimitMiddleware>();
builder.UseMiddleware<TenantMiddleware>();

var app = builder.Build();

using (IServiceScope scope = app.Services.CreateScope())
{
    AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Microsoft.Extensions.Logging.ILogger startupLogger =
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
             .CreateLogger("Startup");
    try
    {
        await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        startupLogger.LogError(ex, "Database migration failed at startup. The host will continue but DB may be unavailable.");
    }
}

app.Run();
