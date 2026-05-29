namespace StrataReports.Functions.Infrastructure;

public interface IEmailService
{
    Task SendVerificationEmailAsync(string to, string token, CancellationToken ct = default);
    Task SendPasswordResetEmailAsync(string to, string token, CancellationToken ct = default);
}
