using System.ComponentModel.DataAnnotations;

namespace CrowdFund.Core.Entities;

public class CampaignDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    [Required, MaxLength(255)]
    public string FileName { get; set; } = string.Empty;
    [Required]
    public string StoredPath { get; set; } = string.Empty;
    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;
    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;
    [MaxLength(64)]
    public string? PerceptualHash { get; set; }
    public string? ExtractedTextJson { get; set; }
    public string? ForensicsJson { get; set; }
    [MaxLength(50)]
    public string DocType { get; set; } = "other";
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public Campaign? Campaign { get; set; }
}
