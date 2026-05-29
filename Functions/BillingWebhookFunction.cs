using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using StrataReports.Functions.Infrastructure;
using StrataReports.Functions.Models;

namespace StrataReports.Functions.Functions;

public class BillingWebhookFunction(
    ILogger<BillingWebhookFunction> logger,
    AppDbContext db,
    IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Function("BillingWebhook")]
    public async Task<HttpResponseData> HandleWebhook(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "billing/webhook")] HttpRequestData req,
        CancellationToken ct)
    {
        string body = await new StreamReader(req.Body).ReadToEndAsync(ct);

        if (!req.Headers.TryGetValues("Stripe-Signature", out IEnumerable<string>? sigValues))
        {
            logger.LogWarning("Stripe webhook received without Stripe-Signature header");
            return await BadRequest(req, "Missing Stripe-Signature header.");
        }

        string? webhookSecret = configuration["Stripe__WebhookSecret"];

        if (string.IsNullOrEmpty(webhookSecret))
        {
            logger.LogError("Stripe__WebhookSecret is not configured; cannot validate Stripe-Signature");
            return await BadRequest(req, "Invalid Stripe signature.");
        }

        string stripeSignature = sigValues.FirstOrDefault() ?? string.Empty;

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(body, stripeSignature, webhookSecret);
        }
        catch (StripeException ex)
        {
            logger.LogWarning("Stripe webhook signature validation failed: {Message}", ex.Message);
            return await BadRequest(req, "Invalid Stripe signature.");
        }

        logger.LogInformation("Stripe webhook received: {EventType} id={EventId}", stripeEvent.Type, stripeEvent.Id);

        switch (stripeEvent.Type)
        {
            case EventTypes.CheckoutSessionCompleted:
                await HandleCheckoutSessionCompleted(stripeEvent, ct);
                break;

            case EventTypes.CustomerSubscriptionUpdated:
                await HandleSubscriptionUpdated(stripeEvent, ct);
                break;

            case EventTypes.CustomerSubscriptionDeleted:
                await HandleSubscriptionDeleted(stripeEvent, ct);
                break;

            case EventTypes.InvoicePaymentFailed:
                await HandleInvoicePaymentFailed(stripeEvent, ct);
                break;

            default:
                logger.LogInformation("Unhandled Stripe event type: {EventType}", stripeEvent.Type);
                break;
        }

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync("{\"received\":true}");
        return response;
    }

    private async Task HandleCheckoutSessionCompleted(Event stripeEvent, CancellationToken ct)
    {
        if (stripeEvent.Data.Object is not Stripe.Checkout.Session session)
        {
            logger.LogWarning("checkout.session.completed: event data is not a Session");
            return;
        }

        string? stripeCustomerId = session.CustomerId;
        string? tenantIdMeta = session.Metadata?.GetValueOrDefault("tenant_id");

        if (!Guid.TryParse(tenantIdMeta, out Guid tenantId))
        {
            logger.LogWarning("checkout.session.completed: missing or invalid tenant_id metadata on session {SessionId}", session.Id);
            return;
        }

        Tenant? tenant = await db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null)
        {
            logger.LogWarning("checkout.session.completed: tenant {TenantId} not found", tenantId);
            return;
        }

        string planMeta = session.Metadata?.GetValueOrDefault("plan") ?? string.Empty;
        string plan = MapPlanName(planMeta);

        tenant.StripeCustomerId = stripeCustomerId;
        tenant.Plan = plan;
        tenant.Status = "active";
        tenant.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "checkout.session.completed: tenant {TenantId} plan={Plan} stripeCustomerId={CustomerId}",
            tenantId, plan, stripeCustomerId);
    }

    private async Task HandleSubscriptionUpdated(Event stripeEvent, CancellationToken ct)
    {
        if (stripeEvent.Data.Object is not Subscription subscription)
        {
            logger.LogWarning("customer.subscription.updated: event data is not a Subscription");
            return;
        }

        string? tenantIdMeta = subscription.Metadata?.GetValueOrDefault("tenant_id");

        if (!Guid.TryParse(tenantIdMeta, out Guid tenantId))
        {
            logger.LogWarning(
                "customer.subscription.updated: missing or invalid tenant_id metadata on subscription {SubId}",
                subscription.Id);
            return;
        }

        Tenant? tenant = await db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null)
        {
            logger.LogWarning("customer.subscription.updated: tenant {TenantId} not found", tenantId);
            return;
        }

        string planMeta = subscription.Metadata?.GetValueOrDefault("plan") ?? string.Empty;
        string priceId = subscription.Items?.Data?.FirstOrDefault()?.Price?.Id ?? string.Empty;
        string plan = string.IsNullOrEmpty(planMeta) ? MapPriceIdToPlan(priceId) : MapPlanName(planMeta);
        string status = MapSubscriptionStatus(subscription.Status);

        tenant.Plan = plan;
        tenant.Status = status;
        tenant.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "customer.subscription.updated: tenant {TenantId} plan={Plan} status={Status}",
            tenantId, plan, status);
    }

    private async Task HandleSubscriptionDeleted(Event stripeEvent, CancellationToken ct)
    {
        if (stripeEvent.Data.Object is not Subscription subscription)
        {
            logger.LogWarning("customer.subscription.deleted: event data is not a Subscription");
            return;
        }

        string? tenantIdMeta = subscription.Metadata?.GetValueOrDefault("tenant_id");

        if (!Guid.TryParse(tenantIdMeta, out Guid tenantId))
        {
            logger.LogWarning(
                "customer.subscription.deleted: missing or invalid tenant_id metadata on subscription {SubId}",
                subscription.Id);
            return;
        }

        Tenant? tenant = await db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null)
        {
            logger.LogWarning("customer.subscription.deleted: tenant {TenantId} not found", tenantId);
            return;
        }

        tenant.Status = "cancelled";
        tenant.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("customer.subscription.deleted: tenant {TenantId} status=cancelled", tenantId);
    }

    private async Task HandleInvoicePaymentFailed(Event stripeEvent, CancellationToken ct)
    {
        if (stripeEvent.Data.Object is not Invoice invoice)
        {
            logger.LogWarning("invoice.payment_failed: event data is not an Invoice");
            return;
        }

        string? stripeCustomerId = invoice.CustomerId;
        if (string.IsNullOrEmpty(stripeCustomerId))
        {
            logger.LogWarning("invoice.payment_failed: invoice has no customer ID");
            return;
        }

        Tenant? tenant = await db.Tenants
            .FirstOrDefaultAsync(t => t.StripeCustomerId == stripeCustomerId, ct);

        if (tenant is null)
        {
            logger.LogWarning("invoice.payment_failed: no tenant found for stripeCustomerId={CustomerId}", stripeCustomerId);
            return;
        }

        tenant.Status = "past_due";
        tenant.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "invoice.payment_failed: tenant {TenantId} status=past_due", tenant.Id);
    }

    private static string MapPlanName(string planName)
    {
        return planName.ToLowerInvariant() switch
        {
            "starter" => "starter",
            "pro" => "pro",
            "scale" => "scale",
            _ => "starter",
        };
    }

    private static string MapPriceIdToPlan(string priceId)
    {
        return priceId switch
        {
            var p when p.Contains("starter", StringComparison.OrdinalIgnoreCase) => "starter",
            var p when p.Contains("pro", StringComparison.OrdinalIgnoreCase) => "pro",
            var p when p.Contains("scale", StringComparison.OrdinalIgnoreCase) => "scale",
            _ => "starter",
        };
    }

    private static string MapSubscriptionStatus(string stripeStatus)
    {
        return stripeStatus switch
        {
            "active" or "trialing" => "active",
            "past_due" => "past_due",
            "canceled" or "cancelled" or "unpaid" or "incomplete_expired" => "cancelled",
            _ => "active",
        };
    }

    private static async Task<HttpResponseData> BadRequest(HttpRequestData req, string detail)
    {
        HttpResponseData response = req.CreateResponse(HttpStatusCode.BadRequest);
        response.Headers.Add("Content-Type", "application/problem+json");
        object payload = new
        {
            type = "about:blank",
            title = "Bad Request",
            status = 400,
            detail,
        };
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
