using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Act04;

/// <summary>
/// 4.2 — A singleton starts its background loop lazily, inside the first request that needs it. Task.Run, Timer and
/// CancellationToken.Register capture the current ExecutionContext: the loop runs forever as tenant "acme"
/// (wrong logs, wrong metrics, and the request's context kept alive). Start background work with
/// ExecutionContext.SuppressFlow() and register with UnsafeRegister. Bonus: .NET 11 skips capture/restore
/// when there's nothing to restore, so code that keeps AsyncLocal off the hot path is also faster.
/// </summary>
sealed class ContextLeakDemo : Demo
{
    static readonly AsyncLocal<string?> Tenant = new();

    public override string Id => "4.2";
    public override string Name => "context-leak";
    public override string Title => "Background work captures the first request's context";

    public override async Task RunAsync(Variant variant)
    {
        using var shutdown = new CancellationTokenSource();
        var refresher = new PriceRefresher(suppressFlow: variant == Variant.Fixed);

        // Three requests, three tenants. The first one wakes the singleton up.
        await RequestAsync("acme", refresher, shutdown.Token);
        await Task.WhenAll(
            RequestAsync("globex", refresher, shutdown.Token),
            RequestAsync("initech", refresher, shutdown.Token));

        await Task.Delay(400);
        Out.Log("application stopping");
        await shutdown.CancelAsync();
        await refresher.StopAsync();

        Out.Metric("Background lines tagged 'acme'", $"{refresher.TaggedLines}/{refresher.Lines}", refresher.TaggedLines == 0);
    }

    static async Task RequestAsync(string tenant, PriceRefresher refresher, CancellationToken shutdown)
    {
        Tenant.Value = tenant;
        refresher.EnsureStarted(shutdown);
        await Task.Delay(50);
        Out.Log($"[tenant={Tenant.Value}] request: served quotes");
    }

    /// <summary>Singleton: one refresh loop for the whole application.</summary>
    sealed class PriceRefresher(bool suppressFlow)
    {
        readonly CancellationTokenSource _stop = new();
        readonly Lock _gate = new();
        Task? _loop;
        int _lines, _taggedLines;

        public int Lines => Volatile.Read(ref _lines);
        public int TaggedLines => Volatile.Read(ref _taggedLines);

        public void EnsureStarted(CancellationToken shutdown)
        {
            lock (_gate)
            {
                if (_loop is not null)
                    return;

                if (suppressFlow)
                {
                    // Nothing of the current request flows into work that outlives it.
                    using (ExecutionContext.SuppressFlow())
                        _loop = Task.Run(LoopAsync);
                    shutdown.UnsafeRegister(_ => Background("flushing subscriptions on shutdown"), null);
                }
                else
                {
                    _loop = Task.Run(LoopAsync);
                    shutdown.Register(() => Background("flushing subscriptions on shutdown"));
                }
            }
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            if (_loop is not null)
                await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(150, _stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (!_stop.IsCancellationRequested)
                    Background("refreshing price cache");
            }
        }

        void Background(string what)
        {
            var tenant = Tenant.Value;
            Interlocked.Increment(ref _lines);
            if (tenant is not null)
            {
                Interlocked.Increment(ref _taggedLines);
                Out.Bad($"[tenant={tenant}] background: {what}");
            }
            else
            {
                Out.Log($"[tenant=-] background: {what}");
            }
        }
    }
}
