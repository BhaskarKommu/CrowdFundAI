using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;

namespace CrowdFund.Ai.Concierge;

/// <summary>
/// Read-only tools for the Grounded Donor Concierge (A5 / 2.0).
/// PrepareDonation only creates a pending intent — never moves money.
/// </summary>
public class ConciergeTools
{
    public record ToolResult(string ToolName, string JsonPayload, IReadOnlyList<string> CitationIds);

    public ToolResult GetCampaignStatus(Campaign c)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            campaignId = c.Id,
            title = c.Title,
            status = c.Status.ToString(),
            category = c.Category,
            goalAmount = c.GoalAmount,
            raisedAmount = c.RaisedAmount,
            currency = c.Currency,
            trustScore = c.TrustScore,
            transparencyScore = c.TransparencyScore,
            donorCount = c.DonorCount,
            publishedAt = c.PublishedAt
        });
        return new ToolResult("GetCampaignStatus", payload, new[] { $"campaign:{c.Id}" });
    }

    public ToolResult GetDonationAllocation(Donation donation, Campaign campaign, IEnumerable<Expense> expenses, FollowMyMoneyService fmm)
    {
        var bd = fmm.BuildForDonation(donation, campaign, expenses);
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            donationId = donation.Id,
            amount = donation.Amount,
            currency = donation.Currency,
            totalRaised = bd.TotalRaised,
            totalSpent = bd.TotalSpent,
            remaining = bd.Remaining,
            categories = bd.Categories.Select(x => new { x.Category, x.Amount, x.Percent, x.DonorShare })
        });
        var cites = new List<string> { $"donation:{donation.Id}" };
        cites.AddRange(bd.Expenses.Select(e => $"expense:{e.Id}"));
        return new ToolResult("GetDonationAllocation", payload, cites);
    }

    public ToolResult ListExpenses(IEnumerable<Expense> expenses, string? category = null)
    {
        var list = expenses
            .Where(e => category == null || e.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Select(e => new
            {
                expenseId = e.Id,
                e.Vendor,
                e.Description,
                e.Amount,
                e.Category,
                e.Status,
                e.Date
            })
            .ToList();
        var payload = System.Text.Json.JsonSerializer.Serialize(list);
        return new ToolResult("ListExpenses", payload, list.Select(x => $"expense:{x.expenseId}").ToList());
    }

    public ToolResult GetEvidence(Expense expense)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            expenseId = expense.Id,
            expense.Vendor,
            expense.Amount,
            expense.Category,
            expense.Status,
            expense.ExtractionConfidence,
            evidences = expense.Evidences.Select(ev => new { ev.FileName, ev.Sha256, ev.StoredPath })
        });
        return new ToolResult("GetEvidence", payload, new[] { $"expense:{expense.Id}" });
    }

    public ToolResult GetTrustAssessment(Campaign c)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            campaignId = c.Id,
            trustScore = c.TrustScore,
            transparencyScore = c.TransparencyScore,
            explanationJson = c.TrustExplanationJson,
            status = c.Status.ToString()
        });
        return new ToolResult("GetTrustAssessment", payload, new[] { $"campaign:{c.Id}", $"trust:{c.Id}" });
    }

    public ToolResult SearchCampaigns(IEnumerable<Campaign> published, string query)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        var hits = published
            .Where(c => c.Status == CampaignStatus.Published)
            .Where(c => string.IsNullOrEmpty(q)
                        || c.Title.ToLowerInvariant().Contains(q)
                        || c.Category.ToLowerInvariant().Contains(q)
                        || c.Story.ToLowerInvariant().Contains(q))
            .Take(8)
            .Select(c => new { c.Id, c.Title, c.Category, c.TrustScore, c.RaisedAmount, c.GoalAmount })
            .ToList();
        var payload = System.Text.Json.JsonSerializer.Serialize(hits);
        return new ToolResult("SearchCampaigns", payload, hits.Select(h => $"campaign:{h.Id}").ToList());
    }

    public ToolResult PrepareDonation(Guid campaignId, decimal amount, string currency = "USD")
    {
        if (amount <= 0 || amount > 100_000)
            return new ToolResult("PrepareDonation",
                System.Text.Json.JsonSerializer.Serialize(new { error = "Invalid amount" }),
                Array.Empty<string>());

        var intentId = Guid.NewGuid();
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            intentId,
            campaignId,
            amount,
            currency,
            status = "pending_confirmation",
            message = "Confirm to continue to PayPal. The agent cannot move money."
        });
        return new ToolResult("PrepareDonation", payload, new[] { $"campaign:{campaignId}", $"intent:{intentId}" });
    }

    public ToolResult GetFraudFlags(IEnumerable<FraudFlag> flags)
    {
        var list = flags.Select(f => new
        {
            flagId = f.Id,
            f.Rule,
            f.Severity,
            f.Status,
            f.Explanation
        }).ToList();
        var payload = System.Text.Json.JsonSerializer.Serialize(list);
        return new ToolResult("GetFraudFlags", payload, list.Select(x => $"flag:{x.flagId}").ToList());
    }

    public ToolResult GetTransparencyHistory(IEnumerable<TransparencyScoreSnapshot> snaps)
    {
        var list = snaps.Take(10).Select(s => new
        {
            s.Score, s.PreviousScore, s.Delta, s.ChangeSummary, s.CreatedAt
        }).ToList();
        var payload = System.Text.Json.JsonSerializer.Serialize(list);
        return new ToolResult("GetTransparencyHistory", payload, new[] { "transparency:history" });
    }

    public ToolResult GetPolicySummary(object policySnapshot)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(policySnapshot);
        return new ToolResult("GetPolicySummary", payload, new[] { "policy:config" });
    }

    public ToolResult CompareCampaigns(Campaign a, Campaign b)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            a = new { a.Id, a.Title, a.TrustScore, a.TransparencyScore, a.RaisedAmount, a.GoalAmount, a.Category },
            b = new { b.Id, b.Title, b.TrustScore, b.TransparencyScore, b.RaisedAmount, b.GoalAmount, b.Category }
        });
        return new ToolResult("CompareCampaigns", payload, new[] { $"campaign:{a.Id}", $"campaign:{b.Id}" });
    }
}
