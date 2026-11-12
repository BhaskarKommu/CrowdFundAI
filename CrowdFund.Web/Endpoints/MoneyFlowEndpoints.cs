using System.Security.Claims;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class MoneyFlowEndpoints
{
    public static void MapMoneyFlowEndpoints(this WebApplication app)
    {
        // Fees
        app.MapGet("/api/fees/quote", QuoteFees);

        // Milestones + tranche release (not regulated escrow)
        app.MapGet("/api/campaigns/{campaignId:guid}/milestones", ListMilestones);
        app.MapPost("/api/campaigns/{campaignId:guid}/milestones", CreateMilestone).RequireAuthorization();
        app.MapPost("/api/milestones/{id:guid}/releases", RequestRelease).RequireAuthorization();
        app.MapPost("/api/admin/releases/{id:guid}/decision", ReleaseDecision).RequireAuthorization();
        app.MapGet("/api/campaigns/{campaignId:guid}/releases", ListReleases);

        // Recurring
        app.MapPost("/api/campaigns/{campaignId:guid}/subscriptions", StartSubscription).RequireAuthorization();
        app.MapGet("/api/me/subscriptions", MySubscriptions).RequireAuthorization();
        app.MapPost("/api/subscriptions/{id:guid}/cancel", CancelSubscription).RequireAuthorization();
        app.MapPost("/api/admin/subscriptions/process-due", ProcessDueSubscriptions);

        // Matching gifts
        app.MapGet("/api/campaigns/{campaignId:guid}/matching", ListMatching);
        app.MapPost("/api/campaigns/{campaignId:guid}/matching", CreateMatching).RequireAuthorization();
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static IResult QuoteFees(
        [FromQuery] decimal amount,
        [FromQuery] decimal tip = 0,
        [FromQuery] string currency = "USD",
        FeeTransparencyService fees = null!)
    {
        if (amount <= 0) return Results.BadRequest(new { error = "amount > 0 required" });
        return Results.Ok(fees.Calculate(amount, tip, currency));
    }

    private static async Task<IResult> ListMilestones(Guid campaignId, AppDbContext db)
    {
        var list = await db.Milestones.AsNoTracking()
            .Where(m => m.CampaignId == campaignId)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.TargetAmount)
            .ToListAsync();
        var releases = await db.MilestoneReleases.AsNoTracking()
            .Where(r => r.CampaignId == campaignId).ToListAsync();
        return Results.Ok(new
        {
            disclaimer = "Tranche releases are platform workflow gates—not regulated escrow or a bank product.",
            milestones = list,
            releases
        });
    }

    private static async Task<IResult> CreateMilestone(
        Guid campaignId, [FromBody] MilestoneRequest req, HttpContext http, AppDbContext db)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        if (campaign.CreatorId != Uid(http) && !http.User.IsInRole("Admin"))
            return Results.Forbid();
        var m = new Milestone
        {
            CampaignId = campaignId,
            Title = req.Title?.Trim() ?? "Milestone",
            Description = req.Description,
            TargetAmount = req.TargetAmount,
            SortOrder = req.SortOrder,
            Status = "Pending"
        };
        db.Milestones.Add(m);
        await db.SaveChangesAsync();
        return Results.Ok(m);
    }

    private static async Task<IResult> RequestRelease(
        Guid id, [FromBody] ReleaseRequest req, HttpContext http, AppDbContext db)
    {
        var m = await db.Milestones.FirstOrDefaultAsync(x => x.Id == id);
        if (m == null) return Results.NotFound();
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == m.CampaignId);
        if (campaign?.CreatorId != Uid(http) && !http.User.IsInRole("Admin"))
            return Results.Forbid();

        // Proof → under review
        if (req.MultimodalProofId.HasValue)
        {
            var proof = await db.MultimodalProofs.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == req.MultimodalProofId && p.CampaignId == m.CampaignId);
            if (proof == null) return Results.BadRequest(new { error = "Proof not found" });
        }

        var amount = req.Amount <= 0 ? m.TargetAmount - m.ReleasedAmount : req.Amount;
        if (amount <= 0) return Results.BadRequest(new { error = "Nothing left to release" });

        var rel = new MilestoneRelease
        {
            MilestoneId = m.Id,
            CampaignId = m.CampaignId,
            Amount = amount,
            Currency = campaign?.Currency ?? "USD",
            Status = MilestoneReleaseStatus.UnderReview,
            MultimodalProofId = req.MultimodalProofId,
            ExpenseId = req.ExpenseId
        };
        m.Status = "UnderReview";
        db.MilestoneReleases.Add(rel);
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            rel,
            disclaimer = "Release is a platform verification step before creator payout eligibility for this tranche—not bank escrow."
        });
    }

    private static async Task<IResult> ReleaseDecision(
        Guid id, [FromBody] ReleaseDecisionRequest req, HttpContext http, AppDbContext db, LedgerService ledger)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var rel = await db.MilestoneReleases.FirstOrDefaultAsync(r => r.Id == id);
        if (rel == null) return Results.NotFound();
        var m = await db.Milestones.FirstOrDefaultAsync(x => x.Id == rel.MilestoneId);
        if (m == null) return Results.NotFound();

        if (string.Equals(req.Decision, "approve", StringComparison.OrdinalIgnoreCase))
        {
            // Optional: require human-approved proof
            if (rel.MultimodalProofId.HasValue)
            {
                var proof = await db.MultimodalProofs.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == rel.MultimodalProofId);
                if (proof == null || proof.Status != MultimodalProofStatus.Approved)
                    return Results.BadRequest(new { error = "Attached proof must be human-approved before release" });
            }

            rel.Status = MilestoneReleaseStatus.Released;
            rel.ReviewedBy = Uid(http);
            rel.ReviewedAt = DateTime.UtcNow;
            rel.ReviewNote = req.Note;
            rel.ReleasedAt = DateTime.UtcNow;
            m.ReleasedAmount += rel.Amount;
            m.Status = m.ReleasedAmount >= m.TargetAmount ? "Released" : "Approved";
            m.ApprovedBy = Uid(http);
            m.ApprovedAt = DateTime.UtcNow;

            var prev = await db.AuditLedger.Where(e => e.CampaignId == rel.CampaignId)
                .OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync() ?? "";
            var entry = ledger.CreateEntry(rel.CampaignId, "MilestoneRelease", new
            {
                rel.Id,
                rel.MilestoneId,
                rel.Amount,
                note = "Platform tranche release gate (not regulated escrow)"
            }, prev);
            db.AuditLedger.Add(entry);
        }
        else
        {
            rel.Status = MilestoneReleaseStatus.Rejected;
            rel.ReviewedBy = Uid(http);
            rel.ReviewedAt = DateTime.UtcNow;
            rel.ReviewNote = req.Note;
            m.Status = "Pending";
        }
        await db.SaveChangesAsync();
        return Results.Ok(rel);
    }

    private static async Task<IResult> ListReleases(Guid campaignId, AppDbContext db)
    {
        var list = await db.MilestoneReleases.AsNoTracking()
            .Where(r => r.CampaignId == campaignId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> StartSubscription(
        Guid campaignId, [FromBody] SubscriptionRequest req, HttpContext http, AppDbContext db)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        if (req.Amount <= 0) return Results.BadRequest(new { error = "Invalid amount" });
        var interval = string.Equals(req.Interval, "weekly", StringComparison.OrdinalIgnoreCase) ? "weekly" : "monthly";
        var sub = new RecurringDonation
        {
            CampaignId = campaignId,
            DonorId = Uid(http),
            Amount = req.Amount,
            Currency = campaign.Currency,
            Interval = interval,
            Status = SubscriptionStatus.Active,
            NextChargeAt = interval == "weekly" ? DateTime.UtcNow.AddDays(7) : DateTime.UtcNow.AddMonths(1),
            SendImpactUpdates = req.SendImpactUpdates ?? true,
            PayPalSubscriptionId = "SIM-" + Guid.NewGuid().ToString("N")[..12]
        };
        db.RecurringDonations.Add(sub);
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            sub,
            message = "Subscription recorded (simulate mode). Production would create a PayPal Billing Subscription."
        });
    }

    private static async Task<IResult> MySubscriptions(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http);
        var list = await db.RecurringDonations.AsNoTracking()
            .Where(s => s.DonorId == uid)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> CancelSubscription(Guid id, HttpContext http, AppDbContext db)
    {
        var sub = await db.RecurringDonations.FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null) return Results.NotFound();
        if (sub.DonorId != Uid(http) && !http.User.IsInRole("Admin"))
            return Results.Forbid();
        sub.Status = SubscriptionStatus.Cancelled;
        await db.SaveChangesAsync();
        return Results.Ok(sub);
    }

    private static async Task<IResult> ProcessDueSubscriptions(
        AppDbContext db, FeeTransparencyService fees, MatchingGiftService matchSvc, LedgerService ledger)
    {
        var due = await db.RecurringDonations
            .Where(s => s.Status == SubscriptionStatus.Active && s.NextChargeAt <= DateTime.UtcNow)
            .Take(50)
            .ToListAsync();
        var created = new List<object>();
        foreach (var sub in due)
        {
            var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == sub.CampaignId);
            if (campaign == null) continue;
            var breakdown = fees.Calculate(sub.Amount, 0, sub.Currency);
            var donation = new Donation
            {
                CampaignId = sub.CampaignId,
                DonorId = sub.DonorId,
                Amount = sub.Amount,
                Currency = sub.Currency,
                Status = DonationStatus.Captured,
                CapturedAt = DateTime.UtcNow,
                PayPalOrderId = "SUB-SIM-ORDER",
                PayPalCaptureId = "SUB-SIM-" + Guid.NewGuid().ToString("N")[..10],
                TipAmount = 0,
                PlatformFee = breakdown.PlatformFee,
                PaymentProcessorFeeEstimate = breakdown.ProcessorFeeEstimate,
                NetToCampaign = breakdown.NetToCampaign,
                IsRecurringCharge = true,
                RecurringDonationId = sub.Id
            };

            // Matching
            var program = await db.MatchingGiftPrograms
                .Where(p => p.CampaignId == sub.CampaignId && p.Status == MatchCampaignStatus.Active)
                .OrderByDescending(p => p.MatchRatio)
                .FirstOrDefaultAsync();
            var (matched, prog) = matchSvc.TryMatch(program, sub.Amount);
            if (matched > 0 && prog != null)
            {
                matchSvc.ApplyMatch(prog, matched);
                donation.MatchedAmount = matched;
                donation.MatchingGiftProgramId = prog.Id;
                db.MatchingGiftLedger.Add(new MatchingGiftLedgerEntry
                {
                    MatchingGiftProgramId = prog.Id,
                    DonationId = donation.Id,
                    CampaignId = sub.CampaignId,
                    DonorAmount = sub.Amount,
                    MatchedAmount = matched
                });
                campaign.RaisedAmount += matched;
            }

            campaign.RaisedAmount += sub.Amount;
            campaign.DonorCount += 1;
            db.Donations.Add(donation);

            var prev = await db.AuditLedger.Where(e => e.CampaignId == sub.CampaignId)
                .OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync() ?? "";
            db.AuditLedger.Add(ledger.CreateEntry(sub.CampaignId, "RecurringDonationCapture", new
            {
                DonationId = donation.Id,
                SubscriptionId = sub.Id,
                Amount = sub.Amount,
                matched
            }, prev));

            sub.LastChargedAt = DateTime.UtcNow;
            sub.SuccessfulCharges += 1;
            sub.NextChargeAt = sub.Interval == "weekly" ? DateTime.UtcNow.AddDays(7) : DateTime.UtcNow.AddMonths(1);
            created.Add(new { DonationId = donation.Id, SubscriptionId = sub.Id, matched });
        }
        await db.SaveChangesAsync();
        return Results.Ok(new { processed = created.Count, created });
    }

    private static async Task<IResult> ListMatching(Guid campaignId, AppDbContext db)
    {
        var list = await db.MatchingGiftPrograms.AsNoTracking()
            .Where(p => p.CampaignId == campaignId)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> CreateMatching(
        Guid campaignId, [FromBody] MatchingRequest req, HttpContext http, AppDbContext db)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        if (campaign.CreatorId != Uid(http) && !http.User.IsInRole("Admin"))
            return Results.Forbid();
        var pool = req.PoolTotal <= 0 ? 1000m : req.PoolTotal;
        var p = new MatchingGiftProgram
        {
            CampaignId = campaignId,
            SponsorName = string.IsNullOrWhiteSpace(req.SponsorName) ? "Sponsor" : req.SponsorName.Trim(),
            Headline = $"Matched by {(req.SponsorName ?? "Sponsor")}",
            MatchRatio = req.MatchRatio <= 0 ? 1m : req.MatchRatio,
            PoolTotal = pool,
            PoolRemaining = pool,
            MaxPerDonation = req.MaxPerDonation <= 0 ? 500m : req.MaxPerDonation,
            Status = MatchCampaignStatus.Active,
            StartsAt = DateTime.UtcNow,
            EndsAt = req.EndsAt,
            ShowSponsorOnDonate = true
        };
        db.MatchingGiftPrograms.Add(p);
        await db.SaveChangesAsync();
        return Results.Ok(p);
    }

    public record MilestoneRequest(string? Title, string? Description, decimal TargetAmount, int SortOrder = 0);
    public record ReleaseRequest(decimal Amount = 0, Guid? MultimodalProofId = null, Guid? ExpenseId = null);
    public record ReleaseDecisionRequest(string Decision, string? Note = null);
    public record SubscriptionRequest(decimal Amount, string? Interval = "monthly", bool? SendImpactUpdates = true);
    public record MatchingRequest(string? SponsorName, decimal MatchRatio = 1, decimal PoolTotal = 1000, decimal MaxPerDonation = 500, DateTime? EndsAt = null);
}
