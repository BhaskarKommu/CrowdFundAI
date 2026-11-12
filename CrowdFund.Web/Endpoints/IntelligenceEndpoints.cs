using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Interfaces;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class IntelligenceEndpoints
{
    public static void MapIntelligenceEndpoints(this WebApplication app)
    {
        // A8 Matching
        app.MapGet("/api/recommendations", Recommend);
        app.MapPost("/api/recommendations", RecommendPost);

        // A9 Impact reports
        app.MapPost("/api/campaigns/{campaignId:guid}/impact-reports/draft", DraftImpactReport).RequireAuthorization();
        app.MapGet("/api/campaigns/{campaignId:guid}/impact-reports", ListImpactReports);
        app.MapGet("/api/impact-reports/{id:guid}", GetImpactReport);
        app.MapPut("/api/impact-reports/{id:guid}", EditImpactReport).RequireAuthorization();
        app.MapPost("/api/impact-reports/{id:guid}/publish", PublishImpactReport).RequireAuthorization();
        app.MapPost("/api/impact-reports/{id:guid}/send", SendImpactReport).RequireAuthorization();
        app.MapGet("/api/impact-reports/{id:guid}/html", ImpactReportHtml);

        // Multimodal proof
        app.MapPost("/api/campaigns/{campaignId:guid}/proofs", UploadProof).DisableAntiforgery().RequireAuthorization();
        app.MapGet("/api/campaigns/{campaignId:guid}/proofs", ListProofs);
        app.MapPost("/api/proofs/{id:guid}/review", ReviewProof).RequireAuthorization();
        app.MapGet("/api/proofs/{id:guid}/file", DownloadProof);
        app.MapGet("/api/milestones/{milestoneId:guid}/payout-ready", MilestonePayoutReady);
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static async Task<IResult> Recommend(
        AppDbContext db,
        CampaignMatchingService matching,
        [FromQuery] string? q = null,
        [FromQuery] string? category = null,
        [FromQuery] int? minTrust = null,
        [FromQuery] int take = 8,
        HttpContext? http = null)
    {
        var published = await db.Campaigns.AsNoTracking()
            .Where(c => c.Status == CampaignStatus.Published)
            .ToListAsync();
        List<Donation>? history = null;
        var uid = http?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (uid != null)
        {
            history = await db.Donations.AsNoTracking().Include(d => d.Campaign)
                .Where(d => d.DonorId == uid && d.Status == DonationStatus.Captured)
                .ToListAsync();
        }
        var results = await matching.RecommendAsync(q ?? "", published, history, category, minTrust, take);
        db.CampaignMatchLogs.Add(new CampaignMatchLog
        {
            UserId = uid,
            Query = q ?? "",
            ResultsJson = JsonSerializer.Serialize(results.Select(r => new { r.CampaignId, r.Score, why = r.Why }))
        });
        await db.SaveChangesAsync();
        return Results.Ok(new { query = q, count = results.Count, recommendations = results });
    }

    private static Task<IResult> RecommendPost([FromBody] RecommendRequest req, AppDbContext db, CampaignMatchingService matching, HttpContext http)
        => Recommend(db, matching, req.Query, req.Category, req.MinTrust, req.Take <= 0 ? 8 : req.Take, http);

    private static async Task<IResult> DraftImpactReport(
        Guid campaignId, HttpContext http, AppDbContext db, ImpactReportService reports, LedgerService ledger)
    {
        var uid = Uid(http);
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        if (campaign.CreatorId != uid && !http.User.IsInRole("Admin"))
            return Results.Forbid();

        var expenses = await db.Expenses.Where(e => e.CampaignId == campaignId).ToListAsync();
        var donations = await db.Donations.Where(d => d.CampaignId == campaignId).ToListAsync();
        var entries = await db.AuditLedger.Where(e => e.CampaignId == campaignId).OrderBy(e => e.Id).ToListAsync();
        var head = entries.LastOrDefault()?.Hash;
        var draft = await reports.DraftAsync(campaign, expenses, donations, head);
        db.ImpactReports.Add(draft);
        await db.SaveChangesAsync();
        return Results.Ok(draft);
    }

    private static async Task<IResult> ListImpactReports(Guid campaignId, AppDbContext db)
    {
        var list = await db.ImpactReports.AsNoTracking()
            .Where(r => r.CampaignId == campaignId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> GetImpactReport(Guid id, AppDbContext db)
    {
        var r = await db.ImpactReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return r == null ? Results.NotFound() : Results.Ok(r);
    }

    private static async Task<IResult> EditImpactReport(
        Guid id, [FromBody] EditReportRequest req, HttpContext http, AppDbContext db)
    {
        var r = await db.ImpactReports.FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return Results.NotFound();
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CampaignId);
        var uid = Uid(http);
        if (campaign?.CreatorId != uid && !http.User.IsInRole("Admin"))
            return Results.Forbid();
        if (!string.IsNullOrWhiteSpace(req.Title)) r.Title = req.Title.Trim();
        if (req.BodyMarkdown != null) r.BodyMarkdown = req.BodyMarkdown;
        r.Status = ImpactReportStatus.CreatorEdited;
        r.EditedBy = uid;
        r.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(r);
    }

    private static async Task<IResult> PublishImpactReport(Guid id, HttpContext http, AppDbContext db)
    {
        var r = await db.ImpactReports.FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return Results.NotFound();
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CampaignId);
        var uid = Uid(http);
        if (campaign?.CreatorId != uid && !http.User.IsInRole("Admin"))
            return Results.Forbid();
        r.Status = ImpactReportStatus.Published;
        r.PublishedAt = DateTime.UtcNow;
        r.UpdatedAt = DateTime.UtcNow;
        // Also create a campaign update entry
        db.CampaignUpdates.Add(new CampaignUpdate
        {
            CampaignId = r.CampaignId,
            Title = r.Title,
            Body = r.BodyMarkdown,
            Source = "AiDraft",
            ApprovedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return Results.Ok(r);
    }

    private static async Task<IResult> SendImpactReport(Guid id, HttpContext http, AppDbContext db)
    {
        var r = await db.ImpactReports.FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return Results.NotFound();
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CampaignId);
        var uid = Uid(http);
        if (campaign?.CreatorId != uid && !http.User.IsInRole("Admin"))
            return Results.Forbid();
        if (r.Status is not (ImpactReportStatus.Published or ImpactReportStatus.CreatorEdited or ImpactReportStatus.Draft))
            return Results.BadRequest(new { error = "Publish or edit before send" });

        var donorIds = await db.Donations.AsNoTracking()
            .Where(d => d.CampaignId == r.CampaignId && d.Status == DonationStatus.Captured && d.DonorId != null)
            .Select(d => d.DonorId!)
            .Distinct()
            .ToListAsync();
        // Demo: no real SMTP — mark sent and return recipient count
        r.Status = ImpactReportStatus.Sent;
        r.SentAt = DateTime.UtcNow;
        r.RecipientsCount = donorIds.Count;
        r.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            r.Id,
            r.Status,
            r.RecipientsCount,
            message = "Marked sent (email transport is a production hook — HTML/PDF available via /html)."
        });
    }

    private static async Task<IResult> ImpactReportHtml(Guid id, AppDbContext db, ImpactReportService reports)
    {
        var r = await db.ImpactReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return Results.NotFound();
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CampaignId);
        if (campaign == null) return Results.NotFound();
        return Results.Content(reports.RenderHtml(r, campaign), "text/html");
    }

    private static async Task<IResult> UploadProof(
        Guid campaignId,
        HttpRequest request,
        HttpContext http,
        AppDbContext db,
        IFileStore files,
        MultimodalProofService proofSvc)
    {
        var uid = Uid(http);
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        if (campaign.CreatorId != uid && !http.User.IsInRole("Admin"))
            return Results.Forbid();
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart required" });
        var form = await request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();
        if (file == null) return Results.BadRequest(new { error = "file required" });
        Guid? expenseId = Guid.TryParse(form["expenseId"], out var e) ? e : null;
        Guid? milestoneId = Guid.TryParse(form["milestoneId"], out var m) ? m : null;

        await using var stream = file.OpenReadStream();
        var (proof, bytes) = await proofSvc.CreateFromUploadAsync(campaignId, expenseId, milestoneId, file.FileName, file.ContentType, stream);
        await using var saveStream = new MemoryStream(bytes);
        var path = await files.SaveAsync(saveStream, file.FileName, file.ContentType);
        proof.StoredPath = path;
        db.MultimodalProofs.Add(proof);
        await db.SaveChangesAsync();
        return Results.Ok(proof);
    }

    private static async Task<IResult> ListProofs(Guid campaignId, AppDbContext db)
    {
        var list = await db.MultimodalProofs.AsNoTracking()
            .Where(p => p.CampaignId == campaignId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> ReviewProof(
        Guid id, [FromBody] ProofReviewRequest req, HttpContext http, AppDbContext db, MultimodalProofService proofSvc)
    {
        if (!http.User.IsInRole("Admin") && !http.User.IsInRole("Auditor"))
            return Results.Forbid();
        var proof = await db.MultimodalProofs.FirstOrDefaultAsync(p => p.Id == id);
        if (proof == null) return Results.NotFound();
        var reviewer = Uid(http) ?? "admin";
        if (string.Equals(req.Decision, "approve", StringComparison.OrdinalIgnoreCase))
            proofSvc.Approve(proof, reviewer, req.Note);
        else
            proofSvc.Reject(proof, reviewer, req.Note);
        await db.SaveChangesAsync();
        return Results.Ok(proof);
    }

    private static async Task<IResult> DownloadProof(Guid id, AppDbContext db, IFileStore files)
    {
        var proof = await db.MultimodalProofs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (proof == null) return Results.NotFound();
        try
        {
            await using var stream = await files.OpenReadAsync(proof.StoredPath);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return Results.File(ms.ToArray(), proof.ContentType, proof.FileName);
        }
        catch { return Results.NotFound(); }
    }

    private static async Task<IResult> MilestonePayoutReady(Guid milestoneId, AppDbContext db)
    {
        var ms = await db.Milestones.AsNoTracking().FirstOrDefaultAsync(m => m.Id == milestoneId);
        if (ms == null) return Results.NotFound();
        var proofs = await db.MultimodalProofs.AsNoTracking()
            .Where(p => p.MilestoneId == milestoneId)
            .ToListAsync();
        var approved = proofs.Where(MultimodalProofService.IsHumanApproved).ToList();
        var ready = approved.Count > 0;
        return Results.Ok(new
        {
            milestoneId,
            payoutReady = ready,
            reason = ready
                ? "At least one human-approved multimodal proof is attached."
                : "No human-approved proof yet — AI transcript alone is not sufficient for payout.",
            proofs = proofs.Select(p => new { p.Id, p.Status, p.MediaType, p.ReviewedBy, p.ReviewedAt })
        });
    }

    public record RecommendRequest(string? Query, string? Category = null, int? MinTrust = null, int Take = 8);
    public record EditReportRequest(string? Title, string? BodyMarkdown);
    public record ProofReviewRequest(string Decision, string? Note = null);
}
