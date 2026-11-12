using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

public class RefundDisputeService
{
    public static readonly IReadOnlyList<(RefundReasonCode Code, string Label)> ReasonCatalog = new[]
    {
        (RefundReasonCode.DonorRequest, "Donor requested refund"),
        (RefundReasonCode.DuplicatePayment, "Duplicate payment"),
        (RefundReasonCode.Fraudulent, "Fraudulent transaction"),
        (RefundReasonCode.CampaignCancelled, "Campaign cancelled"),
        (RefundReasonCode.ServiceNotProvided, "Service / goods not provided"),
        (RefundReasonCode.Other, "Other")
    };

    public RefundRequest CreatePartialRefund(
        Donation donation,
        decimal requestedAmount,
        string? reason,
        string? requestedBy,
        bool freezeForFraud,
        RefundReasonCode reasonCode = RefundReasonCode.DonorRequest)
    {
        if (donation.Status is not (DonationStatus.Captured or DonationStatus.PartiallyCaptured or DonationStatus.Refunded))
            throw new InvalidOperationException("Only captured donations can be refunded.");

        var already = donation.Status == DonationStatus.Refunded ? donation.Amount : 0m;
        // remaining based on request history is enforced by caller via refunded totals
        if (requestedAmount <= 0)
            throw new ArgumentException("Requested refund must be greater than zero.");
        if (requestedAmount > donation.Amount)
            throw new ArgumentException("Requested refund cannot exceed original amount.");

        return new RefundRequest
        {
            DonationId = donation.Id,
            CampaignId = donation.CampaignId,
            RequestedBy = requestedBy,
            OriginalAmount = donation.Amount,
            RequestedAmount = requestedAmount,
            Currency = donation.Currency,
            Reason = reason ?? ReasonCatalog.First(x => x.Code == reasonCode).Label,
            ReasonCode = reasonCode,
            Status = freezeForFraud ? RefundStatus.Frozen : RefundStatus.Requested,
            FrozenForFraud = freezeForFraud,
            NotifyDonor = true
        };
    }

    public void ApplyApproval(RefundRequest req, decimal refundNow, string reviewer)
    {
        if (req.Status == RefundStatus.Frozen)
            throw new InvalidOperationException("Unfreeze before approving a fraud-held refund.");
        if (refundNow <= 0)
            throw new ArgumentException("Refund amount must be > 0.");
        if (req.RefundedAmount + refundNow > req.RequestedAmount + 0.0001m)
            throw new InvalidOperationException("Refund exceeds requested amount.");

        req.RefundedAmount += refundNow;
        if (req.RefundedAmount >= req.RequestedAmount)
            req.Status = RefundStatus.FullyRefunded;
        else
            req.Status = RefundStatus.PartiallyRefunded;
        req.ReviewedBy = reviewer;
        req.ReviewedAt = DateTime.UtcNow;
    }

    public RefundTransaction BeginTransaction(RefundRequest req, decimal amount, string? processedBy, string? note)
    {
        return new RefundTransaction
        {
            RefundRequestId = req.Id,
            DonationId = req.DonationId,
            Amount = amount,
            Currency = req.Currency,
            Status = "Pending",
            ReasonCode = req.ReasonCode.ToString(),
            Note = note ?? req.Reason,
            ProcessedBy = processedBy
        };
    }

    public void CompleteTransaction(RefundTransaction tx, string paypalRefundId, string status)
    {
        tx.PayPalRefundId = paypalRefundId;
        tx.Status = status is "COMPLETED" or "Confirmed" ? "Completed" : status;
        tx.CompletedAt = DateTime.UtcNow;
    }

    public void FailTransaction(RefundTransaction tx, string detail)
    {
        tx.Status = "Failed";
        tx.FailureDetail = detail;
        tx.CompletedAt = DateTime.UtcNow;
    }
}
