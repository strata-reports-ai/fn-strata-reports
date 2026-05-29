using Microsoft.Extensions.Logging;

namespace StrataReports.Functions.Infrastructure;

// TODO: replace with real email service when ACS/Postmark is configured
public class NoOpEmailService(ILogger<NoOpEmailService> logger) : IEmailService
{
    public Task SendVerificationEmailAsync(string to, string token, CancellationToken ct = default)
    {
        logger.LogInformation("NoOpEmailService: would send verification email to {Email} with token {Token}", to, token);
        return Task.CompletedTask;
    }
}
