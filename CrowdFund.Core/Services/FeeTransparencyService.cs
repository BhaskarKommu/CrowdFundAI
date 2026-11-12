namespace CrowdFund.Core.Services;

/// <summary>
/// Transparent fee breakdown for donate UX and Follow My Money.
/// Estimates are disclosed as estimates where processor fees vary.
/// </summary>
public class FeeOptions
{
    public const string Section = "Fees";
    /// <summary>Platform fee % of donation (excluding tip).</summary>
    public decimal PlatformFeePercent { get; set; } = 3.0m;
    /// <summary>Estimated payment processor % (e.g. PayPal).</summary>
    public decimal ProcessorFeePercent { get; set; } = 2.9m;
    /// <summary>Fixed processor fee in currency units.</summary>
    public decimal ProcessorFeeFixed { get; set; } = 0.30m;
    /// <summary>Whether tips go 100% to platform (true) or campaign (false).</summary>
    public bool TipsGoToPlatform { get; set; } = true;
}

public record FeeBreakdown(
    decimal GrossDonation,
    decimal TipAmount,
    decimal PlatformFee,
    decimal ProcessorFeeEstimate,
    decimal NetToCampaign,
    decimal TotalChargedToDonor,
    string Currency,
    string Disclaimer);

public class FeeTransparencyService
{
    private readonly FeeOptions _opt;

    public FeeTransparencyService(FeeOptions? opt = null) => _opt = opt ?? new FeeOptions();

    public FeeOptions Options => _opt;

    public FeeBreakdown Calculate(decimal donationAmount, decimal tipAmount = 0, string currency = "USD")
    {
        if (donationAmount < 0) donationAmount = 0;
        if (tipAmount < 0) tipAmount = 0;

        var platform = Math.Round(donationAmount * (_opt.PlatformFeePercent / 100m), 2, MidpointRounding.AwayFromZero);
        var processor = Math.Round(donationAmount * (_opt.ProcessorFeePercent / 100m) + _opt.ProcessorFeeFixed, 2, MidpointRounding.AwayFromZero);
        // Net to campaign: donation minus platform fee minus estimated processor (tip handling)
        var net = donationAmount - platform - processor;
        if (net < 0) net = 0;
        if (!_opt.TipsGoToPlatform)
            net += tipAmount;

        var totalCharged = donationAmount + tipAmount;
        var disclaimer = _opt.TipsGoToPlatform
            ? $"Platform fee {_opt.PlatformFeePercent}% supports operations. Tips go to CrowdFund AI. Processor fees are estimates and may vary by PayPal."
            : $"Platform fee {_opt.PlatformFeePercent}%. Tips are added to the campaign. Processor fees are estimates.";

        return new FeeBreakdown(donationAmount, tipAmount, platform, processor, net, totalCharged, currency, disclaimer);
    }
}
