using System.Security.Cryptography;
using System.Text;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Interfaces;

namespace CrowdFund.Core.Services;

/// <summary>
/// Multimodal proof: store media, generate lightweight transcript/summary (demo),
/// require human approval before use as payout evidence.
/// </summary>
public class MultimodalProofService
{
    private readonly ILlmClient _llm;

    public MultimodalProofService(ILlmClient llm) => _llm = llm;

    public async Task<(MultimodalProof proof, byte[] bytes)> CreateFromUploadAsync(
        Guid campaignId,
        Guid? expenseId,
        Guid? milestoneId,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var media = contentType.StartsWith("video") ? "video"
            : contentType.StartsWith("audio") ? "audio"
            : "image";

        var proof = new MultimodalProof
        {
            CampaignId = campaignId,
            ExpenseId = expenseId,
            MilestoneId = milestoneId,
            MediaType = media,
            FileName = fileName,
            ContentType = contentType,
            Sha256 = sha,
            SizeBytes = bytes.Length,
            Status = MultimodalProofStatus.Uploaded
        };

        // Demo transcript/summary path (no real ASR) — LLM drafts from metadata
        var hint = $"Media file {fileName} ({media}, {bytes.Length} bytes) for campaign {campaignId}.";
        try
        {
            var resp = await _llm.CompleteAsync(
                new[] { new LlmMessage("user", hint) },
                "Write a 2-sentence neutral placeholder transcript/summary for a proof-of-use media upload. Do not invent specific medical claims.",
                0.2, ct);
            proof.Transcript = resp.Content?.Trim() ?? $"Uploaded {media} proof: {fileName}";
            proof.AiSummary = "AI draft summary pending human review.";
            proof.TranscriptConfidence = 0.4;
            proof.Status = MultimodalProofStatus.PendingHumanReview;
        }
        catch
        {
            proof.Transcript = $"Uploaded {media}: {fileName}";
            proof.Status = MultimodalProofStatus.PendingHumanReview;
        }

        return (proof, bytes);
    }

    public void Approve(MultimodalProof proof, string reviewerId, string? note)
    {
        proof.Status = MultimodalProofStatus.Approved;
        proof.ReviewedBy = reviewerId;
        proof.ReviewedAt = DateTime.UtcNow;
        proof.ReviewNote = note;
    }

    public void Reject(MultimodalProof proof, string reviewerId, string? note)
    {
        proof.Status = MultimodalProofStatus.Rejected;
        proof.ReviewedBy = reviewerId;
        proof.ReviewedAt = DateTime.UtcNow;
        proof.ReviewNote = note;
    }

    /// <summary>Payout/milestone gates should call this — AI never auto-approves.</summary>
    public static bool IsHumanApproved(MultimodalProof proof) =>
        proof.Status == MultimodalProofStatus.Approved && proof.ReviewedAt != null;
}
