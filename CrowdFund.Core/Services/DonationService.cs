using CrowdFund.Core.Entities;
using CrowdFund.Core.Interfaces;

namespace CrowdFund.Core.Services;

/// <summary>
/// Domain service for donations. All money math is deterministic C# — never LLM.
/// </summary>
public class DonationService
{
    private readonly LedgerService _ledger;

    public DonationService(LedgerService ledger) => _ledger = ledger;

    public Donation CreatePending(Guid campaignId, string? donorId, decimal amount, string currency)
    {
        if (amount <= 0 || amount > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be between 0.01 and 1,000,000");

        return new Donation
        {
            CampaignId = campaignId,
            DonorId = donorId,
            Amount = Math.Round(amount, 2),
            Currency = currency.ToUpperInvariant(),
            Status = DonationStatus.Created,
            CreatedAt = DateTime.UtcNow
        };
    }

    public AuditLedgerEntry MarkCaptured(Donation donation, string captureId, string? payerEmailHash, string prevHash)
    {
        donation.PayPalCaptureId = captureId;
        donation.Status = DonationStatus.Captured;
        donation.CapturedAt = DateTime.UtcNow;
        donation.PayerEmailHash = payerEmailHash;

        var payload = new
        {
            donationId = donation.Id,
            campaignId = donation.CampaignId,
            amount = donation.Amount,
            currency = donation.Currency,
            captureId,
            capturedAt = donation.CapturedAt
        };
        return _ledger.CreateEntry(donation.CampaignId, "donation.captured", payload, prevHash);
    }
}
