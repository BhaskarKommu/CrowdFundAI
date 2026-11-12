using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

public class VerificationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int OverallScore { get; set; }
    public string? RecommendationJson { get; set; }
    public string Version { get; set; } = "1.0";

    public Campaign? Campaign { get; set; }
    public ICollection<AgentFinding> Findings { get; set; } = new List<AgentFinding>();
}

public class AgentFinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VerificationRunId { get; set; }
    [MaxLength(100)]
    public string AgentName { get; set; } = string.Empty;
    [MaxLength(20)]
    public string Severity { get; set; } = "Info"; // Info, Low, Medium, High
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string? EvidenceRefsJson { get; set; }
    public double Confidence { get; set; }

    public VerificationRun? VerificationRun { get; set; }
}

public class Milestone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")]
    public decimal TargetAmount { get; set; }
    [MaxLength(50)]
    public string Status { get; set; } = "Pending"; // Pending, Approved, Released
    public Guid? ProofDocumentId { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal ReleasedAmount { get; set; }
    public int SortOrder { get; set; }
    [MaxLength(500)]
    public string? Description { get; set; }

    public Campaign? Campaign { get; set; }
}

public class CampaignUpdate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    [MaxLength(20)]
    public string Source { get; set; } = "Creator"; // Creator, AiDraft
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Campaign? Campaign { get; set; }
}

public class FraudFlag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Guid? ExpenseId { get; set; }
    [MaxLength(100)]
    public string Rule { get; set; } = string.Empty;
    [MaxLength(20)]
    public string Severity { get; set; } = "Low";
    public double Score { get; set; }
    public string Explanation { get; set; } = string.Empty;
    public string? EvidenceJson { get; set; }
    [MaxLength(20)]
    public string Status { get; set; } = "Open"; // Open, Dismissed, Confirmed
    public string? ReviewedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLedgerEntry
{
    public long Id { get; set; } // sequential
    public Guid CampaignId { get; set; }
    [MaxLength(50)]
    public string EventType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    [MaxLength(64)]
    public string PrevHash { get; set; } = string.Empty;
    [MaxLength(64)]
    public string Hash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public Guid? CampaignId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChatSessionId { get; set; }
    [MaxLength(20)]
    public string Role { get; set; } = "user"; // user, assistant, system
    public string Content { get; set; } = string.Empty;
    public string? ToolCallsJson { get; set; }
    public string? CitationsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ChatSession? ChatSession { get; set; }
}

public class EmbeddingRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(50)]
    public string OwnerType { get; set; } = string.Empty; // Campaign, Expense, etc.
    public Guid OwnerId { get; set; }
    public byte[] Vector { get; set; } = Array.Empty<byte>();
    [MaxLength(100)]
    public string Model { get; set; } = string.Empty;
}

public class WebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)]
    public string PayPalEventId { get; set; } = string.Empty;
    [MaxLength(120)]
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime? ProcessedAt { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public bool SignatureValid { get; set; }
    /// <summary>Received | Processed | Failed | DeadLetter</summary>
    [MaxLength(20)]
    public string ProcessingStatus { get; set; } = "Received";
    public int AttemptCount { get; set; }
    public DateTime? NextRetryAt { get; set; }
    [MaxLength(1000)]
    public string? LastError { get; set; }
    [MaxLength(100)]
    public string? ResourceId { get; set; }
    [MaxLength(64)]
    public string? RelatedEntityType { get; set; }
    public Guid? RelatedEntityId { get; set; }
}


/// <summary>Singleton-ish runtime payment config (row Id=1). Secrets stay in env/user-secrets.</summary>
public class PaymentRuntimeSettings
{
    public int Id { get; set; } = 1;
    public bool PayPalEnabled { get; set; } = true;
    /// <summary>sandbox | live — display preference; actual Mode still from config/env unless Simulate.</summary>
    [MaxLength(20)]
    public string PreferredMode { get; set; } = "sandbox";
    public bool SimulateOverride { get; set; }
    [MaxLength(500)]
    public string? WebhookUrlOverride { get; set; }
    public bool NotifyOnCapture { get; set; } = true;
    public bool NotifyOnRefund { get; set; } = true;
    public bool NotifyOnSubscription { get; set; } = true;
    public bool NotifyOnPayout { get; set; } = true;
    public bool NotifyOnDispute { get; set; } = true;
    [MaxLength(500)]
    public string? NotificationWebhookUrl { get; set; }
    [MaxLength(256)]
    public string? NotificationEmail { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
