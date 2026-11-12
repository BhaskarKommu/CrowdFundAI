using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrowdFund.Core.Entities;

/// <summary>Historical snapshot of transparency score for live charts / "what changed".</summary>
public class TransparencyScoreSnapshot
{
    public long Id { get; set; }
    public Guid CampaignId { get; set; }

    public int Score { get; set; }
    public int PreviousScore { get; set; }
    public int Delta { get; set; }

    public int ExpensesVerifiedPct { get; set; }
    public int EvidenceCompleteness { get; set; }
    public int UpdateFreshness { get; set; }
    public int OpenFlagsPenalty { get; set; }
    public int LedgerOkBonus { get; set; }

    [MaxLength(500)]
    public string? ChangeSummary { get; set; }

    [MaxLength(80)]
    public string Source { get; set; } = "watchdog"; // watchdog | manual | api

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ReceiptChallengeVerdict
{
    Pending = 0,
    Matches = 1,
    DoesNotMatch = 2,
    Unsure = 3
}

/// <summary>Donor feedback on whether an expense receipt matches the claim.</summary>
public class ReceiptChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExpenseId { get; set; }
    public Expense? Expense { get; set; }
    public Guid CampaignId { get; set; }

    [MaxLength(450)]
    public string? DonorUserId { get; set; }

    public ReceiptChallengeVerdict Verdict { get; set; } = ReceiptChallengeVerdict.Pending;

    [MaxLength(2000)]
    public string? Comment { get; set; }

    public bool FlaggedForReview { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
