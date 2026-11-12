using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class GeoEndpoints
{
    public static void MapGeoEndpoints(this WebApplication app)
    {
        app.MapGet("/api/campaigns/near", NearCampaigns);
        app.MapGet("/api/campaigns/by-location", ByLocation);
        app.MapGet("/api/geo/countries", () => Results.Ok(CommonCountries));
    }

    private static readonly object[] CommonCountries =
    {
        new { code = "IN", name = "India" },
        new { code = "US", name = "United States" },
        new { code = "GB", name = "United Kingdom" },
        new { code = "CA", name = "Canada" },
        new { code = "AU", name = "Australia" },
        new { code = "DE", name = "Germany" },
        new { code = "FR", name = "France" },
        new { code = "SG", name = "Singapore" },
        new { code = "AE", name = "United Arab Emirates" },
        new { code = "NG", name = "Nigeria" },
        new { code = "KE", name = "Kenya" },
        new { code = "ZA", name = "South Africa" },
        new { code = "BR", name = "Brazil" },
        new { code = "MX", name = "Mexico" },
        new { code = "PH", name = "Philippines" },
        new { code = "BD", name = "Bangladesh" },
        new { code = "PK", name = "Pakistan" },
        new { code = "NP", name = "Nepal" },
        new { code = "LK", name = "Sri Lanka" },
        new { code = "OTHER", name = "Other" }
    };

    private static async Task<IResult> NearCampaigns(
        [FromQuery] double lat,
        [FromQuery] double lng,
        [FromQuery] double radiusKm = 50,
        [FromQuery] string? category = null,
        AppDbContext db = null!)
    {
        if (!GeoService.ValidCoords(lat, lng))
            return Results.BadRequest(new { error = "lat/lng required and must be valid" });
        radiusKm = Math.Clamp(radiusKm, 1, 2000);

        var q = db.Campaigns.AsNoTracking()
            .Where(c => c.Status == CampaignStatus.Published
                        && c.Latitude != null && c.Longitude != null);
        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(c => c.Category == category);

        // Coarse bounding box then precise distance
        var deg = radiusKm / 111.0;
        var minLat = lat - deg;
        var maxLat = lat + deg;
        var minLng = lng - deg / Math.Max(0.2, Math.Cos(lat * Math.PI / 180.0));
        var maxLng = lng + deg / Math.Max(0.2, Math.Cos(lat * Math.PI / 180.0));

        var candidates = await q
            .Where(c => c.Latitude >= minLat && c.Latitude <= maxLat
                        && c.Longitude >= minLng && c.Longitude <= maxLng)
            .OrderByDescending(c => c.PublishedAt)
            .Take(200)
            .ToListAsync();

        var ranked = candidates
            .Select(c =>
            {
                var d = GeoService.DistanceKm(lat, lng, c.Latitude!.Value, c.Longitude!.Value);
                return new { campaign = c, distanceKm = Math.Round(d, 1) };
            })
            .Where(x => x.distanceKm <= radiusKm)
            .OrderBy(x => x.distanceKm)
            .Take(50)
            .Select(x => new
            {
                x.campaign.Id,
                x.campaign.Title,
                x.campaign.Category,
                x.campaign.TrustScore,
                x.campaign.RaisedAmount,
                x.campaign.GoalAmount,
                x.campaign.Currency,
                x.campaign.LocationCity,
                x.campaign.LocationRegion,
                x.campaign.LocationCountry,
                x.campaign.LocationLabel,
                x.campaign.Latitude,
                x.campaign.Longitude,
                x.distanceKm
            })
            .ToList();

        return Results.Ok(new { lat, lng, radiusKm, count = ranked.Count, campaigns = ranked });
    }

    private static async Task<IResult> ByLocation(
        [FromQuery] string? country = null,
        [FromQuery] string? city = null,
        [FromQuery] string? region = null,
        [FromQuery] string? category = null,
        AppDbContext db = null!)
    {
        var q = db.Campaigns.AsNoTracking().Where(c => c.Status == CampaignStatus.Published);
        if (!string.IsNullOrWhiteSpace(country))
        {
            var cc = country.Trim().ToUpperInvariant();
            q = q.Where(c => c.LocationCountry == cc);
        }
        if (!string.IsNullOrWhiteSpace(city))
        {
            var cty = city.Trim();
            q = q.Where(c => c.LocationCity != null && c.LocationCity.Contains(cty));
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            var r = region.Trim();
            q = q.Where(c => c.LocationRegion != null && c.LocationRegion.Contains(r));
        }
        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(c => c.Category == category);

        var list = await q.OrderByDescending(c => c.PublishedAt).Take(100)
            .Select(c => new
            {
                c.Id, c.Title, c.Category, c.TrustScore, c.RaisedAmount, c.GoalAmount, c.Currency,
                c.LocationCity, c.LocationRegion, c.LocationCountry, c.LocationLabel, c.Latitude, c.Longitude
            }).ToListAsync();
        return Results.Ok(new { country, city, region, count = list.Count, campaigns = list });
    }
}
