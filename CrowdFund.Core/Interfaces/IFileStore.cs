namespace CrowdFund.Core.Interfaces;

public interface IFileStore
{
    Task<string> SaveAsync(Stream content, string fileName, string contentType, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct = default);
    Task DeleteAsync(string storedPath, CancellationToken ct = default);
    bool Exists(string storedPath);
}
