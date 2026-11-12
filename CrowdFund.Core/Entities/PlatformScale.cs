using System.ComponentModel.DataAnnotations;

namespace CrowdFund.Core.Entities;

/// <summary>Hard multi-tenant boundary for white-label NGO deployments.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(80)]
    public string Slug { get; set; } = string.Empty;
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // White-label branding
    [MaxLength(7)]
    public string PrimaryColor { get; set; } = "#6C5CE7";
    [MaxLength(7)]
    public string AccentColor { get; set; } = "#A29BFE";
    [MaxLength(200)]
    public string? LogoUrl { get; set; }
    [MaxLength(200)]
    public string? SupportEmail { get; set; }
    [MaxLength(200)]
    public string? CustomDomain { get; set; }
    [MaxLength(200)]
    public string? TrustBadgeLabel { get; set; } = "Verified by CrowdFund AI";
    public bool ShowPoweredBy { get; set; } = true;
}

public enum BackgroundJobType
{
    VerificationCouncil = 0,
    WatchdogScan = 1,
    ImpactReport = 2,
    WebhookDelivery = 3,
    VirusScan = 4,
    ReindexEmbeddings = 5
}

public enum BackgroundJobStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    DeadLetter = 4
}

public class BackgroundJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public BackgroundJobType JobType { get; set; }
    public BackgroundJobStatus Status { get; set; } = BackgroundJobStatus.Pending;
    public Guid? TenantId { get; set; }
    public Guid? CampaignId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RunAfter { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class PartnerClient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;
    [Required, MaxLength(64)]
    public string ClientId { get; set; } = string.Empty;
    [Required, MaxLength(128)]
    public string ClientSecretHash { get; set; } = string.Empty;
    [MaxLength(500)]
    public string Scopes { get; set; } = "campaigns:read donations:read"; // space-separated
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PartnerAccessToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartnerClientId { get; set; }
    [MaxLength(128)]
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class OutboundWebhook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? CampaignId { get; set; }
    [MaxLength(500)]
    public string Url { get; set; } = string.Empty;
    [MaxLength(80)]
    public string EventType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public int Attempts { get; set; }
    public bool Delivered { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
