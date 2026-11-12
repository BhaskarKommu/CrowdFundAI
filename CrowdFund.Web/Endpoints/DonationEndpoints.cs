using CrowdFund.Web.Observability;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class DonationEndpoints
{
    public static void MapDonationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/donations");

        group.MapPost("/create-order", CreateOrder).RequireRateLimiting("strict");
        group.MapPost("/capture", Capture).RequireRateLimiting("strict");
        group.MapGet("/mine", GetMine).RequireAuthorization();
        group.MapGet("/{id:guid}/receipt", GetReceipt);

        app.MapGet("/api/ledger/{campaignId:guid}/verify", VerifyLedger);
        app.MapGet("/api/ledger/{campaignId:guid}", GetLedger);
    }

    private static async Task<IResult> CreateOrder(
        [FromBody] CreateOrderRequest req,
        AppDbContext db,
        PayPalClient paypal,
        DonationService donationSvc,
        FeeTransparencyService fees,
        MatchingGiftService matchSvc,
        HttpContext http)
    {
        if (req.Amount <= 0 || req.Amount > 100_000)
            return Results.BadRequest(new { error = "Invalid amount" });

        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == req.CampaignId);
        if (campaign == null)
            return Results.NotFound(new { error = "Campaign not found" });
        if (campaign.Status != CampaignStatus.Published)
            return Results.BadRequest(new { error = "Campaign is not accepting donations" });
        if (campaign.FundsFrozen || campaign.Status == CampaignStatus.Frozen)
            return Results.BadRequest(new { error = "Campaign funds are frozen pending review" });

        var tip = req.TipAmount < 0 ? 0 : req.TipAmount;
        var breakdown = fees.Calculate(req.Amount, tip, req.Currency ?? "USD");
        var donorId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var donation = donationSvc.CreatePending(req.CampaignId, donorId, req.Amount, req.Currency ?? "USD");
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
                $"Donation to: {campaign.Title}");

            donation.PayPalOrderId = order.OrderId;
            await db.SaveChangesAsync();

            var isFirstTime = string.IsNullOrEmpty(donorId) ||
                !await db.Donations.AsNoTracking().AnyAsync(d =>
                    d.DonorId == donorId && d.Status == DonationStatus.Captured && d.Id != donation.Id);
            var poolCandidates = await db.MatchingGiftPrograms.AsNoTracking()
                .Where(p => p.Status == MatchCampaignStatus.Active &&
                            (p.CampaignId == campaign.Id || p.CampaignId == null))
                .ToListAsync();
            var program = matchSvc.SelectBestProgram(poolCandidates, campaign, donation.Amount, isFirstTime);
            var (matchPreview, _, _) = matchSvc.TryMatch(program, donation.Amount, campaign, isFirstTime);

            return Results.Ok(new
            {
                donationId = donation.Id,
                orderId = order.OrderId,
                status = order.Status,
                amount = donation.Amount,
                tip = donation.TipAmount,
                fees = breakdown,
                matchingPreview = matchPreview,
                matchSponsor = program?.SponsorName,
                matchHeadline = program?.Headline,
                totalImpactIfMatched = donation.Amount + matchPreview,
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

    private static async Task<IResult> Capture(
        [FromBody] CaptureRequest req,
        AppDbContext db,
        PayPalClient paypal,
        DonationService donationSvc,
        LedgerService ledger,
        MatchingGiftService matchSvc,
        AppMetrics metrics,
        AlertService alerts)
    {
        var donation = await db.Donations
            .Include(d => d.Campaign)
            .FirstOrDefaultAsync(d => d.Id == req.DonationId);

        if (donation == null)
            return Results.NotFound(new { error = "Donation not found" });
        if (donation.Status == DonationStatus.Captured)
            return Results.Ok(new { donationId = donation.Id, status = "already_captured", captureId = donation.PayPalCaptureId });
        if (string.IsNullOrEmpty(donation.PayPalOrderId))
            return Results.BadRequest(new { error = "No PayPal order" });

        // Idempotency: if capture ID already exists, short-circuit
        if (!string.IsNullOrEmpty(req.OrderId) && req.OrderId != donation.PayPalOrderId)
            return Results.BadRequest(new { error = "Order ID mismatch" });

        try
        {
            var capture = await paypal.CaptureOrderAsync(donation.PayPalOrderId);
            if (!capture.IsCompleted)
            {
                donation.Status = DonationStatus.Failed;
                await db.SaveChangesAsync();
                metrics.CaptureFailed();
                await alerts.NotifyAsync("warning", "PayPal capture not completed", new { req.DonationId, capture.Status });
                return Results.BadRequest(new { error = "Capture not completed", status = capture.Status });
            }

            // Unique constraint protects against double-capture
            if (await db.Donations.AnyAsync(d => d.PayPalCaptureId == capture.CaptureId))
                return Results.Ok(new { donationId = donation.Id, status = "already_captured" });

            var prevHash = await db.AuditLedger
                .Where(e => e.CampaignId == donation.CampaignId)
                .OrderByDescending(e => e.Id)
                .Select(e => e.Hash)
                .FirstOrDefaultAsync() ?? "";

            var entry = donationSvc.MarkCaptured(donation, capture.CaptureId, null, prevHash);
            db.AuditLedger.Add(entry);

            if (donation.Campaign != null)
            {
                donation.Campaign.RaisedAmount += donation.Amount;
                donation.Campaign.DonorCount += 1;
            }

            // Sponsored matching pools (campaign-specific or platform-wide)
            var isFirstTime = string.IsNullOrEmpty(donation.DonorId) ||
                !await db.Donations.AnyAsync(d =>
                    d.DonorId == donation.DonorId && d.Status == DonationStatus.Captured && d.Id != donation.Id);
            var poolCandidates = await db.MatchingGiftPrograms
                .Where(p => p.Status == MatchCampaignStatus.Active &&
                            (p.CampaignId == donation.CampaignId || p.CampaignId == null))
                .ToListAsync();
            var program = donation.Campaign == null ? null
                : matchSvc.SelectBestProgram(poolCandidates, donation.Campaign, donation.Amount, isFirstTime);
            var (matched, prog, _) = matchSvc.TryMatch(program, donation.Amount, donation.Campaign, isFirstTime);
            if (matched > 0 && prog != null && donation.Campaign != null)
            {
                matchSvc.ApplyMatch(prog, matched);
                donation.MatchedAmount = matched;
                donation.MatchingGiftProgramId = prog.Id;
                donation.Campaign.RaisedAmount += matched;
                db.MatchingGiftLedger.Add(new MatchingGiftLedgerEntry
                {
                    MatchingGiftProgramId = prog.Id,
                    DonationId = donation.Id,
                    CampaignId = donation.CampaignId,
                    DonorAmount = donation.Amount,
                    MatchedAmount = matched
                });
            }

            await db.SaveChangesAsync();

            if (donation.Campaign != null && donation.Campaign.RaisedAmount >= donation.Campaign.GoalAmount && donation.Campaign.GoalAmount > 0)
            {
                var donorIds = await db.Donations.AsNoTracking()
                    .Where(d => d.CampaignId == donation.CampaignId && d.DonorId != null)
                    .Select(d => d.DonorId!)
                    .Distinct()
                    .ToListAsync();
                await GrowthEndpoints.QueuePushAsync(db, "GoalReached", "Goal reached!",
                    $"{donation.Campaign.Title} reached its funding goal.", donation.CampaignId, donorIds);
            }

            metrics.CaptureOk();
            return Results.Ok(new
            {
                donationId = donation.Id,
                captureId = capture.CaptureId,
                status = "Captured",
                amount = donation.Amount,
                currency = donation.Currency,
                tip = donation.TipAmount,
                platformFee = donation.PlatformFee,
                processorFeeEstimate = donation.PaymentProcessorFeeEstimate,
                netToCampaign = donation.NetToCampaign,
                matchedAmount = donation.MatchedAmount,
                ledgerHash = entry.Hash,
                simulated = paypal.IsSimulated
            });
        }
        catch (Exception ex)
        {
            metrics.CaptureFailed();
            await alerts.NotifyAsync("critical", "Donation capture exception", new { req.DonationId, error = ex.Message });
            return Results.Problem(ex.Message);
        }
    }

    private static async Task<IResult> GetMine(AppDbContext db, HttpContext http)
    {
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var list = await db.Donations
            .AsNoTracking()
            .Where(d => d.DonorId == userId && d.Status == DonationStatus.Captured)
            .OrderByDescending(d => d.CapturedAt)
            .Select(d => new
            {
                d.Id,
                d.CampaignId,
                d.Amount,
                d.Currency,
                d.CapturedAt,
                CampaignTitle = d.Campaign != null ? d.Campaign.Title : ""
            })
            .ToListAsync();

        return Results.Ok(list);
    }

    private static async Task<IResult> GetReceipt(Guid id, AppDbContext db)
    {
        var d = await db.Donations
            .AsNoTracking()
            .Include(x => x.Campaign)
            .FirstOrDefaultAsync(x => x.Id == id && x.Status == DonationStatus.Captured);

        if (d == null) return Results.NotFound();

        return Results.Ok(new
        {
            d.Id,
            d.Amount,
            d.Currency,
            d.CapturedAt,
            d.PayPalCaptureId,
            CampaignTitle = d.Campaign?.Title,
            CampaignId = d.CampaignId,
            Message = "Thank you for your support. This receipt is issued after server-side capture verification."
        });
    }

    private static async Task<IResult> VerifyLedger(Guid campaignId, AppDbContext db, LedgerService ledger, AppMetrics metrics, AlertService alerts)
    {
        var entries = await db.AuditLedger
            .Where(e => e.CampaignId == campaignId)
            .OrderBy(e => e.Id)
            .ToListAsync();

        var (valid, brokenAt) = ledger.VerifyChain(entries);
        if (!valid)
        {
            metrics.LedgerBreak();
            await alerts.NotifyAsync("critical", "Ledger chain break detected", new { campaignId, brokenAt });
        }
        return Results.Ok(new
        {
            campaignId,
            valid,
            brokenAtId = brokenAt,
            entryCount = entries.Count,
            headHash = entries.LastOrDefault()?.Hash
        });
    }

    private static async Task<IResult> GetLedger(Guid campaignId, AppDbContext db)
    {
        var entries = await db.AuditLedger
            .AsNoTracking()
            .Where(e => e.CampaignId == campaignId)
            .OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.EventType, e.Hash, e.PrevHash, e.CreatedAt })
            .ToListAsync();
        return Results.Ok(entries);
    }
}

public record CreateOrderRequest(Guid CampaignId, decimal Amount, string? Currency = "USD", decimal TipAmount = 0);
public record CaptureRequest(Guid DonationId, string? OrderId = null);
