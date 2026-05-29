using StrataReports.Functions.Models;

namespace StrataReports.Functions.Services;

public interface IPdfRenderService
{
    Task<PdfRenderResult> RenderAndUploadAsync(
        ReportNarrativeDto narrative,
        ReportContextDto context,
        Guid tenantId,
        Guid reportId,
        CancellationToken ct);
}

public sealed record PdfRenderResult(
    string BlobPath,
    Uri SasDownloadUrl,
    string ContentDispositionFilename);
