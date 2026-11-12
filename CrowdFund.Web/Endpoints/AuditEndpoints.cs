using System.Security.Claims;
using System.Text;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/audit", ListAudit);
        app.MapGet("/api/admin/audit/export", ExportAudit);
        app.MapPost("/api/admin/audit/log", WriteAudit);
    }

    public static async Task WriteAsync(
        AppDbContext db,
        SecurityAuditService audit,
        HttpContext http,
        string action,
        string? entityType = null,
        string? entityId = null,
        object? details = null,
        string result = "ok")
    {
        var user = http.User;
        var ev = audit.Create(
            action,
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name,
            user.FindFirstValue(ClaimTypes.Role),
            entityType,
            entityId,
            http.Connection.RemoteIpAddress?.ToString(),
            http.Request.Headers.UserAgent.ToString(),
            details,
            result);
        db.SecurityAuditEvents.Add(ev);
        await db.SaveChangesAsync();
    }

    private static async Task<IResult> ListAudit(
        AppDbContext db,
        [FromQuery] string? action = null,
        [FromQuery] string? actor = null,
        [FromQuery] int take = 100)
    {
        take = Math.Clamp(take, 1, 500);
        var q = db.SecurityAuditEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action))
            q = q.Where(e => e.Action.Contains(action));
        if (!string.IsNullOrWhiteSpace(actor))
            q = q.Where(e => e.ActorUserId == actor || (e.ActorEmail != null && e.ActorEmail.Contains(actor)));
        var list = await q.OrderByDescending(e => e.Id).Take(take).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> ExportAudit(
        AppDbContext db,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        var q = db.SecurityAuditEvents.AsNoTracking().AsQueryable();
        if (from.HasValue) q = q.Where(e => e.CreatedAt >= from);
        if (to.HasValue) q = q.Where(e => e.CreatedAt <= to);
        var list = await q.OrderBy(e => e.Id).Take(10_000).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Id,CreatedAt,Action,Result,ActorUserId,ActorEmail,ActorRole,EntityType,EntityId,IpAddress,DetailsJson");
        foreach (var e in list)
        {
            string Esc(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            sb.AppendLine(string.Join(",",
                e.Id,
                Esc(e.CreatedAt.ToString("o")),
                Esc(e.Action),
                Esc(e.Result),
                Esc(e.ActorUserId),
                Esc(e.ActorEmail),
                Esc(e.ActorRole),
                Esc(e.EntityType),
                Esc(e.EntityId),
                Esc(e.IpAddress),
                Esc(e.DetailsJson)));
        }
        return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"crowdfund-audit-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
    }

    private static async Task<IResult> WriteAudit(
        [FromBody] ManualAuditRequest req,
        HttpContext http,
        AppDbContext db,
        SecurityAuditService audit)
    {
        await WriteAsync(db, audit, http, req.Action, req.EntityType, req.EntityId, req.Details, req.Result ?? "ok");
        return Results.Ok(new { ok = true });
    }

    public record ManualAuditRequest(string Action, string? EntityType = null, string? EntityId = null, object? Details = null, string? Result = null);
}
