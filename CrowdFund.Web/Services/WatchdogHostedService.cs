using CrowdFund.Web.Observability;
using CrowdFund.Ai.Fraud;

using CrowdFund.Ai.Watchdog;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Services;

/// <summary>
/// A7 Continuous Watchdog — debounced re-score on a timer; pushes SignalR alerts.
/// Full nightly re-verification is represented by the same path.
/// </summary>
public class WatchdogHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHubContext<CampaignHub> _hub;
    private readonly ILogger<WatchdogHostedService> _logger;
    private readonly AppMetrics _metrics;

    public WatchdogHostedService(
        IServiceScopeFactory scopes,
        IHubContext<CampaignHub> hub,
        ILogger<WatchdogHostedService> logger,
        AppMetrics metrics)
    {
        _scopes = scopes;
        _hub = hub;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run every 2 minutes in demo (debounce substitute)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watchdog tick failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fraud = scope.ServiceProvider.GetRequiredService<FraudEngine>();
        var transparency = scope.ServiceProvider.GetRequiredService<TransparencyScoreService>();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerService>();

        var campaigns = await db.Campaigns
            .Where(c => c.Status == CampaignStatus.Published || c.Status == CampaignStatus.NeedsReview || c.Status == CampaignStatus.UnderReview)
            .ToListAsync(ct);

        foreach (var campaign in campaigns)
        {
            var expenses = await db.Expenses.Include(e => e.Evidences)
                .Where(e => e.CampaignId == campaign.Id).ToListAsync(ct);
            var donations = await db.Donations.Where(d => d.CampaignId == campaign.Id).ToListAsync(ct);
            var docs = await db.CampaignDocuments.Where(d => d.CampaignId == campaign.Id).ToListAsync(ct);
            var updates = await db.CampaignUpdates.Where(u => u.CampaignId == campaign.Id).ToListAsync(ct);
            var existingFlags = await db.FraudFlags.Where(f => f.CampaignId == campaign.Id && f.Status == "Open").ToListAsync(ct);

            var newFlags = fraud.EvaluateCampaign(campaign, expenses, donations, docs).ToList();
            try
            {
                var anomaly = scope.ServiceProvider.GetRequiredService<AnomalyEngine>();
                var peers = await db.Campaigns.AsNoTracking()
                    .Where(c => c.Category == campaign.Category && c.Id != campaign.Id).Take(20).ToListAsync(ct);
                var peerIds = peers.Select(x => x.Id).ToList();
                var peerExp = await db.Expenses.AsNoTracking().Include(e => e.Evidences)
                    .Where(e => peerIds.Contains(e.CampaignId)).ToListAsync(ct);
                var hashRows = await db.ExpenseEvidences.AsNoTracking().Select(ev => new { ev.Sha256, ev.ExpenseId }).ToListAsync(ct);
                var expMap = await db.Expenses.AsNoTracking().Select(e => new { e.Id, e.CampaignId }).ToListAsync(ct);
                var map = expMap.ToDictionary(x => x.Id, x => x.CampaignId);
                var corpus = hashRows.Where(h => map.ContainsKey(h.ExpenseId))
                    .Select(h => (h.Sha256, map[h.ExpenseId], h.ExpenseId)).ToList();
                newFlags.AddRange(anomaly.Evaluate(campaign, expenses, peers, peerExp, corpus));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Anomaly engine skipped for {CampaignId}", campaign.Id);
            }
            foreach (var f in newFlags)
            {
                // Avoid flooding identical open rules
                if (existingFlags.Any(e => e.Rule == f.Rule && e.ExpenseId == f.ExpenseId))
                    continue;
                db.FraudFlags.Add(f);
                _metrics.FraudFlag();
                if (f.Severity == "High")
                {
                    await _hub.Clients.Group($"campaign-{campaign.Id}")
                        .SendAsync("FlagRaised", new { campaign.Id, f.Rule, f.Severity, f.Explanation }, ct);
                    _logger.LogWarning("Watchdog high flag {Rule} on {CampaignId}", f.Rule, campaign.Id);
                }
            }

            var allFlags = existingFlags.Concat(newFlags).ToList();
            var entries = await db.AuditLedger.Where(e => e.CampaignId == campaign.Id).OrderBy(e => e.Id).ToListAsync(ct);
            var (valid, _) = ledger.VerifyChain(entries);
            var score = transparency.Compute(campaign, expenses, updates, allFlags, valid);
            var prevScore = campaign.TransparencyScore;
            if (campaign.TransparencyScore != score.Score || prevScore == 0)
            {
                var prevBreakdown = new TransparencyScoreService.Breakdown(
                    prevScore, score.ExpensesVerifiedPct, score.EvidenceCompleteness,
                    score.UpdateFreshness, score.OpenFlagsPenalty, score.LedgerOkBonus);
                // Prefer last snapshot breakdown if available
                var lastSnap = await db.TransparencyScoreSnapshots
                    .Where(s => s.CampaignId == campaign.Id)
                    .OrderByDescending(s => s.Id)
                    .FirstOrDefaultAsync(ct);
                if (lastSnap != null)
                {
                    prevBreakdown = new TransparencyScoreService.Breakdown(
                        lastSnap.Score, lastSnap.ExpensesVerifiedPct, lastSnap.EvidenceCompleteness,
                        lastSnap.UpdateFreshness, lastSnap.OpenFlagsPenalty, lastSnap.LedgerOkBonus);
                }

                var summary = TransparencyScoreService.DescribeChange(prevBreakdown, score);
                db.TransparencyScoreSnapshots.Add(new TransparencyScoreSnapshot
                {
                    CampaignId = campaign.Id,
                    Score = score.Score,
                    PreviousScore = prevBreakdown.Score,
                    Delta = score.Score - prevBreakdown.Score,
                    ExpensesVerifiedPct = score.ExpensesVerifiedPct,
                    EvidenceCompleteness = score.EvidenceCompleteness,
                    UpdateFreshness = score.UpdateFreshness,
                    OpenFlagsPenalty = score.OpenFlagsPenalty,
                    LedgerOkBonus = score.LedgerOkBonus,
                    ChangeSummary = summary,
                    Source = "watchdog"
                });

                campaign.TransparencyScore = score.Score;
                await _hub.Clients.Group($"campaign-{campaign.Id}")
                    .SendAsync("TransparencyUpdated", new { campaign.Id, score.Score, summary }, ct);
            }

            if (allFlags.Any(f => f.Severity == "High" && f.Status == "Open")
                && campaign.Status == CampaignStatus.Published)
            {
                campaign.Status = CampaignStatus.NeedsReview;
                await _hub.Clients.All.SendAsync("CampaignNeedsReview", new { campaign.Id, campaign.Title }, ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
