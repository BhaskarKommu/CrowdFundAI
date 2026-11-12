using System.Text;
using System.Text.Json;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Interfaces;

namespace CrowdFund.Core.Services;

/// <summary>A9 — Draft impact reports from ledger + expenses; creator edits before send.</summary>
public class ImpactReportService
{
    private readonly ILlmClient _llm;

    public ImpactReportService(ILlmClient llm) => _llm = llm;

    public async Task<ImpactReport> DraftAsync(
        Campaign campaign,
        IEnumerable<Expense> expenses,
        IEnumerable<Donation> donations,
        string? ledgerHeadHash,
        CancellationToken ct = default)
    {
        var exp = expenses.ToList();
        var byCat = exp.GroupBy(e => e.Category)
            .Select(g => (Category: g.Key, Total: g.Sum(x => x.Amount), Count: g.Count()))
            .OrderByDescending(x => x.Total)
            .ToList();
        var raised = donations.Where(d => d.Status == DonationStatus.Captured).Sum(d => d.Amount);
        var spent = exp.Where(e => e.Status is ExpenseStatus.Verified or ExpenseStatus.AiExtracted).Sum(e => e.Amount);

        var facts = new StringBuilder();
        facts.AppendLine($"Campaign: {campaign.Title}");
        facts.AppendLine($"Raised: {raised:N2} {campaign.Currency}; Verified spend: {spent:N2}; Goal: {campaign.GoalAmount:N2}");
        facts.AppendLine($"Trust: {campaign.TrustScore}; Transparency: {campaign.TransparencyScore}");
        foreach (var row in byCat.Take(8))
            facts.AppendLine($"- {row.Category}: {row.Total:N2} across {row.Count} expenses");
        foreach (var e in exp.OrderByDescending(x => x.Date).Take(5))
            facts.AppendLine($"Expense {e.Id}: {e.Vendor} {e.Amount:N2} {e.Category} [{e.Status}]");
        if (!string.IsNullOrEmpty(ledgerHeadHash))
            facts.AppendLine($"Ledger head: {ledgerHeadHash}");

        var system = """
            You draft a short donor impact update for a crowdfunding campaign.
            Use ONLY the FACTS block. Do not invent amounts or vendors.
            Write in clear English, 3–6 short paragraphs + bullet list of spending categories.
            End with a one-line disclaimer that figures come from the platform ledger.
            """;
        string body;
        try
        {
            var resp = await _llm.CompleteAsync(
                new[] { new LlmMessage("user", $"FACTS:\n{facts}") },
                system, 0.3, ct);
            body = string.IsNullOrWhiteSpace(resp.Content) ? FallbackMarkdown(campaign, byCat, raised, spent) : resp.Content.Trim();
        }
        catch
        {
            body = FallbackMarkdown(campaign, byCat, raised, spent);
        }

        var citations = exp.Take(10).Select(e => $"expense:{e.Id}").Concat(new[] { $"campaign:{campaign.Id}" }).ToList();
        if (!string.IsNullOrEmpty(ledgerHeadHash))
            citations.Add($"ledger:{ledgerHeadHash[..Math.Min(16, ledgerHeadHash.Length)]}");

        return new ImpactReport
        {
            CampaignId = campaign.Id,
            Title = $"Impact update — {campaign.Title}",
            BodyMarkdown = body,
            CitationsJson = JsonSerializer.Serialize(citations),
            LedgerHeadHash = ledgerHeadHash,
            Status = ImpactReportStatus.Draft,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public string RenderHtml(ImpactReport report, Campaign campaign)
    {
        var body = System.Net.WebUtility.HtmlEncode(report.BodyMarkdown).Replace("\n", "<br/>");
        var titleSafe = System.Net.WebUtility.HtmlEncode(report.Title);
        var campaignSafe = System.Net.WebUtility.HtmlEncode(campaign.Title);
        var citationsSafe = System.Net.WebUtility.HtmlEncode(report.CitationsJson);
        var ledgerMeta = "";
        if (!string.IsNullOrEmpty(report.LedgerHeadHash))
        {
            var shortHash = report.LedgerHeadHash[..Math.Min(12, report.LedgerHeadHash.Length)];
            ledgerMeta = " · ledger " + System.Net.WebUtility.HtmlEncode(shortHash) + "…";
        }
        return
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + titleSafe + "</title>" +
            "<style>body{font-family:system-ui;max-width:720px;margin:2rem auto;color:#2D3436;line-height:1.5}" +
            "h1{color:#6C5CE7}.meta{color:#636E72;font-size:0.9rem}</style></head><body>" +
            "<h1>" + titleSafe + "</h1>" +
            $"<p class=\"meta\">{campaignSafe} · {report.UpdatedAt:u}{ledgerMeta}</p>" +
            "<div>" + body + "</div>" +
            "<p class=\"meta\">Citations: " + citationsSafe + "</p>" +
            "<p class=\"meta\">This report was drafted with AI assistance from platform ledger data; amounts are computed by server code.</p>" +
            "<p><button onclick=\"window.print()\">Print / Save as PDF</button></p>" +
            "</body></html>";
    }

    private static string FallbackMarkdown(Campaign c, IEnumerable<(string Category, decimal Total, int Count)> byCat, decimal raised, decimal spent)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Impact update: {c.Title}");
        sb.AppendLine();
        sb.AppendLine($"Donors have helped raise **{raised:N2} {c.Currency}**. Verified spending so far totals **{spent:N2}**.");
        sb.AppendLine();
        sb.AppendLine("### Where funds went");
        foreach (var row in byCat)
            sb.AppendLine($"- **{row.Category}**: {row.Total:N2}");
        sb.AppendLine();
        sb.AppendLine("_Figures are taken from the CrowdFund AI ledger and verified expenses._");
        return sb.ToString();
    }
}
