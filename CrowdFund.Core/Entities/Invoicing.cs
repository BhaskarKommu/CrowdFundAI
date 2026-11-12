using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

public enum InvoiceStatus
{
    Draft = 0,
    Sent = 1,
    Paid = 2,
    PartiallyPaid = 3,
    Cancelled = 4,
    Overdue = 5,
    Refunded = 6
}

public enum PaymentLinkStatus
{
    Active = 0,
    Paid = 1,
    Expired = 2,
    Cancelled = 3
}

public class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CampaignId { get; set; }
    [MaxLength(450)]
    public string? CreatorUserId { get; set; }

    [MaxLength(40)]
    public string InvoiceNumber { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? PayPalInvoiceId { get; set; }

    [MaxLength(256)]
    public string RecipientEmail { get; set; } = string.Empty;
    [MaxLength(120)]
    public string? RecipientName { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountPaid { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";

    [MaxLength(500)]
    public string? Note { get; set; }
    [MaxLength(200)]
    public string? ItemName { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    public DateTime? DueAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? LastReminderAt { get; set; }
    public int ReminderCount { get; set; }

    [MaxLength(500)]
    public string? ShareUrl { get; set; }
    [MaxLength(100)]
    public string? PayPalOrderId { get; set; }
    [MaxLength(100)]
    public string? PayPalCaptureId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<InvoicePayment> Payments { get; set; } = new List<InvoicePayment>();
}

public class InvoicePayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";
    [MaxLength(100)]
    public string? PayPalCaptureId { get; set; }
    [MaxLength(40)]
    public string Method { get; set; } = "PayPal";
    [MaxLength(500)]
    public string? Note { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(450)]
    public string? RecordedBy { get; set; }
}

public class PaymentLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CampaignId { get; set; }
    [MaxLength(450)]
    public string? CreatorUserId { get; set; }

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    [MaxLength(1000)]
    public string? Description { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";

    /// <summary>Public slug for shareable URL /pay/{slug}</summary>
    [MaxLength(40)]
    public string Slug { get; set; } = string.Empty;
    [MaxLength(500)]
    public string? ShareUrl { get; set; }
    [MaxLength(100)]
    public string? PayPalLinkId { get; set; }

    public PaymentLinkStatus Status { get; set; } = PaymentLinkStatus.Active;
    public DateTime? ExpiresAt { get; set; }
    public int MaxUses { get; set; } // 0 = unlimited
    public int UseCount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCollected { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<PaymentLinkTransaction> Transactions { get; set; } = new List<PaymentLinkTransaction>();
}

public class PaymentLinkTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentLinkId { get; set; }
    public PaymentLink? PaymentLink { get; set; }
    public Guid? DonationId { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";
    [MaxLength(100)]
    public string? PayPalOrderId { get; set; }
    [MaxLength(100)]
    public string? PayPalCaptureId { get; set; }
    [MaxLength(30)]
    public string Status { get; set; } = "Completed";
    [MaxLength(256)]
    public string? PayerEmail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
