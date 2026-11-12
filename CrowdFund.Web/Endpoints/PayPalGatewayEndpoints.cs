using CrowdFund.Infrastructure.PayPal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CrowdFund.Web.Endpoints;

/// <summary>Admin payment gateway configuration &amp; connection status (secrets never returned).</summary>
public static class PayPalGatewayEndpoints
{
    public static void MapPayPalGatewayEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/payments/gateway", GetGateway).RequireAuthorization();
        app.MapPost("/api/admin/payments/gateway/test", TestConnection).RequireAuthorization();
        app.MapGet("/api/admin/payments/merchants", ListMerchants).RequireAuthorization();
        app.MapGet("/api/payments/gateway/public-config", PublicConfig);
    }

    private static IResult GetGateway(HttpContext http, PayPalAccountService accounts)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        return Results.Ok(accounts.GetStatus());
    }

    private static async Task<IResult> TestConnection(
        [FromQuery] string? merchantKey,
        HttpContext http,
        PayPalAccountService accounts)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var result = await accounts.TestConnectionAsync(merchantKey);
        return Results.Ok(result);
    }

    private static IResult ListMerchants(HttpContext http, PayPalAccountService accounts)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        return Results.Ok(accounts.GetStatus().Merchants);
    }

    private static IResult PublicConfig(IOptions<PayPalOptions> opts, PayPalAccountService accounts)
    {
        var o = opts.Value;
        // Safe for browser PayPal JS SDK if needed later
        return Results.Ok(new
        {
            mode = string.Equals(o.Mode, "live", StringComparison.OrdinalIgnoreCase) ? "live" : "sandbox",
            simulate = accounts.IsSimulated,
            clientId = accounts.IsSimulated ? null : o.ClientId, // ClientId is public for JS SDK
            brandName = o.BrandName,
            currencyDefault = "USD"
        });
    }
}
