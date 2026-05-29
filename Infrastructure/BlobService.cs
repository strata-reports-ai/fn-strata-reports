using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace StrataReports.Functions.Infrastructure;

public class BlobService : IBlobService
{
    private const string ContainerName = "imports";

    private readonly ILogger<BlobService> _logger;
    private readonly BlobContainerClient _containerClient;

    public BlobService(IConfiguration configuration, ILogger<BlobService> logger)
    {
        _logger = logger;
        string connectionString = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage connection string is not configured.");
        BlobServiceClient serviceClient = new(connectionString);
        _containerClient = serviceClient.GetBlobContainerClient(ContainerName);
    }

    public Uri GenerateSasUploadUrl(string blobPath, TimeSpan ttl)
    {
        BlobClient blobClient = _containerClient.GetBlobClient(blobPath);

        BlobSasBuilder sasBuilder = new()
        {
            BlobContainerName = ContainerName,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(ttl),
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);

        Uri sasUri = blobClient.GenerateSasUri(sasBuilder);
        _logger.LogInformation("Generated SAS upload URL for blob {BlobPath}", blobPath);
        return sasUri;
    }

    public async Task<bool> BlobExistsAsync(string blobPath, CancellationToken ct)
    {
        BlobClient blobClient = _containerClient.GetBlobClient(blobPath);
        Azure.Response<bool> exists = await blobClient.ExistsAsync(ct);
        return exists.Value;
    }

    public async Task<long> GetBlobSizeAsync(string blobPath, CancellationToken ct)
    {
        BlobClient blobClient = _containerClient.GetBlobClient(blobPath);
        Azure.Response<BlobProperties> properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);
        return properties.Value.ContentLength;
    }
}
