using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

public class KycService
{
    public CreatorProfile EnsureProfile(string userId, string? displayName = null)
    {
        return new CreatorProfile
        {
            UserId = userId,
            LegalName = displayName ?? "",
            KycTier = KycTier.Unverified
        };
    }

    public bool CanPublishCampaign(CreatorProfile profile, decimal goalAmount) =>
        goalAmount <= PlatformLimits.MaxGoalAmount(profile.KycTier)
        && profile.KycTier != KycTier.Rejected
        && profile.LastAmlStatus != AmlScreenStatus.Match;

    public bool CanRequestPayout(CreatorProfile profile, decimal amount) =>
        PlatformLimits.CanRequestPayout(profile.KycTier)
        && amount > 0
        && amount <= PlatformLimits.MaxSinglePayout(profile.KycTier)
        && profile.LastAmlStatus != AmlScreenStatus.Match;

    public (decimal fee, decimal net) CalculatePayoutFee(CreatorProfile profile, decimal gross)
    {
        var rate = PlatformLimits.PayoutFeeRate(profile.KycTier);
        var fee = Math.Round(gross * rate, 2, MidpointRounding.AwayFromZero);
        var net = Math.Round(gross - fee, 2, MidpointRounding.AwayFromZero);
        return (fee, net);
    }

    public void ApplyApproval(CreatorProfile profile, KycSubmission submission, KycTier grantedTier, string reviewerId)
    {
        submission.Status = KycSubmissionStatus.Approved;
        submission.ReviewedAt = DateTime.UtcNow;
        submission.ReviewedBy = reviewerId;
        profile.KycTier = grantedTier;
        profile.KycVerifiedAt = DateTime.UtcNow;
        profile.KycReviewedBy = reviewerId;
        profile.UpdatedAt = DateTime.UtcNow;
    }

    public void ApplyRejection(CreatorProfile profile, KycSubmission submission, string reviewerId, string note)
    {
        submission.Status = KycSubmissionStatus.Rejected;
        submission.NotesFromReviewer = note;
        submission.ReviewedAt = DateTime.UtcNow;
        submission.ReviewedBy = reviewerId;
        profile.KycTier = KycTier.Rejected;
        profile.UpdatedAt = DateTime.UtcNow;
    }
}
