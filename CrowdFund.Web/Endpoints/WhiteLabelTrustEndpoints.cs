using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrowdFund.Ai.Orchestration;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Interfaces;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

/// <summary>
/// White-label Trust API for other fundraising platforms.
/// OAuth client_credentials (same partner clients). Tenant isolation enforced.
/// </summary>
public static class WhiteLabelTrustEndpoints
{
    public static void MapWhiteLabelTrustEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/trust/v1").WithTags("WhiteLabelTrust");

        v1.MapGet("/health", () => Results.Ok(new
        {
            service = "CrowdFund AI White-label Trust API",
            version = "1.0",
            auth = "Bearer token from POST /api/oauth/token (client_credentials)",
            disclaimer = "Advisory risk indicators only — not a guarantee of legitimacy or legal certification."
        }));

        // Register / upsert external campaign for trust assessment
        v1.MapPost("/campaigns", UpsertCampaign);
        v1.MapGet("/campaigns/{id:guid}", GetCampaign);
        v1.MapGet("/campaigns/by-ref/{externalRef}", GetByExternalRef);

        // Documents for verification
        v1.MapPost("/campaigns/{id:guid}/documents", UploadDocument).DisableAntiforgery();

        // Run trust assessment
        v1.MapPost("/campaigns/{id:guid}/evaluate", Evaluate);

        // Read trust result
        v1.MapGet("/campaigns/{id:guid}/trust", GetTrust);

        // Brandable badge
        v1.MapGet("/campaigns/{id:guid}/badge.json", BadgeJson);
        v1.MapGet("/campaigns/{id:guid}/badge.svg", BadgeSvg);

        // Webhook registration for trust events
        v1.MapPost("/webhooks", RegisterWebhook);

        // OpenAPI-ish discovery
        v1.MapGet("/openapi-lite", () => Results.Ok(new
        {
            paths = new[]
            {
                "POST /api/trust/v1/campaigns",
                "POST /api/trust/v1/campaigns/{id}/documents",
                "POST /api/trust/v1/campaigns/{id}/evaluate",
                "GET  /api/trust/v1/campaigns/{id}/trust",
                "GET  /api/trust/v1/campaigns/{id}/badge.svg",
                "POST /api/trust/v1/webhooks",
                "POST /api/oauth/token"
            }
        }));
    }

    private static async Task<(PartnerClient? client, IResult? error)> AuthPartner(HttpContext http, AppDbContext db)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return (null, Results.Json(new { error = "unauthorized", message = "Bearer token required" }, statusCode: 401));
        var raw = header["Bearer ".Length..].Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        var token = await db.PartnerAccessTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.ExpiresAt > DateTime.UtcNow);
        if (token == null)
            return (null, Results.Json(new { error = "unauthorized" }, statusCode: 401));
        var client = await db.PartnerClients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == token.PartnerClientId && c.IsActive);
        if (client == null)
            return (null, Results.Json(new { error = "unauthorized" }, statusCode: 401));
        if (!client.Scopes.Contains("trust", StringComparison.OrdinalIgnoreCase)
            && !client.Scopes.Contains("campaigns", StringComparison.OrdinalIgnoreCase))
            return (null, Results.Json(new { error = "forbidden", message = "Scope trust:write or campaigns:read required" }, statusCode: 403));
        return (client, null);
    }

    private static async Task<IResult> UpsertCampaign([FromBody] TrustCampaignRequest req, HttpContext http, AppDbContext db)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        if (string.IsNullOrWhiteSpace(req.Title))
            return Results.BadRequest(new { error = "title required" });

        Campaign? campaign = null;
        if (!string.IsNullOrWhiteSpace(req.ExternalRef))
        {
            campaign = await db.Campaigns.FirstOrDefaultAsync(c =>
                c.ExternalRef == req.ExternalRef
                && c.TenantId == client!.TenantId
                && c.IsExternalTrustOnly);
        }

        if (campaign == null)
        {
            campaign = new Campaign
            {
                CreatorId = client!.ClientId, // synthetic owner = partner client
                Title = req.Title.Trim(),
                Story = req.Story ?? "",
                Category = string.IsNullOrWhiteSpace(req.Category) ? "Other" : req.Category!,
                GoalAmount = req.GoalAmount <= 0 ? 1 : req.GoalAmount,
                Currency = string.IsNullOrWhiteSpace(req.Currency) ? "USD" : req.Currency!.ToUpperInvariant(),
                Status = CampaignStatus.UnderReview,
                TenantId = client.TenantId,
                ExternalRef = req.ExternalRef,
                ExternalPlatform = req.PlatformName ?? client.Name,
                IsExternalTrustOnly = true,
                PublishedAt = null
            };
            db.Campaigns.Add(campaign);
        }
        else
        {
            campaign.Title = req.Title.Trim();
            if (req.Story != null) campaign.Story = req.Story;
            if (req.Category != null) campaign.Category = req.Category;
            if (req.GoalAmount > 0) campaign.GoalAmount = req.GoalAmount;
            if (req.Currency != null) campaign.Currency = req.Currency.ToUpperInvariant();
            if (req.RaisedAmount.HasValue) campaign.RaisedAmount = req.RaisedAmount.Value;
        }

        if (req.RaisedAmount.HasValue && campaign.Id != Guid.Empty)
            campaign.RaisedAmount = req.RaisedAmount.Value;

        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            campaignId = campaign.Id,
            campaign.ExternalRef,
            campaign.TenantId,
            campaign.Status,
            links = new
            {
                evaluate = $"/api/trust/v1/campaigns/{campaign.Id}/evaluate",
                trust = $"/api/trust/v1/campaigns/{campaign.Id}/trust",
                badgeSvg = $"/api/trust/v1/campaigns/{campaign.Id}/badge.svg"
            }
        });
    }

    private static async Task<IResult> GetCampaign(Guid id, HttpContext http, AppDbContext db)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        var c = await FindScoped(db, client!, id);
        if (c == null) return Results.NotFound();
        return Results.Ok(MapCampaign(c));
    }

    private static async Task<IResult> GetByExternalRef(string externalRef, HttpContext http, AppDbContext db)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        var q = db.Campaigns.AsNoTracking().Where(c => c.ExternalRef == externalRef && c.IsExternalTrustOnly);
        if (client!.TenantId.HasValue)
            q = q.Where(c => c.TenantId == client.TenantId);
        var c = await q.FirstOrDefaultAsync();
        if (c == null) return Results.NotFound();
        return Results.Ok(MapCampaign(c));
    }

    private static async Task<IResult> UploadDocument(
        Guid id, HttpContext http, AppDbContext db, IFileStore files)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        var campaign = await FindScopedTracked(db, client!, id);
        if (campaign == null) return Results.NotFound();

        if (!http.Request.HasFormContentType)
            return Results.BadRequest(new { error = "multipart form required (file, docType)" });
        var form = await http.Request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0)
            return Results.BadRequest(new { error = "file required" });
        var docType = form["docType"].ToString();
        if (string.IsNullOrWhiteSpace(docType)) docType = "other";

        await using var stream = file.OpenReadStream();
        var path = await files.SaveAsync(stream, file.FileName, file.ContentType ?? "application/octet-stream");
        var doc = new CampaignDocument
        {
            CampaignId = campaign.Id,
            FileName = file.FileName,
            ContentType = file.ContentType ?? "application/octet-stream",
            StoredPath = path,
            DocType = docType
        };
        db.CampaignDocuments.Add(doc);
        await db.SaveChangesAsync();
        return Results.Ok(new { documentId = doc.Id, doc.DocType, doc.FileName });
    }

    private static async Task<IResult> Evaluate(
        Guid id, HttpContext http, AppDbContext db,
        VerificationOrchestrator orchestrator, LedgerService ledger)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        var campaign = await db.Campaigns.Include(c => c.Documents).FirstOrDefaultAsync(c => c.Id == id);
        if (campaign == null) return Results.NotFound();
        if (!ScopedOk(client!, campaign)) return Results.NotFound();

        var expenses = await db.Expenses.Include(e => e.Evidences).Where(e => e.CampaignId == id).ToListAsync();
        var donations = await db.Donations.Where(d => d.CampaignId == id).ToListAsync();
        var history = await db.Campaigns
            .Where(c => c.ExternalPlatform == campaign.ExternalPlatform && c.Id != id)
            .Take(10).ToListAsync();

        if (campaign.Status == CampaignStatus.Draft)
            campaign.Status = CampaignStatus.UnderReview;

        var (assessment, flags, run) = await orchestrator.VerifyAsync(
            campaign, campaign.Documents.ToList(), expenses, donations, history);

        db.VerificationRuns.Add(run);
        foreach (var f in flags)
        {
            var exists = await db.FraudFlags.AnyAsync(x =>
                x.CampaignId == id && x.Rule == f.Rule && x.ExpenseId == f.ExpenseId && x.Status == "Open");
            if (!exists) db.FraudFlags.Add(f);
        }

        var prev = await db.AuditLedger.Where(e => e.CampaignId == id)
            .OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync() ?? "";
        db.AuditLedger.Add(ledger.CreateEntry(id, "TrustApiEvaluate", new
        {
            source = "white_label_trust_api",
            partner = client!.ClientId,
            assessment.OverallScore,
            assessment.Band
        }, prev));

        // Queue webhook
        var hooks = await db.OutboundWebhooks
            .Where(w => !w.Delivered && w.EventType == "trust.updated" &&
                        (w.TenantId == null || w.TenantId == client.TenantId))
            .ToListAsync();
        foreach (var h in hooks.Where(h => h.CampaignId == null || h.CampaignId == id).Take(5))
        {
            // refresh payload for delivery worker
            h.PayloadJson = JsonSerializer.Serialize(new
            {
                eventType = "trust.updated",
                campaignId = id,
                externalRef = campaign.ExternalRef,
                trustScore = campaign.TrustScore,
                band = assessment.Band,
                at = DateTime.UtcNow
            });
            h.Delivered = false;
            h.Attempts = 0;
        }
        // Always enqueue a tenant webhook job marker
        db.BackgroundJobs.Add(new BackgroundJob
        {
            JobType = BackgroundJobType.WebhookDelivery,
            CampaignId = id,
            TenantId = client.TenantId,
            PayloadJson = JsonSerializer.Serialize(new { eventType = "trust.updated", campaignId = id })
        });

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            campaignId = id,
            externalRef = campaign.ExternalRef,
            trustScore = assessment.OverallScore,
            band = assessment.Band,
            recommendedAction = assessment.RecommendedAction,
            subScores = assessment.SubScores,
            findings = assessment.Findings,
            missing = assessment.MissingInformation,
            flags = flags.Select(f => new { f.Rule, f.Severity, f.Explanation }),
            disclaimer = assessment.Disclaimer
                ?? "Advisory risk indicators only — not a guarantee of legitimacy.",
            badge = new
            {
                svg = $"/api/trust/v1/campaigns/{id}/badge.svg",
                json = $"/api/trust/v1/campaigns/{id}/badge.json"
            }
        });
    }

    private static async Task<IResult> GetTrust(Guid id, HttpContext http, AppDbContext db)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        var c = await FindScoped(db, client!, id);
        if (c == null) return Results.NotFound();
        var run = await db.VerificationRuns.AsNoTracking()
            .Where(r => r.CampaignId == id)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync();
        var flags = await db.FraudFlags.AsNoTracking()
            .Where(f => f.CampaignId == id && f.Status == "Open")
            .Select(f => new { f.Rule, f.Severity, f.Explanation })
            .ToListAsync();
        return Results.Ok(new
        {
            campaignId = c.Id,
            c.ExternalRef,
            c.TrustScore,
            c.TransparencyScore,
            c.TrustExplanationJson,
            lastRunId = run?.Id,
            lastRunAt = run?.CompletedAt,
            openFlags = flags,
            disclaimer = "Advisory risk indicators only — not a guarantee of legitimacy."
        });
    }

    private static async Task<IResult> BadgeJson(Guid id, HttpContext http, AppDbContext db)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        var c = await FindScoped(db, client!, id);
        if (c == null) return Results.NotFound();
        var tenant = client!.TenantId.HasValue
            ? await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == client.TenantId)
            : null;
        var band = c.TrustScore >= 70 ? "High" : c.TrustScore >= 40 ? "Medium" : "Low";
        return Results.Ok(new
        {
            campaignId = c.Id,
            c.ExternalRef,
            trustScore = c.TrustScore,
            trustBand = band,
            transparencyScore = c.TransparencyScore,
            brand = tenant?.TrustBadgeLabel ?? "Verified by CrowdFund AI",
            primaryColor = tenant?.PrimaryColor ?? "#6C5CE7",
            platform = c.ExternalPlatform,
            disclaimer = "Advisory only"
        });
    }

    private static async Task<IResult> BadgeSvg(Guid id, HttpContext http, AppDbContext db)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        var c = await FindScoped(db, client!, id);
        if (c == null) return Results.NotFound();
        var tenant = client!.TenantId.HasValue
            ? await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == client.TenantId)
            : null;
        var label = tenant?.TrustBadgeLabel ?? "Trust by CrowdFund AI";
        var color = tenant?.PrimaryColor ?? "#6C5CE7";
        var band = c.TrustScore >= 70 ? "High" : c.TrustScore >= 40 ? "Medium" : "Low";
        var bandColor = band switch { "High" => "#2E7D32", "Medium" => "#F9A825", _ => "#C62828" };
        var title = System.Net.WebUtility.HtmlEncode(c.Title.Length > 40 ? c.Title[..40] + "…" : c.Title);
        var svg = $"""
<svg xmlns="http://www.w3.org/2000/svg" width="440" height="100" role="img" aria-label="{System.Net.WebUtility.HtmlEncode(label)}">
  <rect width="440" height="100" rx="12" fill="#FAFAFE" stroke="{color}" stroke-width="2"/>
  <text x="20" y="32" font-family="system-ui,sans-serif" font-size="14" fill="{color}" font-weight="700">{System.Net.WebUtility.HtmlEncode(label)}</text>
  <text x="20" y="56" font-family="system-ui,sans-serif" font-size="13" fill="#333">{title}</text>
  <text x="20" y="80" font-family="system-ui,sans-serif" font-size="12" fill="{bandColor}">Trust {c.TrustScore} · {band} · Transparency {c.TransparencyScore}</text>
</svg>
""";
        return Results.Content(svg, "image/svg+xml");
    }

    private static async Task<IResult> RegisterWebhook([FromBody] TrustWebhookRequest req, HttpContext http, AppDbContext db)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        if (string.IsNullOrWhiteSpace(req.Url))
            return Results.BadRequest(new { error = "url required" });
        var w = new OutboundWebhook
        {
            Url = req.Url.Trim(),
            EventType = req.EventType ?? "trust.updated",
            TenantId = client!.TenantId,
            CampaignId = req.CampaignId,
            PayloadJson = "{}"
        };
        db.OutboundWebhooks.Add(w);
        await db.SaveChangesAsync();
        return Results.Ok(new { w.Id, w.Url, w.EventType, message = "Webhook registered; delivered by background worker." });
    }

    private static async Task<Campaign?> FindScoped(AppDbContext db, PartnerClient client, Guid id)
    {
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null || !ScopedOk(client, c)) return null;
        return c;
    }

    private static async Task<Campaign?> FindScopedTracked(AppDbContext db, PartnerClient client, Guid id)
    {
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null || !ScopedOk(client, c)) return null;
        return c;
    }

    private static bool ScopedOk(PartnerClient client, Campaign c)
    {
        if (client.TenantId.HasValue && c.TenantId != client.TenantId) return false;
        // Partners without tenant can access external trust campaigns they created (CreatorId = client id)
        if (!client.TenantId.HasValue && c.IsExternalTrustOnly && c.CreatorId != client.ClientId)
            return false;
        return true;
    }

    private static object MapCampaign(Campaign c) => new
    {
        c.Id,
        c.Title,
        c.Category,
        c.GoalAmount,
        c.RaisedAmount,
        c.Currency,
        c.TrustScore,
        c.TransparencyScore,
        c.Status,
        c.ExternalRef,
        c.ExternalPlatform,
        c.TenantId,
        c.IsExternalTrustOnly
    };

    public record TrustCampaignRequest(
        string Title,
        string? Story = null,
        string? Category = null,
        decimal GoalAmount = 0,
        decimal? RaisedAmount = null,
        string? Currency = "USD",
        string? ExternalRef = null,
        string? PlatformName = null);

    public record TrustWebhookRequest(string Url, string? EventType = "trust.updated", Guid? CampaignId = null);
}
