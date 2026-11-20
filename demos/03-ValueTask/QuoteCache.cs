using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo03;

/// <summary>
/// A price cache in front of the provider: the hit path completes synchronously.
/// Same code, two return types.
/// </summary>
sealed class QuoteCache(FakeQuoteProvider provider)
{
    readonly Dictionary<string, decimal> prices = [];

    public async Task<decimal> GetPriceAsTaskAsync(string symbol)
    {
        if (prices.TryGetValue(symbol, out var price))
            return price;

        var quote = await provider.GetQuoteAsync(symbol);
        return prices[symbol] = quote.Price;
    }

    public async ValueTask<decimal> GetPriceAsValueTaskAsync(string symbol)
    {
        if (prices.TryGetValue(symbol, out var price))
            return price;

        var quote = await provider.GetQuoteAsync(symbol);
        return prices[symbol] = quote.Price;
    }
}
