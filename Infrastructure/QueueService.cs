using System.Collections.Concurrent;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace StrataReports.Functions.Infrastructure;

public class QueueService : IQueueService
{
    private readonly ILogger<QueueService> _logger;
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, QueueClient> _clients = new();

    public QueueService(IConfiguration configuration, ILogger<QueueService> logger)
    {
        _logger = logger;
        _connectionString = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage connection string is not configured.");
    }

    public async Task EnqueueAsync(string queueName, string message, CancellationToken ct)
    {
        QueueClient client = _clients.GetOrAdd(queueName, name => new QueueClient(_connectionString, name, new QueueClientOptions
        {
            MessageEncoding = QueueMessageEncoding.Base64,
        }));

        await client.CreateIfNotExistsAsync(cancellationToken: ct);
        await client.SendMessageAsync(message, cancellationToken: ct);

        _logger.LogInformation("Enqueued message to queue {QueueName}", queueName);
    }
}
