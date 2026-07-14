using Npgsql;

namespace MigrationPlatform.Api.Services.InstanceLock;

/// <summary>
/// Enforces single-instance background processing via a PostgreSQL
/// <b>session-level advisory lock</b>.
///
/// <para>The platform's job queues are in-memory <see cref="System.Threading.Channels.Channel{T}"/>s
/// re-hydrated from the database on startup. That design is correct for exactly
/// ONE running instance: a second instance would re-hydrate the same active
/// jobs and poll them in parallel, creating duplicate EXO/SPO migration batches.
/// This guard makes that safe — the first instance to start acquires the lock
/// and becomes primary; any additional instance fails to acquire it, is marked
/// non-primary, and its workers suppress themselves (they consult
/// <see cref="SingleInstanceState"/>).</para>
///
/// <para>Implemented as a plain <see cref="IHostedService"/> (not a
/// <see cref="BackgroundService"/>) so the lock is acquired synchronously inside
/// <see cref="StartAsync"/>: the host awaits it before starting later hosted
/// services, guaranteeing <see cref="SingleInstanceState.IsPrimary"/> is set
/// before any worker's ExecuteAsync runs. It must therefore be registered
/// BEFORE the workers.</para>
///
/// <para>The lock is held for the process lifetime by keeping a dedicated
/// connection open; PostgreSQL releases a session advisory lock automatically
/// when that connection closes (including on crash), so no manual cleanup is
/// required for correctness — <see cref="StopAsync"/> releases it promptly on a
/// graceful shutdown.</para>
/// </summary>
public sealed class SingleInstanceGuard : IHostedService, IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SingleInstanceGuard> _logger;
    private NpgsqlConnection? _lockConnection;

    public SingleInstanceGuard(IConfiguration configuration, ILogger<SingleInstanceGuard> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Deterministic 64-bit advisory-lock key derived from a fixed application
    /// string via FNV-1a. Kept as a pure function so the derivation is unit
    /// testable and stable across releases (changing it would let two instances
    /// with different versions both become primary).
    /// </summary>
    internal static long DeriveLockKey(string name)
    {
        // 64-bit FNV-1a.
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(name))
        {
            hash ^= b;
            hash *= prime;
        }
        // pg_try_advisory_lock takes a signed bigint; reinterpret the bits.
        return unchecked((long)hash);
    }

    internal static readonly long LockKey = DeriveLockKey("MigrationPlatform.SingleInstance");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("SingleInstance:Enforce", true))
        {
            _logger.LogInformation(
                "SingleInstance:Enforce is false — advisory lock skipped; this instance runs workers unconditionally. " +
                "Run only ONE instance with workers enabled, or duplicate migration batches may be created.");
            SingleInstanceState.IsPrimary = true;
            return;
        }

        var connString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connString))
        {
            _logger.LogWarning(
                "SingleInstanceGuard: no DefaultConnection configured — cannot acquire the single-instance lock; " +
                "proceeding as primary.");
            SingleInstanceState.IsPrimary = true;
            return;
        }

        try
        {
            _lockConnection = new NpgsqlConnection(connString);
            await _lockConnection.OpenAsync(cancellationToken);

            await using var cmd = _lockConnection.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
            cmd.Parameters.AddWithValue("key", LockKey);
            var acquired = (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;

            SingleInstanceState.IsPrimary = acquired;

            if (acquired)
            {
                _logger.LogInformation(
                    "SingleInstanceGuard: acquired the single-instance lock — this instance is PRIMARY and will run workers.");
            }
            else
            {
                _logger.LogCritical(
                    "SingleInstanceGuard: another instance already holds the single-instance lock. This instance is " +
                    "SECONDARY — all background workers are suppressed to avoid duplicate migration batches. " +
                    "Run only one instance with workers enabled, or set Workers:Enabled=false on the extra instances. " +
                    "HTTP endpoints on this instance continue to serve normally.");
                // Didn't get the lock — release the dedicated connection.
                await _lockConnection.DisposeAsync();
                _lockConnection = null;
            }
        }
        catch (Exception ex)
        {
            // Database unreachable at startup — do not block the process. A single
            // instance must still run; the existing DB-reachability warnings cover
            // the connectivity problem itself.
            _logger.LogWarning(ex,
                "SingleInstanceGuard: could not acquire the single-instance lock (database unreachable?) — " +
                "proceeding as primary. Ensure only one instance runs workers until the database is reachable.");
            SingleInstanceState.IsPrimary = true;
            if (_lockConnection is not null)
            {
                await _lockConnection.DisposeAsync();
                _lockConnection = null;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lockConnection is null) return;
        try
        {
            await using var cmd = _lockConnection.CreateCommand();
            cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
            cmd.Parameters.AddWithValue("key", LockKey);
            await cmd.ExecuteScalarAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SingleInstanceGuard: advisory unlock on shutdown failed (connection closing anyway).");
        }
        finally
        {
            await _lockConnection.DisposeAsync();
            _lockConnection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_lockConnection is not null)
        {
            await _lockConnection.DisposeAsync();
            _lockConnection = null;
        }
    }
}
