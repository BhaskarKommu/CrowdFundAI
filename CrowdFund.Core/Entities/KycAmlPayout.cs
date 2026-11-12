using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

/// <summary>Creator verification tier — drives fundraising and payout limits.</summary>
public enum KycTier
{
    Unverified = 0,
    Pending = 1,
    Basic = 2,      // ID verified
    Enhanced = 3,   // ID + business/nonprofit docs
    Rejected = 9
}

public enum KycDocumentType
{
    GovernmentId = 0,
    Selfie = 1,
    AddressProof = 2,
    BusinessRegistration = 3,
    TaxExemption80G = 4,
    TaxExemption501c3 = 5,
    BankStatement = 6,
    Other = 99
}

public enum KycSubmissionStatus
{
    Draft = 0,
    Submitted = 1,
    UnderReview = 2,
    Approved = 3,
    Rejected = 4,
    NeedsMoreInfo = 5
}

public enum AmlScreenStatus
{
    Clear = 0,
    Review = 1,
    Match = 2,   // potential hit — human must resolve
    Error = 9
}

public enum PayoutStatus
{
    Draft = 0,
    Requested = 1,
    UnderReview = 2,
    Approved = 3,
    Processing = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7,
    Blocked = 8
}

/// <summary>Extended creator / org profile for KYC, tax, and payouts.</summary>
public class CreatorProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string LegalName { get; set; } = string.Empty;
    [MaxLength(200)]
    public string? OrganizationName { get; set; }
    [MaxLength(50)]
    public string Country { get; set; } = "IN";
    [MaxLength(100)]
    public string? State { get; set; }
    [MaxLength(500)]
    public string? Address { get; set; }

    public KycTier KycTier { get; set; } = KycTier.Unverified;
    public DateTime? KycVerifiedAt { get; set; }
    [MaxLength(450)]
    public string? KycReviewedBy { get; set; }

    // Tax / nonprofit
    public bool IsNonprofit { get; set; }
    [MaxLength(50)]
    public string? TaxId { get; set; }              // PAN / EIN
    [MaxLength(50)]
    public string? TaxExemptionNumber { get; set; }  // 80G / 501(c)(3)
    [MaxLength(30)]
    public string? TaxExemptionType { get; set; }    // "80G" | "501c3" | null
    public DateTime? TaxExemptionValidUntil { get; set; }

    // Payout destination (demo — encrypt at rest in production)
    [MaxLength(200)]
    public string? PayPalPayoutEmail { get; set; }
    [MaxLength(50)]
    public string? BankAccountLast4 { get; set; }
    [MaxLength(100)]
    public string? BankName { get; set; }
    [MaxLength(50)]
    public string? BankIfscOrRouting { get; set; }

    public AmlScreenStatus LastAmlStatus { get; set; } = AmlScreenStatus.Clear;
    public DateTime? LastAmlCheckedAt { get; set; }
    [MaxLength(2000)]
    public string? LastAmlNotes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<KycSubmission> Submissions { get; set; } = new List<KycSubmission>();
}

public class KycSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CreatorProfileId { get; set; }
    public CreatorProfile? CreatorProfile { get; set; }

    public KycSubmissionStatus Status { get; set; } = KycSubmissionStatus.Draft;
    public KycTier RequestedTier { get; set; } = KycTier.Basic;
    [MaxLength(2000)]
    public string? NotesFromCreator { get; set; }
    [MaxLength(2000)]
    public string? NotesFromReviewer { get; set; }
    [MaxLength(450)]
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<KycDocument> Documents { get; set; } = new List<KycDocument>();
}

public class KycDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid KycSubmissionId { get; set; }
    public KycSubmission? Submission { get; set; }

    public KycDocumentType DocumentType { get; set; }
    [Required, MaxLength(260)]
    public string FileName { get; set; } = string.Empty;
    [Required, MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;
    [Required, MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;
    [Required, MaxLength(500)]
    public string StoredPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Result of an AML / sanctions screen (creator or large donor).</summary>
public class AmlScreening
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(450)]
    public string? UserId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? DonationId { get; set; }
    public Guid? PayoutId { get; set; }

    [Required, MaxLength(200)]
    public string SubjectName { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? SubjectCountry { get; set; }
    [MaxLength(50)]
    public string Context { get; set; } = "creator"; // creator | donor | payout

    public AmlScreenStatus Status { get; set; } = AmlScreenStatus.Clear;
    [MaxLength(100)]
    public string Provider { get; set; } = "LocalDemoList";
    [MaxLength(4000)]
    public string? RawResponseJson { get; set; }
    [MaxLength(2000)]
    public string? MatchedTerms { get; set; }
    [MaxLength(450)]
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Creator withdrawal / payout request.</summary>
public class Payout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Campaign? Campaign { get; set; }
    [Required, MaxLength(450)]
    public string CreatorUserId { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal GrossAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal PlatformFee { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetAmount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";

    public PayoutStatus Status { get; set; } = PayoutStatus.Requested;
    [MaxLength(30)]
    public string Method { get; set; } = "PayPal"; // PayPal | Bank
    [MaxLength(200)]
    public string? DestinationSummary { get; set; } // email or ****1234

    [MaxLength(100)]
    public string? ExternalPayoutId { get; set; }
    [MaxLength(2000)]
    public string? FailureReason { get; set; }
    [MaxLength(2000)]
    public string? AdminNote { get; set; }
    [MaxLength(450)]
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AmlScreenStatus AmlStatus { get; set; } = AmlScreenStatus.Clear;
}

/// <summary>Platform fee and limit schedule driven by KYC tier.</summary>
public static class PlatformLimits
{
    // Max open fundraising goal total (rough) and max single payout by tier
    public static decimal MaxGoalAmount(KycTier tier) => tier switch
    {
        KycTier.Enhanced => 500_000m,
        KycTier.Basic => 25_000m,
        KycTier.Pending => 5_000m,
        _ => 1_000m // Unverified / Rejected
    };

    public static decimal MaxSinglePayout(KycTier tier) => tier switch
    {
        KycTier.Enhanced => 100_000m,
        KycTier.Basic => 5_000m,
        KycTier.Pending => 0m,
        _ => 0m
    };

    public static bool CanRequestPayout(KycTier tier) =>
        tier is KycTier.Basic or KycTier.Enhanced;

    /// <summary>Platform fee rate on payouts (not on donations unless you choose to).</summary>
    public static decimal PayoutFeeRate(KycTier tier) => tier switch
    {
        KycTier.Enhanced => 0.03m, // 3%
        KycTier.Basic => 0.05m,
        _ => 0.08m
    };

    public static decimal LargeDonationAmlThreshold => 1_000m;
}

/// <summary>Tax metadata snapshot stored with donation for receipts.</summary>
public class DonationTaxInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DonationId { get; set; }
    public Donation? Donation { get; set; }

    public bool IsEligibleForTaxReceipt { get; set; }
    [MaxLength(30)]
    public string? ExemptionType { get; set; }      // 80G | 501c3
    [MaxLength(50)]
    public string? ExemptionNumber { get; set; }
    [MaxLength(50)]
    public string? OrgTaxId { get; set; }
    [MaxLength(200)]
    public string? OrgLegalName { get; set; }
    [MaxLength(200)]
    public string? DonorLegalName { get; set; }
    [MaxLength(500)]
    public string? DonorAddress { get; set; }
    [MaxLength(50)]
    public string? DonorTaxId { get; set; }         // optional PAN for 80G
    [MaxLength(100)]
    public string ReceiptNumber { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
}
