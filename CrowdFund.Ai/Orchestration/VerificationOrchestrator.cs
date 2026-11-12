using System.Text.Json;
using CrowdFund.Ai.Agents;
using CrowdFund.Ai.Fraud;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;

namespace CrowdFund.Ai.Orchestration;

/// <summary>
/// Runs Council + Fraud engine and persists VerificationRun, findings, trust score, flags.
/// </summary>
public class VerificationOrchestrator
{
    private readonly VerificationCouncil _council;
    private readonly FraudEngine _fraud;
    private readonly LedgerService _ledger;

    public VerificationOrchestrator(VerificationCouncil council, FraudEngine fraud, LedgerService ledger)
    {
        _council = council;
        _fraud = fraud;
        _ledger = ledger;
    }

    public async Task<(TrustAssessment Assessment, IReadOnlyList<FraudFlag> Flags, VerificationRun Run)> VerifyAsync(
        Campaign campaign,
        IList<CampaignDocument> documents,
        IList<Expense> expenses,
        IList<Donation> donations,
        IEnumerable<Campaign>? creatorHistory,
        Func<AuditLedgerEntry, Task>? persistLedger = null,
        CancellationToken ct = default)
    {
        var isFraudSeed = campaign.Title.Contains("FRAUD-DEMO", StringComparison.OrdinalIgnoreCase)
                          || campaign.Title.Contains("Planted", StringComparison.OrdinalIgnoreCase);

        var assessment = await _council.RunAsync(campaign, documents, creatorHistory, isFraudSeed, ct);
        var flags = _fraud.EvaluateCampaign(campaign, expenses, donations, documents);

        campaign.TrustScore = assessment.OverallScore;
        campaign.TrustExplanationJson = JsonSerializer.Serialize(assessment);

        if (assessment.RecommendedAction is "ManualReview" or "Reject" || flags.Any(f => f.Severity == "High"))
        {
            if (campaign.Status == CampaignStatus.UnderReview || campaign.Status == CampaignStatus.Draft)
                campaign.Status = CampaignStatus.NeedsReview;
        }

        var run = new VerificationRun
        {
            CampaignId = campaign.Id,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            OverallScore = assessment.OverallScore,
            RecommendationJson = JsonSerializer.Serialize(new
            {
                assessment.RecommendedAction,
                assessment.Band,
                assessment.MissingInformation,
                assessment.Disclaimer
            }),
            Version = "1.0"
        };

        foreach (var f in assessment.Findings)
        {
            run.Findings.Add(new AgentFinding
            {
                AgentName = f.Agent,
                Severity = f.Severity,
                Title = f.Title,
                Detail = f.Detail,
                EvidenceRefsJson = JsonSerializer.Serialize(f.EvidenceRefs),
                Confidence = f.Confidence
            });
        }

        return (assessment, flags, run);
    }

    public AuditLedgerEntry LedgerForTrustChange(Campaign campaign, int newScore, string prevHash)
    {
        return _ledger.CreateEntry(campaign.Id, "trust.score.changed", new
        {
            trustScore = newScore,
            campaignId = campaign.Id
        }, prevHash);
    }
}
