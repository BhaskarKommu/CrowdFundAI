using System.Text.RegularExpressions;

namespace CrowdFund.Web.Security;

/// <summary>
/// For known search/social crawlers hitting /campaigns/{guid}, rewrite to the static SSR HTML snapshot
/// so meta tags are available without waiting for a Blazor circuit.
/// </summary>
public class CrawlerSsrMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly Regex CampaignPath = new(
        @"^/campaigns/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/?$",
        RegexOptions.Compiled);

    private static readonly string[] BotHints =
    {
        "googlebot", "bingbot", "slurp", "duckduckbot", "baiduspider",
        "facebookexternalhit", "facebot", "twitterbot", "linkedinbot",
        "embedly", "quora link preview", "showyoubot", "outbrain",
        "pinterest", "applebot", "semrushbot", "ahrefsbot", "discordbot",
        "whatsapp", "telegrambot", "skypeuripreview"
    };

    public CrawlerSsrMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            var path = context.Request.Path.Value ?? "";
            var m = CampaignPath.Match(path);
            if (m.Success)
            {
                var ua = context.Request.Headers.UserAgent.ToString();
                if (IsBot(ua))
                    context.Request.Path = $"/campaigns/{m.Groups[1].Value}/ssr";
            }
        }
        await _next(context);
    }

    private static bool IsBot(string ua)
    {
        if (string.IsNullOrEmpty(ua)) return false;
        var lower = ua.ToLowerInvariant();
        return BotHints.Any(h => lower.Contains(h));
    }
}
