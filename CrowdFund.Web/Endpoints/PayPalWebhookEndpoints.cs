using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using CrowdFund.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrowdFund.Web.Endpoints;

public static class PayPalWebhookEndpoints
{
    public static void MapPayPalWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/paypal/webhook", ReceiveWebhook).RequireRateLimiting("strict");
        app.MapPost("/api/webhooks/paypal", ReceiveWebhook).RequireRateLimiting("strict"); // alias
        app.MapGet("/api/paypal/webhook/events", SupportedEvents);
        app.MapGet("/api/admin/webhooks", ListLogs).RequireAuthorization();
        app.MapGet("/api/admin/webhooks/{id:guid}", GetLog).RequireAuthorization();
        app.MapPost("/api/admin/webhooks/retry-failed", RetryFailed).RequireAuthorization();
        app.MapGet("/api/admin/webhooks/registration-guide", RegistrationGuide);
    }

    private static IResult SupportedEvents() => Results.Ok(new
    {
        url = "/api/paypal/webhook",
        events = PayPalWebhookService.SupportedEventTypes
    });

    private static async Task<IResult> ReceiveWebhook(
        HttpRequest request,
        PayPalWebhookService webhookService,
        PayPalWebhookRouter router,
        IOptions<PayPalOptions> opts)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();

        var transmissionId = request.Headers["PAYPAL-TRANSMISSION-ID"].FirstOrDefault() ?? "";
        var timestamp = request.Headers["PAYPAL-TRANSMISSION-TIME"].FirstOrDefault() ?? "";
        var certUrl = request.Headers["PAYPAL-CERT-URL"].FirstOrDefault() ?? "";
        var authAlgo = request.Headers["PAYPAL-AUTH-ALGO"].FirstOrDefault() ?? "";
        var transmissionSig = request.Headers["PAYPAL-TRANSMISSION-SIG"].FirstOrDefault() ?? "";
        var webhookId = request.Headers["PAYPAL-WEBHOOK-ID"].FirstOrDefault()
                        ?? opts.Value.WebhookId;

        var result = await webhookService.IngestAsync(
            body, transmissionId, timestamp, transmissionSig, certUrl, authAlgo, webhookId,
            router.RouteAsync);

        if (!result.Success && result.Message == "Unauthorized signature")
            return Results.Unauthorized();

        return Results.Ok(new
        {
            status = result.Status.ToLowerInvariant(),
            duplicate = result.Duplicate,
            eventId = result.EventId,
            message = result.Message
        });
    }

    private static async Task<IResult> ListLogs(
        HttpContext http, AppDbContext db, [FromQuery] string? status = null, [FromQuery] int take = 50)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var q = db.WebhookEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(w => w.ProcessingStatus == status);
        var list = await q.OrderByDescending(w => w.ReceivedAt).Take(Math.Clamp(take, 1, 200))
            .Select(w => new
            {
                w.Id, w.PayPalEventId, w.EventType, w.ProcessingStatus, w.SignatureValid,
                w.AttemptCount, w.ResourceId, w.RelatedEntityType, w.RelatedEntityId,
                w.ReceivedAt, w.ProcessedAt, w.LastError, w.NextRetryAt
            }).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> GetLog(Guid id, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var w = await db.WebhookEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return w == null ? Results.NotFound() : Results.Ok(w);
    }

    private static async Task<IResult> RetryFailed(
        HttpContext http, PayPalWebhookService svc, PayPalWebhookRouter router)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var n = await svc.RetryFailedAsync(router.RouteAsync);
        return Results.Ok(new { retriedOk = n });
    }

    private static IResult RegistrationGuide(IOptions<PayPalOptions> opts, HttpContext http)
    {
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
        return Results.Ok(new
        {
            message = "Register this URL in PayPal Developer Dashboard → Webhooks",
            webhookUrl = $"{baseUrl}/api/paypal/webhook",
            alternateUrl = $"{baseUrl}/api/webhooks/paypal",
            mode = opts.Value.Mode,
            webhookIdConfigured = !string.IsNullOrWhiteSpace(opts.Value.WebhookId),
            eventTypesToSubscribe = PayPalWebhookService.SupportedEventTypes,
            env = "Set PayPal__WebhookId after registration for signature verification"
        });
    }
}
