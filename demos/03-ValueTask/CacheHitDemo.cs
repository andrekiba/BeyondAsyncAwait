using System.Diagnostics;
using System.Reflection;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo03;

/// <summary>
/// 3.1 — The cache-hit path completes synchronously, but with compiler state machines an async Task&lt;decimal&gt;
/// still hands back a new Task (Task.FromResult only caches bool, small ints and null): 80 bytes per hit.
/// ValueTask&lt;decimal&gt; carries the result inline: zero. With runtime async a Task awaited directly is never
/// materialized either — the allocation comes back as soon as the Task becomes an object (field, delegate, WhenAll).
/// Compare: dotnet run -c Release -- 3.1   vs   dotnet run -c Release -p:RuntimeAsync=false -- 3.1
/// </summary>
sealed class CacheHitDemo : Demo
{
    const int Calls = 100_000;
    const string Symbol = "MSFT";

    public override string Id => "3.1";
    public override string Name => "cache-hit";
    public override string Title => "Cache hit: Task<T> vs ValueTask<T> on the hot path";

    public override async Task RunAsync(Variant variant)
    {
        Out.Info(BuildInfo.Description);
        var entry = Assembly.GetEntryAssembly()!;
        if (!BuildInfo.IsRuntimeAsync(entry) && entry.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true)
            Out.Warn("Debug build: compiler state machines are classes in Debug and allocate on every call. Use -c Release.");

        var cache = new QuoteCache(new FakeQuoteProvider { Latency = TimeSpan.FromMilliseconds(50) });
        Out.Log($"cold call (miss): {await cache.GetPriceAsTaskAsync(Symbol)}");
        Out.Log($"cache is warm, now {Calls:N0} hits per measurement");

        if (variant == Variant.Broken)
        {
            Func<string, Task<decimal>> viaDelegate = cache.GetPriceAsTaskAsync;

            await MeasureAsync("Task<T>: await call", async () =>
            {
                for (var i = 0; i < Calls; i++)
                    await cache.GetPriceAsTaskAsync(Symbol);
            });

            await MeasureAsync("Task<T>: await via delegate", async () =>
            {
                for (var i = 0; i < Calls; i++)
                    await viaDelegate(Symbol);
            });

            // A Task kept in a local can still be optimized away in Release: one kept in a field/array can't.
            await MeasureAsync("Task<T>: kept as an object", async () =>
            {
                var pending = new Task<decimal>[1];
                for (var i = 0; i < Calls; i++)
                {
                    pending[0] = cache.GetPriceAsTaskAsync(Symbol);
                    await pending[0];
                }
            });
        }
        else
        {
            Func<string, ValueTask<decimal>> viaDelegate = cache.GetPriceAsValueTaskAsync;

            await MeasureAsync("ValueTask<T>: await call", async () =>
            {
                for (var i = 0; i < Calls; i++)
                    await cache.GetPriceAsValueTaskAsync(Symbol);
            });

            await MeasureAsync("ValueTask<T>: await via delegate", async () =>
            {
                for (var i = 0; i < Calls; i++)
                    await viaDelegate(Symbol);
            });

            await MeasureAsync("ValueTask<T>: kept as an object", async () =>
            {
                var pending = new ValueTask<decimal>[1];
                for (var i = 0; i < Calls; i++)
                {
                    pending[0] = cache.GetPriceAsValueTaskAsync(Symbol);
                    await pending[0];
                }
            });
        }
    }

    // Every hit completes synchronously, so the current thread's allocation counter sees all of it.
    static async Task MeasureAsync(string label, Func<Task> loop)
    {
        await loop(); // warm-up: JIT, tiering

        var before = GC.GetAllocatedBytesForCurrentThread();
        await loop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var perCall = (double)allocated / Calls;
        Out.Metric(label, $"{perCall,5:F1} bytes/call   ({allocated:N0} bytes)", perCall < 1);
    }
}
