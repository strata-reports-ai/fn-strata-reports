namespace StrataReports.Functions.Infrastructure;

public interface IEmailService
{
    Task SendVerificationEmailAsync(string to, string token, CancellationToken ct = default);
<<<<<<< HEAD
    Task SendPasswordResetEmailAsync(string to, string token, CancellationToken ct = default);
=======
>>>>>>> dd79787 (Bug: add /api/auth/logout to unauthenticated routes in TenantMiddleware (#5))
}
