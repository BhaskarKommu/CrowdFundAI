using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

/// <summary>
/// Tamper-evident hash-chained audit ledger. Not a blockchain — a simple hash chain.
/// </summary>
public class LedgerService
{
    public string ComputeHash(string prevHash, string eventType, string payloadJson, DateTime timestamp)
    {
        var canonical = $"{prevHash}|{eventType}|{payloadJson}|{timestamp:O}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public AuditLedgerEntry CreateEntry(Guid campaignId, string eventType, object payload, string prevHash)
    {
        var now = DateTime.UtcNow;
        var payloadJson = JsonSerializer.Serialize(payload);
        var hash = ComputeHash(prevHash, eventType, payloadJson, now);
        return new AuditLedgerEntry
        {
            CampaignId = campaignId,
            EventType = eventType,
            PayloadJson = payloadJson,
            PrevHash = prevHash,
            Hash = hash,
            CreatedAt = now
        };
    }

    public (bool IsValid, long? BrokenAtId) VerifyChain(IEnumerable<AuditLedgerEntry> entries)
    {
        string expectedPrev = "";
        foreach (var entry in entries.OrderBy(e => e.Id))
        {
            if (entry.PrevHash != expectedPrev)
                return (false, entry.Id);

            var recomputed = ComputeHash(entry.PrevHash, entry.EventType, entry.PayloadJson, entry.CreatedAt);
            if (recomputed != entry.Hash)
                return (false, entry.Id);

            expectedPrev = entry.Hash;
        }
        return (true, null);
    }
}
