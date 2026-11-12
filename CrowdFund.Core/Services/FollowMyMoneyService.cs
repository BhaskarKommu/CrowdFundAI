using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

/// <summary>
/// A4 Follow My Money — deterministic pro-rata allocation.
/// Donor share of category = donation / totalRaised × categorySpend.
/// </summary>
public class FollowMyMoneyService
{
    private readonly AllocationEngine _allocation;

    public FollowMyMoneyService(AllocationEngine allocation) => _allocation = allocation;

    public record MoneyFlowNode(string Id, string Name, decimal Value);
    public record MoneyFlowLink(string Source, string Target, decimal Value);
    public record MoneyFlow(IReadOnlyList<MoneyFlowNode> Nodes, IReadOnlyList<MoneyFlowLink> Links);

    public record CategoryRow(string Category, decimal Amount, decimal Percent, decimal DonorShare);
    public record ExpenseRow(Guid Id, string Vendor, string Description, decimal Amount, string Category, string Status, DateTime Date);
    public record DonorBreakdown(
        Guid DonationId,
        decimal DonationAmount,
        string Currency,
        decimal TotalRaised,
        decimal TotalSpent,
        decimal Remaining,
        IReadOnlyList<CategoryRow> Categories,
        IReadOnlyList<ExpenseRow> Expenses,
        MoneyFlow Sankey);

    public DonorBreakdown BuildForDonation(
        Donation donation,
        Campaign campaign,
        IEnumerable<Expense> verifiedExpenses)
    {
        var expenses = verifiedExpenses
            .Where(e => e.Status is ExpenseStatus.Verified or ExpenseStatus.AiExtracted)
            .OrderBy(e => e.Date)
            .ToList();

        var totalRaised = campaign.RaisedAmount > 0 ? campaign.RaisedAmount : donation.Amount;
        var totalSpent = expenses.Sum(e => e.Amount);
        var remaining = Math.Max(0, totalRaised - totalSpent);

        var byCat = expenses
            .GroupBy(e => string.IsNullOrEmpty(e.Category) ? "other" : e.Category)
            .Select(g => new
            {
                Category = g.Key,
                Amount = g.Sum(e => e.Amount)
            })
            .ToList();

        var categories = byCat.Select(c =>
        {
            var pct = totalRaised > 0 ? Math.Round(c.Amount / totalRaised * 100, 2) : 0;
            var donorShare = _allocation.DonorShareOfCategory(donation.Amount, totalRaised, c.Amount);
            return new CategoryRow(c.Category, c.Amount, pct, donorShare);
        }).ToList();

        var expenseRows = expenses.Select(e => new ExpenseRow(
            e.Id, e.Vendor, e.Description, e.Amount, e.Category, e.Status.ToString(), e.Date)).ToList();

        // Sankey: Donation → Categories → Expenses
        var nodes = new List<MoneyFlowNode>
        {
            new("donation", $"Your donation ({donation.Amount:N0} {donation.Currency})", donation.Amount)
        };
        var links = new List<MoneyFlowLink>();

        foreach (var c in categories)
        {
            var nodeId = $"cat-{c.Category}";
            nodes.Add(new MoneyFlowNode(nodeId, $"{c.Category} ({c.Amount:N0})", c.Amount));
            links.Add(new MoneyFlowLink("donation", nodeId, c.DonorShare > 0 ? c.DonorShare : c.Amount * (donation.Amount / Math.Max(totalRaised, 0.01m))));
        }

        if (remaining > 0)
        {
            nodes.Add(new MoneyFlowNode("remaining", $"Remaining ({remaining:N0})", remaining));
            var remShare = donation.Amount / Math.Max(totalRaised, 0.01m) * remaining;
            links.Add(new MoneyFlowLink("donation", "remaining", Math.Round(remShare, 2)));
        }

        // Optional: category → top expenses
        foreach (var e in expenses.Take(8))
        {
            var eid = $"exp-{e.Id:N}"[..12];
            var catId = $"cat-{(string.IsNullOrEmpty(e.Category) ? "other" : e.Category)}";
            nodes.Add(new MoneyFlowNode(eid, $"{e.Vendor}: {e.Amount:N0}", e.Amount));
            var share = donation.Amount / Math.Max(totalRaised, 0.01m) * e.Amount;
            links.Add(new MoneyFlowLink(catId, eid, Math.Round(share, 2)));
        }

        return new DonorBreakdown(
            donation.Id,
            donation.Amount,
            donation.Currency,
            totalRaised,
            totalSpent,
            remaining,
            categories,
            expenseRows,
            new MoneyFlow(nodes, links));
    }

    public string AllocationMethodSentence() =>
        "Each expense is allocated pro rata across donors by share of total raised at the time of spending (donation ÷ total raised × category spend).";
}
