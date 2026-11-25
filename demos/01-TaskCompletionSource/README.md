# Demo 1 — TaskCompletionSource: the building block

**English** · [Italiano](README-it.md)

An `async` method produces a `Task` that completes when the method finishes.
`TaskCompletionSource<T>` (TCS) pulls the two apart: you hand out the `Task` right away, and someone else completes it when the data arrives.
It's the bridge between a callback-based world (a thread reading from a connection, a message bus handler, an operator command) and a caller that just wants to `await`.

In QuoteHub, the gateway that asks slow, unreliable external providers for quotes, a TCS shows up whenever the gateway doesn't call a method but waits for a message: the response on a connection, the correlated reply on a bus, the signal that resumes a paused pipeline.
It's also the building block for custom coordination primitives, such as the event in scenario 1.3.
The basics (what a TCS is, its API, the patterns it's used for) are in the [deep dive on `TaskCompletionSource`](../../docs/task-completion-source.md); this Demo is about the traps.

Whoever completes the TCS does so on their own thread, possibly while holding a lock, and possibly when nobody is waiting anymore.
The three scenarios are the three questions to ask every time: *where* the waiting code runs (1.1), *who* wins when the reply and the timeout arrive together, and who cleans up afterwards (1.2), *when* a TCS can be replaced with a new one (1.3).
None of the three bugs reaches the caller as an exception: they show up as latency on a thread that isn't yours, memory that grows round after round, waiters that never wake up.

## How to run

```bash
dotnet run --project demos/01-TaskCompletionSource -- 1.1                   # broken, Enter, fixed
dotnet run --project demos/01-TaskCompletionSource -- rpc-correlation --fixed
dotnet run --project demos/01-TaskCompletionSource -- all --no-pause        # all scenarios, both variants
```

In an IDE, the launch profiles are `menu`, `1.1 inline-continuations`, `1.2 rpc-correlation` and `1.3 manual-reset-event`.

Every log line shows the milliseconds since the start of the variant and the thread id (`[t  4]`): in this Demo, the thread is almost always the point.
The output below comes from a Debug build on .NET 11.0.0; the milliseconds vary from machine to machine.

## ★ 1.1 `inline-continuations` — `SetResult` runs your code on the read loop

A connection with a dedicated thread, the *read loop*, that reads incoming frames and completes the matching pending request ([InlineContinuationsDemo.cs](InlineContinuationsDemo.cs)).
Five requests are waiting for their reply; frames arrive every 20 ms, and each handler spends 150 ms working on its reply.

```csharp
static async Task HandleAsync(FakeConnection connection, int id, Action countOnReadLoop)
{
    var reply = await connection.SendAsync(id);
    // …
    // CPU-bound work on the reply: deserialize, validate, map.
    Thread.Sleep(HandlerWork);
}

// The read loop: completing inside the lock is common.
lock (gate)
{
    pending.Remove(id, out var tcs);
    tcs!.SetResult(new Frame(id));
}
```

**Broken.** The TCS is created with `TaskCreationOptions.None`:

```text
    35 ms [t  4] read loop: frame 1 dispatched, 6 ms after it arrived
✘ request 1: handling reply 1 ON THE READ LOOP (inside its lock: True)
   194 ms [t  4] read loop: frame 2 dispatched, 146 ms after it arrived
✘ request 2: handling reply 2 ON THE READ LOOP (inside its lock: True)
  …
   668 ms [t  4] read loop: frame 5 dispatched, 560 ms after it arrived
✘ request 5: handling reply 5 ON THE READ LOOP (inside its lock: True)

── Results
  Handlers run on the read loop    5/5
  Max frame delay in the read loop 560 ms
  All replies handled after        829 ms
```

**Why.** With the default options, `SetResult` can run the `await` continuation directly, on the calling thread, before it returns: here it does so all five times.
The rest of `HandleAsync`, `Thread.Sleep` included, therefore runs on the read loop (`t 4`) and inside its `lock`.
For 150 ms the loop reads nothing: each frame waits about 140 ms longer than the previous one between arriving and being dispatched, and the delay builds up to 560 ms.
On top of that, the lock stays held for the whole handler, and `SendAsync` takes the same lock to register a new request: anyone who just wants to send, from another thread, is stuck as well.

**Fixed.** The only change is the option the TCS is created with:

```csharp
var options = variant == Variant.Broken
    ? TaskCreationOptions.None
    : TaskCreationOptions.RunContinuationsAsynchronously;
// …
var tcs = new TaskCompletionSource<Frame>(completionOptions);
```

With `RunContinuationsAsynchronously` the continuation runs asynchronously, on a pool thread, and `SetResult` returns to the loop immediately:

```text
    30 ms [t  8] read loop: frame 1 dispatched, 10 ms after it arrived
✔ request 1: handling reply 1 on a pool thread (inside the loop's lock: False)
  …
   108 ms [t  8] read loop: frame 5 dispatched, 8 ms after it arrived
✔ request 5: handling reply 5 on a pool thread (inside the loop's lock: False)

── Results
  Handlers run on the read loop    0/5
  Max frame delay in the read loop 12 ms
  All replies handled after        264 ms
```

The handlers run in parallel, the loop keeps pace with the frames, and the total drops from 829 to 264 ms: the last frame at 108 ms, plus the 150 ms of its handler.

The takeaway: whoever calls `SetResult` lends their thread to code they know nothing about.
If that thread is an I/O loop, a timer or a bus dispatcher, or if it holds a lock, create the TCS with `RunContinuationsAsynchronously`.

## ★ 1.2 `rpc-correlation` — request/response over a message bus

QuoteHub asks for a quote by publishing a message on a bus, and the reply comes back as another message with the same id ([RpcCorrelationDemo.cs](RpcCorrelationDemo.cs)).
The client keeps one TCS per request in a dictionary keyed by id: the reply handler looks it up and completes it.

`FakeBus` is deterministic: out of every 10 messages, 1 is lost, 2 get a reply after 250 ms, 7 within 40 ms.
The timeout is 100 ms, so the slow replies arrive after the caller has already given up.
The scenario runs 3 rounds of 1000 concurrent requests, and measures the heap with a full GC after each round.

**Broken.** The version everyone writes first:

```csharp
public Task<RpcReply> RequestAsync(string symbol, TimeSpan timeout, CancellationToken cancellationToken)
{
    var id = Guid.NewGuid();
    var tcs = new TaskCompletionSource<RpcReply>();
    pending[id] = tcs;

    // Never disposed: stays registered on the connection token, keeping tcs alive.
    cancellationToken.Register(() => tcs.SetCanceled(cancellationToken));

    // Never disposed either.
    var timeoutCts = new CancellationTokenSource(timeout);
    timeoutCts.Token.Register(() =>
    {
        if (!tcs.Task.IsCompleted)
            tcs.SetCanceled(timeoutCts.Token);
    });
    // …
}

void OnReply(RpcReply reply)
{
    // Never removed; throws if the timeout already completed the task.
    if (pending.TryGetValue(reply.Id, out var tcs))
        tcs.SetResult(reply);
}
```

```text
✘ bus: reply handler threw InvalidOperationException: An attempt was made to transition a task to a final state when it had already completed.
   426 ms [t 16] round 1: 1000 requests (timeout 100 ms), pending entries 1000, heap 839 KB
   848 ms [t 12] round 2: 1000 requests (timeout 100 ms), pending entries 2000, heap 1,355 KB
  1273 ms [t 22] round 3: 1000 requests (timeout 100 ms), pending entries 3000, heap 1,835 KB

── Results
  Replies received                 2100
  Timed out                        900
  Late replies (after the timeout) 600
  Exceptions thrown into the bus   600
  Pending entries left             3000
  Heap growth, round 1 → 3         +995 KB
```

**Why.** Three separate mistakes, and they add up.

- **`SetResult` on an already completed TCS throws.** For every late reply the timeout has already called `SetCanceled`, so `SetResult` throws `InvalidOperationException`. The exception doesn't reach the caller, who has already had their timeout: it escapes the handler and lands in the bus code. Here `FakeBus` catches and counts it, as a client library would: 600 exceptions, one per late reply. Even the `IsCompleted` check before `SetCanceled` offers no protection: the reply can arrive between the check and the call.
- **Nobody removes the entries from the dictionary.** `OnReply` uses `TryGetValue` rather than `TryRemove`, and nothing else removes them: every round leaves 1000 more entries behind, including the ones that completed successfully.
- **The registrations stay attached to the connection token.** `Register` returns a `CancellationTokenRegistration` that nobody disposes: the lambda, and the TCS with it, stays among the token's callbacks for as long as the connection lives. Even if the entries were removed from the dictionary, the TCS would still be reachable from there. And the timeout's `CancellationTokenSource` is never disposed.

The result is a heap that grows by 480-520 KB per round, on a connection that lives for hours in production.

**Fixed.**

```csharp
public async Task<RpcReply> RequestAsync(string symbol, TimeSpan timeout, CancellationToken cancellationToken)
{
    var id = Guid.NewGuid();
    var tcs = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
    pending[id] = tcs;

    try
    {
        // Timeout and cancellation complete the TCS differently: the caller can tell them apart.
        using var timeoutCts = new CancellationTokenSource(timeout);
        await using var onTimeout = timeoutCts.Token.Register(
            static state => ((TaskCompletionSource<RpcReply>)state!).TrySetException(new TimeoutException()), tcs);
        await using var onCancel = cancellationToken.Register(
            static (state, token) => ((TaskCompletionSource<RpcReply>)state!).TrySetCanceled(token), tcs);

        bus.Publish(id, symbol);
        return await tcs.Task;
        // Same semantics, shorter: await tcs.Task.WaitAsync(timeout, cancellationToken)
    }
    finally
    {
        pending.TryRemove(id, out _);
    }
}

void OnReply(RpcReply reply)
{
    // A late reply finds nothing: it's ignored, not thrown.
    if (pending.TryRemove(reply.Id, out var tcs))
        tcs.TrySetResult(reply);
}
```

```text
   421 ms [t 18] round 1: 1000 requests (timeout 100 ms), pending entries 0, heap 445 KB
   837 ms [t 19] round 2: 1000 requests (timeout 100 ms), pending entries 0, heap 447 KB
  1256 ms [t 20] round 3: 1000 requests (timeout 100 ms), pending entries 0, heap 448 KB

── Results
  Replies received                 2100
  Timed out                        900
  Late replies (after the timeout) 600
  Exceptions thrown into the bus   0
  Pending entries left             0
  Heap growth, round 1 → 3         +2 KB
```

- **`TrySetResult`, `TrySetException`, `TrySetCanceled`.** The reply, the timeout and the connection's cancellation race to complete the same TCS: the first one wins, the others get `false` instead of an exception.
- **Timeout and cancellation stay distinguishable.** The timeout completes the TCS with a `TimeoutException`; cancellation uses `TrySetCanceled(token)`, so the `OperationCanceledException` the caller gets carries its token (`ex.CancellationToken == cancellationToken`). Without the token the caller would get `CancellationToken.None` and couldn't tell whether the timeout expired or it cancelled the request itself. These are the same semantics as `Task.WaitAsync(timeout, cancellationToken)`: how it works is in the [deep dive on `WaitAsync`](../../docs/wait-async.md).
- **The entry leaves the dictionary in two places**: in `OnReply`, with `TryRemove`, when the reply arrives, and in the `finally` on timeout or cancellation. A late reply finds nothing and is ignored.
- **The registrations live as long as the call.** One sits on the timeout's token, the other on the connection token, and both are released at the end of the call; the `using` also disposes the timeout's `CancellationTokenSource`. The lambdas are `static` and receive the TCS as state (the `Register` overload with `(state, token)` also passes the token): no closure per request.
- **`await using` for the registrations.** `CancellationTokenRegistration` also implements `IAsyncDisposable`: if the callback is running on another thread at that moment, `Dispose()` waits for it by blocking the thread, while `DisposeAsync()` waits without blocking. Here the callback is a `TrySet*` and the wait would be very short, but in an async method it's the right form. `CancellationTokenSource` only implements `IDisposable`, so it stays `using`. See the [deep dive on `IAsyncDisposable`](../../docs/async-disposable.md).
- **`RunContinuationsAsynchronously`**, for the same reason as in scenario 1.1: `TrySetResult` runs inside the bus's delivery code.

To the caller, the counts are the same: 2100 replies and 900 timeouts in both versions, except that in the fixed one the timeouts arrive as a `TimeoutException`.
What changes is everything left behind: 0 exceptions in the bus, 0 pending entries, the heap flat at around 445 KB.

The takeaway: a TCS has a lifecycle that the compiler doesn't manage for you.
Every place you register it (a dictionary, a token) needs an exit in a `finally`, and when more than one party can complete it, use `TrySet*`.

## 1.3 `manual-reset-event` — `Reset` without losing waiters

An async manual-reset event acts as a gate for a pipeline: `WaitAsync` waits for it to open, `Set` opens it, `Reset` closes it again ([ManualResetEventDemo.cs](ManualResetEventDemo.cs)).
The classic implementation holds a TCS: `WaitAsync` returns its `Task`, `Set` completes it, `Reset` replaces it with a new one.
The scenario: the pipeline starts paused with 3 workers held at the gate, the operator presses Pause (the pipeline is already paused), then Resume.

**Broken.**

```csharp
// Broken: Reset always swaps the TCS, even when nobody has set it yet.
public Task WaitAsync() => tcs.Task;
public void Set() => tcs.TrySetResult();
public void Reset() => tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
```

```text
     2 ms [t 20] worker 1: waiting at the gate
     2 ms [t 20] worker 2: waiting at the gate
     3 ms [t 20] worker 3: waiting at the gate
    60 ms [t 20] operator: Pause (the pipeline is already paused)
   123 ms [t 20] operator: Resume

── Results
  Workers resumed                  0/3
```

**Why.** The workers are awaiting the `Task` of the first TCS.
`Reset` replaces it even though nobody has completed it, and `Set` completes the new one.
The event no longer holds a reference to the old one: nobody can ever complete it, and its three waiters stay suspended forever (the scenario stops waiting for them after 500 ms).
No exception, and no blocked thread to find in a dump: just three operations that never resume.

**Fixed.** `Reset` replaces the TCS only if it's already completed, and only if nobody else has replaced it in the meantime:

```csharp
public Task WaitAsync() => Volatile.Read(ref tcs).Task;

public void Set() => Volatile.Read(ref tcs).TrySetResult();

public void Reset()
{
    while (true)
    {
        var current = Volatile.Read(ref tcs);
        if (!current.Task.IsCompleted)
            return;

        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref tcs, next, current) == current)
            return;
    }
}
```

```text
    62 ms [t 20] operator: Pause (the pipeline is already paused)
   124 ms [t 20] operator: Resume
   125 ms [t 16] worker 1: resumed
   125 ms [t 20] worker 3: resumed
   125 ms [t 14] worker 2: resumed

── Results
  Workers resumed                  3/3
```

A `Reset` on an event that's already closed does nothing, so the waiters stay on the TCS that `Set` will complete.
The `CompareExchange` covers the race between two concurrent `Reset` calls: without it, both could see the completed TCS and each install a new one, and anyone who called `WaitAsync` between the two replacements would be orphaned.
It's the same pattern as Stephen Toub's `AsyncManualResetEvent` (see the references), with the non-generic TCS from .NET 5 in place of `TaskCompletionSource<bool>`.

The three workers resume on different threads (`t 16`, `t 20`, `t 14`) because here, too, the TCS is created with `RunContinuationsAsynchronously`.
Without it, they would resume one after another inside the operator's call to `Set`, like the handlers in scenario 1.1.

The takeaway: a TCS completes only once.
If a primitive recycles it, the replacement must happen only once it's completed, and atomically.

## What to know

- **Create TCSs with `RunContinuationsAsynchronously`** when they're completed by code you don't control or that holds a lock: a read loop, a timer, a bus handler. Without it, that code runs your continuations (1.1).
- **Use `TrySet*` when more than one party can complete the same TCS**: reply, timeout, cancellation, shutdown. `Set*` is fine only if you're certain you're the only one, and an `IsCompleted` check beforehand isn't enough (1.2).
- **Pass the token to `TrySetCanceled(token)`**, and keep the timeout distinct from cancellation (`TimeoutException`): whoever receives the exception must be able to tell who interrupted the wait.
- **Every registration has its exit.** Dictionary entry → `TryRemove` in `finally`; `CancellationToken.Register` → dispose the `CancellationTokenRegistration`, with `await using` in an async method; `CancellationTokenSource` → `using`. A token that lives as long as the connection keeps alive everything you register on it.
- **`Task.WaitAsync(timeout, cancellationToken)`** (.NET 6+) is the shorter alternative to a timeout CTS and registrations, with the same semantics, noted in the source of 1.2. It stops waiting without completing the TCS, so the removal in the `finally` is still needed. The same API, applied to each caller, solves the shared cancellation problem in [Demo 2](../02-AsyncCaching/README.md). See the [deep dive on `WaitAsync`](../../docs/wait-async.md).
- **A TCS can't be reset.** To reuse a signal you replace it, only once it's completed and with `CompareExchange` (1.3). To reuse the source itself without allocating a `Task` per operation, there's `IValueTaskSource` built on `ManualResetValueTaskSourceCore<T>`: see [Demo 3](../03-ValueTask/README.md).
- The output on this page comes from a build with Runtime Async enabled, the repo default ([Directory.Build.targets](../../Directory.Build.targets)): the inline continuations of scenario 1.1 happen there too.

## References

- [TaskCreationOptions — RunContinuationsAsynchronously](https://learn.microsoft.com/dotnet/api/system.threading.tasks.taskcreationoptions)
- [Sergey Tepliakov — The danger of `TaskCompletionSource<T>` class](https://devblogs.microsoft.com/premier-developer/the-danger-of-taskcompletionsourcet-class/)
- [Stephen Toub — Building Async Coordination Primitives, Part 1: AsyncManualResetEvent](https://devblogs.microsoft.com/dotnet/building-async-coordination-primitives-part-1-asyncmanualresetevent/)
- [Cancellation in managed threads](https://learn.microsoft.com/dotnet/standard/threading/cancellation-in-managed-threads)
