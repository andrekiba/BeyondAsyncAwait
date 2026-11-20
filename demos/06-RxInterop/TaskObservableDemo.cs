using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo06;

/// <summary>
/// 6.1 — Task and IObservable convert into each other, but the semantics don't line up.
/// `await observable` waits for completion and returns the LAST value (and throws if there is none).
/// `task.ToObservable()` wraps a Task that is already running: Retry re-subscribes to the same faulted Task,
/// unsubscribing cancels nothing. `Observable.FromAsync(ct => ...)` starts a new call per subscription and
/// cancels the token on unsubscribe.
/// </summary>
sealed class TaskObservableDemo : Demo
{
    public override string Id => "6.1";
    public override string Name => "task-observable";
    public override string Title => "Task ↔ IObservable: await, Retry and cancellation";

    public override async Task RunAsync(Variant variant)
    {
        await AwaitSemanticsAsync(variant);
        await RetryAsync(variant);
        await CancellationAsync(variant);
    }

    static IObservable<decimal> PriceTicks() =>
        Observable.Interval(TimeSpan.FromMilliseconds(100)).Take(3).Select(i => 100m + i);

    static async Task AwaitSemanticsAsync(Variant variant)
    {
        Out.Section("\"I want the current price\": await on an observable");
        var started = Out.ElapsedMs;

        if (variant == Variant.Broken)
        {
            var price = await PriceTicks();
            Out.Bad($"await prices → {price} after {Out.ElapsedMs - started} ms: the LAST value, only when the stream completes");

            try
            {
                await Observable.Empty<Quote>();
            }
            catch (InvalidOperationException ex)
            {
                Out.Bad($"await Observable.Empty() → {ex.GetType().Name}: {ex.Message}");
            }

            using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await Observable.Interval(TimeSpan.FromMilliseconds(100)).ToTask(giveUp.Token);
            }
            catch (OperationCanceledException)
            {
                Out.Bad("await Observable.Interval(...) → never completes (gave up after 500 ms)");
            }
        }
        else
        {
            var price = await PriceTicks().FirstAsync();
            Out.Good($"await prices.FirstAsync() → {price} after {Out.ElapsedMs - started} ms");

            var none = await Observable.Empty<Quote>().LastOrDefaultAsync();
            Out.Good($"await Observable.Empty().LastOrDefaultAsync() → {none?.ToString() ?? "null"} (the empty case is explicit)");

            var tick = await Observable.Interval(TimeSpan.FromMilliseconds(100)).FirstAsync();
            Out.Good($"await Observable.Interval(...).FirstAsync() → {tick}");
        }
    }

    static async Task RetryAsync(Variant variant)
    {
        Out.Section("Retry(3) on a provider that fails twice");
        var provider = new FakeQuoteProvider { Latency = TimeSpan.FromMilliseconds(100), Verbose = true };
        provider.FailNext(2);

        IObservable<Quote> quotes = variant == Variant.Broken
            ? provider.GetQuoteAsync("MSFT").ToObservable()                         // the call starts here, once
            : Observable.FromAsync(ct => provider.GetQuoteAsync("MSFT", ct));        // a new call per subscription

        var attempts = 0;
        try
        {
            var quote = await quotes
                .Do(_ => { }, ex => Out.Log($"attempt {++attempts} failed: {ex.Message}"))
                .Retry(3);
            Out.Good($"Got {quote.Symbol} = {quote.Price}");
        }
        catch (ProviderException ex)
        {
            Out.Bad($"Gave up: {ex.Message}");
        }

        Out.Metric("Failed attempts seen by Retry", attempts);
        Out.Metric("Provider calls", provider.Calls, provider.Calls > 1);
    }

    static async Task CancellationAsync(Variant variant)
    {
        Out.Section("Unsubscribe 50 ms after subscribing");
        var provider = new FakeQuoteProvider { Latency = TimeSpan.FromMilliseconds(200), Verbose = true };

        IObservable<Quote> quote = variant == Variant.Broken
            ? provider.GetQuoteAsync("MSFT").ToObservable()
            : Observable.FromAsync(ct => provider.GetQuoteAsync("MSFT", ct));

        var subscription = quote.Subscribe(q => Out.Log($"subscriber got {q.Price}"));
        await Task.Delay(50);
        subscription.Dispose();
        Out.Log("subscription disposed");
        await Task.Delay(300);

        Out.Metric("Provider calls cancelled", provider.Cancelled, provider.Cancelled == 1);
        if (variant == Variant.Broken)
            Out.Bad("Nobody is listening, but the call ran to completion anyway");
        else
            Out.Good("Disposing the subscription cancelled the CancellationToken");
    }
}
