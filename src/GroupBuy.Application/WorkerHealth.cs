namespace GroupBuy.Application;

using System.Diagnostics;
using System.Diagnostics.Metrics;

public record WorkerSnapshot(DateTimeOffset? LastStartedAtUtc, DateTimeOffset? LastCompletedAtUtc, bool Running, string? LastBatchErrorType, DateTimeOffset? LastProgressAtUtc = null);

// Process-local liveness complements persisted per-campaign retries. Empty after restart is intentional.
public sealed class WorkerHealthState
{
    private readonly object gate = new();
    private WorkerSnapshot snapshot = new(null, null, false, null);
    public void Started(DateTimeOffset now) { lock (gate) snapshot = snapshot with { LastStartedAtUtc = now, LastProgressAtUtc = now, Running = true }; }
    public void Completed(DateTimeOffset now) { lock (gate) snapshot = snapshot with { LastCompletedAtUtc = now, Running = false, LastBatchErrorType = null }; }
    public void Failed(string type) { lock (gate) snapshot = snapshot with { Running = false, LastBatchErrorType = type }; }
    public void Progress(DateTimeOffset now) { lock (gate) snapshot = snapshot with { LastProgressAtUtc = now }; }
    public void Stopped() { lock (gate) snapshot = snapshot with { Running = false }; }
    public WorkerSnapshot Read() { lock (gate) return snapshot; }
}

public sealed class OperationalMetrics : IDisposable
{
    private readonly Meter meter = new("Groudeal.Operations", "1.0.0");
    private readonly Counter<long> requests;
    private readonly Histogram<double> requestDuration;
    private readonly Counter<long> authenticationFailures;
    private readonly Counter<long> workerBatchFailures;
    private readonly Counter<long> campaignRetries;
    private readonly Counter<long> campaignRetryExhaustions;

    public OperationalMetrics()
    {
        requests = meter.CreateCounter<long>("groudeal.api.requests");
        requestDuration = meter.CreateHistogram<double>("groudeal.api.request.duration", "ms");
        authenticationFailures = meter.CreateCounter<long>("groudeal.security.authentication.failures");
        workerBatchFailures = meter.CreateCounter<long>("groudeal.worker.batch.failures");
        campaignRetries = meter.CreateCounter<long>("groudeal.worker.campaign.retries");
        campaignRetryExhaustions = meter.CreateCounter<long>("groudeal.worker.campaign.retry.exhaustions");
    }

    public void RecordRequest(string route, string method, int statusCode, double elapsedMilliseconds)
    {
        var tags = new TagList { { "route", route }, { "method", method }, { "status_code", statusCode } };
        requests.Add(1, tags);
        requestDuration.Record(elapsedMilliseconds, tags);
    }

    public void RecordAuthenticationFailure(string errorType) =>
        authenticationFailures.Add(1, new KeyValuePair<string, object?>("error_type", errorType));

    public void RecordWorkerBatchFailure(string errorType) =>
        workerBatchFailures.Add(1, new KeyValuePair<string, object?>("error_type", errorType));

    public void RecordCampaignRetry(string errorType) =>
        campaignRetries.Add(1, new KeyValuePair<string, object?>("error_type", errorType));

    public void RecordCampaignRetryExhaustion() => campaignRetryExhaustions.Add(1);

    public void Dispose() => meter.Dispose();
}
