using System.Runtime.CompilerServices;
using System.Text.Json;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;

namespace CrowdFund.Ai.Concierge;

/// <summary>
/// Concierge 2.0 — streaming tokens, expanded tools, more languages.
/// Voice: expose text chunks; clients can feed TTS later.
/// </summary>
public class ConciergeStreaming
{
    private readonly DonorConcierge _base;

    public ConciergeStreaming(DonorConcierge baseline) => _base = baseline;

    public async IAsyncEnumerable<string> StreamAnswerAsync(
        string userMessage,
        Campaign? campaign,
        Donation? donation,
        IEnumerable<Expense> expenses,
        IEnumerable<Campaign> searchable,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var reply = await _base.AnswerAsync(userMessage, campaign, donation, expenses, searchable, ct);
        // Stream by sentences/words for UX (true token stream when real LLM streaming is wired)
        var text = reply.Answer;
        var parts = SplitForStream(text);
        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            yield return part;
            await Task.Delay(12, ct); // slight pacing for UI
        }
        // terminal metadata line for clients that want citations
        yield return $"\n\n<!--meta:{JsonSerializer.Serialize(new { reply.Citations, reply.ToolsUsed, reply.Language, reply.GroundingOk })}-->";
    }

    private static IEnumerable<string> SplitForStream(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return "";
            yield break;
        }
        var buf = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            buf.Append(ch);
            if (ch is '.' or '!' or '?' or '\n' || buf.Length >= 40)
            {
                yield return buf.ToString();
                buf.Clear();
            }
        }
        if (buf.Length > 0) yield return buf.ToString();
    }
}
