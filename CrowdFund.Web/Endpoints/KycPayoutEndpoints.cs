using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Security.Cryptography;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Interfaces;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.PayPal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

public static class KycPayoutEndpoints
{
    public static void MapKycPayoutEndpoints(this WebApplication app)
    {
        app.MapGet("/api/kyc/profile", GetMyProfile).RequireRateLimiting("strict");
        app.MapPost("/api/kyc/profile", UpsertProfile).RequireRateLimiting("strict");
        app.MapPost("/api/kyc/submissions", CreateSubmission).DisableAntiforgery().RequireRateLimiting("strict");
        app.MapGet("/api/kyc/submissions", ListMySubmissions).RequireRateLimiting("strict");
        app.MapGet("/api/admin/kyc/queue", AdminKycQueue);
        app.MapPost("/api/admin/kyc/submissions/{id:guid}/decision", AdminKycDecision);

        app.MapPost("/api/aml/screen", ScreenAml);
        app.MapGet("/api/admin/aml", AdminAmlList);

        app.MapPost("/api/campaigns/{campaignId:guid}/payouts", RequestPayout).RequireRateLimiting("strict");
        app.MapGet("/api/payouts/mine", MyPayouts);
        app.MapGet("/api/admin/payouts", AdminPayouts);
        app.MapPost("/api/admin/payouts/{id:guid}/decision", AdminPayoutDecision);

        app.MapGet("/api/donations/{id:guid}/tax-receipt", GetTaxReceipt);
        app.MapPost("/api/donations/{id:guid}/tax-receipt", IssueTaxReceipt);
    }

    private static string? UserId(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static async Task<IResult> GetMyProfile(HttpContext http, AppDbContext db)
    {
        var uid = UserId(http);
        if (uid == null) return Results.Unauthorized();
        var p = await db.CreatorProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid);
        if (p == null) return Results.Ok(new { exists = false, tier = KycTier.Unverified.ToString(), limits = LimitsDto(KycTier.Unverified) });
        return Results.Ok(new
        {
            exists = true,
            profile = p,
            limits = LimitsDto(p.KycTier),
            canPayout = PlatformLimits.CanRequestPayout(p.KycTier)
        });
    }

    private static async Task<IResult> UpsertProfile([FromBody] ProfileRequest req, HttpContext http, AppDbContext db, AmlScreeningService aml)
    {
        var uid = UserId(http);
        if (uid == null) return Results.Unauthorized();
        var p = await db.CreatorProfiles.FirstOrDefaultAsync(x => x.UserId == uid);
        if (p == null)
        {
            p = new CreatorProfile { UserId = uid };
            db.CreatorProfiles.Add(p);
        }
        p.LegalName = req.LegalName?.Trim() ?? p.LegalName;
        p.OrganizationName = req.OrganizationName;
        p.Country = string.IsNullOrWhiteSpace(req.Country) ? p.Country : req.Country!;
        p.State = req.State;
        p.Address = req.Address;
        p.IsNonprofit = req.IsNonprofit;
        p.TaxId = req.TaxId;
        p.TaxExemptionNumber = req.TaxExemptionNumber;
        p.TaxExemptionType = req.TaxExemptionType;
        p.TaxExemptionValidUntil = req.TaxExemptionValidUntil;
        p.PayPalPayoutEmail = req.PayPalPayoutEmail;
        p.BankName = req.BankName;
        p.BankIfscOrRouting = req.BankIfscOrRouting;
        if (!string.IsNullOrWhiteSpace(req.BankAccountNumber) && req.BankAccountNumber.Length >= 4)
            p.BankAccountLast4 = req.BankAccountNumber[^4..];
        p.UpdatedAt = DateTime.UtcNow;

        // AML screen on profile save
        var screen = aml.Screen(p.OrganizationName ?? p.LegalName, p.Country, "creator", uid);
        db.AmlScreenings.Add(screen);
        p.LastAmlStatus = screen.Status;
        p.LastAmlCheckedAt = DateTime.UtcNow;
        p.LastAmlNotes = screen.MatchedTerms;
        await db.SaveChangesAsync();
        return Results.Ok(new { profile = p, aml = screen.Status.ToString(), limits = LimitsDto(p.KycTier) });
    }

    private static async Task<IResult> CreateSubmission(HttpRequest request, HttpContext http, AppDbContext db, IFileStore files)
    {
        var uid = UserId(http);
        if (uid == null) return Results.Unauthorized();
        var profile = await db.CreatorProfiles.FirstOrDefaultAsync(x => x.UserId == uid);
        if (profile == null) return Results.BadRequest(new { error = "Save profile first" });

        if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart required" });
        var form = await request.ReadFormAsync();
        var tierStr = form["requestedTier"].FirstOrDefault() ?? "Basic";
        Enum.TryParse<KycTier>(tierStr, true, out var tier);
        if (tier is not (KycTier.Basic or KycTier.Enhanced)) tier = KycTier.Basic;

        var sub = new KycSubmission
        {
            CreatorProfileId = profile.Id,
            Status = KycSubmissionStatus.Submitted,
            RequestedTier = tier,
            NotesFromCreator = form["notes"].FirstOrDefault()
        };
        db.KycSubmissions.Add(sub);
        profile.KycTier = KycTier.Pending;
        profile.UpdatedAt = DateTime.UtcNow;

        foreach (var file in form.Files)
        {
            await using var stream = file.OpenReadStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            ms.Position = 0;
            var stored = await files.SaveAsync(ms, file.FileName, file.ContentType);
            var docType = GuessDocType(file.FileName, form["docType"].FirstOrDefault());
            db.KycDocuments.Add(new KycDocument
            {
                KycSubmissionId = sub.Id,
                DocumentType = docType,
                FileName = file.FileName,
                ContentType = file.ContentType,
                Sha256 = sha,
                StoredPath = stored,
                SizeBytes = bytes.Length
            });
        }

        await db.SaveChangesAsync();
        return Results.Ok(new { submissionId = sub.Id, status = sub.Status.ToString(), tier = profile.KycTier.ToString() });
    }

    private static KycDocumentType GuessDocType(string name, string? hint)
    {
        if (Enum.TryParse<KycDocumentType>(hint, true, out var t)) return t;
        var n = name.ToLowerInvariant();
        if (n.Contains("passport") || n.Contains("aadhaar") || n.Contains("license") || n.Contains("id")) return KycDocumentType.GovernmentId;
        if (n.Contains("80g")) return KycDocumentType.TaxExemption80G;
        if (n.Contains("501")) return KycDocumentType.TaxExemption501c3;
        if (n.Contains("selfie")) return KycDocumentType.Selfie;
        if (n.Contains("bank")) return KycDocumentType.BankStatement;
        if (n.Contains("registr") || n.Contains("incorp")) return KycDocumentType.BusinessRegistration;
        return KycDocumentType.Other;
    }

    private static async Task<IResult> ListMySubmissions(HttpContext http, AppDbContext db)
    {
        var uid = UserId(http);
        if (uid == null) return Results.Unauthorized();
        var profile = await db.CreatorProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid);
        if (profile == null) return Results.Ok(Array.Empty<object>());
        var list = await db.KycSubmissions.AsNoTracking()
            .Include(s => s.Documents)
            .Where(s => s.CreatorProfileId == profile.Id)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> AdminKycQueue(AppDbContext db)
    {
        var list = await db.KycSubmissions.AsNoTracking()
            .Include(s => s.Documents)
            .Include(s => s.CreatorProfile)
            .Where(s => s.Status == KycSubmissionStatus.Submitted || s.Status == KycSubmissionStatus.UnderReview)
            .OrderBy(s => s.CreatedAt)
            .Take(100)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> AdminKycDecision(
        Guid id, [FromBody] KycDecisionRequest req, HttpContext http, AppDbContext db, KycService kyc, LedgerService ledger, SecurityAuditService audit)
    {
        var uid = UserId(http) ?? "admin";
        var sub = await db.KycSubmissions.Include(s => s.CreatorProfile).FirstOrDefaultAsync(s => s.Id == id);
        if (sub?.CreatorProfile == null) return Results.NotFound();
        var profile = sub.CreatorProfile;

        if (string.Equals(req.Decision, "approve", StringComparison.OrdinalIgnoreCase))
        {
            var granted = req.GrantedTier ?? sub.RequestedTier;
            if (granted < KycTier.Basic) granted = KycTier.Basic;
            kyc.ApplyApproval(profile, sub, granted, uid);
            await AddLedgerAsync(db, ledger, Guid.Empty, "kyc.approved", new { sub.Id, profile.UserId, tier = granted.ToString() });
        }
        else if (string.Equals(req.Decision, "reject", StringComparison.OrdinalIgnoreCase))
        {
            kyc.ApplyRejection(profile, sub, uid, req.Note ?? "Rejected");
            await AddLedgerAsync(db, ledger, Guid.Empty, "kyc.rejected", new { sub.Id, profile.UserId });
        }
        else
        {
            sub.Status = KycSubmissionStatus.NeedsMoreInfo;
            sub.NotesFromReviewer = req.Note;
            sub.ReviewedAt = DateTime.UtcNow;
            sub.ReviewedBy = uid;
        }
        await db.SaveChangesAsync();
        await AuditEndpoints.WriteAsync(db, audit, http, "kyc.decision", "KycSubmission", id.ToString(),
            new { req.Decision, tier = profile.KycTier.ToString() });
        return Results.Ok(new { sub.Status, profile.KycTier });
    }

    private static async Task<IResult> ScreenAml([FromBody] AmlRequest req, AppDbContext db, AmlScreeningService aml)
    {
        var screen = aml.Screen(req.Name, req.Country, req.Context ?? "manual", req.UserId, req.CampaignId, req.DonationId);
        db.AmlScreenings.Add(screen);
        await db.SaveChangesAsync();
        return Results.Ok(screen);
    }

    private static async Task<IResult> AdminAmlList(AppDbContext db)
    {
        var list = await db.AmlScreenings.AsNoTracking()
            .OrderByDescending(a => a.CreatedAt)
            .Take(200)
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> RequestPayout(
        Guid campaignId, [FromBody] PayoutRequest req, HttpContext http, AppDbContext db, KycService kyc, AmlScreeningService aml, LedgerService ledger, PolicyEngine policy)
    {
        var uid = UserId(http);
        if (uid == null) return Results.Unauthorized();
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign == null) return Results.NotFound();
        if (campaign.CreatorId != uid) return Results.Forbid();

        var profile = await db.CreatorProfiles.FirstOrDefaultAsync(p => p.UserId == uid);
        if (profile == null) return Results.BadRequest(new { error = "Complete KYC profile first" });
        if (!kyc.CanRequestPayout(profile, req.Amount))
            return Results.BadRequest(new { error = "KYC tier or amount not eligible for payout", tier = profile.KycTier.ToString(), max = PlatformLimits.MaxSinglePayout(profile.KycTier) });

        // Configurable policy engine
        var expensesForPolicy = await db.Expenses.AsNoTracking().Where(e => e.CampaignId == campaignId).ToListAsync();
        var flagsForPolicy = await db.FraudFlags.AsNoTracking().Where(f => f.CampaignId == campaignId).ToListAsync();
        var hasApprovedProof = await db.MultimodalProofs.AnyAsync(p => p.CampaignId == campaignId && p.Status == MultimodalProofStatus.Approved);
        var policyDecisions = policy.EvaluatePayout(campaign, profile, expensesForPolicy, flagsForPolicy, null, hasApprovedProof, false);
        if (policyDecisions.Any(d => !d.Allowed))
            return Results.BadRequest(new { error = "Blocked by policy", decisions = policyDecisions });

        // Available = raised - already completed/processing payouts (simplified; production should net expenses policy)
        var paidOut = await db.Payouts.Where(p => p.CampaignId == campaignId &&
            (p.Status == PayoutStatus.Completed || p.Status == PayoutStatus.Processing || p.Status == PayoutStatus.Approved))
            .SumAsync(p => (decimal?)p.GrossAmount) ?? 0m;
        var available = campaign.RaisedAmount - paidOut;
        if (req.Amount > available)
            return Results.BadRequest(new { error = "Amount exceeds available balance", available });

        var (fee, net) = kyc.CalculatePayoutFee(profile, req.Amount);
        var method = string.IsNullOrWhiteSpace(req.Method) ? "PayPal" : req.Method!;
        var dest = method == "PayPal" ? profile.PayPalPayoutEmail : $"BANK **{profile.BankAccountLast4}";
        if (method == "PayPal" && string.IsNullOrWhiteSpace(profile.PayPalPayoutEmail))
            return Results.BadRequest(new { error = "Set PayPal payout email on profile" });

        var screen = aml.Screen(profile.OrganizationName ?? profile.LegalName, profile.Country, "payout", uid, campaignId);
        db.AmlScreenings.Add(screen);
        if (screen.Status == AmlScreenStatus.Match)
            return Results.BadRequest(new { error = "AML match — payout blocked pending compliance review", aml = screen.Status.ToString() });

        var payout = new Payout
        {
            CampaignId = campaignId,
            CreatorUserId = uid,
            GrossAmount = req.Amount,
            PlatformFee = fee,
            NetAmount = net,
            Currency = campaign.Currency,
            Status = screen.Status == AmlScreenStatus.Review ? PayoutStatus.UnderReview : PayoutStatus.Requested,
            Method = method,
            DestinationSummary = dest,
            AmlStatus = screen.Status
        };
        db.Payouts.Add(payout);
        await AddLedgerAsync(db, ledger, campaignId, "payout.requested", new { payout.Id, payout.GrossAmount, payout.NetAmount, payout.PlatformFee });
        await db.SaveChangesAsync();
        return Results.Ok(payout);
    }

    private static async Task<IResult> MyPayouts(HttpContext http, AppDbContext db)
    {
        var uid = UserId(http);
        if (uid == null) return Results.Unauthorized();
        var list = await db.Payouts.AsNoTracking().Where(p => p.CreatorUserId == uid).OrderByDescending(p => p.CreatedAt).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> AdminPayouts(AppDbContext db) =>
        Results.Ok(await db.Payouts.AsNoTracking().OrderByDescending(p => p.CreatedAt).Take(200).ToListAsync());

    private static async Task<IResult> AdminPayoutDecision(
        Guid id, [FromBody] PayoutDecisionRequest req, HttpContext http, AppDbContext db,
        PayPalPayoutClient payouts, LedgerService ledger, SecurityAuditService audit)
    {
        var uid = UserId(http) ?? "admin";
        var p = await db.Payouts.FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return Results.NotFound();
        if (string.Equals(req.Decision, "approve", StringComparison.OrdinalIgnoreCase))
        {
            p.Status = PayoutStatus.Processing;
            p.ReviewedBy = uid;
            p.ReviewedAt = DateTime.UtcNow;
            p.AdminNote = req.Note;
            await db.SaveChangesAsync();

            var (ok, batchId, msg) = await payouts.SendPayoutAsync(
                p.DestinationSummary ?? "",
                p.NetAmount,
                p.Currency,
                p.Id.ToString("N"),
                $"CrowdFund AI payout for campaign {p.CampaignId}");

            if (ok)
            {
                p.Status = PayoutStatus.Completed;
                p.ExternalPayoutId = batchId;
                p.CompletedAt = DateTime.UtcNow;
                await AddLedgerAsync(db, ledger, p.CampaignId, "payout.completed", new { p.Id, batchId, p.NetAmount });
            }
            else
            {
                p.Status = PayoutStatus.Failed;
                p.FailureReason = msg;
                await AddLedgerAsync(db, ledger, p.CampaignId, "payout.failed", new { p.Id, msg });
            }
        }
        else if (string.Equals(req.Decision, "reject", StringComparison.OrdinalIgnoreCase))
        {
            p.Status = PayoutStatus.Cancelled;
            p.AdminNote = req.Note;
            p.ReviewedBy = uid;
            p.ReviewedAt = DateTime.UtcNow;
        }
        else
        {
            p.Status = PayoutStatus.Blocked;
            p.AdminNote = req.Note;
        }
        await db.SaveChangesAsync();
        await AuditEndpoints.WriteAsync(db, audit, http, "payout.decision", "Payout", id.ToString(),
            new { req.Decision, p.Status, p.NetAmount });
        return Results.Ok(p);
    }

    private static async Task<IResult> GetTaxReceipt(Guid id, AppDbContext db, TaxReceiptService taxSvc, LedgerService ledger)
    {
        var donation = await db.Donations.AsNoTracking().Include(d => d.Campaign).FirstOrDefaultAsync(d => d.Id == id);
        if (donation?.Campaign == null || donation.Status != DonationStatus.Captured) return Results.NotFound();
        var tax = await db.DonationTaxInfos.AsNoTracking().FirstOrDefaultAsync(t => t.DonationId == id);
        if (tax == null)
        {
            var creator = await db.CreatorProfiles.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == donation.Campaign.CreatorId);
            tax = taxSvc.BuildTaxInfo(donation, donation.Campaign, creator, null, null, null);
        }
        var entries = await db.AuditLedger.Where(e => e.CampaignId == donation.CampaignId).OrderBy(e => e.Id).ToListAsync();
        var head = entries.LastOrDefault()?.Hash;
        var html = taxSvc.RenderHtml(donation, donation.Campaign, tax, head);
        return Results.Content(html, "text/html");
    }

    private static async Task<IResult> IssueTaxReceipt(
        Guid id, [FromBody] TaxReceiptRequest req, HttpContext http, AppDbContext db, TaxReceiptService taxSvc)
    {
        var donation = await db.Donations.Include(d => d.Campaign).FirstOrDefaultAsync(d => d.Id == id);
        if (donation?.Campaign == null || donation.Status != DonationStatus.Captured) return Results.NotFound();
        var creator = await db.CreatorProfiles.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == donation.Campaign.CreatorId);
        var tax = taxSvc.BuildTaxInfo(donation, donation.Campaign, creator, req.DonorLegalName, req.DonorAddress, req.DonorTaxId);
        var existing = await db.DonationTaxInfos.FirstOrDefaultAsync(t => t.DonationId == id);
        if (existing != null) db.DonationTaxInfos.Remove(existing);
        db.DonationTaxInfos.Add(tax);
        await db.SaveChangesAsync();
        return Results.Ok(tax);
    }

    private static object LimitsDto(KycTier tier) => new
    {
        tier = tier.ToString(),
        maxGoal = PlatformLimits.MaxGoalAmount(tier),
        maxPayout = PlatformLimits.MaxSinglePayout(tier),
        canPayout = PlatformLimits.CanRequestPayout(tier),
        payoutFeeRate = PlatformLimits.PayoutFeeRate(tier)
    };


    private static async Task AddLedgerAsync(AppDbContext db, LedgerService ledger, Guid campaignId, string eventType, object payload)
    {
        var prev = await db.AuditLedger.Where(e => e.CampaignId == campaignId)
            .OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync() ?? "";
        var entry = ledger.CreateEntry(campaignId, eventType, payload, prev);
        db.AuditLedger.Add(entry);
    }

    public record ProfileRequest(
        string? LegalName, string? OrganizationName, string? Country, string? State, string? Address,
        bool IsNonprofit, string? TaxId, string? TaxExemptionNumber, string? TaxExemptionType, DateTime? TaxExemptionValidUntil,
        string? PayPalPayoutEmail, string? BankName, string? BankIfscOrRouting, string? BankAccountNumber);

    public record KycDecisionRequest(string Decision, string? Note = null, KycTier? GrantedTier = null);
    public record AmlRequest(string Name, string? Country = null, string? Context = null, string? UserId = null, Guid? CampaignId = null, Guid? DonationId = null);
    public record PayoutRequest(decimal Amount, string? Method = "PayPal");
    public record PayoutDecisionRequest(string Decision, string? Note = null);
    public record TaxReceiptRequest(string? DonorLegalName = null, string? DonorAddress = null, string? DonorTaxId = null);
}
