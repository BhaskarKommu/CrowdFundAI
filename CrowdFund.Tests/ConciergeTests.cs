using CrowdFund.Ai.Concierge;
using CrowdFund.Ai.Watchdog;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrowdFund.Tests;

public class ConciergeGroundingTests
{
    [Fact]
    public void GroundingCheck_FailsWhenNumberNotInTools()
    {
        var tools = new[] { "{\"amount\":25.00,\"raised\":1000}" };
        Assert.False(DonorConcierge.GroundingCheck("You donated 99999 dollars", tools));
        Assert.True(DonorConcierge.GroundingCheck("You donated 25.00 of 1000 raised", tools));
    }

    [Fact]
    public async Task Concierge_AnswersStatusWithCitations()
    {
        var concierge = new DonorConcierge(
            new FakeLlmClient(NullLogger<FakeLlmClient>.Instance),
            new ConciergeTools(),
            new FollowMyMoneyService(new AllocationEngine()));

        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            Title = "Help Little Aarav Fight Cancer",
            Status = CampaignStatus.Published,
            TrustScore = 92,
            TransparencyScore = 88,
            RaisedAmount = 3900,
            GoalAmount = 50000,
            Currency = "USD",
            Category = "Medical",
            Story = "Medical campaign"
        };

        var reply = await concierge.AnswerAsync(
            "What is the current status?",
            campaign,
            null,
            Array.Empty<Expense>(),
            new[] { campaign });

        Assert.Contains("92", reply.Answer);
        Assert.Contains(reply.Citations, c => c.StartsWith("campaign:"));
        Assert.True(reply.GroundingOk);
        Assert.Contains("GetCampaignStatus", reply.ToolsUsed);
    }

    [Fact]
    public async Task Concierge_RefusesWithoutContext()
    {
        var concierge = new DonorConcierge(
            new FakeLlmClient(NullLogger<FakeLlmClient>.Instance),
            new ConciergeTools(),
            new FollowMyMoneyService(new AllocationEngine()));

        var reply = await concierge.AnswerAsync(
            "What color is the sky on Mars?",
            null,
            null,
            Array.Empty<Expense>(),
            Array.Empty<Campaign>());

        Assert.NotEmpty(reply.Answer);
        Assert.Empty(reply.ToolsUsed); // refused without tools
    }

    [Fact]
    public async Task Concierge_PrepareDonation_DoesNotMoveMoney()
    {
        var concierge = new DonorConcierge(
            new FakeLlmClient(NullLogger<FakeLlmClient>.Instance),
            new ConciergeTools(),
            new FollowMyMoneyService(new AllocationEngine()));

        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            Title = "Test",
            Currency = "USD",
            Status = CampaignStatus.Published,
            GoalAmount = 1000
        };

        var reply = await concierge.AnswerAsync("donate 25 dollars to this campaign", campaign, null,
            Array.Empty<Expense>(), new[] { campaign });

        Assert.NotNull(reply.PrepareDonationCard);
        Assert.Contains("PrepareDonation", reply.ToolsUsed);
        Assert.Contains("cannot", reply.Answer, StringComparison.OrdinalIgnoreCase);
    }
}

public class TransparencyScoreTests
{
    [Fact]
    public void Score_IncreasesWithVerifiedExpensesAndLedger()
    {
        var svc = new TransparencyScoreService();
        var campaign = new Campaign { Id = Guid.NewGuid() };
        var expenses = new[]
        {
            new Expense
            {
                Status = ExpenseStatus.Verified,
                Amount = 100,
                Evidences = { new ExpenseEvidence { Sha256 = "abc" } }
            }
        };
        var low = svc.Compute(campaign, Array.Empty<Expense>(), Array.Empty<CampaignUpdate>(),
            Array.Empty<FraudFlag>(), false);
        var high = svc.Compute(campaign, expenses, Array.Empty<CampaignUpdate>(),
            Array.Empty<FraudFlag>(), true);
        Assert.True(high.Score >= low.Score);
        Assert.InRange(high.Score, 0, 100);
    }
}
