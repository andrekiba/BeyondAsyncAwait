namespace BeyondAsyncAwait.Shared;

public sealed record Quote(string Symbol, decimal Price, DateTimeOffset Timestamp);

public sealed class ProviderException(string message) : Exception(message);

/// <summary>
/// The slow, unreliable external quote provider of QuoteHub.
/// Every demo puts its counters on screen: calls, max concurrency, cancellations, failures.
/// </summary>
public sealed class FakeQuoteProvider
{
    readonly Lock _randomGate = new();
    readonly Random _random = new(42);
    int _calls, _inFlight, _maxInFlight, _cancelled, _failed, _failNext;

    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Probability (0..1) that a call fails with <see cref="ProviderException"/>.</summary>
    public double FailureRate { get; set; }

    /// <summary>Log every call, completion and cancellation with timestamp and thread.</summary>
    public bool Verbose { get; set; }

    public int Calls => Volatile.Read(ref _calls);
    public int InFlight => Volatile.Read(ref _inFlight);
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);
    public int Cancelled => Volatile.Read(ref _cancelled);
    public int Failed => Volatile.Read(ref _failed);

    /// <summary>Deterministically fail the next <paramref name="count"/> calls.</summary>
    public void FailNext(int count) => Volatile.Write(ref _failNext, count);

    public void Reset()
    {
        _calls = _inFlight = _maxInFlight = _cancelled = _failed = _failNext = 0;
    }

    public async Task<Quote> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);
        InterlockedEx.Max(ref _maxInFlight, Interlocked.Increment(ref _inFlight));
        if (Verbose)
            Out.Log($"provider → {symbol} (call #{call})");

        try
        {
            await Task.Delay(Latency, cancellationToken);

            if (ShouldFail())
            {
                Interlocked.Increment(ref _failed);
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
            Interlocked.Increment(ref _cancelled);
            if (Verbose)
                Out.Log($"provider ⊘ {symbol} (call #{call}) cancelled");
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    bool ShouldFail()
    {
        while (true)
        {
            var pending = Volatile.Read(ref _failNext);
            if (pending <= 0)
                break;
            if (Interlocked.CompareExchange(ref _failNext, pending - 1, pending) == pending)
                return true;
        }

        if (FailureRate <= 0)
            return false;
        lock (_randomGate)
            return _random.NextDouble() < FailureRate;
    }

    decimal NextPrice(string symbol)
    {
        var basePrice = 100 + StableHash(symbol) % 900;
        lock (_randomGate)
            return Math.Round(basePrice + (decimal)(_random.NextDouble() * 2 - 1), 2);
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
