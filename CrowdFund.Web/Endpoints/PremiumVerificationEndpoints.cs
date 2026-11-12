using System.Security.Claims;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrowdFund.Web.Endpoints;

public static class PremiumVerificationEndpoints
{
    public static void MapPremiumVerificationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/premium/plans", () => Results.Ok(new[]
        {
            new
            {
                plan = nameof(PremiumPlan.Priority),
                priceUsd = PremiumSla.PriceUsd(PremiumPlan.Priority),
                slaHours = PremiumSla.TargetReviewHours(PremiumPlan.Priority),
                description = "Priority review queue — target response within 12 hours during business coverage."
            },
            new
            {
                plan = nameof(PremiumPlan.PriorityPlus),
                priceUsd = PremiumSla.PriceUsd(PremiumPlan.PriorityPlus),
                slaHours = PremiumSla.TargetReviewHours(PremiumPlan.PriorityPlus),
                description = "Highest priority — target response within 4 hours. Does not change trust scoring rules."
            }
        }));

        app.MapGet("/api/premium/me", MyPremium).RequireAuthorization();
        // Starts PayPal checkout (or simulates when gateway is in simulate mode)
        app.MapPost("/api/premium/subscribe", Subscribe).RequireAuthorization();
        // After PayPal approval — capture + activate
        app.MapPost("/api/premium/confirm", ConfirmPayment).RequireAuthorization();
        // Fallback simulate without payment (demo)
        app.MapPost("/api/premium/subscribe-simulate", SubscribeSimulate).RequireAuthorization();
        app.MapPost("/api/premium/campaigns/{id:guid}/apply", ApplyToCampaign).RequireAuthorization();
        app.MapPost("/api/admin/premium/grant", AdminGrant).RequireAuthorization();
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static async Task<IResult> MyPremium(HttpContext http, AppDbContext db, PremiumVerificationService premium)
    {
        var uid = Uid(http)!;
        var subs = await db.PremiumVerificationSubscriptions.AsNoTracking()
            .Where(s => s.CreatorId == uid)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
        var plan = premium.EffectivePlan(subs);
        return Results.Ok(new
        {
            plan = plan.ToString(),
            slaHours = PremiumSla.TargetReviewHours(plan),
            queuePriority = PremiumSla.QueuePriority(plan),
            active = plan != PremiumPlan.None,
            subscriptions = subs
        });
    }

    /// <summary>
    /// Create a PayPal order for the selected premium plan.
    /// Client should redirect user to <c>approveUrl</c> when present, then call /api/premium/confirm.
    /// </summary>
    private static async Task<IResult> Subscribe(
        [FromBody] SubscribeRequest req,
        HttpContext http,
        AppDbContext db,
        PremiumVerificationService premium,
        PayPalClient paypal,
        IOptions<PayPalOptions> paypalOpts)
    {
        var uid = Uid(http);
        if (string.IsNullOrEmpty(uid))
            return Results.Unauthorized();

        if (!Enum.TryParse<PremiumPlan>(req.Plan, true, out var plan) || plan == PremiumPlan.None)
            return Results.BadRequest(new { error = "plan must be Priority or PriorityPlus" });

        var days = req.Days <= 0 ? 30 : req.Days;
        var amount = PremiumSla.PriceUsd(plan);
        var pendingId = Guid.NewGuid();
        var description = $"CrowdFund AI Premium {plan} ({days} days)";

        try
        {
            var order = await paypal.CreateOrderAsync(
                pendingId,
                amount,
                "USD",
                description);

            // Store pending intent on a not-yet-active subscription row (payment ref = order id)
            var pending = premium.Activate(uid, plan, days, promotional: false, paymentRef: order.OrderId, amountOverride: amount);
            pending.Status = PremiumSubscriptionStatus.PendingPayment;
            pending.PaymentReference = order.OrderId;
            db.PremiumVerificationSubscriptions.Add(pending);
            await db.SaveChangesAsync();

            var opts = paypalOpts.Value;
            var baseReturn = string.IsNullOrWhiteSpace(opts.ReturnUrl)
                ? $"{http.Request.Scheme}://{http.Request.Host}/premium/return"
                : opts.ReturnUrl;
            // Prefer host-relative premium return if default still points at donate
            if (baseReturn.Contains("/donate/", StringComparison.OrdinalIgnoreCase))
                baseReturn = $"{http.Request.Scheme}://{http.Request.Host}/premium/return";

            var approveUrl = order.ApproveUrl;
            if (string.IsNullOrWhiteSpace(approveUrl) && !paypal.IsSimulated)
            {
                // Sandbox fallback deep-link pattern (rarely needed if links parsed)
                approveUrl = $"https://www.sandbox.paypal.com/checkoutnow?token={order.OrderId}";
            }
            if (paypal.IsSimulated)
            {
                // Simulated: no external redirect — client should call confirm immediately
                approveUrl = null;
            }

            return Results.Ok(new
            {
                subscriptionId = pending.Id,
                orderId = order.OrderId,
                status = order.Status,
                amount,
                plan = plan.ToString(),
                days,
                approveUrl,
                simulated = paypal.IsSimulated,
                message = paypal.IsSimulated
                    ? "Simulated PayPal order created. Call /api/premium/confirm to activate."
                    : "PayPal order created. Redirect the user to approveUrl, then call /api/premium/confirm."
            });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new
            {
                error = "PayPal order failed",
                detail = ex.Message,
                hint = "Check PayPal:ClientId / ClientSecret / Mode in appsettings. Set Simulate=true for offline demo."
            });
        }
    }

    /// <summary>Capture PayPal order and activate the matching pending premium subscription.</summary>
    private static async Task<IResult> ConfirmPayment(
        [FromBody] ConfirmRequest req,
        HttpContext http,
        AppDbContext db,
        PremiumVerificationService premium,
        PayPalClient paypal)
    {
        var uid = Uid(http);
        if (string.IsNullOrEmpty(uid))
            return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(req.OrderId))
            return Results.BadRequest(new { error = "orderId required" });

        var sub = await db.PremiumVerificationSubscriptions
            .Where(s => s.CreatorId == uid && s.PaymentReference == req.OrderId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();

        if (sub == null)
            return Results.NotFound(new { error = "No pending subscription for this order" });

        try
        {
            var capture = await paypal.CaptureOrderAsync(req.OrderId);
            if (!capture.IsCompleted && !paypal.IsSimulated)
                return Results.BadRequest(new { error = "Payment not completed", status = capture.Status });

            sub.Status = PremiumSubscriptionStatus.Active;
            sub.PaymentReference = capture.CaptureId ?? req.OrderId;
            sub.StartsAt = DateTime.UtcNow;
            if (sub.ExpiresAt < sub.StartsAt)
                sub.ExpiresAt = sub.StartsAt.AddDays(30);

            var open = await db.Campaigns
                .Where(c => c.CreatorId == uid &&
                            (c.Status == CampaignStatus.UnderReview || c.Status == CampaignStatus.NeedsReview || c.Status == CampaignStatus.Draft))
                .ToListAsync();
            foreach (var c in open)
                premium.ApplyToCampaign(c, sub.Plan);

            await db.SaveChangesAsync();
            return Results.Ok(new
            {
                sub.Id,
                plan = sub.Plan.ToString(),
                sub.ExpiresAt,
                sub.AmountPaid,
                paymentReference = sub.PaymentReference,
                captureStatus = capture.Status,
                campaignsBoosted = open.Count,
                simulated = paypal.IsSimulated,
                disclaimer = "Premium speeds human review SLA only. AI trust scores and fraud rules are unchanged."
            });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = "Capture failed", detail = ex.Message });
        }
    }

    /// <summary>Demo path: activate without real money (keeps old simulate button behavior).</summary>
    private static async Task<IResult> SubscribeSimulate(
        [FromBody] SubscribeRequest req,
        HttpContext http,
        AppDbContext db,
        PremiumVerificationService premium)
    {
        var uid = Uid(http);
        if (string.IsNullOrEmpty(uid))
            return Results.Unauthorized();
        if (!Enum.TryParse<PremiumPlan>(req.Plan, true, out var plan) || plan == PremiumPlan.None)
            return Results.BadRequest(new { error = "plan must be Priority or PriorityPlus" });

        var days = req.Days <= 0 ? 30 : req.Days;
        var sub = premium.Activate(uid, plan, days, promotional: req.Promotional ?? false);
        db.PremiumVerificationSubscriptions.Add(sub);

        var open = await db.Campaigns
            .Where(c => c.CreatorId == uid &&
                        (c.Status == CampaignStatus.UnderReview || c.Status == CampaignStatus.NeedsReview || c.Status == CampaignStatus.Draft))
            .ToListAsync();
        foreach (var c in open)
            premium.ApplyToCampaign(c, plan);

        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            sub.Id,
            plan = sub.Plan.ToString(),
            sub.ExpiresAt,
            sub.AmountPaid,
            sub.PaymentReference,
            campaignsBoosted = open.Count,
            simulated = true,
            disclaimer = "Premium speeds human review SLA only. AI trust scores and fraud rules are unchanged."
        });
    }

    private static async Task<IResult> ApplyToCampaign(
        Guid id, HttpContext http, AppDbContext db, PremiumVerificationService premium)
    {
        var uid = Uid(http)!;
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id);
        if (campaign == null) return Results.NotFound();
        if (campaign.CreatorId != uid && !http.User.IsInRole("Admin"))
            return Results.Forbid();

        var subs = await db.PremiumVerificationSubscriptions
            .Where(s => s.CreatorId == campaign.CreatorId)
            .ToListAsync();
        var plan = premium.EffectivePlan(subs);
        if (plan == PremiumPlan.None)
            return Results.BadRequest(new { error = "No active premium subscription. Subscribe first." });

        premium.ApplyToCampaign(campaign, plan);
        db.BackgroundJobs.Add(new BackgroundJob
        {
            JobType = BackgroundJobType.VerificationCouncil,
            CampaignId = campaign.Id,
            Status = BackgroundJobStatus.Pending,
            PayloadJson = "{\"premium\":true}",
            RunAfter = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            campaign.Id,
            campaign.PremiumReview,
            campaign.ReviewPriority,
            campaign.ReviewSlaDueAt,
            plan = plan.ToString(),
            message = "Campaign marked for priority review and verification job queued."
        });
    }

    private static async Task<IResult> AdminGrant(
        [FromBody] GrantRequest req, HttpContext http, AppDbContext db, PremiumVerificationService premium)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(req.CreatorId))
            return Results.BadRequest(new { error = "CreatorId required" });
        if (!Enum.TryParse<PremiumPlan>(req.Plan ?? "Priority", true, out var plan) || plan == PremiumPlan.None)
            plan = PremiumPlan.Priority;
        var sub = premium.Activate(req.CreatorId, plan, req.Days <= 0 ? 30 : req.Days, promotional: true, paymentRef: "ADMIN-GRANT");
        db.PremiumVerificationSubscriptions.Add(sub);
        var open = await db.Campaigns
            .Where(c => c.CreatorId == req.CreatorId &&
                        (c.Status == CampaignStatus.UnderReview || c.Status == CampaignStatus.NeedsReview))
            .ToListAsync();
        foreach (var c in open) premium.ApplyToCampaign(c, plan);
        await db.SaveChangesAsync();
        return Results.Ok(new { sub, boosted = open.Count });
    }

    public record SubscribeRequest(string Plan, int Days = 30, bool? Promotional = false);
    public record ConfirmRequest(string OrderId);
    public record GrantRequest(string CreatorId, string? Plan = "Priority", int Days = 30);
}
