using System.Security.Claims;
using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class CustomerManagementEndpoints
{
    public static void MapCustomerManagementEndpoints(this WebApplication app)
    {
        // Self-service
        app.MapPost("/api/customers/register", RegisterCustomer);
        app.MapGet("/api/customers/me", GetMe).RequireAuthorization();
        app.MapPut("/api/customers/me", UpdateMe).RequireAuthorization();
        app.MapPut("/api/customers/me/billing", UpdateBilling).RequireAuthorization();
        app.MapGet("/api/customers/me/payments", MyPayments).RequireAuthorization();
        app.MapGet("/api/customers/me/subscriptions", MySubscriptions).RequireAuthorization();
        app.MapGet("/api/customers/me/refunds", MyRefunds).RequireAuthorization();
        app.MapGet("/api/customers/me/saved-methods", MySavedMethods).RequireAuthorization();
        app.MapPost("/api/customers/me/saved-methods", SaveMethod).RequireAuthorization();
        app.MapDelete("/api/customers/me/saved-methods/{id:guid}", RemoveMethod).RequireAuthorization();

        // Admin
        var admin = app.MapGroup("/api/admin/customers").RequireAuthorization();
        admin.MapGet("/", SearchCustomers);
        admin.MapGet("/{id:guid}", GetCustomer);
        admin.MapGet("/{id:guid}/payments", AdminPayments);
        admin.MapGet("/{id:guid}/subscriptions", AdminSubscriptions);
        admin.MapGet("/{id:guid}/refunds", AdminRefunds);
        admin.MapGet("/{id:guid}/transactions/search", AdminTxnSearch);
        admin.MapPost("/{id:guid}/associate-paypal", AssociatePayPal);
        admin.MapPost("/ensure-from-user/{userId}", EnsureFromUser);
    }

    private static string? Uid(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier);
    /// <summary>Reject anything that looks like a full PAN or CVV. Only allow 4 digit last4.</summary>
    private static string? SanitizeLast4(string? last4)
    {
        if (string.IsNullOrWhiteSpace(last4)) return null;
        var digits = new string(last4.Where(char.IsDigit).ToArray());
        if (digits.Length != 4) return null; // never store longer digit sequences
        return digits;
    }

    private static bool LooksLikeSensitivePaymentData(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        // 13–19 digits ≈ PAN; 3–4 isolated CVV alone is ambiguous — reject long digit runs in token fields
        return digits.Length >= 12;
    }

    private static string? EmailClaim(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.Email) ?? http.User.Identity?.Name;

    private static async Task<CustomerProfile> EnsureProfile(AppDbContext db, string userId, string? email, string? name)
    {
        var profile = await db.CustomerProfiles.FirstOrDefaultAsync(c => c.UserId == userId);
        if (profile != null) return profile;
        profile = await db.CustomerProfiles.FirstOrDefaultAsync(c => email != null && c.Email == email);
        if (profile != null)
        {
            profile.UserId ??= userId;
            return profile;
        }
        profile = new CustomerProfile
        {
            UserId = userId,
            Email = email,
            DisplayName = name ?? email ?? "Customer"
        };
        db.CustomerProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    private static async Task<IResult> RegisterCustomer(
        [FromBody] RegisterCustomerRequest req,
        UserManager<IdentityUser> users,
        AppDbContext db)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Results.BadRequest(new { error = "Email and password required" });

        var existing = await users.FindByEmailAsync(req.Email);
        if (existing != null)
            return Results.Conflict(new { error = "Email already registered" });

        var user = new IdentityUser
        {
            UserName = req.Email,
            Email = req.Email,
            EmailConfirmed = true
        };
        var result = await users.CreateAsync(user, req.Password);
        if (!result.Succeeded)
            return Results.BadRequest(new { errors = result.Errors.Select(e => e.Description) });

        await users.AddToRoleAsync(user, string.IsNullOrWhiteSpace(req.Role) ? "Donor" : req.Role);

        // App user mirror if used
        if (!await db.AppUsers.AnyAsync(u => u.Email == req.Email))
        {
            db.AppUsers.Add(new ApplicationUser
            {
                Id = user.Id,
                Email = req.Email,
                DisplayName = req.DisplayName ?? req.Email.Split('@')[0],
                Role = string.IsNullOrWhiteSpace(req.Role) ? "Donor" : req.Role
            });
        }

        var profile = new CustomerProfile
        {
            UserId = user.Id,
            Email = req.Email,
            DisplayName = req.DisplayName ?? req.Email.Split('@')[0],
            Phone = req.Phone,
            PayPalPayerId = req.PayPalPayerId,
            PayPalCustomerId = req.PayPalCustomerId,
            BillingName = req.BillingName,
            BillingLine1 = req.BillingLine1,
            BillingCity = req.BillingCity,
            BillingRegion = req.BillingRegion,
            BillingPostalCode = req.BillingPostalCode,
            BillingCountry = req.BillingCountry
        };
        db.CustomerProfiles.Add(profile);
        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            userId = user.Id,
            customerId = profile.Id,
            email = profile.Email,
            message = "Customer registered and associated"
        });
    }

    private static async Task<IResult> GetMe(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var profile = await EnsureProfile(db, uid, EmailClaim(http), http.User.Identity?.Name);
        await db.SaveChangesAsync();
        return Results.Ok(profile);
    }

    private static async Task<IResult> UpdateMe([FromBody] UpdateCustomerRequest req, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var profile = await EnsureProfile(db, uid, EmailClaim(http), null);
        if (req.DisplayName != null) profile.DisplayName = req.DisplayName;
        if (req.Phone != null) profile.Phone = req.Phone;
        if (req.PayPalPayerId != null) profile.PayPalPayerId = req.PayPalPayerId;
        if (req.PayPalCustomerId != null) profile.PayPalCustomerId = req.PayPalCustomerId;
        profile.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(profile);
    }

    private static async Task<IResult> UpdateBilling([FromBody] BillingRequest req, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var profile = await EnsureProfile(db, uid, EmailClaim(http), null);
        profile.BillingName = req.BillingName ?? profile.BillingName;
        profile.BillingLine1 = req.BillingLine1 ?? profile.BillingLine1;
        profile.BillingLine2 = req.BillingLine2 ?? profile.BillingLine2;
        profile.BillingCity = req.BillingCity ?? profile.BillingCity;
        profile.BillingRegion = req.BillingRegion ?? profile.BillingRegion;
        profile.BillingPostalCode = req.BillingPostalCode ?? profile.BillingPostalCode;
        profile.BillingCountry = req.BillingCountry ?? profile.BillingCountry;
        profile.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            profile.Id,
            billing = new
            {
                profile.BillingName, profile.BillingLine1, profile.BillingLine2,
                profile.BillingCity, profile.BillingRegion, profile.BillingPostalCode, profile.BillingCountry
            }
        });
    }

    private static async Task<IResult> MyPayments(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var list = await db.Donations.AsNoTracking()
            .Where(d => d.DonorId == uid)
            .OrderByDescending(d => d.CreatedAt)
            .Take(100)
            .Select(d => new
            {
                d.Id, d.TransactionReference, d.Amount, d.Currency, d.Status,
                d.PayPalOrderId, d.PayPalCaptureId, d.CreatedAt, d.CapturedAt, d.CampaignId
            }).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> MySubscriptions(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var list = await db.RecurringDonations.AsNoTracking()
            .Where(s => s.DonorId == uid)
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .Select(s => new
            {
                s.Id, s.CampaignId, s.Amount, s.Currency, s.Interval, s.Status,
                s.PayPalSubscriptionId, s.NextChargeAt, s.SuccessfulCharges, s.CreatedAt
            }).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> MyRefunds(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var donationIds = await db.Donations.AsNoTracking().Where(d => d.DonorId == uid).Select(d => d.Id).ToListAsync();
        var refunds = await db.RefundRequests.AsNoTracking()
            .Where(r => donationIds.Contains(r.DonationId) || r.RequestedBy == uid)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync();
        return Results.Ok(refunds);
    }

    private static async Task<IResult> MySavedMethods(HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var profile = await EnsureProfile(db, uid, EmailClaim(http), null);
        var methods = await db.SavedPaymentMethods.AsNoTracking()
            .Where(m => m.CustomerProfileId == profile.Id && m.Status == "Active")
            .OrderByDescending(m => m.IsDefault).ThenByDescending(m => m.CreatedAt)
            .ToListAsync();
        return Results.Ok(methods);
    }

    private static async Task<IResult> SaveMethod(
        [FromBody] SaveMethodRequest req, HttpContext http, AppDbContext db, PayPalVaultClient vault)
    {
        var uid = Uid(http)!;
        var profile = await EnsureProfile(db, uid, EmailClaim(http), null);

        string? setupToken = req.SetupTokenId;
        if (string.IsNullOrEmpty(setupToken))
        {
            var created = await vault.CreateSetupTokenAsync(profile.PayPalCustomerId ?? profile.Id.ToString("N"));
            setupToken = created.SetupTokenId;
            if (string.IsNullOrEmpty(profile.PayPalCustomerId))
                profile.PayPalCustomerId = profile.Id.ToString("N");
        }

        if (req.IsDefault)
        {
            var existing = await db.SavedPaymentMethods.Where(m => m.CustomerProfileId == profile.Id && m.IsDefault).ToListAsync();
            foreach (var e in existing) e.IsDefault = false;
        }

        // Security: never persist raw card numbers, CVV, or track data — tokens only
        if (LooksLikeSensitivePaymentData(req.VaultId) || LooksLikeSensitivePaymentData(req.SetupTokenId) ||
            LooksLikeSensitivePaymentData(req.Brand) || (req.Last4 != null && new string(req.Last4.Where(char.IsDigit).ToArray()).Length > 4))
            return Results.BadRequest(new { error = "Raw card or sensitive payment credentials are not accepted. Store vault tokens only." });

        var method = new SavedPaymentMethod
        {
            CustomerProfileId = profile.Id,
            Type = string.IsNullOrWhiteSpace(req.Type) ? "paypal" : req.Type.Trim().ToLowerInvariant(),
            SetupTokenId = setupToken,
            PayPalVaultId = req.VaultId ?? setupToken,
            Brand = string.IsNullOrWhiteSpace(req.Brand) ? "PayPal" : req.Brand.Trim()[..Math.Min(40, req.Brand.Trim().Length)],
            Last4 = SanitizeLast4(req.Last4),
            IsDefault = req.IsDefault,
            Status = "Active"
        };
        db.SavedPaymentMethods.Add(method);
        profile.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(method);
    }

    private static async Task<IResult> RemoveMethod(Guid id, HttpContext http, AppDbContext db)
    {
        var uid = Uid(http)!;
        var profile = await db.CustomerProfiles.FirstOrDefaultAsync(c => c.UserId == uid);
        if (profile == null) return Results.NotFound();
        var m = await db.SavedPaymentMethods.FirstOrDefaultAsync(x => x.Id == id && x.CustomerProfileId == profile.Id);
        if (m == null) return Results.NotFound();
        m.Status = "Removed";
        m.RemovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(new { removed = true });
    }

    private static async Task<IResult> SearchCustomers(
        HttpContext http, AppDbContext db,
        [FromQuery] string? search = null,
        [FromQuery] string? email = null,
        [FromQuery] string? paypalId = null,
        [FromQuery] int take = 50)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var q = db.CustomerProfiles.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(c =>
                (c.Email != null && c.Email.Contains(s)) ||
                (c.DisplayName != null && c.DisplayName.Contains(s)) ||
                (c.UserId != null && c.UserId.Contains(s)) ||
                (c.PayPalPayerId != null && c.PayPalPayerId.Contains(s)) ||
                (c.PayPalCustomerId != null && c.PayPalCustomerId.Contains(s)));
        }
        if (!string.IsNullOrWhiteSpace(email))
            q = q.Where(c => c.Email != null && c.Email.Contains(email));
        if (!string.IsNullOrWhiteSpace(paypalId))
            q = q.Where(c => c.PayPalPayerId == paypalId || c.PayPalCustomerId == paypalId);
        var list = await q.OrderByDescending(c => c.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> GetCustomer(Guid id, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var c = await db.CustomerProfiles.AsNoTracking()
            .Include(x => x.SavedPaymentMethods)
            .FirstOrDefaultAsync(x => x.Id == id);
        return c == null ? Results.NotFound() : Results.Ok(c);
    }

    private static async Task<IResult> AdminPayments(Guid id, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var c = await db.CustomerProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return Results.NotFound();
        var q = db.Donations.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(c.UserId))
            q = q.Where(d => d.DonorId == c.UserId);
        else if (!string.IsNullOrEmpty(c.Email))
            q = q.Where(d => d.Donor != null && d.Donor.Email == c.Email);
        else
            return Results.Ok(Array.Empty<object>());
        var list = await q.OrderByDescending(d => d.CreatedAt).Take(100).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> AdminSubscriptions(Guid id, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var c = await db.CustomerProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c?.UserId == null) return Results.Ok(Array.Empty<object>());
        var list = await db.RecurringDonations.AsNoTracking()
            .Where(s => s.DonorId == c.UserId).OrderByDescending(s => s.CreatedAt).Take(50).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> AdminRefunds(Guid id, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var c = await db.CustomerProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c?.UserId == null) return Results.Ok(Array.Empty<object>());
        var donationIds = await db.Donations.AsNoTracking().Where(d => d.DonorId == c.UserId).Select(d => d.Id).ToListAsync();
        var refunds = await db.RefundRequests.AsNoTracking()
            .Where(r => donationIds.Contains(r.DonationId)).OrderByDescending(r => r.CreatedAt).Take(50).ToListAsync();
        return Results.Ok(refunds);
    }

    private static async Task<IResult> AdminTxnSearch(
        Guid id, HttpContext http, AppDbContext db, [FromQuery] string? q = null)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var c = await db.CustomerProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c?.UserId == null) return Results.Ok(Array.Empty<object>());
        var query = db.Donations.AsNoTracking().Where(d => d.DonorId == c.UserId);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var s = q.Trim();
            query = query.Where(d =>
                (d.TransactionReference != null && d.TransactionReference.Contains(s)) ||
                (d.PayPalOrderId != null && d.PayPalOrderId.Contains(s)) ||
                (d.PayPalCaptureId != null && d.PayPalCaptureId.Contains(s)));
        }
        var list = await query.OrderByDescending(d => d.CreatedAt).Take(100)
            .Select(d => new
            {
                d.Id, d.TransactionReference, d.Amount, d.Currency, d.Status,
                d.PayPalOrderId, d.PayPalCaptureId, d.CreatedAt
            }).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> AssociatePayPal(
        Guid id, [FromBody] AssociatePayPalRequest req, HttpContext http, AppDbContext db)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var c = await db.CustomerProfiles.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return Results.NotFound();
        if (req.PayPalPayerId != null) c.PayPalPayerId = req.PayPalPayerId;
        if (req.PayPalCustomerId != null) c.PayPalCustomerId = req.PayPalCustomerId;
        c.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(c);
    }

    private static async Task<IResult> EnsureFromUser(string userId, HttpContext http, AppDbContext db, UserManager<IdentityUser> users)
    {
        if (!http.User.IsInRole("Admin")) return Results.Forbid();
        var user = await users.FindByIdAsync(userId);
        if (user == null) return Results.NotFound();
        var profile = await EnsureProfile(db, user.Id, user.Email, user.UserName);
        await db.SaveChangesAsync();
        return Results.Ok(profile);
    }

    public record RegisterCustomerRequest(
        string Email,
        string Password,
        string? DisplayName = null,
        string? Role = "Donor",
        string? Phone = null,
        string? PayPalPayerId = null,
        string? PayPalCustomerId = null,
        string? BillingName = null,
        string? BillingLine1 = null,
        string? BillingCity = null,
        string? BillingRegion = null,
        string? BillingPostalCode = null,
        string? BillingCountry = null);

    public record UpdateCustomerRequest(
        string? DisplayName = null,
        string? Phone = null,
        string? PayPalPayerId = null,
        string? PayPalCustomerId = null);

    public record BillingRequest(
        string? BillingName = null,
        string? BillingLine1 = null,
        string? BillingLine2 = null,
        string? BillingCity = null,
        string? BillingRegion = null,
        string? BillingPostalCode = null,
        string? BillingCountry = null);

    public record SaveMethodRequest(
        string? Type = "paypal",
        string? SetupTokenId = null,
        string? VaultId = null,
        string? Brand = null,
        string? Last4 = null,
        bool IsDefault = false);

    public record AssociatePayPalRequest(string? PayPalPayerId = null, string? PayPalCustomerId = null);
}
