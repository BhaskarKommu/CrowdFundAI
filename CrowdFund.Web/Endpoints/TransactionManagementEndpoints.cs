using System.Globalization;
using System.Security.Claims;
using System.Text;
using CrowdFund.Core.Entities;
using CrowdFund.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrowdFund.Web.Endpoints;

/// <summary>
/// Transaction management: search, filters, detail, CSV/Excel export, reconciliation.
/// Transactions are donation payment records enriched with refunds and webhooks.
/// </summary>
public static class TransactionManagementEndpoints
{
    public static void MapTransactionManagementEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin/transactions").RequireAuthorization();
        g.MapGet("/", Search);
        g.MapGet("/{id:guid}", Detail);
        g.MapGet("/export.csv", ExportCsv);
        g.MapGet("/export.xlsx", ExportXlsx);
        g.MapGet("/reconcile", Reconcile);
        g.MapGet("/summary", Summary);
    }

    private static bool IsAdmin(HttpContext http) => http.User.IsInRole("Admin");

    private static IQueryable<Donation> BaseQuery(AppDbContext db) =>
        db.Donations.AsNoTracking().Include(d => d.Donor).Include(d => d.Campaign);

    private static async Task<List<TransactionRow>> BuildRows(
        AppDbContext db,
        IQueryable<Donation> q,
        int take = 200)
    {
        var donations = await q.OrderByDescending(d => d.CreatedAt).Take(take).ToListAsync();
        var ids = donations.Select(d => d.Id).ToList();
        var orderIds = donations.Where(d => d.PayPalOrderId != null).Select(d => d.PayPalOrderId!).ToList();
        var captureIds = donations.Where(d => d.PayPalCaptureId != null).Select(d => d.PayPalCaptureId!).ToList();

        var refunds = await db.RefundRequests.AsNoTracking()
            .Where(r => ids.Contains(r.DonationId))
            .ToListAsync();
        var refundByDonation = refunds.GroupBy(r => r.DonationId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var webhookQ = db.WebhookEvents.AsNoTracking()
            .Where(w => w.RelatedEntityType == "Donation" && w.RelatedEntityId != null && ids.Contains(w.RelatedEntityId.Value));
        // also match by resource id = capture/order
        var webhooks = await webhookQ.ToListAsync();
        var moreHooks = await db.WebhookEvents.AsNoTracking()
            .Where(w => (w.ResourceId != null && (orderIds.Contains(w.ResourceId) || captureIds.Contains(w.ResourceId))))
            .OrderByDescending(w => w.ReceivedAt)
            .Take(500)
            .ToListAsync();
        webhooks = webhooks.Concat(moreHooks).DistinctBy(w => w.Id).ToList();

        var rows = new List<TransactionRow>();
        foreach (var d in donations)
        {
            refundByDonation.TryGetValue(d.Id, out var rrList);
            rrList ??= new List<RefundRequest>();
            var refunded = rrList.Sum(r => r.RefundedAmount);
            var refundStatus = refunded <= 0 ? "None"
                : refunded >= d.Amount ? "Full"
                : "Partial";
            if (rrList.Any(r => r.Status == RefundStatus.Processing || r.Status == RefundStatus.Requested))
                refundStatus = "Pending";
            if (rrList.Any(r => r.Status == RefundStatus.Failed))
                refundStatus = "Failed";

            var relatedHooks = webhooks.Where(w =>
                w.RelatedEntityId == d.Id ||
                w.ResourceId == d.PayPalOrderId ||
                w.ResourceId == d.PayPalCaptureId).ToList();

            rows.Add(new TransactionRow(
                TransactionId: d.Id,
                TransactionReference: d.TransactionReference,
                PayPalOrderId: d.PayPalOrderId,
                PayPalCaptureId: d.PayPalCaptureId,
                PayPalAuthorizationId: d.PayPalAuthorizationId,
                CampaignId: d.CampaignId,
                CampaignTitle: d.Campaign?.Title,
                CustomerId: d.DonorId,
                CustomerEmail: d.Donor?.Email,
                CustomerName: d.Donor != null ? $"{d.Donor.UserName}" : null,
                CustomerEmailHash: d.PayerEmailHash,
                Amount: d.Amount,
                CapturedAmount: d.CapturedAmount,
                Currency: d.Currency,
                PaymentMethod: InferMethod(d),
                Intent: d.Intent,
                Status: d.Status.ToString(),
                RefundStatus: refundStatus,
                RefundedAmount: refunded,
                PaymentReference: d.TransactionReference ?? d.PayPalCaptureId ?? d.PayPalOrderId,
                CreatedAt: d.CreatedAt,
                CapturedAt: d.CapturedAt,
                WebhookEventCount: relatedHooks.Count,
                WebhookEventTypes: string.Join(", ", relatedHooks.Select(h => h.EventType).Distinct().Take(8))
            ));
        }
        return rows;
    }

    private static string InferMethod(Donation d)
    {
        if (d.IsRecurringCharge) return "Recurring / Subscription";
        if (d.Intent == "AUTHORIZE") return "PayPal Authorize";
        return "PayPal Checkout";
    }

    private static IQueryable<Donation> ApplyFilters(
        IQueryable<Donation> q,
        string? search,
        string? status,
        string? customerId,
        string? customerEmail,
        DateTime? from,
        DateTime? to,
        string? currency,
        string? paymentMethod)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(d =>
                (d.TransactionReference != null && d.TransactionReference.Contains(s)) ||
                (d.PayPalOrderId != null && d.PayPalOrderId.Contains(s)) ||
                (d.PayPalCaptureId != null && d.PayPalCaptureId.Contains(s)) ||
                d.Id.ToString().Contains(s) ||
                (d.Donor != null && d.Donor.Email != null && d.Donor.Email.Contains(s)));
        }
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<DonationStatus>(status, true, out var st))
            q = q.Where(d => d.Status == st);
        if (!string.IsNullOrWhiteSpace(customerId))
            q = q.Where(d => d.DonorId == customerId);
        if (!string.IsNullOrWhiteSpace(customerEmail))
            q = q.Where(d => d.Donor != null && d.Donor.Email != null && d.Donor.Email.Contains(customerEmail));
        if (from.HasValue)
            q = q.Where(d => d.CreatedAt >= from.Value.ToUniversalTime());
        if (to.HasValue)
            q = q.Where(d => d.CreatedAt <= to.Value.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(currency))
            q = q.Where(d => d.Currency == currency.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(paymentMethod))
        {
            if (paymentMethod.Contains("Recurring", StringComparison.OrdinalIgnoreCase))
                q = q.Where(d => d.IsRecurringCharge);
            else if (paymentMethod.Contains("Authorize", StringComparison.OrdinalIgnoreCase))
                q = q.Where(d => d.Intent == "AUTHORIZE");
            else
                q = q.Where(d => !d.IsRecurringCharge);
        }
        return q;
    }

    private static async Task<IResult> Search(
        HttpContext http,
        AppDbContext db,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? customerId = null,
        [FromQuery] string? customerEmail = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? currency = null,
        [FromQuery] string? paymentMethod = null,
        [FromQuery] int take = 100)
    {
        if (!IsAdmin(http)) return Results.Forbid();
        var q = ApplyFilters(BaseQuery(db), search, status, customerId, customerEmail, from, to, currency, paymentMethod);
        var rows = await BuildRows(db, q, Math.Clamp(take, 1, 500));
        return Results.Ok(new { count = rows.Count, transactions = rows });
    }

    private static async Task<IResult> Detail(Guid id, HttpContext http, AppDbContext db)
    {
        if (!IsAdmin(http)) return Results.Forbid();
        var d = await db.Donations.AsNoTracking()
            .Include(x => x.Donor).Include(x => x.Campaign)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return Results.NotFound();

        var refunds = await db.RefundRequests.AsNoTracking()
            .Where(r => r.DonationId == id).OrderByDescending(r => r.CreatedAt).ToListAsync();
        var refundTx = await db.RefundTransactions.AsNoTracking()
            .Where(t => t.DonationId == id).OrderByDescending(t => t.CreatedAt).ToListAsync();
        var webhooks = await db.WebhookEvents.AsNoTracking()
            .Where(w =>
                (w.RelatedEntityType == "Donation" && w.RelatedEntityId == id) ||
                (d.PayPalOrderId != null && w.ResourceId == d.PayPalOrderId) ||
                (d.PayPalCaptureId != null && w.ResourceId == d.PayPalCaptureId))
            .OrderByDescending(w => w.ReceivedAt)
            .Take(50)
            .Select(w => new
            {
                w.Id, w.PayPalEventId, w.EventType, w.ProcessingStatus, w.SignatureValid,
                w.ReceivedAt, w.ProcessedAt, w.LastError
            }).ToListAsync();

        var refunded = refunds.Sum(r => r.RefundedAmount);
        return Results.Ok(new
        {
            transactionId = d.Id,
            transactionReference = d.TransactionReference,
            payPalOrderId = d.PayPalOrderId,
            payPalCaptureId = d.PayPalCaptureId,
            payPalAuthorizationId = d.PayPalAuthorizationId,
            customer = new
            {
                id = d.DonorId,
                email = d.Donor?.Email,
                name = d.Donor?.UserName,
                emailHash = d.PayerEmailHash
            },
            paymentAmount = d.Amount,
            capturedAmount = d.CapturedAmount,
            currency = d.Currency,
            paymentMethod = InferMethod(d),
            intent = d.Intent,
            transactionDate = d.CreatedAt,
            capturedAt = d.CapturedAt,
            transactionStatus = d.Status.ToString(),
            statusDetail = d.StatusDetail,
            refundStatus = refunded <= 0 ? "None" : refunded >= d.Amount ? "Full" : "Partial",
            refundedAmount = refunded,
            paymentReference = d.TransactionReference ?? d.PayPalCaptureId ?? d.PayPalOrderId,
            campaign = new { d.CampaignId, title = d.Campaign?.Title },
            fees = new { d.TipAmount, d.PlatformFee, d.PaymentProcessorFeeEstimate, d.NetToCampaign },
            refunds,
            refundTransactions = refundTx,
            relatedWebhookEvents = webhooks
        });
    }

    private static async Task<IResult> ExportCsv(
        HttpContext http, AppDbContext db,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? customerId = null,
        [FromQuery] string? customerEmail = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? currency = null)
    {
        if (!IsAdmin(http)) return Results.Forbid();
        var q = ApplyFilters(BaseQuery(db), search, status, customerId, customerEmail, from, to, currency, null);
        var rows = await BuildRows(db, q, 5000);
        var sb = new StringBuilder();
        sb.AppendLine("TransactionId,TransactionReference,PayPalOrderId,PayPalCaptureId,CustomerId,CustomerEmail,Amount,Currency,PaymentMethod,Status,RefundStatus,RefundedAmount,CreatedAt,CapturedAt,WebhookEvents");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",",
                Csv(r.TransactionId.ToString()),
                Csv(r.TransactionReference),
                Csv(r.PayPalOrderId),
                Csv(r.PayPalCaptureId),
                Csv(r.CustomerId),
                Csv(r.CustomerEmail),
                r.Amount.ToString("F2", CultureInfo.InvariantCulture),
                Csv(r.Currency),
                Csv(r.PaymentMethod),
                Csv(r.Status),
                Csv(r.RefundStatus),
                r.RefundedAmount.ToString("F2", CultureInfo.InvariantCulture),
                Csv(r.CreatedAt.ToString("o")),
                Csv(r.CapturedAt?.ToString("o")),
                Csv(r.WebhookEventTypes)
            ));
        }
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", $"transactions-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
    }

    private static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    /// <summary>Minimal SpreadsheetML Excel XML (opens in Excel).</summary>
    private static async Task<IResult> ExportXlsx(
        HttpContext http, AppDbContext db,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? customerId = null,
        [FromQuery] string? customerEmail = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? currency = null)
    {
        if (!IsAdmin(http)) return Results.Forbid();
        var q = ApplyFilters(BaseQuery(db), search, status, customerId, customerEmail, from, to, currency, null);
        var rows = await BuildRows(db, q, 5000);

        var xml = new StringBuilder();
        xml.Append("""<?xml version="1.0"?><?mso-application progid="Excel.Sheet"?>""");
        xml.Append("""<Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">""");
        xml.Append("<Worksheet ss:Name=\"Transactions\"><Table>");
        string[] headers =
        {
            "TransactionId", "TransactionReference", "PayPalOrderId", "PayPalCaptureId",
            "CustomerEmail", "Amount", "Currency", "PaymentMethod", "Status", "RefundStatus",
            "RefundedAmount", "CreatedAt", "WebhookEvents"
        };
        xml.Append("<Row>");
        foreach (var h in headers)
            xml.Append($"<Cell><Data ss:Type=\"String\">{System.Security.SecurityElement.Escape(h)}</Data></Cell>");
        xml.Append("</Row>");
        foreach (var r in rows)
        {
            xml.Append("<Row>");
            void S(string? v) => xml.Append($"<Cell><Data ss:Type=\"String\">{System.Security.SecurityElement.Escape(v ?? "")}</Data></Cell>");
            void N(decimal v) => xml.Append($"<Cell><Data ss:Type=\"Number\">{v.ToString(CultureInfo.InvariantCulture)}</Data></Cell>");
            S(r.TransactionId.ToString());
            S(r.TransactionReference);
            S(r.PayPalOrderId);
            S(r.PayPalCaptureId);
            S(r.CustomerEmail);
            N(r.Amount);
            S(r.Currency);
            S(r.PaymentMethod);
            S(r.Status);
            S(r.RefundStatus);
            N(r.RefundedAmount);
            S(r.CreatedAt.ToString("o"));
            S(r.WebhookEventTypes);
            xml.Append("</Row>");
        }
        xml.Append("</Table></Worksheet></Workbook>");
        var bytes = Encoding.UTF8.GetBytes(xml.ToString());
        // .xls SpreadsheetML — Excel opens it; filename xls for compatibility
        return Results.File(bytes, "application/vnd.ms-excel", $"transactions-{DateTime.UtcNow:yyyyMMddHHmmss}.xls");
    }

    private static async Task<IResult> Reconcile(
        HttpContext http, AppDbContext db, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        if (!IsAdmin(http)) return Results.Forbid();
        var fromDt = from ?? DateTime.UtcNow.AddDays(-30);
        var toDt = to ?? DateTime.UtcNow;

        var donations = await db.Donations.AsNoTracking()
            .Where(d => d.CreatedAt >= fromDt && d.CreatedAt <= toDt)
            .ToListAsync();

        var captured = donations.Where(d => d.Status == DonationStatus.Captured || d.Status == DonationStatus.PartiallyCaptured).ToList();
        var refundedAmt = await db.RefundRequests.AsNoTracking()
            .Where(r => r.CreatedAt >= fromDt && r.CreatedAt <= toDt)
            .SumAsync(r => r.RefundedAmount);

        var missingCaptureId = captured.Where(d => string.IsNullOrEmpty(d.PayPalCaptureId)).Select(d => d.Id).ToList();
        var capturedNoLedger = new List<Guid>();
        foreach (var d in captured.Take(200))
        {
            var has = await db.AuditLedger.AsNoTracking()
                .AnyAsync(e => e.CampaignId == d.CampaignId &&
                               (e.EventType.Contains("Capture") || e.EventType.Contains("donation.captured") || e.EventType == "PaymentCapture"));
            // weaker check — skip heavy; mark if none for campaign is too broad
        }

        var webhookOrphans = await db.WebhookEvents.AsNoTracking()
            .Where(w => w.ReceivedAt >= fromDt && w.ReceivedAt <= toDt &&
                        w.EventType.Contains("CAPTURE") &&
                        w.RelatedEntityId == null)
            .Take(50)
            .Select(w => new { w.Id, w.EventType, w.ResourceId, w.ProcessingStatus })
            .ToListAsync();

        var statusBreakdown = donations.GroupBy(d => d.Status.ToString())
            .Select(g => new { status = g.Key, count = g.Count(), amount = g.Sum(x => x.Amount) })
            .ToList();

        var grossCaptured = captured.Sum(d => d.CapturedAmount > 0 ? d.CapturedAmount : d.Amount);
        var net = grossCaptured - refundedAmt;

        return Results.Ok(new
        {
            period = new { from = fromDt, to = toDt },
            totals = new
            {
                transactionCount = donations.Count,
                capturedCount = captured.Count,
                grossCaptured,
                refundedAmount = refundedAmt,
                net,
                failedCount = donations.Count(d => d.Status == DonationStatus.Failed),
                pendingCount = donations.Count(d => d.Status is DonationStatus.Pending or DonationStatus.PendingApproval)
            },
            statusBreakdown,
            exceptions = new
            {
                capturedMissingPayPalCaptureId = missingCaptureId,
                webhookEventsWithoutLinkedDonation = webhookOrphans
            },
            reconciledAt = DateTime.UtcNow
        });
    }

    private static async Task<IResult> Summary(HttpContext http, AppDbContext db)
    {
        if (!IsAdmin(http)) return Results.Forbid();
        var today = DateTime.UtcNow.Date;
        var q = db.Donations.AsNoTracking();
        return Results.Ok(new
        {
            total = await q.CountAsync(),
            captured = await q.CountAsync(d => d.Status == DonationStatus.Captured),
            todayCount = await q.CountAsync(d => d.CreatedAt >= today),
            todayVolume = await q.Where(d => d.CreatedAt >= today && d.Status == DonationStatus.Captured).SumAsync(d => d.Amount)
        });
    }

    public record TransactionRow(
        Guid TransactionId,
        string? TransactionReference,
        string? PayPalOrderId,
        string? PayPalCaptureId,
        string? PayPalAuthorizationId,
        Guid CampaignId,
        string? CampaignTitle,
        string? CustomerId,
        string? CustomerEmail,
        string? CustomerName,
        string? CustomerEmailHash,
        decimal Amount,
        decimal CapturedAmount,
        string Currency,
        string PaymentMethod,
        string? Intent,
        string Status,
        string RefundStatus,
        decimal RefundedAmount,
        string? PaymentReference,
        DateTime CreatedAt,
        DateTime? CapturedAt,
        int WebhookEventCount,
        string WebhookEventTypes);
}
