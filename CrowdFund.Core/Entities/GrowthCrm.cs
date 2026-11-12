using System.ComponentModel.DataAnnotations;

namespace CrowdFund.Core.Entities;

public class DonorPreference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;
    public bool OptInCampaignUpdates { get; set; } = true;
    public bool OptInCreatorMessages { get; set; } = false;
    public bool OptInPush { get; set; } = false;
    [MaxLength(500)]
    public string? PushEndpoint { get; set; }
    [MaxLength(200)]
    public string? PushP256dh { get; set; }
    [MaxLength(200)]
    public string? PushAuth { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CreatorMessageBlast
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    [MaxLength(450)]
    public string CreatorId { get; set; } = string.Empty;
    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public int RecipientCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ShortLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(16)]
    public string Code { get; set; } = string.Empty;
    [MaxLength(500)]
    public string TargetPath { get; set; } = string.Empty;
    public Guid? CampaignId { get; set; }
    public int Hits { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ImpactCertificate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(450)]
    public string DonorId { get; set; } = string.Empty;
    public Guid? CampaignId { get; set; }
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";
    public decimal LifetimeGiven { get; set; }
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
}

public class PushNotificationLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(450)]
    public string? UserId { get; set; }
    [MaxLength(80)]
    public string EventType { get; set; } = string.Empty; // ExpenseVerified | GoalReached
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    [MaxLength(500)]
    public string Body { get; set; } = string.Empty;
    public Guid? CampaignId { get; set; }
    public bool Delivered { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
