using System.ComponentModel.DataAnnotations;

namespace CrowdFund.Core.Entities;

/// <summary>Edge in the creator/campaign risk graph (shared docs, vendors, story, email).</summary>
public class RiskGraphEdge
{
    public long Id { get; set; }
    public Guid FromCampaignId { get; set; }
    public Guid ToCampaignId { get; set; }
    [MaxLength(450)]
    public string? FromCreatorId { get; set; }
    [MaxLength(450)]
    public string? ToCreatorId { get; set; }

    [MaxLength(40)]
    public string RelationType { get; set; } = string.Empty;
    // SharedDocumentHash | SharedVendor | StorySimilarity | SharedEmail | SharedDevice

    [MaxLength(64)]
    public string? SharedKey { get; set; } // hash, vendor key, email domain, device fingerprint

    public double Weight { get; set; } = 1.0;
    [MaxLength(500)]
    public string? Detail { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Creator reply on a fraud/open flag (right of reply).</summary>
public class FlagReply
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FraudFlagId { get; set; }
    public FraudFlag? FraudFlag { get; set; }

    [MaxLength(450)]
    public string AuthorUserId { get; set; } = string.Empty;
    [MaxLength(30)]
    public string AuthorRole { get; set; } = "Creator"; // Creator | Admin | Auditor

    [Required, MaxLength(4000)]
    public string Body { get; set; } = string.Empty;

    /// <summary>Public = visible on campaign flags; DonorsOnly = donors of campaign; Internal = admin/auditor only.</summary>
    [MaxLength(20)]
    public string Visibility { get; set; } = "Public"; // Public | DonorsOnly | Internal

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Published ledger root for external verification (not a blockchain).</summary>
public class TransparencyLogEntry
{
    public long Id { get; set; }
    public Guid CampaignId { get; set; }
    [MaxLength(64)]
    public string LedgerHeadHash { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public bool ChainValid { get; set; }
    [MaxLength(64)]
    public string PrevPublishedHash { get; set; } = string.Empty;
    [MaxLength(64)]
    public string PublishedHash { get; set; } = string.Empty; // hash of this publication record
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Record of a full re-verification run triggered on schedule.</summary>
public class ReverificationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    [MaxLength(40)]
    public string Trigger { get; set; } = "scheduled"; // scheduled | manual | watchdog
    public int? TrustScoreBefore { get; set; }
    public int? TrustScoreAfter { get; set; }
    public int FlagsRaised { get; set; }
    public bool NotificationSent { get; set; }
    [MaxLength(2000)]
    public string? Summary { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
