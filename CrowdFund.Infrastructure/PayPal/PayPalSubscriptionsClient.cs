using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrowdFund.Infrastructure.PayPal;

/// <summary>
/// PayPal Subscriptions API: products, plans (trial/setup fee/cycles), subscribe,
/// activate, suspend, cancel, revise (upgrade/downgrade).
/// </summary>
public class PayPalSubscriptionsClient
{
    private readonly HttpClient _http;
    private readonly PayPalOptions _opt;
    private readonly PayPalAccountService _accounts;
    private readonly ILogger<PayPalSubscriptionsClient> _log;

    public PayPalSubscriptionsClient(
        HttpClient http,
        IOptions<PayPalOptions> opt,
        PayPalAccountService accounts,
        ILogger<PayPalSubscriptionsClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _accounts = accounts;
        _log = log;
    }

    public bool IsSimulated => _accounts.IsSimulated;

    public async Task<(string planId, string productId)> CreatePlanAsync(
        string productName,
        decimal amount,
        string currency,
        string intervalUnit, // WEEK MONTH YEAR
        int intervalCount = 1,
        int totalCycles = 0,
        int trialDays = 0,
        decimal setupFee = 0,
        int paymentFailureThreshold = 3,
        CancellationToken ct = default)
    {
        intervalUnit = intervalUnit.ToUpperInvariant() switch
        {
            "WEEK" or "WEEKLY" => "WEEK",
            "YEAR" or "YEARLY" or "ANNUAL" => "YEAR",
            _ => "MONTH"
        };

        if (IsSimulated)
        {
            var pid = $"SIM-PLAN-{intervalUnit}-{amount:0.##}";
            var prod = $"SIM-PROD-{Guid.NewGuid():N}"[..16];
            return (pid, prod);
        }

        var token = await _accounts.GetAccessTokenAsync(null, ct);
        var productId = await CreateProductAsync(token, productName, ct);

        var billingCycles = new List<object>();
        var sequence = 1;
        if (trialDays > 0)
        {
            // Approximate trial as a weekly/day cycle — PayPal uses interval units; use DAY if possible
            billingCycles.Add(new
            {
                frequency = new { interval_unit = "DAY", interval_count = Math.Max(1, trialDays) },
                tenure_type = "TRIAL",
                sequence,
                total_cycles = 1,
                pricing_scheme = new { fixed_price = new { value = "0.00", currency_code = currency } }
            });
            sequence++;
        }

        billingCycles.Add(new
        {
            frequency = new { interval_unit = intervalUnit, interval_count = Math.Max(1, intervalCount) },
            tenure_type = "REGULAR",
            sequence,
            total_cycles = totalCycles, // 0 = infinite on PayPal for REGULAR
            pricing_scheme = new { fixed_price = new { value = amount.ToString("F2"), currency_code = currency } }
        });

        var planBody = new Dictionary<string, object?>
        {
            ["product_id"] = productId,
            ["name"] = $"{productName} ({intervalUnit})",
            ["billing_cycles"] = billingCycles,
            ["payment_preferences"] = new
            {
                auto_bill_outstanding = true,
                setup_fee = setupFee > 0
                    ? new { value = setupFee.ToString("F2"), currency_code = currency }
                    : null,
                setup_fee_failure_action = "CONTINUE",
                payment_failure_threshold = Math.Max(1, paymentFailureThreshold)
            },
            ["status"] = "ACTIVE"
        };

        using var planReq = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl}/v1/billing/plans");
        planReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        planReq.Headers.Add("PayPal-Request-Id", Guid.NewGuid().ToString());
        planReq.Content = new StringContent(JsonSerializer.Serialize(planBody), Encoding.UTF8, "application/json");
        var planResp = await _http.SendAsync(planReq, ct);
        var planJson = await planResp.Content.ReadAsStringAsync(ct);
        if (!planResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PayPal plan create failed: {planResp.StatusCode} {planJson}");
        using var planDoc = JsonDocument.Parse(planJson);
        return (planDoc.RootElement.GetProperty("id").GetString()!, productId);
    }

    private async Task<string> CreateProductAsync(string token, string name, CancellationToken ct)
    {
        var productBody = new { name, type = "SERVICE", category = "CHARITY" };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl}/v1/catalogs/products");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("PayPal-Request-Id", Guid.NewGuid().ToString());
        req.Content = new StringContent(JsonSerializer.Serialize(productBody), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PayPal product create failed: {resp.StatusCode} {json}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    public async Task<PayPalSubscriptionCreateResult> CreateSubscriptionAsync(
        string planId, string customId, string returnUrl, string cancelUrl, CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            var simSubId = "SIM-SUB-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            return new PayPalSubscriptionCreateResult(simSubId, "APPROVAL_PENDING", $"{returnUrl}?subscription_id={simSubId}&simulated=1");
        }
        var token = await _accounts.GetAccessTokenAsync(null, ct);
        var body = new
        {
            plan_id = planId,
            custom_id = customId,
            application_context = new
            {
                brand_name = _opt.BrandName,
                user_action = "SUBSCRIBE_NOW",
                return_url = returnUrl,
                cancel_url = cancelUrl
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl}/v1/billing/subscriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PayPal subscription create failed: {resp.StatusCode} {json}");
        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetString()!;
        var status = doc.RootElement.GetProperty("status").GetString() ?? "";
        string? approve = null;
        if (doc.RootElement.TryGetProperty("links", out var links))
        {
            foreach (var link in links.EnumerateArray())
            {
                if (link.GetProperty("rel").GetString() == "approve")
                {
                    approve = link.GetProperty("href").GetString();
                    break;
                }
            }
        }
        return new PayPalSubscriptionCreateResult(id, status, approve);
    }

    public Task CancelSubscriptionAsync(string subscriptionId, string reason, CancellationToken ct = default) =>
        PostActionAsync(subscriptionId, "cancel", new { reason }, ct);

    public Task SuspendSubscriptionAsync(string subscriptionId, string reason, CancellationToken ct = default) =>
        PostActionAsync(subscriptionId, "suspend", new { reason }, ct);

    public Task ActivateSubscriptionAsync(string subscriptionId, string reason, CancellationToken ct = default) =>
        PostActionAsync(subscriptionId, "activate", new { reason }, ct);

    public async Task ReviseSubscriptionPlanAsync(string subscriptionId, string newPlanId, CancellationToken ct = default)
    {
        if (IsSimulated) return;
        var token = await _accounts.GetAccessTokenAsync(null, ct);
        var body = new { plan_id = newPlanId };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl}/v1/billing/subscriptions/{subscriptionId}/revise");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"PayPal revise failed: {resp.StatusCode} {json}");
        }
    }

    public async Task<string> GetSubscriptionStatusAsync(string subscriptionId, CancellationToken ct = default)
    {
        if (IsSimulated) return "ACTIVE";
        var token = await _accounts.GetAccessTokenAsync(null, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_opt.BaseUrl}/v1/billing/subscriptions/{subscriptionId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return "UNKNOWN";
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("status").GetString() ?? "UNKNOWN";
    }

    private async Task PostActionAsync(string subscriptionId, string action, object body, CancellationToken ct)
    {
        if (IsSimulated)
        {
            _log.LogInformation("Simulated subscription {Action} {Id}", action, subscriptionId);
            return;
        }
        var token = await _accounts.GetAccessTokenAsync(null, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl}/v1/billing/subscriptions/{subscriptionId}/{action}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("Subscription {Action} {Id} -> {Status} {Body}", action, subscriptionId, resp.StatusCode, json);
            throw new InvalidOperationException($"PayPal subscription {action} failed: {resp.StatusCode}");
        }
    }

    // Back-compat helper used by older endpoints
    public Task<(string planId, string productId)> EnsureMonthlyPlanAsync(
        string productName, decimal amount, string currency, CancellationToken ct = default) =>
        CreatePlanAsync(productName, amount, currency, "MONTH", 1, 0, 0, 0, 3, ct);
}

public record PayPalSubscriptionCreateResult(string SubscriptionId, string Status, string? ApproveUrl);
