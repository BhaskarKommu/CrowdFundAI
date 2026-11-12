namespace CrowdFund.Ai.Agents;

public record SubScores(
    int ClaimCompleteness,
    int DocumentAuthenticity,
    int Consistency,
    int BudgetPlausibility,
    int CreatorHistory);

public record AgentFindingDto(
    string Agent,
    string Severity,
    string Title,
    string Detail,
    IReadOnlyList<string> EvidenceRefs,
    double Confidence);

public record TrustAssessment(
    int OverallScore,
    string Band,
    SubScores SubScores,
    IReadOnlyList<AgentFindingDto> Findings,
    IReadOnlyList<string> MissingInformation,
    string RecommendedAction,
    string Disclaimer);

public static class TrustScoreCalculator
{
    /// <summary>Weighted blend computed in C# — LLM supplies sub-scores only.</summary>
    public static int ComputeOverall(SubScores s)
    {
        // Weights: claims 20%, docs 25%, consistency 25%, budget 15%, history 15%
        var score =
            s.ClaimCompleteness * 0.20 +
            s.DocumentAuthenticity * 0.25 +
            s.Consistency * 0.25 +
            s.BudgetPlausibility * 0.15 +
            s.CreatorHistory * 0.15;
        return (int)Math.Clamp(Math.Round(score), 0, 100);
    }

    public static string Band(int overall) => overall switch
    {
        >= 75 => "High",
        >= 50 => "Medium",
        >= 30 => "Low",
        _ => "Insufficient evidence"
    };
}
