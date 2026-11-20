using System.Collections.Concurrent;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo01;

/// <summary>
/// 1.2 — Request/response over a message bus: a TaskCompletionSource per request, correlated by id.
/// The naive client never removes entries, never disposes its registrations on the connection token,
/// and uses SetResult, so a reply arriving after the timeout throws inside the bus.
/// Fixed: TrySet*, dispose CTS and registration, remove the entry in finally.
/// </summary>
sealed class RpcCorrelationDemo : Demo
{
    const int Requests = 1000;
    static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    public override string Id => "1.2";
    public override string Name => "rpc-correlation";
    public override string Title => "RPC over a message bus with TaskCompletionSource";

    public override async Task RunAsync(Variant variant)
    {
        const int rounds = 3;
        var bus = new FakeBus();
        using var connectionLifetime = new CancellationTokenSource();
        IRpcClient client = variant == Variant.Broken ? new NaiveRpcClient(bus) : new RpcClient(bus);

        int received = 0, timedOut = 0;
        long firstRoundHeap = 0, lastRoundHeap = 0;

        for (var round = 1; round <= rounds; round++)
        {
            var outcomes = await Task.WhenAll(Enumerable.Range(0, Requests)
                .Select(i => CallAsync(client, $"SYM{i % 20}", connectionLifetime.Token)));
            received += outcomes.Count(ok => ok);
            timedOut += outcomes.Count(ok => !ok);

            // Late replies are still on the wire: give them time to arrive.
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            var heap = GC.GetTotalMemory(forceFullCollection: true);
            if (round == 1)
                firstRoundHeap = heap;
            lastRoundHeap = heap;
            Out.Log($"round {round}: {Requests} requests (timeout {Timeout.TotalMilliseconds} ms), pending entries {client.PendingCount}, heap {heap / 1024:N0} KB");
        }

        // The connection lives for hours: what matters is whether memory keeps growing round after round.
        var growth = lastRoundHeap - firstRoundHeap;

        Out.Section("Results");
        Out.Metric("Replies received", received);
        Out.Metric("Timed out", timedOut);
        Out.Metric("Late replies (after the timeout)", bus.LateReplies);
        Out.Metric("Exceptions thrown into the bus", bus.HandlerExceptions, bus.HandlerExceptions == 0);
        Out.Metric("Pending entries left", client.PendingCount, client.PendingCount == 0);
        Out.Metric($"Heap growth, round 1 → {rounds}", $"{growth / 1024:+#,0;-#,0;0} KB", growth < 32 * 1024);
    }

    static async Task<bool> CallAsync(IRpcClient client, string symbol, CancellationToken cancellationToken)
    {
        try
        {
            await client.RequestAsync(symbol, Timeout, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

sealed record RpcReply(Guid Id, Quote Quote);

interface IRpcClient
{
    int PendingCount { get; }
    Task<RpcReply> RequestAsync(string symbol, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Broken: the usual first version.</summary>
sealed class NaiveRpcClient : IRpcClient
{
    readonly ConcurrentDictionary<Guid, TaskCompletionSource<RpcReply>> pending = new();
    readonly FakeBus bus;

    public NaiveRpcClient(FakeBus bus)
    {
        this.bus = bus;
        bus.Subscribe(OnReply);
    }

    public int PendingCount => pending.Count;

    public Task<RpcReply> RequestAsync(string symbol, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var tcs = new TaskCompletionSource<RpcReply>();
        pending[id] = tcs;

        // Never disposed: stays registered on the connection token, keeping tcs alive.
        cancellationToken.Register(() => tcs.SetCanceled());

        // Never disposed either.
        var timeoutCts = new CancellationTokenSource(timeout);
        timeoutCts.Token.Register(() =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.SetCanceled();
        });

        bus.Publish(id, symbol);
        return tcs.Task;
    }

    void OnReply(RpcReply reply)
    {
        // Never removed; throws if the timeout already completed the task.
        if (pending.TryGetValue(reply.Id, out var tcs))
            tcs.SetResult(reply);
    }
}

/// <summary>Fixed.</summary>
sealed class RpcClient : IRpcClient
{
    readonly ConcurrentDictionary<Guid, TaskCompletionSource<RpcReply>> pending = new();
    readonly FakeBus bus;

    public RpcClient(FakeBus bus)
    {
        this.bus = bus;
        bus.Subscribe(OnReply);
    }

    public int PendingCount => pending.Count;

    public async Task<RpcReply> RequestAsync(string symbol, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var tcs = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using var registration = timeoutCts.Token.Register(
                static state => ((TaskCompletionSource<RpcReply>)state!).TrySetCanceled(), tcs);

            bus.Publish(id, symbol);
            return await tcs.Task;
            // Shorter alternative for the timeout part: await tcs.Task.WaitAsync(timeout, cancellationToken)
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    void OnReply(RpcReply reply)
    {
        // A late reply finds nothing: it's ignored, not thrown.
        if (pending.TryRemove(reply.Id, out var tcs))
            tcs.TrySetResult(reply);
    }
}

/// <summary>
/// Deterministic bus: of every 10 messages, 1 is dropped, 2 are answered after 250 ms (late), 7 within 40 ms.
/// Like a real client library, it catches and counts exceptions thrown by reply handlers.
/// </summary>
sealed class FakeBus
{
    Action<RpcReply>? handler;
    int published, lateReplies, handlerExceptions;

    public int LateReplies => Volatile.Read(ref lateReplies);
    public int HandlerExceptions => Volatile.Read(ref handlerExceptions);

    public void Subscribe(Action<RpcReply>? handler) => this.handler = handler;

    public void Publish(Guid id, string symbol)
    {
        var index = Interlocked.Increment(ref published);
        var slot = index % 10;
        if (slot == 0)
            return;

        var late = slot is 1 or 2;
        var latency = late ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromMilliseconds(20 + index % 3 * 10);
        _ = DeliverAsync(new RpcReply(id, new Quote(symbol, 100 + slot, DateTimeOffset.UtcNow)), latency, late);
    }

    async Task DeliverAsync(RpcReply reply, TimeSpan latency, bool late)
    {
        await Task.Delay(latency);
        if (late)
            Interlocked.Increment(ref lateReplies);

        try
        {
            handler?.Invoke(reply);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref handlerExceptions) == 1)
                Out.Bad($"bus: reply handler threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
