using CrowdFund.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrowdFund.Infrastructure.Storage;

public class LocalFileStore : IFileStore
{
    private readonly string _root;
    private readonly ILogger<LocalFileStore> _logger;

    public LocalFileStore(IConfiguration config, ILogger<LocalFileStore> logger)
    {
        _root = config["FileStore:Root"] ?? Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "uploads");
        Directory.CreateDirectory(_root);
        _logger = logger;
    }

    public async Task<string> SaveAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        // Validate type and size (max 10 MB)
        var allowed = new[] { "application/pdf", "image/png", "image/jpeg", "image/jpg" };
        if (!allowed.Contains(contentType.ToLowerInvariant()))
            throw new InvalidOperationException($"File type {contentType} not allowed.");

        if (content.CanSeek && content.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("File exceeds 10 MB limit.");

        var safeName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var relative = Path.Combine(DateTime.UtcNow.ToString("yyyy/MM"), safeName);
        var fullPath = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var fs = File.Create(fullPath);
        await content.CopyToAsync(fs, ct);
        _logger.LogInformation("Saved file {Path}", relative);
        return relative.Replace('\\', '/');
    }

    public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct = default)
    {
        var full = Path.Combine(_root, storedPath);
        if (!File.Exists(full))
            throw new FileNotFoundException("File not found", storedPath);
        return Task.FromResult<Stream>(File.OpenRead(full));
    }

    public Task DeleteAsync(string storedPath, CancellationToken ct = default)
    {
        var full = Path.Combine(_root, storedPath);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    public bool Exists(string storedPath)
    {
        return File.Exists(Path.Combine(_root, storedPath));
    }
}
