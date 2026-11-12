namespace CrowdFund.Infrastructure.PayPal;

/// <summary>
/// PayPal Business account / payment gateway settings.
/// Bind from configuration section "PayPal". Secrets via env / user-secrets / Key Vault only.
/// </summary>
public class PayPalOptions
{
    public const string SectionName = "PayPal";

    /// <summary>Primary REST app Client ID (sandbox or live).</summary>
    public string ClientId { get; set; } = "";
    /// <summary>Primary REST app Client Secret — never log or return to clients.</summary>
    public string ClientSecret { get; set; } = "";
    /// <summary>sandbox | live</summary>
    public string Mode { get; set; } = "sandbox";
    public string WebhookId { get; set; } = "";
    /// <summary>When true (or when ClientId empty) use local simulation for offline demos.</summary>
    public bool Simulate { get; set; } = true;

    public string ReturnUrl { get; set; } = "https://localhost:5001/donate/return";
    public string CancelUrl { get; set; } = "https://localhost:5001/donate/cancel";
    public string BrandName { get; set; } = "CrowdFund AI";

    /// <summary>Optional default merchant key from <see cref="Merchants"/>.</summary>
    public string? DefaultMerchantKey { get; set; }

    /// <summary>
    /// Additional merchant / business accounts (marketplace or multi-entity).
    /// Each may use platform credentials with a PayPal Merchant Id (payee), or own REST app credentials.
    /// </summary>
    public List<PayPalMerchantAccountOptions> Merchants { get; set; } = new();

    public string BaseUrl => string.Equals(Mode, "live", StringComparison.OrdinalIgnoreCase)
        ? "https://api-m.paypal.com"
        : "https://api-m.sandbox.paypal.com";

    public bool HasPrimaryCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public PayPalMerchantAccountOptions? ResolveMerchant(string? merchantKey)
    {
        if (Merchants.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(merchantKey))
            return Merchants.FirstOrDefault(m =>
                string.Equals(m.Key, merchantKey, StringComparison.OrdinalIgnoreCase) && m.IsActive);
        if (!string.IsNullOrWhiteSpace(DefaultMerchantKey))
            return Merchants.FirstOrDefault(m =>
                string.Equals(m.Key, DefaultMerchantKey, StringComparison.OrdinalIgnoreCase) && m.IsActive);
        return Merchants.FirstOrDefault(m => m.IsActive);
    }
}

public class PayPalMerchantAccountOptions
{
    /// <summary>Stable key used in APIs (e.g. "primary", "ngo-trust").</summary>
    public string Key { get; set; } = "primary";
    public string DisplayName { get; set; } = "Primary";
    /// <summary>PayPal Merchant ID / payer id for payee object on orders (optional).</summary>
    public string? MerchantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? PayoutEmail { get; set; }
    public string Currency { get; set; } = "USD";
    public bool IsActive { get; set; } = true;
    /// <summary>When true, use this account's ClientId/Secret instead of primary.</summary>
    public bool UseOwnCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
