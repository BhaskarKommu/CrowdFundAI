using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrowdFund.Infrastructure.PayPal;

/// <summary>
/// PayPal Orders API (create / capture) with OAuth via <see cref="PayPalAccountService"/>.
/// Supports sandbox &amp; live, simulate mode, and optional multi-merchant payee.
/// </summary>
public class PayPalClient
{
    private readonly HttpClient _http;
    private readonly PayPalOptions _opts;
    private readonly PayPalAccountService _accounts;
    private readonly ILogger<PayPalClient> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PayPalClient(
        HttpClient http,
        IOptions<PayPalOptions> opts,
        PayPalAccountService accounts,
        ILogger<PayPalClient> logger)
    {
        _http = http;
        _opts = opts.Value;
        _accounts = accounts;
        _logger = logger;
    }

    public bool IsSimulated => _accounts.IsSimulated;

    public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
        _accounts.GetAccessTokenAsync(null, ct);

    public async Task<PayPalOrderResult> CreateOrderAsync(
        Guid donationId,
        decimal amount,
        string currency,
        string description,
        string? merchantKey = null,
        string intent = "CAPTURE",
        CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            var simOrderId = $"SIM-ORDER-{donationId:N}"[..32];
            _logger.LogInformation("PayPal SIMULATE CreateOrder {OrderId} for donation {DonationId}", simOrderId, donationId);
            return new PayPalOrderResult(simOrderId, "CREATED", null);
        }

        var token = await _accounts.GetAccessTokenAsync(merchantKey, ct);
        var (payeeMerchantId, _) = _accounts.GetPayee(merchantKey);

        object purchaseUnit;
        if (!string.IsNullOrWhiteSpace(payeeMerchantId))
        {
            purchaseUnit = new
            {
                reference_id = donationId.ToString(),
                custom_id = donationId.ToString(),
                description,
                amount = new { currency_code = currency, value = amount.ToString("F2") },
                payee = new { merchant_id = payeeMerchantId }
            };
        }
        else
        {
            purchaseUnit = new
            {
                reference_id = donationId.ToString(),
                custom_id = donationId.ToString(),
                description,
                amount = new { currency_code = currency, value = amount.ToString("F2") }
            };
        }

        var body = new
        {
            intent = string.Equals(intent, "AUTHORIZE", StringComparison.OrdinalIgnoreCase) ? "AUTHORIZE" : "CAPTURE",
            purchase_units = new[] { purchaseUnit },
            application_context = new
            {
                brand_name = string.IsNullOrWhiteSpace(_opts.BrandName) ? "CrowdFund AI" : _opts.BrandName,
                user_action = "PAY_NOW",
                return_url = string.IsNullOrWhiteSpace(_opts.ReturnUrl) ? "https://localhost:5001/donate/return" : _opts.ReturnUrl,
                cancel_url = string.IsNullOrWhiteSpace(_opts.CancelUrl) ? "https://localhost:5001/donate/cancel" : _opts.CancelUrl
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v2/checkout/orders");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Token may have been revoked — invalidate and surface error
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                _accounts.InvalidateToken(merchantKey);
            _logger.LogError("PayPal CreateOrder failed: {Status} {Body}", resp.StatusCode, json);
            throw new InvalidOperationException($"PayPal create order failed: {resp.StatusCode}");
        }

        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetString()!;
        var status = doc.RootElement.GetProperty("status").GetString()!;
        string? approveUrl = null;
        if (doc.RootElement.TryGetProperty("links", out var links))
        {
            foreach (var link in links.EnumerateArray())
            {
                if (string.Equals(link.GetProperty("rel").GetString(), "approve", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(link.GetProperty("rel").GetString(), "payer-action", StringComparison.OrdinalIgnoreCase))
                {
                    approveUrl = link.GetProperty("href").GetString();
                    break;
                }
            }
        }
        return new PayPalOrderResult(id, status, approveUrl);
    }

    public async Task<PayPalCaptureResult> CaptureOrderAsync(string orderId, string? merchantKey = null, CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            var simCaptureId = $"SIM-CAP-{Guid.NewGuid():N}"[..24];
            _logger.LogInformation("PayPal SIMULATE Capture {OrderId} -> {CaptureId}", orderId, simCaptureId);
            return new PayPalCaptureResult(orderId, simCaptureId, "COMPLETED", true);
        }

        var token = await _accounts.GetAccessTokenAsync(merchantKey, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v2/checkout/orders/{orderId}/capture");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                _accounts.InvalidateToken(merchantKey);
            _logger.LogError("PayPal Capture failed: {Status} {Body}", resp.StatusCode, json);
            throw new InvalidOperationException($"PayPal capture failed: {resp.StatusCode}");
        }

        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.GetProperty("status").GetString() ?? "UNKNOWN";
        string? captureId = null;
        if (doc.RootElement.TryGetProperty("purchase_units", out var units) && units.GetArrayLength() > 0)
        {
            var u0 = units[0];
            if (u0.TryGetProperty("payments", out var payments) &&
                payments.TryGetProperty("captures", out var captures) &&
                captures.GetArrayLength() > 0)
            {
                captureId = captures[0].GetProperty("id").GetString();
            }
        }
        return new PayPalCaptureResult(orderId, captureId ?? orderId, status, status is "COMPLETED" or "APPROVED");
    }

    public async Task<bool> VerifyWebhookSignatureAsync(
        string transmissionId,
        string timestamp,
        string sig,
        string certUrl,
        string authAlgo,
        string body,
        string? webhookId = null,
        CancellationToken ct = default)
    {
        if (IsSimulated) return true;
        var wid = webhookId ?? _opts.WebhookId;
        if (string.IsNullOrWhiteSpace(wid)) return false;

        var token = await _accounts.GetAccessTokenAsync(null, ct);
        var payload = new
        {
            transmission_id = transmissionId,
            transmission_time = timestamp,
            cert_url = certUrl,
            auth_algo = authAlgo,
            transmission_sig = sig,
            webhook_id = wid,
            webhook_event = JsonDocument.Parse(body).RootElement
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v1/notifications/verify-webhook-signature");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return false;
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("verification_status").GetString() == "SUCCESS";
    }


    public async Task<PayPalAuthorizeResult> AuthorizeOrderAsync(string orderId, string? merchantKey = null, CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            var simAuthId = $"SIM-AUTH-{Guid.NewGuid():N}"[..24];
            _logger.LogInformation("PayPal SIMULATE Authorize {OrderId} -> {AuthId}", orderId, simAuthId);
            return new PayPalAuthorizeResult(orderId, simAuthId, "CREATED", true);
        }
        var token = await _accounts.GetAccessTokenAsync(merchantKey, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v2/checkout/orders/{orderId}/authorize");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) _accounts.InvalidateToken(merchantKey);
            _logger.LogError("PayPal Authorize failed: {Status} {Body}", resp.StatusCode, json);
            throw new InvalidOperationException($"PayPal authorize failed: {resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.GetProperty("status").GetString() ?? "UNKNOWN";
        string? authId = null;
        if (doc.RootElement.TryGetProperty("purchase_units", out var units) && units.GetArrayLength() > 0)
        {
            var u0 = units[0];
            if (u0.TryGetProperty("payments", out var payments) &&
                payments.TryGetProperty("authorizations", out var auths) &&
                auths.GetArrayLength() > 0)
                authId = auths[0].GetProperty("id").GetString();
        }
        return new PayPalAuthorizeResult(orderId, authId ?? orderId, status, status is "COMPLETED" or "APPROVED");
    }

    public async Task<PayPalCaptureResult> CaptureAuthorizationAsync(
        string authorizationId,
        decimal? amount,
        string currency,
        bool finalCapture = true,
        string? merchantKey = null,
        CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            var capId = $"SIM-CAP-AUTH-{Guid.NewGuid():N}"[..24];
            return new PayPalCaptureResult(authorizationId, capId, "COMPLETED", true);
        }
        var token = await _accounts.GetAccessTokenAsync(merchantKey, ct);
        object body = amount == null
            ? new { final_capture = finalCapture }
            : new
            {
                amount = new { value = amount.Value.ToString("F2"), currency_code = currency },
                final_capture = finalCapture
            };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v2/payments/authorizations/{authorizationId}/capture");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("PayPal capture authorization failed: {Status} {Body}", resp.StatusCode, json);
            throw new InvalidOperationException($"PayPal capture authorization failed: {resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetString() ?? authorizationId;
        var status = doc.RootElement.GetProperty("status").GetString() ?? "UNKNOWN";
        return new PayPalCaptureResult(authorizationId, id, status, status == "COMPLETED");
    }

    public async Task<bool> VoidAuthorizationAsync(string authorizationId, string? merchantKey = null, CancellationToken ct = default)
    {
        if (IsSimulated) return true;
        var token = await _accounts.GetAccessTokenAsync(merchantKey, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v2/payments/authorizations/{authorizationId}/void");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<PayPalOrderStatusResult> GetOrderAsync(string orderId, string? merchantKey = null, CancellationToken ct = default)
    {
        if (IsSimulated)
            return new PayPalOrderStatusResult(orderId, "APPROVED", "SIMULATED");
        var token = await _accounts.GetAccessTokenAsync(merchantKey, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_opts.BaseUrl}/v2/checkout/orders/{orderId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PayPal get order failed: {resp.StatusCode}");
        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.GetProperty("status").GetString() ?? "UNKNOWN";
        var intent = doc.RootElement.TryGetProperty("intent", out var i) ? i.GetString() : null;
        return new PayPalOrderStatusResult(orderId, status, intent);
    }



    public async Task<PayPalRefundResult> RefundCaptureAsync(
        string captureId,
        decimal amount,
        string currency,
        string? note = null,
        string? merchantKey = null,
        CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            var simRefundId = $"SIM-REF-{Guid.NewGuid():N}"[..20];
            _logger.LogInformation("PayPal SIMULATE Refund capture {CaptureId} amount {Amount} -> {RefundId}", captureId, amount, simRefundId);
            return new PayPalRefundResult(simRefundId, "COMPLETED", true);
        }
        var token = await _accounts.GetAccessTokenAsync(merchantKey, ct);
        var body = new
        {
            amount = new { value = amount.ToString("F2"), currency_code = currency },
            note_to_payer = note
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v2/payments/captures/{captureId}/refund");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("PayPal-Request-Id", Guid.NewGuid().ToString());
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("PayPal Refund failed: {Status} {Body}", resp.StatusCode, json);
            throw new InvalidOperationException($"PayPal refund failed: {resp.StatusCode} {json}");
        }
        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetString() ?? captureId;
        var status = doc.RootElement.GetProperty("status").GetString() ?? "UNKNOWN";
        return new PayPalRefundResult(id, status, status is "COMPLETED" or "PENDING");
    }

}

public record PayPalOrderResult(string OrderId, string Status, string? ApproveUrl = null);
public record PayPalCaptureResult(string OrderId, string CaptureId, string Status, bool IsCompleted);
public record PayPalAuthorizeResult(string OrderId, string AuthorizationId, string Status, bool IsOk);
public record PayPalOrderStatusResult(string OrderId, string Status, string? Intent);
public record PayPalRefundResult(string RefundId, string Status, bool IsOk);
