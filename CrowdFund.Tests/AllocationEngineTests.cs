using CrowdFund.Core.Entities;
using CrowdFund.Core.Services;
using Xunit;

namespace CrowdFund.Tests;

public class AllocationEngineTests
{
    private readonly AllocationEngine _engine = new();

    [Fact]
    public void DonorShare_IsProRata()
    {
        // donation 100, total raised 1000, category spend 400 → share 40
        var share = _engine.DonorShareOfCategory(100m, 1000m, 400m);
        Assert.Equal(40m, share);
    }

    [Fact]
    public void DonorShare_ZeroRaised_ReturnsZero()
    {
        Assert.Equal(0m, _engine.DonorShareOfCategory(50m, 0m, 100m));
    }

    [Fact]
    public void AllocateByCategory_ComputesPercents()
    {
        var expenses = new List<Expense>
        {
            new() { Category = "medicines", Amount = 2500, Status = ExpenseStatus.Verified },
            new() { Category = "tests", Amount = 1000, Status = ExpenseStatus.Verified },
            new() { Category = "medicines", Amount = 500, Status = ExpenseStatus.Verified },
            new() { Category = "other", Amount = 100, Status = ExpenseStatus.Submitted } // ignored
        };
        var rows = _engine.AllocateByCategory(10000m, expenses);
        Assert.Equal(2, rows.Count); // medicines + tests only (verified)
        var med = rows.First(r => r.Category == "medicines");
        Assert.Equal(3000m, med.Amount);
        Assert.Equal(30m, med.Percent);
    }
}

public class LedgerServiceTests
{
    private readonly LedgerService _ledger = new();

    [Fact]
    public void Chain_ValidWhenUnbroken()
    {
        var e1 = _ledger.CreateEntry(Guid.NewGuid(), "donation.captured", new { amount = 10 }, "");
        e1.Id = 1;
        var e2 = _ledger.CreateEntry(e1.CampaignId, "expense.added", new { amount = 5 }, e1.Hash);
        e2.Id = 2;
        var (valid, broken) = _ledger.VerifyChain(new[] { e1, e2 });
        Assert.True(valid);
        Assert.Null(broken);
    }

    [Fact]
    public void Chain_DetectsTamper()
    {
        var e1 = _ledger.CreateEntry(Guid.NewGuid(), "donation.captured", new { amount = 10 }, "");
        e1.Id = 1;
        var e2 = _ledger.CreateEntry(e1.CampaignId, "expense.added", new { amount = 5 }, e1.Hash);
        e2.Id = 2;
        e2.PayloadJson = "{\"amount\":999}"; // tamper
        var (valid, broken) = _ledger.VerifyChain(new[] { e1, e2 });
        Assert.False(valid);
        Assert.Equal(2, broken);
    }
}

public class FollowMyMoneyTests
{
    [Fact]
    public void Breakdown_IncludesRemainingAndCategories()
    {
        var fmm = new FollowMyMoneyService(new AllocationEngine());
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            Title = "Test",
            RaisedAmount = 1000,
            Currency = "USD",
            Status = CampaignStatus.Published
        };
        var donation = new Donation
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            Amount = 200,
            Currency = "USD",
            Status = DonationStatus.Captured
        };
        var expenses = new[]
        {
            new Expense { Category = "medicines", Amount = 300, Status = ExpenseStatus.Verified, Vendor = "Pharm", Date = DateTime.UtcNow },
            new Expense { Category = "tests", Amount = 200, Status = ExpenseStatus.Verified, Vendor = "Lab", Date = DateTime.UtcNow }
        };
        var bd = fmm.BuildForDonation(donation, campaign, expenses);
        Assert.Equal(1000, bd.TotalRaised);
        Assert.Equal(500, bd.TotalSpent);
        Assert.Equal(500, bd.Remaining);
        Assert.Equal(2, bd.Categories.Count);
        // donor 20% of medicines 300 = 60
        Assert.Equal(60m, bd.Categories.First(c => c.Category == "medicines").DonorShare);
        Assert.NotEmpty(bd.Sankey.Nodes);
        Assert.NotEmpty(bd.Sankey.Links);
    }
}
