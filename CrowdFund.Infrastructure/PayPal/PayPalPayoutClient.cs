using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrowdFund.Infrastructure.PayPal;

/// <summary>
/// PayPal Payouts API (sandbox) + simulate mode for offline demos.
/// Production: use live credentials, webhook reconciliation, and strong auth on destinations.
/// </summary>
public class PayPalPayoutClient
{
    private readonly HttpClient _http;
    private readonly PayPalOptions _opt;
    private readonly PayPalAccountService _accounts;
    private readonly ILogger<PayPalPayoutClient> _log;

    public PayPalPayoutClient(HttpClient http, IOptions<PayPalOptions> opt, PayPalAccountService accounts, ILogger<PayPalPayoutClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _accounts = accounts;
        _log = log;
    }

    public async Task<(bool ok, string payoutBatchId, string message)> SendPayoutAsync(
        string receiverEmail,
        decimal amount,
        string currency,
        string senderItemId,
        string note,
        CancellationToken ct = default)
    {
        if (_opt.Simulate || string.IsNullOrWhiteSpace(_opt.ClientId))
        {
            var simId = "SIM-PAYOUT-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            _log.LogInformation("Simulated payout {Id} to {Email} amount {Amount} {Currency}", simId, receiverEmail, amount, currency);
            return (true, simId, "Simulated payout completed");
        }

        var token = await _accounts.GetAccessTokenAsync(null, ct);
        var baseUrl = _opt.Mode?.Equals("live", StringComparison.OrdinalIgnoreCase) == true
            ? "https://api-m.paypal.com"
            : "https://api-m.sandbox.paypal.com";

        var body = new
        {
            sender_batch_header = new
            {
                sender_batch_id = "batch_" + senderItemId,
                email_subject = "You have a payout from CrowdFund AI",
                email_message = note
            },
            items = new[]
            {
                new
                {
                    recipient_type = "EMAIL",
                    amount = new { value = amount.ToString("0.00"), currency },
                    receiver = receiverEmail,
                    sender_item_id = senderItemId,
                    note
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/payments/payouts");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(body);
        var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            _log.LogError("PayPal payout failed: {Status} {Body}", res.StatusCode, json);
            return (false, "", json);
        }

        using var doc = JsonDocument.Parse(json);
        var batchId = doc.RootElement.GetProperty("batch_header").GetProperty("payout_batch_id").GetString() ?? "";
        return (true, batchId, "Submitted to PayPal Payouts");
    }
}
