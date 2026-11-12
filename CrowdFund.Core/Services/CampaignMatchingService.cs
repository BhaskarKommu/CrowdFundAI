using CrowdFund.Core.Entities;
using CrowdFund.Core.Interfaces;

namespace CrowdFund.Core.Services;

/// <summary>
/// A8 Smart campaign matching — embeddings + hard filters + citation-style reasons.
/// </summary>
public class CampaignMatchingService
{
    private readonly IEmbeddingService _embed;

    public CampaignMatchingService(IEmbeddingService embed) => _embed = embed;

    public record MatchReason(string Code, string Text, string? Citation);
    public record MatchResult(
        Guid CampaignId,
        string Title,
        string Category,
        int TrustScore,
        int TransparencyScore,
        decimal RaisedAmount,
        decimal GoalAmount,
        double Score,
        IReadOnlyList<MatchReason> Why);

    public async Task<IReadOnlyList<MatchResult>> RecommendAsync(
        string query,
        IEnumerable<Campaign> published,
        IEnumerable<Donation>? donorHistory = null,
        string? categoryFilter = null,
        int? minTrust = null,
        int take = 8,
        CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        var qVec = await _embed.EmbedAsync(string.IsNullOrEmpty(q) ? "general donation impact" : q, ct);
        var historyCats = donorHistory?
            .Select(d => d.Campaign?.Category)
            .Where(c => !string.IsNullOrEmpty(c))
            .GroupBy(c => c!)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(3)
            .ToList() ?? new List<string>();

        var candidates = published
            .Where(c => c.Status == CampaignStatus.Published)
            .Where(c => categoryFilter == null || c.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
            .Where(c => minTrust == null || c.TrustScore >= minTrust)
            .ToList();

        var results = new List<MatchResult>();
        foreach (var c in candidates)
        {
            var text = $"{c.Title}. {c.Category}. {c.Story}";
            var vec = await _embed.EmbedAsync(text, ct);
            var sim = _embed.CosineSimilarity(qVec, vec);

            var why = new List<MatchReason>();
            if (sim > 0.05)
                why.Add(new MatchReason("embedding", $"Story/title similarity to your interest ({sim:P0})", $"campaign:{c.Id}"));
            if (!string.IsNullOrEmpty(q) && (c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Story.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Category.Contains(q, StringComparison.OrdinalIgnoreCase)))
                why.Add(new MatchReason("keyword", $"Matches keyword “{q}”", $"campaign:{c.Id}:text"));
            if (historyCats.Contains(c.Category))
                why.Add(new MatchReason("history", $"Aligns with categories you’ve supported ({c.Category})", "donor:history"));
            if (c.TrustScore >= 75)
                why.Add(new MatchReason("trust", $"High trust score ({c.TrustScore})", $"campaign:{c.Id}:trust"));
            if (c.TransparencyScore >= 70)
                why.Add(new MatchReason("transparency", $"Strong transparency ({c.TransparencyScore})", $"campaign:{c.Id}:transparency"));
            var progress = c.GoalAmount <= 0 ? 0 : (double)(c.RaisedAmount / c.GoalAmount);
            if (progress is > 0.2 and < 0.9)
                why.Add(new MatchReason("momentum", $"Active progress toward goal ({progress:P0})", $"campaign:{c.Id}:raised"));

            // Composite
            var score = sim * 0.55
                        + (historyCats.Contains(c.Category) ? 0.15 : 0)
                        + Math.Min(c.TrustScore, 100) / 100.0 * 0.15
                        + Math.Min(c.TransparencyScore, 100) / 100.0 * 0.10
                        + (progress is > 0.2 and < 0.9 ? 0.05 : 0);

            if (why.Count == 0)
                why.Add(new MatchReason("default", "Published campaign within filters", $"campaign:{c.Id}"));

            results.Add(new MatchResult(c.Id, c.Title, c.Category, c.TrustScore, c.TransparencyScore,
                c.RaisedAmount, c.GoalAmount, score, why));
        }

        return results.OrderByDescending(r => r.Score).Take(take).ToList();
    }
}
