using System.ComponentModel.DataAnnotations;

namespace CrowdFund.Core.Entities;

/// <summary>
/// Append-only operational audit log for compliance / disputes.
/// Distinct from the campaign money ledger (AuditLedgerEntry).
/// Application code should only INSERT — never UPDATE/DELETE rows.
/// </summary>
public class SecurityAuditEvent
{
    public long Id { get; set; }

    [Required, MaxLength(80)]
    public string Action { get; set; } = string.Empty; // e.g. kyc.approved, payout.completed, login.success

    [MaxLength(450)]
    public string? ActorUserId { get; set; }

    [MaxLength(256)]
    public string? ActorEmail { get; set; }

    [MaxLength(64)]
    public string? ActorRole { get; set; }

    [MaxLength(80)]
    public string? EntityType { get; set; } // Campaign, Donation, KycSubmission, Payout...

    [MaxLength(80)]
    public string? EntityId { get; set; }

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    [MaxLength(400)]
    public string? UserAgent { get; set; }

    [MaxLength(4000)]
    public string? DetailsJson { get; set; }

    [MaxLength(32)]
    public string Result { get; set; } = "ok"; // ok | denied | error

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
