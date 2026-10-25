using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.RuntimeAsyncDemos;

/// <summary>The method we look at in IL: one await, one result.</summary>
public static class QuotePricing
{
    public static async Task<decimal> GetGrossPriceAsync(FakeQuoteProvider provider, string symbol)
    {
        var quote = await provider.GetQuoteAsync(symbol);
        return quote.Price * 1.22m;
    }
}
