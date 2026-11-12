namespace CrowdFund.Core.Services;

/// <summary>
/// Display multi-currency conversion with explicit FX disclosure.
/// Settlement remains in the campaign's currency unless a separate settlement rail is configured.
/// </summary>
public class CurrencyOptions
{
    public const string Section = "Currency";
    public string SettlementCurrency { get; set; } = "USD";
    public string[] SupportedDisplayCurrencies { get; set; } = { "USD", "EUR", "GBP", "INR", "AED" };
}

public record MoneyDisplay(
    decimal Amount,
    string Currency,
    decimal? ConvertedAmount,
    string? DisplayCurrency,
    decimal? Rate,
    string Disclosure);

public class CurrencyService
{
    private readonly CurrencyOptions _opt;
    // Demo static rates vs USD (1 USD = X)
    private static readonly Dictionary<string, decimal> UsdTo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1m,
        ["EUR"] = 0.92m,
        ["GBP"] = 0.79m,
        ["INR"] = 83.50m,
        ["AED"] = 3.67m
    };

    public CurrencyService(CurrencyOptions? opt = null) => _opt = opt ?? new CurrencyOptions();

    public CurrencyOptions Options => _opt;

    public IReadOnlyList<string> Supported => _opt.SupportedDisplayCurrencies;

    public decimal GetRate(string from, string to)
    {
        from = from.ToUpperInvariant();
        to = to.ToUpperInvariant();
        if (from == to) return 1m;
        if (!UsdTo.ContainsKey(from) || !UsdTo.ContainsKey(to))
            return 0;
        // from -> USD -> to
        var inUsd = 1m / UsdTo[from];
        return inUsd * UsdTo[to];
    }

    public MoneyDisplay Display(decimal amount, string amountCurrency, string? displayCurrency)
    {
        amountCurrency = string.IsNullOrWhiteSpace(amountCurrency) ? "USD" : amountCurrency.ToUpperInvariant();
        displayCurrency = string.IsNullOrWhiteSpace(displayCurrency) ? null : displayCurrency.ToUpperInvariant();

        if (displayCurrency == null || displayCurrency == amountCurrency)
        {
            return new MoneyDisplay(amount, amountCurrency, null, null, null,
                "Amounts are shown in the campaign settlement currency. No FX conversion applied.");
        }

        var rate = GetRate(amountCurrency, displayCurrency);
        if (rate <= 0)
        {
            return new MoneyDisplay(amount, amountCurrency, null, null, null,
                $"Display currency {displayCurrency} is not supported for conversion. Showing {amountCurrency} only.");
        }

        var converted = Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);
        var disclosure =
            $"Indicative only: {amount:N2} {amountCurrency} ≈ {converted:N2} {displayCurrency} at demo rate {rate:N4}. " +
            $"Settlement and charges remain in {amountCurrency}. Rates are not live market quotes and are not a currency exchange offer.";

        return new MoneyDisplay(amount, amountCurrency, converted, displayCurrency, rate, disclosure);
    }

    public bool IsSupported(string currency) =>
        UsdTo.ContainsKey(currency.ToUpperInvariant());
}
