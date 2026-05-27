# CLAUDE.md — fn-strata-reports coding standards

## Project overview

Azure Functions isolated worker (.NET 9) backend for StrataReport AI. PostgreSQL via EF Core + Dapper.

## ⚠ Build must pass before committing

Always run `dotnet build StrataReports.Functions.csproj --configuration Release` and fix **all** errors before committing. Do not commit code that does not compile.

## Available NuGet packages

Only use packages that are already declared in `StrataReports.Functions.csproj`. Do NOT add new packages without also editing the csproj `<PackageReference>` list. Current packages:

- `Microsoft.Azure.Functions.Worker` 2.52.0 + extensions
- `Microsoft.EntityFrameworkCore` 9.0.4 + `Npgsql.EntityFrameworkCore.PostgreSQL`
- `Dapper` 2.1.66
- `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`
- `OpenTelemetry` stack (Azure Monitor exporter)

**No Postmark, no SendGrid, no ACS — email is not yet wired up.**

## Email sending

Email is not yet configured in this project. When an issue requires sending email:

1. Define an `IEmailService` interface with methods like `SendVerificationEmailAsync(string to, string token)`.
2. Implement a `NoOpEmailService` that logs the call but does nothing.
3. Register `services.AddSingleton<IEmailService, NoOpEmailService>()` in `Program.cs`.
4. Leave a `// TODO: replace with real email service when ACS/Postmark is configured` comment.

Do NOT reference `PostmarkClient`, `PostmarkSharp`, `SendGridClient`, or any external email SDK.

## Microsoft Entra External ID

Secrets `ENTRA_CLIENT_ID` and `ENTRA_TENANT_ID` will be available as environment variables (Azure Key Vault references). Read them via `configuration["ENTRA_CLIENT_ID"]`. Do not hard-code tenant or client IDs.

## Authentication pattern

- JWT stored in `httpOnly` + `Secure` + `SameSite=Lax` cookies.
- Access token TTL: 15 minutes. Refresh token: 7 days, rotated on each use.
- Refresh token revocation: store a `refresh_tokens` table with `jti` (GUID) + `revoked_at` column.
- Middleware sets the PostgreSQL session variable `app.current_tenant_id` on every authenticated connection.

## Database access

- Use `AppDbContext` (EF Core) for writes and complex queries.
- Use `IDbConnectionFactory` (Dapper) for read-heavy or raw SQL queries.
- Never bypass RLS. `TenantMiddleware` must run on all authenticated endpoints.

## Function conventions

- One HTTP-triggered function class per logical group (e.g. `AuthFunction`, `PropertiesFunction`).
- Route prefix: `/api/{resource}`.
- Return `Results.Ok(...)`, `Results.BadRequest(...)`, etc. via `IActionResult` or minimal API results.
- Log via the injected `ILogger<T>`, not `Console.WriteLine`.

## C# style

- Use primary constructors for DI (e.g. `public class AuthFunction(ILogger<AuthFunction> logger, AppDbContext db)`).
- Enable nullable: treat all warnings as potential bugs; initialise variables before use.
- No `var` for non-obvious types. Explicit types on public members.
- No bare `catch (Exception)` — catch specific exception types.
