using System.Diagnostics.Metrics;
using GroupBuy.Application;

namespace GroupBuy.IntegrationTests;

public sealed class OperationalMetricsTests
{
    [Fact]
    public void Operational_metrics_publish_bounded_low_cardinality_tags()
    {
        var observed = new List<(string Name, object? Value, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Groudeal.Operations") meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            observed.Add((instrument.Name, value, string.Join(";", tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}")))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            observed.Add((instrument.Name, value, string.Join(";", tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}")))));
        listener.Start();

        using var metrics = new OperationalMetrics();
        metrics.RecordRequest("/api/v1/me/reservations/{id}", "GET", 500, 12.5);
        metrics.RecordAuthenticationFailure("SecurityTokenException");
        metrics.RecordWorkerBatchFailure("NpgsqlException");
        metrics.RecordCampaignRetry("DbUpdateException");
        metrics.RecordCampaignRetryExhaustion();

        Assert.Contains(observed, item => item.Name == "groudeal.api.requests" && item.Tags.Contains("route=/api/v1/me/reservations/{id}", StringComparison.Ordinal));
        Assert.Contains(observed, item => item.Name == "groudeal.api.request.duration" && Equals(item.Value, 12.5));
        Assert.Contains(observed, item => item.Name == "groudeal.security.authentication.failures" && item.Tags.Contains("error_type=SecurityTokenException", StringComparison.Ordinal));
        Assert.Contains(observed, item => item.Name == "groudeal.worker.batch.failures");
        Assert.Contains(observed, item => item.Name == "groudeal.worker.campaign.retries");
        Assert.Contains(observed, item => item.Name == "groudeal.worker.campaign.retry.exhaustions");
        Assert.DoesNotContain(observed, item => item.Tags.Contains("/12345678-", StringComparison.Ordinal));
    }
}
