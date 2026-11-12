using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrowdFund.Infrastructure.PayPal;

/// <summary>
/// Minimal PayPal Vault / Setup Token client for saved payment methods.
/// Simulated when no live credentials are configured.
/// </summary>
public class PayPalVaultClient
{
    private readonly HttpClient _http;
    private readonly PayPalOptions _opts;
    private readonly PayPalAccountService _accounts;
    private readonly ILogger<PayPalVaultClient> _logger;

    public PayPalVaultClient(
        HttpClient http,
        IOptions<PayPalOptions> opts,
        PayPalAccountService accounts,
        ILogger<PayPalVaultClient> logger)
    {
        _http = http;
        _opts = opts.Value;
        _accounts = accounts;
        _logger = logger;
    }

    private bool IsSimulated =>
        string.IsNullOrWhiteSpace(_opts.ClientId) ||
        string.IsNullOrWhiteSpace(_opts.ClientSecret) ||
        _opts.Simulate;

    public async Task<PayPalSetupTokenResult> CreateSetupTokenAsync(string customerId, CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            var id = "SIM-SETUP-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
            _logger.LogInformation("PayPal SIMULATE CreateSetupToken for customer {CustomerId} -> {Token}", customerId, id);
            return new PayPalSetupTokenResult(id, "APPROVED");
        }

        // Live path would call PayPal Vault setup-tokens API; keep stub for compile/demo.
        _logger.LogWarning("Live PayPal Vault CreateSetupToken not fully implemented; returning simulated token.");
        var sim = "SETUP-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        return new PayPalSetupTokenResult(sim, "APPROVED");
    }
}

public record PayPalSetupTokenResult(string SetupTokenId, string Status);
