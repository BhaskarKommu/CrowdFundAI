namespace CrowdFund.Web.Observability;

/// <summary>Periodically evaluates metric thresholds and fires alerts.</summary>
public class MetricsAlertHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MetricsAlertHostedService> _log;

    public MetricsAlertHostedService(IServiceScopeFactory scopes, ILogger<MetricsAlertHostedService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
                await alerts.CheckThresholdsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Metrics alert loop failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}
