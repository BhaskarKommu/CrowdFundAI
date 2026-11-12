using CrowdFund.Ai.Orchestration;
using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Web.Endpoints;
using CrowdFund.Web.Hubs;
using CrowdFund.Web.Observability;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Services;

/// <summary>
/// Nightly-style full re-verification (demo: every 6 hours) + optional transparency log publish.
/// Complements the 2-minute Watchdog tick with deeper Council runs.
/// </summary>
public class ReverificationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ReverificationHostedService> _log;

    public ReverificationHostedService(IServiceScopeFactory scopes, ILogger<ReverificationHostedService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so app can boot
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FullPassAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scheduled re-verification pass failed");
            }
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }

    public async Task FullPassAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orch = scope.ServiceProvider.GetRequiredService<VerificationOrchestrator>();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<CampaignHub>>();
        var risk = scope.ServiceProvider.GetRequiredService<CrowdFund.Core.Services.RiskGraphService>();
        var ledger = scope.ServiceProvider.GetRequiredService<CrowdFund.Core.Services.LedgerService>();
        var logSvc = scope.ServiceProvider.GetRequiredService<CrowdFund.Core.Services.TransparencyLogService>();

        var campaigns = await db.Campaigns
            .Where(c => c.Status == CampaignStatus.Published
                        || c.Status == CampaignStatus.NeedsReview
                        || c.Status == CampaignStatus.UnderReview)
            .Select(c => c.Id)
            .ToListAsync(ct);

        _log.LogInformation("Scheduled re-verification for {Count} campaigns", campaigns.Count);
        foreach (var id in campaigns)
        {
            ct.ThrowIfCancellationRequested();
            await RiskTrustEndpoints.RunFullReverifyAsync(id, "scheduled", db, orch, alerts, hub);

            // Publish ledger root to public transparency log
            var entries = await db.AuditLedger.Where(e => e.CampaignId == id).OrderBy(e => e.Id).ToListAsync(ct);
            if (entries.Count > 0)
            {
                var (valid, _) = ledger.VerifyChain(entries);
                var prev = await db.TransparencyLog.OrderByDescending(t => t.Id).Select(t => t.PublishedHash).FirstOrDefaultAsync(ct) ?? "";
                var pub = logSvc.CreatePublication(id, entries.Last().Hash, entries.Count, valid, prev);
                db.TransparencyLog.Add(pub);
                await db.SaveChangesAsync(ct);
            }
        }

        // Rebuild risk graph after pass
        var allCampaigns = await db.Campaigns.AsNoTracking().ToListAsync(ct);
        var docs = await db.CampaignDocuments.AsNoTracking().ToListAsync(ct);
        var expenses = await db.Expenses.AsNoTracking().ToListAsync(ct);
        var emails = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Email ?? "", ct);
        foreach (var au in await db.AppUsers.AsNoTracking().ToListAsync(ct))
            emails[au.Id] = au.Email;
        var edges = risk.BuildEdges(allCampaigns, docs, expenses, emails);
        db.RiskGraphEdges.RemoveRange(db.RiskGraphEdges);
        await db.SaveChangesAsync(ct);
        db.RiskGraphEdges.AddRange(edges);
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Risk graph rebuilt with {N} edges", edges.Count);
    }
}
