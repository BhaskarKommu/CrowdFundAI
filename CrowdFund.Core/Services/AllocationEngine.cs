using CrowdFund.Core.Entities;

namespace CrowdFund.Core.Services;

/// <summary>
/// Deterministic allocation of expenses across donors (pro-rata by share of total raised at time of spending).
/// LLM never computes money; this does.
/// </summary>
public class AllocationEngine
{
    public record CategoryAllocation(string Category, decimal Amount, decimal Percent, decimal Remaining);
    public record DonorAllocation(Guid DonationId, decimal Amount, IReadOnlyList<CategoryAllocation> Categories);

    public IReadOnlyList<CategoryAllocation> AllocateByCategory(
        decimal totalRaised,
        IEnumerable<Expense> verifiedExpenses)
    {
        if (totalRaised <= 0) return Array.Empty<CategoryAllocation>();

        var byCategory = verifiedExpenses
            .Where(e => e.Status == ExpenseStatus.Verified)
            .GroupBy(e => e.Category)
            .Select(g => new
            {
                Category = g.Key,
                Amount = g.Sum(e => e.Amount)
            })
            .ToList();

        var totalSpent = byCategory.Sum(x => x.Amount);
        var remaining = Math.Max(0, totalRaised - totalSpent);

        return byCategory
            .Select(x => new CategoryAllocation(
                x.Category,
                x.Amount,
                totalRaised > 0 ? Math.Round(x.Amount / totalRaised * 100, 2) : 0,
                remaining))
            .ToList();
    }

    public decimal DonorShareOfCategory(decimal donationAmount, decimal totalRaisedAtSpend, decimal categorySpend)
    {
        if (totalRaisedAtSpend <= 0) return 0;
        return Math.Round(donationAmount / totalRaisedAtSpend * categorySpend, 2);
    }
}
