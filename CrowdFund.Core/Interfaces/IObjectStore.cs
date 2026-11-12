namespace CrowdFund.Core.Interfaces;

/// <summary>
/// Object storage for receipts/evidence. Implementations: local disk, S3, Cloudflare R2.
/// </summary>
public interface IObjectStore
{
    Task<ObjectStoreResult> PutAsync(Stream content, string key, string contentType, CancellationToken ct = default);
    Task<Stream> GetAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    /// <summary>Public or signed URL for CDN/edge delivery when configured.</summary>
    string? GetPublicUrl(string key);
}

public record ObjectStoreResult(string Key, string? PublicUrl, long Bytes);
