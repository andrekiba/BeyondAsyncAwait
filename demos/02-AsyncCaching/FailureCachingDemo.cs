using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo02;

/// <summary>
/// 2.2 — Trap 1 of a cached Task: a faulted Task is a perfectly valid value, so the cache keeps it.
/// The provider fails once and recovers, the cache keeps failing forever.
/// Fixed: AsyncLazy swaps in a fresh load when the shared one fails.
/// </summary>
sealed class FailureCachingDemo : Demo
{
    public override string Id => "2.2";
    public override string Name => "failure-caching";
    public override string Title => "Trap 1: the failure is cached forever";

    public override async Task RunAsync(Variant variant)
    {
        var provider = new FakeQuoteProvider { Latency = TimeSpan.FromMilliseconds(100) };
        provider.FailNext(1);

        Func<Task<Quote>> getQuote;
        if (variant == Variant.Broken)
        {
            var lazy = new Lazy<Task<Quote>>(() => provider.GetQuoteAsync("MSFT"));
            getQuote = () => lazy.Value;
        }
        else
        {
            var lazy = new AsyncLazy<Quote>(() => provider.GetQuoteAsync("MSFT"));
            getQuote = () => lazy.GetValueAsync();
        }

        var failures = 0;
        for (var request = 1; request <= 5; request++)
        {
            try
            {
                var quote = await getQuote();
                Out.Good($"request {request}: MSFT = {quote.Price}");
            }
            catch (ProviderException ex)
            {
                failures++;
                Out.Bad($"request {request}: {ex.Message}");
            }

            await Task.Delay(50);
        }

        Out.Section("Results");
        Out.Metric("Failed requests", $"{failures}/5", failures <= 1);
        Out.Metric("Provider calls", provider.Calls);
        Out.Metric("Provider failures", provider.Failed);
    }
}
