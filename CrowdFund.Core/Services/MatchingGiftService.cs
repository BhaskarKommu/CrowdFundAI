using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

public class MatchingGiftService
{
    public bool IsWindowOpen(MatchingGiftProgram program, DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.UtcNow;
        if (program.Status != MatchCampaignStatus.Active) return false;
        if (program.EndsAt.HasValue && program.EndsAt < now) return false;
        if (program.StartsAt.HasValue && program.StartsAt > now) return false;
        if (program.PoolRemaining <= 0) return false;
        return true;
    }

    public bool IsCampaignEligible(MatchingGiftProgram program, Campaign campaign)
    {
        if (program.CampaignId.HasValue && program.CampaignId != campaign.Id)
            return false;
        if (program.TenantId.HasValue && campaign.TenantId != program.TenantId)
            return false;
        if (string.IsNullOrWhiteSpace(program.EligibleCategories))
            return true;
        var cats = program.EligibleCategories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return cats.Any(c => c.Equals(campaign.Category, StringComparison.OrdinalIgnoreCase));
    }

    public (decimal matched, MatchingGiftProgram? program, string? reason) TryMatch(
        MatchingGiftProgram? program,
        decimal donorAmount,
        Campaign? campaign = null,
        bool isFirstTimeDonor = true)
    {
        if (program == null)
            return (0, null, "no_program");
        if (!IsWindowOpen(program))
            return (0, program, "window_closed_or_exhausted");
        if (campaign != null && !IsCampaignEligible(program, campaign))
            return (0, program, "campaign_not_eligible");
        if (donorAmount < program.MinDonation)
            return (0, program, "below_min_donation");
        if (program.FirstTimeDonorsOnly && !isFirstTimeDonor)
            return (0, program, "first_time_only");

        var raw = Math.Round(donorAmount * program.MatchRatio, 2, MidpointRounding.AwayFromZero);
        if (raw > program.MaxPerDonation) raw = program.MaxPerDonation;
        if (raw > program.PoolRemaining) raw = program.PoolRemaining;
        if (raw <= 0) return (0, program, "zero_match");
        return (raw, program, null);
    }

    /// <summary>Back-compat for call sites without campaign context.</summary>
    public (decimal matched, MatchingGiftProgram? program) TryMatch(
        MatchingGiftProgram? program,
        decimal donorAmount)
    {
        var (m, p, _) = TryMatch(program, donorAmount, null, true);
        return (m, p);
    }

    public void ApplyMatch(MatchingGiftProgram program, decimal matched)
    {
        program.PoolRemaining = Math.Max(0, program.PoolRemaining - matched);
        program.TotalMatched += matched;
        program.MatchCount += 1;
        if (program.PoolRemaining <= 0)
        {
            program.PoolRemaining = 0;
            program.Status = MatchCampaignStatus.Exhausted;
        }
    }

    public MatchingGiftProgram? SelectBestProgram(
        IEnumerable<MatchingGiftProgram> candidates,
        Campaign campaign,
        decimal donorAmount,
        bool isFirstTimeDonor)
    {
        MatchingGiftProgram? best = null;
        decimal bestMatch = 0;
        foreach (var p in candidates)
        {
            var (m, _, reason) = TryMatch(p, donorAmount, campaign, isFirstTimeDonor);
            if (m > bestMatch)
            {
                bestMatch = m;
                best = p;
            }
        }
        return best;
    }

    public object PublicSummary(MatchingGiftProgram p) => new
    {
        p.Id,
        p.CampaignId,
        p.SponsorName,
        p.SponsorLogoUrl,
        p.SponsorWebsite,
        p.Headline,
        p.Description,
        p.MatchRatio,
        p.PoolTotal,
        p.PoolRemaining,
        percentRemaining = p.PoolTotal <= 0 ? 0 : Math.Round(100m * p.PoolRemaining / p.PoolTotal, 1),
        p.MaxPerDonation,
        p.MinDonation,
        p.EligibleCategories,
        p.FirstTimeDonorsOnly,
        p.ShowSponsorOnDonate,
        status = p.Status.ToString(),
        p.StartsAt,
        p.EndsAt,
        p.MatchCount,
        p.TotalMatched
    };
}
