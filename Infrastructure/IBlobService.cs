namespace StrataReports.Functions.Infrastructure;

public interface IBlobService
{
    Uri GenerateSasUploadUrl(string blobPath, TimeSpan ttl);
    Task<bool> BlobExistsAsync(string blobPath, CancellationToken ct);
    Task<long> GetBlobSizeAsync(string blobPath, CancellationToken ct);
}
