using AirBridge.Core.Interfaces;

namespace AirBridge.Transfer;

/// <summary>
/// Holds and processes <see cref="ITransferSession"/> objects, running up to
/// <see cref="Concurrency"/> sessions in parallel.
/// <para>
/// Sessions are started in enqueue order within each concurrency slot.
/// <see cref="PauseAllAsync"/> pauses all active sessions; <see cref="CancelAllAsync"/>
/// cancels everything, including queued sessions that have not yet started.
/// </para>
/// </summary>
public sealed class TransferQueue : IDisposable
{
    private readonly int _concurrency;
    private readonly SemaphoreSlim _slot;
    private readonly List<ITransferSession> _allSessions = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="TransferQueue"/>.
    /// </summary>
    /// <param name="concurrency">
    /// Maximum number of sessions that may run concurrently. Defaults to 1 (sequential).
    /// </param>
    public TransferQueue(int concurrency = 1)
    {
        if (concurrency < 1) throw new ArgumentOutOfRangeException(nameof(concurrency));
        _concurrency = concurrency;
        _slot        = new SemaphoreSlim(concurrency, concurrency);
    }

    /// <summary>Maximum number of sessions that run simultaneously.</summary>
    public int Concurrency => _concurrency;

    /// <summary>
    /// Snapshot of all sessions that have been enqueued (regardless of state).
    /// </summary>
    public IReadOnlyList<ITransferSession> AllSessions
    {
        get { lock (_lock) return _allSessions.ToList(); }
    }

    /// <summary>
    /// Adds <paramref name="session"/> to the queue and schedules it to run when a
    /// concurrency slot is available.  Returns a <see cref="Task"/> that completes
    /// when the session reaches a terminal state (<c>Completed</c>, <c>Failed</c>,
    /// or <c>Cancelled</c>).
    /// </summary>
    /// <param name="session">The session to enqueue.</param>
    /// <param name="cancellationToken">
    /// Token that cancels the session if signalled while it is waiting for a slot
    /// or while it is running.
    /// </param>
    public Task EnqueueAsync(ITransferSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_lock) _allSessions.Add(session);
        return RunWhenSlotAvailableAsync(session, cancellationToken);
    }

    /// <summary>
    /// Pauses all sessions that are currently in the <c>Active</c> state.
    /// Already-paused, completed, or cancelled sessions are not affected.
    /// </summary>
    public async Task PauseAllAsync()
    {
        List<ITransferSession> snapshot;
        lock (_lock) snapshot = _allSessions.ToList();

        foreach (var s in snapshot)
            if (s.State == TransferState.Active)
                await s.PauseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels every session that has not yet reached a terminal state.
    /// </summary>
    public async Task CancelAllAsync()
    {
        List<ITransferSession> snapshot;
        lock (_lock) snapshot = _allSessions.ToList();

        foreach (var s in snapshot)
            if (s.State is TransferState.Pending or TransferState.Active or TransferState.Paused)
                await s.CancelAsync().ConfigureAwait(false);
    }

    // ── Private ────────────────────────────────────────────────────────────

    private async Task RunWhenSlotAvailableAsync(ITransferSession session, CancellationToken ct)
    {
        // Wait for a free concurrency slot (or cancellation)
        await _slot.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ct.IsCancellationRequested)
            {
                await session.CancelAsync().ConfigureAwait(false);
                return;
            }

            // Let the session-level CancellationToken propagate; if it throws we
            // swallow it here so the queue keeps processing subsequent sessions.
            try
            {
                await session.StartAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Session was cancelled or paused — state is already set by the session.
            }
            catch (Exception ex)
            {
                // Log-friendly: surface as a completed (faulted) task; the session State
                // is already Failed. Callers who care attach StateChanged handlers.
                _ = ex; // suppress unused-variable warning
            }
        }
        finally
        {
            _slot.Release();
        }
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _slot.Dispose();
    }
}
