using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace CrowdFund.Web.Security;

public static class RateLimitAndHeadersExtensions
{
    public static IServiceCollection AddCrowdFundRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var permit = config.GetValue("RateLimiting:PermitLimit", 60);
        var window = config.GetValue("RateLimiting:WindowSeconds", 60);
        var strictPermit = config.GetValue("RateLimiting:StrictPermitLimit", 10);
        var strictWindow = config.GetValue("RateLimiting:StrictWindowSeconds", 60);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (ctx, token) =>
            {
                ctx.HttpContext.Response.ContentType = "application/json";
                await ctx.HttpContext.Response.WriteAsync(
                    """{"error":"Rate limit exceeded. Please try again later."}""", token);
            };

            options.AddPolicy("api", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permit,
                        Window = TimeSpan.FromSeconds(window),
                        QueueLimit = 0
                    }));

            options.AddPolicy("strict", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = strictPermit,
                        Window = TimeSpan.FromSeconds(strictWindow),
                        QueueLimit = 0
                    }));

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permit * 3,
                        Window = TimeSpan.FromSeconds(window),
                        QueueLimit = 0
                    }));
        });

        return services;
    }

    public static IApplicationBuilder UseCrowdFundSecurityHeaders(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            var headers = ctx.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["X-XSS-Protection"] = "0";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(self)";
            if (!headers.ContainsKey("Content-Security-Policy"))
            {
                headers["Content-Security-Policy"] =
                    "default-src 'self'; " +
                    "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
                    "style-src 'self' 'unsafe-inline'; " +
                    "img-src 'self' data: blob:; " +
                    "font-src 'self' data:; " +
                    "connect-src 'self' wss: ws:; " +
                    "frame-ancestors 'none'; " +
                    "base-uri 'self'; " +
                    "form-action 'self'";
            }
            await next();
        });
        return app;
    }
}
