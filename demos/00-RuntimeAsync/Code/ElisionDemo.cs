using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.RuntimeAsyncDemos;

/// <summary>
/// 0.3 — "Async elision" (returning the Task instead of awaiting it) used to save a state machine.
/// With runtime async the saving shrinks (see the benchmarks), the semantic traps stay:
/// here the using scope ends before the operation does.
/// </summary>
sealed class ElisionDemo : Demo
{
    public override string Id => "0.3";
    public override string Name => "elision";
    public override string Title => "Async elision: the using scope ends too early";

    public override async Task RunAsync(Variant variant)
    {
        try
        {
            var quote = variant == Variant.Broken
                ? await GetQuoteElided("MSFT")
                : await GetQuoteAwaited("MSFT");
            Out.Good($"Got {quote.Symbol} = {quote.Price}");
        }
        catch (ObjectDisposedException ex)
        {
            Out.Bad($"{ex.GetType().Name}: the session was disposed while the call was still running");
        }
    }

    // Broken: no async/await, the Task escapes the using scope.
    static Task<Quote> GetQuoteElided(string symbol)
    {
        using var session = new QuoteSession();
        return session.GetQuoteAsync(symbol);
    }

    // Fixed: the method stays alive (and the session open) until the call completes.
    static async Task<Quote> GetQuoteAwaited(string symbol)
    {
        using var session = new QuoteSession();
        return await session.GetQuoteAsync(symbol);
    }

    sealed class QuoteSession : IDisposable
    {
        readonly FakeQuoteProvider _provider = new() { Latency = TimeSpan.FromMilliseconds(100) };
        bool _disposed;

        public async Task<Quote> GetQuoteAsync(string symbol)
        {
            Out.Log("session: request sent");
            var quote = await _provider.GetQuoteAsync(symbol);
            Out.Log("session: response received");
            ObjectDisposedException.ThrowIf(_disposed, this);
            return quote;
        }

        public void Dispose()
        {
            _disposed = true;
            Out.Log("session: disposed");
        }
    }
}
