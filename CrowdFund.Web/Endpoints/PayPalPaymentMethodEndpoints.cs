using System.Security.Claims;
using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CrowdFund.Web.Endpoints;

public static class PayPalPaymentMethodEndpoints
{
    public static void MapPayPalPaymentMethodEndpoints(this WebApplication app)
    {
        app.MapGet("/api/payments/methods", ListMethods);
        app.MapGet("/api/payments/checkout-config", CheckoutConfig);
        app.MapPost("/api/donations/create-order-extended", CreateOrderExtended).RequireRateLimiting("strict");
        app.MapPost("/api/payments/vault/setup-token", CreateVaultSetup).RequireAuthorization();
        app.MapPost("/api/payments/subscriptions/start", StartPayPalSubscription).RequireAuthorization();
        app.MapPost("/api/payments/subscriptions/{paypalSubscriptionId}/cancel", CancelPayPalSub).RequireAuthorization();
    }

    private static IResult ListMethods(IOptions<PayPalCheckoutOptions> checkout, PayPalAccountService accounts)
    {
        var c = checkout.Value;
        var methods = new List<object>
        {
            new { id = "paypal_checkout", name = "PayPal Checkout", kind = "one_time", enabled = true, description = "Standard PayPal Checkout (Orders API)." },
            new { id = "express_checkout", name = "PayPal Express Checkout", kind = "one_time", enabled = true, description = "Express path via Smart Buttons / wallet (modern replacement for classic Express Checkout)." },
            new { id = "smart_buttons", name = "Smart Payment Buttons", kind = "ui", enabled = c.SmartButtonsEnabled, description = "JS SDK buttons: PayPal, Card, Pay Later, APMs." },
            new { id = "card", name = "Debit & Credit Cards", kind = "one_time", enabled = c.CardFieldsEnabled, description = "Card funding source via Smart Buttons / hosted fields." },
            new { id = "guest", name = "Guest Checkout", kind = "one_time", enabled = c.GuestCheckoutEnabled, description = "Pay without a PayPal account where supported." },
            new { id = "paylater", name = "Pay Later", kind = "one_time", enabled = c.PayLaterEnabled, description = "Pay Later / credit messaging where available by region." },
            new { id = "wallet", name = "PayPal Wallet", kind = "one_time", enabled = true, description = "Balance and linked instruments in PayPal wallet." },
            new { id = "one_time", name = "One-Time Payments", kind = "one_time", enabled = true, description = "Single capture donations." },
            new { id = "recurring", name = "Recurring Payments", kind = "recurring", enabled = c.RecurringEnabled, description = "Monthly subscriptions via PayPal Billing Subscriptions." },
            new { id = "automatic", name = "Automatic Payments", kind = "recurring", enabled = c.RecurringEnabled, description = "Auto-bill outstanding on subscription plans." },
            new { id = "vault", name = "Saved Payment Methods (Vault)", kind = "vault", enabled = c.VaultEnabled, description = "Vault setup tokens for returning donors." },
            new { id = "apm", name = "Alternative Payment Methods", kind = "apm", enabled = true, description = "Regional APMs enabled via enable-funding (iDEAL, Bancontact, etc. when eligible)." }
        };
        return Results.Ok(new
        {
            environment = accounts.GetStatus().Environment,
            simulate = accounts.IsSimulated,
            methods
        });
    }

    private static IResult CheckoutConfig(IOptions<PayPalOptions> opts, IOptions<PayPalCheckoutOptions> checkout, PayPalAccountService accounts)
    {
        var o = opts.Value;
        var c = checkout.Value;
        return Results.Ok(new
        {
            clientId = accounts.IsSimulated ? "sb" : o.ClientId,
            mode = string.Equals(o.Mode, "live", StringComparison.OrdinalIgnoreCase) ? "live" : "sandbox",
            simulate = accounts.IsSimulated,
            currency = "USD",
            intent = "capture",
            components = "buttons,messages,funding-eligibility",
            enableFunding = c.EnableFunding,
            disableFunding = c.DisableFunding,
            smartButtons = new
            {
                enabled = c.SmartButtonsEnabled,
                layout = c.Layout,
                color = c.Color,
                shape = c.Shape,
                label = c.Label
            },
            guestCheckout = c.GuestCheckoutEnabled,
            payLater = c.PayLaterEnabled,
            cards = c.CardFieldsEnabled,
            vault = c.VaultEnabled,
            recurring = c.RecurringEnabled,
            brandName = o.BrandName
        });
    }

    private static async Task<IResult> CreateOrderExtended(
        [FromBody] ExtendedOrderRequest req,
        AppDbContext db,
        PayPalClient paypal,
        CrowdFund.Core.Services.DonationService donationSvc,
        CrowdFund.Core.Services.FeeTransparencyService fees,
        CrowdFund.Core.Services.MatchingGiftService matchSvc,
        HttpContext http)
    {
        if (req.Amount <= 0 || req.Amount > 100_000)
            return Results.BadRequest(new { error = "Invalid amount" });
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == req.CampaignId);
        if (campaign == null) return Results.NotFound();
        if (campaign.Status != CampaignStatus.Published)
            return Results.BadRequest(new { error = "Campaign is not accepting donations" });
        if (campaign.FundsFrozen)
            return Results.BadRequest(new { error = "Campaign funds are frozen" });

        var tip = req.TipAmount < 0 ? 0 : req.TipAmount;
        var breakdown = fees.Calculate(req.Amount, tip, req.Currency ?? "USD");
        var donorId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var donation = donationSvc.CreatePending(req.CampaignId, donorId, req.Amount, req.Currency ?? campaign.Currency);
        donation.TipAmount = breakdown.TipAmount;
        donation.PlatformFee = breakdown.PlatformFee;
        donation.PaymentProcessorFeeEstimate = breakdown.ProcessorFeeEstimate;
        donation.NetToCampaign = breakdown.NetToCampaign;
        db.Donations.Add(donation);
        await db.SaveChangesAsync();

        try
        {
            var order = await paypal.CreateOrderAsync(
                donation.Id,
                donation.Amount,
                donation.Currency,
                $"Donation to: {campaign.Title}",
                req.MerchantKey);
            donation.PayPalOrderId = order.OrderId;
            await db.SaveChangesAsync();
            return Results.Ok(new
            {
                donationId = donation.Id,
                orderId = order.OrderId,
                status = order.Status,
                amount = donation.Amount,
                currency = donation.Currency,
                paymentMethod = req.PreferredMethod ?? "paypal_checkout",
                fees = breakdown,
                simulated = paypal.IsSimulated
            });
        }
        catch (Exception ex)
        {
            donation.Status = DonationStatus.Failed;
            await db.SaveChangesAsync();
            return Results.Problem(ex.Message);
        }
    }

    private static async Task<IResult> CreateVaultSetup(
        HttpContext http, PayPalVaultClient vault, IOptions<PayPalCheckoutOptions> checkout)
    {
        if (!checkout.Value.VaultEnabled)
            return Results.BadRequest(new { error = "Vault is disabled" });
        var uid = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.NewGuid().ToString("N");
        var result = await vault.CreateSetupTokenAsync(uid);
        return Results.Ok(result);
    }

    private static async Task<IResult> StartPayPalSubscription(
        [FromBody] StartSubRequest req,
        HttpContext http,
        AppDbContext db,
        PayPalSubscriptionsClient subs,
        IOptions<PayPalCheckoutOptions> checkout,
        IOptions<PayPalOptions> paypalOpts)
    {
        if (!checkout.Value.RecurringEnabled)
            return Results.BadRequest(new { error = "Recurring payments disabled" });
        if (req.Amount <= 0) return Results.BadRequest(new { error = "Invalid amount" });
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CampaignId);
        if (campaign == null) return Results.NotFound();
        var uid = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (planId, productId) = await subs.EnsureMonthlyPlanAsync(
            $"CrowdFund · {campaign.Title}",
            req.Amount,
            req.Currency ?? campaign.Currency);
        var returnUrl = string.IsNullOrWhiteSpace(paypalOpts.Value.ReturnUrl)
            ? "https://localhost:5001/donate/return"
            : paypalOpts.Value.ReturnUrl;
        var cancelUrl = paypalOpts.Value.CancelUrl;
        var created = await subs.CreateSubscriptionAsync(planId, $"{req.CampaignId}:{uid}", returnUrl, cancelUrl);

        var local = new RecurringDonation
        {
            CampaignId = req.CampaignId,
            DonorId = uid,
            Amount = req.Amount,
            Currency = req.Currency ?? campaign.Currency,
            Status = SubscriptionStatus.Active,
            PayPalSubscriptionId = created.SubscriptionId,
            Interval = "monthly",
            NextChargeAt = DateTime.UtcNow.AddMonths(1)
        };
        db.RecurringDonations.Add(local);
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            localId = local.Id,
            paypalSubscriptionId = created.SubscriptionId,
            status = created.Status,
            approveUrl = created.ApproveUrl,
            planId,
            productId,
            simulated = subs.IsSimulated
        });
    }

    private static async Task<IResult> CancelPayPalSub(
        string paypalSubscriptionId,
        HttpContext http,
        AppDbContext db,
        PayPalSubscriptionsClient subs)
    {
        var uid = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var local = await db.RecurringDonations
            .FirstOrDefaultAsync(s => s.PayPalSubscriptionId == paypalSubscriptionId && s.DonorId == uid);
        if (local == null && !http.User.IsInRole("Admin"))
            return Results.NotFound();
        await subs.CancelSubscriptionAsync(paypalSubscriptionId, "Donor requested cancellation");
        if (local != null)
        {
            local.Status = SubscriptionStatus.Cancelled;
            await db.SaveChangesAsync();
        }
        return Results.Ok(new { cancelled = true, paypalSubscriptionId });
    }

    public record ExtendedOrderRequest(
        Guid CampaignId,
        decimal Amount,
        string? Currency = "USD",
        decimal TipAmount = 0,
        string? PreferredMethod = "paypal_checkout",
        string? MerchantKey = null);

    public record StartSubRequest(Guid CampaignId, decimal Amount, string? Currency = "USD");
}
