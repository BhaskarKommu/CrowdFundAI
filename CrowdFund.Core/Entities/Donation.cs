using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

public enum DonationStatus
{
    Created = 0,
    /// <summary>Order created; awaiting buyer approval on PayPal.</summary>
    PendingApproval = 1,
    /// <summary>Payment authorized (intent=AUTHORIZE), not yet captured.</summary>
    Authorized = 2,
    /// <summary>Funds captured (full or remaining zero).</summary>
    Captured = 3,
    /// <summary>Partially captured; authorization may still have balance.</summary>
    PartiallyCaptured = 4,
    Pending = 5,
    Cancelled = 6,
    Failed = 7,
    Refunded = 8,
    Expired = 9
}

public class Donation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public string? DonorId { get; set; } // nullable for guest
    [MaxLength(100)]
    public string? PayPalOrderId { get; set; }
    [MaxLength(100)]
    public string? PayPalCaptureId { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";
    public DonationStatus Status { get; set; } = DonationStatus.Created;
    [MaxLength(64)]
    public string? PayerEmailHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CapturedAt { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TipAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal PlatformFee { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal PaymentProcessorFeeEstimate { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetToCampaign { get; set; }
    public bool IsRecurringCharge { get; set; }
    public Guid? RecurringDonationId { get; set; }
    public Guid? MatchingGiftProgramId { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal MatchedAmount { get; set; }

    /// <summary>CAPTURE or AUTHORIZE.</summary>
    [MaxLength(20)]
    public string Intent { get; set; } = "CAPTURE";
    [MaxLength(100)]
    public string? PayPalAuthorizationId { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal AuthorizedAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal CapturedAmount { get; set; }
    [MaxLength(100)]
    public string? IdempotencyKey { get; set; }
    [MaxLength(40)]
    public string? TransactionReference { get; set; }
    [MaxLength(500)]
    public string? StatusDetail { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? FailedAt { get; set; }

    public Campaign? Campaign { get; set; }
    public ApplicationUser? Donor { get; set; }
}
