using System.Diagnostics;
using System.Threading.Channels;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo05;

/// <summary>
/// 5.1 — A fast producer (market ticks, ~2000/s) feeds a slow consumer (persistence, ~200/s).
/// Unbounded: nothing ever pushes back, so the gap becomes queue, memory and latency.
/// Bounded: the channel decides what "full" means. Wait slows the producer down (orders: nothing may be lost);
/// DropOldest keeps the freshest data and reports what it throws away (prices: only the latest one matters).
/// </summary>
sealed class BackpressureDemo : Demo
{
    const int Capacity = 100;
    const int TicksPerBurst = 100;          // producer: 100 ticks every 50 ms ≈ 2000/s
    const int TicksPerBatch = 10;           // consumer: 10 ticks every 50 ms ≈ 200/s
    static readonly TimeSpan Period = TimeSpan.FromMilliseconds(50);
    static readonly TimeSpan Duration = TimeSpan.FromSeconds(2.5);
    static readonly string[] Symbols = ["MSFT", "AAPL", "NVDA", "AMZN"];

    enum Mode { Unbounded, Wait, DropOldest }

    public override string Id => "5.1";
    public override string Name => "backpressure";
    public override string Title => "Backpressure: unbounded vs bounded channels";

    public override async Task RunAsync(Variant variant)
    {
        if (variant == Variant.Broken)
        {
            Out.Section("Channel.CreateUnbounded — the producer never waits");
            await RunScenarioAsync(Mode.Unbounded);
            return;
        }

        Out.Section($"Bounded({Capacity}), FullMode.Wait — orders: nothing may be lost, so the producer slows down");
        await RunScenarioAsync(Mode.Wait);

        Out.Section($"Bounded({Capacity}), FullMode.DropOldest — prices: only the latest one matters");
        await RunScenarioAsync(Mode.DropOldest);

        Out.Info("Which FullMode is right is a business decision, not a technical one.");
    }

    static async Task RunScenarioAsync(Mode mode)
    {
        Out.ResetClock();
        var stats = new Stats();
        var channel = mode switch
        {
            Mode.Unbounded => Channel.CreateUnbounded<Tick>(),
            Mode.Wait => Channel.CreateBounded<Tick>(new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait }),
            // itemDropped: the only way to know (count, log, dispose) what DropOldest threw away
            _ => Channel.CreateBounded<Tick>(
                new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest },
                itemDropped: _ => stats.OnDropped())
        };

        GC.Collect();
        var baselineMemory = GC.GetTotalMemory(forceFullCollection: true);

        using var cts = new CancellationTokenSource(Duration);
        var producer = ProduceAsync(channel.Writer, stats, cts.Token);
        var consumer = ConsumeAsync(channel.Reader, stats, cts.Token);

        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(500); // no token: the loop ends on its own at the deadline, and the last sample must still be printed
            Out.Log($"produced {stats.Produced,5}  persisted {stats.Persisted,4}  queued {channel.Reader.Count,5}  " +
                    $"dropped {stats.Dropped,5}  staleness {stats.StalenessMs,5} ms  memory +{LiveMegaBytes(baselineMemory):0.0} MB");
        }

        await Task.WhenAll(producer, consumer);

        var seconds = Duration.TotalSeconds;
        var queued = channel.Reader.Count;
        var memory = LiveMegaBytes(baselineMemory);

        Out.Metric("Producer rate", $"{stats.Produced / seconds:0} ticks/s", mode == Mode.Wait ? false : null);
        Out.Metric("Consumer rate", $"{stats.Persisted / seconds:0} ticks/s");
        Out.Metric("Still queued", queued, queued <= Capacity);
        Out.Metric("Dropped (itemDropped callback)", stats.Dropped, mode == Mode.DropOldest ? null : stats.Dropped == 0);
        Out.Metric("Age of the last persisted tick", $"{stats.StalenessMs} ms", stats.StalenessMs < 1000);
        Out.Metric("Memory held by the queue", $"+{memory:0.0} MB", memory < 1);

        switch (mode)
        {
            case Mode.Unbounded:
                Out.Bad("The queue grows without limit: memory and latency climb until the process dies.");
                break;
            case Mode.Wait:
                Out.Warn("Nothing lost, memory flat — but the producer now runs at the consumer's speed: upstream must cope.");
                break;
            case Mode.DropOldest:
                Out.Good("Producer at full speed, memory flat, the data persisted is fresh — the old ticks are gone, and counted.");
                break;
        }
    }

    static async Task ProduceAsync(ChannelWriter<Tick> writer, Stats stats, CancellationToken cancellationToken)
    {
        var sequence = 0L;
        try
        {
            while (true)
            {
                for (var i = 0; i < TicksPerBurst; i++)
                {
                    var symbol = Symbols[sequence % Symbols.Length];
                    await writer.WriteAsync(new Tick(++sequence, symbol, Stopwatch.GetTimestamp(), new byte[1024]), cancellationToken);
                    stats.OnProduced();
                }

                await Task.Delay(Period, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    static async Task ConsumeAsync(ChannelReader<Tick> reader, Stats stats, CancellationToken cancellationToken)
    {
        var batch = 0;
        try
        {
            await foreach (var tick in reader.ReadAllAsync(cancellationToken))
            {
                stats.OnPersisted(tick);
                if (++batch == TicksPerBatch)
                {
                    batch = 0;
                    await Task.Delay(Period, cancellationToken);   // the database round-trip
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Live memory only: a full collection first, so the ticks DropOldest threw away are not counted.
    static double LiveMegaBytes(long baseline) => (GC.GetTotalMemory(forceFullCollection: true) - baseline) / 1024.0 / 1024.0;

    /// <summary>A market tick with its raw 1 KB message attached.</summary>
    sealed record Tick(long Sequence, string Symbol, long Timestamp, byte[] Raw);

    sealed class Stats
    {
        int produced, persisted, dropped;
        long stalenessMs;

        public int Produced => Volatile.Read(ref produced);
        public int Persisted => Volatile.Read(ref persisted);
        public int Dropped => Volatile.Read(ref dropped);
        public long StalenessMs => Volatile.Read(ref stalenessMs);

        public void OnProduced() => Interlocked.Increment(ref produced);
        public void OnDropped() => Interlocked.Increment(ref dropped);

        public void OnPersisted(Tick tick)
        {
            Interlocked.Increment(ref persisted);
            Volatile.Write(ref stalenessMs, (long)Stopwatch.GetElapsedTime(tick.Timestamp).TotalMilliseconds);
        }
    }
}
