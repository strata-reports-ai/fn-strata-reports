namespace StrataReports.Functions.Infrastructure;

public interface IQueueService
{
    Task EnqueueAsync(string queueName, string message, CancellationToken ct);
}
