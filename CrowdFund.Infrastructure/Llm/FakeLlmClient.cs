using System.Text.Json;
using CrowdFund.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace CrowdFund.Infrastructure.Llm;

/// <summary>
/// Offline / test LLM that returns canned JSON so demos and tests work without API keys.
/// </summary>
public class FakeLlmClient : ILlmClient
{
    private readonly ILogger<FakeLlmClient> _logger;

    public FakeLlmClient(ILogger<FakeLlmClient> logger) => _logger = logger;

    public Task<LlmResponse> CompleteAsync(IEnumerable<LlmMessage> messages, string? systemPrompt = null, double temperature = 0.2, CancellationToken ct = default)
    {
        _logger.LogInformation("FakeLlmClient.CompleteAsync called");
        return Task.FromResult(new LlmResponse(
            "This is a simulated AI response for offline demo. Configure a real Anthropic key for production features.",
            100, 50, "fake-model"));
    }

    public Task<T?> CompleteJsonAsync<T>(IEnumerable<LlmMessage> messages, string? systemPrompt = null, CancellationToken ct = default) where T : class
    {
        _logger.LogInformation("FakeLlmClient.CompleteJsonAsync<{Type}> called", typeof(T).Name);

        // Canned payloads for known types, then deserialize so nested types match T
        object? canned = typeof(T).Name switch
        {
            "TrustAssessment" or "TrustAssessmentDto" => CreateTrustAssessment(),
            "ReceiptExtraction" or "ReceiptExtractionDto" => CreateReceiptExtraction(),
            _ => null
        };

        if (canned is not null)
        {
            try
            {
                var json = JsonSerializer.Serialize(canned);
                var typed = JsonSerializer.Deserialize<T>(json);
                if (typed is not null)
                    return Task.FromResult<T?>(typed);
            }
            catch
            {
                // fall through
            }
        }

        try
        {
            return Task.FromResult(JsonSerializer.Deserialize<T>("{}"));
        }
        catch
        {
            return Task.FromResult<T?>(null);
        }
    }

    private static object CreateTrustAssessment()
    {
        return new
        {
            overallScore = 85,
            band = "High",
            subScores = new
            {
                claimCompleteness = 90,
                documentAuthenticity = 88,
                consistency = 85,
                budgetPlausibility = 80,
                creatorHistory = 92
            },
            findings = new[]
            {
                new { agent = "ClaimExtractor", severity = "Info", title = "Claims extracted", detail = "Found 5 verifiable claims.", evidenceRefs = Array.Empty<string>(), confidence = 0.9 },
                new { agent = "DocumentForensics", severity = "Info", title = "Documents appear authentic", detail = "No obvious tampering detected.", evidenceRefs = Array.Empty<string>(), confidence = 0.85 }
            },
            missingInformation = Array.Empty<string>(),
            recommendedAction = "Approve",
            disclaimer = "This is a risk indicator, not proof of authenticity."
        };
    }

    private static object CreateReceiptExtraction()
    {
        return new
        {
            vendor = "City Hospital Pharmacy",
            date = "2025-04-18",
            currency = "INR",
            total = 2500.00m,
            tax = 0m,
            lineItems = new[]
            {
                new { description = "Medicines", qty = 1, unitPrice = 2500.00m, category = "medicines" }
            },
            paymentMethod = "Cash",
            confidence = 0.92
        };
    }
}
