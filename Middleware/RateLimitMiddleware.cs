using System.Collections.Concurrent;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace StrataReports.Functions.Middleware;

public class RateLimitMiddleware(ILogger<RateLimitMiddleware> logger) : IFunctionsWorkerMiddleware
{
    // WARNING: _entries is process-local. On multi-instance Azure Functions deployments each
    // instance maintains independent state, so the effective per-IP limit is MaxAttempts * N
    // instances. Replace with a distributed store (e.g. Redis via Azure Cache) for
    // production-strength rate limiting across all instances.
    private static readonly ConcurrentDictionary<string, IpRateEntry> _entries = new();

    private const int MaxAttempts = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        HttpRequestData? request = await context.GetHttpRequestDataAsync();

        if (request is not null && IsAuthRoute(request.Url.AbsolutePath))
        {
            string ip = GetClientIp(request);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            foreach (string key in _entries.Keys)
            {
                if (_entries.TryGetValue(key, out IpRateEntry? stale) && now - stale.WindowStart > Window)
                    _entries.TryRemove(key, out _);
            }

            IpRateEntry entry = _entries.AddOrUpdate(
                ip,
                _ => new IpRateEntry(now, 1),
                (_, existing) =>
                {
                    if (now - existing.WindowStart > Window)
                        return new IpRateEntry(now, 1);
                    return existing with { Count = existing.Count + 1 };
                });

            if (entry.Count > MaxAttempts)
            {
                logger.LogWarning("Rate limit exceeded for IP {Ip} on {Path}", ip, request.Url.AbsolutePath);

                HttpResponseData response = request.CreateResponse(HttpStatusCode.TooManyRequests);
                DateTimeOffset retryAfter = entry.WindowStart.Add(Window);
                response.Headers.Add("Retry-After", ((int)(retryAfter - now).TotalSeconds).ToString());
                response.Headers.Add("Content-Type", "application/json");
                await response.WriteStringAsync("{\"error\":\"Rate limit exceeded. Try again later.\"}");

                context.GetInvocationResult().Value = response;
                return;
            }
        }

        await next(context);
    }

    private static bool IsAuthRoute(string path)
    {
        return path.Contains("/api/auth/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetClientIp(HttpRequestData request)
    {
        // X-Azure-ClientIP is set by Azure Front Door / APIM and cannot be spoofed by clients.
        // Prefer this over X-Forwarded-For which is attacker-controlled.
        if (request.Headers.TryGetValues("X-Azure-ClientIP", out IEnumerable<string>? azureIp))
        {
            string? ip = azureIp.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(ip))
                return ip.Trim();
        }

        // Fallback: X-Forwarded-For is not trusted for security decisions because clients
        // can set arbitrary values. Only use it as a last resort for non-security purposes.
        if (request.Headers.TryGetValues("X-Forwarded-For", out IEnumerable<string>? forwarded))
        {
            string? first = forwarded.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
                return first.Split(',')[0].Trim();
        }

        return "unknown";
    }

    private sealed record IpRateEntry(DateTimeOffset WindowStart, int Count);
}
