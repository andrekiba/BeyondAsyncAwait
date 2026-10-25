using System.Collections.Concurrent;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Act02;

/// <summary>
/// 2.1 — 200 concurrent requests for the same cold key.
/// Caching the value lets every request miss; caching the Task with GetOrAdd is better but not enough,
/// because GetOrAdd doesn't guarantee the factory runs once. Lazy&lt;Task&lt;T&gt;&gt; does.
/// </summary>
sealed class CacheStampedeDemo : Demo
{
    const int Requests = 200;

    public override string Id => "2.1";
    public override string Name => "cache-stampede";
    public override string Title => "Cache stampede: 200 requests, one cold key";

    public override async Task RunAsync(Variant variant)
    {
        var provider = new FakeQuoteProvider();

        if (variant == Variant.Broken)
        {
            Out.Section("A. Cache the value: check, await, store");
            var values = new ConcurrentDictionary<string, Quote>();
            await Concurrently.RunAsync(Requests, () => GetCachingValueAsync(values, provider, "MSFT"));
            Out.Metric("Provider calls", provider.Calls, provider.Calls == 1);

            provider.Reset();
            Out.Section("B. Cache the Task with ConcurrentDictionary.GetOrAdd");
            var tasks = new ConcurrentDictionary<string, Task<Quote>>();
            await Concurrently.RunAsync(Requests, () => tasks.GetOrAdd("MSFT", symbol => LoadAsync(provider, symbol)));
            Out.Metric("Provider calls", provider.Calls, provider.Calls == 1);
        }
        else
        {
            Out.Section("Cache Lazy<Task<T>>: GetOrAdd may build several Lazy, only the published one runs");
            var lazies = new ConcurrentDictionary<string, Lazy<Task<Quote>>>();
            var created = 0;
            await Concurrently.RunAsync(Requests, () => lazies.GetOrAdd("MSFT", symbol =>
            {
                Interlocked.Increment(ref created);
                return new Lazy<Task<Quote>>(() => LoadAsync(provider, symbol));
            }).Value);
            Out.Metric("Lazy instances created", created);
            Out.Metric("Provider calls", provider.Calls, provider.Calls == 1);
        }
    }

    static async Task<Quote> GetCachingValueAsync(ConcurrentDictionary<string, Quote> cache, FakeQuoteProvider provider, string symbol)
    {
        if (cache.TryGetValue(symbol, out var cached))
            return cached;

        var quote = await LoadAsync(provider, symbol);
        cache[symbol] = quote;
        return quote;
    }

    // A real loader has a synchronous prefix (build the request, take a pooled connection...):
    // that is the window in which concurrent callers all see a miss.
    static Task<Quote> LoadAsync(FakeQuoteProvider provider, string symbol)
    {
        Thread.Sleep(5);
        return provider.GetQuoteAsync(symbol);
    }
}
