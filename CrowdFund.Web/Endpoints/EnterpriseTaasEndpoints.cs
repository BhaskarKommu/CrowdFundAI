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

public static class EnterpriseTaasEndpoints
{
    public static void MapEnterpriseTaasEndpoints(this WebApplication app)
    {
        // Admin enterprise account management
        app.MapPost("/api/admin/enterprise", CreateEnterprise).RequireAuthorization();
        app.MapGet("/api/admin/enterprise", ListEnterprise).RequireAuthorization();
        app.MapGet("/api/admin/enterprise/{tenantId:guid}", GetEnterprise).RequireAuthorization();

        // Enterprise portal (admin or partner with tenant)
        app.MapGet("/api/enterprise/{tenantId:guid}/dashboard", Dashboard).RequireAuthorization();
        app.MapPost("/api/enterprise/{tenantId:guid}/reports/generate", GenerateReport).RequireAuthorization();
        app.MapGet("/api/enterprise/{tenantId:guid}/reports", ListReports).RequireAuthorization();
        app.MapGet("/api/enterprise/reports/{id:guid}/html", ReportHtml);
        app.MapPost("/api/enterprise/{tenantId:guid}/compliance-pack", BuildCompliancePack).RequireAuthorization();
        app.MapGet("/api/enterprise/{tenantId:guid}/compliance-packs", ListPacks).RequireAuthorization();
        app.MapPost("/api/enterprise/{tenantId:guid}/bulk-verify", BulkVerify).RequireAuthorization();

        // Partner enterprise read API
        app.MapGet("/api/enterprise/v1/portfolio", PartnerPortfolio);
        app.MapPost("/api/enterprise/v1/reports/generate", PartnerGenerateReport);
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static async Task<bool> CanAccessTenant(HttpContext http, AppDbContext db, Guid tenantId)
    {
        if (http.User.IsInRole("Admin")) return true;
        // Org members whose campaigns sit on this tenant — soft check via creator campaigns
        return await db.Campaigns.AnyAsync(c => c.TenantId == tenantId && c.CreatorId == Uid(http));
    }

    private static async Task TrackUsage(AppDbContext db, Guid tenantId)
    {
        var day = DateTime.UtcNow.Date;
        var row = await db.EnterpriseApiUsage.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Day == day);
        if (row == null)
        {
            db.EnterpriseApiUsage.Add(new EnterpriseApiUsage { TenantId = tenantId, Day = day, CallCount = 1 });
        }
        else row.CallCount++;
        await db.SaveChangesAsync();
    }

    private static async Task<IResult> CreateEnterprise(
        [FromBody] EnterpriseCreateRequest req, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == req.TenantId);
        if (tenant == null) return Results.BadRequest(new { error = "Tenant not found. Create tenant first." });
        if (await db.EnterpriseAccounts.AnyAsync(e => e.TenantId == req.TenantId && e.IsActive))
            return Results.BadRequest(new { error = "Active enterprise account already exists for tenant" });

        var tier = Enum.TryParse<EnterpriseTier>(req.Tier ?? "Enterprise", true, out var t) ? t : EnterpriseTier.Enterprise;
        var acct = new EnterpriseAccount
        {
            TenantId = req.TenantId,
            Tier = tier,
            MaxCampaigns = req.MaxCampaigns ?? (tier == EnterpriseTier.Enterprise ? 500 : 100),
            MaxApiCallsPerDay = req.MaxApiCallsPerDay ?? 50_000,
            ReportCadence = req.ReportCadence ?? "weekly",
            ReportRecipients = req.ReportRecipients,
            PrimaryContactUserId = req.PrimaryContactUserId ?? Uid(http),
            ContractStartsAt = DateTime.UtcNow,
            ContractEndsAt = DateTime.UtcNow.AddYears(1)
        };
        db.EnterpriseAccounts.Add(acct);
        await db.SaveChangesAsync();
        return Results.Ok(acct);
    }

    private static async Task<IResult> ListEnterprise(HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var list = await db.EnterpriseAccounts.AsNoTracking().OrderByDescending(e => e.CreatedAt).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> GetEnterprise(Guid tenantId, HttpContext http, AppDbContext db)
    {
        if (!await CanAccessTenant(http, db, tenantId)) return Results.Forbid();
        var acct = await db.EnterpriseAccounts.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IsActive);
        if (acct == null) return Results.NotFound();
        return Results.Ok(acct);
    }

    private static async Task<IResult> Dashboard(Guid tenantId, HttpContext http, AppDbContext db)
    {
        if (!await CanAccessTenant(http, db, tenantId)) return Results.Forbid();
        var acct = await db.EnterpriseAccounts.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IsActive);
        if (acct == null)
            return Results.BadRequest(new { error = "No active Enterprise Transparency account for tenant" });

        var campaigns = await db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync();
        var ids = campaigns.Select(c => c.Id).ToList();
        var expenses = await db.Expenses.AsNoTracking().Where(e => ids.Contains(e.CampaignId)).ToListAsync();
        var flags = await db.FraudFlags.AsNoTracking()
            .Where(f => ids.Contains(f.CampaignId) && f.Status == "Open").ToListAsync();
        var usage = await db.EnterpriseApiUsage.AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.Day >= DateTime.UtcNow.Date.AddDays(-7))
            .OrderBy(u => u.Day).ToListAsync();

        var verified = expenses.Where(e => e.Status is ExpenseStatus.Verified or ExpenseStatus.AiExtracted).Sum(e => e.Amount);
        var raised = campaigns.Sum(c => c.RaisedAmount);

        await TrackUsage(db, tenantId);

        return Results.Ok(new
        {
            account = acct,
            portfolio = new
            {
                campaignCount = campaigns.Count,
                published = campaigns.Count(c => c.Status == CampaignStatus.Published),
                underReview = campaigns.Count(c => c.Status is CampaignStatus.UnderReview or CampaignStatus.NeedsReview),
                totalRaised = raised,
                verifiedSpend = verified,
                verifiedPct = raised <= 0 ? 0 : Math.Round(100m * verified / raised, 1),
                avgTrust = campaigns.Count == 0 ? 0 : Math.Round(campaigns.Average(c => c.TrustScore), 1),
                avgTransparency = campaigns.Count == 0 ? 0 : Math.Round(campaigns.Average(c => c.TransparencyScore), 1),
                openHighFlags = flags.Count(f => f.Severity == "High"),
                openFlags = flags.Count
            },
            campaigns = campaigns.OrderByDescending(c => c.RaisedAmount).Take(25).Select(c => new
            {
                c.Id, c.Title, c.Status, c.TrustScore, c.TransparencyScore, c.RaisedAmount, c.GoalAmount, c.Category
            }),
            apiUsageLast7Days = usage,
            product = "Enterprise Transparency-as-a-Service",
            disclaimer = "Portfolio metrics are advisory operational indicators for NGO program teams."
        });
    }

    private static async Task<IResult> GenerateReport(
        Guid tenantId, HttpContext http, AppDbContext db, EnterpriseTransparencyService svc)
    {
        if (!await CanAccessTenant(http, db, tenantId)) return Results.Forbid();
        var acct = await db.EnterpriseAccounts.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IsActive);
        if (acct == null) return Results.BadRequest(new { error = "Enterprise account required" });
        if (!acct.ScheduledReportsEnabled)
            return Results.BadRequest(new { error = "Reports disabled on this account" });

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId);
        var end = DateTime.UtcNow;
        var start = end.AddDays(-30);
        var campaigns = await db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync();
        var ids = campaigns.Select(c => c.Id).ToList();
        var expenses = await db.Expenses.AsNoTracking().Where(e => ids.Contains(e.CampaignId)).ToListAsync();
        var flags = await db.FraudFlags.AsNoTracking().Where(f => ids.Contains(f.CampaignId)).ToListAsync();

        var report = svc.BuildPortfolioReport(tenantId, acct.Id, campaigns, expenses, flags, start, end, tenant.Name);
        db.PortfolioTransparencyReports.Add(report);
        await db.SaveChangesAsync();
        await TrackUsage(db, tenantId);
        return Results.Ok(new { report.Id, report.Status, report.CampaignCount, report.AvgTrustScore, html = $"/api/enterprise/reports/{report.Id}/html" });
    }

    private static async Task<IResult> ListReports(Guid tenantId, HttpContext http, AppDbContext db)
    {
        if (!await CanAccessTenant(http, db, tenantId)) return Results.Forbid();
        var list = await db.PortfolioTransparencyReports.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> ReportHtml(Guid id, AppDbContext db)
    {
        var r = await db.PortfolioTransparencyReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return Results.NotFound();
        return Results.Content(r.HtmlBody ?? "<p>No content</p>", "text/html");
    }

    private static async Task<IResult> BuildCompliancePack(
        Guid tenantId, [FromBody] PackRequest? req, HttpContext http, AppDbContext db, EnterpriseTransparencyService svc)
    {
        if (!await CanAccessTenant(http, db, tenantId)) return Results.Forbid();
        var acct = await db.EnterpriseAccounts.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IsActive);
        if (acct == null || !acct.ComplianceExportEnabled)
            return Results.BadRequest(new { error = "Compliance export not enabled" });

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId);
        var campaignsQ = db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (req?.CampaignId != null)
            campaignsQ = campaignsQ.Where(c => c.Id == req.CampaignId);
        var campaigns = await campaignsQ.ToListAsync();
        var ids = campaigns.Select(c => c.Id).ToList();
        var expenses = await db.Expenses.AsNoTracking().Where(e => ids.Contains(e.CampaignId)).ToListAsync();
        var heads = new List<AuditLedgerEntry>();
        foreach (var id in ids.Take(100))
        {
            var head = await db.AuditLedger.AsNoTracking().Where(e => e.CampaignId == id)
                .OrderByDescending(e => e.Id).FirstOrDefaultAsync();
            if (head != null) heads.Add(head);
        }
        var manifest = svc.ComplianceManifest(tenant, campaigns, expenses, heads);
        var pack = new CompliancePack
        {
            TenantId = tenantId,
            CampaignId = req?.CampaignId,
            PackType = req?.CampaignId == null ? "portfolio" : "campaign",
            ManifestJson = JsonSerializer.Serialize(manifest),
            DownloadHint = "Use manifest + object storage evidence paths for offline audit binders."
        };
        db.CompliancePacks.Add(pack);
        await db.SaveChangesAsync();
        await TrackUsage(db, tenantId);
        return Results.Ok(new { pack.Id, pack.PackType, manifest, pack.DownloadHint });
    }

    private static async Task<IResult> ListPacks(Guid tenantId, HttpContext http, AppDbContext db)
    {
        if (!await CanAccessTenant(http, db, tenantId)) return Results.Forbid();
        var list = await db.CompliancePacks.AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.CreatedAt)
            .Take(50)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> BulkVerify(
        Guid tenantId, [FromBody] BulkVerifyRequest req, HttpContext http, AppDbContext db)
    {
        if (!await CanAccessTenant(http, db, tenantId)) return Results.Forbid();
        var acct = await db.EnterpriseAccounts.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IsActive);
        if (acct == null || !acct.BulkVerifyEnabled)
            return Results.BadRequest(new { error = "Bulk verify not enabled" });

        var q = db.Campaigns.Where(c => c.TenantId == tenantId &&
            (c.Status == CampaignStatus.UnderReview || c.Status == CampaignStatus.NeedsReview || c.Status == CampaignStatus.Published));
        if (req.CampaignIds is { Count: > 0 })
            q = q.Where(c => req.CampaignIds.Contains(c.Id));
        var ids = await q.Select(c => c.Id).Take(50).ToListAsync();
        var queued = 0;
        foreach (var id in ids)
        {
            db.BackgroundJobs.Add(new BackgroundJob
            {
                JobType = BackgroundJobType.VerificationCouncil,
                CampaignId = id,
                TenantId = tenantId,
                Status = BackgroundJobStatus.Pending,
                PayloadJson = JsonSerializer.Serialize(new { source = "enterprise_bulk_verify" })
            });
            queued++;
        }
        await db.SaveChangesAsync();
        await TrackUsage(db, tenantId);
        return Results.Ok(new { queued, message = "Verification jobs enqueued for worker processing." });
    }

    private static async Task<(PartnerClient? client, IResult? error)> AuthPartner(HttpContext http, AppDbContext db)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return (null, Results.Unauthorized());
        var raw = header["Bearer ".Length..].Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        var token = await db.PartnerAccessTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.ExpiresAt > DateTime.UtcNow);
        if (token == null) return (null, Results.Unauthorized());
        var client = await db.PartnerClients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == token.PartnerClientId && c.IsActive);
        if (client?.TenantId == null) return (null, Results.Json(new { error = "Partner must be tenant-scoped for enterprise API" }, statusCode: 403));
        return (client, null);
    }

    private static async Task<IResult> PartnerPortfolio(HttpContext http, AppDbContext db)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        // Reuse dashboard logic path via internal call shape
        var tenantId = client!.TenantId!.Value;
        var acct = await db.EnterpriseAccounts.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IsActive);
        if (acct == null) return Results.BadRequest(new { error = "Enterprise not enabled for tenant" });
        var campaigns = await db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync();
        await TrackUsage(db, tenantId);
        return Results.Ok(new
        {
            tenantId,
            campaignCount = campaigns.Count,
            totalRaised = campaigns.Sum(c => c.RaisedAmount),
            avgTrust = campaigns.Count == 0 ? 0 : campaigns.Average(c => (double)c.TrustScore),
            campaigns = campaigns.Select(c => new { c.Id, c.Title, c.TrustScore, c.TransparencyScore, c.RaisedAmount, c.Status })
        });
    }

    private static async Task<IResult> PartnerGenerateReport(
        HttpContext http, AppDbContext db, EnterpriseTransparencyService svc)
    {
        var (client, err) = await AuthPartner(http, db);
        if (err != null) return err;
        var tenantId = client!.TenantId!.Value;
        var acct = await db.EnterpriseAccounts.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IsActive);
        if (acct == null) return Results.BadRequest(new { error = "Enterprise not enabled" });
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId);
        var campaigns = await db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync();
        var ids = campaigns.Select(c => c.Id).ToList();
        var expenses = await db.Expenses.AsNoTracking().Where(e => ids.Contains(e.CampaignId)).ToListAsync();
        var flags = await db.FraudFlags.AsNoTracking().Where(f => ids.Contains(f.CampaignId)).ToListAsync();
        var report = svc.BuildPortfolioReport(tenantId, acct.Id, campaigns, expenses, flags, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow, tenant.Name);
        db.PortfolioTransparencyReports.Add(report);
        await db.SaveChangesAsync();
        return Results.Ok(new { report.Id, html = $"/api/enterprise/reports/{report.Id}/html", report.SummaryMarkdown });
    }

    public record EnterpriseCreateRequest(
        Guid TenantId,
        string? Tier = "Enterprise",
        int? MaxCampaigns = null,
        int? MaxApiCallsPerDay = null,
        string? ReportCadence = "weekly",
        string? ReportRecipients = null,
        string? PrimaryContactUserId = null);

    public record PackRequest(Guid? CampaignId = null);
    public record BulkVerifyRequest(List<Guid>? CampaignIds = null);
}
