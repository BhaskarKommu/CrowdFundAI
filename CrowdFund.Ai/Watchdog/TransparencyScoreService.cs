using CrowdFund.Core.Entities;

namespace CrowdFund.Ai.Watchdog;

/// <summary>
/// A7 Live Transparency Score (0–100) from verified expenses, evidence, updates, flags, ledger.
/// </summary>
public class TransparencyScoreService
{
    public record Breakdown(
        int Score,
        int ExpensesVerifiedPct,
        int EvidenceCompleteness,
        int UpdateFreshness,
        int OpenFlagsPenalty,
        int LedgerOkBonus);

    public Breakdown Compute(
        Campaign campaign,
        IEnumerable<Expense> expenses,
        IEnumerable<CampaignUpdate> updates,
        IEnumerable<FraudFlag> flags,
        bool ledgerValid)
    {
        var exp = expenses.ToList();
        var verified = exp.Count == 0 ? 100 : (int)Math.Round(100.0 * exp.Count(e => e.Status == ExpenseStatus.Verified || e.Status == ExpenseStatus.AiExtracted) / exp.Count);
        var withEvidence = exp.Count == 0 ? 100 : (int)Math.Round(100.0 * exp.Count(e => e.Evidences.Any()) / Math.Max(1, exp.Count));

        var lastUpdate = updates.OrderByDescending(u => u.CreatedAt).FirstOrDefault()?.CreatedAt;
        int freshness = lastUpdate == null ? 40
            : (DateTime.UtcNow - lastUpdate.Value).TotalDays switch
            {
                <= 7 => 100,
                <= 30 => 70,
                <= 90 => 40,
                _ => 20
            };

        var openHigh = flags.Count(f => f.Status == "Open" && f.Severity is "High" or "Medium");
        var flagPenalty = Math.Min(40, openHigh * 15);

        var ledgerBonus = ledgerValid ? 10 : 0;

        // Weighted
        var raw = verified * 0.35 + withEvidence * 0.25 + freshness * 0.20 + (100 - flagPenalty) * 0.15 + ledgerBonus;
        var score = (int)Math.Clamp(Math.Round(raw), 0, 100);

        return new Breakdown(score, verified, withEvidence, freshness, flagPenalty, ledgerBonus);
    }

    public static string DescribeChange(Breakdown prev, Breakdown next)
    {
        var parts = new List<string>();
        if (next.Score != prev.Score)
            parts.Add($"Score {prev.Score} → {next.Score}");
        if (next.ExpensesVerifiedPct != prev.ExpensesVerifiedPct)
            parts.Add($"verified expenses {prev.ExpensesVerifiedPct}% → {next.ExpensesVerifiedPct}%");
        if (next.EvidenceCompleteness != prev.EvidenceCompleteness)
            parts.Add($"evidence completeness {prev.EvidenceCompleteness}% → {next.EvidenceCompleteness}%");
        if (next.UpdateFreshness != prev.UpdateFreshness)
            parts.Add($"update freshness {prev.UpdateFreshness} → {next.UpdateFreshness}");
        if (next.OpenFlagsPenalty != prev.OpenFlagsPenalty)
            parts.Add($"open-flag penalty {prev.OpenFlagsPenalty} → {next.OpenFlagsPenalty}");
        if (next.LedgerOkBonus != prev.LedgerOkBonus)
            parts.Add(next.LedgerOkBonus > 0 ? "ledger valid" : "ledger issue");
        return parts.Count == 0 ? "No material change" : string.Join("; ", parts);
    }
}

