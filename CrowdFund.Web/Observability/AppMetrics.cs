using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CrowdFund.Web.Observability;

/// <summary>In-process counters for capture failures, ledger issues, fraud spikes.</summary>
public class AppMetrics
{
    public const string MeterName = "CrowdFundAI";

    private readonly Meter _meter;
    private readonly Counter<long> _capturesOk;
    private readonly Counter<long> _capturesFailed;
    private readonly Counter<long> _ledgerBreaks;
    private readonly Counter<long> _fraudFlags;
    private readonly Counter<long> _payoutsFailed;
    private readonly Counter<long> _alertsSent;

    private long _capturesOkLocal;
    private long _capturesFailedLocal;
    private long _ledgerBreaksLocal;
    private long _fraudFlagsLocal;
    private long _payoutsFailedLocal;

    private readonly ConcurrentQueue<DateTime> _recentFraud = new();
    private readonly ConcurrentQueue<DateTime> _recentCaptureFails = new();

    public AppMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _capturesOk = _meter.CreateCounter<long>("crowdfund.captures.ok");
        _capturesFailed = _meter.CreateCounter<long>("crowdfund.captures.failed");
        _ledgerBreaks = _meter.CreateCounter<long>("crowdfund.ledger.breaks");
        _fraudFlags = _meter.CreateCounter<long>("crowdfund.fraud.flags");
        _payoutsFailed = _meter.CreateCounter<long>("crowdfund.payouts.failed");
        _alertsSent = _meter.CreateCounter<long>("crowdfund.alerts.sent");
    }

    public void CaptureOk() { Interlocked.Increment(ref _capturesOkLocal); _capturesOk.Add(1); }
    public void CaptureFailed()
    {
        Interlocked.Increment(ref _capturesFailedLocal);
        _capturesFailed.Add(1);
        _recentCaptureFails.Enqueue(DateTime.UtcNow);
        Trim(_recentCaptureFails, TimeSpan.FromMinutes(30));
    }

    public void LedgerBreak() { Interlocked.Increment(ref _ledgerBreaksLocal); _ledgerBreaks.Add(1); }
    public void FraudFlag()
    {
        Interlocked.Increment(ref _fraudFlagsLocal);
        _fraudFlags.Add(1);
        _recentFraud.Enqueue(DateTime.UtcNow);
        Trim(_recentFraud, TimeSpan.FromMinutes(60));
    }

    public void PayoutFailed() { Interlocked.Increment(ref _payoutsFailedLocal); _payoutsFailed.Add(1); }
    public void AlertSent() => _alertsSent.Add(1);

    public int RecentFraudCount(TimeSpan window)
    {
        Trim(_recentFraud, window);
        return _recentFraud.Count;
    }

    public int RecentCaptureFailCount(TimeSpan window)
    {
        Trim(_recentCaptureFails, window);
        return _recentCaptureFails.Count;
    }

    public object Snapshot() => new
    {
        capturesOk = Interlocked.Read(ref _capturesOkLocal),
        capturesFailed = Interlocked.Read(ref _capturesFailedLocal),
        ledgerBreaks = Interlocked.Read(ref _ledgerBreaksLocal),
        fraudFlags = Interlocked.Read(ref _fraudFlagsLocal),
        payoutsFailed = Interlocked.Read(ref _payoutsFailedLocal),
        recentFraud30m = RecentFraudCount(TimeSpan.FromMinutes(30)),
        recentCaptureFails30m = RecentCaptureFailCount(TimeSpan.FromMinutes(30))
    };

    private static void Trim(ConcurrentQueue<DateTime> q, TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        while (q.TryPeek(out var t) && t < cutoff)
            q.TryDequeue(out _);
    }
}
