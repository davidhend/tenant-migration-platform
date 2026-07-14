using System.Collections.Concurrent;

namespace MigrationPlatform.Api.Data;

/// <summary>
/// In-process, unbounded channel used to pass scan job IDs from controllers
/// to the background <see cref="Workers.ScanWorker"/>. This is intentionally
/// kept in-memory (not persisted); on restart the worker re-hydrates from the
/// database for any Queued scans.
///
/// Also carries cooperative cancellation: <see cref="RequestCancel"/> cancels a
/// running scan's linked token (or records a pending request for a queued scan),
/// which the worker observes and turns into a Cancelled job status.
/// </summary>
public class ScanJobQueue
{
    public System.Threading.Channels.Channel<Guid> Channel { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<Guid>();

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingCancels = new();

    /// <summary>
    /// Request cancellation of a scan. Cancels the live token if the scan is
    /// currently running; otherwise records the request so the worker discards
    /// the scan when it is dequeued.
    /// </summary>
    public void RequestCancel(Guid scanId)
    {
        if (_running.TryGetValue(scanId, out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* scan finished concurrently */ }
        }
        else
        {
            _pendingCancels[scanId] = 1;
        }
    }

    /// <summary>
    /// Called by the worker when a scan starts executing. Returns a token source
    /// linked to the host shutdown token that <see cref="RequestCancel"/> can trip.
    /// Must be paired with <see cref="UnregisterRunning"/>.
    /// </summary>
    public CancellationTokenSource RegisterRunning(Guid scanId, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _running[scanId] = cts;
        // A cancel may have arrived between dequeue and registration.
        if (_pendingCancels.TryRemove(scanId, out _))
            cts.Cancel();
        return cts;
    }

    /// <summary>Called by the worker when a scan stops executing (any outcome).</summary>
    public void UnregisterRunning(Guid scanId)
    {
        if (_running.TryRemove(scanId, out var cts))
            cts.Dispose();
    }

    /// <summary>
    /// Consume a pending cancel request for a scan that has not started yet.
    /// Returns true when the scan should be discarded instead of executed.
    /// </summary>
    public bool ConsumePendingCancel(Guid scanId) => _pendingCancels.TryRemove(scanId, out _);
}
