using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

public enum ExpenseStatus
{
    Submitted,
    AiExtracted,
    NeedsReview,
    Verified,
    Rejected
}

public class Expense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public DateTime Date { get; set; }
    [MaxLength(200)]
    public string Vendor { get; set; } = string.Empty;
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; } = "USD";
    [MaxLength(50)]
    public string Category { get; set; } = string.Empty; // treatment, medicines, tests, travel, other
    public ExpenseStatus Status { get; set; } = ExpenseStatus.Submitted;
    public double ExtractionConfidence { get; set; }
    public string? ExtractionJson { get; set; }
    [MaxLength(450)]
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Campaign? Campaign { get; set; }
    public ICollection<ExpenseEvidence> Evidences { get; set; } = new List<ExpenseEvidence>();
    public ICollection<ExpenseLineItem> LineItems { get; set; } = new List<ExpenseLineItem>();
}

public class ExpenseEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExpenseId { get; set; }
    [Required, MaxLength(255)]
    public string FileName { get; set; } = string.Empty;
    [Required]
    public string StoredPath { get; set; } = string.Empty;
    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;
    [MaxLength(64)]
    public string? PerceptualHash { get; set; }
    public string? ExtractedJson { get; set; }
    public string? ForensicsJson { get; set; }

    public Expense? Expense { get; set; }
}

public class ExpenseLineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExpenseId { get; set; }
    [MaxLength(300)]
    public string Description { get; set; } = string.Empty;
    public int Qty { get; set; } = 1;
    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice { get; set; }
    [MaxLength(50)]
    public string Category { get; set; } = string.Empty;

    public Expense? Expense { get; set; }
}
