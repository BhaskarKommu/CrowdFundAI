using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

public enum CampaignStatus
{
    Draft,
    UnderReview,
    NeedsReview,
    Published,
    Closed,
    Rejected,
    Frozen
}

public class Campaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required]
    public string CreatorId { get; set; } = string.Empty;
    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    [Required]
    public string Story { get; set; } = string.Empty;
    [Required, MaxLength(50)]
    public string Category { get; set; } = string.Empty; // Medical, Education, Disaster Relief, Other
    [Column(TypeName = "decimal(18,2)")]
    public decimal GoalAmount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public int TrustScore { get; set; }
    public string? TrustExplanationJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
    public string? CoverImagePath { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal RaisedAmount { get; set; }
    public int DonorCount { get; set; }
    public int TransparencyScore { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? TenantId { get; set; }
    /// <summary>Higher = reviewed sooner (premium).</summary>
    public int ReviewPriority { get; set; }
    public DateTime? ReviewSlaDueAt { get; set; }
    public bool PremiumReview { get; set; }
    /// <summary>Partner platform's campaign id for white-label trust API.</summary>
    [MaxLength(120)]
    public string? ExternalRef { get; set; }
    [MaxLength(200)]
    public string? ExternalPlatform { get; set; }
    public bool IsExternalTrustOnly { get; set; }
    [MaxLength(50)]
    public string? TemplateKey { get; set; }
    public bool FundsFrozen { get; set; }
    [MaxLength(500)]
    public string? FreezeReason { get; set; }

    // Geographic SEO / discovery
    [MaxLength(100)]
    public string? LocationCity { get; set; }
    [MaxLength(100)]
    public string? LocationRegion { get; set; }   // state / province
    [MaxLength(2)]
    public string? LocationCountry { get; set; }  // ISO 3166-1 alpha-2
    [MaxLength(200)]
    public string? LocationLabel { get; set; }    // "Mumbai, MH, IN"
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Approx radius in km for "near" discovery (optional).</summary>
    public int? ServiceRadiusKm { get; set; }

    // Navigation
    public ApplicationUser? Creator { get; set; }
    public ICollection<CampaignDocument> Documents { get; set; } = new List<CampaignDocument>();
    public ICollection<Donation> Donations { get; set; } = new List<Donation>();
    public ICollection<Expense> Expenses { get; set; } = new List<Expense>();
    public ICollection<Milestone> Milestones { get; set; } = new List<Milestone>();
    public ICollection<CampaignUpdate> Updates { get; set; } = new List<CampaignUpdate>();
    public ICollection<VerificationRun> VerificationRuns { get; set; } = new List<VerificationRun>();
}
