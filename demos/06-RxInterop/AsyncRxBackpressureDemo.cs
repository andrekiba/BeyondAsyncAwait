using System.Reactive.Linq;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo06;

/// <summary>
/// 6.5 — AsyncRx.NET (IAsyncObservable&lt;T&gt;, experimental): the same consumer, two types.
/// Classic Rx: OnNext is void, the async handler is fire-and-forget — 5 calls in flight and OnCompleted
/// before the work ends. AsyncRx: the producer awaits OnNextAsync — one call at a time, the producer is
/// paced by the consumer, and OnCompleted arrives when the work is really done.
/// </summary>
sealed class AsyncRxBackpressureDemo : Demo
{
    const int Ticks = 5;
    static readonly TimeSpan Period = TimeSpan.FromMilliseconds(100);
    static readonly TimeSpan Work = TimeSpan.FromMilliseconds(500);

    public override string Id => "6.5";
    public override string Name => "asyncrx-backpressure";
    public override string Title => "AsyncRx: the producer awaits the consumer";

    public override async Task RunAsync(Variant variant)
    {
        var provider = new FakeQuoteProvider { Latency = Work };
        var starts = new List<long>();
        long lastEnd = 0;
        var completed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask HandleAsync(long tick)
        {
            lock (starts)
                starts.Add(Out.ElapsedMs);
            Out.Log($"start #{tick} (in flight: {provider.InFlight + 1})");
            await provider.GetQuoteAsync("MSFT");
            Out.Log($"end   #{tick}");
            Interlocked.Exchange(ref lastEnd, Out.ElapsedMs);
        }

        if (variant == Variant.Broken)
        {
            Out.Info($"Observable.Interval({Period.TotalMilliseconds} ms).Take({Ticks}).Subscribe(async x => ...)");
            Observable.Interval(Period).Take(Ticks).Subscribe(
                async tick => await HandleAsync(tick),
                () =>
                {
                    Out.Log("OnCompleted");
                    completed.SetResult(Out.ElapsedMs);
                });
        }
        else
        {
            Out.Info($"AsyncObservable.Interval({Period.TotalMilliseconds} ms).Take({Ticks}).SubscribeAsync(async x => ...)");
            await AsyncObservable.Interval(Period).Take(Ticks).SubscribeAsync(
                HandleAsync,
                ex =>
                {
                    completed.TrySetException(ex);
                    return ValueTask.CompletedTask;
                },
                () =>
                {
                    Out.Log("OnCompletedAsync");
                    completed.TrySetResult(Out.ElapsedMs);
                    return ValueTask.CompletedTask;
                });
        }

        var completedAt = await completed.Task;
        while (provider.Calls < Ticks || provider.InFlight > 0)
            await Task.Delay(5);

        var gaps = starts.Zip(starts.Skip(1), (a, b) => b - a).ToList();
        Out.Metric("Max calls in flight", provider.MaxInFlight, provider.MaxInFlight == 1);
        Out.Metric("Time between ticks (avg)", $"{(gaps.Count > 0 ? gaps.Average() : 0):N0} ms");
        Out.Metric("OnCompleted at", $"{completedAt} ms", completedAt >= Interlocked.Read(ref lastEnd));
        Out.Metric("Last work finished at", $"{Interlocked.Read(ref lastEnd)} ms");

        if (variant == Variant.Broken)
            Out.Bad("No backpressure and a lying OnCompleted: close the DB connection now and the handlers break");
        else
            Out.Good("Backpressure by await: the timer slowed down to the consumer's pace, OnCompleted is honest");
    }
}
