using System.Collections.Concurrent;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo02;

/// <summary>
/// 2.3 — Trap 2 of a cached Task: the shared load uses the token of whoever triggered it.
/// When that client disconnects, everyone waiting on the shared load gets cancelled, and the
/// cancelled Task stays in the cache. Fixed: the load takes no caller token; each caller cancels
/// only its own wait with WaitAsync(ct).
/// </summary>
sealed class SharedCancellationDemo : Demo
{
    public override string Id => "2.3";
    public override string Name => "shared-cancellation";
    public override string Title => "Trap 2: the first caller's token cancels everyone";

    public override async Task RunAsync(Variant variant)
    {
        var provider = new FakeQuoteProvider { Verbose = true };
        Func<CancellationToken, Task<Quote>> getQuote;

        if (variant == Variant.Broken)
        {
            var cache = new ConcurrentDictionary<string, Lazy<Task<Quote>>>();
            getQuote = ct => cache.GetOrAdd("MSFT",
                symbol => new Lazy<Task<Quote>>(() => provider.GetQuoteAsync(symbol, ct))).Value;
        }
        else
        {
            var cache = new ConcurrentDictionary<string, AsyncLazy<Quote>>();
            getQuote = ct => cache.GetOrAdd("MSFT",
                symbol => new AsyncLazy<Quote>(() => provider.GetQuoteAsync(symbol))).GetValueAsync(ct);
        }

        // web-1 triggers the load and disconnects after 50 ms; web-2 and web-3 are patient.
        var served = (await Task.WhenAll(
            Caller.CallAsync("web-1", getQuote, cancelAfter: TimeSpan.FromMilliseconds(50)),
            Caller.CallAsync("web-2", getQuote),
            Caller.CallAsync("web-3", getQuote))).Count(ok => ok);

        Out.Section("A minute later, a new request for the same key");
        var later = await Caller.CallAsync("web-4", getQuote);

        Out.Section("Results");
        Out.Metric("Patient callers served", $"{served}/2", served == 2);
        Out.Metric("Later request served", later, later);
        Out.Metric("Provider calls / cancelled", $"{provider.Calls} / {provider.Cancelled}");
    }
}
