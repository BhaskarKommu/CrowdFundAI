using System.Security.Claims;
using System.Text.Json;
using CrowdFund.Ai.Agents;
using CrowdFund.Ai.Fraud;
using CrowdFund.Ai.Orchestration;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/campaigns/{id:guid}/verify", VerifyCampaign).RequireAuthorization(new AuthorizeAttribute { Roles = "Admin,Auditor" });
        app.MapGet("/api/campaigns/{id:guid}/verification/latest", LatestVerification);
        app.MapGet("/api/admin/queue", AdminQueue).RequireAuthorization(new AuthorizeAttribute { Roles = "Admin,Auditor" });
        app.MapPost("/api/admin/campaigns/{id:guid}/decision", AdminDecision).RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });
        app.MapPatch("/api/admin/flags/{id:guid}", PatchFlag);
        app.MapGet("/api/admin/campaigns/{id:guid}/dossier", GetDossier);
        app.MapPost("/api/admin/campaigns/{id:guid}/break-ledger", BreakLedgerDemo);
    }

    private static async Task<IResult> VerifyCampaign(
        Guid id,
        AppDbContext db,
        VerificationOrchestrator orchestrator,
        LedgerService ledger,
        HttpContext http)
    {
        var campaign = await db.Campaigns
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (campaign == null) return Results.NotFound();

        var expenses = await db.Expenses.Include(e => e.Evidences).Where(e => e.CampaignId == id).ToListAsync();
        var donations = await db.Donations.Where(d => d.CampaignId == id).ToListAsync();
        var history = await db.Campaigns.Where(c => c.CreatorId == campaign.CreatorId && c.Id != id).ToListAsync();

        if (campaign.Status == CampaignStatus.Draft)
            campaign.Status = CampaignStatus.UnderReview;

        // Priority SLA if creator has active premium
        var premiumSvc = http.RequestServices.GetRequiredService<PremiumVerificationService>();
        var subs = await db.PremiumVerificationSubscriptions.AsNoTracking()
            .Where(s => s.CreatorId == campaign.CreatorId).ToListAsync();
        var plan = premiumSvc.EffectivePlan(subs);
        if (plan != PremiumPlan.None)
            premiumSvc.ApplyToCampaign(campaign, plan);

        var (assessment, flags, run) = await orchestrator.VerifyAsync(
            campaign, campaign.Documents.ToList(), expenses, donations, history);

        db.VerificationRuns.Add(run);
        foreach (var f in flags)
            db.FraudFlags.Add(f);

        var prevHash = await db.AuditLedger.Where(e => e.CampaignId == id)
            .OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync() ?? "";
        db.AuditLedger.Add(orchestrator.LedgerForTrustChange(campaign, assessment.OverallScore, prevHash));

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            runId = run.Id,
            assessment.OverallScore,
            assessment.Band,
            assessment.RecommendedAction,
            assessment.SubScores,
            findings = assessment.Findings,
            missing = assessment.MissingInformation,
            disclaimer = assessment.Disclaimer,
            flags = flags.Select(f => new { f.Id, f.Rule, f.Severity, f.Score, f.Explanation, f.Status }),
            campaignStatus = campaign.Status.ToString()
        });
    }

    private static async Task<IResult> LatestVerification(Guid id, AppDbContext db)
    {
        var run = await db.VerificationRuns
            .AsNoTracking()
            .Include(r => r.Findings)
            .Where(r => r.CampaignId == id)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync();
        if (run == null) return Results.NotFound(new { error = "No verification yet" });
        return Results.Ok(new
        {
            run.Id,
            run.OverallScore,
            run.CompletedAt,
            run.RecommendationJson,
            findings = run.Findings.Select(f => new { f.AgentName, f.Severity, f.Title, f.Detail, f.Confidence })
        });
    }

    private static async Task<IResult> AdminQueue(AppDbContext db)
    {
        var campaigns = await db.Campaigns
            .AsNoTracking()
            .Where(c => c.Status == CampaignStatus.UnderReview
                     || c.Status == CampaignStatus.NeedsReview
                     || c.Status == CampaignStatus.Draft)
            // Premium first, then risk (low trust), then oldest SLA due
            .OrderByDescending(c => c.ReviewPriority)
            .ThenBy(c => c.ReviewSlaDueAt ?? DateTime.MaxValue)
            .ThenByDescending(c => c.TrustScore == 0 ? 0 : (100 - c.TrustScore))
            .ThenBy(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.Category,
                c.Status,
                c.TrustScore,
                c.GoalAmount,
                c.CreatedAt,
                c.PremiumReview,
                c.ReviewPriority,
                c.ReviewSlaDueAt,
                slaOverdue = c.ReviewSlaDueAt != null && c.ReviewSlaDueAt < DateTime.UtcNow,
                OpenFlags = db.FraudFlags.Count(f => f.CampaignId == c.Id && f.Status == "Open")
            })
            .Take(50)
            .ToListAsync();
        return Results.Ok(campaigns);
    }

    private static async Task<IResult> AdminDecision(
        Guid id,
        [FromBody] DecisionRequest req,
        AppDbContext db,
        LedgerService ledger,
        HttpContext http)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id);
        if (campaign == null) return Results.NotFound();

        var admin = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "admin";
        campaign.Status = req.Decision.ToLowerInvariant() switch
        {
            "approve" => CampaignStatus.Published,
            "reject" => CampaignStatus.Rejected,
            "requestmore" => CampaignStatus.NeedsReview,
            _ => campaign.Status
        };
        if (campaign.Status == CampaignStatus.Published)
            campaign.PublishedAt = DateTime.UtcNow;

        var prevHash = await db.AuditLedger.Where(e => e.CampaignId == id)
            .OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync() ?? "";
        db.AuditLedger.Add(ledger.CreateEntry(id, "admin.decision", new
        {
            decision = req.Decision,
            by = admin,
            note = req.Note
        }, prevHash));

        await db.SaveChangesAsync();
        return Results.Ok(new { campaign.Id, campaign.Status });
    }

    private static async Task<IResult> PatchFlag(Guid id, [FromBody] FlagPatch req, AppDbContext db)
    {
        var flag = await db.FraudFlags.FirstOrDefaultAsync(f => f.Id == id);
        if (flag == null) return Results.NotFound();
        flag.Status = req.Status;
        flag.ReviewedBy = req.ReviewedBy;
        await db.SaveChangesAsync();
        return Results.Ok(new { flag.Id, flag.Status });
    }

    private static async Task<IResult> GetDossier(Guid id, AppDbContext db)
    {
        var campaign = await db.Campaigns.AsNoTracking()
            .Include(c => c.Documents)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (campaign == null) return Results.NotFound();

        var run = await db.VerificationRuns.AsNoTracking()
            .Include(r => r.Findings)
            .Where(r => r.CampaignId == id)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync();

        var flags = await db.FraudFlags.AsNoTracking()
            .Where(f => f.CampaignId == id)
            .OrderByDescending(f => f.Score)
            .ToListAsync();

        return Results.Ok(new
        {
            campaign = new
            {
                campaign.Id,
                campaign.Title,
                campaign.Story,
                campaign.Category,
                campaign.GoalAmount,
                campaign.Status,
                campaign.TrustScore,
                campaign.TrustExplanationJson
            },
            documents = campaign.Documents.Select(d => new { d.FileName, d.ContentType, d.Sha256 }),
            verification = run == null ? null : new
            {
                run.OverallScore,
                run.RecommendationJson,
                findings = run.Findings.Select(f => new { f.AgentName, f.Severity, f.Title, f.Detail, f.Confidence })
            },
            flags = flags.Select(f => new { f.Id, f.Rule, f.Severity, f.Score, f.Explanation, f.Status, f.EvidenceJson })
        });
    }


    /// <summary>Admin-only demo: mutates last ledger payload so /verify fails visibly.</summary>
    private static async Task<IResult> BreakLedgerDemo(Guid id, AppDbContext db)
    {
        var last = await db.AuditLedger.Where(e => e.CampaignId == id).OrderByDescending(e => e.Id).FirstOrDefaultAsync();
        if (last == null) return Results.BadRequest(new { error = "No ledger entries" });
        last.PayloadJson = last.PayloadJson + "/*tampered-demo*/";
        await db.SaveChangesAsync();
        return Results.Ok(new { ok = true, message = "Ledger payload tampered for demo. Call GET /api/ledger/{id}/verify" });
    }

    public record DecisionRequest(string Decision, string? Note = null);
    public record FlagPatch(string Status, string? ReviewedBy = null);
}
