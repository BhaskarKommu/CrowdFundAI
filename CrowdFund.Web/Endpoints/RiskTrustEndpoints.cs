using System.Security.Claims;
using CrowdFund.Ai.Orchestration;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Web.Observability;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using CrowdFund.Web.Hubs;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class RiskTrustEndpoints
{
    public static void MapRiskTrustEndpoints(this WebApplication app)
    {
        // Risk graph
        app.MapPost("/api/admin/risk-graph/rebuild", RebuildRiskGraph);
        app.MapGet("/api/admin/risk-graph", GetRiskGraph);
        app.MapGet("/api/admin/risk-graph/campaigns/{id:guid}", GetCampaignRisk);

        // Flags + right of reply
        app.MapGet("/api/campaigns/{campaignId:guid}/flags", ListCampaignFlags);
        app.MapPost("/api/flags/{flagId:guid}/replies", PostFlagReply).RequireAuthorization();
        app.MapGet("/api/flags/{flagId:guid}/replies", ListFlagReplies);

        // Auditor read APIs
        app.MapGet("/api/auditor/campaigns", AuditorCampaigns).RequireAuthorization(policy => policy.RequireRole("Auditor", "Admin"));
        app.MapGet("/api/auditor/campaigns/{id:guid}", AuditorCampaignDetail).RequireAuthorization(policy => policy.RequireRole("Auditor", "Admin"));

        // Periodic re-verification
        app.MapPost("/api/admin/campaigns/{id:guid}/reverify", ManualReverify);
        app.MapGet("/api/admin/reverify/runs", ListReverifyRuns);

        // Cryptographic transparency log (public)
        app.MapPost("/api/admin/campaigns/{id:guid}/publish-ledger-root", PublishLedgerRoot);
        app.MapGet("/api/public/transparency-log", PublicTransparencyLog);
        app.MapGet("/api/public/transparency-log/{campaignId:guid}", PublicCampaignLog);
        app.MapGet("/api/public/transparency-log/verify", VerifyTransparencyLog);
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static async Task<IResult> RebuildRiskGraph(AppDbContext db, RiskGraphService risk)
    {
        var campaigns = await db.Campaigns.AsNoTracking().ToListAsync();
        var docs = await db.CampaignDocuments.AsNoTracking().ToListAsync();
        var expenses = await db.Expenses.AsNoTracking().ToListAsync();
        var emails = await db.Users.AsNoTracking()
            .ToDictionaryAsync(u => u.Id, u => u.Email ?? "");
        // Also AppUsers if used
        var appUsers = await db.AppUsers.AsNoTracking().ToListAsync();
        foreach (var au in appUsers)
            emails[au.Id] = au.Email;

        var edges = risk.BuildEdges(campaigns, docs, expenses, emails);
        // Replace edges for simplicity (demo)
        db.RiskGraphEdges.RemoveRange(db.RiskGraphEdges);
        await db.SaveChangesAsync();
        db.RiskGraphEdges.AddRange(edges);
        await db.SaveChangesAsync();
        return Results.Ok(new { count = edges.Count, types = edges.GroupBy(e => e.RelationType).Select(g => new { type = g.Key, n = g.Count() }) });
    }

    private static async Task<IResult> GetRiskGraph(AppDbContext db)
    {
        var edges = await db.RiskGraphEdges.AsNoTracking().OrderByDescending(e => e.Weight).Take(500).ToListAsync();
        var nodes = edges.SelectMany(e => new[] { e.FromCampaignId, e.ToCampaignId }).Distinct().ToList();
        return Results.Ok(new { nodes, edges });
    }

    private static async Task<IResult> GetCampaignRisk(Guid id, AppDbContext db)
    {
        var edges = await db.RiskGraphEdges.AsNoTracking()
            .Where(e => e.FromCampaignId == id || e.ToCampaignId == id)
            .OrderByDescending(e => e.Weight)
            .ToListAsync();
        return Results.Ok(new { campaignId = id, riskScore = edges.Sum(e => e.Weight), edges });
    }

    private static async Task<IResult> ListCampaignFlags(Guid campaignId, HttpContext http, AppDbContext db)
    {
        var flags = await db.FraudFlags.AsNoTracking()
            .Where(f => f.CampaignId == campaignId)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync();

        var flagIds = flags.Select(f => f.Id).ToList();
        var replies = await db.FlagReplies.AsNoTracking()
            .Where(r => flagIds.Contains(r.FraudFlagId))
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

        var isAdmin = http.User.IsInRole("Admin") || http.User.IsInRole("Auditor");
        var userId = Uid(http);
        var isDonor = false;
        if (userId != null)
            isDonor = await db.Donations.AnyAsync(d => d.CampaignId == campaignId && d.DonorId == userId && d.Status == DonationStatus.Captured);

        var payload = flags.Select(f =>
        {
            var fr = replies.Where(r => r.FraudFlagId == f.Id)
                .Where(r =>
                    r.Visibility == "Public"
                    || (r.Visibility == "DonorsOnly" && (isDonor || isAdmin))
                    || (r.Visibility == "Internal" && isAdmin))
                .Select(r => new { r.Id, r.AuthorRole, r.Body, r.Visibility, r.CreatedAt });
            return new
            {
                f.Id,
                f.Rule,
                f.Severity,
                f.Explanation,
                f.Status,
                f.ExpenseId,
                f.CreatedAt,
                replies = fr
            };
        });
        return Results.Ok(payload);
    }

    private static async Task<IResult> PostFlagReply(
        Guid flagId, [FromBody] FlagReplyRequest req, HttpContext http, AppDbContext db)
    {
        var flag = await db.FraudFlags.FirstOrDefaultAsync(f => f.Id == flagId);
        if (flag == null) return Results.NotFound();

        var userId = Uid(http);
        if (userId == null) return Results.Unauthorized();

        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == flag.CampaignId);
        var isCreator = campaign?.CreatorId == userId;
        var isAdmin = http.User.IsInRole("Admin");
        var isAuditor = http.User.IsInRole("Auditor");
        if (!isCreator && !isAdmin && !isAuditor)
            return Results.Forbid();

        var visibility = req.Visibility ?? "Public";
        if (visibility is not ("Public" or "DonorsOnly" or "Internal"))
            visibility = "Public";
        if (isCreator && visibility == "Internal")
            visibility = "Public";

        var reply = new FlagReply
        {
            FraudFlagId = flagId,
            AuthorUserId = userId,
            AuthorRole = isAdmin ? "Admin" : isAuditor ? "Auditor" : "Creator",
            Body = (req.Body ?? "").Trim(),
            Visibility = visibility
        };
        if (string.IsNullOrWhiteSpace(reply.Body))
            return Results.BadRequest(new { error = "Body required" });

        db.FlagReplies.Add(reply);
        await db.SaveChangesAsync();
        return Results.Ok(reply);
    }

    private static async Task<IResult> ListFlagReplies(Guid flagId, HttpContext http, AppDbContext db)
    {
        var flag = await db.FraudFlags.AsNoTracking().FirstOrDefaultAsync(f => f.Id == flagId);
        if (flag == null) return Results.NotFound();
        // Reuse visibility rules via ListCampaignFlags logic — simplified public/admin
        var isAdmin = http.User.IsInRole("Admin") || http.User.IsInRole("Auditor");
        var list = await db.FlagReplies.AsNoTracking()
            .Where(r => r.FraudFlagId == flagId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();
        if (!isAdmin)
            list = list.Where(r => r.Visibility == "Public" || r.Visibility == "DonorsOnly").ToList();
        return Results.Ok(list);
    }

    private static async Task<IResult> AuditorCampaigns(AppDbContext db)
    {
        var list = await db.Campaigns.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.Status,
                c.TrustScore,
                c.TransparencyScore,
                c.RaisedAmount,
                c.GoalAmount,
                c.Category
            })
            .Take(200)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> AuditorCampaignDetail(Guid id, AppDbContext db)
    {
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return Results.NotFound();
        var flags = await db.FraudFlags.AsNoTracking().Where(f => f.CampaignId == id).ToListAsync();
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.CampaignId == id).Select(e => new { e.Id, e.Vendor, e.Amount, e.Status, e.Category }).ToListAsync();
        var runs = await db.VerificationRuns.AsNoTracking().Where(r => r.CampaignId == id).OrderByDescending(r => r.StartedAt).Take(5).ToListAsync();
        var risk = await db.RiskGraphEdges.AsNoTracking().Where(e => e.FromCampaignId == id || e.ToCampaignId == id).ToListAsync();
        return Results.Ok(new { campaign = c, flags, expenses, verificationRuns = runs, riskEdges = risk, mode = "read-only" });
    }

    private static async Task<IResult> ManualReverify(
        Guid id, AppDbContext db, VerificationOrchestrator orch, AlertService alerts, IHubContext<CampaignHub> hub)
    {
        var run = await RunFullReverifyAsync(id, "manual", db, orch, alerts, hub);
        if (run == null) return Results.NotFound();
        return Results.Ok(run);
    }

    public static async Task<ReverificationRun?> RunFullReverifyAsync(
        Guid campaignId,
        string trigger,
        AppDbContext db,
        VerificationOrchestrator orch,
        AlertService alerts,
        IHubContext<CampaignHub> hub)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return null;
        var before = campaign.TrustScore;
        var run = new ReverificationRun
        {
            CampaignId = campaignId,
            Trigger = trigger,
            TrustScoreBefore = before,
            StartedAt = DateTime.UtcNow
        };
        db.ReverificationRuns.Add(run);
        await db.SaveChangesAsync();

        try
        {
            var documents = await db.CampaignDocuments.Where(d => d.CampaignId == campaignId).ToListAsync();
            var expenses = await db.Expenses.Include(e => e.Evidences).Where(e => e.CampaignId == campaignId).ToListAsync();
            var donations = await db.Donations.Where(d => d.CampaignId == campaignId).ToListAsync();
            var history = await db.Campaigns.AsNoTracking()
                .Where(c => c.CreatorId == campaign.CreatorId && c.Id != campaignId).Take(20).ToListAsync();

            var (assessment, flags, vrun) = await orch.VerifyAsync(campaign, documents, expenses, donations, history);
            db.VerificationRuns.Add(vrun);
            foreach (var f in flags)
            {
                var exists = await db.FraudFlags.AnyAsync(x =>
                    x.CampaignId == campaignId && x.Rule == f.Rule && x.ExpenseId == f.ExpenseId && x.Status == "Open");
                if (!exists) db.FraudFlags.Add(f);
            }
            await db.SaveChangesAsync();

            run.TrustScoreAfter = campaign.TrustScore;
            run.FlagsRaised = await db.FraudFlags.CountAsync(f => f.CampaignId == campaignId && f.Status == "Open");
            run.Summary = $"Band={assessment.Band}; action={assessment.RecommendedAction}; findings={assessment.Findings.Count}";
            run.CompletedAt = DateTime.UtcNow;

            if (run.TrustScoreAfter < (run.TrustScoreBefore ?? 0) - 10 || flags.Any(f => f.Severity is "High" or "Medium"))
            {
                await alerts.NotifyAsync("warning", "Re-verification result", new
                {
                    campaignId,
                    before = run.TrustScoreBefore,
                    after = run.TrustScoreAfter,
                    run.FlagsRaised,
                    trigger
                });
                run.NotificationSent = true;
                await hub.Clients.Group($"campaign-{campaignId}")
                    .SendAsync("Reverified", new { campaignId, run.TrustScoreAfter, run.FlagsRaised });
            }
        }
        catch (Exception ex)
        {
            run.Summary = "Error: " + ex.Message;
            run.CompletedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return run;
    }

    private static async Task<IResult> ListReverifyRuns(AppDbContext db)
    {
        var list = await db.ReverificationRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(100).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> PublishLedgerRoot(
        Guid id, AppDbContext db, LedgerService ledger, TransparencyLogService logSvc)
    {
        var exists = await db.Campaigns.AnyAsync(c => c.Id == id);
        if (!exists) return Results.NotFound();
        var entries = await db.AuditLedger.Where(e => e.CampaignId == id).OrderBy(e => e.Id).ToListAsync();
        var (valid, _) = ledger.VerifyChain(entries);
        var head = entries.LastOrDefault()?.Hash ?? "";
        var prevPub = await db.TransparencyLog.OrderByDescending(t => t.Id).Select(t => t.PublishedHash).FirstOrDefaultAsync() ?? "";
        var pub = logSvc.CreatePublication(id, head, entries.Count, valid, prevPub);
        db.TransparencyLog.Add(pub);
        await db.SaveChangesAsync();
        return Results.Ok(pub);
    }

    private static async Task<IResult> PublicTransparencyLog(AppDbContext db)
    {
        var list = await db.TransparencyLog.AsNoTracking().OrderByDescending(t => t.Id).Take(200).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> PublicCampaignLog(Guid campaignId, AppDbContext db)
    {
        var list = await db.TransparencyLog.AsNoTracking()
            .Where(t => t.CampaignId == campaignId)
            .OrderByDescending(t => t.Id)
            .Take(50)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> VerifyTransparencyLog(AppDbContext db, TransparencyLogService logSvc)
    {
        var list = await db.TransparencyLog.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        var (valid, brokenAt) = logSvc.VerifyLogChain(list);
        return Results.Ok(new { valid, brokenAt, count = list.Count });
    }

    public record FlagReplyRequest(string Body, string? Visibility = "Public");
}
