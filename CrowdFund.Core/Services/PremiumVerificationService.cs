using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

public class PremiumVerificationService
{
    public PremiumVerificationSubscription Activate(
        string creatorId,
        PremiumPlan plan,
        int durationDays = 30,
        bool promotional = false,
        string? paymentRef = null,
        decimal? amountOverride = null)
    {
        if (plan == PremiumPlan.None)
            throw new ArgumentException("Choose Priority or PriorityPlus");
        var amount = amountOverride ?? (promotional ? 0 : PremiumSla.PriceUsd(plan));
        return new PremiumVerificationSubscription
        {
            CreatorId = creatorId,
            Plan = plan,
            Status = PremiumSubscriptionStatus.Active,
            StartsAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(Math.Max(1, durationDays)),
            AmountPaid = amount,
            Currency = "USD",
            PaymentReference = paymentRef ?? (promotional ? "PROMO" : "SIM-" + Guid.NewGuid().ToString("N")[..10]),
            IsPromotional = promotional
        };
    }

    public PremiumPlan EffectivePlan(IEnumerable<PremiumVerificationSubscription> subs, DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.UtcNow;
        var active = subs
            .Where(s => s.Status == PremiumSubscriptionStatus.Active && s.ExpiresAt > now)
            .OrderByDescending(s => s.Plan)
            .ThenByDescending(s => s.ExpiresAt)
            .FirstOrDefault();
        return active?.Plan ?? PremiumPlan.None;
    }

    public void ApplyToCampaign(Campaign campaign, PremiumPlan plan)
    {
        campaign.PremiumReview = plan != PremiumPlan.None;
        campaign.ReviewPriority = PremiumSla.QueuePriority(plan);
        if (campaign.Status is CampaignStatus.UnderReview or CampaignStatus.NeedsReview or CampaignStatus.Draft)
        {
            var hours = PremiumSla.TargetReviewHours(plan);
            campaign.ReviewSlaDueAt = DateTime.UtcNow.AddHours(hours);
        }
    }
}
