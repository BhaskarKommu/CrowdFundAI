using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrowdFund.Infrastructure.PayPal;

/// <summary>
/// PayPal Business account connection: OAuth2 client-credentials, token cache/refresh,
/// multi-merchant resolution, and connection health checks.
/// </summary>
public class PayPalAccountService
{
    private readonly HttpClient _http;
    private readonly PayPalOptions _opts;
    private readonly ILogger<PayPalAccountService> _log;
    private readonly ConcurrentDictionary<string, (string token, DateTime expiry)> _tokens = new();

    public PayPalAccountService(HttpClient http, IOptions<PayPalOptions> opts, ILogger<PayPalAccountService> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public PayPalOptions Options => _opts;

    public bool IsSimulated => _opts.Simulate || string.IsNullOrWhiteSpace(_opts.ClientId) || string.IsNullOrWhiteSpace(_opts.ClientSecret);

    public PayPalGatewayStatus GetStatus()
    {
        var merchants = _opts.Merchants
            .Where(m => m.IsActive)
            .Select(m => new PayPalMerchantSummary(
                m.Key,
                m.DisplayName,
                Mask(m.MerchantId),
                m.UseOwnCredentials,
                MaskId(m.ClientId ?? _opts.ClientId),
                m.PayoutEmail,
                m.Currency,
                m.IsActive))
            .ToList();

        return new PayPalGatewayStatus(
            Environment: string.Equals(_opts.Mode, "live", StringComparison.OrdinalIgnoreCase) ? "live" : "sandbox",
            Simulate: IsSimulated,
            ClientIdConfigured: !string.IsNullOrWhiteSpace(_opts.ClientId),
            ClientSecretConfigured: !string.IsNullOrWhiteSpace(_opts.ClientSecret),
            ClientIdMasked: MaskId(_opts.ClientId),
            BaseUrl: _opts.BaseUrl,
            BrandName: _opts.BrandName,
            WebhookIdConfigured: !string.IsNullOrWhiteSpace(_opts.WebhookId),
            ReturnUrl: _opts.ReturnUrl,
            CancelUrl: _opts.CancelUrl,
            DefaultMerchantKey: _opts.DefaultMerchantKey,
            MerchantCount: merchants.Count,
            Merchants: merchants,
            OAuth: "client_credentials",
            Notes: IsSimulated
                ? "Running in simulate mode — no live PayPal calls."
                : "REST credentials present — OAuth tokens requested from PayPal.");
    }

    /// <summary>OAuth 2.0 client_credentials; caches token and refreshes 60s before expiry.</summary>
    public async Task<string> GetAccessTokenAsync(string? merchantKey = null, CancellationToken ct = default)
    {
        if (IsSimulated)
            return "SIMULATED_TOKEN";

        var (clientId, clientSecret, cacheKey) = ResolveCredentials(merchantKey);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("PayPal ClientId/ClientSecret not configured for this merchant.");

        if (_tokens.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.expiry)
            return cached.token;

        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v1/oauth2/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        });

        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("PayPal OAuth failed for {CacheKey}: {Status} {Body}", cacheKey, resp.StatusCode, body);
            throw new InvalidOperationException($"PayPal OAuth failed: {resp.StatusCode}");
        }

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()
                    ?? throw new InvalidOperationException("PayPal OAuth response missing access_token");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 32000;
        // Refresh early
        var expiry = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
        _tokens[cacheKey] = (token, expiry);
        _log.LogInformation("PayPal OAuth token acquired for {CacheKey}, expires in ~{Sec}s", cacheKey, expiresIn);
        return token;
    }

    /// <summary>Invalidates cached token so next call forces refresh.</summary>
    public void InvalidateToken(string? merchantKey = null)
    {
        var (_, _, cacheKey) = ResolveCredentials(merchantKey);
        _tokens.TryRemove(cacheKey, out _);
    }

    public async Task<PayPalConnectionTestResult> TestConnectionAsync(string? merchantKey = null, CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            return new PayPalConnectionTestResult(
                true, "simulate", "Simulated connection OK (no network call).",
                null, DateTime.UtcNow);
        }

        try
        {
            InvalidateToken(merchantKey);
            var token = await GetAccessTokenAsync(merchantKey, ct);
            // Lightweight identity check
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_opts.BaseUrl}/v1/identity/oauth2/userinfo?schema=paypalv1.1");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode || (int)resp.StatusCode == 403)
            {
                // 403 can still mean token is valid but scope lacks identity — treat token success as connected
                return new PayPalConnectionTestResult(
                    true,
                    string.Equals(_opts.Mode, "live", StringComparison.OrdinalIgnoreCase) ? "live" : "sandbox",
                    resp.IsSuccessStatusCode
                        ? "Connected — OAuth token and identity endpoint OK."
                        : "Connected — OAuth token OK (identity scope limited).",
                    MaskId(ResolveCredentials(merchantKey).clientId),
                    DateTime.UtcNow);
            }

            return new PayPalConnectionTestResult(
                false,
                _opts.Mode,
                $"Token obtained but follow-up call failed: {(int)resp.StatusCode}",
                MaskId(ResolveCredentials(merchantKey).clientId),
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PayPal connection test failed");
            return new PayPalConnectionTestResult(false, _opts.Mode, ex.Message, null, DateTime.UtcNow);
        }
    }

    public (string? payeeMerchantId, string? merchantKey) GetPayee(string? merchantKey)
    {
        var m = _opts.ResolveMerchant(merchantKey);
        return (m?.MerchantId, m?.Key);
    }

    private (string clientId, string clientSecret, string cacheKey) ResolveCredentials(string? merchantKey)
    {
        var m = _opts.ResolveMerchant(merchantKey);
        if (m is { UseOwnCredentials: true })
            return (m.ClientId!, m.ClientSecret!, $"m:{m.Key}");
        return (_opts.ClientId, _opts.ClientSecret, "primary");
    }

    private static string? MaskId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Length <= 8) return "****";
        return id[..4] + "…" + id[^4];
    }

    private static string? Mask(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : (v.Length <= 4 ? "****" : v[..2] + "…" + v[^2..]);
}

public record PayPalGatewayStatus(
    string Environment,
    bool Simulate,
    bool ClientIdConfigured,
    bool ClientSecretConfigured,
    string? ClientIdMasked,
    string BaseUrl,
    string BrandName,
    bool WebhookIdConfigured,
    string ReturnUrl,
    string CancelUrl,
    string? DefaultMerchantKey,
    int MerchantCount,
    IReadOnlyList<PayPalMerchantSummary> Merchants,
    string OAuth,
    string Notes);

public record PayPalMerchantSummary(
    string Key,
    string DisplayName,
    string? MerchantIdMasked,
    bool UsesOwnCredentials,
    string? ClientIdMasked,
    string? PayoutEmail,
    string Currency,
    bool IsActive);

public record PayPalConnectionTestResult(
    bool Success,
    string Environment,
    string Message,
    string? ClientIdMasked,
    DateTime TestedAtUtc);
