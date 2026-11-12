using System.Text.Json;
using System.Text.RegularExpressions;
using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

/// <summary>
/// Basic AML / sanctions screening. Demo uses a local high-risk name list.
/// Swap Provider for a real sanctions API (OFAC, UN, commercial) in production.
/// </summary>
public class AmlScreeningService
{
    // Demo blocklist — illustrative only. Production must use official lists + licensed provider.
    private static readonly string[] DemoHighRiskNames =
    {
        "sanctioned person demo",
        "blocked entity demo",
        "osama bin example",
        "north korea trading demo",
        "terror front demo"
    };

    public AmlScreening Screen(
        string subjectName,
        string? country,
        string context,
        string? userId = null,
        Guid? campaignId = null,
        Guid? donationId = null,
        Guid? payoutId = null)
    {
        var normalized = Normalize(subjectName);
        var hits = DemoHighRiskNames
            .Where(n => normalized.Contains(Normalize(n)) || Normalize(n).Contains(normalized))
            .Where(n => normalized.Length >= 4)
            .ToList();

        // Also flag empty / tiny names as Review
        var status = AmlScreenStatus.Clear;
        if (string.IsNullOrWhiteSpace(subjectName) || subjectName.Trim().Length < 3)
            status = AmlScreenStatus.Review;
        else if (hits.Count > 0)
            status = AmlScreenStatus.Match;

        return new AmlScreening
        {
            UserId = userId,
            CampaignId = campaignId,
            DonationId = donationId,
            PayoutId = payoutId,
            SubjectName = subjectName.Trim(),
            SubjectCountry = country,
            Context = context,
            Status = status,
            Provider = "LocalDemoList",
            MatchedTerms = hits.Count > 0 ? string.Join("; ", hits) : null,
            RawResponseJson = JsonSerializer.Serialize(new
            {
                provider = "LocalDemoList",
                normalized,
                hits,
                note = "Replace with OFAC/UN/commercial API before production go-live."
            }),
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string Normalize(string s) =>
        Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9\s]", " ").Trim();
}
