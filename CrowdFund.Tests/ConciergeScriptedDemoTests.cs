using CrowdFund.Ai.Concierge;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrowdFund.Tests;

/// <summary>
/// Step 5 Definition of Done: five scripted questions with citations + one refusal.
/// </summary>
public class ConciergeScriptedDemoTests
{
    private readonly DonorConcierge _concierge;
    private readonly Campaign _campaign;
    private readonly Donation _donation;
    private readonly List<Expense> _expenses;

    public ConciergeScriptedDemoTests()
    {
        _concierge = new DonorConcierge(
            new FakeLlmClient(NullLogger<FakeLlmClient>.Instance),
            new ConciergeTools(),
            new FollowMyMoneyService(new AllocationEngine()));

        _campaign = new Campaign
        {
            Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Title = "Help Little Aarav Fight Cancer",
            Status = CampaignStatus.Published,
            TrustScore = 92,
            TransparencyScore = 88,
            RaisedAmount = 3900,
            GoalAmount = 50000,
            Currency = "USD",
            Category = "Medical",
            Story = "Medical treatment for Aarav"
        };
        _donation = new Donation
        {
            Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            CampaignId = _campaign.Id,
            Amount = 100,
            Currency = "USD",
            Status = DonationStatus.Captured
        };
        var expId = Guid.Parse("99999999-8888-7777-6666-555555555555");
        _expenses =
        [
            new Expense
            {
                Id = expId,
                CampaignId = _campaign.Id,
                Vendor = "City Hospital",
                Amount = 2500,
                Category = "medicines",
                Status = ExpenseStatus.Verified,
                Date = DateTime.UtcNow.Date,
                Description = "Medicines"
            }
        ];
    }

    [Fact]
    public async Task Q1_CurrentStatus_HasTrustAndCitation()
    {
        var r = await _concierge.AnswerAsync("What is the current status?", _campaign, null, _expenses, new[] { _campaign });
        Assert.Contains("92", r.Answer);
        Assert.Contains(r.Citations, c => c.Contains("campaign:"));
        Assert.True(r.GroundingOk);
    }

    [Fact]
    public async Task Q2_WhereIsMoneyGoing_HasExpenseCitation()
    {
        var r = await _concierge.AnswerAsync("Where is my money going?", _campaign, _donation, _expenses, new[] { _campaign });
        Assert.True(r.ToolsUsed.Contains("ListExpenses") || r.ToolsUsed.Contains("GetDonationAllocation"));
        Assert.Contains(r.Citations, c => c.StartsWith("expense:") || c.StartsWith("donation:"));
        Assert.True(r.GroundingOk);
    }

    [Fact]
    public async Task Q3_Hindi_RespondsInHindiScriptOrLocalized()
    {
        var r = await _concierge.AnswerAsync("मेरे पैसे कहाँ खर्च हुए?", _campaign, _donation, _expenses, new[] { _campaign });
        Assert.Equal("hi", r.Language);
        Assert.NotEmpty(r.Answer);
        Assert.True(r.GroundingOk);
    }

    [Fact]
    public async Task Q4_Telugu_RespondsWithTeLanguage()
    {
        var r = await _concierge.AnswerAsync("ప్రస్తుత స్థితి ఏమిటి?", _campaign, null, _expenses, new[] { _campaign });
        Assert.Equal("te", r.Language);
        Assert.Contains("92", r.Answer);
    }

    [Fact]
    public async Task Q5_Donate_PrepareOnly_NoMoneyMove()
    {
        var r = await _concierge.AnswerAsync("donate 25 dollars to this campaign", _campaign, null, _expenses, new[] { _campaign });
        Assert.Contains("PrepareDonation", r.ToolsUsed);
        Assert.NotNull(r.PrepareDonationCard);
        Assert.Contains("cannot", r.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Q6_Unanswerable_Refuses()
    {
        var r = await _concierge.AnswerAsync("What is the capital of Atlantis and the secret PIN?", null, null, Array.Empty<Expense>(), Array.Empty<Campaign>());
        Assert.Empty(r.ToolsUsed);
        Assert.NotEmpty(r.Answer);
    }
}
