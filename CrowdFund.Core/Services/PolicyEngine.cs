using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

/// <summary>
/// Configurable policy gates for payouts, categories, and new-creator cooldowns.
/// Bind from configuration section "Policies".
/// </summary>
public class PolicyOptions
{
    public const string Section = "Policies";

    /// <summary>Minimum % of raised amount that must be in Verified/AiExtracted expenses before payout.</summary>
    public decimal MinVerifiedSpendPercentForPayout { get; set; } = 30m;

    /// <summary>Max share of raised that may sit in a single expense category (0–100).</summary>
    public decimal MaxCategorySharePercent { get; set; } = 80m;

    /// <summary>Hours after account creation before a creator may request payout.</summary>
    public int NewCreatorCooldownHours { get; set; } = 24;

    /// <summary>Minimum KYC tier name required for payout: Basic | Enhanced.</summary>
    public string MinKycTierForPayout { get; set; } = "Basic";

    /// <summary>Block payout when open High fraud flags exist.</summary>
    public bool BlockPayoutOnOpenHighFlags { get; set; } = true;

    /// <summary>Require at least one human-approved multimodal proof for milestone-linked payouts.</summary>
    public bool RequireApprovedProofForMilestonePayout { get; set; } = true;
}

public record PolicyDecision(bool Allowed, string Code, string Message);

public class PolicyEngine
{
    private readonly PolicyOptions _opt;

    public PolicyEngine(PolicyOptions? opt = null) => _opt = opt ?? new PolicyOptions();

    public PolicyOptions Options => _opt;

    public IReadOnlyList<PolicyDecision> EvaluatePayout(
        Campaign campaign,
        CreatorProfile? profile,
        IEnumerable<Expense> expenses,
        IEnumerable<FraudFlag> flags,
        DateTime? creatorCreatedAt = null,
        bool hasApprovedMultimodalProof = false,
        bool isMilestonePayout = false)
    {
        var decisions = new List<PolicyDecision>();
        var exp = expenses.ToList();
        var raised = campaign.RaisedAmount <= 0 ? 1m : campaign.RaisedAmount;
        var verified = exp.Where(e => e.Status is ExpenseStatus.Verified or ExpenseStatus.AiExtracted).Sum(e => e.Amount);
        var verifiedPct = 100m * verified / raised;

        if (verifiedPct < _opt.MinVerifiedSpendPercentForPayout)
        {
            decisions.Add(new PolicyDecision(false, "MinVerifiedSpend",
                $"Verified spend is {verifiedPct:N0}% of raised; policy requires ≥ {_opt.MinVerifiedSpendPercentForPayout:N0}% before payout."));
        }

        var byCat = exp.GroupBy(e => e.Category)
            .Select(g => new { Cat = g.Key, Share = 100m * g.Sum(x => x.Amount) / raised })
            .Where(x => x.Share > _opt.MaxCategorySharePercent)
            .ToList();
        foreach (var c in byCat)
        {
            decisions.Add(new PolicyDecision(false, "CategoryCap",
                $"Category '{c.Cat}' is {c.Share:N0}% of raised (cap {_opt.MaxCategorySharePercent:N0}%)."));
        }

        if (profile != null)
        {
            var minTier = Enum.TryParse<KycTier>(_opt.MinKycTierForPayout, true, out var t) ? t : KycTier.Basic;
            if (profile.KycTier < minTier || profile.KycTier == KycTier.Rejected)
            {
                decisions.Add(new PolicyDecision(false, "KycTier",
                    $"KYC tier {profile.KycTier} does not meet minimum {minTier}."));
            }
        }
        else
        {
            decisions.Add(new PolicyDecision(false, "KycMissing", "Creator KYC profile is required for payout."));
        }

        if (creatorCreatedAt.HasValue)
        {
            var hours = (DateTime.UtcNow - creatorCreatedAt.Value).TotalHours;
            if (hours < _opt.NewCreatorCooldownHours)
            {
                decisions.Add(new PolicyDecision(false, "NewCreatorCooldown",
                    $"New creator cooldown: {hours:N0}h elapsed, {_opt.NewCreatorCooldownHours}h required."));
            }
        }

        if (_opt.BlockPayoutOnOpenHighFlags && flags.Any(f => f.Status == "Open" && f.Severity == "High"))
        {
            decisions.Add(new PolicyDecision(false, "OpenHighFlags",
                "Open High-severity fraud flags block payout until resolved."));
        }

        if (isMilestonePayout && _opt.RequireApprovedProofForMilestonePayout && !hasApprovedMultimodalProof)
        {
            decisions.Add(new PolicyDecision(false, "ProofRequired",
                "Milestone payout requires at least one human-approved multimodal proof."));
        }

        if (decisions.Count == 0)
            decisions.Add(new PolicyDecision(true, "Ok", "All configured payout policies passed."));

        return decisions;
    }

    public bool CanPublishCampaign(CreatorProfile? profile, DateTime? creatorCreatedAt, out string reason)
    {
        if (profile?.KycTier == KycTier.Rejected)
        {
            reason = "Rejected KYC cannot publish.";
            return false;
        }
        if (creatorCreatedAt.HasValue && (DateTime.UtcNow - creatorCreatedAt.Value).TotalHours < Math.Min(1, _opt.NewCreatorCooldownHours / 24.0))
        {
            // light publish cooldown optional — allow by default
        }
        reason = "ok";
        return true;
    }
}
