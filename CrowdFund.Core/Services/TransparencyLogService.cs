using System.Security.Cryptography;
using System.Text;
using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

/// <summary>
/// Publishes ledger head hashes to an append-only public transparency log (not a blockchain).
/// External parties can verify campaign ledger roots over time.
/// </summary>
public class TransparencyLogService
{
    public TransparencyLogEntry CreatePublication(
        Guid campaignId,
        string ledgerHeadHash,
        int entryCount,
        bool chainValid,
        string prevPublishedHash)
    {
        var now = DateTime.UtcNow;
        var payload = $"{prevPublishedHash}|{campaignId}|{ledgerHeadHash}|{entryCount}|{chainValid}|{now:O}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return new TransparencyLogEntry
        {
            CampaignId = campaignId,
            LedgerHeadHash = ledgerHeadHash,
            EntryCount = entryCount,
            ChainValid = chainValid,
            PrevPublishedHash = prevPublishedHash ?? "",
            PublishedHash = hash,
            PublishedAt = now
        };
    }

    public (bool valid, long? brokenAt) VerifyLogChain(IEnumerable<TransparencyLogEntry> entries)
    {
        string expectedPrev = "";
        foreach (var e in entries.OrderBy(x => x.Id))
        {
            if ((e.PrevPublishedHash ?? "") != expectedPrev)
                return (false, e.Id);
            var payload = $"{e.PrevPublishedHash}|{e.CampaignId}|{e.LedgerHeadHash}|{e.EntryCount}|{e.ChainValid}|{e.PublishedAt:O}";
            var recomputed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            if (!string.Equals(recomputed, e.PublishedHash, StringComparison.OrdinalIgnoreCase))
                return (false, e.Id);
            expectedPrev = e.PublishedHash;
        }
        return (true, null);
    }
}
