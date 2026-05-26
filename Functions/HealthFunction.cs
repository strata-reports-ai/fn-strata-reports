using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace StrataReports.Functions.Functions;

public class HealthFunction
{
    [Function(nameof(HealthFunction))]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req)
    {
        return new OkObjectResult(new { status = "healthy", timestamp = DateTimeOffset.UtcNow });
    }
}
