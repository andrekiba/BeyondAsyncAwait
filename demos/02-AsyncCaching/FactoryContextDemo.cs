using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo02;

/// <summary>
/// 2.4 — Trap 3 of a cached Task: Lazy runs the factory on the first caller's thread, inside its
/// ExecutionContext. Every AsyncLocal of that request (correlation id, tenant, Activity) flows into
/// the shared load, which serves every other request too.
/// Fixed: AsyncLazy starts the load on the thread pool with ExecutionContext flow suppressed.
/// </summary>
sealed class FactoryContextDemo : Demo
{
    static readonly AsyncLocal<string?> CorrelationId = new();

    public override string Id => "2.4";
    public override string Name => "factory-context";
    public override string Title => "Trap 3: the shared load runs in the first caller's context";

    public override async Task RunAsync(Variant variant)
    {
        var provider = new FakeQuoteProvider();
        Func<Task<Quote>> getQuote;

        if (variant == Variant.Broken)
        {
            var lazy = new Lazy<Task<Quote>>(() => LoadAsync(provider));
            getQuote = () => lazy.Value;
        }
        else
        {
            var lazy = new AsyncLazy<Quote>(() => LoadAsync(provider));
            getQuote = () => lazy.GetValueAsync();
        }

        await Task.WhenAll(RequestAsync("req-A", getQuote), RequestAsync("req-B", getQuote));
    }

    static async Task RequestAsync(string correlationId, Func<Task<Quote>> getQuote)
    {
        CorrelationId.Value = correlationId;
        Out.Log($"{correlationId}: request started");
        var quote = await getQuote();
        Out.Log($"{correlationId}: MSFT = {quote.Price}");
    }

    static async Task<Quote> LoadAsync(FakeQuoteProvider provider)
    {
        Report("shared load started");
        var quote = await provider.GetQuoteAsync("MSFT");
        Report("shared load completed");
        return quote;
    }

    static void Report(string message)
    {
        var owner = CorrelationId.Value;
        if (owner is null)
            Out.Good($"{message} — correlation id: (none), it belongs to no request [t{Environment.CurrentManagedThreadId}]");
        else
            Out.Bad($"{message} — correlation id: {owner} [t{Environment.CurrentManagedThreadId}]");
    }
}
