using System.Net;
using System.Security.Claims;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StrataReports.Functions.Infrastructure;

namespace StrataReports.Functions.Middleware;

public class TenantMiddleware(ILogger<TenantMiddleware> logger) : IFunctionsWorkerMiddleware
{
    private static readonly HashSet<string> _unauthenticatedRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/register",
        "/api/auth/login",
        "/api/auth/verify-email",
        "/api/auth/refresh",
        "/api/auth/logout",
        "/api/auth/reset-password",
        "/api/health",
    };

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        HttpRequestData? request = await context.GetHttpRequestDataAsync();

        if (request is null || IsUnauthenticatedRoute(request.Url.AbsolutePath))
        {
            await next(context);
            return;
        }

        string? accessToken = GetAccessTokenFromCookie(request);

        if (string.IsNullOrEmpty(accessToken))
        {
            HttpResponseData response = request.CreateResponse(HttpStatusCode.Unauthorized);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync("{\"error\":\"Authentication required.\"}");
            context.GetInvocationResult().Value = response;
            return;
        }

        IJwtService jwtService = (IJwtService)context.InstanceServices.GetService(typeof(IJwtService))!;
        ClaimsPrincipal? principal = jwtService.ValidateAccessToken(accessToken);

        if (principal is null)
        {
            HttpResponseData response = request.CreateResponse(HttpStatusCode.Unauthorized);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync("{\"error\":\"Invalid or expired token.\"}");
            context.GetInvocationResult().Value = response;
            return;
        }

        string? tenantIdClaim = principal.FindFirstValue("tenant_id");
        if (!Guid.TryParse(tenantIdClaim, out Guid tenantId))
        {
            logger.LogWarning("JWT is missing or has invalid tenant_id claim");
            HttpResponseData response = request.CreateResponse(HttpStatusCode.Unauthorized);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync("{\"error\":\"Invalid token claims.\"}");
            context.GetInvocationResult().Value = response;
            return;
        }

        context.Items["TenantId"] = tenantId;
        context.Items["UserId"] = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? string.Empty;
        context.Items["ClaimsPrincipal"] = principal;

        AppDbContext db = (AppDbContext)context.InstanceServices.GetService(typeof(AppDbContext))!;
        await db.Database.ExecuteSqlRawAsync("SET app.current_tenant_id = {0}", tenantId.ToString());

        await next(context);
    }

    private static bool IsUnauthenticatedRoute(string path)
    {
        foreach (string route in _unauthenticatedRoutes)
        {
            if (path.Equals(route, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? GetAccessTokenFromCookie(HttpRequestData request)
    {
        if (request.Cookies is null)
            return null;

        foreach (IHttpCookie cookie in request.Cookies)
        {
            if (cookie.Name == "access_token")
                return cookie.Value;
        }

        return null;
    }
}
