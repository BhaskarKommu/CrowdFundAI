using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

/// <summary>
/// Builds creator/campaign risk edges: shared document hashes, vendors, story similarity, email.
/// Device fingerprint edges are recorded when a key is supplied by the caller.
/// </summary>
public class RiskGraphService
{
    public IReadOnlyList<RiskGraphEdge> BuildEdges(
        IReadOnlyList<Campaign> campaigns,
        IReadOnlyList<CampaignDocument> documents,
        IReadOnlyList<Expense> expenses,
        IReadOnlyDictionary<string, string>? creatorEmails = null,
        IReadOnlyDictionary<Guid, string>? campaignDeviceKeys = null)
    {
        var edges = new List<RiskGraphEdge>();
        var byId = campaigns.ToDictionary(c => c.Id);

        // Shared document SHA
        var docGroups = documents
            .Where(d => !string.IsNullOrWhiteSpace(d.Sha256))
            .GroupBy(d => d.Sha256!, StringComparer.OrdinalIgnoreCase);
        foreach (var g in docGroups)
        {
            var campIds = g.Select(x => x.CampaignId).Distinct().ToList();
            Pairwise(campIds, (a, b) => edges.Add(Edge(a, b, byId, "SharedDocumentHash", g.Key, 3.0, $"Shared doc hash {g.Key[..Math.Min(12, g.Key.Length)]}…")));
        }

        // Shared vendors (normalized)
        var vendorGroups = expenses
            .Where(e => !string.IsNullOrWhiteSpace(e.Vendor))
            .GroupBy(e => NormalizeVendor(e.Vendor));
        foreach (var g in vendorGroups.Where(x => x.Key.Length >= 3))
        {
            var campIds = g.Select(x => x.CampaignId).Distinct().ToList();
            Pairwise(campIds, (a, b) => edges.Add(Edge(a, b, byId, "SharedVendor", g.Key, 1.5, $"Shared vendor '{g.Key}'")));
        }

        // Story similarity (Jaccard on tokens) for published/review campaigns
        for (var i = 0; i < campaigns.Count; i++)
        {
            for (var j = i + 1; j < campaigns.Count; j++)
            {
                var a = campaigns[i];
                var b = campaigns[j];
                var sim = Jaccard(Tokenize(a.Story + " " + a.Title), Tokenize(b.Story + " " + b.Title));
                if (sim >= 0.35)
                {
                    edges.Add(Edge(a.Id, b.Id, byId, "StorySimilarity", sim.ToString("0.00"), sim * 2.0,
                        $"Story similarity {sim:P0}"));
                }
            }
        }

        // Shared email / domain across creators
        if (creatorEmails != null)
        {
            var byEmail = creatorEmails
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .GroupBy(kv => kv.Value.Trim().ToLowerInvariant());
            foreach (var g in byEmail.Where(x => x.Count() > 1))
            {
                var creatorIds = g.Select(x => x.Key).ToList();
                var relatedCamps = campaigns.Where(c => creatorIds.Contains(c.CreatorId)).Select(c => c.Id).Distinct().ToList();
                Pairwise(relatedCamps, (a, b) =>
                    edges.Add(Edge(a, b, byId, "SharedEmail", g.Key, 4.0, $"Shared account email {MaskEmail(g.Key)}")));
            }

            var byDomain = creatorEmails
                .Where(kv => kv.Value.Contains('@'))
                .GroupBy(kv => kv.Value.Split('@').Last().ToLowerInvariant())
                .Where(g => g.Key is not ("gmail.com" or "yahoo.com" or "outlook.com" or "hotmail.com" or "icloud.com"));
            foreach (var g in byDomain.Where(x => x.Select(v => v.Key).Distinct().Count() > 1))
            {
                var creatorIds = g.Select(x => x.Key).Distinct().ToList();
                var relatedCamps = campaigns.Where(c => creatorIds.Contains(c.CreatorId)).Select(c => c.Id).Distinct().ToList();
                Pairwise(relatedCamps, (a, b) =>
                    edges.Add(Edge(a, b, byId, "SharedEmailDomain", g.Key, 1.2, $"Shared email domain {g.Key}")));
            }
        }

        // Device fingerprint graph (caller supplies opaque keys)
        if (campaignDeviceKeys != null)
        {
            var byDevice = campaignDeviceKeys.GroupBy(kv => kv.Value);
            foreach (var g in byDevice.Where(x => !string.IsNullOrEmpty(x.Key) && x.Count() > 1))
            {
                var campIds = g.Select(x => x.Key).ToList();
                Pairwise(campIds, (a, b) =>
                    edges.Add(Edge(a, b, byId, "SharedDevice", g.Key, 3.5, "Shared device fingerprint")));
            }
        }

        return edges;
    }

    public static string Fingerprint(string? ip, string? userAgent)
    {
        var raw = $"{ip}|{userAgent}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    private static RiskGraphEdge Edge(Guid a, Guid b, Dictionary<Guid, Campaign> byId, string type, string key, double w, string detail)
    {
        byId.TryGetValue(a, out var ca);
        byId.TryGetValue(b, out var cb);
        return new RiskGraphEdge
        {
            FromCampaignId = a,
            ToCampaignId = b,
            FromCreatorId = ca?.CreatorId,
            ToCreatorId = cb?.CreatorId,
            RelationType = type,
            SharedKey = key.Length > 64 ? key[..64] : key,
            Weight = w,
            Detail = detail,
            DetectedAt = DateTime.UtcNow
        };
    }

    private static void Pairwise(List<Guid> ids, Action<Guid, Guid> act)
    {
        for (var i = 0; i < ids.Count; i++)
            for (var j = i + 1; j < ids.Count; j++)
                act(ids[i], ids[j]);
    }

    private static string NormalizeVendor(string v) =>
        Regex.Replace(v.Trim().ToLowerInvariant(), @"[^a-z0-9\s]", "").Trim();

    private static HashSet<string> Tokenize(string text)
    {
        return Regex.Split((text ?? "").ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length > 3)
            .ToHashSet();
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return union == 0 ? 0 : (double)inter / union;
    }

    private static string MaskEmail(string email)
    {
        var parts = email.Split('@');
        if (parts.Length != 2) return "***";
        var u = parts[0];
        var shown = u.Length <= 2 ? u[0] + "*" : u[..2] + "***";
        return shown + "@" + parts[1];
    }
}
