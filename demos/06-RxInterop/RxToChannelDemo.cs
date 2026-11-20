using System.Reactive.Subjects;
using System.Threading.Channels;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo06;

/// <summary>
/// 6.3 — Push (Rx, no backpressure) meets pull (IAsyncEnumerable, a slow consumer).
/// Converting push to pull must buffer or drop: the one-liner ToAsyncEnumerable() buffers everything,
/// silently, and the consumer works on older and older prices. An explicit bounded Channel with
/// DropOldest makes the policy visible: memory stays flat, drops are counted, prices stay fresh.
/// </summary>
sealed class RxToChannelDemo : Demo
{
    const int Reports = 5;
    const int SlicesPerReport = 25;
    const int TicksPerSlice = 4_000;
    const int Capacity = 8;
    static readonly TimeSpan ConsumerWork = TimeSpan.FromMilliseconds(10);

    public override string Id => "6.3";
    public override string Name => "rx-to-channel";
    public override string Title => "Rx → IAsyncEnumerable: who holds the backlog?";

    public override async Task RunAsync(Variant variant)
    {
        // Hot market data: pushed from a socket callback, it cannot be slowed down.
        var ticks = new Subject<Quote>();
        long produced = 0, consumed = 0, dropped = 0;
        IDisposable bridge = System.Reactive.Disposables.Disposable.Empty;
        IAsyncEnumerable<Quote> stream;

        if (variant == Variant.Broken)
        {
            Out.Info("ticks.ToAsyncEnumerable()  — AsyncEnumerableEx, System.Interactive.Async 7");
            stream = ticks.ToAsyncEnumerable();
        }
        else
        {
            Out.Info($"ticks → Channel.CreateBounded({Capacity}, DropOldest) → ReadAllAsync()");
            var channel = Channel.CreateBounded<Quote>(
                new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
                itemDropped: _ => Interlocked.Increment(ref dropped));

            bridge = ticks.Subscribe(
                quote => channel.Writer.TryWrite(quote),
                ex => channel.Writer.TryComplete(ex),
                () => channel.Writer.TryComplete());
            stream = channel.Reader.ReadAllAsync();
        }

        using var stop = new CancellationTokenSource();
        double lastAgeMs = 0;
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var quote in stream.WithCancellation(stop.Token))
                {
                    Volatile.Write(ref lastAgeMs, (DateTimeOffset.UtcNow - quote.Timestamp).TotalMilliseconds);
                    Interlocked.Increment(ref consumed);
                    await Task.Delay(ConsumerWork, stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        for (var report = 1; report <= Reports; report++)
        {
            for (var slice = 0; slice < SlicesPerReport; slice++)
            {
                for (var i = 0; i < TicksPerSlice; i++)
                {
                    ticks.OnNext(new Quote("MSFT", 100m + i % 100, DateTimeOffset.UtcNow));
                    produced++;
                }

                await Task.Delay(10);
            }

            var backlog = produced - Interlocked.Read(ref consumed) - Interlocked.Read(ref dropped);
            var heapMb = (GC.GetTotalMemory(forceFullCollection: true) - heapBefore) / 1024.0 / 1024.0;
            Out.Log($"produced {produced,7:N0} │ consumed {Interlocked.Read(ref consumed),3} │ dropped {Interlocked.Read(ref dropped),7:N0} │ backlog {backlog,7:N0} │ heap {heapMb,6:+0.0;-0.0} MB │ age of price in use {Volatile.Read(ref lastAgeMs),5:N0} ms");
        }

        stop.Cancel();
        await consumer;
        bridge.Dispose();

        var finalBacklog = produced - consumed - dropped;
        Out.Metric("Backlog when we stopped", finalBacklog.ToString("N0"), finalBacklog <= Capacity);
        Out.Metric("Dropped (counted)", dropped.ToString("N0"));
        Out.Metric("Age of the last price processed", $"{lastAgeMs:N0} ms", lastAgeMs < 500);

        if (variant == Variant.Broken)
            Out.Bad("Unbounded hidden queue: memory grows and the consumer processes stale prices");
        else
            Out.Good("Bounded buffer: the drop policy is explicit, memory is flat, prices are fresh");
    }
}
