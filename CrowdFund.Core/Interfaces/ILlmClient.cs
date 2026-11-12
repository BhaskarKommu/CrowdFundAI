namespace CrowdFund.Core.Interfaces;

public record LlmMessage(string Role, string Content, string? ImageBase64 = null, string? MediaType = null);

public record LlmResponse(string Content, int InputTokens, int OutputTokens, string Model);

public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(IEnumerable<LlmMessage> messages, string? systemPrompt = null, double temperature = 0.2, CancellationToken ct = default);
    Task<T?> CompleteJsonAsync<T>(IEnumerable<LlmMessage> messages, string? systemPrompt = null, CancellationToken ct = default) where T : class;
}
