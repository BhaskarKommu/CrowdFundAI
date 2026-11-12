using System.Net.Http.Json;
using System.Text.Json;

namespace CrowdFund.Web.Observability;

public class AlertOptions
{
    public const string Section = "Observability";
    public string? AlertWebhookUrl { get; set; }
    public int FailedCaptureAlertThreshold { get; set; } = 3;
    public int FraudSpikeThreshold { get; set; } = 5;
    public int FraudSpikeWindowMinutes { get; set; } = 30;
}

/// <summary>Sends structured alerts to a webhook (Slack/Teams/generic) and always logs critically.</summary>
public class AlertService
{
    private readonly HttpClient _http;
    private readonly AlertOptions _opt;
    private readonly AppMetrics _metrics;
    private readonly ILogger<AlertService> _log;

    public AlertService(HttpClient http, Microsoft.Extensions.Options.IOptions<AlertOptions> opt, AppMetrics metrics, ILogger<AlertService> log)
    {
        _http = http;
        _opt = opt.Value;
        _metrics = metrics;
        _log = log;
    }

    public async Task NotifyAsync(string severity, string title, object details, CancellationToken ct = default)
    {
        _log.Log(
            severity.Equals("critical", StringComparison.OrdinalIgnoreCase) ? LogLevel.Critical : LogLevel.Warning,
            "ALERT {Severity} {Title} {@Details}", severity, title, details);

        _metrics.AlertSent();

        var url = _opt.AlertWebhookUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            var payload = new
            {
                text = $"[{severity}] {title}",
                severity,
                title,
                details,
                at = DateTime.UtcNow
            };
            using var res = await _http.PostAsJsonAsync(url, payload, ct);
            if (!res.IsSuccessStatusCode)
                _log.LogWarning("Alert webhook returned {Status}", res.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to post alert webhook");
        }
    }

    public async Task CheckThresholdsAsync(CancellationToken ct = default)
    {
        var failWindow = TimeSpan.FromMinutes(30);
        var fails = _metrics.RecentCaptureFailCount(failWindow);
        if (fails >= _opt.FailedCaptureAlertThreshold)
        {
            await NotifyAsync("critical", "Failed payment captures spike", new { fails, windowMinutes = 30 }, ct);
        }

        var fraudWindow = TimeSpan.FromMinutes(_opt.FraudSpikeWindowMinutes);
        var fraud = _metrics.RecentFraudCount(fraudWindow);
        if (fraud >= _opt.FraudSpikeThreshold)
        {
            await NotifyAsync("warning", "Fraud flag spike", new { fraud, windowMinutes = _opt.FraudSpikeWindowMinutes }, ct);
        }
    }
}
