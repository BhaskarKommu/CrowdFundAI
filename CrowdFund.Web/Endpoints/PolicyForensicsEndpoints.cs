using CrowdFund.Ai.Fraud;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Interfaces;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class PolicyForensicsEndpoints
{
    public static void MapPolicyForensicsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/policies", GetPolicies);
        app.MapPost("/api/campaigns/{campaignId:guid}/policy/evaluate-payout", EvaluatePayoutPolicy);

        app.MapPost("/api/admin/campaigns/{campaignId:guid}/anomalies/run", RunAnomalies);
        app.MapPost("/api/admin/campaigns/{campaignId:guid}/forensics/run", RunForensics);
        app.MapPost("/api/expenses/{expenseId:guid}/forensics", ForensicsOne).DisableAntiforgery();
    }

    private static IResult GetPolicies(PolicyEngine policy) => Results.Ok(policy.Options);

    private static async Task<IResult> EvaluatePayoutPolicy(
        Guid campaignId, AppDbContext db, PolicyEngine policy)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        var profile = await db.CreatorProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == campaign.CreatorId);
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.CampaignId == campaignId).ToListAsync();
        var flags = await db.FraudFlags.AsNoTracking().Where(f => f.CampaignId == campaignId).ToListAsync();
        var hasProof = await db.MultimodalProofs.AnyAsync(p =>
            p.CampaignId == campaignId && p.Status == MultimodalProofStatus.Approved);
        DateTime? created = null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == campaign.CreatorId);
        if (user != null) { /* IdentityUser may not have CreatedDate - skip */ }
        var decisions = policy.EvaluatePayout(campaign, profile, expenses, flags, created, hasProof, false);
        return Results.Ok(new
        {
            allowed = decisions.All(d => d.Allowed),
            decisions
        });
    }

    private static async Task<IResult> RunAnomalies(
        Guid campaignId, AppDbContext db, AnomalyEngine anomalies)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        var expenses = await db.Expenses.Include(e => e.Evidences).Where(e => e.CampaignId == campaignId).ToListAsync();
        var peers = await db.Campaigns.AsNoTracking()
            .Where(c => c.Category == campaign.Category && c.Id != campaignId).Take(30).ToListAsync();
        var peerIds = peers.Select(p => p.Id).ToList();
        var peerExp = await db.Expenses.AsNoTracking().Include(e => e.Evidences)
            .Where(e => peerIds.Contains(e.CampaignId)).ToListAsync();
        var allHashes = await db.ExpenseEvidences.AsNoTracking()
            .Select(ev => new { ev.Sha256, ev.ExpenseId })
            .ToListAsync();
        var expCamp = await db.Expenses.AsNoTracking().Select(e => new { e.Id, e.CampaignId }).ToListAsync();
        var map = expCamp.ToDictionary(x => x.Id, x => x.CampaignId);
        var corpus = allHashes
            .Where(h => !string.IsNullOrEmpty(h.Sha256) && map.ContainsKey(h.ExpenseId))
            .Select(h => (h.Sha256, map[h.ExpenseId], h.ExpenseId))
            .ToList();

        var flags = anomalies.Evaluate(campaign, expenses, peers, peerExp, corpus);
        var added = 0;
        foreach (var f in flags)
        {
            var exists = await db.FraudFlags.AnyAsync(x =>
                x.CampaignId == campaignId && x.Rule == f.Rule && x.ExpenseId == f.ExpenseId && x.Status == "Open");
            if (!exists) { db.FraudFlags.Add(f); added++; }
        }
        await db.SaveChangesAsync();
        return Results.Ok(new { found = flags.Count, added, flags });
    }

    private static async Task<IResult> RunForensics(
        Guid campaignId, AppDbContext db, DocumentForensicsService forensics, IFileStore files)
    {
        var evidences = await db.ExpenseEvidences.AsNoTracking()
            .Join(db.Expenses.Where(e => e.CampaignId == campaignId),
                ev => ev.ExpenseId, e => e.Id, (ev, e) => new { ev, e.Id })
            .ToListAsync();
        var results = new List<object>();
        var flags = new List<FraudFlag>();
        foreach (var row in evidences)
        {
            try
            {
                await using var stream = await files.OpenReadAsync(row.ev.StoredPath);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var fr = forensics.AnalyzeBytes(row.ev.FileName, "application/octet-stream", ms.ToArray(), row.Id, row.ev.Sha256);
                results.Add(fr);
                if (fr.Suspicious)
                {
                    flags.Add(new FraudFlag
                    {
                        CampaignId = campaignId,
                        ExpenseId = row.Id,
                        Rule = "DocumentForensics",
                        Severity = "Low",
                        Score = 0.4,
                        Explanation = string.Join(" ", fr.Findings),
                        EvidenceJson = System.Text.Json.JsonSerializer.Serialize(fr.Details),
                        Status = "Open"
                    });
                }
            }
            catch
            {
                results.Add(new { expenseId = row.Id, error = "file missing" });
            }
        }

        var corpus = await (
            from ev in db.ExpenseEvidences.AsNoTracking()
            join e in db.Expenses.AsNoTracking() on ev.ExpenseId equals e.Id
            select new { ev.Sha256, e.CampaignId, e.Id, ev.FileName }
        ).ToListAsync();
        var dupFlags = forensics.CrossCampaignDuplicates(campaignId,
            corpus.Select(c => (c.Sha256, c.CampaignId, c.Id, c.FileName)));
        flags.AddRange(dupFlags);

        var added = 0;
        foreach (var f in flags)
        {
            var exists = await db.FraudFlags.AnyAsync(x =>
                x.CampaignId == f.CampaignId && x.Rule == f.Rule && x.ExpenseId == f.ExpenseId && x.Status == "Open");
            if (!exists) { db.FraudFlags.Add(f); added++; }
        }
        await db.SaveChangesAsync();
        return Results.Ok(new { analyzed = results.Count, flagsAdded = added, results, duplicateFlags = dupFlags.Count });
    }

    private static async Task<IResult> ForensicsOne(
        Guid expenseId, AppDbContext db, DocumentForensicsService forensics, IFileStore files)
    {
        var expense = await db.Expenses.Include(e => e.Evidences).FirstOrDefaultAsync(e => e.Id == expenseId);
        if (expense == null) return Results.NotFound();
        var list = new List<object>();
        foreach (var ev in expense.Evidences)
        {
            try
            {
                await using var stream = await files.OpenReadAsync(ev.StoredPath);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                list.Add(forensics.AnalyzeBytes(ev.FileName, "application/octet-stream", ms.ToArray(), expense.Id, ev.Sha256));
            }
            catch
            {
                list.Add(new { ev.FileName, error = "unreadable" });
            }
        }
        return Results.Ok(list);
    }
}
