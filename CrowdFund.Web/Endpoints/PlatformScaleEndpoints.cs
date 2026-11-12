using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class PlatformScaleEndpoints
{
    public static void MapPlatformScaleEndpoints(this WebApplication app)
    {
        // Jobs
        app.MapPost("/api/admin/jobs", EnqueueJob).RequireAuthorization();
        app.MapGet("/api/admin/jobs", ListJobs).RequireAuthorization();

        // Tenants / white-label
        app.MapGet("/api/tenants/{slug}", GetTenantPublic);
        app.MapPost("/api/admin/tenants", CreateTenant).RequireAuthorization();
        app.MapPut("/api/admin/tenants/{id:guid}/branding", UpdateBranding).RequireAuthorization();
        app.MapGet("/api/admin/tenants", ListTenants).RequireAuthorization();

        // Partner OAuth-style client credentials + public read API
        app.MapPost("/api/oauth/token", PartnerToken);
        app.MapPost("/api/admin/partners", CreatePartner).RequireAuthorization();
        app.MapGet("/api/partner/v1/campaigns", PartnerListCampaigns);
        app.MapGet("/api/partner/v1/campaigns/{id:guid}", PartnerGetCampaign);
        app.MapGet("/api/public/v1/campaigns", PublicListCampaigns);
        app.MapGet("/api/public/v1/campaigns/{id:guid}", PublicGetCampaign);

        // Webhooks register
        app.MapPost("/api/admin/webhooks", RegisterWebhook).RequireAuthorization();
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static async Task<IResult> EnqueueJob([FromBody] JobRequest req, HttpContext http, AppDbContext db, BackgroundJobQueue queue)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        if (!Enum.TryParse<BackgroundJobType>(req.JobType, true, out var type))
            return Results.BadRequest(new { error = "Unknown job type" });
        var job = queue.Enqueue(type, req.Payload, req.TenantId, req.CampaignId, req.RunAfter);
        db.BackgroundJobs.Add(job);
        await db.SaveChangesAsync();
        return Results.Ok(job);
    }

    private static async Task<IResult> ListJobs(HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var list = await db.BackgroundJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).Take(100).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> GetTenantPublic(string slug, AppDbContext db)
    {
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug && x.IsActive);
        if (t == null) return Results.NotFound();
        return Results.Ok(new
        {
            t.Slug, t.Name, t.PrimaryColor, t.AccentColor, t.LogoUrl,
            t.SupportEmail, t.TrustBadgeLabel, t.ShowPoweredBy, t.CustomDomain
        });
    }

    private static async Task<IResult> CreateTenant([FromBody] TenantRequest req, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var slug = (req.Slug ?? req.Name ?? "tenant").Trim().ToLowerInvariant()
            .Replace(" ", "-");
        if (await db.Tenants.AnyAsync(t => t.Slug == slug))
            return Results.BadRequest(new { error = "Slug taken" });
        var t = new Tenant
        {
            Slug = slug,
            Name = req.Name?.Trim() ?? slug,
            PrimaryColor = req.PrimaryColor ?? "#6C5CE7",
            AccentColor = req.AccentColor ?? "#A29BFE",
            LogoUrl = req.LogoUrl,
            SupportEmail = req.SupportEmail,
            CustomDomain = req.CustomDomain,
            TrustBadgeLabel = req.TrustBadgeLabel ?? "Verified by CrowdFund AI"
        };
        db.Tenants.Add(t);
        await db.SaveChangesAsync();
        return Results.Ok(t);
    }

    private static async Task<IResult> UpdateBranding(Guid id, [FromBody] TenantRequest req, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return Results.NotFound();
        if (!string.IsNullOrWhiteSpace(req.Name)) t.Name = req.Name.Trim();
        if (!string.IsNullOrWhiteSpace(req.PrimaryColor)) t.PrimaryColor = req.PrimaryColor;
        if (!string.IsNullOrWhiteSpace(req.AccentColor)) t.AccentColor = req.AccentColor;
        if (req.LogoUrl != null) t.LogoUrl = req.LogoUrl;
        if (req.SupportEmail != null) t.SupportEmail = req.SupportEmail;
        if (req.CustomDomain != null) t.CustomDomain = req.CustomDomain;
        if (req.TrustBadgeLabel != null) t.TrustBadgeLabel = req.TrustBadgeLabel;
        if (req.ShowPoweredBy.HasValue) t.ShowPoweredBy = req.ShowPoweredBy.Value;
        await db.SaveChangesAsync();
        return Results.Ok(t);
    }

    private static async Task<IResult> ListTenants(HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        return Results.Ok(await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync());
    }

    private static async Task<IResult> PartnerToken([FromBody] TokenRequest req, AppDbContext db)
    {
        if (string.IsNullOrWhiteSpace(req.ClientId) || string.IsNullOrWhiteSpace(req.ClientSecret))
            return Results.BadRequest(new { error = "client_id and client_secret required" });
        if (!string.Equals(req.GrantType, "client_credentials", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(req.GrantType))
            return Results.BadRequest(new { error = "grant_type must be client_credentials" });

        var client = await db.PartnerClients.FirstOrDefaultAsync(c => c.ClientId == req.ClientId && c.IsActive);
        if (client == null) return Results.Unauthorized();
        var hash = Hash(req.ClientSecret);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(hash), Encoding.UTF8.GetBytes(client.ClientSecretHash)))
            return Results.Unauthorized();

        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var token = new PartnerAccessToken
        {
            PartnerClientId = client.Id,
            TokenHash = Hash(raw),
            ExpiresAt = DateTime.UtcNow.AddHours(2)
        };
        db.PartnerAccessTokens.Add(token);
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            access_token = raw,
            token_type = "Bearer",
            expires_in = 7200,
            scope = client.Scopes
        });
    }

    private static async Task<IResult> CreatePartner([FromBody] PartnerRequest req, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var clientId = "cf_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var p = new PartnerClient
        {
            Name = req.Name?.Trim() ?? "Partner",
            ClientId = clientId,
            ClientSecretHash = Hash(secret),
            TenantId = req.TenantId,
            Scopes = req.Scopes ?? "campaigns:read donations:read trust:write"
        };
        db.PartnerClients.Add(p);
        await db.SaveChangesAsync();
        return Results.Ok(new { p.Id, p.ClientId, clientSecret = secret, p.Scopes, p.TenantId, note = "Store client_secret now; it is not shown again." });
    }

    private static async Task<(PartnerClient? client, IResult? error)> AuthPartner(HttpContext http, AppDbContext db)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return (null, Results.Unauthorized());
        var raw = header["Bearer ".Length..].Trim();
        var hash = Hash(raw);
        var token = await db.PartnerAccessTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.ExpiresAt > DateTime.UtcNow);
        if (token == null) return (null, Results.Unauthorized());
        var client = await db.PartnerClients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == token.PartnerClientId && c.IsActive);
        if (client == null) return (null, Results.Unauthorized());
        return (client, null);
    }

    private static async Task<IResult> PartnerListCampaigns(HttpContext http, AppDbContext db)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        var q = db.Campaigns.AsNoTracking().Where(c => c.Status == CampaignStatus.Published);
        if (client!.TenantId.HasValue)
            q = q.Where(c => c.TenantId == client.TenantId);
        var list = await q.OrderByDescending(c => c.PublishedAt).Take(100)
            .Select(c => new
            {
                c.Id, c.Title, c.Category, c.GoalAmount, c.RaisedAmount, c.Currency,
                c.TrustScore, c.TransparencyScore, c.DonorCount, c.Status, c.TenantId
            }).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> PartnerGetCampaign(Guid id, HttpContext http, AppDbContext db)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return Results.NotFound();
        if (client!.TenantId.HasValue && c.TenantId != client.TenantId)
            return Results.NotFound(); // hard isolation — no cross-tenant leak
        return Results.Ok(new
        {
            c.Id, c.Title, c.Story, c.Category, c.GoalAmount, c.RaisedAmount, c.Currency,
            c.TrustScore, c.TransparencyScore, c.DonorCount, c.Status, c.TenantId, c.PublishedAt
        });
    }

    private static async Task<IResult> PublicListCampaigns([FromQuery] string? tenant, AppDbContext db)
    {
        var q = db.Campaigns.AsNoTracking().Where(c => c.Status == CampaignStatus.Published);
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == tenant);
            if (t == null) return Results.Ok(Array.Empty<object>());
            q = q.Where(c => c.TenantId == t.Id);
        }
        var list = await q.OrderByDescending(c => c.PublishedAt).Take(50)
            .Select(c => new { c.Id, c.Title, c.Category, c.GoalAmount, c.RaisedAmount, c.Currency, c.TrustScore })
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> PublicGetCampaign(Guid id, AppDbContext db)
    {
        var c = await db.Campaigns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.Status == CampaignStatus.Published);
        if (c == null) return Results.NotFound();
        return Results.Ok(new
        {
            c.Id, c.Title, c.Story, c.Category, c.GoalAmount, c.RaisedAmount, c.Currency,
            c.TrustScore, c.TransparencyScore, c.DonorCount
        });
    }

    private static async Task<IResult> RegisterWebhook([FromBody] WebhookRequest req, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(req.Url)) return Results.BadRequest(new { error = "url required" });
        var w = new OutboundWebhook
        {
            Url = req.Url,
            EventType = req.EventType ?? "campaign.updated",
            TenantId = req.TenantId,
            CampaignId = req.CampaignId,
            PayloadJson = req.PayloadJson ?? "{}"
        };
        db.OutboundWebhooks.Add(w);
        await db.SaveChangesAsync();
        return Results.Ok(w);
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public record JobRequest(string JobType, Guid? CampaignId = null, Guid? TenantId = null, object? Payload = null, DateTime? RunAfter = null);
    public record TenantRequest(string? Slug, string? Name, string? PrimaryColor, string? AccentColor, string? LogoUrl, string? SupportEmail, string? CustomDomain, string? TrustBadgeLabel, bool? ShowPoweredBy = null);
    public record TokenRequest(string? ClientId, string? ClientSecret, string? GrantType = "client_credentials");
    public record PartnerRequest(string? Name, Guid? TenantId = null, string? Scopes = null);
    public record WebhookRequest(string Url, string? EventType = null, Guid? TenantId = null, Guid? CampaignId = null, string? PayloadJson = null);
}
