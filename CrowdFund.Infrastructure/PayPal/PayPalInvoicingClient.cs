using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrowdFund.Infrastructure.PayPal;

/// <summary>PayPal Invoicing API v2 — create, send, cancel, remind (simulate-aware).</summary>
public class PayPalInvoicingClient
{
    private readonly HttpClient _http;
    private readonly PayPalOptions _opt;
    private readonly PayPalAccountService _accounts;
    private readonly ILogger<PayPalInvoicingClient> _log;

    public PayPalInvoicingClient(HttpClient http, IOptions<PayPalOptions> opt, PayPalAccountService accounts, ILogger<PayPalInvoicingClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _accounts = accounts;
        _log = log;
    }

    public bool IsSimulated => _accounts.IsSimulated;

    public async Task<PayPalInvoiceResult> CreateInvoiceAsync(
        string invoiceNumber,
        string recipientEmail,
        string? recipientName,
        decimal amount,
        string currency,
        string itemName,
        string? note,
        DateTime? dueAt,
        CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            var simInvoiceId = "SIM-INV-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            return new PayPalInvoiceResult(simInvoiceId, "DRAFT", null);
        }

        var token = await _accounts.GetAccessTokenAsync(null, ct);
        var body = new
        {
            detail = new
            {
                invoice_number = invoiceNumber,
                currency_code = currency,
                note = note,
                payment_term = dueAt.HasValue
                    ? new { term_type = "DUE_ON_DATE", due_date = dueAt.Value.ToString("yyyy-MM-dd") }
                    : new { term_type = "DUE_ON_RECEIPT", due_date = (string?)null }
            },
            primary_recipients = new[]
            {
                new
                {
                    billing_info = new
                    {
                        email_address = recipientEmail,
                        name = string.IsNullOrWhiteSpace(recipientName) ? null : new { full_name = recipientName }
                    }
                }
            },
            items = new[]
            {
                new
                {
                    name = itemName,
                    quantity = "1",
                    unit_amount = new { currency_code = currency, value = amount.ToString("F2") }
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl}/v2/invoicing/invoices");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("PayPal-Request-Id", Guid.NewGuid().ToString());
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PayPal create invoice failed: {resp.StatusCode} {json}");

        // Location header often has invoice id
        string? id = null;
        if (resp.Headers.Location != null)
            id = resp.Headers.Location.ToString().Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(id) && json.Contains("id"))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out var idEl))
                    id = idEl.GetString();
            }
            catch { }
        }
        id ??= "UNKNOWN";
        return new PayPalInvoiceResult(id, "DRAFT", null);
    }

    public async Task SendInvoiceAsync(string paypalInvoiceId, CancellationToken ct = default)
    {
        if (IsSimulated) return;
        await PostAsync($"{_opt.BaseUrl}/v2/invoicing/invoices/{paypalInvoiceId}/send", new { send_to_recipient = true }, ct);
    }

    public async Task CancelInvoiceAsync(string paypalInvoiceId, string reason, CancellationToken ct = default)
    {
        if (IsSimulated) return;
        await PostAsync($"{_opt.BaseUrl}/v2/invoicing/invoices/{paypalInvoiceId}/cancel",
            new { subject = "Invoice cancelled", note = reason, send_to_recipient = true }, ct);
    }

    public async Task RemindInvoiceAsync(string paypalInvoiceId, string? subject, string? note, CancellationToken ct = default)
    {
        if (IsSimulated) return;
        await PostAsync($"{_opt.BaseUrl}/v2/invoicing/invoices/{paypalInvoiceId}/remind",
            new { subject = subject ?? "Payment reminder", note = note ?? "Please pay your outstanding invoice." }, ct);
    }

    private async Task PostAsync(string url, object body, CancellationToken ct)
    {
        var token = await _accounts.GetAccessTokenAsync(null, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("PayPal invoicing POST {Url} -> {Status} {Body}", url, resp.StatusCode, json);
            throw new InvalidOperationException($"PayPal invoicing failed: {resp.StatusCode}");
        }
    }
}

public record PayPalInvoiceResult(string InvoiceId, string Status, string? Href);
