using CrowdFund.Core.Entities;

namespace CrowdFund.Ai.Fraud;

/// <summary>
/// Statistical anomaly signals beyond static rules: spend velocity, category mix vs peers, image reuse.
/// Emits FraudFlag rows (advisory).
/// </summary>
public class AnomalyEngine
{
    public IReadOnlyList<FraudFlag> Evaluate(
        Campaign campaign,
        IEnumerable<Expense> expenses,
        IEnumerable<Campaign> peerCampaigns,
        IEnumerable<Expense> peerExpenses,
        IEnumerable<(string Sha256, Guid CampaignId, Guid ExpenseId)> allEvidenceHashes)
    {
        var flags = new List<FraudFlag>();
        var exp = expenses.OrderBy(e => e.Date).ToList();
        if (exp.Count == 0) return flags;

        // Spend velocity: amount per day over active window
        var first = exp.First().Date;
        var last = exp.Last().Date;
        var days = Math.Max(1.0, (last - first).TotalDays + 1);
        var total = exp.Sum(e => e.Amount);
        var velocity = (double)total / days;

        var peerVelocities = new List<double>();
        foreach (var peer in peerCampaigns.Where(c => c.Id != campaign.Id && c.Category == campaign.Category))
        {
            var pe = peerExpenses.Where(e => e.CampaignId == peer.Id).OrderBy(e => e.Date).ToList();
            if (pe.Count < 2) continue;
            var d = Math.Max(1.0, (pe.Last().Date - pe.First().Date).TotalDays + 1);
            peerVelocities.Add((double)pe.Sum(x => x.Amount) / d);
        }
        if (peerVelocities.Count >= 3)
        {
            var mean = peerVelocities.Average();
            var std = Math.Sqrt(peerVelocities.Average(v => Math.Pow(v - mean, 2)));
            if (std > 0 && velocity > mean + 2.5 * std)
            {
                flags.Add(Flag(campaign.Id, null, "SpendVelocityAnomaly", "Medium", 0.6,
                    $"Spend velocity {velocity:N0}/day is high vs peer {campaign.Category} campaigns (mean {mean:N0}, σ {std:N0}).",
                    new { velocity, mean, std, days }));
            }
        }
        else if (days <= 2 && total > campaign.GoalAmount * 0.5m && exp.Count >= 3)
        {
            flags.Add(Flag(campaign.Id, null, "SpendVelocityBurst", "Low", 0.45,
                "Large share of goal spent across many expenses in ≤2 days — review timing.",
                new { total, days, count = exp.Count }));
        }

        // Category mix vs peers
        var campMix = CategoryShares(exp);
        var peerMix = CategoryShares(peerExpenses.Where(e => peerCampaigns.Any(c => c.Id == e.CampaignId && c.Category == campaign.Category && c.Id != campaign.Id)));
        if (peerMix.Count > 0 && campMix.Count > 0)
        {
            foreach (var (cat, share) in campMix)
            {
                peerMix.TryGetValue(cat, out var peerShare);
                if (share >= 0.7 && peerShare < 0.25)
                {
                    flags.Add(Flag(campaign.Id, null, "CategoryMixAnomaly", "Low", 0.5,
                        $"Category '{cat}' is {share:P0} of this campaign’s spend vs ~{peerShare:P0} among peers.",
                        new { cat, share, peerShare }));
                }
            }
        }

        // Image / evidence reuse across campaigns (hash)
        var myHashes = exp.SelectMany(e => e.Evidences.Select(ev => (ev.Sha256, e.Id)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Sha256))
            .ToList();
        foreach (var (sha, expenseId) in myHashes)
        {
            var others = allEvidenceHashes
                .Where(h => h.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase) && h.CampaignId != campaign.Id)
                .ToList();
            if (others.Count > 0)
            {
                flags.Add(Flag(campaign.Id, expenseId, "ImageReuseAcrossCampaigns", "High", 0.9,
                    $"Evidence hash reused on {others.Count} other campaign expense(s).",
                    new { sha, others = others.Select(o => new { o.CampaignId, o.ExpenseId }) }));
            }
        }

        return flags;
    }

    private static Dictionary<string, double> CategoryShares(IEnumerable<Expense> expenses)
    {
        var list = expenses.ToList();
        var sum = list.Sum(e => e.Amount);
        if (sum <= 0) return new Dictionary<string, double>();
        return list.GroupBy(e => string.IsNullOrEmpty(e.Category) ? "other" : e.Category)
            .ToDictionary(g => g.Key, g => (double)(g.Sum(x => x.Amount) / sum));
    }

    private static FraudFlag Flag(Guid campaignId, Guid? expenseId, string rule, string severity, double score, string explanation, object? evidence)
        => new()
        {
            CampaignId = campaignId,
            ExpenseId = expenseId,
            Rule = rule,
            Severity = severity,
            Score = score,
            Explanation = explanation,
            EvidenceJson = evidence == null ? null : System.Text.Json.JsonSerializer.Serialize(evidence),
            Status = "Open",
            CreatedAt = DateTime.UtcNow
        };
}
