using System.Collections.Concurrent;
using System.Reactive.Linq;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Act06;

/// <summary>
/// 6.2 — One async provider call per event. Subscribe(async ...) is async void: overlapping work,
/// OnCompleted before the work is done, exceptions outside Rx. SelectMany(async ...) runs everything
/// at once, out of order, and nothing is cancelled. Wrapping the call in Observable.FromAsync gives an
/// IObservable&lt;IObservable&lt;T&gt;&gt; and the operator picks the policy: Concat, Merge(n) or Switch.
/// </summary>
sealed class AsyncInPipelineDemo : Demo
{
    // Work before the provider call varies per request, so completion order differs from arrival order.
    static readonly int[] Jitter = [300, 40, 200, 10, 250, 90, 30, 150];
    static readonly string[] Symbols = ["MSFT", "AAPL", "NVDA", "AMZN", "GOOG", "META", "TSLA", "ORCL"];

    public override string Id => "6.2";
    public override string Name => "async-in-pipeline";
    public override string Title => "Async calls inside an Rx pipeline";

    /// <summary>8 price requests, one every 50 ms; each takes 110–400 ms.</summary>
    static IObservable<int> Requests() =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).Take(Jitter.Length).Select(i => (int)i);

    public override async Task RunAsync(Variant variant)
    {
        if (variant == Variant.Broken)
        {
            await SubscribeAsyncVoidAsync();
            await SelectManyAsync();
        }
        else
        {
            await ComposeAsync("Select(FromAsync).Concat() — one at a time, in order", s => s.Concat());
            await ComposeAsync("Select(FromAsync).Merge(4) — at most 4 in flight", s => s.Merge(4));
            await ComposeAsync("Select(FromAsync).Switch() — latest wins, stale calls cancelled", s => s.Switch());
        }
    }

    static async Task SubscribeAsyncVoidAsync()
    {
        Out.Section("Subscribe(async x => ...) — an async void lambda");
        var probe = new Probe();
        var completed = new TaskCompletionSource<long>();

        Requests().Subscribe(
            async seq =>
            {
                // async void: Rx doesn't wait for this. If it threw, the exception would go to the
                // thread pool and terminate the process — OnError would never see it.
                var quote = await probe.PriceAsync(seq);
                Out.Log($"#{seq} {quote.Symbol} = {quote.Price}");
            },
            () =>
            {
                Out.Log("OnCompleted");
                completed.SetResult(Out.ElapsedMs);
            });

        var completedAt = await completed.Task;
        while (probe.Completed < Jitter.Length)
            await Task.Delay(5);

        Out.Metric("Max in flight", probe.MaxInFlight, false);
        Out.Metric("OnCompleted at", $"{completedAt} ms", false);
        Out.Metric("Last result at", $"{probe.LastCompletedAt} ms");
        Out.Bad("The stream said \"completed\" while the work was still running");
    }

    static async Task SelectManyAsync()
    {
        Out.Section("SelectMany(async x => ...)");
        var probe = new Probe();
        var quotes = await Requests().SelectMany(async seq => await probe.PriceAsync(seq)).ToList();

        Out.Metric("Results", quotes.Count);
        Out.Metric("Max in flight", probe.MaxInFlight, false);
        Out.Metric("Completion order", probe.Order, false);

        Out.Section("SelectMany(async x => ...), unsubscribed after 200 ms");
        probe = new Probe();
        var subscription = Requests().SelectMany(async seq => await probe.PriceAsync(seq)).Subscribe(_ => { });
        await Task.Delay(200);
        subscription.Dispose();
        var completedAtDispose = probe.Completed;
        Out.Log("subscription disposed");
        await Task.Delay(500);

        Out.Metric("Calls cancelled", probe.Cancelled, false);
        Out.Metric("Calls completed after Dispose", probe.Completed - completedAtDispose, false);
        Out.Bad("Nobody listens any more, the calls run to the end anyway");
    }

    static async Task ComposeAsync(string title, Func<IObservable<IObservable<Quote>>, IObservable<Quote>> compose)
    {
        Out.Section(title);
        var probe = new Probe();
        var started = Out.ElapsedMs;

        var calls = Requests().Select(seq => Observable.FromAsync(ct => probe.PriceAsync(seq, ct)));
        var quotes = await compose(calls).ToList();

        Out.Metric("Results", quotes.Count);
        Out.Metric("Max in flight", probe.MaxInFlight);
        Out.Metric("Completion order", probe.Order);
        Out.Metric("Calls cancelled", probe.Cancelled);
        Out.Metric("Elapsed", $"{Out.ElapsedMs - started} ms");
    }

    sealed class Probe
    {
        readonly FakeQuoteProvider _provider = new() { Latency = TimeSpan.FromMilliseconds(100) };
        readonly ConcurrentQueue<int> _completed = new();
        int _inFlight, _maxInFlight, _cancelled;
        long _lastCompletedAt;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);
        public int Cancelled => Volatile.Read(ref _cancelled);
        public int Completed => _completed.Count;
        public long LastCompletedAt => Interlocked.Read(ref _lastCompletedAt);
        public string Order => string.Join(" ", _completed.Select(seq => $"#{seq}"));

        public async Task<Quote> PriceAsync(int seq, CancellationToken cancellationToken = default)
        {
            InterlockedEx.Max(ref _maxInFlight, Interlocked.Increment(ref _inFlight));
            try
            {
                await Task.Delay(Jitter[seq], cancellationToken);
                var quote = await _provider.GetQuoteAsync(Symbols[seq], cancellationToken);
                _completed.Enqueue(seq);
                Interlocked.Exchange(ref _lastCompletedAt, Out.ElapsedMs);
                return quote;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                Out.Log($"⊘ #{seq} {Symbols[seq]} cancelled");
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
