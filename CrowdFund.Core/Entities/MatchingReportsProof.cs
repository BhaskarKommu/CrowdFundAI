using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

public enum ImpactReportStatus
{
    Draft = 0,
    CreatorEdited = 1,
    Published = 2,
    Sent = 3
}

public enum MultimodalProofStatus
{
    Uploaded = 0,
    TranscriptReady = 1,
    PendingHumanReview = 2,
    Approved = 3,
    Rejected = 4
}

/// <summary>A8-style recommendation audit trail (optional persist).</summary>
public class CampaignMatchLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(450)]
    public string? UserId { get; set; }
    [MaxLength(500)]
    public string Query { get; set; } = string.Empty;
    public string ResultsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A9 Auto impact report draft → creator edit → publish/send.</summary>
public class ImpactReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Campaign? Campaign { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    public string BodyMarkdown { get; set; } = string.Empty;
    public string? CitationsJson { get; set; }
    public string? LedgerHeadHash { get; set; }

    public ImpactReportStatus Status { get; set; } = ImpactReportStatus.Draft;
    [MaxLength(450)]
    public string? EditedBy { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int RecipientsCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Photos / short video proof with optional transcript; human approval required for payout gates.</summary>
public class MultimodalProof
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Guid? ExpenseId { get; set; }
    public Guid? MilestoneId { get; set; }

    [MaxLength(30)]
    public string MediaType { get; set; } = "image"; // image | video | audio
    [MaxLength(260)]
    public string FileName { get; set; } = string.Empty;
    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;
    [MaxLength(500)]
    public string StoredPath { get; set; } = string.Empty;
    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public string? Transcript { get; set; }
    public string? AiSummary { get; set; }
    public double TranscriptConfidence { get; set; }

    public MultimodalProofStatus Status { get; set; } = MultimodalProofStatus.Uploaded;
    [MaxLength(450)]
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    [MaxLength(2000)]
    public string? ReviewNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
