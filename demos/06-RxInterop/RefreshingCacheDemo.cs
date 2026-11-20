using System.Reactive.Linq;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo06;

/// <summary>
/// 6.4 — Demo 2's problem (one value, many consumers) in Rx: an auto-refreshing async cache in one expression.
/// Timer → FromAsync(load) → Switch → Replay(1), consumed with `await config.FirstAsync()`.
/// With RefCount every await connects and disconnects: the replayed old value answers synchronously,
/// the subscriber leaves, the connection drops before the timer can tick — the cache never refreshes.
/// AutoConnect keeps one connection: loads happen when the timer says so.
/// </summary>
sealed class RefreshingCacheDemo : Demo
{
    static readonly TimeSpan RefreshPeriod = TimeSpan.FromMilliseconds(500);
    const int Calls = 12;

    public override string Id => "6.4";
    public override string Name => "refreshing-cache";
    public override string Title => "Auto-refreshing async cache: Replay(1) + RefCount vs AutoConnect";

    sealed record AppConfig(int Version);

    public override async Task RunAsync(Variant variant)
    {
        int connections = 0, started = 0, completed = 0, cancelled = 0;

        async Task<AppConfig> LoadAsync(CancellationToken cancellationToken)
        {
            var version = Interlocked.Increment(ref started);
            try
            {
                await Task.Delay(100, cancellationToken);
                Interlocked.Increment(ref completed);
                Out.Log($"  config load #{version} completed");
                return new AppConfig(version);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref cancelled);
                Out.Log($"  config load #{version} cancelled");
                throw;
            }
        }

        var replayed = Observable.Defer(() =>
            {
                Interlocked.Increment(ref connections);
                return Observable.Timer(TimeSpan.Zero, RefreshPeriod);
            })
            .Select(_ => Observable.FromAsync(LoadAsync))
            .Switch()
            .Replay(1);

        IDisposable? connection = null;
        var config = variant == Variant.Broken
            ? replayed.RefCount()
            : replayed.AutoConnect(1, c => connection = c);

        Out.Info($"{Calls} consumers, one every 100 ms; the timer refreshes every {RefreshPeriod.TotalMilliseconds} ms");
        var demoStarted = Out.ElapsedMs;
        for (var call = 1; call <= Calls; call++)
        {
            var callStarted = Out.ElapsedMs;
            var current = await config.FirstAsync();
            Out.Log($"call {call,2} → config v{current.Version} ({Out.ElapsedMs - callStarted} ms)");
            await Task.Delay(100);
        }

        connection?.Dispose();
        var expected = (int)((Out.ElapsedMs - demoStarted) / RefreshPeriod.TotalMilliseconds) + 1;

        Out.Metric("Connections to the source", connections, connections == 1);
        Out.Metric("Loads the timer asked for", expected);
        Out.Metric("Loads completed", completed, completed == expected);
        Out.Metric("Loads cancelled", cancelled, cancelled == 0);

        if (variant == Variant.Broken)
            Out.Bad("RefCount: every await reconnects, the replayed v1 answers first, the timer never ticks — stale forever");
        else
            Out.Good("AutoConnect: one connection, refreshed by the timer, consumers get the replayed value");
    }
}
