namespace StrataReports.Functions.Models;

public class Import
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? PropertyId { get; set; }
    public required string ImportType { get; set; }
    public required string SourceFilename { get; set; }
    public required string BlobPath { get; set; }
    public string Status { get; set; } = "pending";
    public int? RecordsTotal { get; set; }
    public int? RecordsImported { get; set; }
    public int? RecordsSkipped { get; set; }
    public string? ErrorSummary { get; set; }
    public string? ColumnMapping { get; set; }
    public string? ChecksumSha256 { get; set; }
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
