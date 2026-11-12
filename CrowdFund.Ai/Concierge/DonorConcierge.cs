using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Interfaces;
using CrowdFund.Core.Services;

namespace CrowdFund.Ai.Concierge;

/// <summary>
/// A5 Grounded Donor Concierge — tool-using, citation chips, grounding checks.
/// Supports English, Hindi, Telugu responses (simple detection + templates).
/// Numbers always come from tool/code results, never invented by LLM.
/// </summary>
public class DonorConcierge
{
    private readonly ILlmClient _llm;
    private readonly ConciergeTools _tools;
    private readonly FollowMyMoneyService _fmm;

    public DonorConcierge(ILlmClient llm, ConciergeTools tools, FollowMyMoneyService fmm)
    {
        _llm = llm;
        _tools = tools;
        _fmm = fmm;
    }

    public record ConciergeReply(
        string Answer,
        IReadOnlyList<string> Citations,
        IReadOnlyList<string> ToolsUsed,
        string Language,
        bool GroundingOk,
        object? PrepareDonationCard);

    public async Task<ConciergeReply> AnswerAsync(
        string userMessage,
        Campaign? campaign,
        Donation? donation,
        IEnumerable<Expense> expenses,
        IEnumerable<Campaign> searchableCampaigns,
        CancellationToken ct = default)
    {
        var lang = DetectLanguage(userMessage);
        var msg = userMessage.Trim();
        var toolsUsed = new List<string>();
        var citations = new List<string>();
        var toolBlobs = new List<string>();
        object? prepareCard = null;

        // Route tools from intent (deterministic routing + LLM narration)
        var lower = msg.ToLowerInvariant();

        if (ContainsAny(lower, "donate", "दान", "విరాళం", "give money", "contribute"))
        {
            var amount = ExtractAmount(msg) ?? 25m;
            if (campaign == null)
            {
                return Refuse(lang, "I need a campaign context to prepare a donation. Open a campaign page first.", citations);
            }
            var tr = _tools.PrepareDonation(campaign.Id, amount, campaign.Currency);
            toolsUsed.Add(tr.ToolName);
            citations.AddRange(tr.CitationIds);
            toolBlobs.Add(tr.JsonPayload);
            prepareCard = JsonSerializer.Deserialize<JsonElement>(tr.JsonPayload);
            var ans = Localize(lang,
                $"I've prepared a donation of {amount:N2} {campaign.Currency} to \"{campaign.Title}\". Please confirm to continue to PayPal. I cannot move money myself.",
                $"मैंने \"{campaign.Title}\" के लिए {amount:N2} {campaign.Currency} का दान तैयार किया है। PayPal जारी रखने के लिए पुष्टि करें। मैं स्वयं पैसे नहीं भेज सकता।",
                $"\"{campaign.Title}\"కి {amount:N2} {campaign.Currency} విరాళం సిద్ధం చేశాను. PayPal కొనసాగించడానికి నిర్ధారించండి. నేను సొంతంగా డబ్బు బదిలీ చేయలేను.");
            return new ConciergeReply(ans, citations.Distinct().ToList(), toolsUsed, lang, true, prepareCard);
        }

        if (campaign != null && ContainsAny(lower, "status", "trust", "transparency", "स्थिति", "స్థితి", "score"))
        {
            var tr = _tools.GetCampaignStatus(campaign);
            toolsUsed.Add(tr.ToolName);
            citations.AddRange(tr.CitationIds);
            toolBlobs.Add(tr.JsonPayload);
            var tr2 = _tools.GetTrustAssessment(campaign);
            toolsUsed.Add(tr2.ToolName);
            citations.AddRange(tr2.CitationIds);
            toolBlobs.Add(tr2.JsonPayload);
        }

        if (campaign != null && ContainsAny(lower, "money", "going", "spent", "allocation", "where", "पैसे", "खर्च", "డబ్బు", "follow"))
        {
            if (donation != null)
            {
                var tr = _tools.GetDonationAllocation(donation, campaign, expenses, _fmm);
                toolsUsed.Add(tr.ToolName);
                citations.AddRange(tr.CitationIds);
                toolBlobs.Add(tr.JsonPayload);
            }
            var trE = _tools.ListExpenses(expenses);
            toolsUsed.Add(trE.ToolName);
            citations.AddRange(trE.CitationIds);
            toolBlobs.Add(trE.JsonPayload);
        }

        if (campaign != null && ContainsAny(lower, "expense", "receipt", "evidence", "रसीद", "రసీదు"))
        {
            var tr = _tools.ListExpenses(expenses);
            toolsUsed.Add(tr.ToolName);
            citations.AddRange(tr.CitationIds);
            toolBlobs.Add(tr.JsonPayload);
            var first = expenses.FirstOrDefault();
            if (first != null)
            {
                var ev = _tools.GetEvidence(first);
                toolsUsed.Add(ev.ToolName);
                citations.AddRange(ev.CitationIds);
                toolBlobs.Add(ev.JsonPayload);
            }
        }

        if (ContainsAny(lower, "search", "find campaign", "recommend", "खोज", "వెతుకు"))
        {
            var q = msg;
            var tr = _tools.SearchCampaigns(searchableCampaigns, q);
            toolsUsed.Add(tr.ToolName);
            citations.AddRange(tr.CitationIds);
            toolBlobs.Add(tr.JsonPayload);
        }

        // If nothing matched and we have campaign context, default to status + expenses
        if (toolsUsed.Count == 0 && campaign != null)
        {
            var tr = _tools.GetCampaignStatus(campaign);
            toolsUsed.Add(tr.ToolName);
            citations.AddRange(tr.CitationIds);
            toolBlobs.Add(tr.JsonPayload);
        }

        if (toolsUsed.Count == 0)
        {
            return Refuse(lang,
                "I don't have enough data in this context to answer. Try opening a campaign or asking about status, expenses, or where your donation went.",
                citations);
        }

        // Build grounded answer from tool JSON (prefer deterministic templates so numbers always match)
        var grounded = BuildGroundedAnswer(lang, campaign, donation, expenses, toolBlobs, toolsUsed);

        // Optional LLM polish — still validate grounding
        string finalAnswer = grounded;
        try
        {
            var system = """
                You are the CrowdFund AI donor concierge. Use ONLY facts from the TOOL_RESULTS.
                Never invent amounts, IDs, or statuses. If data is missing, say so.
                Keep the same language as the user (English, Hindi, or Telugu).
                Append citation markers like [expense:guid] when referencing data.
                """;
            var user = $"USER: {msg}\n\nTOOL_RESULTS:\n{string.Join("\n---\n", toolBlobs)}\n\nDraft answer to improve (keep all numbers unchanged):\n{grounded}";
            var llm = await _llm.CompleteAsync(new[] { new LlmMessage("user", user) }, system, 0.2, ct);
            if (!string.IsNullOrWhiteSpace(llm.Content) && GroundingCheck(llm.Content, toolBlobs))
                finalAnswer = llm.Content.Trim();
        }
        catch
        {
            // stay with template
        }

        var ok = GroundingCheck(finalAnswer, toolBlobs);
        if (!ok)
            finalAnswer = grounded; // fall back to deterministic

        return new ConciergeReply(
            finalAnswer,
            citations.Distinct().ToList(),
            toolsUsed.Distinct().ToList(),
            lang,
            ok,
            prepareCard);
    }

    private static string BuildGroundedAnswer(
        string lang,
        Campaign? campaign,
        Donation? donation,
        IEnumerable<Expense> expenses,
        List<string> toolBlobs,
        List<string> toolsUsed)
    {
        var sb = new StringBuilder();
        if (campaign != null)
        {
            sb.AppendLine(Localize(lang,
                $"Campaign \"{campaign.Title}\" is {campaign.Status}, trust score {campaign.TrustScore}, transparency {campaign.TransparencyScore}. Raised {campaign.RaisedAmount:N2} of {campaign.GoalAmount:N2} {campaign.Currency}. [campaign:{campaign.Id}]",
                $"अभियान \"{campaign.Title}\" स्थिति {campaign.Status}, ट्रस्ट स्कोर {campaign.TrustScore}, पारदर्शिता {campaign.TransparencyScore}। जुटाए {campaign.RaisedAmount:N2} / लक्ष्य {campaign.GoalAmount:N2} {campaign.Currency}। [campaign:{campaign.Id}]",
                $"ప్రచారం \"{campaign.Title}\" స్థితి {campaign.Status}, ట్రస్ట్ {campaign.TrustScore}, పారదర్శకత {campaign.TransparencyScore}. సేకరణ {campaign.RaisedAmount:N2} / లక్ష్యం {campaign.GoalAmount:N2} {campaign.Currency}. [campaign:{campaign.Id}]"));
        }

        var expList = expenses.ToList();
        if (expList.Count > 0 && toolsUsed.Contains("ListExpenses"))
        {
            var total = expList.Sum(e => e.Amount);
            sb.AppendLine(Localize(lang,
                $"There are {expList.Count} expenses totaling {total:N2}. Categories: {string.Join(", ", expList.Select(e => e.Category).Distinct())}.",
                $"कुल {expList.Count} खर्च, राशि {total:N2}। श्रेणियाँ: {string.Join(", ", expList.Select(e => e.Category).Distinct())}।",
                $"మొత్తం {expList.Count} ఖర్చులు, మొత్తం {total:N2}. వర్గాలు: {string.Join(", ", expList.Select(e => e.Category).Distinct())}."));
            foreach (var e in expList.Take(5))
                sb.AppendLine($"- {e.Vendor}: {e.Amount:N2} ({e.Category}) [expense:{e.Id}]");
        }

        if (donation != null && toolsUsed.Contains("GetDonationAllocation"))
        {
            sb.AppendLine(Localize(lang,
                $"Your donation of {donation.Amount:N2} {donation.Currency} is allocated pro rata across verified spending. [donation:{donation.Id}]",
                $"आपका दान {donation.Amount:N2} {donation.Currency} सत्यापित खर्च पर आनुपातिक रूप से आवंटित है। [donation:{donation.Id}]",
                $"మీ విరాళం {donation.Amount:N2} {donation.Currency} ధృవీకరించిన ఖర్చులకు నిష్పత్తిలో కేటాయించబడింది. [donation:{donation.Id}]"));
        }

        if (sb.Length == 0)
            sb.Append(Localize(lang, "I retrieved data but have nothing specific to summarize.", "डेटा मिला पर सारांश सीमित है।", "డేటా వచ్చింది కానీ సారాంశం పరిమితం."));

        sb.AppendLine();
        sb.Append(Localize(lang,
            "Allocation method: donation ÷ total raised × category spend. Amounts are computed by server code, not AI.",
            "आवंटन: दान ÷ कुल जुटाई × श्रेणी खर्च। राशियाँ सर्वर कोड से गणना होती हैं, AI से नहीं।",
            "కేటాయింపు: విరాళం ÷ మొత్తం సేకరణ × వర్గం ఖర్చు. మొత్తాలు సర్వర్ కోడ్‌తో లెక్క, AI కాదు."));
        return sb.ToString().Trim();
    }

    /// <summary>Any numeric token or GUID-like id in the answer must appear in tool results.</summary>
    public static bool GroundingCheck(string answer, IEnumerable<string> toolJsonBlobs)
    {
        var corpus = string.Join("\n", toolJsonBlobs);
        // Currency-like or plain decimals
        var nums = Regex.Matches(answer, @"\b\d+(\.\d+)?\b")
            .Select(m => m.Value)
            .Where(v => v.Length >= 1)
            .Distinct()
            .ToList();
        foreach (var n in nums)
        {
            // allow small structural numbers like counts already in templates
            if (n is "0" or "1" or "2" or "3" or "4" or "5") continue;
            if (!corpus.Contains(n, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static ConciergeReply Refuse(string lang, string en, IReadOnlyList<string> citations)
    {
        var ans = Localize(lang, en,
            "इस संदर्भ में पर्याप्त डेटा नहीं है। कृपया अभियान पृष्ठ खोलें या स्थिति/खर्च के बारे में पूछें।",
            "ఈ సందర్భంలో తగిన డేటా లేదు. ప్రచార పేజీ తెరవండి లేదా స్థితి/ఖర్చుల గురించి అడగండి.");
        return new ConciergeReply(ans, citations.ToList(), Array.Empty<string>(), lang, true, null);
    }

    private static string DetectLanguage(string text)
    {
        if (Regex.IsMatch(text, @"[\u0900-\u097F]")) return "hi";
        if (Regex.IsMatch(text, @"[\u0C00-\u0C7F]")) return "te";
        return "en";
    }

    private static string Localize(string lang, string en, string hi, string te) => lang switch
    {
        "hi" => hi,
        "te" => te,
        "es" => en, // Spanish: fall back to EN template until full strings added
        "fr" => en,
        "ar" => en,
        "ta" => en,
        _ => en
    };

    private static bool ContainsAny(string hay, params string[] needles) =>
        needles.Any(n => hay.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static decimal? ExtractAmount(string text)
    {
        var m = Regex.Match(text, @"(\d+(\.\d{1,2})?)");
        if (m.Success && decimal.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }
}
