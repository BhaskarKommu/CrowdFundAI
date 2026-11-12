using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

public enum PremiumPlan
{
    None = 0,
    Priority = 1,      // Faster review SLA
    PriorityPlus = 2   // Fastest + dedicated queue note
}

public enum PremiumSubscriptionStatus
{
    Active = 0,
    Expired = 1,
    Cancelled = 2,
    PendingPayment = 3
}

/// <summary>
/// Paid / promotional priority verification for creators.
/// Does not change trust rules — only review SLA and queue ordering.
/// </summary>
public class PremiumVerificationSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(450)]
    public string CreatorId { get; set; } = string.Empty;

    public PremiumPlan Plan { get; set; } = PremiumPlan.Priority;
    public PremiumSubscriptionStatus Status { get; set; } = PremiumSubscriptionStatus.Active;

    public DateTime StartsAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(30);

    [Column(TypeName = "decimal(18,2)")]
    public decimal AmountPaid { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";

    [MaxLength(100)]
    public string? PaymentReference { get; set; } // SIM- or PayPal capture id
    public bool IsPromotional { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class PremiumSla
{
    /// <summary>Target human review window in hours.</summary>
    public static int TargetReviewHours(PremiumPlan plan) => plan switch
    {
        PremiumPlan.PriorityPlus => 4,
        PremiumPlan.Priority => 12,
        _ => 48
    };

    public static int QueuePriority(PremiumPlan plan) => plan switch
    {
        PremiumPlan.PriorityPlus => 100,
        PremiumPlan.Priority => 50,
        _ => 0
    };

    public static decimal PriceUsd(PremiumPlan plan) => plan switch
    {
        PremiumPlan.PriorityPlus => 99m,
        PremiumPlan.Priority => 39m,
        _ => 0m
    };
}
