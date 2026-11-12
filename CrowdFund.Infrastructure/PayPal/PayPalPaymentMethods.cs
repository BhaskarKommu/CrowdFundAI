namespace CrowdFund.Infrastructure.PayPal;

/// <summary>PayPal funding / checkout method flags (merchant region may limit availability).</summary>
public static class PayPalFunding
{
    public const string PayPal = "paypal";
    public const string Card = "card";
    public const string PayLater = "paylater";
    public const string Credit = "credit";
    public const string Venmo = "venmo";
    public const string Ideal = "ideal";
    public const string Bancontact = "bancontact";
    public const string Blik = "blik";
    public const string Eps = "eps";
    public const string Giropay = "giropay";
    public const string MyBank = "mybank";
    public const string Przelewy24 = "p24";
    public const string Sepa = "sepa";
}

public class PayPalCheckoutOptions
{
    public const string SectionName = "PayPal:Checkout";

    /// <summary>Enable Smart Payment Buttons on donate UI.</summary>
    public bool SmartButtonsEnabled { get; set; } = true;
    /// <summary>Guest checkout (pay without PayPal account) where PayPal allows.</summary>
    public bool GuestCheckoutEnabled { get; set; } = true;
    public bool PayLaterEnabled { get; set; } = true;
    public bool CardFieldsEnabled { get; set; } = true;
    public bool VenmoEnabled { get; set; } = false;
    public bool VaultEnabled { get; set; } = true;
    public bool RecurringEnabled { get; set; } = true;
    /// <summary>Comma-separated disable-funding for JS SDK (e.g. leave empty to allow all).</summary>
    public string DisableFunding { get; set; } = "";
    /// <summary>Comma-separated enable-funding extras (paylater,venmo,etc.).</summary>
    public string EnableFunding { get; set; } = "paylater";
    public string Layout { get; set; } = "vertical"; // horizontal | vertical
    public string Color { get; set; } = "gold";
    public string Shape { get; set; } = "rect";
    public string Label { get; set; } = "paypal"; // paypal | checkout | pay | buynow
}

public enum PayPalPaymentMethodKind
{
    OneTimeWallet = 0,
    OneTimeCard = 1,
    PayLater = 2,
    ExpressCheckout = 3,
    Guest = 4,
    Recurring = 5,
    Vaulted = 6,
    Alternative = 7
}
