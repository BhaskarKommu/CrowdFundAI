using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Data;

/// <summary>
/// Seeds ~50 campaigns plus donations, expenses, customers, matching pools, flags, ledger, FX.
/// Runs only when the Campaigns table is empty.
/// </summary>
public static class DummyDataSeeder
{
    private static readonly string[] Categories = { "Medical", "Education", "Disaster Relief", "Other" };
    private static readonly string[] Currencies = { "USD", "INR", "EUR", "GBP", "AED" };
    private static readonly string[] Cities = { "Mumbai", "Delhi", "Bangalore", "Hyderabad", "Chennai", "Pune", "Austin", "Seattle", "London", "Dubai" };
    private static readonly string[] Regions = { "MH", "DL", "KA", "TS", "TN", "MH", "TX", "WA", "ENG", "DU" };
    private static readonly string[] Countries = { "IN", "IN", "IN", "IN", "IN", "IN", "US", "US", "GB", "AE" };
    private static readonly string[] Vendors =
    {
        "City Hospital", "MedPlus Pharmacy", "Apollo Diagnostics", "School Supplies Co",
        "BuildRight Contractors", "Relief Logistics", "Local Clinic", "EduBooks Pvt Ltd"
    };
    private static readonly string[] ExpenseCats = { "treatment", "medicines", "tests", "travel", "other" };
    private static readonly string[] TitleTemplates =
    {
        "Help {0} with medical treatment",
        "School supplies for {0} village kids",
        "Emergency relief after flood in {0}",
        "Support {0}'s education fund",
        "Surgery fund for {0}",
        "Rebuild homes in {0}",
        "Cancer care for {0}",
        "Wheelchair and rehab for {0}",
        "Clean water project — {0}",
        "Scholarship fund — {0}"
    };
    private static readonly string[] FirstNames =
    {
        "Aarav", "Priya", "Rahul", "Ananya", "Vikram", "Sneha", "Omar", "Fatima", "James", "Sofia",
        "Mei", "Carlos", "Aisha", "Noah", "Zara", "Liam", "Isha", "Dev", "Maya", "Arjun",
        "Nina", "Sam", "Leila", "Kai", "Ravi", "Elena", "Hassan", "Yuki", "Amir", "Sara",
        "Ben", "Tara", "Jon", "Rita", "Alex", "Kim", "Leo", "Nora", "Max", "Ivy",
        "Jay", "Amy", "Tom", "Eva", "Ron", "Uma", "Ned", "Joy", "Gus", "Bea"
    };

    public static async Task SeedAsync(AppDbContext db, UserManager<IdentityUser> userMgr, ILogger logger)
    {
        if (await db.Campaigns.AnyAsync())
        {
            logger.LogInformation("Dummy seed skipped — campaigns already exist.");
            return;
        }

        var rng = new Random(42);
        var admin = await userMgr.FindByEmailAsync("admin@crowdfundai.demo");
        var adminId = admin?.Id ?? "seed-admin";

        // Creators
        var creatorIds = new List<string> { adminId };
        for (var i = 1; i <= 10; i++)
        {
            var email = $"creator{i}@crowdfundai.demo";
            var existing = await userMgr.FindByEmailAsync(email);
            if (existing == null)
            {
                var u = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
                var r = await userMgr.CreateAsync(u, "Creator123!");
                if (r.Succeeded)
                {
                    await userMgr.AddToRoleAsync(u, "Creator");
                    creatorIds.Add(u.Id);
                }
            }
            else creatorIds.Add(existing.Id);
        }

        // Donors
        var donorIds = new List<string>();
        for (var i = 1; i <= 25; i++)
        {
            var email = $"donor{i}@crowdfundai.demo";
            var existing = await userMgr.FindByEmailAsync(email);
            if (existing == null)
            {
                var u = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
                var r = await userMgr.CreateAsync(u, "Donor123!");
                if (r.Succeeded)
                {
                    await userMgr.AddToRoleAsync(u, "Donor");
                    donorIds.Add(u.Id);
                }
            }
            else donorIds.Add(existing.Id);
        }

        // Domain AppUsers — Campaign.CreatorId FK references AppUsers.Id (not AspNetUsers)
        async Task EnsureAppUserAsync(string id, string email, string displayName, string role)
        {
            if (await db.AppUsers.AnyAsync(u => u.Id == id))
                return;
            db.AppUsers.Add(new ApplicationUser
            {
                Id = id,
                Email = email,
                DisplayName = displayName,
                Role = role,
                CreatedAt = DateTime.UtcNow
            });
        }

        await EnsureAppUserAsync(adminId, "admin@crowdfundai.demo", "Demo Admin", "Admin");
        var creatorIndex = 0;
        foreach (var id in creatorIds)
        {
            if (id == adminId) continue;
            creatorIndex++;
            await EnsureAppUserAsync(id, $"creator{creatorIndex}@crowdfundai.demo", $"Creator {creatorIndex}", "Creator");
        }
        for (var i = 0; i < donorIds.Count; i++)
        {
            await EnsureAppUserAsync(
                donorIds[i],
                $"donor{i + 1}@crowdfundai.demo",
                FirstNames[i % FirstNames.Length] + " Donor",
                "Donor");
        }
        await db.SaveChangesAsync();

        // Customer profiles
        var profiles = new List<CustomerProfile>();
        for (var i = 0; i < donorIds.Count; i++)
        {
            var cityIdx = i % Cities.Length;
            profiles.Add(new CustomerProfile
            {
                UserId = donorIds[i],
                Email = $"donor{i + 1}@crowdfundai.demo",
                DisplayName = FirstNames[i % FirstNames.Length] + " Donor",
                Phone = $"+919{i:000000000}",
                BillingName = FirstNames[i % FirstNames.Length],
                BillingLine1 = $"{100 + i} Main Street",
                BillingCity = Cities[cityIdx],
                BillingRegion = Regions[cityIdx],
                BillingPostalCode = $"{400000 + i}",
                BillingCountry = Countries[cityIdx],
                MarketingOptIn = i % 2 == 0,
                PayPalPayerId = $"PAYER-DEMO-{i + 1:000}",
                PayPalCustomerId = $"CUST-DEMO-{i + 1:000}"
            });
        }
        db.CustomerProfiles.AddRange(profiles);

        // Demo organization
        db.Organizations.Add(new Organization
        {
            Name = "CrowdFund AI Demo Foundation",
            HomeCurrency = "USD",
            OwnerUserId = adminId
        });

        var statuses = new[]
        {
            CampaignStatus.Published, CampaignStatus.Published, CampaignStatus.Published,
            CampaignStatus.Published, CampaignStatus.UnderReview, CampaignStatus.NeedsReview,
            CampaignStatus.Draft, CampaignStatus.Closed, CampaignStatus.Published, CampaignStatus.Frozen
        };

        var campaigns = new List<Campaign>();
        for (var i = 0; i < 50; i++)
        {
            var name = FirstNames[i % FirstNames.Length];
            var cat = Categories[i % Categories.Length];
            var cityIdx = i % Cities.Length;
            var status = statuses[i % statuses.Length];
            var goal = 5000m + (i * 1500m) + rng.Next(500, 5000);
            var trust = status == CampaignStatus.Published ? 55 + rng.Next(0, 40)
                : status == CampaignStatus.Frozen ? rng.Next(10, 35)
                : rng.Next(0, 50);

            campaigns.Add(new Campaign
            {
                CreatorId = creatorIds[i % creatorIds.Count],
                Title = string.Format(TitleTemplates[i % TitleTemplates.Length], name) + $" #{i + 1}",
                Story = $"Demo campaign #{i + 1} for {name}. Category: {cat}. " +
                        "Funds are for verified expenses only. Seeded for development and testing.",
                Category = cat,
                GoalAmount = goal,
                Currency = Currencies[i % Currencies.Length],
                Status = status,
                TrustScore = trust,
                TransparencyScore = Math.Clamp(trust - 5 + rng.Next(0, 15), 0, 100),
                RaisedAmount = 0,
                DonorCount = 0,
                PublishedAt = status is CampaignStatus.Published or CampaignStatus.Closed or CampaignStatus.Frozen
                    ? DateTime.UtcNow.AddDays(-rng.Next(1, 90))
                    : null,
                CreatedAt = DateTime.UtcNow.AddDays(-rng.Next(5, 120)),
                LocationCity = Cities[cityIdx],
                LocationRegion = Regions[cityIdx],
                LocationCountry = Countries[cityIdx],
                LocationLabel = $"{Cities[cityIdx]}, {Regions[cityIdx]}, {Countries[cityIdx]}",
                FundsFrozen = status == CampaignStatus.Frozen,
                FreezeReason = status == CampaignStatus.Frozen ? "Demo freeze — high risk flags" : null,
                PremiumReview = i % 7 == 0,
                TemplateKey = cat.ToLowerInvariant(),
                CoverImagePath = cat switch
                {
                    "Medical" => $"/images/campaigns/medical-{(i % 2) + 1}.svg",
                    "Education" => $"/images/campaigns/education-{(i % 2) + 1}.svg",
                    "Disaster Relief" => $"/images/campaigns/disaster-{(i % 2) + 1}.svg",
                    _ => $"/images/campaigns/other-{(i % 2) + 1}.svg"
                }
            });
        }
        db.Campaigns.AddRange(campaigns);
        await db.SaveChangesAsync();

        // Donations
        var donations = new List<Donation>();
        foreach (var c in campaigns.Where(c =>
                     c.Status is CampaignStatus.Published or CampaignStatus.Closed or CampaignStatus.Frozen))
        {
            var n = 3 + rng.Next(0, 6);
            decimal sum = 0;
            for (var d = 0; d < n; d++)
            {
                var amount = Math.Round((decimal)(25 + rng.NextDouble() * 500), 2);
                var tip = Math.Round(amount * 0.05m, 2);
                var platformFee = Math.Round(amount * 0.03m, 2);
                var processorFee = Math.Round(amount * 0.029m + 0.30m, 2);
                var donorId = donorIds.Count > 0 ? donorIds[rng.Next(donorIds.Count)] : null;
                var captured = c.Status != CampaignStatus.Frozen || d % 2 == 0;
                sum += amount;
                var emailBytes = System.Text.Encoding.UTF8.GetBytes(donorId ?? $"guest-{c.Id}-{d}");
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(emailBytes));

                donations.Add(new Donation
                {
                    CampaignId = c.Id,
                    DonorId = donorId,
                    Amount = amount,
                    TipAmount = tip,
                    PlatformFee = platformFee,
                    PaymentProcessorFeeEstimate = processorFee,
                    NetToCampaign = amount - platformFee - processorFee,
                    Currency = c.Currency,
                    Status = captured ? DonationStatus.Captured : DonationStatus.Pending,
                    PayPalOrderId = $"ORDER-DEMO-{Guid.NewGuid():N}"[..24],
                    PayPalCaptureId = captured ? $"CAP-DEMO-{Guid.NewGuid():N}"[..24] : null,
                    CreatedAt = DateTime.UtcNow.AddDays(-rng.Next(0, 60)),
                    CapturedAt = captured ? DateTime.UtcNow.AddDays(-rng.Next(0, 55)) : null,
                    PayerEmailHash = hash[..16]
                });
            }
            c.RaisedAmount = Math.Min(c.GoalAmount * 1.2m, sum);
            c.DonorCount = n;
        }
        db.Donations.AddRange(donations);

        // Expenses
        var expenses = new List<Expense>();
        foreach (var c in campaigns.Where(c => c.RaisedAmount > 0).Take(40))
        {
            var n = 2 + rng.Next(0, 4);
            for (var e = 0; e < n; e++)
            {
                expenses.Add(new Expense
                {
                    CampaignId = c.Id,
                    Date = DateTime.UtcNow.AddDays(-rng.Next(1, 45)),
                    Vendor = Vendors[rng.Next(Vendors.Length)],
                    Description = $"Demo expense {e + 1} for campaign",
                    Amount = Math.Round((decimal)(50 + rng.NextDouble() * 800), 2),
                    Currency = c.Currency,
                    Category = ExpenseCats[e % ExpenseCats.Length],
                    Status = (ExpenseStatus)(e % 5)
                });
            }
        }
        db.Expenses.AddRange(expenses);

        // Matching programs
        var matchPrograms = new List<MatchingGiftProgram>();
        foreach (var c in campaigns.Where(c => c.Status == CampaignStatus.Published).Take(10))
        {
            matchPrograms.Add(new MatchingGiftProgram
            {
                CampaignId = c.Id,
                SponsorName = "Demo Sponsor Corp",
                Headline = $"Match up to 50% for {c.Category}",
                MatchRatio = 0.5m,
                MaxPerDonation = 250m,
                MinDonation = 25m,
                PoolTotal = 5000m,
                PoolRemaining = 5000m,
                TotalMatched = 0,
                MatchCount = 0,
                Status = MatchCampaignStatus.Active,
                EligibleCategories = c.Category,
                FirstTimeDonorsOnly = false
            });
        }
        matchPrograms.Add(new MatchingGiftProgram
        {
            CampaignId = null,
            SponsorName = "Platform Match Pool",
            Headline = "Platform-wide 1:1 match (demo)",
            MatchRatio = 1.0m,
            MaxPerDonation = 100m,
            MinDonation = 10m,
            PoolTotal = 20000m,
            PoolRemaining = 20000m,
            TotalMatched = 0,
            MatchCount = 0,
            Status = MatchCampaignStatus.Active,
            EligibleCategories = "Medical,Education,Disaster Relief,Other",
            FirstTimeDonorsOnly = true
        });
        db.MatchingGiftPrograms.AddRange(matchPrograms);

        // Fraud flags
        foreach (var c in campaigns.Where(c => c.TrustScore < 40 || c.Status == CampaignStatus.Frozen).Take(8))
        {
            db.FraudFlags.Add(new FraudFlag
            {
                CampaignId = c.Id,
                Rule = "demo_high_risk",
                Severity = "High",
                Score = 0.85,
                Explanation = "Seeded high-severity flag for testing admin queues.",
                Status = "Open",
                CreatedAt = DateTime.UtcNow.AddDays(-rng.Next(1, 20))
            });
        }

        // Audit ledger
        var prev = "";
        foreach (var c in campaigns.Take(15))
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new { c.Title, c.Status });
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{c.Id}|{prev}|{payload}"))).ToLowerInvariant();
            db.AuditLedger.Add(new AuditLedgerEntry
            {
                CampaignId = c.Id,
                EventType = "CampaignSeeded",
                PayloadJson = payload,
                PrevHash = prev,
                Hash = hash,
                CreatedAt = c.CreatedAt
            });
            prev = hash;
        }

        // FX rates
        db.FxRates.AddRange(
            new FxRateSnapshot { BaseCurrency = "USD", QuoteCurrency = "INR", Rate = 83.25m, AsOfUtc = DateTime.UtcNow, Source = "seed" },
            new FxRateSnapshot { BaseCurrency = "USD", QuoteCurrency = "EUR", Rate = 0.92m, AsOfUtc = DateTime.UtcNow, Source = "seed" },
            new FxRateSnapshot { BaseCurrency = "USD", QuoteCurrency = "GBP", Rate = 0.79m, AsOfUtc = DateTime.UtcNow, Source = "seed" },
            new FxRateSnapshot { BaseCurrency = "USD", QuoteCurrency = "AED", Rate = 3.67m, AsOfUtc = DateTime.UtcNow, Source = "seed" },
            new FxRateSnapshot { BaseCurrency = "INR", QuoteCurrency = "USD", Rate = 0.012m, AsOfUtc = DateTime.UtcNow, Source = "seed" }
        );

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Dummy seed complete: {Campaigns} campaigns, {Donations} donations, {Expenses} expenses, {Profiles} profiles, {Match} matching programs",
            campaigns.Count, donations.Count, expenses.Count, profiles.Count, matchPrograms.Count);
    }
}
