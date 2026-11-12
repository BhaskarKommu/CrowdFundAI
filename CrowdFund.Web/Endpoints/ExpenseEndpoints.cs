using Microsoft.AspNetCore.RateLimiting;
using CrowdFund.Ai.Watchdog;
using System.Security.Claims;
using System.Security.Cryptography;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Interfaces;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class ExpenseEndpoints
{
    public static void MapExpenseEndpoints(this WebApplication app)
    {
        app.MapPost("/api/campaigns/{campaignId:guid}/expenses", UploadExpense).DisableAntiforgery().RequireRateLimiting("strict");
        app.MapGet("/api/campaigns/{campaignId:guid}/expenses", ListExpenses).RequireRateLimiting("strict");
        app.MapPatch("/api/expenses/{id:guid}/review", ReviewExpense);
        app.MapGet("/api/campaigns/{campaignId:guid}/money-flow", GetMoneyFlow);
        app.MapGet("/api/donations/{id:guid}/allocation", GetAllocation);
        app.MapGet("/api/donations/{id:guid}/impact-receipt", GetImpactReceipt);
        app.MapGet("/api/campaigns/{campaignId:guid}/transparency-score", GetTransparencyScore);
    }

    private static async Task<IResult> UploadExpense(
        Guid campaignId,
        HttpRequest request,
        AppDbContext db,
        IFileStore files,
        ReceiptIntelligence receiptAi,
        LedgerService ledger,
        HttpContext http)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound(new { error = "Campaign not found" });

        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "multipart/form-data required" });

        var form = await request.ReadFormAsync();
        var amountStr = form["amount"].FirstOrDefault();
        var vendor = form["vendor"].FirstOrDefault() ?? "";
        var description = form["description"].FirstOrDefault() ?? "";
        var category = form["category"].FirstOrDefault() ?? "other";
        var dateStr = form["date"].FirstOrDefault();

        if (!decimal.TryParse(amountStr, out var amount) || amount <= 0)
            return Results.BadRequest(new { error = "Valid amount required" });

        DateTime date = DateTime.UtcNow.Date;
        if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var parsed))
            date = parsed.Date;

        var expense = new Expense
        {
            CampaignId = campaignId,
            Date = date,
            Vendor = vendor,
            Description = description,
            Amount = amount,
            Currency = campaign.Currency,
            Category = category,
            Status = ExpenseStatus.Submitted
        };

        // Optional file
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        string? storedPath = null;
        string sha256 = "";
        if (file != null && file.Length > 0)
        {
            await using var stream = file.OpenReadStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            sha256 = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
            ms.Position = 0;
            storedPath = await files.SaveAsync(ms, file.FileName, file.ContentType ?? "application/octet-stream");
            expense.Evidences.Add(new ExpenseEvidence
            {
                FileName = file.FileName,
                StoredPath = storedPath,
                Sha256 = sha256
            });
        }

        // Receipt Intelligence (A2)
        var hint = $"Vendor: {vendor}. Amount: {amount}. Description: {description}. Category: {category}.";
        var extraction = await receiptAi.ExtractAsync(
            hint,
            campaign.Category,
            campaign.CreatedAt,
            campaign.Status == CampaignStatus.Closed ? DateTime.UtcNow : null,
            amount);

        receiptAi.ApplyToExpense(expense, extraction);
        // Keep creator amount if extraction failed badly
        if (expense.Amount <= 0) expense.Amount = amount;
        if (string.IsNullOrEmpty(expense.Vendor)) expense.Vendor = vendor;

        db.Expenses.Add(expense);

        var prevHash = await db.AuditLedger
            .Where(e => e.CampaignId == campaignId)
            .OrderByDescending(e => e.Id)
            .Select(e => e.Hash)
            .FirstOrDefaultAsync() ?? "";

        var entry = ledger.CreateEntry(campaignId, "expense.added", new
        {
            expenseId = expense.Id,
            amount = expense.Amount,
            vendor = expense.Vendor,
            category = expense.Category,
            status = expense.Status.ToString(),
            confidence = expense.ExtractionConfidence
        }, prevHash);
        db.AuditLedger.Add(entry);

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            expense.Id,
            expense.Vendor,
            expense.Amount,
            expense.Category,
            expense.Status,
            expense.ExtractionConfidence,
            validationErrors = extraction.ValidationErrors,
            lineItems = expense.LineItems.Select(l => new { l.Description, l.Qty, l.UnitPrice, l.Category }),
            ledgerHash = entry.Hash
        });
    }

    private static async Task<IResult> ListExpenses(Guid campaignId, AppDbContext db)
    {
        var list = await db.Expenses
            .AsNoTracking()
            .Where(e => e.CampaignId == campaignId)
            .OrderByDescending(e => e.Date)
            .Select(e => new
            {
                e.Id,
                e.Vendor,
                e.Description,
                e.Amount,
                e.Currency,
                e.Category,
                e.Status,
                e.Date,
                e.ExtractionConfidence,
                LineItemCount = e.LineItems.Count
            })
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> ReviewExpense(
        Guid id,
        [FromBody] ReviewRequest req,
        AppDbContext db,
        LedgerService ledger)
    {
        var expense = await db.Expenses.FirstOrDefaultAsync(e => e.Id == id);
        if (expense == null) return Results.NotFound();

        expense.Status = req.Approve ? ExpenseStatus.Verified : ExpenseStatus.Rejected;
        if (!string.IsNullOrEmpty(req.Category)) expense.Category = req.Category;

        var prevHash = await db.AuditLedger
            .Where(e => e.CampaignId == expense.CampaignId)
            .OrderByDescending(e => e.Id)
            .Select(e => e.Hash)
            .FirstOrDefaultAsync() ?? "";

        db.AuditLedger.Add(ledger.CreateEntry(expense.CampaignId,
            req.Approve ? "expense.verified" : "expense.rejected",
            new { expenseId = expense.Id, status = expense.Status.ToString() }, prevHash));

        await db.SaveChangesAsync();
        if (req.Approve)
        {
            var donorIds = await db.Donations.AsNoTracking()
                .Where(d => d.CampaignId == expense.CampaignId && d.Status == DonationStatus.Captured && d.DonorId != null)
                .Select(d => d.DonorId!)
                .Distinct()
                .ToListAsync();
            await GrowthEndpoints.QueuePushAsync(db, "ExpenseVerified", "Expense verified",
                $"An expense on a campaign you support was verified.", expense.CampaignId, donorIds);
        }
        return Results.Ok(new { expense.Id, expense.Status });
    }

    private static async Task<IResult> GetMoneyFlow(Guid campaignId, AppDbContext db, FollowMyMoneyService fmm)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();

        var expenses = await db.Expenses
            .AsNoTracking()
            .Where(e => e.CampaignId == campaignId)
            .ToListAsync();

        // Aggregate view as if one synthetic donation = total raised
        var synthetic = new Donation
        {
            Id = Guid.Empty,
            CampaignId = campaignId,
            Amount = campaign.RaisedAmount > 0 ? campaign.RaisedAmount : 1,
            Currency = campaign.Currency,
            Status = DonationStatus.Captured
        };
        var breakdown = fmm.BuildForDonation(synthetic, campaign, expenses);
        return Results.Ok(new
        {
            campaignId,
            method = fmm.AllocationMethodSentence(),
            breakdown.TotalRaised,
            breakdown.TotalSpent,
            breakdown.Remaining,
            breakdown.Categories,
            breakdown.Expenses,
            sankey = breakdown.Sankey
        });
    }

    private static async Task<IResult> GetAllocation(Guid id, AppDbContext db, FollowMyMoneyService fmm)
    {
        var donation = await db.Donations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
        if (donation == null) return Results.NotFound();
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == donation.CampaignId);
        if (campaign == null) return Results.NotFound();
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.CampaignId == campaign.Id).ToListAsync();
        var breakdown = fmm.BuildForDonation(donation, campaign, expenses);
        return Results.Ok(new
        {
            method = fmm.AllocationMethodSentence(),
            breakdown
        });
    }

    private static async Task<IResult> GetImpactReceipt(Guid id, AppDbContext db, FollowMyMoneyService fmm, LedgerService ledger)
    {
        var donation = await db.Donations.AsNoTracking().Include(d => d.Campaign).FirstOrDefaultAsync(d => d.Id == id);
        if (donation == null || donation.Status != DonationStatus.Captured)
            return Results.NotFound();

        var campaign = donation.Campaign!;
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.CampaignId == campaign.Id).ToListAsync();
        var breakdown = fmm.BuildForDonation(donation, campaign, expenses);

        var entries = await db.AuditLedger.Where(e => e.CampaignId == campaign.Id).OrderBy(e => e.Id).ToListAsync();
        var (valid, _) = ledger.VerifyChain(entries);
        var headHash = entries.LastOrDefault()?.Hash ?? "";

        // Simple text/HTML impact receipt (printable). Full PDF via QuestPDF can be added later.
        var rows = string.Join("", breakdown.Categories.Select(c =>
            $"<tr><td>{System.Net.WebUtility.HtmlEncode(c.Category)}</td><td>{c.Amount:N2}</td><td>{c.DonorShare:N2}</td><td>{c.Percent}%</td></tr>"));
        var html =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Impact Receipt</title>" +
            "<style>body{font-family:system-ui;max-width:640px;margin:2rem auto;color:#2D3436}" +
            "h1{color:#6C5CE7}.meta{color:#636E72;font-size:0.9rem}table{width:100%;border-collapse:collapse;margin:1rem 0}" +
            "th,td{text-align:left;padding:0.5rem;border-bottom:1px solid #E8E6F5}.hash{font-family:monospace;font-size:0.75rem;word-break:break-all}</style></head>" +
            "<body>" +
            "<h1>Impact Receipt — CrowdFund AI</h1>" +
            $"<p class=\"meta\">Generated {DateTime.UtcNow:u} · Ledger verified: {(valid ? "✔" : "✖")}</p>" +
            $"<p><strong>Campaign:</strong> {System.Net.WebUtility.HtmlEncode(campaign.Title)}</p>" +
            $"<p><strong>Your donation:</strong> {donation.Amount:N2} {donation.Currency}<br/>" +
            $"<strong>Capture ID:</strong> {System.Net.WebUtility.HtmlEncode(donation.PayPalCaptureId ?? "")}<br/>" +
            $"<strong>Date:</strong> {donation.CapturedAt:u}</p>" +
            $"<p class=\"meta\">{fmm.AllocationMethodSentence()}</p>" +
            "<h3>Category allocation (your share)</h3>" +
            "<table><tr><th>Category</th><th>Campaign spent</th><th>Your share</th><th>%</th></tr>" +
            rows +
            "</table>" +
            $"<p><strong>Remaining (unspent of raised):</strong> {breakdown.Remaining:N2}</p>" +
            $"<p class=\"hash\">Ledger head hash: {System.Net.WebUtility.HtmlEncode(headHash)}</p>" +
            "<p class=\"meta\">This receipt is advisory. Amounts are computed by deterministic server code, not AI.</p>" +
            "</body></html>";

        return Results.Content(html, "text/html");
    }


    private static async Task<IResult> GetTransparencyScore(
        Guid campaignId,
        AppDbContext db,
        TransparencyScoreService transparency,
        LedgerService ledger)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        var expenses = await db.Expenses.AsNoTracking().Include(e => e.Evidences)
            .Where(e => e.CampaignId == campaignId).ToListAsync();
        var updates = await db.CampaignUpdates.AsNoTracking()
            .Where(u => u.CampaignId == campaignId).ToListAsync();
        var flags = await db.FraudFlags.AsNoTracking()
            .Where(f => f.CampaignId == campaignId).ToListAsync();
        var entries = await db.AuditLedger.Where(e => e.CampaignId == campaignId)
            .OrderBy(e => e.Id).ToListAsync();
        var (valid, _) = ledger.VerifyChain(entries);
        var score = transparency.Compute(campaign, expenses, updates, flags, valid);
        return Results.Ok(new
        {
            campaignId,
            score = score.Score,
            breakdown = new
            {
                score.Score,
                score.ExpensesVerifiedPct,
                score.EvidenceCompleteness,
                score.UpdateFreshness,
                score.OpenFlagsPenalty,
                score.LedgerOkBonus
            },
            storedTransparencyScore = campaign.TransparencyScore,
            ledgerValid = valid
        });
    }

    public record ReviewRequest(bool Approve, string? Category = null);
}
