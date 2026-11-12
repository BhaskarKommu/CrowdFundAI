using System.Security.Claims;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using CrowdFund.Web.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

/// <summary>
/// Full / partial / multi-partial refunds, history, reasons, status, confirmation,
/// failure handling, notifications, webhook sync.
/// </summary>
public static class RefundManagementEndpoints
{
    public static void MapRefundManagementEndpoints(this WebApplication app)
    {
        app.MapGet("/api/refunds/reasons", () => Results.Ok(
            RefundDisputeService.ReasonCatalog.Select(r => new { code = r.Code.ToString(), label = r.Label })));

        app.MapPost("/api/donations/{donationId:guid}/refunds/request", RequestRefund).RequireAuthorization();
        app.MapGet("/api/donations/{donationId:guid}/refunds", ListDonationRefunds).RequireAuthorization();
        app.MapGet("/api/refunds/{id:guid}", GetRefund).RequireAuthorization();
        app.MapGet("/api/refunds/{id:guid}/history", GetHistory).RequireAuthorization();
        app.MapPost("/api/admin/refunds/{id:guid}/process", ProcessRefund).RequireAuthorization();
        app.MapPost("/api/admin/refunds/{id:guid}/confirm", ConfirmRefund).RequireAuthorization();
        app.MapPost("/api/admin/refunds/sync-webhook", SyncFromWebhookPayload); // called from webhook handler too
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static async Task Notify(IHubContext<CampaignHub> hub, Guid campaignId, object payload)
    {
        try { await hub.Clients.Group($"campaign-{campaignId}").SendAsync("RefundStatusChanged", payload); }
        catch { }
    }

    private static async Task<IResult> RequestRefund(
        Guid donationId,
        [FromBody] RefundRequestBody req,
        HttpContext http,
        AppDbContext db,
        RefundDisputeService svc,
        IHubContext<CampaignHub> hub)
    {
        var donation = await db.Donations.FirstOrDefaultAsync(d => d.Id == donationId);
        if (donation == null) return Results.NotFound();
        if (string.IsNullOrEmpty(donation.PayPalCaptureId) && donation.Status != DonationStatus.Captured)
            return Results.BadRequest(new { error = "Donation has no capture to refund" });

        var priorRefunded = await db.RefundRequests.Where(r => r.DonationId == donationId)
            .SumAsync(r => r.RefundedAmount);
        var remaining = donation.Amount - priorRefunded;
        var amount = req.Amount <= 0 ? remaining : req.Amount;
        if (amount <= 0 || amount > remaining)
            return Results.BadRequest(new { error = "Invalid amount", remaining });

        if (!Enum.TryParse<RefundReasonCode>(req.ReasonCode ?? "DonorRequest", true, out var code))
            code = RefundReasonCode.DonorRequest;

        var freeze = req.ForceFreeze == true;
        try
        {
            var rr = svc.CreatePartialRefund(donation, amount, req.Reason, Uid(http), freeze, code);
            db.RefundRequests.Add(rr);
            await db.SaveChangesAsync();
            await Notify(hub, rr.CampaignId, new { refundId = rr.Id, status = rr.Status.ToString(), eventType = "requested" });
            return Results.Ok(new
            {
                refundId = rr.Id,
                status = rr.Status.ToString(),
                requestedAmount = rr.RequestedAmount,
                reasonCode = rr.ReasonCode.ToString(),
                reason = rr.Reason,
                remainingAfterRequest = remaining - amount
            });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ListDonationRefunds(Guid donationId, HttpContext http, AppDbContext db)
    {
        var list = await db.RefundRequests.AsNoTracking()
            .Where(r => r.DonationId == donationId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id, r.Status, r.RequestedAmount, r.RefundedAmount, r.Currency,
                r.Reason, ReasonCode = r.ReasonCode.ToString(), r.CreatedAt, r.ConfirmedAt, r.FailureReason
            }).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> GetRefund(Guid id, AppDbContext db)
    {
        var r = await db.RefundRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return Results.NotFound();
        return Results.Ok(r);
    }

    private static async Task<IResult> GetHistory(Guid id, AppDbContext db)
    {
        var rows = await db.RefundTransactions.AsNoTracking()
            .Where(t => t.RefundRequestId == id)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();
        return Results.Ok(rows);
    }

    /// <summary>Admin: execute PayPal refund (full or partial of remaining requested).</summary>
    private static async Task<IResult> ProcessRefund(
        Guid id,
        [FromBody] ProcessRefundBody? body,
        HttpContext http,
        AppDbContext db,
        RefundDisputeService svc,
        PayPalClient paypal,
        LedgerService ledger,
        IHubContext<CampaignHub> hub)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var rr = await db.RefundRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (rr == null) return Results.NotFound();
        if (rr.Status is RefundStatus.Frozen)
            return Results.BadRequest(new { error = "Unfreeze before processing" });
        if (rr.Status is RefundStatus.Rejected or RefundStatus.FullyRefunded or RefundStatus.Confirmed)
            return Results.BadRequest(new { error = $"Cannot process status {rr.Status}" });

        var donation = await db.Donations.FirstOrDefaultAsync(d => d.Id == rr.DonationId);
        if (donation == null) return Results.NotFound();
        if (string.IsNullOrEmpty(donation.PayPalCaptureId))
            return Results.BadRequest(new { error = "Missing PayPal capture id" });

        var remaining = rr.RequestedAmount - rr.RefundedAmount;
        var amount = body?.Amount is > 0 ? body.Amount.Value : remaining;
        if (amount <= 0 || amount > remaining)
            return Results.BadRequest(new { error = "Invalid process amount", remaining });

        rr.Status = RefundStatus.Processing;
        var tx = svc.BeginTransaction(rr, amount, Uid(http), body?.Note);
        tx.PayPalCaptureId = donation.PayPalCaptureId;
        db.RefundTransactions.Add(tx);
        await db.SaveChangesAsync();
        await Notify(hub, rr.CampaignId, new { refundId = rr.Id, status = "Processing", amount, eventType = "processing" });

        try
        {
            var result = await paypal.RefundCaptureAsync(
                donation.PayPalCaptureId!, amount, rr.Currency, body?.Note ?? rr.Reason);
            svc.CompleteTransaction(tx, result.RefundId, result.Status);
            svc.ApplyApproval(rr, amount, Uid(http) ?? "admin");
            rr.LastPayPalRefundId = result.RefundId;

            if (result.Status == "COMPLETED" || paypal.IsSimulated)
            {
                rr.Status = rr.RefundedAmount >= rr.RequestedAmount
                    ? RefundStatus.Confirmed
                    : RefundStatus.PartiallyRefunded;
                if (rr.Status == RefundStatus.Confirmed)
                    rr.ConfirmedAt = DateTime.UtcNow;
            }

            var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == rr.CampaignId);
            if (campaign != null)
                campaign.RaisedAmount = Math.Max(0, campaign.RaisedAmount - amount);

            var totalRefundedOnDonation = await db.RefundRequests.Where(r => r.DonationId == donation.Id).SumAsync(r => r.RefundedAmount);
            // include current rr already updated
            totalRefundedOnDonation = await db.RefundRequests.Where(r => r.DonationId == donation.Id).SumAsync(r => r.RefundedAmount);
            if (rr.RefundedAmount >= donation.Amount || totalRefundedOnDonation >= donation.Amount)
                donation.Status = DonationStatus.Refunded;

            var prev = await db.AuditLedger.Where(e => e.CampaignId == rr.CampaignId)
                .OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync() ?? "";
            db.AuditLedger.Add(ledger.CreateEntry(rr.CampaignId, "RefundProcessed", new
            {
                rr.Id,
                rr.DonationId,
                amount,
                result.RefundId,
                rr.Status
            }, prev));

            await db.SaveChangesAsync();
            await Notify(hub, rr.CampaignId, new
            {
                refundId = rr.Id,
                status = rr.Status.ToString(),
                amount,
                paypalRefundId = result.RefundId,
                eventType = "processed",
                notifyDonor = rr.NotifyDonor
            });

            return Results.Ok(new
            {
                refundId = rr.Id,
                transactionId = tx.Id,
                status = rr.Status.ToString(),
                refundedAmount = rr.RefundedAmount,
                requestedAmount = rr.RequestedAmount,
                paypalRefundId = result.RefundId,
                paypalStatus = result.Status,
                confirmation = rr.Status is RefundStatus.Confirmed or RefundStatus.FullyRefunded,
                simulated = paypal.IsSimulated
            });
        }
        catch (Exception ex)
        {
            svc.FailTransaction(tx, ex.Message);
            rr.Status = RefundStatus.Failed;
            rr.FailedAt = DateTime.UtcNow;
            rr.FailureReason = ex.Message;
            await db.SaveChangesAsync();
            await Notify(hub, rr.CampaignId, new { refundId = rr.Id, status = "Failed", error = ex.Message, eventType = "failed" });
            return Results.Problem(detail: ex.Message, title: "Refund failed");
        }
    }

    private static async Task<IResult> ConfirmRefund(
        Guid id, HttpContext http, AppDbContext db, IHubContext<CampaignHub> hub)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var rr = await db.RefundRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (rr == null) return Results.NotFound();
        if (rr.RefundedAmount <= 0)
            return Results.BadRequest(new { error = "Nothing refunded yet" });
        rr.Status = rr.RefundedAmount >= rr.RequestedAmount ? RefundStatus.Confirmed : RefundStatus.PartiallyRefunded;
        rr.ConfirmedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await Notify(hub, rr.CampaignId, new { refundId = rr.Id, status = rr.Status.ToString(), eventType = "confirmed" });
        return Results.Ok(new { refundId = rr.Id, status = rr.Status.ToString(), confirmedAt = rr.ConfirmedAt });
    }

    /// <summary>Apply PayPal webhook refund event (PAYMENT.CAPTURE.REFUNDED / etc.).</summary>
    public static async Task ApplyWebhookRefundAsync(
        AppDbContext db,
        string? paypalRefundId,
        string? captureId,
        decimal? amount,
        string? status,
        IHubContext<CampaignHub>? hub = null)
    {
        if (string.IsNullOrEmpty(captureId) && string.IsNullOrEmpty(paypalRefundId)) return;

        var donation = !string.IsNullOrEmpty(captureId)
            ? await db.Donations.FirstOrDefaultAsync(d => d.PayPalCaptureId == captureId)
            : null;

        RefundRequest? rr = null;
        if (donation != null)
        {
            rr = await db.RefundRequests
                .Where(r => r.DonationId == donation.Id)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync();
        }
        if (rr == null && !string.IsNullOrEmpty(paypalRefundId))
        {
            rr = await db.RefundRequests.FirstOrDefaultAsync(r => r.LastPayPalRefundId == paypalRefundId);
        }
        if (rr == null) return;

        // Deduplicate webhook
        if (!string.IsNullOrEmpty(paypalRefundId) &&
            await db.RefundTransactions.AnyAsync(t => t.PayPalRefundId == paypalRefundId))
            return;

        var tx = new RefundTransaction
        {
            RefundRequestId = rr.Id,
            DonationId = rr.DonationId,
            Amount = amount ?? 0,
            Currency = rr.Currency,
            Status = string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase) ? "Completed" : (status ?? "Pending"),
            PayPalRefundId = paypalRefundId,
            PayPalCaptureId = captureId,
            FromWebhook = true,
            CompletedAt = DateTime.UtcNow,
            Note = "Synced from PayPal webhook"
        };
        db.RefundTransactions.Add(tx);

        if (amount is > 0 && string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            // only add if not already counted under same paypal id
            rr.RefundedAmount = Math.Min(rr.RequestedAmount, rr.RefundedAmount + amount.Value);
            rr.LastPayPalRefundId = paypalRefundId ?? rr.LastPayPalRefundId;
            rr.Status = rr.RefundedAmount >= rr.RequestedAmount
                ? RefundStatus.Confirmed
                : RefundStatus.PartiallyRefunded;
            if (rr.Status == RefundStatus.Confirmed)
                rr.ConfirmedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        if (hub != null)
            await Notify(hub, rr.CampaignId, new { refundId = rr.Id, status = rr.Status.ToString(), eventType = "webhook_sync", paypalRefundId });
    }

    private static async Task<IResult> SyncFromWebhookPayload([FromBody] WebhookRefundBody body, AppDbContext db, IHubContext<CampaignHub> hub)
    {
        await ApplyWebhookRefundAsync(db, body.RefundId, body.CaptureId, body.Amount, body.Status, hub);
        return Results.Ok(new { synced = true });
    }

    public record RefundRequestBody(decimal Amount = 0, string? Reason = null, string? ReasonCode = null, bool? ForceFreeze = null);
    public record ProcessRefundBody(decimal? Amount = null, string? Note = null);
    public record WebhookRefundBody(string? RefundId, string? CaptureId, decimal? Amount, string? Status);
}
