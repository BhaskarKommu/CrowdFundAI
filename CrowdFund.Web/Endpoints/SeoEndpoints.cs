using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class SeoEndpoints
{
    public static void MapSeoEndpoints(this WebApplication app)
    {
        app.MapGet("/sitemap.xml", Sitemap);
        app.MapGet("/sitemap", Sitemap);
        app.MapGet("/api/public/campaigns/{id:guid}/og.png", OgPng);
        app.MapGet("/campaigns/{id:guid}/og.png", OgPng);
        // Static HTML snapshot for crawlers / link unfurlers (SSR-style)
        app.MapGet("/campaigns/{id:guid}/ssr", CampaignSsrHtml);
    }

    private static async Task<IResult> Sitemap(HttpContext http, AppDbContext db)
    {
        var baseUri = $"{http.Request.Scheme}://{http.Request.Host}";
        var campaigns = await db.Campaigns.AsNoTracking()
            .Where(c => c.Status == CampaignStatus.Published)
            .OrderByDescending(c => c.PublishedAt ?? c.CreatedAt)
            .Select(c => new { c.Id, c.PublishedAt, c.CreatedAt })
            .Take(5000)
            .ToListAsync();

        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var urlset = new XElement(ns + "urlset",
            new XElement(ns + "url",
                new XElement(ns + "loc", baseUri + "/"),
                new XElement(ns + "changefreq", "daily"),
                new XElement(ns + "priority", "1.0")),
            new XElement(ns + "url",
                new XElement(ns + "loc", baseUri + "/campaigns"),
                new XElement(ns + "changefreq", "hourly"),
                new XElement(ns + "priority", "0.9"))
        );

        foreach (var c in campaigns)
        {
            var lastmod = (c.PublishedAt ?? c.CreatedAt).ToString("yyyy-MM-dd");
            urlset.Add(new XElement(ns + "url",
                new XElement(ns + "loc", $"{baseUri}/campaigns/{c.Id}"),
                new XElement(ns + "lastmod", lastmod),
                new XElement(ns + "changefreq", "daily"),
                new XElement(ns + "priority", "0.8")));
            urlset.Add(new XElement(ns + "url",
                new XElement(ns + "loc", $"{baseUri}/campaigns/{c.Id}/ssr"),
                new XElement(ns + "lastmod", lastmod),
                new XElement(ns + "changefreq", "daily"),
                new XElement(ns + "priority", "0.7")));
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), urlset);
        return Results.Content(doc.ToString(), "application/xml", Encoding.UTF8);
    }

    private static async Task<IResult> OgPng(Guid id, AppDbContext db)
    {
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return Results.NotFound();
        var band = c.TrustScore >= 70 ? "High" : c.TrustScore >= 40 ? "Medium" : "Low";
        var sub = string.IsNullOrEmpty(c.LocationLabel)
            ? $"{c.Category} · Raised {c.RaisedAmount:N0} / {c.GoalAmount:N0} {c.Currency}"
            : $"{c.LocationLabel} · {c.Category} · {c.RaisedAmount:N0} {c.Currency}";
        var png = OgImageGenerator.Generate(c.Title, sub, c.TrustScore, band);
        return Results.File(png, "image/png");
    }

    private static async Task<IResult> CampaignSsrHtml(Guid id, HttpContext http, AppDbContext db)
    {
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return Results.NotFound();

        var baseUri = $"{http.Request.Scheme}://{http.Request.Host}";
        var canonical = $"{baseUri}/campaigns/{c.Id}";
        var ogImage = $"{baseUri}/api/public/campaigns/{c.Id}/og.png";
        var desc = (c.Story ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
        if (desc.Length > 160) desc = desc[..157] + "…";
        if (string.IsNullOrWhiteSpace(desc))
            desc = $"{c.Category} campaign · Trust {c.TrustScore} · {c.RaisedAmount:N0} {c.Currency} raised";

        var title = WebUtility.HtmlEncode(c.Title);
        var description = WebUtility.HtmlEncode(desc);
        var story = WebUtility.HtmlEncode(c.Story ?? "");
        var noindex = c.Status == CampaignStatus.Published
            ? ""
            : "<meta name=\"robots\" content=\"noindex,nofollow\" />";

        object recipient;
        if (!string.IsNullOrEmpty(c.LocationCity) || c.Latitude.HasValue)
        {
            var place = new Dictionary<string, object>
            {
                ["@type"] = "Place",
                ["name"] = c.LocationLabel ?? c.LocationCity ?? c.Title
            };
            if (!string.IsNullOrEmpty(c.LocationCountry))
            {
                place["address"] = new Dictionary<string, object?>
                {
                    ["@type"] = "PostalAddress",
                    ["addressLocality"] = c.LocationCity,
                    ["addressRegion"] = c.LocationRegion,
                    ["addressCountry"] = c.LocationCountry
                };
            }
            if (c.Latitude.HasValue && c.Longitude.HasValue)
            {
                place["geo"] = new Dictionary<string, object>
                {
                    ["@type"] = "GeoCoordinates",
                    ["latitude"] = c.Latitude.Value,
                    ["longitude"] = c.Longitude.Value
                };
            }
            recipient = new Dictionary<string, object>
            {
                ["@type"] = "Organization",
                ["name"] = c.Title,
                ["location"] = place
            };
        }
        else
        {
            recipient = new Dictionary<string, string>
            {
                ["@type"] = "Organization",
                ["name"] = c.Title
            };
        }

        var jsonLd = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "DonateAction",
            ["name"] = c.Title,
            ["description"] = desc,
            ["url"] = canonical,
            ["recipient"] = recipient
        });

        var geoTags = "";
        if (!string.IsNullOrEmpty(c.LocationCountry))
        {
            var region = string.IsNullOrEmpty(c.LocationRegion)
                ? c.LocationCountry
                : $"{c.LocationCountry}-{c.LocationRegion}";
            geoTags += $"  <meta name=\"geo.region\" content=\"{WebUtility.HtmlEncode(region)}\" />\n";
            geoTags += $"  <meta name=\"geo.placename\" content=\"{WebUtility.HtmlEncode(c.LocationCity ?? c.LocationLabel ?? "")}\" />\n";
            if (c.Latitude.HasValue && c.Longitude.HasValue)
                geoTags += $"  <meta name=\"ICBM\" content=\"{c.Latitude},{c.Longitude}\" />\n";
        }
        if (c.Latitude.HasValue && c.Longitude.HasValue)
        {
            var lat = c.Latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lon = c.Longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            geoTags += $"  <meta property=\"place:location:latitude\" content=\"{lat}\" />\n";
            geoTags += $"  <meta property=\"place:location:longitude\" content=\"{lon}\" />\n";
        }
        var locLine = string.IsNullOrEmpty(c.LocationLabel)
            ? ""
            : $"  <p>📍 {WebUtility.HtmlEncode(c.LocationLabel)}</p>\n";

        var html =
            "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n" +
            "  <meta charset=\"utf-8\" />\n" +
            "  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n" +
            $"  {noindex}\n" +
            $"  <title>{title} — CrowdFund AI</title>\n" +
            $"  <meta name=\"description\" content=\"{description}\" />\n" +
            $"  <link rel=\"canonical\" href=\"{canonical}\" />\n" +
            "  <meta property=\"og:type\" content=\"website\" />\n" +
            "  <meta property=\"og:site_name\" content=\"CrowdFund AI\" />\n" +
            $"  <meta property=\"og:title\" content=\"{title} — CrowdFund AI\" />\n" +
            $"  <meta property=\"og:description\" content=\"{description}\" />\n" +
            $"  <meta property=\"og:url\" content=\"{canonical}\" />\n" +
            $"  <meta property=\"og:image\" content=\"{ogImage}\" />\n" +
            "  <meta property=\"og:image:width\" content=\"1200\" />\n" +
            "  <meta property=\"og:image:height\" content=\"630\" />\n" +
            "  <meta property=\"og:image:type\" content=\"image/png\" />\n" +
            "  <meta name=\"twitter:card\" content=\"summary_large_image\" />\n" +
            $"  <meta name=\"twitter:title\" content=\"{title} — CrowdFund AI\" />\n" +
            $"  <meta name=\"twitter:description\" content=\"{description}\" />\n" +
            $"  <meta name=\"twitter:image\" content=\"{ogImage}\" />\n" +
            geoTags +
            $"  <script type=\"application/ld+json\">{jsonLd}</script>\n" +
            "  <style>body{font-family:system-ui,sans-serif;margin:0;background:#fafafe;color:#222}" +
            ".wrap{max-width:720px;margin:0 auto;padding:2rem}" +
            ".badge{display:inline-block;background:#6C5CE7;color:#fff;padding:.25rem .6rem;border-radius:999px;font-size:.85rem}" +
            "a.btn{display:inline-block;margin-top:1rem;background:#6C5CE7;color:#fff;padding:.75rem 1.25rem;border-radius:10px;text-decoration:none}</style>\n" +
            "</head>\n<body>\n<div class=\"wrap\">\n" +
            $"  <p><span class=\"badge\">{WebUtility.HtmlEncode(c.Category)}</span> " +
            $"<span class=\"badge\">Trust {c.TrustScore}</span></p>\n" +
            $"  <h1>{title}</h1>\n" +
            locLine +
            $"  <p><strong>{c.RaisedAmount:N0}</strong> raised of {c.GoalAmount:N0} {WebUtility.HtmlEncode(c.Currency)} · {c.DonorCount} donors</p>\n" +
            $"  <p style=\"white-space:pre-wrap\">{story}</p>\n" +
            $"  <p><a class=\"btn\" href=\"{canonical}\">View campaign / donate</a></p>\n" +
            $"  <p style=\"color:#666;font-size:.9rem\">Static HTML snapshot for search and social crawlers. Interactive app: <a href=\"{canonical}\">{canonical}</a></p>\n" +
            "</div>\n</body>\n</html>\n";

        return Results.Content(html, "text/html; charset=utf-8");
    }
}
