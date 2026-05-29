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
    private readonly BlobServiceClient _serviceClient;

    public BlobService(IConfiguration configuration, ILogger<BlobService> logger)
    {
        _logger = logger;
        string connectionString = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage connection string is not configured.");
        _serviceClient = new BlobServiceClient(connectionString);
        _containerClient = _serviceClient.GetBlobContainerClient(ContainerName);
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

    public async Task UploadBlobAsync(string containerName, string blobPath, Stream content, string contentType, CancellationToken ct)
    {
        BlobContainerClient container = _serviceClient.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(Azure.Storage.Blobs.Models.PublicAccessType.None, cancellationToken: ct);
        BlobClient blobClient = container.GetBlobClient(blobPath);
        Azure.Storage.Blobs.Models.BlobUploadOptions options = new()
        {
            HttpHeaders = new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = contentType },
            AccessTier = Azure.Storage.Blobs.Models.AccessTier.Hot,
        };
        await blobClient.UploadAsync(content, options, ct);
        _logger.LogInformation("Uploaded blob {BlobPath} to container {Container}", blobPath, containerName);
    }

    public Uri GenerateSasDownloadUrl(string containerName, string blobPath, TimeSpan ttl)
    {
        BlobContainerClient container = _serviceClient.GetBlobContainerClient(containerName);
        BlobClient blobClient = container.GetBlobClient(blobPath);
        BlobSasBuilder sasBuilder = new()
        {
            BlobContainerName = containerName,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(ttl),
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);
        Uri sasUri = blobClient.GenerateSasUri(sasBuilder);
        _logger.LogInformation("Generated SAS download URL for blob {BlobPath}", blobPath);
        return sasUri;
    }
}
