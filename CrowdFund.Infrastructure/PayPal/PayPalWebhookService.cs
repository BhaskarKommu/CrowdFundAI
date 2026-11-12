using System.Globalization;
using System.Text.Json;
using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrowdFund.Infrastructure.PayPal;

/// <summary>
/// Validates, deduplicates, routes, and retries PayPal webhook events.
/// </summary>
public class PayPalWebhookService
{
    public static readonly string[] SupportedEventTypes =
    {
        "PAYMENT.CAPTURE.COMPLETED",
        "PAYMENT.CAPTURE.PENDING",
        "PAYMENT.CAPTURE.DENIED",
        "PAYMENT.CAPTURE.REFUNDED",
        "PAYMENT.AUTHORIZATION.CREATED",
        "PAYMENT.AUTHORIZATION.VOIDED",
        "CHECKOUT.ORDER.APPROVED",
        "CHECKOUT.ORDER.COMPLETED",
        "BILLING.SUBSCRIPTION.CREATED",
        "BILLING.SUBSCRIPTION.ACTIVATED",
        "BILLING.SUBSCRIPTION.CANCELLED",
        "BILLING.SUBSCRIPTION.SUSPENDED",
        "BILLING.SUBSCRIPTION.PAYMENT.FAILED",
        "BILLING.SUBSCRIPTION.PAYMENT.COMPLETED",
        "CUSTOMER.DISPUTE.CREATED",
        "CUSTOMER.DISPUTE.RESOLVED"
    };

    private readonly AppDbContext _db;
    private readonly PayPalClient _paypal;
    private readonly ILogger<PayPalWebhookService> _log;

    public PayPalWebhookService(AppDbContext db, PayPalClient paypal, ILogger<PayPalWebhookService> log)
    {
        _db = db;
        _paypal = paypal;
        _log = log;
    }

    public async Task<WebhookIngestResult> IngestAsync(
        string body,
        string transmissionId,
        string transmissionTime,
        string transmissionSig,
        string certUrl,
        string authAlgo,
        string? webhookId,
        Func<AppDbContext, WebhookEvent, JsonElement, Task>? onRouted = null,
        CancellationToken ct = default)
    {
        string eventId;
        string eventType;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
            eventId = doc.RootElement.TryGetProperty("id", out var idEl)
                ? idEl.GetString() ?? Guid.NewGuid().ToString("N")
                : Guid.NewGuid().ToString("N");
            eventType = doc.RootElement.TryGetProperty("event_type", out var et)
                ? et.GetString() ?? "UNKNOWN"
                : "UNKNOWN";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Invalid webhook JSON");
            return new WebhookIngestResult(false, false, "invalid_payload", null, "Payload is not valid JSON");
        }

        // Duplicate prevention
        var existing = await _db.WebhookEvents.AsNoTracking()
            .FirstOrDefaultAsync(w => w.PayPalEventId == eventId, ct);
        if (existing != null)
        {
            doc.Dispose();
            return new WebhookIngestResult(true, true, existing.ProcessingStatus, existing.Id, "Duplicate event ignored");
        }

        var resourceId = TryGetResourceId(doc.RootElement);
        var we = new WebhookEvent
        {
            PayPalEventId = eventId,
            EventType = eventType,
            Payload = body,
            ReceivedAt = DateTime.UtcNow,
            ProcessingStatus = "Received",
            ResourceId = resourceId,
            AttemptCount = 0
        };
        _db.WebhookEvents.Add(we);
        await _db.SaveChangesAsync(ct);

        var verified = await _paypal.VerifyWebhookSignatureAsync(
            transmissionId, transmissionTime, transmissionSig, certUrl, authAlgo, body, webhookId, ct);
        we.SignatureValid = verified || _paypal.IsSimulated;
        if (!we.SignatureValid)
        {
            we.ProcessingStatus = "Failed";
            we.LastError = "Signature verification failed";
            we.AttemptCount = 1;
            await _db.SaveChangesAsync(ct);
            doc.Dispose();
            return new WebhookIngestResult(false, false, "Failed", we.Id, "Unauthorized signature");
        }

        try
        {
            we.AttemptCount = 1;
            if (onRouted != null)
                await onRouted(_db, we, doc.RootElement);
            we.ProcessingStatus = "Processed";
            we.ProcessedAt = DateTime.UtcNow;
            we.LastError = null;
            we.NextRetryAt = null;
            await _db.SaveChangesAsync(ct);
            doc.Dispose();
            return new WebhookIngestResult(true, false, "Processed", we.Id, eventType);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Webhook processing failed for {EventId} {Type}", eventId, eventType);
            we.ProcessingStatus = "Failed";
            we.LastError = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
            we.NextRetryAt = DateTime.UtcNow.AddMinutes(5);
            await _db.SaveChangesAsync(ct);
            doc.Dispose();
            return new WebhookIngestResult(false, false, "Failed", we.Id, ex.Message);
        }
    }

    public async Task<int> RetryFailedAsync(
        Func<AppDbContext, WebhookEvent, JsonElement, Task> onRouted,
        int max = 20,
        CancellationToken ct = default)
    {
        var due = await _db.WebhookEvents
            .Where(w => w.ProcessingStatus == "Failed" && w.AttemptCount < 5 &&
                        (w.NextRetryAt == null || w.NextRetryAt <= DateTime.UtcNow))
            .OrderBy(w => w.ReceivedAt)
            .Take(max)
            .ToListAsync(ct);

        var ok = 0;
        foreach (var we in due)
        {
            we.AttemptCount += 1;
            try
            {
                using var doc = JsonDocument.Parse(we.Payload);
                await onRouted(_db, we, doc.RootElement);
                we.ProcessingStatus = "Processed";
                we.ProcessedAt = DateTime.UtcNow;
                we.LastError = null;
                we.NextRetryAt = null;
                ok++;
            }
            catch (Exception ex)
            {
                we.LastError = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
                we.NextRetryAt = DateTime.UtcNow.AddMinutes(Math.Min(60, 5 * we.AttemptCount));
                if (we.AttemptCount >= 5)
                    we.ProcessingStatus = "DeadLetter";
            }
            await _db.SaveChangesAsync(ct);
        }
        return ok;
    }

    public static string? TryGetResourceId(JsonElement root)
    {
        if (!root.TryGetProperty("resource", out var resource)) return null;
        if (resource.TryGetProperty("id", out var id)) return id.GetString();
        return null;
    }

    public static decimal? TryGetAmount(JsonElement resource)
    {
        if (resource.TryGetProperty("amount", out var amt) && amt.TryGetProperty("value", out var val))
        {
            if (decimal.TryParse(val.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        if (resource.TryGetProperty("seller_receivable_breakdown", out var br) &&
            br.TryGetProperty("gross_amount", out var gross) && gross.TryGetProperty("value", out var gv))
        {
            if (decimal.TryParse(gv.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    public static string? TryGetCustomOrRef(JsonElement resource)
    {
        if (resource.TryGetProperty("custom_id", out var c)) return c.GetString();
        if (resource.TryGetProperty("supplementary_data", out var sup) &&
            sup.TryGetProperty("related_ids", out var rel) &&
            rel.TryGetProperty("order_id", out var oid))
            return oid.GetString();
        return null;
    }
}

public record WebhookIngestResult(bool Success, bool Duplicate, string Status, Guid? EventId, string Message);
