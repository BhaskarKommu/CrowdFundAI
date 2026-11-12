using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class GrowthEndpoints
{
    public static void MapGrowthEndpoints(this WebApplication app)
    {
        // Donor dashboard 2.0
        app.MapGet("/api/donor/dashboard", DonorDashboard).RequireAuthorization();
        app.MapPost("/api/donor/certificates/issue", IssueCertificate).RequireAuthorization();
        app.MapGet("/api/donor/certificates/{id:guid}/html", CertificateHtml);
        app.MapGet("/api/donor/preferences", GetPrefs).RequireAuthorization();
        app.MapPut("/api/donor/preferences", SavePrefs).RequireAuthorization();

        // Creator CRM-lite
        app.MapGet("/api/campaigns/{campaignId:guid}/crm/donors", CrmDonors).RequireAuthorization();
        app.MapPost("/api/campaigns/{campaignId:guid}/crm/blast", SendBlast).RequireAuthorization();
        app.MapPost("/api/campaigns/{campaignId:guid}/updates", ComposeUpdate).RequireAuthorization();
        app.MapGet("/api/campaigns/{campaignId:guid}/updates", ListUpdates);

        // SEO / short links
        app.MapGet("/api/campaigns/{id:guid}/seo", CampaignSeo);
        app.MapPost("/api/short-links", CreateShortLink);
        app.MapGet("/s/{code}", ResolveShortLink);

        // PWA push (demo log + preference storage)
        app.MapPost("/api/push/subscribe", PushSubscribe).RequireAuthorization();
        app.MapPost("/api/push/test", PushTest).RequireAuthorization();
        app.MapGet("/api/push/logs", PushLogs);
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static async Task<IResult> DonorDashboard(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http);
        if (uid == null) return Results.Unauthorized();

        var donations = await db.Donations.AsNoTracking()
            .Include(d => d.Campaign)
            .Where(d => d.DonorId == uid && d.Status == DonationStatus.Captured)
            .OrderByDescending(d => d.CapturedAt)
            .ToListAsync();

        var lifetime = donations.Sum(d => d.Amount);
        var byCategory = donations
            .Where(d => d.Campaign != null)
            .GroupBy(d => d.Campaign!.Category)
            .Select(g => new { category = g.Key, amount = g.Sum(x => x.Amount), count = g.Count() })
            .OrderByDescending(x => x.amount)
            .ToList();

        var byCampaign = donations
            .GroupBy(d => new { d.CampaignId, Title = d.Campaign!.Title })
            .Select(g => new
            {
                campaignId = g.Key.CampaignId,
                title = g.Key.Title,
                given = g.Sum(x => x.Amount),
                gifts = g.Count(),
                matched = g.Sum(x => x.MatchedAmount)
            })
            .OrderByDescending(x => x.given)
            .ToList();

        var certs = await db.ImpactCertificates.AsNoTracking()
            .Where(c => c.DonorId == uid)
            .OrderByDescending(c => c.IssuedAt)
            .Take(20)
            .ToListAsync();

        return Results.Ok(new
        {
            lifetimeGiven = lifetime,
            giftCount = donations.Count,
            categoryMix = byCategory,
            campaigns = byCampaign,
            certificates = certs,
            recent = donations.Take(10).Select(d => new
            {
                d.Id,
                d.Amount,
                d.Currency,
                d.CapturedAt,
                d.CampaignId,
                title = d.Campaign!.Title,
                d.MatchedAmount,
                d.NetToCampaign
            })
        });
    }

    private static async Task<IResult> IssueCertificate(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var donations = await db.Donations.AsNoTracking()
            .Include(d => d.Campaign)
            .Where(d => d.DonorId == uid && d.Status == DonationStatus.Captured)
            .ToListAsync();
        var lifetime = donations.Sum(d => d.Amount);
        if (lifetime <= 0)
            return Results.BadRequest(new { error = "No captured donations yet" });

        var cats = string.Join(", ", donations.Where(d => d.Campaign != null)
            .Select(d => d.Campaign!.Category).Distinct().Take(5));
        var cert = new ImpactCertificate
        {
            DonorId = uid,
            Title = "Lifetime impact certificate",
            LifetimeGiven = lifetime,
            Currency = donations.FirstOrDefault()?.Currency ?? "USD",
            BodyHtml = $"""
                <h1>Certificate of Impact</h1>
                <p>This certifies a lifetime of <strong>{lifetime:N2} {donations.First().Currency}</strong> in verified donations on CrowdFund AI.</p>
                <p>Categories supported: {System.Net.WebUtility.HtmlEncode(cats)}.</p>
                <p>Issued {DateTime.UtcNow:u}. Advisory record only — not a tax document unless paired with an official receipt.</p>
                """
        };
        db.ImpactCertificates.Add(cert);
        await db.SaveChangesAsync();
        return Results.Ok(cert);
    }

    private static async Task<IResult> CertificateHtml(Guid id, AppDbContext db)
    {
        var c = await db.ImpactCertificates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return Results.NotFound();
        var titleSafe = System.Net.WebUtility.HtmlEncode(c.Title);
        var html =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + titleSafe + "</title>" +
            "<style>body{font-family:Georgia,serif;max-width:720px;margin:2rem auto;padding:2rem;border:8px solid #6C5CE7;}" +
            "h1{color:#6C5CE7;text-align:center}</style></head><body>" +
            (c.BodyHtml ?? "") +
            "<p style=\"text-align:center\"><button onclick=\"window.print()\">Print / Save PDF</button></p>" +
            "</body></html>";
        return Results.Content(html, "text/html");
    }

    private static async Task<IResult> GetPrefs(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var p = await db.DonorPreferences.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid);
        return Results.Ok(p ?? new DonorPreference { UserId = uid });
    }

    private static async Task<IResult> SavePrefs([FromBody] DonorPreference req, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var p = await db.DonorPreferences.FirstOrDefaultAsync(x => x.UserId == uid);
        if (p == null)
        {
            p = new DonorPreference { UserId = uid };
            db.DonorPreferences.Add(p);
        }
        p.OptInCampaignUpdates = req.OptInCampaignUpdates;
        p.OptInCreatorMessages = req.OptInCreatorMessages;
        p.OptInPush = req.OptInPush;
        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(p);
    }

    private static async Task<IResult> CrmDonors(Guid campaignId, HttpContext http, AppDbContext db)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        if (campaign.CreatorId != Uid(http) && !http.User.IsInRole("Admin"))
            return Results.Forbid();

        var rows = await db.Donations.AsNoTracking()
            .Where(d => d.CampaignId == campaignId && d.Status == DonationStatus.Captured)
            .GroupBy(d => d.DonorId)
            .Select(g => new
            {
                donorId = g.Key,
                total = g.Sum(x => x.Amount),
                gifts = g.Count(),
                lastGift = g.Max(x => x.CapturedAt)
            })
            .OrderByDescending(x => x.total)
            .ToListAsync();

        var prefs = await db.DonorPreferences.AsNoTracking()
            .Where(p => rows.Select(r => r.donorId).Contains(p.UserId))
            .ToListAsync();
        var prefMap = prefs.ToDictionary(p => p.UserId);

        return Results.Ok(rows.Select(r => new
        {
            r.donorId,
            r.total,
            r.gifts,
            r.lastGift,
            optInMessages = r.donorId != null && prefMap.TryGetValue(r.donorId, out var pr) && pr.OptInCreatorMessages,
            optInUpdates = r.donorId == null || !prefMap.ContainsKey(r.donorId) || prefMap[r.donorId].OptInCampaignUpdates
        }));
    }

    private static async Task<IResult> SendBlast(
        Guid campaignId, [FromBody] BlastRequest req, HttpContext http, AppDbContext db)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        if (campaign.CreatorId != Uid(http) && !http.User.IsInRole("Admin"))
            return Results.Forbid();

        var donorIds = await db.Donations.AsNoTracking()
            .Where(d => d.CampaignId == campaignId && d.Status == DonationStatus.Captured && d.DonorId != null)
            .Select(d => d.DonorId!)
            .Distinct()
            .ToListAsync();
        var opted = await db.DonorPreferences.AsNoTracking()
            .Where(p => donorIds.Contains(p.UserId) && p.OptInCreatorMessages)
            .Select(p => p.UserId)
            .ToListAsync();

        var blast = new CreatorMessageBlast
        {
            CampaignId = campaignId,
            CreatorId = Uid(http)!,
            Subject = req.Subject?.Trim() ?? "Update from the campaign",
            Body = req.Body ?? "",
            RecipientCount = opted.Count
        };
        db.CreatorMessageBlasts.Add(blast);
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            blast.Id,
            blast.RecipientCount,
            message = "Recorded (email transport is a production hook). Only opt-in donors counted."
        });
    }

    private static async Task<IResult> ComposeUpdate(
        Guid campaignId, [FromBody] UpdateRequest req, HttpContext http, AppDbContext db)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        if (campaign.CreatorId != Uid(http) && !http.User.IsInRole("Admin"))
            return Results.Forbid();
        var u = new CampaignUpdate
        {
            CampaignId = campaignId,
            Title = req.Title?.Trim() ?? "Campaign update",
            Body = req.Body ?? "",
            Source = "Creator",
            ApprovedAt = DateTime.UtcNow
        };
        db.CampaignUpdates.Add(u);
        await db.SaveChangesAsync();
        return Results.Ok(u);
    }

    private static async Task<IResult> ListUpdates(Guid campaignId, AppDbContext db)
    {
        var list = await db.CampaignUpdates.AsNoTracking()
            .Where(u => u.CampaignId == campaignId)
            .OrderByDescending(u => u.CreatedAt)
            .Take(50)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> CampaignSeo(Guid id, AppDbContext db, HttpContext http)
    {
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return Results.NotFound();
        var baseUri = $"{http.Request.Scheme}://{http.Request.Host}";
        var url = $"{baseUri}/campaigns/{c.Id}";
        var desc = c.Story.Length > 160 ? c.Story[..160] + "…" : c.Story;
        object recipientObj;
        if (!string.IsNullOrEmpty(c.LocationCity) || c.Latitude.HasValue)
        {
            recipientObj = new
            {
                type = "Organization",
                name = c.Title,
                location = new
                {
                    type = "Place",
                    name = c.LocationLabel ?? c.LocationCity ?? c.Title,
                    address = new
                    {
                        type = "PostalAddress",
                        addressLocality = c.LocationCity,
                        addressRegion = c.LocationRegion,
                        addressCountry = c.LocationCountry
                    },
                    geo = c.Latitude == null ? null : new
                    {
                        type = "GeoCoordinates",
                        latitude = c.Latitude,
                        longitude = c.Longitude
                    }
                }
            };
        }
        else
            recipientObj = new { type = "Organization", name = c.Title };

        var jsonLd = new
        {
            context = "https://schema.org",
            type = "DonateAction",
            name = c.Title,
            description = desc,
            url,
            recipient = recipientObj
        };
        return Results.Ok(new
        {
            title = c.Title,
            description = desc,
            canonical = url,
            location = new {
                city = c.LocationCity,
                region = c.LocationRegion,
                country = c.LocationCountry,
                label = c.LocationLabel,
                latitude = c.Latitude,
                longitude = c.Longitude
            },
            geoMeta = c.LocationCountry == null ? null : new {
                region = string.IsNullOrEmpty(c.LocationRegion) ? c.LocationCountry : $"{c.LocationCountry}-{c.LocationRegion}",
                placename = c.LocationCity ?? c.LocationLabel,
                icbm = c.Latitude == null ? null : $"{c.Latitude},{c.Longitude}"
            },
            og = new
            {
                title = c.Title,
                description = desc,
                url,
                type = "website",
                image = $"{baseUri}/api/public/campaigns/{c.Id}/og.png"
            },
            twitter = new { card = "summary_large_image", title = c.Title, description = desc },
            jsonLd
        });
    }

    private static async Task<IResult> CreateShortLink([FromBody] ShortLinkRequest req, AppDbContext db)
    {
        var path = req.TargetPath ?? (req.CampaignId.HasValue ? $"/campaigns/{req.CampaignId}" : "/");
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var link = new ShortLink
        {
            Code = code,
            TargetPath = path.StartsWith("/") ? path : "/" + path,
            CampaignId = req.CampaignId
        };
        db.ShortLinks.Add(link);
        await db.SaveChangesAsync();
        return Results.Ok(new { link.Code, path = $"/s/{link.Code}", link.TargetPath });
    }

    private static async Task<IResult> ResolveShortLink(string code, AppDbContext db)
    {
        var link = await db.ShortLinks.FirstOrDefaultAsync(l => l.Code == code);
        if (link == null) return Results.NotFound();
        link.Hits++;
        await db.SaveChangesAsync();
        return Results.Redirect(link.TargetPath);
    }

    private static async Task<IResult> PushSubscribe([FromBody] PushSubRequest req, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var p = await db.DonorPreferences.FirstOrDefaultAsync(x => x.UserId == uid);
        if (p == null)
        {
            p = new DonorPreference { UserId = uid };
            db.DonorPreferences.Add(p);
        }
        p.OptInPush = true;
        p.PushEndpoint = req.Endpoint;
        p.PushP256dh = req.P256dh;
        p.PushAuth = req.Auth;
        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(new { ok = true, message = "Push subscription stored (delivery is a production Web Push hook)." });
    }

    private static async Task<IResult> PushTest([FromBody] PushTestRequest req, HttpContext http, AppDbContext db)
    {
        var log = new PushNotificationLog
        {
            UserId = Uid(http),
            EventType = req.EventType ?? "Test",
            Title = req.Title ?? "CrowdFund AI",
            Body = req.Body ?? "Test notification",
            CampaignId = req.CampaignId,
            Delivered = false // true when VAPID web-push is configured
        };
        db.PushNotificationLogs.Add(log);
        await db.SaveChangesAsync();
        return Results.Ok(log);
    }

    private static async Task<IResult> PushLogs(AppDbContext db)
    {
        var list = await db.PushNotificationLogs.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync();
        return Results.Ok(list);
    }

    /// <summary>Called from expense verify / goal logic to queue push events.</summary>
    public static async Task QueuePushAsync(AppDbContext db, string eventType, string title, string body, Guid? campaignId, IEnumerable<string>? userIds = null)
    {
        if (userIds != null)
        {
            foreach (var uid in userIds.Distinct())
            {
                db.PushNotificationLogs.Add(new PushNotificationLog
                {
                    UserId = uid,
                    EventType = eventType,
                    Title = title,
                    Body = body,
                    CampaignId = campaignId,
                    Delivered = false
                });
            }
        }
        else
        {
            db.PushNotificationLogs.Add(new PushNotificationLog
            {
                EventType = eventType,
                Title = title,
                Body = body,
                CampaignId = campaignId,
                Delivered = false
            });
        }
        await db.SaveChangesAsync();
    }

    public record BlastRequest(string? Subject, string? Body);
    public record UpdateRequest(string? Title, string? Body);
    public record ShortLinkRequest(string? TargetPath = null, Guid? CampaignId = null);
    public record PushSubRequest(string? Endpoint, string? P256dh, string? Auth);
    public record PushTestRequest(string? EventType, string? Title, string? Body, Guid? CampaignId);
}
