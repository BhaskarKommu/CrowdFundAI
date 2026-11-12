using System.Security.Claims;
using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using CrowdFund.Web.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrowdFund.Web.Endpoints;

public static class SubscriptionManagementEndpoints
{
    public static void MapSubscriptionManagementEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/subscriptions").WithTags("Subscriptions");

        // Plans
        g.MapPost("/plans", CreatePlan).RequireAuthorization();
        g.MapGet("/plans", ListPlans);
        g.MapGet("/plans/{id:guid}", GetPlan);

        // Lifecycle
        g.MapPost("/start", StartSubscription).RequireAuthorization();
        g.MapPost("/{id:guid}/activate", Activate);
        g.MapPost("/{id:guid}/cancel", Cancel).RequireAuthorization();
        g.MapPost("/{id:guid}/suspend", Suspend).RequireAuthorization();
        g.MapPost("/{id:guid}/reactivate", Reactivate).RequireAuthorization();
        g.MapPost("/{id:guid}/change-plan", ChangePlan).RequireAuthorization();
        g.MapPost("/{id:guid}/renew", Renew).RequireAuthorization();
        g.MapPost("/{id:guid}/record-failure", RecordFailure).RequireAuthorization();
        g.MapPost("/{id:guid}/retry-payment", RetryPayment).RequireAuthorization();
        g.MapPost("/process-due", ProcessDue); // admin/job
        g.MapPost("/expire-stale", ExpireStale);

        // Status & history
        g.MapGet("/mine", MySubscriptions).RequireAuthorization();
        g.MapGet("/{id:guid}", GetSubscription).RequireAuthorization();
        g.MapGet("/{id:guid}/billing-history", BillingHistory).RequireAuthorization();
        g.MapGet("/{id:guid}/status", GetStatus).RequireAuthorization();
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static async Task AddEvent(AppDbContext db, Guid subId, string type, decimal? amount = null, string? currency = null, string status = "ok", string? detail = null, string? txn = null)
    {
        db.SubscriptionBillingEvents.Add(new SubscriptionBillingEvent
        {
            RecurringDonationId = subId,
            EventType = type,
            Amount = amount,
            Currency = currency,
            Status = status,
            Detail = detail,
            PayPalTransactionId = txn
        });
    }

    private static async Task Notify(IHubContext<CampaignHub> hub, Guid campaignId, object payload)
    {
        try { await hub.Clients.Group($"campaign-{campaignId}").SendAsync("SubscriptionStatusChanged", payload); }
        catch { }
    }

    private static DateTime NextCharge(string unit, int count, DateTime from)
    {
        return unit.ToUpperInvariant() switch
        {
            "WEEK" => from.AddDays(7 * Math.Max(1, count)),
            "YEAR" => from.AddYears(Math.Max(1, count)),
            _ => from.AddMonths(Math.Max(1, count))
        };
    }

    private static async Task<IResult> CreatePlan(
        [FromBody] CreatePlanRequest req,
        HttpContext http,
        AppDbContext db,
        PayPalSubscriptionsClient paypal)
    {
        if (!http.User.IsInRole("Admin") && !http.User.IsInRole("Creator"))
            return Results.Forbid();
        if (req.Amount <= 0) return Results.BadRequest(new { error = "Invalid amount" });

        var unit = (req.IntervalUnit ?? "MONTH").ToUpperInvariant();
        if (unit is "WEEKLY") unit = "WEEK";
        if (unit is "MONTHLY") unit = "MONTH";
        if (unit is "YEARLY" or "ANNUAL") unit = "YEAR";

        try
        {
            var (planId, productId) = await paypal.CreatePlanAsync(
                req.Name,
                req.Amount,
                req.Currency ?? "USD",
                unit,
                req.IntervalCount <= 0 ? 1 : req.IntervalCount,
                req.TotalCycles,
                req.TrialDays,
                req.SetupFee,
                req.PaymentFailureThreshold <= 0 ? 3 : req.PaymentFailureThreshold);

            var plan = new SubscriptionPlan
            {
                CampaignId = req.CampaignId,
                Name = req.Name,
                Description = req.Description,
                Amount = req.Amount,
                Currency = req.Currency ?? "USD",
                IntervalUnit = unit,
                IntervalCount = req.IntervalCount <= 0 ? 1 : req.IntervalCount,
                TotalCycles = req.TotalCycles,
                TrialDays = req.TrialDays,
                SetupFee = req.SetupFee,
                PaymentFailureThreshold = req.PaymentFailureThreshold <= 0 ? 3 : req.PaymentFailureThreshold,
                PayPalPlanId = planId,
                PayPalProductId = productId
            };
            db.SubscriptionPlans.Add(plan);
            await db.SaveChangesAsync();
            return Results.Ok(new
            {
                plan.Id,
                plan.Name,
                plan.Amount,
                plan.Currency,
                plan.IntervalUnit,
                plan.IntervalCount,
                plan.TrialDays,
                plan.SetupFee,
                plan.TotalCycles,
                plan.PayPalPlanId,
                plan.PayPalProductId,
                simulated = paypal.IsSimulated
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    private static async Task<IResult> ListPlans([FromQuery] Guid? campaignId, AppDbContext db)
    {
        var q = db.SubscriptionPlans.AsNoTracking().Where(p => p.IsActive);
        if (campaignId.HasValue) q = q.Where(p => p.CampaignId == campaignId || p.CampaignId == null);
        var list = await q.OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> GetPlan(Guid id, AppDbContext db)
    {
        var p = await db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return p == null ? Results.NotFound() : Results.Ok(p);
    }

    private static async Task<IResult> StartSubscription(
        [FromBody] StartSubRequest req,
        HttpContext http,
        AppDbContext db,
        PayPalSubscriptionsClient paypal,
        IOptions<PayPalOptions> paypalOpts,
        IHubContext<CampaignHub> hub)
    {
        var uid = Uid(http)!;
        SubscriptionPlan? plan = null;
        if (req.PlanId.HasValue)
            plan = await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == req.PlanId && p.IsActive);
        if (plan == null && req.CampaignId != Guid.Empty)
        {
            // ad-hoc monthly from amount
            if (req.Amount <= 0) return Results.BadRequest(new { error = "Amount or planId required" });
            var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CampaignId);
            if (campaign == null) return Results.NotFound();
            var (planId, productId) = await paypal.CreatePlanAsync(
                $"CrowdFund · {campaign.Title}",
                req.Amount,
                req.Currency ?? campaign.Currency,
                req.IntervalUnit ?? "MONTH",
                1, 0, req.TrialDays, req.SetupFee);
            plan = new SubscriptionPlan
            {
                CampaignId = req.CampaignId,
                Name = $"Ad hoc {req.IntervalUnit ?? "MONTH"}",
                Amount = req.Amount,
                Currency = req.Currency ?? campaign.Currency,
                IntervalUnit = (req.IntervalUnit ?? "MONTH").ToUpperInvariant(),
                TrialDays = req.TrialDays,
                SetupFee = req.SetupFee,
                PayPalPlanId = planId,
                PayPalProductId = productId
            };
            db.SubscriptionPlans.Add(plan);
            await db.SaveChangesAsync();
        }
        if (plan == null || string.IsNullOrEmpty(plan.PayPalPlanId))
            return Results.BadRequest(new { error = "Plan not available" });

        var returnUrl = paypalOpts.Value.ReturnUrl;
        var cancelUrl = paypalOpts.Value.CancelUrl;
        var created = await paypal.CreateSubscriptionAsync(
            plan.PayPalPlanId, $"{plan.CampaignId}:{uid}:{plan.Id}", returnUrl, cancelUrl);

        var local = new RecurringDonation
        {
            CampaignId = plan.CampaignId ?? req.CampaignId,
            DonorId = uid,
            Amount = plan.Amount,
            Currency = plan.Currency,
            Interval = plan.IntervalUnit.ToLowerInvariant(),
            Status = SubscriptionStatus.ApprovalPending,
            PayPalSubscriptionId = created.SubscriptionId,
            PayPalPlanId = plan.PayPalPlanId,
            PayPalProductId = plan.PayPalProductId,
            SubscriptionPlanId = plan.Id,
            MaxPaymentRetries = plan.PaymentFailureThreshold,
            TrialEndsAt = plan.TrialDays > 0 ? DateTime.UtcNow.AddDays(plan.TrialDays) : null,
            SetupFeePaid = 0,
            NextChargeAt = plan.TrialDays > 0
                ? DateTime.UtcNow.AddDays(plan.TrialDays)
                : NextCharge(plan.IntervalUnit, plan.IntervalCount, DateTime.UtcNow),
            StatusDetail = "Awaiting PayPal approval"
        };
        if (plan.TrialDays > 0) local.Status = SubscriptionStatus.Trial;
        db.RecurringDonations.Add(local);
        await AddEvent(db, local.Id, "created", plan.Amount, plan.Currency, detail: created.Status);
        await db.SaveChangesAsync();
        await Notify(hub, local.CampaignId, new { subscriptionId = local.Id, status = local.Status.ToString(), eventType = "created" });

        return Results.Ok(new
        {
            localId = local.Id,
            paypalSubscriptionId = created.SubscriptionId,
            status = local.Status.ToString(),
            approveUrl = created.ApproveUrl,
            planId = plan.Id,
            paypalPlanId = plan.PayPalPlanId,
            trialEndsAt = local.TrialEndsAt,
            setupFee = plan.SetupFee,
            simulated = paypal.IsSimulated
        });
    }

    private static async Task<IResult> Activate(Guid id, AppDbContext db, PayPalSubscriptionsClient paypal, IHubContext<CampaignHub> hub)
    {
        var sub = await db.RecurringDonations.Include(s => s.Plan).FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null) return Results.NotFound();
        if (!string.IsNullOrEmpty(sub.PayPalSubscriptionId))
            await paypal.ActivateSubscriptionAsync(sub.PayPalSubscriptionId, "Donor activated / return URL");
        sub.Status = SubscriptionStatus.Active;
        sub.ActivatedAt = DateTime.UtcNow;
        sub.StatusDetail = "Active";
        if (sub.Plan != null && sub.Plan.SetupFee > 0 && sub.SetupFeePaid <= 0)
            sub.SetupFeePaid = sub.Plan.SetupFee;
        await AddEvent(db, sub.Id, "activated", sub.Amount, sub.Currency);
        await db.SaveChangesAsync();
        await Notify(hub, sub.CampaignId, new { subscriptionId = sub.Id, status = "Active", eventType = "activated" });
        return Results.Ok(new { sub.Id, status = sub.Status.ToString() });
    }

    private static async Task<IResult> Cancel(Guid id, [FromBody] ReasonBody? body, HttpContext http, AppDbContext db, PayPalSubscriptionsClient paypal, IHubContext<CampaignHub> hub)
    {
        var sub = await db.RecurringDonations.FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null) return Results.NotFound();
        if (sub.DonorId != Uid(http) && !http.User.IsInRole("Admin")) return Results.Forbid();
        if (!string.IsNullOrEmpty(sub.PayPalSubscriptionId))
            await paypal.CancelSubscriptionAsync(sub.PayPalSubscriptionId, body?.Reason ?? "Cancelled by user");
        sub.Status = SubscriptionStatus.Cancelled;
        sub.CancelledAt = DateTime.UtcNow;
        sub.StatusDetail = body?.Reason ?? "Cancelled";
        await AddEvent(db, sub.Id, "cancelled", detail: sub.StatusDetail);
        await db.SaveChangesAsync();
        await Notify(hub, sub.CampaignId, new { subscriptionId = sub.Id, status = "Cancelled", eventType = "cancelled" });
        return Results.Ok(new { sub.Id, status = sub.Status.ToString() });
    }

    private static async Task<IResult> Suspend(Guid id, [FromBody] ReasonBody? body, HttpContext http, AppDbContext db, PayPalSubscriptionsClient paypal, IHubContext<CampaignHub> hub)
    {
        if (!http.User.IsInRole("Admin") && Uid(http) == null) return Results.Forbid();
        var sub = await db.RecurringDonations.FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null) return Results.NotFound();
        if (!string.IsNullOrEmpty(sub.PayPalSubscriptionId))
            await paypal.SuspendSubscriptionAsync(sub.PayPalSubscriptionId, body?.Reason ?? "Suspended");
        sub.Status = SubscriptionStatus.Suspended;
        sub.SuspendedAt = DateTime.UtcNow;
        sub.StatusDetail = body?.Reason ?? "Suspended";
        await AddEvent(db, sub.Id, "suspended", detail: sub.StatusDetail);
        await db.SaveChangesAsync();
        await Notify(hub, sub.CampaignId, new { subscriptionId = sub.Id, status = "Suspended", eventType = "suspended" });
        return Results.Ok(new { sub.Id, status = sub.Status.ToString() });
    }

    private static async Task<IResult> Reactivate(Guid id, HttpContext http, AppDbContext db, PayPalSubscriptionsClient paypal, IHubContext<CampaignHub> hub)
    {
        var sub = await db.RecurringDonations.FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null) return Results.NotFound();
        if (sub.DonorId != Uid(http) && !http.User.IsInRole("Admin")) return Results.Forbid();
        if (sub.Status is not (SubscriptionStatus.Suspended or SubscriptionStatus.PastDue))
            return Results.BadRequest(new { error = "Only suspended/past-due can reactivate" });
        if (!string.IsNullOrEmpty(sub.PayPalSubscriptionId))
            await paypal.ActivateSubscriptionAsync(sub.PayPalSubscriptionId, "Reactivated");
        sub.Status = SubscriptionStatus.Active;
        sub.SuspendedAt = null;
        sub.FailedPaymentCount = 0;
        sub.NextRetryAt = null;
        sub.StatusDetail = "Reactivated";
        await AddEvent(db, sub.Id, "reactivated");
        await db.SaveChangesAsync();
        await Notify(hub, sub.CampaignId, new { subscriptionId = sub.Id, status = "Active", eventType = "reactivated" });
        return Results.Ok(new { sub.Id, status = sub.Status.ToString() });
    }

    private static async Task<IResult> ChangePlan(
        Guid id, [FromBody] ChangePlanRequest req, HttpContext http, AppDbContext db, PayPalSubscriptionsClient paypal, IHubContext<CampaignHub> hub)
    {
        var sub = await db.RecurringDonations.Include(s => s.Plan).FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null) return Results.NotFound();
        if (sub.DonorId != Uid(http) && !http.User.IsInRole("Admin")) return Results.Forbid();
        var newPlan = await db.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == req.NewPlanId && p.IsActive);
        if (newPlan == null || string.IsNullOrEmpty(newPlan.PayPalPlanId))
            return Results.BadRequest(new { error = "New plan not found" });

        if (!string.IsNullOrEmpty(sub.PayPalSubscriptionId))
            await paypal.ReviseSubscriptionPlanAsync(sub.PayPalSubscriptionId, newPlan.PayPalPlanId);

        var direction = newPlan.Amount >= sub.Amount ? "upgraded" : "downgraded";
        sub.SubscriptionPlanId = newPlan.Id;
        sub.PayPalPlanId = newPlan.PayPalPlanId;
        sub.Amount = newPlan.Amount;
        sub.Currency = newPlan.Currency;
        sub.Interval = newPlan.IntervalUnit.ToLowerInvariant();
        await AddEvent(db, sub.Id, direction, newPlan.Amount, newPlan.Currency, detail: $"Plan {newPlan.Id}");
        await db.SaveChangesAsync();
        await Notify(hub, sub.CampaignId, new { subscriptionId = sub.Id, eventType = direction, amount = newPlan.Amount });
        return Results.Ok(new { sub.Id, direction, amount = sub.Amount, planId = newPlan.Id });
    }

    private static async Task<IResult> Renew(Guid id, HttpContext http, AppDbContext db, IHubContext<CampaignHub> hub)
    {
        var sub = await db.RecurringDonations.Include(s => s.Plan).FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null) return Results.NotFound();
        if (sub.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired)
            return Results.BadRequest(new { error = "Cannot renew cancelled/expired; start a new subscription" });
        var unit = sub.Plan?.IntervalUnit ?? "MONTH";
        var count = sub.Plan?.IntervalCount ?? 1;
        sub.NextChargeAt = NextCharge(unit, count, DateTime.UtcNow);
        sub.LastChargedAt = DateTime.UtcNow;
        sub.SuccessfulCharges += 1;
        sub.Status = SubscriptionStatus.Active;
        sub.StatusDetail = "Renewed";
        await AddEvent(db, sub.Id, "renewed", sub.Amount, sub.Currency);
        await db.SaveChangesAsync();
        await Notify(hub, sub.CampaignId, new { subscriptionId = sub.Id, eventType = "renewed" });
        return Results.Ok(new { sub.Id, nextChargeAt = sub.NextChargeAt, successfulCharges = sub.SuccessfulCharges });
    }

    private static async Task<IResult> RecordFailure(
        Guid id, [FromBody] ReasonBody? body, HttpContext http, AppDbContext db, IHubContext<CampaignHub> hub)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var sub = await db.RecurringDonations.FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null) return Results.NotFound();
        sub.FailedPaymentCount += 1;
        sub.LastFailedPaymentAt = DateTime.UtcNow;
        sub.Status = SubscriptionStatus.PastDue;
        sub.StatusDetail = body?.Reason ?? "Payment failed";
        sub.NextRetryAt = DateTime.UtcNow.AddHours(Math.Min(72, 6 * sub.FailedPaymentCount));
        if (sub.FailedPaymentCount >= sub.MaxPaymentRetries)
        {
            sub.Status = SubscriptionStatus.Suspended;
            sub.SuspendedAt = DateTime.UtcNow;
            sub.StatusDetail = "Suspended after max payment retries";
        }
        await AddEvent(db, sub.Id, "failed", sub.Amount, sub.Currency, "failed", sub.StatusDetail);
        await db.SaveChangesAsync();
        await Notify(hub, sub.CampaignId, new { subscriptionId = sub.Id, status = sub.Status.ToString(), eventType = "failed", retries = sub.FailedPaymentCount });
        return Results.Ok(new { sub.Id, status = sub.Status.ToString(), failedPaymentCount = sub.FailedPaymentCount, nextRetryAt = sub.NextRetryAt });
    }

    private static async Task<IResult> RetryPayment(Guid id, HttpContext http, AppDbContext db, IHubContext<CampaignHub> hub)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var sub = await db.RecurringDonations.FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null) return Results.NotFound();
        if (sub.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired)
            return Results.BadRequest(new { error = "Subscription not retryable" });
        // Simulated successful retry — live would depend on PayPal outstanding balance / subscriber instrument
        sub.FailedPaymentCount = 0;
        sub.NextRetryAt = null;
        sub.LastChargedAt = DateTime.UtcNow;
        sub.SuccessfulCharges += 1;
        sub.Status = SubscriptionStatus.Active;
        sub.StatusDetail = "Retry succeeded";
        await AddEvent(db, sub.Id, "retry", sub.Amount, sub.Currency, detail: "Manual/admin retry");
        await db.SaveChangesAsync();
        await Notify(hub, sub.CampaignId, new { subscriptionId = sub.Id, eventType = "retry_ok" });
        return Results.Ok(new { sub.Id, status = sub.Status.ToString() });
    }

    private static async Task<IResult> ProcessDue(HttpContext http, AppDbContext db, IHubContext<CampaignHub> hub)
    {
        if (!http.User.IsInRole("Admin") && http.Request.Headers["X-Job-Key"].FirstOrDefault() != "local")
            return Results.Forbid();
        var due = await db.RecurringDonations
            .Include(s => s.Plan)
            .Where(s => s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trial)
            .Where(s => s.NextChargeAt <= DateTime.UtcNow)
            .Take(50)
            .ToListAsync();
        var processed = 0;
        foreach (var sub in due)
        {
            if (sub.Status == SubscriptionStatus.Trial && sub.TrialEndsAt > DateTime.UtcNow)
                continue;
            sub.LastChargedAt = DateTime.UtcNow;
            sub.SuccessfulCharges += 1;
            if (sub.Status == SubscriptionStatus.Trial)
                sub.Status = SubscriptionStatus.Active;
            var unit = sub.Plan?.IntervalUnit ?? "MONTH";
            var count = sub.Plan?.IntervalCount ?? 1;
            sub.NextChargeAt = NextCharge(unit, count, DateTime.UtcNow);
            if (sub.Plan is { TotalCycles: > 0 } && sub.SuccessfulCharges >= sub.Plan.TotalCycles)
            {
                sub.Status = SubscriptionStatus.Expired;
                sub.ExpiresAt = DateTime.UtcNow;
                await AddEvent(db, sub.Id, "expired", sub.Amount, sub.Currency);
            }
            else
                await AddEvent(db, sub.Id, "charged", sub.Amount, sub.Currency, detail: "process-due job");
            processed++;
            await Notify(hub, sub.CampaignId, new { subscriptionId = sub.Id, eventType = "charged" });
        }
        await db.SaveChangesAsync();
        return Results.Ok(new { processed });
    }

    private static async Task<IResult> ExpireStale(HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var stale = await db.RecurringDonations
            .Where(s => s.ExpiresAt != null && s.ExpiresAt < DateTime.UtcNow && s.Status != SubscriptionStatus.Expired && s.Status != SubscriptionStatus.Cancelled)
            .ToListAsync();
        foreach (var s in stale)
        {
            s.Status = SubscriptionStatus.Expired;
            s.StatusDetail = "Expired";
            await AddEvent(db, s.Id, "expired");
        }
        await db.SaveChangesAsync();
        return Results.Ok(new { expired = stale.Count });
    }

    private static async Task<IResult> MySubscriptions(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http);
        var list = await db.RecurringDonations.AsNoTracking()
            .Where(s => s.DonorId == uid)
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .Select(s => new
            {
                s.Id, s.CampaignId, s.Amount, s.Currency, s.Interval, s.Status,
                s.NextChargeAt, s.LastChargedAt, s.SuccessfulCharges, s.FailedPaymentCount,
                s.PayPalSubscriptionId, s.TrialEndsAt
            }).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> GetSubscription(Guid id, HttpContext http, AppDbContext db)
    {
        var s = await db.RecurringDonations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return Results.NotFound();
        if (s.DonorId != Uid(http) && !http.User.IsInRole("Admin")) return Results.Forbid();
        return Results.Ok(s);
    }

    private static async Task<IResult> BillingHistory(Guid id, HttpContext http, AppDbContext db)
    {
        var s = await db.RecurringDonations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return Results.NotFound();
        if (s.DonorId != Uid(http) && !http.User.IsInRole("Admin")) return Results.Forbid();
        var events = await db.SubscriptionBillingEvents.AsNoTracking()
            .Where(e => e.RecurringDonationId == id)
            .OrderByDescending(e => e.CreatedAt)
            .Take(200)
            .ToListAsync();
        return Results.Ok(events);
    }

    private static async Task<IResult> GetStatus(Guid id, HttpContext http, AppDbContext db, PayPalSubscriptionsClient paypal)
    {
        var s = await db.RecurringDonations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return Results.NotFound();
        string? remote = null;
        if (!string.IsNullOrEmpty(s.PayPalSubscriptionId))
            remote = await paypal.GetSubscriptionStatusAsync(s.PayPalSubscriptionId);
        return Results.Ok(new
        {
            s.Id,
            localStatus = s.Status.ToString(),
            paypalStatus = remote,
            s.NextChargeAt,
            s.FailedPaymentCount,
            s.NextRetryAt,
            s.TrialEndsAt,
            s.ExpiresAt,
            s.StatusDetail
        });
    }

    public record CreatePlanRequest(
        string Name,
        decimal Amount,
        string? Currency = "USD",
        string? IntervalUnit = "MONTH",
        int IntervalCount = 1,
        int TotalCycles = 0,
        int TrialDays = 0,
        decimal SetupFee = 0,
        int PaymentFailureThreshold = 3,
        Guid? CampaignId = null,
        string? Description = null);

    public record StartSubRequest(
        Guid CampaignId,
        Guid? PlanId = null,
        decimal Amount = 0,
        string? Currency = "USD",
        string? IntervalUnit = "MONTH",
        int TrialDays = 0,
        decimal SetupFee = 0);

    public record ChangePlanRequest(Guid NewPlanId);
    public record ReasonBody(string? Reason = null);
}
