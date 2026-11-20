using BeyondAsyncAwait.Shared;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondAsyncAwait.Demo02;

/// <summary>
/// 2.5 — In production, use the one that exists: HybridCache (.NET 9+) has stampede protection built in.
/// Having built AsyncLazy by hand, we know which question to ask it: what happens when callers cancel?
/// </summary>
sealed class HybridCacheDemo : Demo
{
    public override string Id => "2.5";
    public override string Name => "hybrid-cache";
    public override string Title => "HybridCache: stampede protection and shared cancellation";
    public override bool HasVariants => false;

    public override async Task RunAsync(Variant variant)
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        await using var serviceProvider = services.BuildServiceProvider();
        var cache = serviceProvider.GetRequiredService<HybridCache>();
        var provider = new FakeQuoteProvider();

        Func<CancellationToken, Task<Quote>> Get(string symbol) => ct =>
            cache.GetOrCreateAsync($"quote:{symbol}", token => new ValueTask<Quote>(provider.GetQuoteAsync(symbol, token)),
                cancellationToken: ct).AsTask();

        Out.Section("200 concurrent requests on a cold key");
        var getMsft = Get("MSFT");
        await Concurrently.RunAsync(200, () => getMsft(CancellationToken.None));
        Out.Metric("Provider calls", provider.Calls, provider.Calls == 1);

        provider.Reset();
        provider.Verbose = true;
        Out.Section("One caller disconnects after 50 ms, two wait");
        var getAapl = Get("AAPL");
        var served = (await Task.WhenAll(
            Caller.CallAsync("web-1", getAapl, cancelAfter: TimeSpan.FromMilliseconds(50)),
            Caller.CallAsync("web-2", getAapl),
            Caller.CallAsync("web-3", getAapl))).Count(ok => ok);
        Out.Metric("Patient callers served", $"{served}/2", served == 2);
        Out.Metric("Provider calls / cancelled", $"{provider.Calls} / {provider.Cancelled}");
        Out.Dim("  → one caller leaving cancels only its own wait, like AsyncLazy + WaitAsync");

        provider.Reset();
        Out.Section("Every caller disconnects (50 ms and 80 ms)");
        var getGoog = Get("GOOG");
        await Task.WhenAll(
            Caller.CallAsync("web-1", getGoog, cancelAfter: TimeSpan.FromMilliseconds(50)),
            Caller.CallAsync("web-2", getGoog, cancelAfter: TimeSpan.FromMilliseconds(80)));
        await Task.Delay(250);
        Out.Metric("Provider calls / cancelled", $"{provider.Calls} / {provider.Cancelled}");
        Out.Dim("  → when the last caller leaves, HybridCache cancels the shared load (AsyncLazy lets it finish)");

        Out.Section("A new request for the same key");
        await Caller.CallAsync("web-3", getGoog);
        Out.Metric("Provider calls / cancelled", $"{provider.Calls} / {provider.Cancelled}");
        Out.Dim("  → and the cancelled load was not cached: a new call");
    }
}
