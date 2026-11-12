using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrowdFund.Core.Entities;

namespace CrowdFund.Ai.Fraud;

/// <summary>
/// Lightweight document forensics for demo/production-hook:
/// EXIF-like header signals, PDF structure heuristics, cross-campaign duplicate images.
/// Full ELA requires image decoding libraries — we expose structural + hash forensics here.
/// </summary>
public class DocumentForensicsService
{
    public record ForensicsResult(
        Guid? ExpenseId,
        string FileName,
        string Sha256,
        bool Suspicious,
        IReadOnlyList<string> Findings,
        object Details);

    public ForensicsResult AnalyzeBytes(string fileName, string contentType, byte[] bytes, Guid? expenseId = null, string? sha256 = null)
    {
        var findings = new List<string>();
        var details = new Dictionary<string, object?>();
        var name = fileName.ToLowerInvariant();
        var sha = sha256 ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        details["size"] = bytes.Length;
        details["contentType"] = contentType;

        if (name.EndsWith(".pdf") || contentType.Contains("pdf"))
        {
            var text = Encoding.ASCII.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 64_000)));
            var hasEof = text.Contains("%%EOF") || Encoding.ASCII.GetString(bytes.AsSpan(Math.Max(0, bytes.Length - 32))).Contains("%%EOF");
            var hasTrailer = text.Contains("trailer");
            var hasXref = text.Contains("xref");
            var js = Regex.IsMatch(text, @"/JS\b|/JavaScript\b");
            var embed = Regex.IsMatch(text, @"/EmbeddedFile|/Filespec", RegexOptions.IgnoreCase);
            details["pdf"] = new { hasEof, hasTrailer, hasXref, javascript = js, embeddedFiles = embed };
            if (!hasEof) findings.Add("PDF may be truncated (missing %%EOF).");
            if (!hasTrailer || !hasXref) findings.Add("PDF structure incomplete (xref/trailer).");
            if (js) findings.Add("PDF contains JavaScript actions — review carefully.");
            if (embed) findings.Add("PDF has embedded files.");
        }
        else if (name.EndsWith(".jpg") || name.EndsWith(".jpeg") || contentType.Contains("jpeg"))
        {
            // JPEG SOI
            var soi = bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            details["jpegSoi"] = soi;
            if (!soi) findings.Add("JPEG missing SOI marker.");
            // Scan for EXIF APP1
            var exif = IndexOf(bytes, Encoding.ASCII.GetBytes("Exif"));
            details["exifOffset"] = exif;
            if (exif < 0) findings.Add("No EXIF marker found (possible re-export/strip).");
            // Software tags often left by editors
            var soft = IndexOf(bytes, Encoding.ASCII.GetBytes("Adobe")) >= 0
                       || IndexOf(bytes, Encoding.ASCII.GetBytes("Photoshop")) >= 0
                       || IndexOf(bytes, Encoding.ASCII.GetBytes("GIMP")) >= 0;
            details["editorTag"] = soft;
            if (soft) findings.Add("Editor software marker present in image metadata bytes.");
        }
        else if (name.EndsWith(".png") || contentType.Contains("png"))
        {
            var png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50;
            details["pngSignature"] = png;
            if (!png) findings.Add("PNG signature mismatch.");
        }

        // Entropy-ish: high null padding
        if (bytes.Length > 1024)
        {
            var zeros = bytes.Take(4096).Count(b => b == 0);
            if (zeros > 3000) findings.Add("Large null padding in file head — unusual for receipts.");
            details["nullBytesSample"] = zeros;
        }

        return new ForensicsResult(expenseId, fileName, sha, findings.Count > 0, findings, details);
    }

    public IReadOnlyList<FraudFlag> CrossCampaignDuplicates(
        Guid campaignId,
        IEnumerable<(string Sha256, Guid CampaignId, Guid ExpenseId, string FileName)> corpus)
    {
        var flags = new List<FraudFlag>();
        var mine = corpus.Where(c => c.CampaignId == campaignId).ToList();
        foreach (var m in mine)
        {
            if (string.IsNullOrWhiteSpace(m.Sha256)) continue;
            var hits = corpus.Where(c => c.CampaignId != campaignId
                                         && c.Sha256.Equals(m.Sha256, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) continue;
            flags.Add(new FraudFlag
            {
                CampaignId = campaignId,
                ExpenseId = m.ExpenseId,
                Rule = "DuplicateImageSearch",
                Severity = "High",
                Score = 0.93,
                Explanation = $"File '{m.FileName}' hash matches {hits.Count} file(s) on other campaigns.",
                EvidenceJson = JsonSerializer.Serialize(hits.Select(h => new { h.CampaignId, h.ExpenseId, h.FileName })),
                Status = "Open",
                CreatedAt = DateTime.UtcNow
            });
        }
        return flags;
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        if (needle.Length == 0 || hay.Length < needle.Length) return -1;
        for (var i = 0; i <= hay.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
