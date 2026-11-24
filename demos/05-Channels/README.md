# Demo 5 — Many values, pull: Channels

**English** · [Italiano](README-it.md)

Up to Demo 4, every asynchronous operation produces a single value: a `Task<T>`, a `TaskCompletionSource`, an `AsyncLazy<T>`, a `ValueTask<T>`.
Here QuoteHub receives a stream: price ticks that arrive faster than the database can save them, and messages that flow through several processing stages.
A `Channel<T>` is the asynchronous queue between whoever produces and whoever consumes, and it's *pull*-based: the consumer reads when it's ready, and when the producer runs faster, the channel decides what happens.
Demo 6 takes the same journey *push*-based, with Rx.NET.

The API takes a few minutes to learn: `WriteAsync`, `ReadAllAsync`, `Complete`.
What matters in production are the decisions around it: how far the queue may grow and what to do when it's full (5.1), who closes a channel and how an error travels between the stages of a pipeline (5.2).
Getting them wrong produces no compile errors, and the wrong code looks reasonable in code review: what you get is a queue that eats memory, a pipeline that says "completed" while losing messages, or one that hangs forever when an external service goes down.
Scenario 5.3 adds two .NET 9 tools for handling many results in the right order.

## How to run

```bash
dotnet run --project demos/05-Channels                        # interactive menu
dotnet run --project demos/05-Channels -- 5.1                 # broken, Enter, fixed
dotnet run --project demos/05-Channels -- pipeline --fixed    # fixed variant only
dotnet run --project demos/05-Channels -- all --no-pause      # all scenarios, no pauses
```

In an IDE there's the `menu` profile plus one per scenario: `5.1 backpressure`, `5.2 pipeline`, `5.3 when-each`.
Scenario 5.3 has no variants and runs only once.

The numbers below come from a Debug run on .NET 11.0.0.
The timing-dependent ones (queued items, memory, data age, milliseconds) change from run to run: these are the values from that run.

## ★ 5.1 `backpressure` — when the producer runs faster

In [BackpressureDemo.cs](BackpressureDemo.cs) the producer is the market feed: 100 ticks every 50 ms, each with its own 1 KB raw message.
The consumer persists them: 10 ticks, then a 50 ms round-trip to the database. On paper, 2000 ticks per second against 200.
Each scenario lasts 2.5 seconds, and every 500 ms it prints the ticks produced, persisted, queued (`Reader.Count`) and dropped, the age of the last persisted tick, and the memory held, measured after a full garbage collection.

The producer is the same in every variant, and knows nothing about the channel in front of it:

```csharp
while (true)
{
    for (var i = 0; i < TicksPerBurst; i++)
    {
        var symbol = Symbols[sequence % Symbols.Length];
        await writer.WriteAsync(new Tick(++sequence, symbol, Stopwatch.GetTimestamp(), new byte[1024]), cancellationToken);
        stats.OnProduced();
    }

    await Task.Delay(Period, cancellationToken);
}
```

**Broken.** The channel is unbounded:

```csharp
var channel = mode switch
{
    Mode.Unbounded => Channel.CreateUnbounded<Tick>(),
    // …
};
```

```text
── Channel.CreateUnbounded — the producer never waits
   509 ms [t  5] produced   900  persisted   90  queued   810  dropped     0  staleness   502 ms  memory +0.9 MB
  1023 ms [t  9] produced  1700  persisted  170  queued  1530  dropped     0  staleness   936 ms  memory +1.7 MB
  1538 ms [t  8] produced  2500  persisted  250  queued  2250  dropped     0  staleness  1375 ms  memory +2.4 MB
  2054 ms [t  5] produced  3300  persisted  330  queued  2970  dropped     0  staleness  1812 ms  memory +3.2 MB
  2566 ms [t  9] produced  4065  persisted  410  queued  3655  dropped     0  staleness  2248 ms  memory +4.0 MB
  Producer rate                    1626 ticks/s
  Consumer rate                    164 ticks/s
  …
✘ The queue grows without limit: memory and latency climb until the process dies.
```

An unbounded channel always has room, so `WriteAsync` completes immediately and the producer never waits.
In this run the producer writes 1626 ticks per second and the consumer persists 164: the difference becomes queue, about 700 more ticks every half second.
Every queued tick holds on to its KB, and memory climbs in lockstep: +4.0 MB after 2.5 seconds.
And since the channel is FIFO, the consumer persists older and older ticks: the last price saved is 2.2 seconds old.
Nothing stops the growth: in a longer run, memory and latency keep climbing.

**Fixed.** The same producer and the same consumer, with a bounded channel of 100 items, in two modes:

```csharp
var channel = mode switch
{
    // …
    Mode.Wait => Channel.CreateBounded<Tick>(new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait }),
    // itemDropped: the only way to know (count, log, dispose) what DropOldest threw away
    _ => Channel.CreateBounded<Tick>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest },
        itemDropped: _ => stats.OnDropped())
};
```

```text
── Bounded(100), FullMode.Wait — orders: nothing may be lost, so the producer slows down
   512 ms [t  9] produced   190  persisted   90  queued   100  dropped     0  staleness   492 ms  memory +0.1 MB
  …
  2542 ms [t  5] produced   510  persisted  410  queued   100  dropped     0  staleness   625 ms  memory +0.1 MB
  Producer rate                    204 ticks/s
  Consumer rate                    164 ticks/s
! Nothing lost, memory flat — but the producer now runs at the consumer's speed: upstream must cope.

── Bounded(100), FullMode.DropOldest — prices: only the latest one matters
   514 ms [t  8] produced   900  persisted   90  queued   100  dropped   710  staleness    62 ms  memory +0.1 MB
  …
  2558 ms [t  5] produced  4100  persisted  410  queued   100  dropped  3590  staleness    62 ms  memory +0.1 MB
  Producer rate                    1640 ticks/s
  Dropped (itemDropped callback)   3590
✔ Producer at full speed, memory flat, the data persisted is fresh — the old ticks are gone, and counted.
Which FullMode is right is a business decision, not a technical one.
```

| After 2.5 s | Unbounded | Bounded(100) `Wait` | Bounded(100) `DropOldest` |
|---|---|---|---|
| Producer | 1626 ticks/s | **204 ticks/s** | 1640 ticks/s |
| Consumer | 164 ticks/s | 164 ticks/s | 164 ticks/s |
| Queued | 3655 | 100 | 100 |
| Dropped (`itemDropped`) | 0 | 0 | 3590 |
| Age of the last persisted tick | 2248 ms | 625 ms | **62 ms** |
| Memory held | +4.0 MB | +0.1 MB | +0.1 MB |

- **`Wait`**: when the channel is full, `WriteAsync` waits for a slot to free up. This is backpressure: the producer slows down to the consumer's pace (510 produced = 410 persisted + 100 queued), memory stays flat and nothing is lost. But every tick waits behind the other 100: at 164 ticks per second that's about 0.6 seconds, the 625 ms in the output. And the problem moves upstream: whatever feeds the producer must be able to wait, or buffer somewhere else. It's the right choice for orders.
- **`DropOldest`**: the write never waits. When the channel is full, it throws away the oldest item to make room for the new one. The producer stays at full speed, the queue never exceeds 100 items, and since it holds only the most recent ticks, the persisted price is at most a few tens of milliseconds old. The 3590 dropped ticks are counted by `itemDropped`: without the callback they would vanish without a trace. It's the right choice for prices, where a tick superseded by a newer one is worth nothing.

An unbounded channel isn't a choice: it postpones the problem for as long as there's memory.
With a bounded channel the question becomes *what to do when it's full*, and the answer depends on the data, not on the technology.

## ★ 5.2 `pipeline` — completion and errors between stages

In [PipelineDemo.cs](PipelineDemo.cs) the bus messages flow through four stages: source → parse → enrich (the price from `FakeQuoteProvider`, 20 ms) → persist (a 5 ms insert).
Each stage has 4 workers, and between one stage and the next there's a `Bounded(20)` channel.
The caller reads the receipts from the last channel with `await foreach` until the channel completes, with a 3-second timeout.

Each variant runs two scenarios:

1. 160 messages, including a poison message, #100 (`100|MSFT|ten`: the quantity isn't a number), then the normal end of input;
2. the quote provider goes down (`FailNext(int.MaxValue)`: every call fails).

**Broken.** Each stage fires off its workers and forgets about them:

```csharp
// Broken: looks reasonable in a code review, cancellation included.
static void StartStageBroken<TIn, TOut>(ChannelReader<TIn> input, ChannelWriter<TOut> output,
    Func<TIn, CancellationToken, Task<TOut>> transform, CancellationToken cancellationToken)
{
    for (var i = 0; i < Workers; i++)
    {
        _ = Task.Run(async () =>                         // fire-and-forget: a failing worker dies in silence
        {
            await foreach (var item in input.ReadAllAsync(cancellationToken))
                await output.WriteAsync(await transform(item, cancellationToken), cancellationToken);

            output.Complete();                           // the FIRST worker to finish closes the channel for all the others
        });
    }
}
```

The cancellation token is there, passed to `ReadAllAsync`, `WriteAsync` and the transform just as in the fixed variant, but nobody ever cancels it: what's missing is a stage that owns its workers.

Scenario 1, the poison message:

```text
── Scenario 1: a poison message, then a normal end of input
✔ Caller: pipeline completed in 1228 ms
  Produced                         160
  Persisted                        156
  Dead-lettered                    0
  Unaccounted for (lost)           4
  Exceptions nobody observed       9
```

The ✔ lies: the caller sees the pipeline complete, but the database holds 156 messages out of 160.
The poison message kills the parse worker that receives it: the exception stays inside its `Task.Run`, which nobody awaits, and the message vanishes without ending up anywhere.
Then, at the end of input, in each stage the first worker that finds the input empty and completed calls `output.Complete()` for everyone.
At that moment the others either still have an item in hand, and their `WriteAsync` on a closed channel throws `ChannelClosedException` (the item is lost), or they too reach `output.Complete()`, which on an already completed channel throws as well (`ChannelClosedException`, an `InvalidOperationException`).
In each stage only one worker out of four ends cleanly: three stages, 9 exceptions, which nobody sees until the GC finalizes the faulted tasks (`TaskScheduler.UnobservedTaskException`).
The 4 missing messages are #100 and the ones other workers had in hand when the channel closed underneath them.

Scenario 2, the provider down:

```text
── Scenario 2: the quote provider goes down
✘ Caller: no completion after 3015 ms — the pipeline hangs (and its blocked tasks leak)
  Produced                         48
  Persisted                        0
  Dead-lettered                    0
  Exceptions nobody observed       4
```

Each enrich worker picks up a trade, the provider throws `ProviderException` and the worker dies: 4 unobserved exceptions, and nobody will ever reach the `Complete()` of the channel to persist.
Persist and the caller wait on a channel that will never complete.
Upstream, nobody reads anymore: the channel to enrich fills up, the parse workers stay blocked on `WriteAsync`, then the source's channel fills up and the source blocks too.
The 48 messages produced are exactly as many as fit: 4 in the hands of the dead enrich workers, 20 in the channel to enrich, 4 in the blocked parse workers, 20 in the source's channel.
Without the caller's timeout the wait would never end, and the blocked tasks stay there.

**Fixed.** The stage owns its workers, awaits all of them, and completes its output only once:

```csharp
// Fixed: the stage owns its workers and completes its output exactly once.
static async Task RunStageAsync<TIn, TOut>(string name, ChannelReader<TIn> input, ChannelWriter<TOut> output,
    Func<TIn, CancellationToken, Task<TOut>> transform, PipelineContext context)
{
    var workers = Enumerable.Range(0, Workers).Select(_ => Task.Run(async () =>
    {
        try
        {
            await foreach (var item in input.ReadAllAsync(context.Token))
            {
                TOut result;
                try
                {
                    result = await transform(item, context.Token);
                }
                catch (PoisonMessageException ex)
                {
                    context.DeadLetters.Enqueue(ex.MessageId);   // item error: park it and keep going
                    continue;
                }

                await output.WriteAsync(result, context.Token);
            }
        }
        catch (Exception ex)
        {
            context.Fail(name, ex);   // stage error: cancel everything, upstream would block on a full channel forever
            throw;
        }
    })).ToArray();

    try
    {
        await Task.WhenAll(workers);
        output.TryComplete();                                     // only when ALL workers are done
    }
    catch (Exception ex)
    {
        output.TryComplete(context.FirstError ?? ex);             // downstream sees the root cause when it reads
    }
}
```

**Completion.** The stage completes its output after `Task.WhenAll(workers)`, that is, when all the workers have exited their `await foreach`.
This way completion flows down the pipeline one step at a time: the source completes the first channel after the last message; the parse workers exit when that channel is empty and completed, and only then does the stage complete the channel to enrich; and so on down to the last channel, where the caller's `await foreach` ends.
Completing a channel doesn't throw away what it contains: the reader first receives all the remaining items, then the end.

**Errors.** An error in a single item (`PoisonMessageException`) doesn't stop the stage: the message is dead-lettered and the worker moves on to the next one.
Any other exception is a stage error, and goes through `context.Fail`:

```csharp
public void Fail(string stage, Exception error)
{
    if (error is OperationCanceledException)
        return;

    if (Interlocked.CompareExchange(ref firstError, error, null) is null)
    {
        FailedStage = stage;
        cts.Cancel();
    }
}
```

**What happens to the other workers.** The first real error wins (`CompareExchange`) and cancels the token that every stage passes to `ReadAllAsync`, `WriteAsync` and the transform.
The other workers, upstream and downstream, exit through cancellation: even those blocked on a full channel, which in the broken variant, where nobody cancels the token, stayed there forever.
`Fail` ignores `OperationCanceledException`, so the recorded cause remains the `ProviderException`, not the cancellation that follows from it.
Once its workers have exited, each stage completes its output with `context.FirstError`: the error travels downstream with the channel, and the caller gets the original exception from its `await foreach`.

```text
── Scenario 1: a poison message, then a normal end of input
✔ Caller: pipeline completed in 1260 ms
  Stage tasks still running        0
  Produced                         160
  Persisted                        159
  Dead-lettered                    1 (100)
  Unaccounted for (lost)           0
  Exceptions nobody observed       0

── Scenario 2: the quote provider goes down
✔ Caller: pipeline failed in 27 ms with ProviderException: Provider failed for AAPL (call #4)
  root cause, from stage 'enrich': ProviderException: Provider failed for AAPL (call #4)
  Stage tasks still running        0
  Produced                         48
  Persisted                        0
  Dead-lettered                    0
  Exceptions nobody observed       0
```

In the first scenario #100 is dead-lettered and the other 159 reach the database, in about the same time as the broken variant.
In the second, the pipeline fails in 27 ms instead of hanging, and the caller knows what happened and in which stage.
In both, no task is left running and no exception is lost: every worker is awaited by its stage, and every stage turns its own error into the completion of its channel.

**Channel options.** Before the scenarios, the program prints:

```text
160 messages, poison message #100, 4 workers per stage, Bounded(20) between stages
Stage channels: SingleReader/SingleWriter = false (4 workers each side),
AllowSynchronousContinuations = false (the default: same trap as RunContinuationsAsynchronously in Demo 1).
```

```csharp
static Channel<T> Bounded<T>() => Channel.CreateBounded<T>(new BoundedChannelOptions(ChannelCapacity)
{
    SingleReader = false,
    SingleWriter = false,
    AllowSynchronousContinuations = false
});
```

These are all default values, spelled out because they're decisions.
`SingleReader` and `SingleWriter = true` are a promise, at most one reader (or one writer) at a time, in exchange for which the channel can use a specialized implementation: with four workers on each side, that promise can't be kept.
`AllowSynchronousContinuations = true` lets the writer run the waiting reader's continuation directly, inside its own `WriteAsync` or `TryWrite`, instead of queuing it.
It's the same trap as a `TaskCompletionSource` without `RunContinuationsAsynchronously` in [Demo 1](../01-TaskCompletionSource/README.md) (scenario 1.1): the consumer's code runs on the producer's thread, even while the producer holds a lock.
The demo keeps the default, `false`.

In a pipeline, completion is a protocol, not a call: the channel is closed by whoever owns *all* of its writers, only once.
Errors travel downstream with `Complete(ex)` and upstream with cancellation, and workers aren't started fire-and-forget.

## 5.3 `when-each` — in arrival order, or by priority

No broken variant: the scenario shows two tools that arrived in .NET 9, from [WhenEachDemo.cs](WhenEachDemo.cs).

**`Task.WhenEach`.** Five quote requests start together, each with its own latency: GOOG 50 ms, AAPL 100, AMZN 200, MSFT 300, NVDA 500.

```csharp
var quotes = await Task.WhenAll(StartQuotes());
foreach (var quote in quotes)
    Out.Log($"{quote.Symbol} {quote.Price}");

// …

await foreach (var task in Task.WhenEach(StartQuotes()))
{
    var quote = await task;
    Out.Log($"{quote.Symbol} {quote.Price}");
}
```

```text
── Task.WhenAll — every quote appears when the slowest one arrives
   516 ms [t  8] MSFT 281.34
   516 ms [t  8] AAPL 193.34
   516 ms [t  8] NVDA 474.34
   516 ms [t  8] AMZN 337.34
   516 ms [t  8] GOOG 657.34

── Task.WhenEach — each quote as soon as it is ready (completion order)
    61 ms [t  8] GOOG 657.34
   108 ms [t 17] AAPL 193.34
   203 ms [t  8] AMZN 337.34
   313 ms [t 17] MSFT 281.34
   500 ms [t 17] NVDA 474.34
```

With `WhenAll` every line appears at 516 ms, in request order: the GOOG quote, ready after 50 ms, waits for the NVDA one.
`Task.WhenEach` returns an `IAsyncEnumerable<Task<T>>` that yields the tasks as they complete, and each quote shows up as soon as it arrives.
It yields the task, not the value: `await task` gets the result, or rethrows the exception of that one call, and doesn't suspend because the task has already completed.
Before .NET 9, the same result required a loop that called `Task.WhenAny` over and over to get the next completed task.

**`Channel.CreateUnboundedPrioritized`.** A consumer processes one order every 60 ms; six normal orders arrive and, after 150 ms, two urgent ones.

```csharp
// The comparer decides the order: the smallest item is read first.
var channel = Channel.CreateUnboundedPrioritized(new UnboundedPrioritizedChannelOptions<Order>
{
    Comparer = Comparer<Order>.Create((a, b) => a.Priority != b.Priority
        ? a.Priority.CompareTo(b.Priority)
        : a.Id.CompareTo(b.Id))
});
// …
enum Priority { Urgent, Normal }
```

```text
── Channel.CreateUnboundedPrioritized — urgent orders jump the queue
     4 ms [t 17] 6 normal orders queued
     6 ms [t  8] processing order #1 (Normal)
    75 ms [t  8] processing order #2 (Normal)
   138 ms [t  8] processing order #3 (Normal)
   169 ms [t  8] 2 urgent orders queued
   201 ms [t  8] processing order #7 (Urgent)
   263 ms [t  8] processing order #8 (Urgent)
   326 ms [t  8] processing order #4 (Normal)
   388 ms [t  8] processing order #5 (Normal)
   451 ms [t  8] processing order #6 (Normal)
```

A prioritized channel isn't FIFO: the next item read is the smallest according to the comparer (`Comparer<T>.Default` if you don't pass one).
Here `Urgent` is 0 and `Normal` is 1, and for equal priority the Id decides: normal orders stay in arrival order.
The two urgent ones, queued at 169 ms, jump ahead of #4, #5 and #6, which had been queued earlier.
The order already being processed (#3) isn't interrupted: priority only decides which item is read next.
The channel is unbounded: as in 5.1, nothing holds back the producer.

`WhenEach` is for when you have N tasks and want to act on each result as soon as it's ready; the prioritized channel is for when the consumer must pick by importance rather than by arrival order.

## What to know

- **Default to bounded.** With `CreateUnbounded`, and with `CreateUnboundedPrioritized`, nothing holds back the producer: if it's faster than the consumer, the difference becomes memory and latency, without limit (5.1).
- **The data picks the `FullMode`.** `Wait`, the default, when nothing may be lost: the producer slows down and the problem moves upstream. `DropOldest` when only the latest value matters. There are also `DropNewest` (drops the newest item in the channel) and `DropWrite` (drops the item being written).
- **With the drop modes, pass `itemDropped`.** It's the only way to count, log or dispose of what the channel throws away.
- **The writer is completed once, when everyone is done.** With several workers on the same channel, completion isn't up to a worker but to whoever awaits them all (`Task.WhenAll`, then `TryComplete`). `Complete` on an already completed channel throws, `TryComplete` returns `false`.
- **Errors travel both ways.** Downstream with `TryComplete(ex)`: the reader's `await foreach` ends with that exception. Upstream with a shared cancellation token: without one, a writer on a full channel that nobody reads anymore stays blocked forever. The caller gets the original error, not the cancellation that follows from it.
- **Item error ≠ stage error.** A malformed message is dead-lettered and the pipeline carries on; a provider outage stops everything.
- **No fire-and-forget workers.** A faulted `Task` that nobody awaits is only discovered when the GC finalizes it.
- **Options are promises.** `SingleReader`/`SingleWriter = true` only with a single reader or writer at a time; `AllowSynchronousContinuations` stays `false` unless you know exactly where the continuations will run.
- The same bounded channel with `DropOldest` comes back in [Demo 6](../06-RxInterop/README.md) as the explicit boundary between an Rx stream and an async consumer.

## References

- [Channels — .NET documentation](https://learn.microsoft.com/dotnet/core/extensions/channels)
- [Stephen Toub — An Introduction to System.Threading.Channels](https://devblogs.microsoft.com/dotnet/an-introduction-to-system-threading-channels/)
- [Task.WhenEach — API](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task.wheneach)
- [Channel.CreateUnboundedPrioritized — API](https://learn.microsoft.com/dotnet/api/system.threading.channels.channel.createunboundedprioritized)
