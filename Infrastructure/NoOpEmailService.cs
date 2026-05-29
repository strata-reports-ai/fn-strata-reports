using Microsoft.Extensions.Logging;

namespace StrataReports.Functions.Infrastructure;

// TODO: replace with real email service when ACS/Postmark is configured
public class NoOpEmailService(ILogger<NoOpEmailService> logger) : IEmailService
{
    public Task SendVerificationEmailAsync(string to, string token, CancellationToken ct = default)
    {
        logger.LogInformation("NoOpEmailService: would send verification email to {Email}", to);
        return Task.CompletedTask;
    }
<<<<<<< HEAD

    public Task SendPasswordResetEmailAsync(string to, string token, CancellationToken ct = default)
    {
        logger.LogInformation("NoOpEmailService: would send password reset email to {Email}", to);
        return Task.CompletedTask;
    }
=======
>>>>>>> dd79787 (Bug: add /api/auth/logout to unauthenticated routes in TenantMiddleware (#5))
}
