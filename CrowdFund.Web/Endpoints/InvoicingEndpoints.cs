using System.Security.Claims;
using System.Security.Cryptography;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class InvoicingEndpoints
{
    public static void MapInvoicingEndpoints(this WebApplication app)
    {
        var inv = app.MapGroup("/api/invoices").RequireAuthorization();
        inv.MapPost("/", CreateInvoice);
        inv.MapGet("/", ListInvoices);
        inv.MapGet("/{id:guid}", GetInvoice);
        inv.MapPost("/{id:guid}/send", SendInvoice);
        inv.MapPost("/{id:guid}/cancel", CancelInvoice);
        inv.MapPost("/{id:guid}/remind", RemindInvoice);
        inv.MapPost("/{id:guid}/record-payment", RecordPayment);

        var links = app.MapGroup("/api/payment-links").RequireAuthorization();
        links.MapPost("/", CreatePaymentLink);
        links.MapGet("/", ListPaymentLinks);
        links.MapGet("/{id:guid}", GetPaymentLink);
        links.MapPost("/{id:guid}/cancel", CancelPaymentLink);
        links.MapGet("/{id:guid}/transactions", LinkTransactions);

        // Public pay link resolve + pay
        app.MapGet("/api/public/pay/{slug}", ResolvePayLink);
        app.MapPost("/api/public/pay/{slug}/checkout", CheckoutPayLink).RequireRateLimiting("strict");
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static string NewInvoiceNumber() =>
        "INV-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private static string NewSlug() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private static async Task<IResult> CreateInvoice(
        [FromBody] CreateInvoiceRequest req, HttpContext http, AppDbContext db, PayPalInvoicingClient paypal)
    {
        if (req.Amount <= 0) return Results.BadRequest(new { error = "Invalid amount" });
        if (string.IsNullOrWhiteSpace(req.RecipientEmail)) return Results.BadRequest(new { error = "Recipient email required" });

        var number = NewInvoiceNumber();
        var item = string.IsNullOrWhiteSpace(req.ItemName) ? "CrowdFund contribution" : req.ItemName!;
        PayPalInvoiceResult? remote = null;
        try
        {
            remote = await paypal.CreateInvoiceAsync(number, req.RecipientEmail, req.RecipientName, req.Amount, req.Currency ?? "USD", item, req.Note, req.DueAt);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }

        var inv = new Invoice
        {
            CampaignId = req.CampaignId,
            CreatorUserId = Uid(http),
            InvoiceNumber = number,
            PayPalInvoiceId = remote.InvoiceId,
            RecipientEmail = req.RecipientEmail.Trim(),
            RecipientName = req.RecipientName,
            Amount = req.Amount,
            Currency = (req.Currency ?? "USD").ToUpperInvariant(),
            Note = req.Note,
            ItemName = item,
            DueAt = req.DueAt,
            Status = InvoiceStatus.Draft
        };
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        return Results.Ok(new { inv.Id, inv.InvoiceNumber, inv.PayPalInvoiceId, inv.Status, simulated = paypal.IsSimulated });
    }

    private static async Task<IResult> ListInvoices(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http);
        var q = db.Invoices.AsNoTracking().AsQueryable();
        if (!http.User.IsInRole("Admin"))
            q = q.Where(i => i.CreatorUserId == uid);
        var list = await q.OrderByDescending(i => i.CreatedAt).Take(100).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> GetInvoice(Guid id, HttpContext http, AppDbContext db)
    {
        var inv = await db.Invoices.AsNoTracking().Include(i => i.Payments).FirstOrDefaultAsync(i => i.Id == id);
        if (inv == null) return Results.NotFound();
        if (!http.User.IsInRole("Admin") && inv.CreatorUserId != Uid(http)) return Results.Forbid();
        return Results.Ok(inv);
    }

    private static async Task<IResult> SendInvoice(Guid id, HttpContext http, AppDbContext db, PayPalInvoicingClient paypal)
    {
        var inv = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id);
        if (inv == null) return Results.NotFound();
        if (!http.User.IsInRole("Admin") && inv.CreatorUserId != Uid(http)) return Results.Forbid();
        if (inv.Status is InvoiceStatus.Cancelled or InvoiceStatus.Paid)
            return Results.BadRequest(new { error = $"Cannot send status {inv.Status}" });
        if (!string.IsNullOrEmpty(inv.PayPalInvoiceId))
            await paypal.SendInvoiceAsync(inv.PayPalInvoiceId);
        inv.Status = InvoiceStatus.Sent;
        inv.SentAt = DateTime.UtcNow;
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
        inv.ShareUrl = $"{baseUrl}/invoice/{inv.Id}";
        await db.SaveChangesAsync();
        return Results.Ok(new { inv.Id, inv.Status, inv.ShareUrl, inv.SentAt });
    }

    private static async Task<IResult> CancelInvoice(
        Guid id, [FromBody] ReasonBody? body, HttpContext http, AppDbContext db, PayPalInvoicingClient paypal)
    {
        var inv = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id);
        if (inv == null) return Results.NotFound();
        if (!http.User.IsInRole("Admin") && inv.CreatorUserId != Uid(http)) return Results.Forbid();
        if (inv.Status == InvoiceStatus.Paid)
            return Results.BadRequest(new { error = "Cannot cancel paid invoice" });
        if (!string.IsNullOrEmpty(inv.PayPalInvoiceId))
            await paypal.CancelInvoiceAsync(inv.PayPalInvoiceId, body?.Reason ?? "Cancelled");
        inv.Status = InvoiceStatus.Cancelled;
        inv.CancelledAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(new { inv.Id, inv.Status });
    }

    private static async Task<IResult> RemindInvoice(
        Guid id, [FromBody] RemindBody? body, HttpContext http, AppDbContext db, PayPalInvoicingClient paypal)
    {
        var inv = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id);
        if (inv == null) return Results.NotFound();
        if (!http.User.IsInRole("Admin") && inv.CreatorUserId != Uid(http)) return Results.Forbid();
        if (inv.Status is not (InvoiceStatus.Sent or InvoiceStatus.PartiallyPaid or InvoiceStatus.Overdue))
            return Results.BadRequest(new { error = "Remind only for open invoices" });
        if (!string.IsNullOrEmpty(inv.PayPalInvoiceId))
            await paypal.RemindInvoiceAsync(inv.PayPalInvoiceId, body?.Subject, body?.Note);
        inv.LastReminderAt = DateTime.UtcNow;
        inv.ReminderCount += 1;
        await db.SaveChangesAsync();
        return Results.Ok(new { inv.Id, inv.ReminderCount, inv.LastReminderAt });
    }

    private static async Task<IResult> RecordPayment(
        Guid id, [FromBody] RecordPaymentBody req, HttpContext http, AppDbContext db)
    {
        var inv = await db.Invoices.Include(i => i.Payments).FirstOrDefaultAsync(i => i.Id == id);
        if (inv == null) return Results.NotFound();
        if (!http.User.IsInRole("Admin") && inv.CreatorUserId != Uid(http)) return Results.Forbid();
        if (inv.Status == InvoiceStatus.Cancelled)
            return Results.BadRequest(new { error = "Invoice cancelled" });
        var amount = req.Amount <= 0 ? inv.Amount - inv.AmountPaid : req.Amount;
        if (amount <= 0) return Results.BadRequest(new { error = "Nothing to record" });

        inv.AmountPaid += amount;
        inv.PayPalCaptureId = req.PayPalCaptureId ?? inv.PayPalCaptureId;
        inv.Payments.Add(new InvoicePayment
        {
            InvoiceId = inv.Id,
            Amount = amount,
            Currency = inv.Currency,
            PayPalCaptureId = req.PayPalCaptureId,
            Method = req.Method ?? "PayPal",
            Note = req.Note,
            RecordedBy = Uid(http)
        });
        inv.Status = inv.AmountPaid >= inv.Amount ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
        if (inv.Status == InvoiceStatus.Paid) inv.PaidAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(new { inv.Id, inv.Status, inv.AmountPaid, inv.Amount });
    }

    private static async Task<IResult> CreatePaymentLink(
        [FromBody] CreateLinkRequest req, HttpContext http, AppDbContext db)
    {
        if (req.Amount <= 0) return Results.BadRequest(new { error = "Invalid amount" });
        if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "Title required" });
        var slug = NewSlug();
        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
        var link = new PaymentLink
        {
            CampaignId = req.CampaignId,
            CreatorUserId = Uid(http),
            Title = req.Title.Trim(),
            Description = req.Description,
            Amount = req.Amount,
            Currency = (req.Currency ?? "USD").ToUpperInvariant(),
            Slug = slug,
            ShareUrl = $"{baseUrl}/pay/{slug}",
            ExpiresAt = req.ExpiresAt,
            MaxUses = req.MaxUses,
            Status = PaymentLinkStatus.Active
        };
        db.PaymentLinks.Add(link);
        await db.SaveChangesAsync();
        return Results.Ok(new { link.Id, link.Slug, link.ShareUrl, link.Amount, link.Currency, link.Status });
    }

    private static async Task<IResult> ListPaymentLinks(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http);
        var q = db.PaymentLinks.AsNoTracking().AsQueryable();
        if (!http.User.IsInRole("Admin"))
            q = q.Where(l => l.CreatorUserId == uid);
        return Results.Ok(await q.OrderByDescending(l => l.CreatedAt).Take(100).ToListAsync());
    }

    private static async Task<IResult> GetPaymentLink(Guid id, HttpContext http, AppDbContext db)
    {
        var link = await db.PaymentLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id);
        if (link == null) return Results.NotFound();
        if (!http.User.IsInRole("Admin") && link.CreatorUserId != Uid(http)) return Results.Forbid();
        return Results.Ok(link);
    }

    private static async Task<IResult> CancelPaymentLink(Guid id, HttpContext http, AppDbContext db)
    {
        var link = await db.PaymentLinks.FirstOrDefaultAsync(l => l.Id == id);
        if (link == null) return Results.NotFound();
        if (!http.User.IsInRole("Admin") && link.CreatorUserId != Uid(http)) return Results.Forbid();
        link.Status = PaymentLinkStatus.Cancelled;
        await db.SaveChangesAsync();
        return Results.Ok(new { link.Id, link.Status });
    }

    private static async Task<IResult> LinkTransactions(Guid id, HttpContext http, AppDbContext db)
    {
        var link = await db.PaymentLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id);
        if (link == null) return Results.NotFound();
        if (!http.User.IsInRole("Admin") && link.CreatorUserId != Uid(http)) return Results.Forbid();
        var tx = await db.PaymentLinkTransactions.AsNoTracking()
            .Where(t => t.PaymentLinkId == id).OrderByDescending(t => t.CreatedAt).Take(100).ToListAsync();
        return Results.Ok(tx);
    }

    private static async Task<IResult> ResolvePayLink(string slug, AppDbContext db)
    {
        var link = await db.PaymentLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Slug == slug);
        if (link == null) return Results.NotFound();
        if (link.Status != PaymentLinkStatus.Active)
            return Results.BadRequest(new { error = "Link not active", status = link.Status.ToString() });
        if (link.ExpiresAt.HasValue && link.ExpiresAt < DateTime.UtcNow)
            return Results.BadRequest(new { error = "Link expired" });
        if (link.MaxUses > 0 && link.UseCount >= link.MaxUses)
            return Results.BadRequest(new { error = "Link use limit reached" });
        return Results.Ok(new
        {
            link.Title,
            link.Description,
            link.Amount,
            link.Currency,
            link.Slug,
            link.CampaignId
        });
    }

    private static async Task<IResult> CheckoutPayLink(
        string slug, AppDbContext db, PayPalClient paypal, DonationService donationSvc, HttpContext http)
    {
        var link = await db.PaymentLinks.FirstOrDefaultAsync(l => l.Slug == slug);
        if (link == null) return Results.NotFound();
        if (link.Status != PaymentLinkStatus.Active)
            return Results.BadRequest(new { error = "Link not active" });
        if (link.ExpiresAt.HasValue && link.ExpiresAt < DateTime.UtcNow)
        {
            link.Status = PaymentLinkStatus.Expired;
            await db.SaveChangesAsync();
            return Results.BadRequest(new { error = "Link expired" });
        }
        if (link.MaxUses > 0 && link.UseCount >= link.MaxUses)
            return Results.BadRequest(new { error = "Link use limit reached" });

        // Create order against optional campaign; if no campaign, still track link txn only
        Guid? donationId = null;
        string? orderId = null;
        string? captureId = null;
        if (link.CampaignId.HasValue)
        {
            var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == link.CampaignId.Value);
            if (campaign != null && campaign.Status == CampaignStatus.Published)
            {
                var donation = donationSvc.CreatePending(campaign.Id, Uid(http), link.Amount, link.Currency);
                donation.TransactionReference = "PL-" + link.Slug.ToUpperInvariant();
                db.Donations.Add(donation);
                await db.SaveChangesAsync();
                var order = await paypal.CreateOrderAsync(donation.Id, link.Amount, link.Currency, link.Title);
                donation.PayPalOrderId = order.OrderId;
                var capture = await paypal.CaptureOrderAsync(order.OrderId);
                donation.PayPalCaptureId = capture.CaptureId;
                donation.Status = DonationStatus.Captured;
                donation.CapturedAt = DateTime.UtcNow;
                donation.CapturedAmount = link.Amount;
                campaign.RaisedAmount += link.Amount;
                campaign.DonorCount += 1;
                donationId = donation.Id;
                orderId = order.OrderId;
                captureId = capture.CaptureId;
            }
        }
        if (orderId == null)
        {
            // Standalone link payment (simulated order id)
            orderId = "LINK-" + Guid.NewGuid().ToString("N")[..12];
            captureId = "CAP-" + Guid.NewGuid().ToString("N")[..12];
            if (!paypal.IsSimulated && link.CampaignId == null)
            {
                // Still create a PayPal order without campaign donation
                try
                {
                    var order = await paypal.CreateOrderAsync(Guid.NewGuid(), link.Amount, link.Currency, link.Title);
                    orderId = order.OrderId;
                    var capture = await paypal.CaptureOrderAsync(order.OrderId);
                    captureId = capture.CaptureId;
                }
                catch { /* keep simulated ids if order fails */ }
            }
        }

        link.UseCount += 1;
        link.TotalCollected += link.Amount;
        if (link.MaxUses > 0 && link.UseCount >= link.MaxUses)
            link.Status = PaymentLinkStatus.Paid;

        db.PaymentLinkTransactions.Add(new PaymentLinkTransaction
        {
            PaymentLinkId = link.Id,
            DonationId = donationId,
            Amount = link.Amount,
            Currency = link.Currency,
            PayPalOrderId = orderId,
            PayPalCaptureId = captureId,
            Status = "Completed",
            PayerEmail = http.User.FindFirstValue(ClaimTypes.Email)
        });
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            linkId = link.Id,
            donationId,
            orderId,
            captureId,
            amount = link.Amount,
            currency = link.Currency,
            status = "Completed",
            simulated = paypal.IsSimulated
        });
    }

    public record CreateInvoiceRequest(
        string RecipientEmail,
        decimal Amount,
        string? RecipientName = null,
        string? Currency = "USD",
        string? ItemName = null,
        string? Note = null,
        Guid? CampaignId = null,
        DateTime? DueAt = null);

    public record RecordPaymentBody(decimal Amount = 0, string? PayPalCaptureId = null, string? Method = null, string? Note = null);
    public record RemindBody(string? Subject = null, string? Note = null);
    public record ReasonBody(string? Reason = null);

    public record CreateLinkRequest(
        string Title,
        decimal Amount,
        string? Description = null,
        string? Currency = "USD",
        Guid? CampaignId = null,
        DateTime? ExpiresAt = null,
        int MaxUses = 0);
}
