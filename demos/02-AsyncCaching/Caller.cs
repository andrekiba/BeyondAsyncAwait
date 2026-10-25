using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Act02;

static class Caller
{
    /// <summary>
    /// A client asking for MSFT-like data; with <paramref name="cancelAfter"/> it disconnects after that delay.
    /// Returns true if it got a quote.
    /// </summary>
    public static async Task<bool> CallAsync(string name, Func<CancellationToken, Task<Quote>> getQuote, TimeSpan? cancelAfter = null)
    {
        using var cts = cancelAfter is { } delay ? new CancellationTokenSource(delay) : new CancellationTokenSource();
        try
        {
            var quote = await getQuote(cts.Token);
            Out.Good($"{name}: {quote.Symbol} = {quote.Price}");
            return true;
        }
        catch (OperationCanceledException)
        {
            if (cancelAfter is null)
                Out.Bad($"{name}: cancelled — but it never asked to!");
            else
                Out.Warn($"{name}: cancelled (client disconnected)");
            return false;
        }
    }
}
