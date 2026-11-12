using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrowdFund.Web.Endpoints;

public static class PaymentsDashboardEndpoints
{
    public static void MapPaymentsDashboardEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin/payments").RequireAuthorization();
        g.MapGet("/metrics", Metrics);
        g.MapGet("/report", Report);
        g.MapGet("/settings", GetSettings);
        g.MapPut("/settings", UpdateSettings);
        g.MapGet("/overview", Overview);
    }

    private static async Task<PaymentRuntimeSettings> EnsureSettings(AppDbContext db)
    {
        var s = await db.PaymentRuntimeSettings.FirstOrDefaultAsync(x => x.Id == 1);
        if (s == null)
        {
            s = new PaymentRuntimeSettings { Id = 1 };
            db.PaymentRuntimeSettings.Add(s);
            await db.SaveChangesAsync();
        }
        return s;
    }

    private static async Task<IResult> Metrics(HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();

        var total = await db.Donations.CountAsync();
        var successful = await db.Donations.CountAsync(d =>
            d.Status == DonationStatus.Captured || d.Status == DonationStatus.PartiallyCaptured);
        var failed = await db.Donations.CountAsync(d => d.Status == DonationStatus.Failed);
        var pending = await db.Donations.CountAsync(d =>
            d.Status == DonationStatus.Pending || d.Status == DonationStatus.PendingApproval ||
            d.Status == DonationStatus.Authorized || d.Status == DonationStatus.Created);
        var volume = await db.Donations
            .Where(d => d.Status == DonationStatus.Captured || d.Status == DonationStatus.PartiallyCaptured)
            .SumAsync(d => d.CapturedAmount > 0 ? d.CapturedAmount : d.Amount);
        var refundsTotal = await db.RefundRequests.SumAsync(r => r.RefundedAmount);
        var refundsCount = await db.RefundRequests.CountAsync(r => r.RefundedAmount > 0);
        var activeSubs = await db.RecurringDonations.CountAsync(s =>
            s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trial);
        var cancelledSubs = await db.RecurringDonations.CountAsync(s => s.Status == SubscriptionStatus.Cancelled);
        var pendingPayouts = await db.Payouts.CountAsync(p =>
            p.Status == PayoutStatus.Requested || p.Status == PayoutStatus.UnderReview || p.Status == PayoutStatus.Approved || p.Status == PayoutStatus.Processing);
        var disputes = await db.DisputeCases.CountAsync(d =>
            d.Status == DisputeStatus.Open || d.Status == DisputeStatus.Investigating);

        return Results.Ok(new
        {
            totalTransactions = total,
            successfulPayments = successful,
            failedPayments = failed,
            pendingPayments = pending,
            totalPaymentVolume = volume,
            totalRefundsAmount = refundsTotal,
            totalRefundsCount = refundsCount,
            activeSubscriptions = activeSubs,
            cancelledSubscriptions = cancelledSubs,
            pendingPayouts,
            disputedTransactions = disputes,
            generatedAt = DateTime.UtcNow
        });
    }

    private static async Task<IResult> Report(
        HttpContext http, AppDbContext db,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var fromDt = from ?? DateTime.UtcNow.AddDays(-30);
        var toDt = to ?? DateTime.UtcNow;
        var donations = await db.Donations.AsNoTracking()
            .Where(d => d.CreatedAt >= fromDt && d.CreatedAt <= toDt).ToListAsync();
        var byDay = donations
            .GroupBy(d => d.CreatedAt.Date)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                date = g.Key,
                count = g.Count(),
                volume = g.Where(x => x.Status == DonationStatus.Captured || x.Status == DonationStatus.PartiallyCaptured)
                    .Sum(x => x.CapturedAmount > 0 ? x.CapturedAmount : x.Amount),
                failed = g.Count(x => x.Status == DonationStatus.Failed)
            }).ToList();
        var byCurrency = donations.GroupBy(d => d.Currency)
            .Select(g => new { currency = g.Key, count = g.Count(), amount = g.Sum(x => x.Amount) }).ToList();
        return Results.Ok(new { from = fromDt, to = toDt, byDay, byCurrency, total = donations.Count });
    }

    private static async Task<IResult> GetSettings(
        HttpContext http, AppDbContext db, PayPalAccountService accounts, IOptions<PayPalOptions> opts, HttpRequest request)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var s = await EnsureSettings(db);
        var baseUrl = $"{request.Scheme}://{request.Host}";
        return Results.Ok(new
        {
            s.PayPalEnabled,
            s.PreferredMode,
            s.SimulateOverride,
            s.WebhookUrlOverride,
            defaultWebhookUrl = $"{baseUrl}/api/paypal/webhook",
            configMode = opts.Value.Mode,
            isSimulated = accounts.IsSimulated,
            clientIdConfigured = !string.IsNullOrWhiteSpace(opts.Value.ClientId),
            secretConfigured = !string.IsNullOrWhiteSpace(opts.Value.ClientSecret),
            webhookIdConfigured = !string.IsNullOrWhiteSpace(opts.Value.WebhookId),
            notifications = new
            {
                s.NotifyOnCapture,
                s.NotifyOnRefund,
                s.NotifyOnSubscription,
                s.NotifyOnPayout,
                s.NotifyOnDispute,
                s.NotificationWebhookUrl,
                s.NotificationEmail
            },
            s.UpdatedAt,
            note = "API Client ID/Secret are set via environment variables or user-secrets — never stored in this settings row."
        });
    }

    private static async Task<IResult> UpdateSettings(
        [FromBody] UpdatePaymentSettingsRequest req, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var s = await EnsureSettings(db);
        if (req.PayPalEnabled.HasValue) s.PayPalEnabled = req.PayPalEnabled.Value;
        if (!string.IsNullOrWhiteSpace(req.PreferredMode))
            s.PreferredMode = req.PreferredMode.Equals("live", StringComparison.OrdinalIgnoreCase) ? "live" : "sandbox";
        if (req.SimulateOverride.HasValue) s.SimulateOverride = req.SimulateOverride.Value;
        if (req.WebhookUrlOverride != null) s.WebhookUrlOverride = req.WebhookUrlOverride;
        if (req.NotifyOnCapture.HasValue) s.NotifyOnCapture = req.NotifyOnCapture.Value;
        if (req.NotifyOnRefund.HasValue) s.NotifyOnRefund = req.NotifyOnRefund.Value;
        if (req.NotifyOnSubscription.HasValue) s.NotifyOnSubscription = req.NotifyOnSubscription.Value;
        if (req.NotifyOnPayout.HasValue) s.NotifyOnPayout = req.NotifyOnPayout.Value;
        if (req.NotifyOnDispute.HasValue) s.NotifyOnDispute = req.NotifyOnDispute.Value;
        if (req.NotificationWebhookUrl != null) s.NotificationWebhookUrl = req.NotificationWebhookUrl;
        if (req.NotificationEmail != null) s.NotificationEmail = req.NotificationEmail;
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(new { saved = true, s.UpdatedAt });
    }

    private static async Task<IResult> Overview(HttpContext http, AppDbContext db, PayPalAccountService accounts)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var s = await EnsureSettings(db);
        return Results.Ok(new
        {
            paypalEnabled = s.PayPalEnabled,
            gateway = accounts.GetStatus(),
            links = new
            {
                transactions = "/admin/transactions",
                refunds = "/admin/refunds",
                customers = "/admin/customers",
                invoices = "/invoices",
                gateway = "/admin/payments",
                webhooks = "/api/admin/webhooks",
                payouts = "/api/admin/payouts",
                subscriptions = "/api/subscriptions/plans"
            }
        });
    }

    public record UpdatePaymentSettingsRequest(
        bool? PayPalEnabled = null,
        string? PreferredMode = null,
        bool? SimulateOverride = null,
        string? WebhookUrlOverride = null,
        bool? NotifyOnCapture = null,
        bool? NotifyOnRefund = null,
        bool? NotifyOnSubscription = null,
        bool? NotifyOnPayout = null,
        bool? NotifyOnDispute = null,
        string? NotificationWebhookUrl = null,
        string? NotificationEmail = null);
}
