using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

public enum MilestoneReleaseStatus
{
    PendingProof = 0,
    ProofSubmitted = 1,
    UnderReview = 2,
    Approved = 3,
    Released = 4,
    Rejected = 5
}

public enum SubscriptionStatus
{
    ApprovalPending = 0,
    Active = 1,
    Suspended = 2,
    Cancelled = 3,
    PastDue = 4,
    Expired = 5,
    Trial = 6
}

public enum MatchCampaignStatus
{
    Draft = 0,
    Active = 1,
    Exhausted = 2,
    Ended = 3
}

/// <summary>
/// Platform-held tranche tracker. Not regulated escrow — funds remain with payment processor
/// until creator payout; this models release gates for transparency.
/// </summary>
public class MilestoneRelease
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MilestoneId { get; set; }
    public Milestone? Milestone { get; set; }
    public Guid CampaignId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";

    public MilestoneReleaseStatus Status { get; set; } = MilestoneReleaseStatus.PendingProof;
    public Guid? MultimodalProofId { get; set; }
    public Guid? ExpenseId { get; set; }

    [MaxLength(450)]
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    [MaxLength(2000)]
    public string? ReviewNote { get; set; }

    public Guid? LinkedPayoutId { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RecurringDonation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Campaign? Campaign { get; set; }

    [MaxLength(450)]
    public string? DonorId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";

    /// <summary>monthly | weekly</summary>
    [MaxLength(20)]
    public string Interval { get; set; } = "monthly";

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public DateTime NextChargeAt { get; set; } = DateTime.UtcNow.AddMonths(1);
    public DateTime? LastChargedAt { get; set; }
    public int SuccessfulCharges { get; set; }

    [MaxLength(100)]
    public string? PayPalSubscriptionId { get; set; }
    [MaxLength(100)]
    public string? PayPalPlanId { get; set; }
    [MaxLength(100)]
    public string? PayPalProductId { get; set; }
    public Guid? SubscriptionPlanId { get; set; }
    public SubscriptionPlan? Plan { get; set; }

    public int FailedPaymentCount { get; set; }
    public int MaxPaymentRetries { get; set; } = 3;
    public DateTime? LastFailedPaymentAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    [MaxLength(500)]
    public string? StatusDetail { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal SetupFeePaid { get; set; }

    public bool SendImpactUpdates { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<SubscriptionBillingEvent> BillingEvents { get; set; } = new List<SubscriptionBillingEvent>();
}

/// <summary>Reusable PayPal-style billing plan (local catalog + remote plan id).</summary>
public class SubscriptionPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CampaignId { get; set; }
    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(500)]
    public string? Description { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";
    /// <summary>WEEK | MONTH | YEAR</summary>
    [MaxLength(10)]
    public string IntervalUnit { get; set; } = "MONTH";
    public int IntervalCount { get; set; } = 1;
    public int TotalCycles { get; set; } // 0 = infinite
    public int TrialDays { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal SetupFee { get; set; }
    public int PaymentFailureThreshold { get; set; } = 3;
    public bool AutoBillOutstanding { get; set; } = true;
    [MaxLength(100)]
    public string? PayPalProductId { get; set; }
    [MaxLength(100)]
    public string? PayPalPlanId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SubscriptionBillingEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecurringDonationId { get; set; }
    public RecurringDonation? RecurringDonation { get; set; }
    [MaxLength(40)]
    public string EventType { get; set; } = string.Empty; // charged, failed, retry, activated, suspended, cancelled, renewed, upgraded, downgraded, expired
    [Column(TypeName = "decimal(18,2)")]
    public decimal? Amount { get; set; }
    [MaxLength(3)]
    public string? Currency { get; set; }
    [MaxLength(100)]
    public string? PayPalTransactionId { get; set; }
    [MaxLength(30)]
    public string Status { get; set; } = "ok";
    [MaxLength(500)]
    public string? Detail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Sponsored matching pool — corporate/NGO sponsor multiplies eligible donations until pool runs out.</summary>
public class MatchingGiftProgram
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Null = platform-wide pool (eligible campaigns by category filter).</summary>
    public Guid? CampaignId { get; set; }
    public Campaign? Campaign { get; set; }
    public Guid? TenantId { get; set; }

    [MaxLength(200)]
    public string SponsorName { get; set; } = string.Empty;
    [MaxLength(500)]
    public string? SponsorLogoUrl { get; set; }
    [MaxLength(500)]
    public string? SponsorWebsite { get; set; }
    [MaxLength(300)]
    public string? Headline { get; set; } // e.g. "Double your impact — matched by Acme"
    public string? Description { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MatchRatio { get; set; } = 1m; // 1 = 1:1
    [Column(TypeName = "decimal(18,2)")]
    public decimal PoolTotal { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal PoolRemaining { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal MaxPerDonation { get; set; } = 500m;
    [Column(TypeName = "decimal(18,2)")]
    public decimal MinDonation { get; set; } = 1m;

    /// <summary>Comma-separated categories for platform-wide pools (empty = all).</summary>
    [MaxLength(200)]
    public string? EligibleCategories { get; set; }
    public bool FirstTimeDonorsOnly { get; set; }
    public bool ShowSponsorOnDonate { get; set; } = true;

    public MatchCampaignStatus Status { get; set; } = MatchCampaignStatus.Active;
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public int MatchCount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalMatched { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class MatchingGiftLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MatchingGiftProgramId { get; set; }
    public Guid DonationId { get; set; }
    public Guid CampaignId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DonorAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal MatchedAmount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
