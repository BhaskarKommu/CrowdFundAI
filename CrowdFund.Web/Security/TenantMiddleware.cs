using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Security;

/// <summary>
/// Resolves tenant from host (custom domain) or X-Tenant-Slug / ?tenant= for hard isolation.
/// Auth cookie pages should not be edge-cached — Cache-Control set for HTML.
/// </summary>
public class TenantMiddleware
{
    private readonly RequestDelegate _next;

    public TenantMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext, AppDbContext db)
    {
        if (context.User.IsInRole("Admin"))
            tenantContext.IsPlatformAdmin = true;

        var slug = context.Request.Headers["X-Tenant-Slug"].FirstOrDefault()
                   ?? context.Request.Query["tenant"].FirstOrDefault();
        var host = context.Request.Host.Host;

        Core.Entities.Tenant? tenant = null;
        if (!string.IsNullOrWhiteSpace(slug))
            tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive);
        else if (!string.IsNullOrEmpty(host))
            tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.CustomDomain == host && t.IsActive);

        if (tenant != null)
        {
            tenantContext.TenantId = tenant.Id;
            tenantContext.Slug = tenant.Slug;
            context.Items["Tenant"] = tenant;
            context.Response.Headers["X-Tenant"] = tenant.Slug;
        }

        // CDN / edge: allow long cache on static under wwwroot only; never cache auth HTML
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/Account", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers["Cache-Control"] = "no-store";
        }

        await _next(context);
    }
}

public static class CdnStaticExtensions
{
    public static IApplicationBuilder UseCrowdFundStaticWithCdnHeaders(this IApplicationBuilder app)
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var path = ctx.File.Name;
                // Cache static assets at edge/CDN; HTML/auth never go through this pipeline the same way
                ctx.Context.Response.Headers["Cache-Control"] = "public,max-age=604800,immutable";
                ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            }
        });
        return app;
    }
}
