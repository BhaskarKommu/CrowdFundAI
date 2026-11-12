using System.Text.Json;
using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using CrowdFund.Web.Endpoints;
using CrowdFund.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Services;

/// <summary>Maps PayPal event types to domain state updates.</summary>
public class PayPalWebhookRouter
{
    private readonly IHubContext<CampaignHub> _hub;
    private readonly ILogger<PayPalWebhookRouter> _log;

    public PayPalWebhookRouter(IHubContext<CampaignHub> hub, ILogger<PayPalWebhookRouter> log)
    {
        _hub = hub;
        _log = log;
    }

    public async Task RouteAsync(AppDbContext db, WebhookEvent we, JsonElement root)
    {
        if (!root.TryGetProperty("resource", out var resource))
        {
            _log.LogWarning("Webhook {Type} missing resource", we.EventType);
            return;
        }

        switch (we.EventType)
        {
            case "PAYMENT.CAPTURE.COMPLETED":
                await OnCaptureCompleted(db, we, resource);
                break;
            case "PAYMENT.CAPTURE.PENDING":
                await OnCapturePending(db, we, resource);
                break;
            case "PAYMENT.CAPTURE.DENIED":
                await OnCaptureDenied(db, we, resource);
                break;
            case "PAYMENT.CAPTURE.REFUNDED":
                await OnCaptureRefunded(db, we, resource);
                break;
            case "PAYMENT.AUTHORIZATION.CREATED":
                await OnAuthorizationCreated(db, we, resource);
                break;
            case "PAYMENT.AUTHORIZATION.VOIDED":
                await OnAuthorizationVoided(db, we, resource);
                break;
            case "CHECKOUT.ORDER.APPROVED":
                await OnOrderApproved(db, we, resource);
                break;
            case "CHECKOUT.ORDER.COMPLETED":
                await OnOrderCompleted(db, we, resource);
                break;
            case "BILLING.SUBSCRIPTION.CREATED":
                await OnSubscription(db, we, resource, SubscriptionStatus.ApprovalPending, "created");
                break;
            case "BILLING.SUBSCRIPTION.ACTIVATED":
                await OnSubscription(db, we, resource, SubscriptionStatus.Active, "activated");
                break;
            case "BILLING.SUBSCRIPTION.CANCELLED":
                await OnSubscription(db, we, resource, SubscriptionStatus.Cancelled, "cancelled");
                break;
            case "BILLING.SUBSCRIPTION.SUSPENDED":
                await OnSubscription(db, we, resource, SubscriptionStatus.Suspended, "suspended");
                break;
            case "BILLING.SUBSCRIPTION.PAYMENT.FAILED":
                await OnSubscriptionPaymentFailed(db, we, resource);
                break;
            case "BILLING.SUBSCRIPTION.PAYMENT.COMPLETED":
                await OnSubscriptionPaymentCompleted(db, we, resource);
                break;
            case "CUSTOMER.DISPUTE.CREATED":
                await OnDisputeCreated(db, we, resource);
                break;
            case "CUSTOMER.DISPUTE.RESOLVED":
                await OnDisputeResolved(db, we, resource);
                break;
            default:
                _log.LogInformation("Unhandled webhook type {Type} logged only", we.EventType);
                break;
        }
    }

    private async Task OnCaptureCompleted(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        var captureId = resource.TryGetProperty("id", out var id) ? id.GetString() : null;
        var orderId = resource.TryGetProperty("supplementary_data", out var sd) &&
                      sd.TryGetProperty("related_ids", out var rel) &&
                      rel.TryGetProperty("order_id", out var oid)
            ? oid.GetString()
            : null;
        var customId = resource.TryGetProperty("custom_id", out var c) ? c.GetString() : null;

        Donation? donation = null;
        if (Guid.TryParse(customId, out var donationId))
            donation = await db.Donations.Include(d => d.Campaign).FirstOrDefaultAsync(d => d.Id == donationId);
        donation ??= !string.IsNullOrEmpty(orderId)
            ? await db.Donations.Include(d => d.Campaign).FirstOrDefaultAsync(d => d.PayPalOrderId == orderId)
            : null;
        if (donation == null) return;

        if (donation.Status == DonationStatus.Captured && donation.PayPalCaptureId == captureId)
        {
            we.RelatedEntityType = "Donation";
            we.RelatedEntityId = donation.Id;
            return; // already applied
        }

        donation.PayPalCaptureId = captureId ?? donation.PayPalCaptureId;
        donation.Status = DonationStatus.Captured;
        donation.CapturedAt = DateTime.UtcNow;
        donation.CapturedAmount = donation.Amount;
        donation.StatusDetail = "Capture completed via webhook";
        we.RelatedEntityType = "Donation";
        we.RelatedEntityId = donation.Id;
        await db.SaveChangesAsync();
        await _hub.Clients.Group($"campaign-{donation.CampaignId}")
            .SendAsync("PaymentStatusChanged", new { donationId = donation.Id, status = "Captured", source = "webhook" });
    }

    private async Task OnCapturePending(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        var donation = await FindDonation(db, resource);
        if (donation == null) return;
        donation.Status = DonationStatus.Pending;
        donation.StatusDetail = "Capture pending (PayPal)";
        we.RelatedEntityType = "Donation";
        we.RelatedEntityId = donation.Id;
        await db.SaveChangesAsync();
        await _hub.Clients.Group($"campaign-{donation.CampaignId}")
            .SendAsync("PaymentStatusChanged", new { donationId = donation.Id, status = "Pending", source = "webhook" });
    }

    private async Task OnCaptureDenied(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        var donation = await FindDonation(db, resource);
        if (donation == null) return;
        donation.Status = DonationStatus.Failed;
        donation.FailedAt = DateTime.UtcNow;
        donation.StatusDetail = "Capture denied by PayPal";
        we.RelatedEntityType = "Donation";
        we.RelatedEntityId = donation.Id;
        await db.SaveChangesAsync();
        await _hub.Clients.Group($"campaign-{donation.CampaignId}")
            .SendAsync("PaymentStatusChanged", new { donationId = donation.Id, status = "Failed", source = "webhook" });
    }

    private async Task OnCaptureRefunded(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        var refundId = resource.TryGetProperty("id", out var rid) ? rid.GetString() : null;
        string? captureId = null;
        if (resource.TryGetProperty("links", out var links))
        {
            foreach (var link in links.EnumerateArray())
            {
                if (link.TryGetProperty("rel", out var rel) && rel.GetString() == "up" &&
                    link.TryGetProperty("href", out var href))
                {
                    var parts = (href.GetString() ?? "").Split('/');
                    captureId = parts.Length > 0 ? parts[^1] : null;
                }
            }
        }
        var amount = PayPalWebhookService.TryGetAmount(resource);
        var st = resource.TryGetProperty("status", out var stEl) ? stEl.GetString() : "COMPLETED";
        await RefundManagementEndpoints.ApplyWebhookRefundAsync(db, refundId, captureId, amount, st, _hub);
        we.RelatedEntityType = "Refund";
    }

    private async Task OnAuthorizationCreated(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        var donation = await FindDonation(db, resource);
        if (donation == null) return;
        var authId = resource.TryGetProperty("id", out var id) ? id.GetString() : null;
        donation.PayPalAuthorizationId = authId ?? donation.PayPalAuthorizationId;
        donation.Status = DonationStatus.Authorized;
        donation.AuthorizedAmount = donation.Amount;
        donation.ApprovedAt = DateTime.UtcNow;
        donation.StatusDetail = "Authorization created via webhook";
        we.RelatedEntityType = "Donation";
        we.RelatedEntityId = donation.Id;
        await db.SaveChangesAsync();
    }

    private async Task OnAuthorizationVoided(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        var donation = await FindDonation(db, resource);
        if (donation == null) return;
        donation.Status = DonationStatus.Cancelled;
        donation.CancelledAt = DateTime.UtcNow;
        donation.StatusDetail = "Authorization voided via webhook";
        we.RelatedEntityType = "Donation";
        we.RelatedEntityId = donation.Id;
        await db.SaveChangesAsync();
    }

    private async Task OnOrderApproved(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        var orderId = resource.TryGetProperty("id", out var id) ? id.GetString() : null;
        var donation = !string.IsNullOrEmpty(orderId)
            ? await db.Donations.FirstOrDefaultAsync(d => d.PayPalOrderId == orderId)
            : null;
        if (donation == null) return;
        if (donation.Status == DonationStatus.Created || donation.Status == DonationStatus.PendingApproval)
        {
            donation.Status = DonationStatus.PendingApproval;
            donation.ApprovedAt = DateTime.UtcNow;
            donation.StatusDetail = "Order approved by buyer";
        }
        we.RelatedEntityType = "Donation";
        we.RelatedEntityId = donation.Id;
        await db.SaveChangesAsync();
    }

    private async Task OnOrderCompleted(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        var orderId = resource.TryGetProperty("id", out var id) ? id.GetString() : null;
        var donation = !string.IsNullOrEmpty(orderId)
            ? await db.Donations.FirstOrDefaultAsync(d => d.PayPalOrderId == orderId)
            : null;
        if (donation == null) return;
        donation.StatusDetail = "Checkout order completed";
        we.RelatedEntityType = "Donation";
        we.RelatedEntityId = donation.Id;
        await db.SaveChangesAsync();
    }

    private async Task OnSubscription(AppDbContext db, WebhookEvent we, JsonElement resource, SubscriptionStatus status, string eventName)
    {
        var subId = resource.TryGetProperty("id", out var id) ? id.GetString() : null;
        if (string.IsNullOrEmpty(subId)) return;
        var sub = await db.RecurringDonations.FirstOrDefaultAsync(s => s.PayPalSubscriptionId == subId);
        if (sub == null) return;
        sub.Status = status;
        sub.StatusDetail = $"Webhook {we.EventType}";
        if (status == SubscriptionStatus.Active) sub.ActivatedAt = DateTime.UtcNow;
        if (status == SubscriptionStatus.Cancelled) sub.CancelledAt = DateTime.UtcNow;
        if (status == SubscriptionStatus.Suspended) sub.SuspendedAt = DateTime.UtcNow;
        db.SubscriptionBillingEvents.Add(new SubscriptionBillingEvent
        {
            RecurringDonationId = sub.Id,
            EventType = eventName,
            Status = "ok",
            Detail = we.EventType,
            PayPalTransactionId = subId
        });
        we.RelatedEntityType = "Subscription";
        we.RelatedEntityId = sub.Id;
        await db.SaveChangesAsync();
        await _hub.Clients.Group($"campaign-{sub.CampaignId}")
            .SendAsync("SubscriptionStatusChanged", new { subscriptionId = sub.Id, status = status.ToString(), source = "webhook" });
    }

    private async Task OnSubscriptionPaymentFailed(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        var subId = resource.TryGetProperty("id", out var id) ? id.GetString() : null;
        // billing_info may nest differently — also try billing_agreement_id
        subId ??= resource.TryGetProperty("billing_agreement_id", out var ba) ? ba.GetString() : null;
        var sub = !string.IsNullOrEmpty(subId)
            ? await db.RecurringDonations.FirstOrDefaultAsync(s => s.PayPalSubscriptionId == subId)
            : null;
        if (sub == null) return;
        sub.FailedPaymentCount += 1;
        sub.LastFailedPaymentAt = DateTime.UtcNow;
        sub.Status = SubscriptionStatus.PastDue;
        sub.NextRetryAt = DateTime.UtcNow.AddHours(Math.Min(72, 6 * sub.FailedPaymentCount));
        if (sub.FailedPaymentCount >= sub.MaxPaymentRetries)
        {
            sub.Status = SubscriptionStatus.Suspended;
            sub.SuspendedAt = DateTime.UtcNow;
        }
        db.SubscriptionBillingEvents.Add(new SubscriptionBillingEvent
        {
            RecurringDonationId = sub.Id,
            EventType = "failed",
            Status = "failed",
            Amount = sub.Amount,
            Currency = sub.Currency,
            Detail = we.EventType
        });
        we.RelatedEntityType = "Subscription";
        we.RelatedEntityId = sub.Id;
        await db.SaveChangesAsync();
        await _hub.Clients.Group($"campaign-{sub.CampaignId}")
            .SendAsync("SubscriptionStatusChanged", new { subscriptionId = sub.Id, status = sub.Status.ToString(), source = "webhook" });
    }

    private async Task OnSubscriptionPaymentCompleted(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        var subId = resource.TryGetProperty("billing_agreement_id", out var ba) ? ba.GetString() : null;
        subId ??= resource.TryGetProperty("id", out var id) ? id.GetString() : null;
        var sub = !string.IsNullOrEmpty(subId)
            ? await db.RecurringDonations.Include(s => s.Plan).FirstOrDefaultAsync(s => s.PayPalSubscriptionId == subId)
            : null;
        if (sub == null) return;
        sub.SuccessfulCharges += 1;
        sub.LastChargedAt = DateTime.UtcNow;
        sub.FailedPaymentCount = 0;
        sub.Status = SubscriptionStatus.Active;
        var unit = sub.Plan?.IntervalUnit ?? "MONTH";
        var count = sub.Plan?.IntervalCount ?? 1;
        sub.NextChargeAt = unit switch
        {
            "WEEK" => DateTime.UtcNow.AddDays(7 * count),
            "YEAR" => DateTime.UtcNow.AddYears(count),
            _ => DateTime.UtcNow.AddMonths(count)
        };
        db.SubscriptionBillingEvents.Add(new SubscriptionBillingEvent
        {
            RecurringDonationId = sub.Id,
            EventType = "charged",
            Status = "ok",
            Amount = PayPalWebhookService.TryGetAmount(resource) ?? sub.Amount,
            Currency = sub.Currency,
            Detail = we.EventType
        });
        we.RelatedEntityType = "Subscription";
        we.RelatedEntityId = sub.Id;
        await db.SaveChangesAsync();
        await _hub.Clients.Group($"campaign-{sub.CampaignId}")
            .SendAsync("SubscriptionStatusChanged", new { subscriptionId = sub.Id, status = "Active", source = "webhook" });
    }

    private async Task OnDisputeCreated(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        var disputeId = resource.TryGetProperty("dispute_id", out var di) ? di.GetString()
            : resource.TryGetProperty("id", out var id) ? id.GetString() : null;
        Guid? donationId = null;
        Guid campaignId = Guid.Empty;
        if (resource.TryGetProperty("disputed_transactions", out var txs) && txs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tx in txs.EnumerateArray())
            {
                var sellerTxn = tx.TryGetProperty("seller_transaction_id", out var st) ? st.GetString() : null;
                if (!string.IsNullOrEmpty(sellerTxn))
                {
                    var donation = await db.Donations.FirstOrDefaultAsync(d =>
                        d.PayPalCaptureId == sellerTxn || d.PayPalOrderId == sellerTxn);
                    if (donation != null)
                    {
                        donationId = donation.Id;
                        campaignId = donation.CampaignId;
                        break;
                    }
                }
            }
        }
        if (campaignId == Guid.Empty) return;
        db.DisputeCases.Add(new DisputeCase
        {
            DonationId = donationId,
            CampaignId = campaignId,
            Category = "PayPalDispute",
            Description = $"PayPal dispute {disputeId} created via webhook",
            Status = DisputeStatus.Open
        });
        we.RelatedEntityType = "Dispute";
        await db.SaveChangesAsync();
    }

    private async Task OnDisputeResolved(AppDbContext db, WebhookEvent we, JsonElement resource)
    {
        // Mark latest open paypal dispute for matching campaign if we can resolve
        var outcome = resource.TryGetProperty("dispute_outcome", out var o) ? o.ToString() : "resolved";
        we.RelatedEntityType = "Dispute";
        // Soft-update: find open PayPalDispute cases recently
        var open = await db.DisputeCases
            .Where(d => d.Category == "PayPalDispute" && d.Status == DisputeStatus.Open)
            .OrderByDescending(d => d.CreatedAt)
            .Take(5)
            .ToListAsync();
        foreach (var d in open)
        {
            d.Status = DisputeStatus.Closed;
            d.Description += $" | Resolved webhook: {outcome}";
        }
        await db.SaveChangesAsync();
    }

    private static async Task<Donation?> FindDonation(AppDbContext db, JsonElement resource)
    {
        var customId = resource.TryGetProperty("custom_id", out var c) ? c.GetString() : null;
        if (Guid.TryParse(customId, out var donationId))
            return await db.Donations.FirstOrDefaultAsync(d => d.Id == donationId);

        var orderId = resource.TryGetProperty("supplementary_data", out var sd) &&
                      sd.TryGetProperty("related_ids", out var rel) &&
                      rel.TryGetProperty("order_id", out var oid)
            ? oid.GetString()
            : null;
        if (!string.IsNullOrEmpty(orderId))
            return await db.Donations.FirstOrDefaultAsync(d => d.PayPalOrderId == orderId);

        var captureId = resource.TryGetProperty("id", out var id) ? id.GetString() : null;
        if (!string.IsNullOrEmpty(captureId))
            return await db.Donations.FirstOrDefaultAsync(d => d.PayPalCaptureId == captureId);
        return null;
    }
}
