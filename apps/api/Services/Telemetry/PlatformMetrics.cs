using System.Diagnostics.Metrics;

namespace MigrationPlatform.Api.Services.Telemetry;

/// <summary>
/// Custom application metrics, published on the <see cref="MeterName"/> meter
/// (exported only when OpenTelemetry is configured — see
/// <see cref="TelemetryRegistration"/>). Registered as a singleton so any code
/// can record without touching the OTel pipeline directly.
///
/// The observable gauges read from a <see cref="ActiveWorkSnapshot"/> that is
/// refreshed out-of-band by <see cref="ActiveWorkMetrics"/>; the observe
/// callbacks are cheap, non-blocking reads of that cached snapshot so metric
/// collection never issues a database query on the collector's thread.
/// </summary>
public sealed class PlatformMetrics
{
    public const string MeterName = "MigrationPlatform";

    private readonly Counter<long> _workerPollFailures;
    private volatile ActiveWorkSnapshot _snapshot = ActiveWorkSnapshot.Empty;

    public PlatformMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _workerPollFailures = meter.CreateCounter<long>(
            "migration.worker_poll_failures",
            unit: "{failure}",
            description: "Count of background-worker poll cycles that threw.");

        meter.CreateObservableGauge(
            "migration.active_batches",
            ObserveActiveBatches,
            unit: "{batch}",
            description: "Migration batches/jobs currently in an active (non-terminal) state, tagged by kind.");

        meter.CreateObservableGauge(
            "migration.stuck_jobs",
            () => _snapshot.StuckCount,
            unit: "{job}",
            description: "Active migration batches/jobs with no progress past the stuck threshold.");
    }

    /// <summary>Increment the poll-failure counter, tagged by worker name.</summary>
    public void RecordPollFailure(string worker)
        => _workerPollFailures.Add(1, new KeyValuePair<string, object?>("worker", worker));

    /// <summary>Swap in a freshly-computed snapshot (called by the refresh loop).</summary>
    public void UpdateActiveWork(ActiveWorkSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>The most recent snapshot (read by the stuck-jobs health check).</summary>
    public ActiveWorkSnapshot Current => _snapshot;

    private IEnumerable<Measurement<long>> ObserveActiveBatches()
    {
        // Copy the reference once; the snapshot is immutable so this is safe even
        // if UpdateActiveWork swaps it mid-enumeration.
        var snapshot = _snapshot;
        var measurements = new List<Measurement<long>>(snapshot.CountsByKind.Count);
        foreach (var kvp in snapshot.CountsByKind)
            measurements.Add(new Measurement<long>(
                kvp.Value, new KeyValuePair<string, object?>("kind", kvp.Key)));
        return measurements;
    }
}
