using System.Reactive.Disposables;
using System.Reactive.Linq;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Act06;

/// <summary>
/// 6.7 — Subscriptions with async setup and teardown: connect to a broker, commit offsets when done.
/// Classic Rx: Subscribe returns before the connection exists, Dispose() is synchronous, so the
/// commit is fire-and-forget (if the process exits now, the offsets are lost). AsyncRx: SubscribeAsync
/// returns once connected, DisposeAsync returns once the offsets are committed.
/// </summary>
sealed class AsyncRxLifecycleDemo : Demo
{
    public override string Id => "6.7";
    public override string Name => "asyncrx-lifecycle";
    public override string Title => "AsyncRx: async subscribe and async dispose";

    public override async Task RunAsync(Variant variant)
    {
        var broker = new FakeBroker();
        long subscribeReturnedAt, disposeReturnedAt;

        if (variant == Variant.Broken)
        {
            var quotes = Observable.Create<Quote>(async (observer, cancellationToken) =>
            {
                await broker.ConnectAsync(cancellationToken);
                _ = PumpAsync(q => { observer.OnNext(q); return ValueTask.CompletedTask; }, cancellationToken);
                return () => _ = broker.CommitAndCloseAsync(); // Dispose is void: fire-and-forget
            });

            var subscription = quotes.Subscribe(q => Out.Log($"  quote {q.Price}"));
            subscribeReturnedAt = Out.ElapsedMs;
            Out.Log("Subscribe returned");

            await Task.Delay(300);
            subscription.Dispose();
            disposeReturnedAt = Out.ElapsedMs;
            Out.Log("Dispose returned — the app shuts down now");
        }
        else
        {
            var quotes = AsyncObservable.Create<Quote>(async observer =>
            {
                await broker.ConnectAsync();
                var stop = new CancellationTokenSource();
                var pump = PumpAsync(observer.OnNextAsync, stop.Token);
                return AsyncDisposable.Create(async () =>
                {
                    await stop.CancelAsync();
                    await pump;
                    await broker.CommitAndCloseAsync();
                    stop.Dispose();
                });
            });

            var subscription = await quotes.SubscribeAsync(q =>
            {
                Out.Log($"  quote {q.Price}");
                return ValueTask.CompletedTask;
            });
            subscribeReturnedAt = Out.ElapsedMs;
            Out.Log("SubscribeAsync returned");

            await Task.Delay(300 - broker.ConnectTime);
            await subscription.DisposeAsync();
            disposeReturnedAt = Out.ElapsedMs;
            Out.Log("DisposeAsync returned — the app shuts down now");
        }

        var connectedBeforeSubscribe = broker.ConnectedAt >= 0 && broker.ConnectedAt <= subscribeReturnedAt;
        var committedBeforeDispose = broker.CommittedAt >= 0 && broker.CommittedAt <= disposeReturnedAt;
        Out.Metric("Connected when Subscribe returned", connectedBeforeSubscribe, connectedBeforeSubscribe);
        Out.Metric("Offsets committed when Dispose returned", committedBeforeDispose, committedBeforeDispose);

        await Task.Delay(400); // let the fire-and-forget commit finish, so the log shows when it happened
        if (variant == Variant.Broken)
            Out.Bad("Dispose can't be awaited: the commit outlives the subscription (or is lost on exit)");
        else
            Out.Good("Setup and teardown are awaitable: the subscription's lifetime is the resource's lifetime");
    }

    static async Task PumpAsync(Func<Quote, ValueTask> onNext, CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; !cancellationToken.IsCancellationRequested; i++)
            {
                await onNext(new Quote("MSFT", 100m + i, DateTimeOffset.UtcNow));
                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    sealed class FakeBroker
    {
        public int ConnectTime => 150;
        public long ConnectedAt { get; private set; } = -1;
        public long CommittedAt { get; private set; } = -1;

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            Out.Log("broker: connecting...");
            await Task.Delay(ConnectTime, cancellationToken);
            ConnectedAt = Out.ElapsedMs;
            Out.Log("broker: connected");
        }

        public async Task CommitAndCloseAsync()
        {
            Out.Log("broker: committing offsets...");
            await Task.Delay(200);
            CommittedAt = Out.ElapsedMs;
            Out.Log("broker: offsets committed, connection closed");
        }
    }
}
