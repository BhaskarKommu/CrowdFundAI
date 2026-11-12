using System.Text.Json;
using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

public class BackgroundJobQueue
{
    public BackgroundJob Enqueue(
        BackgroundJobType type,
        object? payload = null,
        Guid? tenantId = null,
        Guid? campaignId = null,
        DateTime? runAfter = null)
    {
        return new BackgroundJob
        {
            JobType = type,
            PayloadJson = payload == null ? "{}" : JsonSerializer.Serialize(payload),
            TenantId = tenantId,
            CampaignId = campaignId,
            RunAfter = runAfter,
            Status = BackgroundJobStatus.Pending
        };
    }
}
