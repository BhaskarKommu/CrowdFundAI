using System.Security.Claims;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class SponsoredMatchingEndpoints
{
    public static void MapSponsoredMatchingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/matching-pools", ListActivePools);
        app.MapGet("/api/matching-pools/{id:guid}", GetPool);
        app.MapGet("/api/campaigns/{campaignId:guid}/matching-pools", CampaignPools);
        app.MapGet("/api/campaigns/{campaignId:guid}/matching-preview", PreviewMatch);
        app.MapPost("/api/matching-pools", CreatePool).RequireAuthorization();
        app.MapPost("/api/admin/matching-pools/{id:guid}/status", SetStatus).RequireAuthorization();
        app.MapGet("/api/matching-pools/{id:guid}/ledger", PoolLedger);
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static async Task<IResult> ListActivePools(AppDbContext db, MatchingGiftService match)
    {
        var list = await db.MatchingGiftPrograms.AsNoTracking()
            .Where(p => p.Status == MatchCampaignStatus.Active)
            .OrderByDescending(p => p.PoolRemaining)
            .Take(50)
            .ToListAsync();
        return Results.Ok(list.Select(match.PublicSummary));
    }

    private static async Task<IResult> GetPool(Guid id, AppDbContext db, MatchingGiftService match)
    {
        var p = await db.MatchingGiftPrograms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return Results.NotFound();
        return Results.Ok(match.PublicSummary(p));
    }

    private static async Task<IResult> CampaignPools(Guid campaignId, AppDbContext db, MatchingGiftService match)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        var pools = await db.MatchingGiftPrograms.AsNoTracking()
            .Where(p => p.Status == MatchCampaignStatus.Active &&
                        (p.CampaignId == campaignId || p.CampaignId == null))
            .ToListAsync();
        var eligible = pools.Where(p => match.IsCampaignEligible(p, campaign) && match.IsWindowOpen(p)).ToList();
        return Results.Ok(new
        {
            campaignId,
            pools = eligible.Select(match.PublicSummary),
            message = eligible.Count == 0 ? "No active sponsored matching for this campaign." : null
        });
    }

    private static async Task<IResult> PreviewMatch(
        Guid campaignId, [FromQuery] decimal amount, [FromQuery] bool firstTime = true,
        AppDbContext db = null!, MatchingGiftService match = null!)
    {
        if (amount <= 0) return Results.BadRequest(new { error = "amount > 0 required" });
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        var pools = await db.MatchingGiftPrograms
            .Where(p => p.Status == MatchCampaignStatus.Active &&
                        (p.CampaignId == campaignId || p.CampaignId == null))
            .ToListAsync();
        var best = match.SelectBestProgram(pools, campaign, amount, firstTime);
        if (best == null)
            return Results.Ok(new { matched = 0m, totalImpact = amount, program = (object?)null });
        var (m, _, reason) = match.TryMatch(best, amount, campaign, firstTime);
        return Results.Ok(new
        {
            matched = m,
            totalImpact = amount + m,
            reason,
            program = match.PublicSummary(best),
            disclosure = $"If eligible, {best.SponsorName} may match up to {best.MatchRatio:0.##}× your gift from a sponsored pool (remaining {best.PoolRemaining:N2} {campaign.Currency}). Match is not guaranteed after pool exhaustion."
        });
    }

    private static async Task<IResult> CreatePool(
        [FromBody] CreatePoolRequest req, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http);
        if (req.CampaignId.HasValue)
        {
            var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CampaignId);
            if (campaign == null) return Results.NotFound(new { error = "Campaign not found" });
            if (campaign.CreatorId != uid && !http.User.IsInRole("Admin"))
                return Results.Forbid();
        }
        else if (!http.User.IsInRole("Admin"))
        {
            return Results.Json(new { error = "Platform-wide pools require Admin" }, statusCode: 403);
        }

        var poolTotal = req.PoolTotal <= 0 ? 1000m : req.PoolTotal;
        var p = new MatchingGiftProgram
        {
            CampaignId = req.CampaignId,
            TenantId = req.TenantId,
            SponsorName = string.IsNullOrWhiteSpace(req.SponsorName) ? "Sponsor" : req.SponsorName.Trim(),
            SponsorLogoUrl = req.SponsorLogoUrl,
            SponsorWebsite = req.SponsorWebsite,
            Headline = req.Headline ?? $"Matched by {(req.SponsorName ?? "Sponsor")}",
            Description = req.Description,
            MatchRatio = req.MatchRatio <= 0 ? 1m : req.MatchRatio,
            PoolTotal = poolTotal,
            PoolRemaining = poolTotal,
            MaxPerDonation = req.MaxPerDonation <= 0 ? 500m : req.MaxPerDonation,
            MinDonation = req.MinDonation < 0 ? 0 : req.MinDonation,
            EligibleCategories = req.EligibleCategories,
            FirstTimeDonorsOnly = req.FirstTimeDonorsOnly ?? false,
            ShowSponsorOnDonate = req.ShowSponsorOnDonate ?? true,
            Status = MatchCampaignStatus.Active,
            StartsAt = req.StartsAt ?? DateTime.UtcNow,
            EndsAt = req.EndsAt
        };
        db.MatchingGiftPrograms.Add(p);
        await db.SaveChangesAsync();
        return Results.Ok(p);
    }

    private static async Task<IResult> SetStatus(
        Guid id, [FromBody] StatusRequest req, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var p = await db.MatchingGiftPrograms.FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return Results.NotFound();
        if (Enum.TryParse<MatchCampaignStatus>(req.Status, true, out var st))
            p.Status = st;
        await db.SaveChangesAsync();
        return Results.Ok(p);
    }

    private static async Task<IResult> PoolLedger(Guid id, AppDbContext db)
    {
        var entries = await db.MatchingGiftLedger.AsNoTracking()
            .Where(e => e.MatchingGiftProgramId == id)
            .OrderByDescending(e => e.CreatedAt)
            .Take(100)
            .ToListAsync();
        return Results.Ok(entries);
    }

    public record CreatePoolRequest(
        Guid? CampaignId,
        Guid? TenantId,
        string? SponsorName,
        string? SponsorLogoUrl,
        string? SponsorWebsite,
        string? Headline,
        string? Description,
        decimal MatchRatio = 1,
        decimal PoolTotal = 1000,
        decimal MaxPerDonation = 500,
        decimal MinDonation = 1,
        string? EligibleCategories = null,
        bool? FirstTimeDonorsOnly = false,
        bool? ShowSponsorOnDonate = true,
        DateTime? StartsAt = null,
        DateTime? EndsAt = null);

    public record StatusRequest(string Status);
}
