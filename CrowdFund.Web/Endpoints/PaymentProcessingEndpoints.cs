using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using CrowdFund.Web.Hubs;
using CrowdFund.Web.Observability;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

/// <summary>
/// Complete payment processing: create, approve path, authorize, capture (full/partial),
/// cancel, fail handling, status, idempotency, multi-currency validation.
/// </summary>
public static class PaymentProcessingEndpoints
{
    private static readonly HashSet<string> SupportedCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "USD", "EUR", "GBP", "INR", "AUD", "CAD", "JPY", "SGD", "AED", "NZD", "CHF", "HKD", "MXN", "BRL", "PHP"
    };

    public static void MapPaymentProcessingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/payments").WithTags("PaymentProcessing");

        g.MapPost("/orders", CreatePaymentOrder).RequireRateLimiting("strict");
        g.MapPost("/orders/{donationId:guid}/authorize", AuthorizePayment).RequireRateLimiting("strict");
        g.MapPost("/orders/{donationId:guid}/capture", CapturePayment).RequireRateLimiting("strict");
        g.MapPost("/orders/{donationId:guid}/capture-authorization", CaptureAuthorization).RequireRateLimiting("strict");
        g.MapPost("/orders/{donationId:guid}/cancel", CancelPayment).RequireRateLimiting("strict");
        g.MapPost("/orders/{donationId:guid}/mark-failed", MarkFailed).RequireAuthorization();
        g.MapGet("/orders/{donationId:guid}/status", GetPaymentStatus);
        g.MapGet("/orders/by-reference/{transactionReference}", GetByReference);
        g.MapGet("/supported-currencies", () => Results.Ok(SupportedCurrencies.OrderBy(x => x)));
    }

    private static string NewTxnRef() =>
        "CF-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static IResult? ValidateAmount(decimal amount, string currency)
    {
        if (amount <= 0) return Results.BadRequest(new { error = "Amount must be greater than zero" });
        if (amount > 100_000m) return Results.BadRequest(new { error = "Amount exceeds maximum 100000" });
        // JPY typically zero-decimal — still accept major units as platform convention
        if (!SupportedCurrencies.Contains(currency))
            return Results.BadRequest(new { error = $"Currency '{currency}' not supported", supported = SupportedCurrencies });
        return null;
    }

    private static async Task NotifyStatus(IHubContext<CampaignHub>? hub, Donation d)
    {
        if (hub == null) return;
        try
        {
            await hub.Clients.Group($"campaign-{d.CampaignId}").SendAsync("PaymentStatusChanged", new
            {
                donationId = d.Id,
                status = d.Status.ToString(),
                amount = d.Amount,
                capturedAmount = d.CapturedAmount,
                currency = d.Currency,
                transactionReference = d.TransactionReference,
                at = DateTime.UtcNow
            });
        }
        catch { /* non-fatal */ }
    }

    private static async Task<IResult> CreatePaymentOrder(
        [FromBody] CreatePaymentOrderRequest req,
        AppDbContext db,
        PayPalClient paypal,
        DonationService donationSvc,
        FeeTransparencyService fees,
        HttpContext http,
        IHubContext<CampaignHub> hub)
    {
        var currency = (req.Currency ?? "USD").Trim().ToUpperInvariant();
        var invalid = ValidateAmount(req.Amount, currency);
        if (invalid != null) return invalid;

        // Idempotency: return existing order for same key
        if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
        {
            var existing = await db.Donations.AsNoTracking()
                .FirstOrDefaultAsync(d => d.IdempotencyKey == req.IdempotencyKey);
            if (existing != null)
            {
                return Results.Ok(new
                {
                    donationId = existing.Id,
                    orderId = existing.PayPalOrderId,
                    status = existing.Status.ToString(),
                    transactionReference = existing.TransactionReference,
                    intent = existing.Intent,
                    duplicate = true,
                    message = "Idempotent replay — existing payment order returned"
                });
            }
        }

        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == req.CampaignId);
        if (campaign == null) return Results.NotFound(new { error = "Campaign not found" });
        if (campaign.Status != CampaignStatus.Published)
            return Results.BadRequest(new { error = "Campaign is not accepting donations" });
        if (campaign.FundsFrozen)
            return Results.BadRequest(new { error = "Campaign funds are frozen" });

        var tip = req.TipAmount < 0 ? 0 : req.TipAmount;
        var breakdown = fees.Calculate(req.Amount, tip, currency);
        var intent = string.Equals(req.Intent, "AUTHORIZE", StringComparison.OrdinalIgnoreCase) ? "AUTHORIZE" : "CAPTURE";
        var donorId = Uid(http);
        var donation = donationSvc.CreatePending(req.CampaignId, donorId, req.Amount, currency);
        donation.TipAmount = breakdown.TipAmount;
        donation.PlatformFee = breakdown.PlatformFee;
        donation.PaymentProcessorFeeEstimate = breakdown.ProcessorFeeEstimate;
        donation.NetToCampaign = breakdown.NetToCampaign;
        donation.Intent = intent;
        donation.Status = DonationStatus.PendingApproval;
        donation.IdempotencyKey = string.IsNullOrWhiteSpace(req.IdempotencyKey) ? null : req.IdempotencyKey.Trim();
        donation.TransactionReference = NewTxnRef();
        donation.StatusDetail = "Order created; awaiting checkout approval";
        db.Donations.Add(donation);
        await db.SaveChangesAsync();

        try
        {
            var order = await paypal.CreateOrderAsync(
                donation.Id, donation.Amount, donation.Currency,
                $"Donation to: {campaign.Title}", req.MerchantKey, intent);
            donation.PayPalOrderId = order.OrderId;
            await db.SaveChangesAsync();
            await NotifyStatus(hub, donation);

            return Results.Ok(new
            {
                donationId = donation.Id,
                orderId = order.OrderId,
                status = donation.Status.ToString(),
                paypalStatus = order.Status,
                intent,
                transactionReference = donation.TransactionReference,
                amount = donation.Amount,
                currency = donation.Currency,
                fees = breakdown,
                checkout = new
                {
                    message = "Redirect buyer to PayPal or use Smart Buttons with this orderId",
                    returnUrlHint = "After approval, call authorize or capture endpoint"
                },
                simulated = paypal.IsSimulated,
                duplicate = false
            });
        }
        catch (Exception ex)
        {
            donation.Status = DonationStatus.Failed;
            donation.FailedAt = DateTime.UtcNow;
            donation.StatusDetail = ex.Message;
            await db.SaveChangesAsync();
            await NotifyStatus(hub, donation);
            return Results.Problem(ex.Message);
        }
    }

    private static async Task<IResult> AuthorizePayment(
        Guid donationId, AppDbContext db, PayPalClient paypal, IHubContext<CampaignHub> hub)
    {
        var d = await db.Donations.Include(x => x.Campaign).FirstOrDefaultAsync(x => x.Id == donationId);
        if (d == null) return Results.NotFound();
        if (d.Status is DonationStatus.Captured or DonationStatus.Cancelled or DonationStatus.Refunded)
            return Results.BadRequest(new { error = $"Cannot authorize from status {d.Status}" });
        if (string.IsNullOrEmpty(d.PayPalOrderId))
            return Results.BadRequest(new { error = "No PayPal order on donation" });

        try
        {
            var auth = await paypal.AuthorizeOrderAsync(d.PayPalOrderId);
            d.PayPalAuthorizationId = auth.AuthorizationId;
            d.AuthorizedAmount = d.Amount;
            d.Status = DonationStatus.Authorized;
            d.ApprovedAt = DateTime.UtcNow;
            d.StatusDetail = $"Authorized {auth.AuthorizationId}";
            await db.SaveChangesAsync();
            await NotifyStatus(hub, d);
            return Results.Ok(new
            {
                donationId = d.Id,
                authorizationId = d.PayPalAuthorizationId,
                status = d.Status.ToString(),
                authorizedAmount = d.AuthorizedAmount,
                transactionReference = d.TransactionReference,
                simulated = paypal.IsSimulated
            });
        }
        catch (Exception ex)
        {
            d.Status = DonationStatus.Failed;
            d.FailedAt = DateTime.UtcNow;
            d.StatusDetail = ex.Message;
            await db.SaveChangesAsync();
            await NotifyStatus(hub, d);
            return Results.Problem(ex.Message);
        }
    }

    private static async Task<IResult> CapturePayment(
        Guid donationId,
        [FromBody] CapturePaymentRequest? req,
        AppDbContext db,
        PayPalClient paypal,
        DonationService donationSvc,
        LedgerService ledger,
        MatchingGiftService matchSvc,
        AppMetrics metrics,
        IHubContext<CampaignHub> hub)
    {
        var d = await db.Donations.Include(x => x.Campaign).FirstOrDefaultAsync(x => x.Id == donationId);
        if (d == null) return Results.NotFound();

        // Duplicate prevention
        if (d.Status == DonationStatus.Captured && !string.IsNullOrEmpty(d.PayPalCaptureId))
        {
            return Results.Ok(new
            {
                donationId = d.Id,
                captureId = d.PayPalCaptureId,
                status = d.Status.ToString(),
                duplicate = true,
                message = "Already captured"
            });
        }
        if (d.Status is DonationStatus.Cancelled or DonationStatus.Refunded)
            return Results.BadRequest(new { error = $"Cannot capture status {d.Status}" });
        if (string.IsNullOrEmpty(d.PayPalOrderId))
            return Results.BadRequest(new { error = "No PayPal order" });

        // If intent was AUTHORIZE, prefer capture-authorization
        if (d.Intent == "AUTHORIZE" && !string.IsNullOrEmpty(d.PayPalAuthorizationId))
            return await CaptureAuthorization(donationId, new CaptureAuthRequest(req?.Amount, req?.FinalCapture ?? true), db, paypal, donationSvc, ledger, matchSvc, metrics, hub);

        try
        {
            var capture = await paypal.CaptureOrderAsync(d.PayPalOrderId);
            if (!capture.IsCompleted && !paypal.IsSimulated)
            {
                d.Status = DonationStatus.Pending;
                d.StatusDetail = $"Capture status: {capture.Status}";
                await db.SaveChangesAsync();
                await NotifyStatus(hub, d);
                return Results.Ok(new { donationId = d.Id, status = d.Status.ToString(), paypalStatus = capture.Status, pending = true });
            }

            // Another request may have captured
            if (await db.Donations.AnyAsync(x => x.PayPalCaptureId == capture.CaptureId && x.Id != d.Id))
                return Results.Conflict(new { error = "Capture ID already used" });

            await ApplySuccessfulCapture(db, donationSvc, ledger, matchSvc, metrics, d, capture.CaptureId, d.Amount);
            await NotifyStatus(hub, d);
            return Results.Ok(new
            {
                donationId = d.Id,
                captureId = d.PayPalCaptureId,
                status = d.Status.ToString(),
                capturedAmount = d.CapturedAmount,
                transactionReference = d.TransactionReference,
                duplicate = false,
                simulated = paypal.IsSimulated
            });
        }
        catch (Exception ex)
        {
            d.Status = DonationStatus.Failed;
            d.FailedAt = DateTime.UtcNow;
            d.StatusDetail = ex.Message;
            metrics.CaptureFailed();
            await db.SaveChangesAsync();
            await NotifyStatus(hub, d);
            return Results.Problem(ex.Message);
        }
    }

    private static async Task<IResult> CaptureAuthorization(
        Guid donationId,
        [FromBody] CaptureAuthRequest? req,
        AppDbContext db,
        PayPalClient paypal,
        DonationService donationSvc,
        LedgerService ledger,
        MatchingGiftService matchSvc,
        AppMetrics metrics,
        IHubContext<CampaignHub> hub)
    {
        var d = await db.Donations.Include(x => x.Campaign).FirstOrDefaultAsync(x => x.Id == donationId);
        if (d == null) return Results.NotFound();
        if (string.IsNullOrEmpty(d.PayPalAuthorizationId))
            return Results.BadRequest(new { error = "No authorization on donation — call authorize first" });

        var amount = req?.Amount;
        if (amount != null)
        {
            if (amount <= 0) return Results.BadRequest(new { error = "Invalid partial amount" });
            if (amount > d.Amount - d.CapturedAmount)
                return Results.BadRequest(new { error = "Amount exceeds remaining authorized balance" });
        }

        var finalCapture = req?.FinalCapture ?? amount == null || amount >= d.Amount - d.CapturedAmount;
        try
        {
            var capture = await paypal.CaptureAuthorizationAsync(
                d.PayPalAuthorizationId, amount, d.Currency, finalCapture);
            var capAmt = amount ?? (d.Amount - d.CapturedAmount);
            await ApplySuccessfulCapture(db, donationSvc, ledger, matchSvc, metrics, d, capture.CaptureId, capAmt, partial: !finalCapture || d.CapturedAmount + capAmt < d.Amount);
            await NotifyStatus(hub, d);
            return Results.Ok(new
            {
                donationId = d.Id,
                captureId = d.PayPalCaptureId,
                status = d.Status.ToString(),
                capturedAmount = d.CapturedAmount,
                remaining = d.Amount - d.CapturedAmount,
                finalCapture,
                transactionReference = d.TransactionReference
            });
        }
        catch (Exception ex)
        {
            d.StatusDetail = ex.Message;
            await db.SaveChangesAsync();
            return Results.Problem(ex.Message);
        }
    }

    private static async Task ApplySuccessfulCapture(
        AppDbContext db,
        DonationService donationSvc,
        LedgerService ledger,
        MatchingGiftService matchSvc,
        AppMetrics metrics,
        Donation d,
        string captureId,
        decimal captureAmount,
        bool partial = false)
    {
        if (d.Status == DonationStatus.Captured && d.PayPalCaptureId == captureId)
            return;

        d.PayPalCaptureId = captureId;
        d.CapturedAmount += captureAmount;
        d.CapturedAt = DateTime.UtcNow;
        d.Status = partial || d.CapturedAmount < d.Amount
            ? DonationStatus.PartiallyCaptured
            : DonationStatus.Captured;
        d.StatusDetail = partial ? "Partial capture applied" : "Fully captured";

        if (d.Campaign != null && (d.Status == DonationStatus.Captured || d.Status == DonationStatus.PartiallyCaptured))
        {
            // Only add to raised once for the delta
            d.Campaign.RaisedAmount += captureAmount;
            if (d.Status == DonationStatus.Captured || d.CapturedAmount == captureAmount)
                d.Campaign.DonorCount += 1;
        }

        // Matching only on first full/partial path when moving from non-captured
        // Ledger entry
        var prev = await db.AuditLedger.Where(e => e.CampaignId == d.CampaignId)
            .OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync() ?? "";
        db.AuditLedger.Add(ledger.CreateEntry(d.CampaignId, "PaymentCapture", new
        {
            d.Id,
            captureId,
            captureAmount,
            d.Currency,
            d.TransactionReference,
            d.Status
        }, prev));

        metrics.CaptureOk();
        await db.SaveChangesAsync();
    }

    private static async Task<IResult> CancelPayment(
        Guid donationId, AppDbContext db, PayPalClient paypal, IHubContext<CampaignHub> hub)
    {
        var d = await db.Donations.FirstOrDefaultAsync(x => x.Id == donationId);
        if (d == null) return Results.NotFound();
        if (d.Status is DonationStatus.Captured or DonationStatus.Refunded)
            return Results.BadRequest(new { error = "Cannot cancel captured/refunded payment" });

        if (!string.IsNullOrEmpty(d.PayPalAuthorizationId) && d.Status == DonationStatus.Authorized)
        {
            await paypal.VoidAuthorizationAsync(d.PayPalAuthorizationId);
        }

        d.Status = DonationStatus.Cancelled;
        d.CancelledAt = DateTime.UtcNow;
        d.StatusDetail = "Cancelled by system or user";
        await db.SaveChangesAsync();
        await NotifyStatus(hub, d);
        return Results.Ok(new { donationId = d.Id, status = d.Status.ToString(), transactionReference = d.TransactionReference });
    }

    private static async Task<IResult> MarkFailed(
        Guid donationId, [FromBody] FailRequest? req, HttpContext http, AppDbContext db, IHubContext<CampaignHub> hub)
    {
        var d = await db.Donations.FirstOrDefaultAsync(x => x.Id == donationId);
        if (d == null) return Results.NotFound();
        if (d.Status == DonationStatus.Captured)
            return Results.BadRequest(new { error = "Already captured" });
        d.Status = DonationStatus.Failed;
        d.FailedAt = DateTime.UtcNow;
        d.StatusDetail = req?.Reason ?? "Marked failed";
        await db.SaveChangesAsync();
        await NotifyStatus(hub, d);
        return Results.Ok(new { donationId = d.Id, status = d.Status.ToString() });
    }

    private static async Task<IResult> GetPaymentStatus(Guid donationId, AppDbContext db, PayPalClient paypal)
    {
        var d = await db.Donations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == donationId);
        if (d == null) return Results.NotFound();
        object? paypalOrder = null;
        if (!string.IsNullOrEmpty(d.PayPalOrderId) && !paypal.IsSimulated)
        {
            try { paypalOrder = await paypal.GetOrderAsync(d.PayPalOrderId); }
            catch { /* ignore live lookup errors */ }
        }
        return Results.Ok(new
        {
            donationId = d.Id,
            status = d.Status.ToString(),
            statusDetail = d.StatusDetail,
            intent = d.Intent,
            amount = d.Amount,
            authorizedAmount = d.AuthorizedAmount,
            capturedAmount = d.CapturedAmount,
            currency = d.Currency,
            transactionReference = d.TransactionReference,
            orderId = d.PayPalOrderId,
            authorizationId = d.PayPalAuthorizationId,
            captureId = d.PayPalCaptureId,
            createdAt = d.CreatedAt,
            approvedAt = d.ApprovedAt,
            capturedAt = d.CapturedAt,
            cancelledAt = d.CancelledAt,
            failedAt = d.FailedAt,
            paypalOrder
        });
    }

    private static async Task<IResult> GetByReference(string transactionReference, AppDbContext db)
    {
        var d = await db.Donations.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TransactionReference == transactionReference);
        if (d == null) return Results.NotFound();
        return Results.Ok(new
        {
            donationId = d.Id,
            status = d.Status.ToString(),
            transactionReference = d.TransactionReference,
            amount = d.Amount,
            currency = d.Currency
        });
    }

    public record CreatePaymentOrderRequest(
        Guid CampaignId,
        decimal Amount,
        string? Currency = "USD",
        decimal TipAmount = 0,
        string? Intent = "CAPTURE",
        string? IdempotencyKey = null,
        string? MerchantKey = null);

    public record CapturePaymentRequest(decimal? Amount = null, bool FinalCapture = true);
    public record CaptureAuthRequest(decimal? Amount = null, bool FinalCapture = true);
    public record FailRequest(string? Reason = null);
}
