namespace CrowdFund.Core.Interfaces;

public enum ScanVerdict { Clean, Infected, Error, Skipped }

public record VirusScanResult(ScanVerdict Verdict, string Engine, string? Detail);

public interface IVirusScanner
{
    Task<VirusScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct = default);
}
