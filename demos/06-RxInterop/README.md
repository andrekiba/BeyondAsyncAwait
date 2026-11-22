# Demo 6 — Many values, push: Task ↔ Rx.NET, and AsyncRx

**English** · [Italiano](README-it.md)

[Demo 5](../05-Channels/README.md) dealt with many values in *pull* mode: the consumer reads from a `Channel` when it is ready.
Here the values arrive by *push*: the producer decides when, and the consumer gets called.

`Task<T>` and `IObservable<T>` are the two extremes: one value you ask for and then wait on, versus a sequence of values that is delivered to you.
In QuoteHub they live side by side: calls to the quote provider are `Task`s, published prices are an Rx stream, the configuration reloads itself on a timer, and consumers sometimes want a single price (`await`), sometimes an `IAsyncEnumerable`.
Rx converts in both directions, but every conversion has precise semantics, and scenarios 6.1–6.4 show where they surprise you: which value `await observable` returns, what `Retry` repeats, what disposing cancels, where the backlog piles up.

The fundamental limit of classic Rx is that `IObserver<T>.OnNext` is `void`: an observer doing async work cannot tell the producer to wait, and `Subscribe` and `Dispose` are synchronous too.
**AsyncRx.NET** (`System.Reactive.Async`) defines `IAsyncObservable<T>` and `IAsyncObserver<T>`: `OnNextAsync` returns a `ValueTask` that the producer awaits (backpressure by construction), `SubscribeAsync` can be awaited, and the subscription is closed with `DisposeAsync`.
It is experimental, and the only build on NuGet is a 2023 alpha, but scenarios 6.5–6.7 show the problem it solves, and 6.6 shows where it stops.

## How to run

```bash
dotnet run --project demos/06-RxInterop -- 6.1                     # broken, Enter, fixed
dotnet run --project demos/06-RxInterop -- asyncrx-bridge --fixed
dotnet run --project demos/06-RxInterop -- all --no-pause          # all scenarios, both variants
```

In the IDE there is the `menu` profile plus one profile per scenario, from `6.1 task-observable` to `6.7 asyncrx-lifecycle` ([launchSettings.json](Properties/launchSettings.json)).
From the terminal you pick the same profile with `--launch-profile "6.3 rx-to-channel"`.

The packages ([Demo06.RxInterop.csproj](Demo06.RxInterop.csproj), versions in [Directory.Packages.props](../../Directory.Packages.props)):

| Package | Version | What the Demo uses |
|---|---|---|
| `System.Reactive` | 7.0.0 | Rx.NET: `Observable`, `Subject<T>`, `ToObservable`/`ToTask`, `FromAsync`, `Concat`/`Merge`/`Switch`, `Replay`/`RefCount`/`AutoConnect` |
| `System.Interactive.Async` | 7.0.1 | Ix: `IObservable<T>.ToAsyncEnumerable()` (6.3) |
| `System.Reactive.Async` | 6.0.0-alpha.18 | AsyncRx.NET, experimental: `IAsyncObservable<T>`, `AsyncObservable.Create`/`Interval`, `ToAsyncObservable()` (6.5–6.7) |

The output below comes from a Debug run on .NET 11.0.0.
Milliseconds, completion order and timing-dependent counts change from one run to the next: these are the values from that run.

## ★ 6.1 `task-observable` — await, Retry and cancellation between Task and IObservable

Three conversions between the two worlds, in [TaskObservableDemo.cs](TaskObservableDemo.cs), whose semantics do not match those of a `Task`.

**`await` on an observable.** `IObservable<T>` is awaitable, but the `await` waits for the sequence to *complete* and returns the *last* value.

```csharp
static IObservable<decimal> PriceTicks() =>
    Observable.Interval(TimeSpan.FromMilliseconds(100)).Take(3).Select(i => 100m + i);

var price = await PriceTicks();
// …
await Observable.Empty<Quote>();
// …
await Observable.Interval(TimeSpan.FromMilliseconds(100)).ToTask(giveUp.Token);
```

```text
✘ await prices → 102 after 330 ms: the LAST value, only when the stream completes
✘ await Observable.Empty() → InvalidOperationException: Sequence contains no elements.
✘ await Observable.Interval(...) → never completes (gave up after 500 ms)
```

Whoever writes `await prices` means "the current price" and gets the third and last one, after 330 ms.
An empty sequence has no value to return, hence `InvalidOperationException`; an infinite sequence never completes, and the demo needs a token to stop waiting for it after 500 ms.

The fix is to say which value you want:

```csharp
var price = await PriceTicks().FirstAsync();
// …
var none = await Observable.Empty<Quote>().LastOrDefaultAsync();
// …
var tick = await Observable.Interval(TimeSpan.FromMilliseconds(100)).FirstAsync();
```

```text
✔ await prices.FirstAsync() → 100 after 111 ms
✔ await Observable.Empty().LastOrDefaultAsync() → null (the empty case is explicit)
✔ await Observable.Interval(...).FirstAsync() → 0
```

**`Retry` on a Task.** The provider fails the first two calls, and the pipeline retries with `Retry(3)`.

```csharp
IObservable<Quote> quotes = variant == Variant.Broken
    ? provider.GetQuoteAsync("MSFT").ToObservable()                         // the call starts here, once
    : Observable.FromAsync(ct => provider.GetQuoteAsync("MSFT", ct));        // a new call per subscription

var quote = await quotes
    .Do(_ => { }, ex => Out.Log($"attempt {++attempts} failed: {ex.Message}"))
    .Retry(3);
```

```text
   856 ms [t  7] provider → MSFT (call #1)
   958 ms [t  7] provider ✘ MSFT (call #1) failed
   959 ms [t  7] attempt 1 failed: Provider failed for MSFT (call #1)
   962 ms [t  7] attempt 2 failed: Provider failed for MSFT (call #1)
   962 ms [t  7] attempt 3 failed: Provider failed for MSFT (call #1)
✘ Gave up: Provider failed for MSFT (call #1)
  Failed attempts seen by Retry    3
  Provider calls                   1
```

`GetQuoteAsync` is called while the observable is being built, before any subscription: `ToObservable()` wraps a `Task` that is already running.
`Retry` resubscribes, but to the same `Task`, which has already failed: three attempts in 3 ms, always the error from `call #1`, a single call to the provider.

With `Observable.FromAsync` the factory runs on every subscription, and every attempt is a real call:

```text
   223 ms [t  7] provider → MSFT (call #1)
   331 ms [t  7] provider ✘ MSFT (call #1) failed
   332 ms [t  7] attempt 1 failed: Provider failed for MSFT (call #1)
   332 ms [t  7] provider → MSFT (call #2)
   442 ms [t  7] provider ✘ MSFT (call #2) failed
   442 ms [t  7] attempt 2 failed: Provider failed for MSFT (call #2)
   442 ms [t  7] provider → MSFT (call #3)
   552 ms [t  7] provider ← MSFT = 281.34 (call #3)
✔ Got MSFT = 281.34
  Failed attempts seen by Retry    2
  Provider calls                   3
```

**Cancellation.** The same two sources, a 200 ms provider, and the subscription disposed after 50 ms.

```csharp
var subscription = quote.Subscribe(q => Out.Log($"subscriber got {q.Price}"));
await Task.Delay(50);
subscription.Dispose();
```

With `ToObservable()` disposing stops the result from being delivered, but the call carries on to the end:

```text
   964 ms [t  7] provider → MSFT (call #1)
  1020 ms [t  7] subscription disposed
  1177 ms [t  7] provider ← MSFT = 281.34 (call #1)
  Provider calls cancelled         0
✘ Nobody is listening, but the call ran to completion anyway
```

With `FromAsync(ct => …)` Rx cancels the token when the subscription is disposed:

```text
   554 ms [t  7] provider → MSFT (call #1)
   614 ms [t  7] subscription disposed
   615 ms [t  8] provider ⊘ MSFT (call #1) cancelled
  Provider calls cancelled         1
✔ Disposing the subscription cancelled the CancellationToken
```

The takeaway: `ToObservable()` adapts a `Task` that already exists, a single, shared result.
An *operation* you want to retry or cancel is brought into Rx with `Observable.FromAsync(ct => …)`: lazy, one call per subscription, token tied to the dispose.

## ★ 6.2 `async-in-pipeline` — async calls inside an Rx pipeline

Eight price requests, one every 50 ms; each one makes an async call that takes between 110 and 400 ms, with a different duration per request, so the completion order is not the arrival order.
A probe in [AsyncInPipelineDemo.cs](AsyncInPipelineDemo.cs) counts the calls in flight, the completion order and the cancellations.

**`Subscribe(async …)`.** The first approach that comes to mind:

```csharp
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
```

```text
   267 ms [t  8] #1 AAPL = 193.34
   329 ms [t  8] #3 AMZN = 336.28
   409 ms [t  7] OnCompleted
   472 ms [t  7] #2 NVDA = 473.25
   487 ms [t  8] #0 MSFT = 281.05
   …
   675 ms [t  8] #7 ORCL = 301.03
  Max in flight                    6
  OnCompleted at                   409 ms
  Last result at                   675 ms
✘ The stream said "completed" while the work was still running
```

`OnNext` is `void`, so the lambda is `async void`: Rx calls it, gets control back at the first `await` and moves on to the next element.
No limit (6 calls in flight), no ordering, and `OnCompleted` arrives when the *source* completes, at 409 ms, while the last result arrives at 675 ms.

**`SelectMany(async …)`.** It waits for the results, and its `OnCompleted` arrives after the last one:

```csharp
var quotes = await Requests().SelectMany(async seq => await probe.PriceAsync(seq)).ToList();
```

```text
── SelectMany(async x => ...)
  Results                          8
  Max in flight                    6
  Completion order                 #1 #3 #2 #0 #6 #5 #4 #7

── SelectMany(async x => ...), unsubscribed after 200 ms
  1582 ms [t  8] subscription disposed
  Calls cancelled                  0
  Calls completed after Dispose    4
✘ Nobody listens any more, the calls run to the end anyway
```

But it is an unbounded merge: everything in parallel, results in completion order.
And the lambda gets no token: after the dispose, 4 calls run to the end anyway.

**Fixed.** Each request becomes an `Observable.FromAsync`, which does not start until someone subscribes.
The result is an `IObservable<IObservable<Quote>>`, and the policy is chosen by the operator that flattens it:

```csharp
await ComposeAsync("Select(FromAsync).Concat() — one at a time, in order", s => s.Concat());
await ComposeAsync("Select(FromAsync).Merge(4) — at most 4 in flight", s => s.Merge(4));
await ComposeAsync("Select(FromAsync).Switch() — latest wins, stale calls cancelled", s => s.Switch());

// … inside ComposeAsync
var calls = Requests().Select(seq => Observable.FromAsync(ct => probe.PriceAsync(seq, ct)));
var quotes = await compose(calls).ToList();
```

| Operator | Max in flight | Completion order | Cancelled | Time |
|---|---|---|---|---|
| `Concat()` | 1 | #0 #1 #2 #3 #4 #5 #6 #7 | 0 | 2047 ms |
| `Merge(4)` | 4 | #1 #3 #2 #0 #5 #6 #4 #7 | 0 | 755 ms |
| `Switch()` | 2 | #7 (a single result) | 7 | 680 ms |

- `Concat()` subscribes to the next call only when the previous one has completed: in order, one at a time, and the total time is roughly the sum of the durations.
- `Merge(4)` keeps at most 4 calls open: the provider never sees more than 4 concurrent requests.
- `Switch()` unsubscribes from the previous request whenever a new one arrives, and the dispose cancels the `FromAsync` token. Requests arrive every 50 ms and no call takes less than 110 ms, so only the last one gets through:

```text
  2912 ms [t  5] ⊘ #0 MSFT cancelled
  2960 ms [t  8] ⊘ #1 AAPL cancelled
  …
  3214 ms [t  9] ⊘ #6 TSLA cancelled
  Results                          1
  Max in flight                    2
  Completion order                 #7
  Calls cancelled                  7
```

The takeaway: async work in a pipeline should be expressed as an observable (`FromAsync`), not as a callback.
That way the operator decides concurrency, ordering and cancellation: in order, at most *n*, or only the latest.

## ★ 6.3 `rx-to-channel` — Rx → IAsyncEnumerable: who holds the backlog

A hot `Subject<Quote>` (market data that arrives from a socket callback and cannot be slowed down) produces 500,000 ticks in about 2 seconds.
The consumer is an `await foreach` that spends 10 ms on each element.
Push versus pull, in [RxToChannelDemo.cs](RxToChannelDemo.cs): the difference has to be queued or dropped somewhere.

```csharp
// Hot market data: pushed from a socket callback, it cannot be slowed down.
var ticks = new Subject<Quote>();
// …
stream = ticks.ToAsyncEnumerable();
// …
await foreach (var quote in stream.WithCancellation(stop.Token))
{
    Volatile.Write(ref lastAgeMs, (DateTimeOffset.UtcNow - quote.Timestamp).TotalMilliseconds);
    Interlocked.Increment(ref consumed);
    await Task.Delay(ConsumerWork, stop.Token);
}
```

```text
ticks.ToAsyncEnumerable()  — AsyncEnumerableEx, System.Interactive.Async 7
   424 ms [t  5] produced 100,000 │ consumed  26 │ dropped       0 │ backlog  99,974 │ heap   +7.1 MB │ age of price in use   388 ms
   …
  2021 ms [t  8] produced 500,000 │ consumed 128 │ dropped       0 │ backlog 499,873 │ heap  +34.5 MB │ age of price in use 1,979 ms
  Backlog when we stopped          499,872
  Dropped (counted)                0
  Age of the last price processed  1,979 ms
✘ Unbounded hidden queue: memory grows and the consumer processes stale prices
```

`ToAsyncEnumerable()` cannot slow down the `Subject`, because `OnNext` is `void`: everything the consumer has not asked for yet ends up in an unbounded internal queue.
The backlog grows with production (almost 500,000 elements), the heap by 34.5 MB in 2 seconds, and the consumer works on a price that is almost 2 seconds old.
No exception and no counter reports it.

The fix puts a bounded `Channel` at the boundary, with a capacity and a policy of your choosing:

```csharp
var channel = Channel.CreateBounded<Quote>(
    new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
    itemDropped: _ => Interlocked.Increment(ref dropped));

bridge = ticks.Subscribe(
    quote => channel.Writer.TryWrite(quote),
    ex => channel.Writer.TryComplete(ex),
    () => channel.Writer.TryComplete());
stream = channel.Reader.ReadAllAsync();
```

```text
ticks → Channel.CreateBounded(8, DropOldest) → ReadAllAsync()
   394 ms [t  9] produced 100,000 │ consumed  26 │ dropped  99,967 │ backlog       8 │ heap   +0.0 MB │ age of price in use    14 ms
   …
  1953 ms [t  5] produced 500,000 │ consumed 126 │ dropped 499,867 │ backlog       7 │ heap   +0.0 MB │ age of price in use    14 ms
  Backlog when we stopped          7
  Dropped (counted)                499,867
  Age of the last price processed  14 ms
✔ Bounded buffer: the drop policy is explicit, memory is flat, prices are fresh
```

The consumer processes roughly the same number of elements (126 versus 128): its speed does not change.
What changes is *what* it processes: a price from 14 ms ago instead of 2 seconds ago, with flat memory and the drops counted by `itemDropped`.
Errors and completion of the source are passed on to the channel with `TryComplete`.

The takeaway: going from push to pull means holding the backlog somewhere, and it had better be a buffer whose size you chose.
As in [Demo 5](../05-Channels/README.md), the policy is a business decision: for a price, the latest one is what counts, hence `DropOldest`.

## 6.4 `refreshing-cache` — a self-refreshing async cache: Replay(1) + RefCount vs AutoConnect

The problem from [Demo 2](../02-AsyncCaching/README.md) (one value, many consumers) in Rx, in [RefreshingCacheDemo.cs](RefreshingCacheDemo.cs).
A configuration is loaded asynchronously (100 ms) and a timer reloads it every 500 ms; 12 consumers, one every 100 ms, read it with `await config.FirstAsync()`.

```csharp
var replayed = Observable.Defer(() =>
    {
        Interlocked.Increment(ref connections);
        return Observable.Timer(TimeSpan.Zero, RefreshPeriod);
    })
    .Select(_ => Observable.FromAsync(LoadAsync))
    .Switch()
    .Replay(1);

IDisposable? connection = null;
var config = variant == Variant.Broken
    ? replayed.RefCount()
    : replayed.AutoConnect(1, c => connection = c);
// …
var current = await config.FirstAsync();
```

Timer → one load per tick (`FromAsync`) → `Switch` (a load still running at the next tick is cancelled) → `Replay(1)`, which holds the latest configuration and hands it straight to whoever subscribes.
`Defer` counts how many times the source gets connected.

With `RefCount()`:

```text
12 consumers, one every 100 ms; the timer refreshes every 500 ms
   122 ms [t  9]   config load #1 completed
   122 ms [t  9] call  1 → config v1 (119 ms)
   232 ms [t  9] call  2 → config v1 (0 ms)
   …
  1324 ms [t  5] call 12 → config v1 (0 ms)
  Connections to the source        12
  Loads the timer asked for        3
  Loads completed                  1
  Loads cancelled                  0
✘ RefCount: every await reconnects, the replayed v1 answers first, the timer never ticks — stale forever
```

Every `await` on an observable is a new subscription, and `FirstAsync()` only lasts until the first value.
`RefCount` connects the source when the first subscriber arrives and disconnects it when none is left: here that happens on every call, 12 connections.
From the second call on, `Replay(1)` already has v1 and answers immediately (0 ms): the consumer leaves, the connection drops, and the new timer never gets the chance to fire.
The result: one load out of three, none cancelled, and the cache stays at v1 forever, with no errors.

With `AutoConnect(1, …)`:

```text
   109 ms [t  7]   config load #1 completed
   110 ms [t  7] call  1 → config v1 (110 ms)
   …
   545 ms [t  7] call  5 → config v1 (0 ms)
   623 ms [t  7]   config load #2 completed
   654 ms [t  7] call  6 → config v2 (0 ms)
   …
  1121 ms [t  7]   config load #3 completed
  1199 ms [t  7] call 11 → config v3 (0 ms)
  1309 ms [t  7] call 12 → config v3 (0 ms)
  Connections to the source        1
  Loads the timer asked for        3
  Loads completed                  3
  Loads cancelled                  0
✔ AutoConnect: one connection, refreshed by the timer, consumers get the replayed value
```

`AutoConnect` connects on the first subscriber and never disconnects on its own: the timer runs, loads arrive every 500 ms, and consumers read the latest value in 0 ms.
The price is that the connection must be closed by hand: the demo receives it from the `c => connection = c` callback and calls `connection?.Dispose()` at the end.

The takeaway: `Replay(1).RefCount()` is fine for subscribers that stick around; a cache read with `await FirstAsync()` only has momentary subscriptions, and `RefCount` switches the refresh off.

## ★ 6.5 `asyncrx-backpressure` — AsyncRx: the producer waits for the consumer

In 6.2 the underlying problem was that `OnNext` is `void`: an async observer cannot make the source wait.
[AsyncRxBackpressureDemo.cs](AsyncRxBackpressureDemo.cs) puts the same consumer (a 500 ms provider call) on a 100 ms timer, once with classic Rx and once with AsyncRx.

```csharp
async ValueTask HandleAsync(long tick)
{
    lock (starts)
        starts.Add(Out.ElapsedMs);
    Out.Log($"start #{tick} (in flight: {provider.InFlight + 1})");
    await provider.GetQuoteAsync("MSFT");
    Out.Log($"end   #{tick}");
    Interlocked.Exchange(ref lastEnd, Out.ElapsedMs);
}
```

Classic Rx, with an `async void` lambda:

```csharp
Observable.Interval(Period).Take(Ticks).Subscribe(
    async tick => await HandleAsync(tick),
    () =>
    {
        Out.Log("OnCompleted");
        completed.SetResult(Out.ElapsedMs);
    });
```

```text
Observable.Interval(100 ms).Take(5).Subscribe(async x => ...)
   110 ms [t  7] start #0 (in flight: 1)
   217 ms [t  7] start #1 (in flight: 2)
   310 ms [t  7] start #2 (in flight: 3)
   420 ms [t  7] start #3 (in flight: 4)
   514 ms [t  7] start #4 (in flight: 5)
   514 ms [t  7] OnCompleted
   623 ms [t  5] end   #0
   …
  1028 ms [t  7] end   #4
  Max calls in flight              5
  Time between ticks (avg)         101 ms
  OnCompleted at                   515 ms
  Last work finished at            1028 ms
✘ No backpressure and a lying OnCompleted: close the DB connection now and the handlers break
```

The timer keeps its own pace, the calls pile up (5 in flight), and `OnCompleted` arrives at 515 ms while the work finishes at 1028.
Anyone who closes a resource on `OnCompleted` pulls it out from under the handlers that are still running.

AsyncRx, with the same handler:

```csharp
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
```

```text
AsyncObservable.Interval(100 ms).Take(5).SubscribeAsync(async x => ...)
   119 ms [t  9] start #0 (in flight: 1)
   630 ms [t  9] end   #0
   740 ms [t  9] start #1 (in flight: 1)
  1254 ms [t  9] end   #1
  1363 ms [t  7] start #2 (in flight: 1)
   …
  2610 ms [t  5] start #4 (in flight: 1)
  3111 ms [t  5] end   #4
  3112 ms [t  5] OnCompletedAsync
  Max calls in flight              1
  Time between ticks (avg)         623 ms
  OnCompleted at                   3112 ms
  Last work finished at            3111 ms
✔ Backpressure by await: the timer slowed down to the consumer's pace, OnCompleted is honest
```

`HandleAsync` is passed as is: it is the observer's `OnNextAsync`, and it returns a `ValueTask` that the source awaits before emitting the next value.
One call in flight at a time, and the next tick arrives about 100 ms after the previous work ends (630 → 740, 1254 → 1363): the timer has adjusted to the consumer's pace, 623 ms on average between ticks instead of 101.
`OnCompletedAsync` arrives at 3112 ms, after the last piece of work (3111): now it tells the truth.

The takeaway: in AsyncRx the async observer is part of the protocol, not a fire-and-forget callback, and backpressure is an `await`.
It works because the source, an AsyncRx timer, can wait: 6.6 shows what happens when it cannot.

## ★ 6.6 `asyncrx-bridge` — AsyncRx: the sync → async boundary

AsyncRx does not make backpressure disappear, it moves it to the boundary.
In QuoteHub the prices come from a classic Rx `Subject<Quote>`: `OnNext` is `void`, and the producer cannot await anything.
In [AsyncRxBridgeDemo.cs](AsyncRxBridgeDemo.cs) the producer pushes 5 prices, one every 50 ms, to an AsyncRx observer that spends 300 ms on each price.

```csharp
var ticks = new Subject<Quote>();
// …
source = ticks.ToAsyncObservable();
// …
await using var subscription = await source.SubscribeAsync(HandleAsync, OnError, OnCompleted);
await Task.Delay(100); // ToAsyncObservable subscribes on the task pool

for (var i = 0; i < Pushes; i++)
{
    var before = Out.ElapsedMs;
    ticks.OnNext(new Quote("MSFT", 100m + i, DateTimeOffset.UtcNow));
    var elapsed = Out.ElapsedMs - before;
    // …
    await Task.Delay(PushInterval);
}
```

```text
ticks.ToAsyncObservable().SubscribeAsync(async q => ...)
   438 ms [t  9]   handled MSFT 100
   439 ms [t  5] producer: OnNext(100) returned after 316 ms
   812 ms [t  9]   handled MSFT 101
   812 ms [t  5] producer: OnNext(101) returned after 311 ms
   …
  1931 ms [t  9]   handled MSFT 104
  1931 ms [t  5] producer: OnNext(104) returned after 312 ms
  Producer blocked in OnNext (total) 1559 ms
  Quotes handled                   5
  Quotes dropped (counted)         0
✘ The hot producer's thread was blocked: sync-over-async inside the bridge. From the AsyncRx source:
  // REVIEW: Add a way to parameterize blocking behavior (e.g. blocking, fire-and-forget, async chaining).
```

Each `OnNext` returns only once the handler has finished, after about 310 ms: the producer's thread (`t 5`) sits idle while the handler runs on another one (`t 9`), 1559 ms in total for 5 prices.
In the [AsyncRx.NET source](https://github.com/dotnet/reactive/blob/asyncrxnet-v6.0.0-alpha.18/AsyncRx.NET/System.Reactive.Async/Linq/Operators/ToAsyncObservable.cs), `ToAsyncObservable()` subscribes to the classic source with an adapter `IObserver<T>` whose `OnNext` does `OnNextAsync(value).GetAwaiter().GetResult()`: sync-over-async.
Above the adapter sits the `REVIEW` comment that the demo prints: the behavior at the boundary is still an open question in the library.

The fix is the same as in Demo 5 and 6.3: an explicit boundary, a bounded `Channel` with `DropOldest`, and on the other side an `IAsyncObservable` that reads from the channel and awaits the observer before moving on to the next element.

```csharp
var channel = Channel.CreateBounded<Quote>(
    new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest },
    itemDropped: q =>
    {
        Interlocked.Increment(ref dropped);
        Out.Log($"  dropped {q.Symbol} {q.Price}");
    });
bridge = ticks.Subscribe(q => channel.Writer.TryWrite(q), ex => channel.Writer.TryComplete(ex), () => channel.Writer.TryComplete());
source = FromChannel(channel.Reader);

// …
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
            // … catch: cancellation ends quietly, other errors go to OnErrorAsync
        });

        return ValueTask.FromResult(AsyncDisposable.Create(async () =>
        {
            await stop.CancelAsync();
            await pump;
            stop.Dispose();
        }));
    });
```

```text
ticks → Channel.CreateBounded(2, DropOldest) → IAsyncObservable paced by its observer
   117 ms [t  5] producer: OnNext(100) returned after 0 ms
   180 ms [t  8] producer: OnNext(101) returned after 0 ms
   244 ms [t  8] producer: OnNext(102) returned after 0 ms
   308 ms [t  8]   dropped MSFT 101
   308 ms [t  8] producer: OnNext(103) returned after 1 ms
   370 ms [t  8]   dropped MSFT 102
   370 ms [t  8] producer: OnNext(104) returned after 0 ms
   417 ms [t  8]   handled MSFT 100
   730 ms [t  8]   handled MSFT 103
  1045 ms [t  8]   handled MSFT 104
  Producer blocked in OnNext (total) 1 ms
  Quotes handled                   3
  Quotes dropped (counted)         2
✔ The producer never waits: the boundary has a size and a drop policy you chose
```

The producer writes with `TryWrite` and never waits: 1 ms in total.
Price 100 goes straight to the handler, 101 and 102 wait in the 2-slot buffer, then 103 and 104 push them out: 100, 103 and 104 are processed, 101 and 102 are dropped, counted and logged.
AsyncRx's backpressure operates between the channel and the observer; the mismatch with the producer is absorbed by the channel, with the policy you chose.

The takeaway: inside the async world AsyncRx gives you backpressure with an `await`, but at the boundary with a source that cannot wait, someone has to decide where the excess goes.
An explicit, bounded, counted buffer is better than a blocked producer thread.

## 6.7 `asyncrx-lifecycle` — AsyncRx: async subscribe and dispose

A subscription that owns a resource with async setup and teardown, in [AsyncRxLifecycleDemo.cs](AsyncRxLifecycleDemo.cs): connect to a broker (150 ms) and, at the end, commit the offsets and close (200 ms).
The app subscribes, reads for 300 ms, disposes and "shuts down".

In classic Rx, `Observable.Create` accepts an async lambda, but the contract stays synchronous:

```csharp
var quotes = Observable.Create<Quote>(async (observer, cancellationToken) =>
{
    await broker.ConnectAsync(cancellationToken);
    _ = PumpAsync(q => { observer.OnNext(q); return ValueTask.CompletedTask; }, cancellationToken);
    return () => _ = broker.CommitAndCloseAsync(); // Dispose is void: fire-and-forget
});

var subscription = quotes.Subscribe(q => Out.Log($"  quote {q.Price}"));
// …
await Task.Delay(300);
subscription.Dispose();
```

```text
     4 ms [t  8] broker: connecting...
     5 ms [t  8] Subscribe returned
   166 ms [t  8] broker: connected
   166 ms [t  8]   quote 100
   227 ms [t  8]   quote 101
   291 ms [t  8]   quote 102
   307 ms [t  8] broker: committing offsets...
   307 ms [t  8] Dispose returned — the app shuts down now
  Connected when Subscribe returned False
  Offsets committed when Dispose returned False
   509 ms [t  8] broker: offsets committed, connection closed
✘ Dispose can't be awaited: the commit outlives the subscription (or is lost on exit)
```

`Subscribe` returns at 5 ms, but the connection is only up at 166 ms.
`IDisposable.Dispose` is `void`, so the commit can only be fire-and-forget: `Dispose` returns at 307 ms, and the offsets are committed at 509.
The demo waits another 400 ms to show it; an app that really shuts down loses the commit.

AsyncRx puts setup and teardown into the type:

```csharp
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
// …
await subscription.DisposeAsync();
```

```text
     0 ms [t  8] broker: connecting...
   156 ms [t  8] broker: connected
   157 ms [t  8]   quote 100
   158 ms [t  8] SubscribeAsync returned
   218 ms [t  8]   quote 101
   281 ms [t  8]   quote 102
   312 ms [t  5] broker: committing offsets...
   515 ms [t  5] broker: offsets committed, connection closed
   515 ms [t  5] DisposeAsync returned — the app shuts down now
  Connected when Subscribe returned True
  Offsets committed when Dispose returned True
✔ Setup and teardown are awaitable: the subscription's lifetime is the resource's lifetime
```

`SubscribeAsync` returns once the connection is established (158 ms, connected at 156).
The teardown is an `IAsyncDisposable`: `DisposeAsync` stops the pump, waits for it, commits, and returns only at the very end (515 ms).
One detail: `quote 100` arrives before `SubscribeAsync` returns, because the pump starts inside the subscribe function; the observer must be ready before the `await` completes.

The takeaway: when a subscription owns a resource with an async lifetime, in AsyncRx `await SubscribeAsync` means "ready" and `await DisposeAsync` means "closed".
In classic Rx the type does not allow it: `IDisposable.Dispose` is `void`.

## What to know

- Where the conversions live in Rx/Ix 7: `IObservable<T>.ToAsyncEnumerable()` is in `System.Interactive.Async` (`System.Linq.AsyncEnumerableEx`), not in `System.Reactive`, nor in the BCL's `System.Linq.AsyncEnumerable`.
- With `runtime-async=on` the only state machines left in the assembly are those of the `async void` lambdas, that is, the lambdas passed to `Subscribe` in 6.2 and 6.5: Runtime Async does not cover `async void` (see [Demo 0](../00-RuntimeAsync/README.md)).
- `await observable` returns the *last* value on completion, throws `InvalidOperationException` if the sequence is empty, and never returns if it is infinite. Say which value you want: `FirstAsync()`, `LastOrDefaultAsync()`. Every `await` is a new subscription, and with `RefCount` that means a new connection (6.4).
- `task.ToObservable()` adapts a `Task` that is already running: one shared result, `Retry` sees the same error again, disposing cancels nothing. `Observable.FromAsync(ct => …)` is lazy: one call per subscription, and the token is cancelled on dispose. Wherever you retry or cancel, use `FromAsync`.
- Async work in a pipeline: no `Subscribe(async …)` (async void: no limit, an early `OnCompleted`, exceptions outside Rx). `SelectMany(async …)` is an unbounded, unordered merge; the `SelectMany((x, ct) => …)` overload adds cancellation, not a limit. With `Select(x => Observable.FromAsync(ct => …))` you choose the policy: `Concat()` in order, one at a time; `Merge(n)` at most *n*; `Switch()` only the latest, with the others cancelled.
- `Replay(1).RefCount()` disconnects when the subscriber count drops to zero, and reconnects on the next one: right for subscribers that stick around, wrong for a cache read with `await FirstAsync()`. The `RefCount` overload with `disconnectDelay` postpones the disconnection, it does not prevent it. `AutoConnect` stays connected until you close the connection it hands you in the `Action<IDisposable>` callback: if you do not keep it, it stays open forever.
- From push to pull (toward `IAsyncEnumerable` or toward AsyncRx) the backlog always lives somewhere: `ToAsyncEnumerable()` puts it in a hidden, unbounded queue, and `ToAsyncObservable()` on a hot source offloads it onto the producer's thread, blocking it. A bounded `Channel` puts it in a buffer whose size and policy you chose: `DropOldest` when the latest value is what counts, `Wait` when the producer can slow down (see [Demo 5](../05-Channels/README.md)).
- AsyncRx.NET is experimental: the only build on NuGet is `6.0.0-alpha.18` (2023), which works with Rx 7. It brings an async protocol (backpressure via `await`, awaitable subscribe and dispose); the boundary with classic Rx (6.6) remains your problem.

## References

- [dotnet/reactive — Rx.NET, Ix.NET and AsyncRx.NET](https://github.com/dotnet/reactive)
- [Introduction to Rx.NET — Creating Observable Sequences: from `Task` to `IObservable`, `ToObservable` and `FromAsync`](https://introtorx.com/chapters/creating-observable-sequences.html)
- [Introduction to Rx.NET — Leaving Rx's World: `await` on an `IObservable<T>`, `ToTask`](https://introtorx.com/chapters/leaving-reactive-extensions-world.html)
- [Introduction to Rx.NET — Publishing Operators: `Replay`, `RefCount`, `AutoConnect`](https://introtorx.com/chapters/publishing-operators.html)
