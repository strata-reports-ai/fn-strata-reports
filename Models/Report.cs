namespace StrataReports.Functions.Models;

public class Report
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PropertyId { get; set; }
    public string ReportType { get; set; } = "quarterly_owner";
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string Status { get; set; } = "queued";
    public string? AiModel { get; set; }
    public int? AiInputTokens { get; set; }
    public int? AiOutputTokens { get; set; }
    public decimal? AiCostUsd { get; set; }
    public int? GenerationMs { get; set; }
    public string? PdfBlobPath { get; set; }
    public string? JsonPayload { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid GeneratedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
