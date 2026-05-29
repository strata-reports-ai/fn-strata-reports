# CLAUDE.md — fn-strata-reports coding standards

## Project overview

Azure Functions isolated worker (.NET 9) backend for StrataReport AI. PostgreSQL via EF Core + Dapper.

## Build must pass before committing

Always run `dotnet build StrataReports.Functions.csproj --configuration Release` and fix **all** errors before committing. Do not commit code that does not compile.

## Available NuGet packages

Only use packages that are already declared in `StrataReports.Functions.csproj`. Do NOT add new packages without also editing the csproj `<PackageReference>` list. Current packages:

- `Microsoft.Azure.Functions.Worker` 2.52.0 + extensions
- `Microsoft.EntityFrameworkCore` 9.0.4 + `Npgsql.EntityFrameworkCore.PostgreSQL`
- `Dapper` 2.1.66
- `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`
- `OpenTelemetry` stack (Azure Monitor exporter)
- `Microsoft.IdentityModel.Tokens` 8.4.0 + `System.IdentityModel.Tokens.Jwt` 8.4.0 (JWT signing/validation)

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

## IMPORTANT: This project uses FunctionsApplication.CreateBuilder (new-style host)

This project uses `FunctionsApplication.CreateBuilder`, NOT the old `HostBuilder` pattern. The existing `Program.cs` uses this pattern:

```csharp
var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Register services on builder.Services:
builder.Services.AddDbContext<AppDbContext>(...);
builder.Services.AddSingleton<IEmailService, NoOpEmailService>();

builder.Build().Run();
```

### Adding middleware

Call `builder.UseMiddleware<T>()` directly on the builder — do NOT use `ConfigureFunctionsWorkerDefaults` or pass a lambda to `ConfigureFunctionsWebApplication`, those are the old `IHostBuilder` API and will not compile here.

```csharp
// CORRECT:
builder.UseMiddleware<TenantMiddleware>();

// WRONG — will not compile (old IHostBuilder pattern):
var host = new HostBuilder().ConfigureFunctionsWorkerDefaults(w => w.UseMiddleware<TenantMiddleware>()).Build();

// WRONG — will not compile (wrong overload):
builder.ConfigureFunctionsWebApplication(worker => { worker.UseMiddleware<TenantMiddleware>(); });
```

`IFunctionsWorkerMiddleware` (not `IMiddleware`) is the correct interface:

```csharp
public class TenantMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        await next(context);
    }
}
```

To execute raw SQL in middleware, inject `AppDbContext` and call:
```csharp
await db.Database.ExecuteSqlRawAsync("SET app.current_tenant_id = {0}", tenantId);
```
Requires `using Microsoft.EntityFrameworkCore;`.

`CookieOptions` is a **class** (not a record). Do NOT use `with { }` syntax on it:
```csharp
// Wrong:
var opts = existingOpts with { Expires = DateTimeOffset.UtcNow.AddDays(7) };
// Correct:
var opts = new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.AddDays(7) };
```

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