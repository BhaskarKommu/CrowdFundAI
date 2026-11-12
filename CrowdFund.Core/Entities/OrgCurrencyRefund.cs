using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

public enum OrgMemberRole
{
    Owner = 0,
    Admin = 1,
    Editor = 2,
    Finance = 3,
    Viewer = 4
}

public enum RefundStatus
{
    Requested = 0,
    UnderReview = 1,
    Approved = 2,
    Processing = 3,
    PartiallyRefunded = 4,
    FullyRefunded = 5,
    Rejected = 6,
    Frozen = 7,
    Failed = 8,
    Confirmed = 9
}

public enum RefundReasonCode
{
    DonorRequest = 0,
    DuplicatePayment = 1,
    Fraudulent = 2,
    CampaignCancelled = 3,
    ServiceNotProvided = 4,
    Other = 5
}

public enum DisputeStatus
{
    Open = 0,
    Investigating = 1,
    ResolvedDonor = 2,
    ResolvedCreator = 3,
    Closed = 4
}

/// <summary>Organization / team account that can own multiple campaigns.</summary>
public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(3)]
    public string HomeCurrency { get; set; } = "USD";
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<OrganizationMember> Members { get; set; } = new List<OrganizationMember>();
}

public class OrganizationMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;
    public OrgMemberRole Role { get; set; } = OrgMemberRole.Viewer;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Approval step in an org chain (e.g. publish or payout).</summary>
public class ApprovalRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? CampaignId { get; set; }
    [MaxLength(40)]
    public string Action { get; set; } = string.Empty; // PublishCampaign | RequestPayout | Refund
    [MaxLength(450)]
    public string RequestedBy { get; set; } = string.Empty;
    public OrgMemberRole RequiredRole { get; set; } = OrgMemberRole.Admin;
    [MaxLength(20)]
    public string Status { get; set; } = "Pending"; // Pending, Approved, Rejected
    [MaxLength(450)]
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    [MaxLength(2000)]
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RefundRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DonationId { get; set; }
    public Guid CampaignId { get; set; }
    [MaxLength(450)]
    public string? RequestedBy { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OriginalAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal RequestedAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal RefundedAmount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";

    public RefundStatus Status { get; set; } = RefundStatus.Requested;
    [MaxLength(2000)]
    public string? Reason { get; set; }
    public RefundReasonCode ReasonCode { get; set; } = RefundReasonCode.DonorRequest;
    public bool FrozenForFraud { get; set; }
    [MaxLength(450)]
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    [MaxLength(500)]
    public string? FailureReason { get; set; }
    [MaxLength(100)]
    public string? LastPayPalRefundId { get; set; }
    public bool NotifyDonor { get; set; } = true;
    public ICollection<RefundTransaction> Transactions { get; set; } = new List<RefundTransaction>();
}

/// <summary>Immutable history for each partial or full refund attempt.</summary>
public class RefundTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RefundRequestId { get; set; }
    public RefundRequest? RefundRequest { get; set; }
    public Guid DonationId { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";
    [MaxLength(30)]
    public string Status { get; set; } = "Pending";
    [MaxLength(100)]
    public string? PayPalRefundId { get; set; }
    [MaxLength(100)]
    public string? PayPalCaptureId { get; set; }
    [MaxLength(40)]
    public string? ReasonCode { get; set; }
    [MaxLength(2000)]
    public string? Note { get; set; }
    [MaxLength(450)]
    public string? ProcessedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    [MaxLength(500)]
    public string? FailureDetail { get; set; }
    public bool FromWebhook { get; set; }
}

public class DisputeCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? DonationId { get; set; }
    public Guid CampaignId { get; set; }
    [MaxLength(450)]
    public string? OpenedBy { get; set; }
    [MaxLength(40)]
    public string Category { get; set; } = "General"; // Unauthorized, NotAsDescribed, Fraud, Other
    public string Description { get; set; } = string.Empty;
    public DisputeStatus Status { get; set; } = DisputeStatus.Open;
    public Guid? LinkedRefundId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
}

/// <summary>Snapshot FX rate used for display conversion (not a live FX trade).</summary>
public class FxRateSnapshot
{
    public long Id { get; set; }
    [MaxLength(3)]
    public string BaseCurrency { get; set; } = "USD";
    [MaxLength(3)]
    public string QuoteCurrency { get; set; } = "INR";
    [Column(TypeName = "decimal(18,6)")]
    public decimal Rate { get; set; } // 1 base = Rate quote
    public DateTime AsOfUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(100)]
    public string Source { get; set; } = "demo-table";
}
