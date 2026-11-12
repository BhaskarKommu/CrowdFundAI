using CrowdFund.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrowdFund.Infrastructure.Storage;

/// <summary>Demo scanner: rejects EICAR test string; otherwise clean.</summary>
public class BasicVirusScanner : IVirusScanner
{
    private static readonly byte[] Eicar = System.Text.Encoding.ASCII.GetBytes(
        "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    public Task<VirusScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        var bytes = ms.ToArray();
        if (Contains(bytes, Eicar))
            return Task.FromResult(new VirusScanResult(ScanVerdict.Infected, "BasicVirusScanner", "EICAR test signature"));
        // Empty / oversized soft fail
        if (bytes.Length == 0)
            return Task.FromResult(new VirusScanResult(ScanVerdict.Error, "BasicVirusScanner", "Empty file"));
        return Task.FromResult(new VirusScanResult(ScanVerdict.Clean, "BasicVirusScanner", null));
    }

    private static bool Contains(byte[] hay, byte[] needle)
    {
        if (needle.Length > hay.Length) return false;
        for (var i = 0; i <= hay.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }
}

/// <summary>Optional ClamAV daemon via clamd TCP (when ObjectStorage:ClamAvHost is set).</summary>
public class ClamAvVirusScanner : IVirusScanner
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger<ClamAvVirusScanner> _logger;

    public ClamAvVirusScanner(IConfiguration config, ILogger<ClamAvVirusScanner> logger)
    {
        _host = config["ObjectStorage:ClamAvHost"] ?? "";
        _port = int.TryParse(config["ObjectStorage:ClamAvPort"], out var p) ? p : 3310;
        _logger = logger;
    }

    public async Task<VirusScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_host))
            return new VirusScanResult(ScanVerdict.Skipped, "ClamAV", "Host not configured");
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(_host, _port, ct);
            await using var stream = client.GetStream();
            var cmd = System.Text.Encoding.ASCII.GetBytes("zINSTREAM\0");
            await stream.WriteAsync(cmd, ct);
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var data = ms.ToArray();
            var len = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(data.Length));
            await stream.WriteAsync(len, ct);
            await stream.WriteAsync(data, ct);
            await stream.WriteAsync(BitConverter.GetBytes(0), ct);
            using var reader = new StreamReader(stream);
            var response = await reader.ReadToEndAsync(ct);
            if (response.Contains("FOUND", StringComparison.OrdinalIgnoreCase))
                return new VirusScanResult(ScanVerdict.Infected, "ClamAV", response.Trim());
            return new VirusScanResult(ScanVerdict.Clean, "ClamAV", response.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClamAV scan failed");
            return new VirusScanResult(ScanVerdict.Error, "ClamAV", ex.Message);
        }
    }
}
