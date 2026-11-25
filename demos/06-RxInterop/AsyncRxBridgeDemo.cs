using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Channels;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo06;

/// <summary>
/// 6.6 — AsyncRx doesn't make backpressure disappear, it moves it to the boundary.
/// A hot classic source (Subject.OnNext is void) can't await anything: ToAsyncObservable() blocks the
/// producer's thread until the async observer is done (sync-over-async). The fix is the same as Demo 5:
/// an explicit bounded Channel at the boundary, where you choose the policy (here: DropOldest).
/// </summary>
sealed class AsyncRxBridgeDemo : Demo
{
    const int Pushes = 5;
    const int Capacity = 2;
    static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(50);
    static readonly TimeSpan Work = TimeSpan.FromMilliseconds(300);

    public override string Id => "6.6";
    public override string Name => "asyncrx-bridge";
    public override string Title => "AsyncRx: the sync → async boundary";

    public override async Task RunAsync(Variant variant)
    {
        var provider = new FakeQuoteProvider { Latency = Work };
        var ticks = new Subject<Quote>();
        var handled = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long dropped = 0;

        async ValueTask HandleAsync(Quote quote)
        {
            await provider.GetQuoteAsync(quote.Symbol);
            Interlocked.Increment(ref handled);
            Out.Log($"  handled {quote.Symbol} {quote.Price}");
        }

        ValueTask OnCompleted()
        {
            done.TrySetResult();
            return ValueTask.CompletedTask;
        }

        ValueTask OnError(Exception ex)
        {
            done.TrySetException(ex);
            return ValueTask.CompletedTask;
        }

        IAsyncObservable<Quote> source;
        IDisposable bridge = Disposable.Empty;

        if (variant == Variant.Broken)
        {
            Out.Info("ticks.ToAsyncObservable().SubscribeAsync(async q => ...)");
            source = ticks.ToAsyncObservable();
        }
        else
        {
            Out.Info($"ticks → Channel.CreateBounded({Capacity}, DropOldest) → IAsyncObservable paced by its observer");
            var channel = Channel.CreateBounded<Quote>(
                new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest },
                itemDropped: q =>
                {
                    Interlocked.Increment(ref dropped);
                    Out.Log($"  dropped {q.Symbol} {q.Price}");
                });
            bridge = ticks.Subscribe(q => channel.Writer.TryWrite(q), ex => channel.Writer.TryComplete(ex), () => channel.Writer.TryComplete());
            source = FromChannel(channel.Reader);
        }

        await using var subscription = await source.SubscribeAsync(HandleAsync, OnError, OnCompleted);
        await Task.Delay(100); // ToAsyncObservable subscribes on the task pool

        var blocked = new List<long>();
        for (var i = 0; i < Pushes; i++)
        {
            var before = Out.ElapsedMs;
            ticks.OnNext(new Quote("MSFT", 100m + i, DateTimeOffset.UtcNow));
            var elapsed = Out.ElapsedMs - before;
            blocked.Add(elapsed);
            Out.Log($"producer: OnNext({100 + i}) returned after {elapsed} ms");
            await Task.Delay(PushInterval);
        }

        ticks.OnCompleted();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bridge.Dispose();

        Out.Metric("Producer blocked in OnNext (total)", $"{blocked.Sum()} ms", blocked.Sum() < 50);
        Out.Metric("Quotes handled", handled);
        Out.Metric("Quotes dropped (counted)", Interlocked.Read(ref dropped));

        if (variant == Variant.Broken)
        {
            Out.Bad("The hot producer's thread was blocked: sync-over-async inside the bridge. From the AsyncRx source:");
            Out.Dim("  // REVIEW: Add a way to parameterize blocking behavior (e.g. blocking, fire-and-forget, async chaining).");
        }
        else
        {
            Out.Good("The producer never waits: the boundary has a size and a drop policy you chose");
        }
    }

    /// <summary>An IAsyncObservable that pulls from a channel and awaits its observer before the next item.</summary>
    static IAsyncObservable<T> FromChannel<T>(ChannelReader<T> reader) =>
        AsyncObservable.Create<T>(observer =>
        {
            var stop = new CancellationTokenSource();
            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in reader.ReadAllAsync(stop.Token))
                        await observer.OnNextAsync(item);
                    await observer.OnCompletedAsync();
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    await observer.OnErrorAsync(ex);
                }
            });

            return ValueTask.FromResult(AsyncDisposable.Create(async () =>
            {
                await stop.CancelAsync();
                await pump;
                stop.Dispose();
            }));
        });
}
