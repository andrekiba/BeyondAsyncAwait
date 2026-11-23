namespace BeyondAsyncAwait.Shared;

public sealed record Quote(string Symbol, decimal Price, DateTimeOffset Timestamp);

public sealed class ProviderException(string message) : Exception(message);

/// <summary>
/// The slow, unreliable external quote provider of QuoteHub.
/// Every demo puts its counters on screen: calls, max concurrency, cancellations, failures.
/// </summary>
public sealed class FakeQuoteProvider
{
    readonly Lock randomGate = new();
    readonly Random random = new(42);
    int calls, inFlight, maxInFlight, cancelled, failed, failNext;

    public TimeSpan Latency { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Probability (0..1) that a call fails with <see cref="ProviderException"/>.</summary>
    public double FailureRate { get; set; }

    /// <summary>Log every call, completion and cancellation with timestamp and thread.</summary>
    public bool Verbose { get; set; }

    public int Calls => Volatile.Read(ref calls);
    public int InFlight => Volatile.Read(ref inFlight);
    public int MaxInFlight => Volatile.Read(ref maxInFlight);
    public int Cancelled => Volatile.Read(ref cancelled);
    public int Failed => Volatile.Read(ref failed);

    /// <summary>Deterministically fail the next <paramref name="count"/> calls.</summary>
    public void FailNext(int count) => Volatile.Write(ref failNext, count);

    public void Reset()
    {
        calls = inFlight = maxInFlight = cancelled = failed = failNext = 0;
    }

    public async Task<Quote> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref calls);
        InterlockedEx.Max(ref maxInFlight, Interlocked.Increment(ref inFlight));
        if (Verbose)
            Out.Log($"provider → {symbol} (call #{call})");

        try
        {
            await Task.Delay(Latency, cancellationToken);

            if (ShouldFail())
            {
                Interlocked.Increment(ref failed);
                if (Verbose)
                    Out.Log($"provider ✘ {symbol} (call #{call}) failed");
                throw new ProviderException($"Provider failed for {symbol} (call #{call})");
            }

            var quote = new Quote(symbol, NextPrice(symbol), DateTimeOffset.UtcNow);
            if (Verbose)
                Out.Log($"provider ← {symbol} = {quote.Price} (call #{call})");
            return quote;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref cancelled);
            if (Verbose)
                Out.Log($"provider ⊘ {symbol} (call #{call}) cancelled");
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    bool ShouldFail()
    {
        while (true)
        {
            var pending = Volatile.Read(ref failNext);
            if (pending <= 0)
                break;
            if (Interlocked.CompareExchange(ref failNext, pending - 1, pending) == pending)
                return true;
        }

        if (FailureRate <= 0)
            return false;
        lock (randomGate)
            return random.NextDouble() < FailureRate;
    }

    decimal NextPrice(string symbol)
    {
        var basePrice = 100 + StableHash(symbol) % 900;
        lock (randomGate)
            return Math.Round(basePrice + (decimal)(random.NextDouble() * 2 - 1), 2);
    }

    // string.GetHashCode is randomized per process: prices must be the same at every run.
    static int StableHash(string text)
    {
        var hash = 17;
        foreach (var c in text)
            hash = hash * 31 + c;
        return hash & 0x7FFFFFFF;
    }
}
