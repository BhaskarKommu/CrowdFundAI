using System.Text.Json;
using CrowdFund.Ai.Orchestration;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Services;

/// <summary>
/// Queue worker for Council runs, Watchdog, impact reports, webhooks, virus-scan follow-ups.
/// </summary>
public class BackgroundJobWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackgroundJobWorker> _logger;
    private readonly IHttpClientFactory _httpFactory;

    public BackgroundJobWorker(IServiceScopeFactory scopeFactory, ILogger<BackgroundJobWorker> logger, IHttpClientFactory httpFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _httpFactory = httpFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackgroundJobWorker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker loop error");
            }
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Prefer jobs linked to premium campaigns
        var jobs = await (
            from j in db.BackgroundJobs
            where j.Status == BackgroundJobStatus.Pending
                  && (j.RunAfter == null || j.RunAfter <= DateTime.UtcNow)
                  && j.Attempts < j.MaxAttempts
            join c in db.Campaigns on j.CampaignId equals c.Id into cj
            from c in cj.DefaultIfEmpty()
            orderby (c != null ? c.ReviewPriority : 0) descending, j.CreatedAt
            select j
        ).Take(10).ToListAsync(ct);

        foreach (var job in jobs)
        {
            job.Status = BackgroundJobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            job.Attempts++;
            await db.SaveChangesAsync(ct);
            try
            {
                await ExecuteJobAsync(scope.ServiceProvider, db, job, ct);
                job.Status = BackgroundJobStatus.Succeeded;
                job.CompletedAt = DateTime.UtcNow;
                job.LastError = null;
            }
            catch (Exception ex)
            {
                job.LastError = ex.Message;
                job.Status = job.Attempts >= job.MaxAttempts
                    ? BackgroundJobStatus.DeadLetter
                    : BackgroundJobStatus.Pending;
                job.RunAfter = DateTime.UtcNow.AddSeconds(Math.Pow(2, job.Attempts) * 5);
                _logger.LogWarning(ex, "Job {Id} {Type} failed attempt {Attempt}", job.Id, job.JobType, job.Attempts);
            }
            await db.SaveChangesAsync(ct);
        }

        // Deliver pending webhooks
        var hooks = await db.OutboundWebhooks
            .Where(w => !w.Delivered && w.Attempts < 5)
            .OrderBy(w => w.CreatedAt)
            .Take(10)
            .ToListAsync(ct);
        foreach (var hook in hooks)
        {
            hook.Attempts++;
            try
            {
                var client = _httpFactory.CreateClient("webhooks");
                using var content = new StringContent(hook.PayloadJson, System.Text.Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(hook.Url, content, ct);
                if (resp.IsSuccessStatusCode) hook.Delivered = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Webhook {Id} failed", hook.Id);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task ExecuteJobAsync(IServiceProvider sp, AppDbContext db, BackgroundJob job, CancellationToken ct)
    {
        switch (job.JobType)
        {
            case BackgroundJobType.VerificationCouncil:
            {
                if (!job.CampaignId.HasValue) return;
                var orchestrator = sp.GetRequiredService<VerificationOrchestrator>();
                var campaign = await db.Campaigns
                    .Include(c => c.Documents)
                    .FirstOrDefaultAsync(c => c.Id == job.CampaignId, ct);
                if (campaign == null) return;
                var expenses = await db.Expenses.Where(e => e.CampaignId == campaign.Id).ToListAsync(ct);
                var donations = await db.Donations.Where(d => d.CampaignId == campaign.Id).ToListAsync(ct);
                var history = await db.Campaigns.Where(c => c.CreatorId == campaign.CreatorId && c.Id != campaign.Id).Take(20).ToListAsync(ct);
                await orchestrator.VerifyAsync(campaign, campaign.Documents.ToList(), expenses, donations, history, null, ct);
                await db.SaveChangesAsync(ct);
                break;
            }
            case BackgroundJobType.ImpactReport:
            {
                if (!job.CampaignId.HasValue) return;
                var reports = sp.GetRequiredService<ImpactReportService>();
                var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == job.CampaignId, ct);
                if (campaign == null) return;
                var expenses = await db.Expenses.Where(e => e.CampaignId == campaign.Id).ToListAsync(ct);
                var donations = await db.Donations.Where(d => d.CampaignId == campaign.Id).ToListAsync(ct);
                var head = await db.AuditLedger.Where(e => e.CampaignId == campaign.Id)
                    .OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync(ct);
                var draft = await reports.DraftAsync(campaign, expenses, donations, head, ct);
                db.ImpactReports.Add(draft);
                await db.SaveChangesAsync(ct);
                break;
            }
            case BackgroundJobType.WatchdogScan:
            {
                // Watchdog hosted service remains primary; job is a forced single-campaign nudge
                break;
            }
            case BackgroundJobType.WebhookDelivery:
            {
                // handled in batch webhooks loop
                break;
            }
            default:
                break;
        }
    }
}
