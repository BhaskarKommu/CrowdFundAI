using System.Security.Claims;
using System.Text;
using CrowdFund.Ai.Watchdog;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class TrustEndpoints
{
    public static void MapTrustEndpoints(this WebApplication app)
    {
        // Public badge (no auth) — embeddable
        app.MapGet("/api/public/campaigns/{id:guid}/badge.json", BadgeJson);
        app.MapGet("/api/public/campaigns/{id:guid}/badge.svg", BadgeSvg);
        app.MapGet("/api/public/campaigns/{id:guid}/transparency/history", TransparencyHistory);

        // Evidence pack
        app.MapGet("/api/expenses/{id:guid}/evidence-pack", EvidencePack);

        // Donor receipt challenge
        app.MapPost("/api/expenses/{id:guid}/challenge", SubmitChallenge).RequireAuthorization();
        app.MapGet("/api/expenses/{id:guid}/challenges", ListChallenges);
        app.MapGet("/api/admin/challenges", AdminChallenges);
        app.MapGet("/api/expenses/evidence/{evidenceId:guid}/file", DownloadEvidence);
    }

    private static async Task<IResult> BadgeJson(
        Guid id, AppDbContext db, TransparencyScoreService transparency, LedgerService ledger)
    {
        var data = await BuildBadgeData(id, db, transparency, ledger);
        if (data == null) return Results.NotFound();
        return Results.Ok(data);
    }

    private static async Task<IResult> BadgeSvg(
        Guid id, AppDbContext db, TransparencyScoreService transparency, LedgerService ledger)
    {
        var data = await BuildBadgeData(id, db, transparency, ledger);
        if (data == null) return Results.NotFound();

        var band = (string)data.GetType().GetProperty("trustBand")!.GetValue(data)!;
        var trust = (int)data.GetType().GetProperty("trustScore")!.GetValue(data)!;
        var trans = (int)data.GetType().GetProperty("transparencyScore")!.GetValue(data)!;
        var verifiedPct = (int)data.GetType().GetProperty("verifiedSpendPercent")!.GetValue(data)!;
        var hash = (string?)data.GetType().GetProperty("ledgerHeadHash")!.GetValue(data) ?? "";
        var title = (string)data.GetType().GetProperty("title")!.GetValue(data)!;
        var shortHash = hash.Length > 12 ? hash[..12] : hash;

        var color = band switch
        {
            "High" => "#2E7D32",
            "Medium" => "#F9A825",
            _ => "#C62828"
        };

        var safeTitle = System.Net.WebUtility.HtmlEncode(title.Length > 40 ? title[..40] + "…" : title);
        var svg = $"""
<svg xmlns="http://www.w3.org/2000/svg" width="420" height="96" role="img" aria-label="CrowdFund AI verification badge">
  <rect width="420" height="96" rx="12" fill="#FAFAFE" stroke="#E8E6F5"/>
  <rect x="0" y="0" width="8" height="96" fill="{color}"/>
  <text x="24" y="28" font-family="system-ui,Segoe UI,sans-serif" font-size="14" font-weight="700" fill="#6C5CE7">CrowdFund AI</text>
  <text x="24" y="48" font-family="system-ui,Segoe UI,sans-serif" font-size="13" fill="#2D3436">{safeTitle}</text>
  <text x="24" y="70" font-family="system-ui,Segoe UI,sans-serif" font-size="12" fill="#636E72">Trust {trust} ({band}) · Transparency {trans} · Verified spend {verifiedPct}%</text>
  <text x="24" y="88" font-family="ui-monospace,monospace" font-size="10" fill="#636E72">ledger {System.Net.WebUtility.HtmlEncode(shortHash)}…</text>
</svg>
""";
        return Results.Content(svg, "image/svg+xml");
    }

    private static async Task<object?> BuildBadgeData(
        Guid id, AppDbContext db, TransparencyScoreService transparency, LedgerService ledger)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (campaign == null) return null;
        if (campaign.Status is CampaignStatus.Draft or CampaignStatus.Rejected)
            return null; // only public-ish campaigns

        var expenses = await db.Expenses.AsNoTracking().Include(e => e.Evidences)
            .Where(e => e.CampaignId == id).ToListAsync();
        var updates = await db.CampaignUpdates.AsNoTracking().Where(u => u.CampaignId == id).ToListAsync();
        var flags = await db.FraudFlags.AsNoTracking().Where(f => f.CampaignId == id).ToListAsync();
        var entries = await db.AuditLedger.Where(e => e.CampaignId == id).OrderBy(e => e.Id).ToListAsync();
        var (valid, _) = ledger.VerifyChain(entries);
        var breakdown = transparency.Compute(campaign, expenses, updates, flags, valid);

        var spent = expenses.Where(e => e.Status is ExpenseStatus.Verified or ExpenseStatus.AiExtracted).Sum(e => e.Amount);
        var verifiedPct = campaign.RaisedAmount <= 0 ? 0
            : (int)Math.Clamp(Math.Round(100m * spent / campaign.RaisedAmount), 0, 100);

        var band = campaign.TrustScore >= 75 ? "High" : campaign.TrustScore >= 50 ? "Medium" : "Low";
        var head = entries.LastOrDefault()?.Hash ?? "";

        return new
        {
            campaignId = campaign.Id,
            title = campaign.Title,
            status = campaign.Status.ToString(),
            trustScore = campaign.TrustScore,
            trustBand = band,
            transparencyScore = campaign.TransparencyScore != 0 ? campaign.TransparencyScore : breakdown.Score,
            verifiedSpendPercent = verifiedPct,
            ledgerValid = valid,
            ledgerHeadHash = head,
            openFlags = flags.Count(f => f.Status == "Open"),
            generatedAt = DateTime.UtcNow,
            embed = new
            {
                svgUrl = $"/api/public/campaigns/{campaign.Id}/badge.svg",
                jsonUrl = $"/api/public/campaigns/{campaign.Id}/badge.json"
            },
            disclaimer = "Advisory risk indicators only — not a guarantee of legitimacy."
        };
    }

    private static async Task<IResult> TransparencyHistory(Guid id, AppDbContext db)
    {
        var exists = await db.Campaigns.AsNoTracking().AnyAsync(c => c.Id == id);
        if (!exists) return Results.NotFound();
        var history = await db.TransparencyScoreSnapshots.AsNoTracking()
            .Where(s => s.CampaignId == id)
            .OrderByDescending(s => s.Id)
            .Take(50)
            .ToListAsync();
        return Results.Ok(new
        {
            campaignId = id,
            current = history.FirstOrDefault(),
            history,
            whatChanged = history.Take(5).Select(h => new
            {
                h.CreatedAt,
                h.PreviousScore,
                h.Score,
                h.Delta,
                h.ChangeSummary,
                h.Source
            })
        });
    }

    private static async Task<IResult> EvidencePack(Guid id, AppDbContext db)
    {
        var expense = await db.Expenses.AsNoTracking()
            .Include(e => e.Evidences)
            .Include(e => e.LineItems)
            .FirstOrDefaultAsync(e => e.Id == id);
        if (expense == null) return Results.NotFound();

        var challenges = await db.ReceiptChallenges.AsNoTracking()
            .Where(c => c.ExpenseId == id)
            .OrderByDescending(c => c.CreatedAt)
            .Take(20)
            .ToListAsync();

        var matchCount = challenges.Count(c => c.Verdict == ReceiptChallengeVerdict.Matches);
        var mismatchCount = challenges.Count(c => c.Verdict == ReceiptChallengeVerdict.DoesNotMatch);

        return Results.Ok(new
        {
            expense = new
            {
                expense.Id,
                expense.CampaignId,
                expense.Vendor,
                expense.Description,
                expense.Amount,
                expense.Currency,
                expense.Category,
                expense.Date,
                status = expense.Status.ToString(),
                expense.ExtractionConfidence,
                expense.ExtractionJson,
                expense.ReviewedBy,
                expense.ReviewedAt
            },
            evidences = expense.Evidences.Select(ev => new
            {
                ev.Id,
                ev.FileName,
                ev.Sha256,
                ev.StoredPath,
                ev.PerceptualHash,
                ev.ExtractedJson,
                ev.ForensicsJson,
                downloadUrl = $"/api/expenses/evidence/{ev.Id}/file"
            }),
            lineItems = expense.LineItems.Select(li => new { li.Description, li.Qty, li.UnitPrice, amount = li.Qty * li.UnitPrice, li.Category }),
            humanReview = new
            {
                status = expense.Status.ToString(),
                expense.ReviewedBy,
                expense.ReviewedAt
            },
            donorChallenges = new
            {
                total = challenges.Count,
                matches = matchCount,
                mismatches = mismatchCount,
                latest = challenges.Take(5)
            },
            integrity = new
            {
                note = "SHA-256 of original files; compare to download to detect tampering."
            }
        });
    }

    private static async Task<IResult> SubmitChallenge(
        Guid id,
        [FromBody] ChallengeRequest req,
        HttpContext http,
        AppDbContext db)
    {
        var expense = await db.Expenses.FirstOrDefaultAsync(e => e.Id == id);
        if (expense == null) return Results.NotFound();

        if (!Enum.TryParse<ReceiptChallengeVerdict>(req.Verdict, true, out var verdict)
            || verdict == ReceiptChallengeVerdict.Pending)
            return Results.BadRequest(new { error = "Verdict must be Matches, DoesNotMatch, or Unsure" });

        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var challenge = new ReceiptChallenge
        {
            ExpenseId = id,
            CampaignId = expense.CampaignId,
            DonorUserId = userId,
            Verdict = verdict,
            Comment = req.Comment,
            FlaggedForReview = verdict == ReceiptChallengeVerdict.DoesNotMatch
        };
        db.ReceiptChallenges.Add(challenge);

        // Feed back into rules: mismatch creates a soft fraud flag for admin queue
        if (verdict == ReceiptChallengeVerdict.DoesNotMatch)
        {
            db.FraudFlags.Add(new FraudFlag
            {
                CampaignId = expense.CampaignId,
                ExpenseId = expense.Id,
                Rule = "DonorReceiptChallenge",
                Severity = "Medium",
                Explanation = $"Donor reported receipt does not match. Comment: {req.Comment}",
                Status = "Open"
            });
        }

        await db.SaveChangesAsync();
        return Results.Ok(new { challenge.Id, challenge.Verdict, challenge.FlaggedForReview });
    }

    private static async Task<IResult> ListChallenges(Guid id, AppDbContext db)
    {
        var list = await db.ReceiptChallenges.AsNoTracking()
            .Where(c => c.ExpenseId == id)
            .OrderByDescending(c => c.CreatedAt)
            .Take(50)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> AdminChallenges(AppDbContext db)
    {
        var list = await db.ReceiptChallenges.AsNoTracking()
            .Where(c => c.FlaggedForReview)
            .OrderByDescending(c => c.CreatedAt)
            .Take(100)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> DownloadEvidence(Guid evidenceId, AppDbContext db, CrowdFund.Core.Interfaces.IFileStore files)
    {
        var ev = await db.ExpenseEvidences.AsNoTracking().FirstOrDefaultAsync(e => e.Id == evidenceId);
        if (ev == null) return Results.NotFound();
        try
        {
            await using var stream = await files.OpenReadAsync(ev.StoredPath);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var contentType = "application/octet-stream";
            var name = ev.FileName.ToLowerInvariant();
            if (name.EndsWith(".pdf")) contentType = "application/pdf";
            else if (name.EndsWith(".png")) contentType = "image/png";
            else if (name.EndsWith(".jpg") || name.EndsWith(".jpeg")) contentType = "image/jpeg";
            return Results.File(bytes, contentType, ev.FileName);
        }
        catch
        {
            return Results.NotFound(new { error = "File missing from store" });
        }
    }

    public record ChallengeRequest(string Verdict, string? Comment = null);
}

