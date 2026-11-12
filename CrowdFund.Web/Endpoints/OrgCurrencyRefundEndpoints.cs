using System.Security.Claims;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class OrgCurrencyRefundEndpoints
{
    public static void MapOrgCurrencyRefundEndpoints(this WebApplication app)
    {
        // Currency
        app.MapGet("/api/currency/supported", (CurrencyService c) => Results.Ok(new
        {
            settlementDefault = c.Options.SettlementCurrency,
            supported = c.Supported,
            disclosure = "Display conversions are indicative only. Settlement remains in the campaign currency unless a separate FX product is offered."
        }));
        app.MapGet("/api/currency/convert", ConvertMoney);

        // Templates
        app.MapGet("/api/campaign-templates", (CampaignTemplateService t) => Results.Ok(t.All()));
        app.MapGet("/api/campaign-templates/{key}", (string key, CampaignTemplateService t) =>
        {
            var x = t.Get(key);
            return x == null ? Results.NotFound() : Results.Ok(x);
        });

        // Org / team
        app.MapPost("/api/orgs", CreateOrg).RequireAuthorization();
        app.MapGet("/api/orgs/mine", MyOrgs).RequireAuthorization();
        app.MapPost("/api/orgs/{id:guid}/members", AddMember).RequireAuthorization();
        app.MapGet("/api/orgs/{id:guid}/members", ListMembers).RequireAuthorization();
        app.MapPost("/api/orgs/{id:guid}/approvals", RequestApproval).RequireAuthorization();
        app.MapPost("/api/approvals/{id:guid}/decide", DecideApproval).RequireAuthorization();
        app.MapGet("/api/orgs/{id:guid}/approvals", ListApprovals).RequireAuthorization();

        // Refunds & disputes
        app.MapPost("/api/donations/{id:guid}/refunds", RequestRefund).RequireAuthorization();
        app.MapPost("/api/admin/refunds/{id:guid}/decision", DecideRefund).RequireAuthorization();
        app.MapPost("/api/admin/campaigns/{id:guid}/freeze", FreezeCampaign).RequireAuthorization();
        app.MapPost("/api/admin/campaigns/{id:guid}/unfreeze", UnfreezeCampaign).RequireAuthorization();
        app.MapPost("/api/disputes", OpenDispute).RequireAuthorization();
        app.MapGet("/api/admin/refunds", ListRefunds);
        app.MapGet("/api/admin/disputes", ListDisputes);
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static IResult ConvertMoney(
        [FromQuery] decimal amount,
        [FromQuery] string from,
        [FromQuery] string? to,
        CurrencyService currency)
    {
        var display = currency.Display(amount, from, to);
        return Results.Ok(display);
    }

    private static async Task<IResult> CreateOrg([FromBody] OrgRequest req, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var org = new Organization
        {
            Name = (req.Name ?? "My organization").Trim(),
            HomeCurrency = string.IsNullOrWhiteSpace(req.HomeCurrency) ? "USD" : req.HomeCurrency.ToUpperInvariant(),
            OwnerUserId = uid
        };
        db.Organizations.Add(org);
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = org.Id,
            UserId = uid,
            Role = OrgMemberRole.Owner
        });
        await db.SaveChangesAsync();
        return Results.Ok(org);
    }

    private static async Task<IResult> MyOrgs(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http);
        var ids = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.UserId == uid)
            .Select(m => m.OrganizationId)
            .ToListAsync();
        var orgs = await db.Organizations.AsNoTracking().Where(o => ids.Contains(o.Id)).ToListAsync();
        return Results.Ok(orgs);
    }

    private static async Task<bool> HasRole(AppDbContext db, Guid orgId, string userId, params OrgMemberRole[] roles)
    {
        var m = await db.OrganizationMembers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.UserId == userId);
        return m != null && roles.Contains(m.Role);
    }

    private static async Task<IResult> AddMember(Guid id, [FromBody] MemberRequest req, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        if (!await HasRole(db, id, uid, OrgMemberRole.Owner, OrgMemberRole.Admin))
            return Results.Forbid();
        if (string.IsNullOrWhiteSpace(req.UserId))
            return Results.BadRequest(new { error = "UserId required" });
        var role = Enum.TryParse<OrgMemberRole>(req.Role, true, out var r) ? r : OrgMemberRole.Editor;
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = id,
            UserId = req.UserId,
            Role = role
        });
        await db.SaveChangesAsync();
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> ListMembers(Guid id, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        if (!await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == id && m.UserId == uid))
            return Results.Forbid();
        var list = await db.OrganizationMembers.AsNoTracking().Where(m => m.OrganizationId == id).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> RequestApproval(
        Guid id, [FromBody] ApprovalCreateRequest req, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        if (!await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == id && m.UserId == uid))
            return Results.Forbid();
        var required = Enum.TryParse<OrgMemberRole>(req.RequiredRole, true, out var rr) ? rr : OrgMemberRole.Admin;
        var a = new ApprovalRequest
        {
            OrganizationId = id,
            CampaignId = req.CampaignId,
            Action = req.Action ?? "PublishCampaign",
            RequestedBy = uid,
            RequiredRole = required,
            Status = "Pending"
        };
        db.ApprovalRequests.Add(a);
        await db.SaveChangesAsync();
        return Results.Ok(a);
    }

    private static async Task<IResult> DecideApproval(
        Guid id, [FromBody] ApprovalDecideRequest req, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var a = await db.ApprovalRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return Results.NotFound();
        var member = await db.OrganizationMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.OrganizationId == a.OrganizationId && m.UserId == uid);
        if (member == null || member.Role > a.RequiredRole) // Owner=0 is highest
        {
            // Owner and Admin can approve Admin-required; compare numerically: lower enum = higher privilege
            if (member == null || (int)member.Role > (int)a.RequiredRole)
                return Results.Forbid();
        }
        a.Status = string.Equals(req.Decision, "approve", StringComparison.OrdinalIgnoreCase) ? "Approved" : "Rejected";
        a.DecidedBy = uid;
        a.DecidedAt = DateTime.UtcNow;
        a.Note = req.Note;
        await db.SaveChangesAsync();
        return Results.Ok(a);
    }

    private static async Task<IResult> ListApprovals(Guid id, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        if (!await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == id && m.UserId == uid))
            return Results.Forbid();
        var list = await db.ApprovalRequests.AsNoTracking()
            .Where(a => a.OrganizationId == id)
            .OrderByDescending(a => a.CreatedAt)
            .Take(100)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> RequestRefund(
        Guid id, [FromBody] RefundCreateRequest req, HttpContext http, AppDbContext db,
        RefundDisputeService refunds)
    {
        var donation = await db.Donations.FirstOrDefaultAsync(d => d.Id == id);
        if (donation == null) return Results.NotFound();
        if (donation.Status != DonationStatus.Captured)
            return Results.BadRequest(new { error = "Only captured donations can be refunded" });

        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == donation.CampaignId);
        var highFraud = await db.FraudFlags.AnyAsync(f =>
            f.CampaignId == donation.CampaignId && f.Status == "Open" && f.Severity == "High");
        var freeze = highFraud || (campaign?.FundsFrozen ?? false) || (req.ForceFreeze ?? false);

        var amount = req.Amount <= 0 ? donation.Amount : req.Amount;
        try
        {
            var rr = refunds.CreatePartialRefund(donation, amount, req.Reason, Uid(http), freeze);
            db.RefundRequests.Add(rr);
            if (freeze && campaign != null && !campaign.FundsFrozen)
            {
                campaign.FundsFrozen = true;
                campaign.FreezeReason = "Auto-freeze: high fraud flags or policy hold during refund request";
                if (campaign.Status == CampaignStatus.Published)
                    campaign.Status = CampaignStatus.Frozen;
            }
            await db.SaveChangesAsync();
            return Results.Ok(rr);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> DecideRefund(
        Guid id, [FromBody] RefundDecideRequest req, HttpContext http, AppDbContext db,
        RefundDisputeService refunds, LedgerService ledger)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var rr = await db.RefundRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (rr == null) return Results.NotFound();
        var donation = await db.Donations.FirstOrDefaultAsync(d => d.Id == rr.DonationId);
        if (donation == null) return Results.NotFound();

        if (string.Equals(req.Decision, "reject", StringComparison.OrdinalIgnoreCase))
        {
            rr.Status = RefundStatus.Rejected;
            rr.ReviewedBy = Uid(http);
            rr.ReviewedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(rr);
        }

        if (string.Equals(req.Decision, "unfreeze", StringComparison.OrdinalIgnoreCase))
        {
            rr.FrozenForFraud = false;
            if (rr.Status == RefundStatus.Frozen) rr.Status = RefundStatus.UnderReview;
            await db.SaveChangesAsync();
            return Results.Ok(rr);
        }

        var amount = req.Amount <= 0 ? rr.RequestedAmount - rr.RefundedAmount : req.Amount;
        try
        {
            refunds.ApplyApproval(rr, amount, Uid(http) ?? "admin");
            // Adjust raised / donation status
            var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == rr.CampaignId);
            if (campaign != null)
                campaign.RaisedAmount = Math.Max(0, campaign.RaisedAmount - amount);
            if (rr.RefundedAmount >= donation.Amount)
                donation.Status = DonationStatus.Refunded;

            var prev = await db.AuditLedger.Where(e => e.CampaignId == rr.CampaignId)
                .OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync() ?? "";
            db.AuditLedger.Add(ledger.CreateEntry(rr.CampaignId, "Refund", new
            {
                rr.Id,
                rr.DonationId,
                amount,
                rr.Status
            }, prev));
            await db.SaveChangesAsync();
            return Results.Ok(rr);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> FreezeCampaign(Guid id, [FromBody] FreezeRequest req, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return Results.NotFound();
        c.FundsFrozen = true;
        c.FreezeReason = req.Reason ?? "Admin freeze";
        c.Status = CampaignStatus.Frozen;
        await db.SaveChangesAsync();
        return Results.Ok(new { c.Id, c.FundsFrozen, c.FreezeReason, c.Status });
    }

    private static async Task<IResult> UnfreezeCampaign(Guid id, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return Results.NotFound();
        c.FundsFrozen = false;
        c.FreezeReason = null;
        if (c.Status == CampaignStatus.Frozen)
            c.Status = CampaignStatus.Published;
        await db.SaveChangesAsync();
        return Results.Ok(new { c.Id, c.FundsFrozen, c.Status });
    }

    private static async Task<IResult> OpenDispute([FromBody] DisputeRequest req, HttpContext http, AppDbContext db)
    {
        if (req.CampaignId == Guid.Empty)
            return Results.BadRequest(new { error = "CampaignId required" });
        var d = new DisputeCase
        {
            CampaignId = req.CampaignId,
            DonationId = req.DonationId,
            OpenedBy = Uid(http),
            Category = req.Category ?? "General",
            Description = req.Description ?? "",
            Status = DisputeStatus.Open
        };
        db.DisputeCases.Add(d);
        await db.SaveChangesAsync();
        return Results.Ok(d);
    }

    private static async Task<IResult> ListRefunds(AppDbContext db)
    {
        var list = await db.RefundRequests.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> ListDisputes(AppDbContext db)
    {
        var list = await db.DisputeCases.AsNoTracking().OrderByDescending(d => d.CreatedAt).Take(100).ToListAsync();
        return Results.Ok(list);
    }

    public record OrgRequest(string? Name, string? HomeCurrency = "USD");
    public record MemberRequest(string UserId, string? Role = "Editor");
    public record ApprovalCreateRequest(string? Action, Guid? CampaignId, string? RequiredRole = "Admin");
    public record ApprovalDecideRequest(string Decision, string? Note = null);
    public record RefundCreateRequest(decimal Amount = 0, string? Reason = null, bool? ForceFreeze = null);
    public record RefundDecideRequest(string Decision, decimal Amount = 0);
    public record FreezeRequest(string? Reason = null);
    public record DisputeRequest(Guid CampaignId, Guid? DonationId = null, string? Category = null, string? Description = null);
}
