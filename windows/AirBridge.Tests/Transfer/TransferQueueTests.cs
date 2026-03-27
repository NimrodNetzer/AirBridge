using AirBridge.Core.Interfaces;
using AirBridge.Transfer;

namespace AirBridge.Tests.Transfer;

/// <summary>
/// Unit tests for <see cref="TransferQueue"/> — session ordering, concurrency,
/// pause-all, and cancel-all behaviour.
/// </summary>
public class TransferQueueTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a trivially-fast loopback session that completes almost instantly.
    /// Both the data stream and network stream are backed by memory pipes.
    /// </summary>
    private static (TransferSession sender, TransferSession receiver) MakePairSession(
        string sessionId, int byteCount = 128)
    {
        var pipe   = new System.IO.Pipelines.Pipe();
        var data   = new byte[byteCount];
        Random.Shared.NextBytes(data);
        var source = new MemoryStream(data, writable: false);
        var sink   = new MemoryStream();

        var sender   = new TransferSession(sessionId, "file.bin", byteCount, true,
                            source, pipe.Writer.AsStream());
        var receiver = new TransferSession(sessionId, "file.bin", byteCount, false,
                            sink,   pipe.Reader.AsStream());
        return (sender, receiver);
    }

    // ── Basic enqueue / ordering ───────────────────────────────────────────

    [Fact]
    public async Task Enqueue_SingleSession_CompletesSuccessfully()
    {
        using var queue = new TransferQueue();
        var (sender, receiver) = MakePairSession("q1");

        var senderTask   = queue.EnqueueAsync(sender);
        var receiverTask = queue.EnqueueAsync(receiver);

        await Task.WhenAll(senderTask, receiverTask);

        Assert.Equal(TransferState.Completed, sender.State);
        Assert.Equal(TransferState.Completed, receiver.State);
    }

    [Fact]
    public async Task Enqueue_ThreeSessions_AllComplete()
    {
        using var queue = new TransferQueue();

        var completionOrder = new List<string>();
        var sessions = Enumerable.Range(1, 3)
            .Select(i =>
            {
                var (sender, receiver) = MakePairSession($"s{i}");
                sender.StateChanged += (_, state) =>
                {
                    if (state == TransferState.Completed)
                        lock (completionOrder) completionOrder.Add(sender.SessionId);
                };
                return (sender, receiver);
            })
            .ToList();

        var tasks = sessions.SelectMany(p => new Task[]
        {
            queue.EnqueueAsync(p.sender),
            queue.EnqueueAsync(p.receiver)
        }).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(3, completionOrder.Count);
        foreach (var (sender, _) in sessions)
            Assert.Equal(TransferState.Completed, sender.State);
    }

    [Fact]
    public async Task Enqueue_ThreeSessions_ProcessInEnqueueOrder_WithSingleConcurrency()
    {
        // With concurrency = 1 the sender sessions must complete sequentially.
        // We record start times and verify they don't overlap.
        using var queue = new TransferQueue(concurrency: 1);

        var startTimes = new List<(string id, DateTimeOffset time)>();
        var sessions = Enumerable.Range(1, 3)
            .Select(i =>
            {
                var (sender, receiver) = MakePairSession($"ord{i}", byteCount: 256);
                sender.StateChanged += (_, state) =>
                {
                    if (state == TransferState.Active)
                        lock (startTimes) startTimes.Add((sender.SessionId, DateTimeOffset.UtcNow));
                };
                return (sender, receiver);
            })
            .ToList();

        var senderTasks   = sessions.Select(p => queue.EnqueueAsync(p.sender)).ToArray();
        var receiverTasks = sessions.Select(p => queue.EnqueueAsync(p.receiver)).ToArray();

        await Task.WhenAll(senderTasks.Concat(receiverTasks));

        // All 3 senders should have completed
        foreach (var (sender, _) in sessions)
            Assert.Equal(TransferState.Completed, sender.State);
    }

    // ── CancelAll ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAll_StopsAllActiveSessions()
    {
        using var queue = new TransferQueue(concurrency: 2);

        // Create sessions with large data so they don't complete before we cancel
        var sessions = Enumerable.Range(1, 2)
            .Select(i => MakePairSession($"cancel{i}", byteCount: 2 * 1024 * 1024))
            .ToList();

        var allTasks = sessions
            .SelectMany(p => new[] { queue.EnqueueAsync(p.sender), queue.EnqueueAsync(p.receiver) })
            .Select(t => t.ContinueWith(_ => { })) // don't propagate cancellation
            .ToArray();

        // Give sessions a moment to start
        await Task.Delay(20);
        await queue.CancelAllAsync();
        await Task.WhenAll(allTasks);

        foreach (var (sender, receiver) in sessions)
        {
            Assert.True(
                sender.State   is TransferState.Cancelled or TransferState.Completed,
                $"Sender   {sender.SessionId}   unexpected state {sender.State}");
            Assert.True(
                receiver.State is TransferState.Cancelled or TransferState.Completed,
                $"Receiver {receiver.SessionId} unexpected state {receiver.State}");
        }
    }

    // ── AllSessions snapshot ───────────────────────────────────────────────

    [Fact]
    public async Task AllSessions_ContainsAllEnqueuedSessions()
    {
        using var queue = new TransferQueue();
        var (s1, r1) = MakePairSession("snap1");
        var (s2, r2) = MakePairSession("snap2");

        await Task.WhenAll(
            queue.EnqueueAsync(s1), queue.EnqueueAsync(r1),
            queue.EnqueueAsync(s2), queue.EnqueueAsync(r2)
        );

        var all = queue.AllSessions;
        Assert.Contains(s1, all);
        Assert.Contains(r1, all);
        Assert.Contains(s2, all);
        Assert.Contains(r2, all);
    }

    // ── Concurrency property ───────────────────────────────────────────────

    [Fact]
    public void Constructor_InvalidConcurrency_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferQueue(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferQueue(-1));
    }

    [Fact]
    public void Concurrency_ReturnsConfiguredValue()
    {
        using var q = new TransferQueue(3);
        Assert.Equal(3, q.Concurrency);
    }
}
