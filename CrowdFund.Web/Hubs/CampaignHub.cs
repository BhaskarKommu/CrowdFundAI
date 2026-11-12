using Microsoft.AspNetCore.SignalR;

namespace CrowdFund.Web.Hubs;

public class CampaignHub : Hub
{
    public async Task JoinCampaign(string campaignId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"campaign-{campaignId}");
    }

    public async Task LeaveCampaign(string campaignId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"campaign-{campaignId}");
    }
}
