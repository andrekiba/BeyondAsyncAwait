# Demo 3 — Hot path: ValueTask

**English** · [Italiano](README-it.md)

In the QuoteHub gateway, prices sit in a cache in front of the slow provider.
When the price is there, the async method returns without ever suspending: this is the hot path, the call the gateway makes over and over.
With compiler state machines even this synchronous return has a cost: an `async Task<decimal>` has to hand back its result inside a `Task`, and for a price it allocates a new one on every call.
That is what `ValueTask<T>` is for: a struct that holds the result when it is already available, and a `Task<T>` or an `IValueTaskSource<T>` when it is not.

Runtime Async ([Demo 0](../00-RuntimeAsync/README.md)) changes the classic advice.
A `Task<T>` that is awaited directly is never materialized, so a cache hit costs 0 bytes even without `ValueTask`.
The allocation comes back as soon as the `Task` becomes an object: it goes through a delegate, ends up in a field, is passed to `WhenAll`.
Scenario 3.1 measures the two models side by side. Scenario 3.2 shows where `ValueTask` is still decisive, reusable sources (`IValueTaskSource`), and the rule that comes with it: a `ValueTask` can be consumed only once.

## How to run

The numbers to look at are bytes per call, and they must be taken in Release:

```bash
dotnet run -c Release --project demos/03-ValueTask -- 3.1                         # Runtime Async, the repo default
dotnet run -c Release --project demos/03-ValueTask -p:RuntimeAsync=false -- 3.1   # compiler state machines
dotnet run -c Release --project demos/03-ValueTask -- 3.2
```

Each scenario runs the broken variant and then the fixed one; with `--broken` or `--fixed` it runs only that one.
The banner (`runtime async, .NET 11.0.0` or `compiler state machines, .NET 11.0.0`) tells you which model the assembly was compiled with.
In the IDE you get the `menu`, `3.1 cache-hit (Release)` and `3.2 value-task-source` profiles.

**Debug or Release.** With Runtime Async on, a Debug run gives the same bytes per call and the same outcomes as Release: only the 3.2 totals change, because they count allocations across the whole process.
With `-p:RuntimeAsync=false`, on the other hand, compiler state machines are a `class` in Debug and allocate on every call: scenario 3.1 detects this and prints a warning (`Debug build: compiler state machines are classes in Debug and allocate on every call. Use -c Release.`).
Comparing the two models therefore takes two Release builds.
The `3.1 cache-hit (Release)` profile does not set the configuration: the suffix is a reminder to pick Release in the IDE.

## ★ 3.1 `cache-hit` — what a cache hit costs

[QuoteCache.cs](QuoteCache.cs): same code, two return types.

```csharp
public async Task<decimal> GetPriceAsTaskAsync(string symbol)
{
    if (prices.TryGetValue(symbol, out var price))
        return price;

    var quote = await provider.GetQuoteAsync(symbol);
    return prices[symbol] = quote.Price;
}

public async ValueTask<decimal> GetPriceAsValueTaskAsync(string symbol)
{
    // … same body
}
```

The scenario ([CacheHitDemo.cs](CacheHitDemo.cs)) warms up the cache with a first call, a miss that goes to the provider, and then measures 100,000 hits with three ways of consuming the result.
`MeasureAsync` runs the loop once as a warm-up (JIT, tiering), then runs it again, reading `GC.GetAllocatedBytesForCurrentThread()` before and after: every hit completes synchronously, so all the allocations happen on the current thread.

**Broken: `Task<T>`.**

```csharp
Func<string, Task<decimal>> viaDelegate = cache.GetPriceAsTaskAsync;

await MeasureAsync("Task<T>: await call", async () =>
{
    for (var i = 0; i < Calls; i++)
        await cache.GetPriceAsTaskAsync(Symbol);
});

await MeasureAsync("Task<T>: await via delegate", async () =>
{
    for (var i = 0; i < Calls; i++)
        await viaDelegate(Symbol);
});

// A Task kept in a local can still be optimized away in Release: one kept in a field/array can't.
await MeasureAsync("Task<T>: kept as an object", async () =>
{
    var pending = new Task<decimal>[1];
    for (var i = 0; i < Calls; i++)
    {
        pending[0] = cache.GetPriceAsTaskAsync(Symbol);
        await pending[0];
    }
});
```

With compiler state machines (`-p:RuntimeAsync=false`), 80 bytes per hit in all three cases:

```text
compiler state machines, .NET 11.0.0
    76 ms [t  5] cold call (miss): 281.34
    76 ms [t  5] cache is warm, now 100,000 hits per measurement
  Task<T>: await call               80.0 bytes/call   (8,000,000 bytes)
  Task<T>: await via delegate       80.0 bytes/call   (8,000,000 bytes)
  Task<T>: kept as an object        80.0 bytes/call   (8,000,032 bytes)
```

With Runtime Async the direct await drops to zero, the other two do not:

```text
runtime async, .NET 11.0.0
    57 ms [t  5] cold call (miss): 281.34
    57 ms [t  5] cache is warm, now 100,000 hits per measurement
  Task<T>: await call                0.0 bytes/call   (0 bytes)
  Task<T>: await via delegate       80.0 bytes/call   (8,000,000 bytes)
  Task<T>: kept as an object        80.0 bytes/call   (8,000,032 bytes)
```

The extra 32 bytes on the last line are the one-element array, created once per measurement.

**Why.** With state machines, an `async Task<decimal>` method that returns synchronously still hands back its result inside a completed `Task`.
The runtime caches completed `Task`s for only a few values (bool, small integers, `null`): a price is not one of them, so every hit allocates a new `Task<decimal>`, 80 bytes.
With Runtime Async, when an async method awaits the call directly, the result reaches the caller without going through a `Task` object.
If instead the `Task` has to exist as a value (here a delegate's return value and an array element, elsewhere a field or a `WhenAll`), it is created and costs the same as before.

**Fixed: `ValueTask<T>`.** The same three loops on `GetPriceAsValueTaskAsync`, with a `Func<string, ValueTask<decimal>>` and a `ValueTask<decimal>[1]`.
The output is identical in both models:

```text
  ValueTask<T>: await call           0.0 bytes/call   (0 bytes)
  ValueTask<T>: await via delegate   0.0 bytes/call   (0 bytes)
  ValueTask<T>: kept as an object    0.0 bytes/call   (56 bytes)
```

`ValueTask<decimal>` is a struct: the synchronous result travels inside the value, whether it goes through a delegate or an array. The 56 bytes are, once again, the array.

| Bytes per hit, Release | State machine | Runtime Async |
|---|---|---|
| `Task<T>`, direct await | 80 | **0** |
| `Task<T>`, await via delegate | 80 | 80 |
| `Task<T>`, kept in an array | 80 | 80 |
| `ValueTask<T>`, all three cases | 0 | 0 |

With Runtime Async the classic argument, `ValueTask` on the hot path, carries less weight: a `Task<T>` that is awaited right away no longer allocates.
`ValueTask<T>` is still decisive when the result becomes an object before it is awaited, and in code compiled without Runtime Async.

## ★ 3.2 `value-task-source` — zero allocations, one await

A cache hit never suspends. An operation that really suspends, such as a network receive, has no result to carry inline: the `ValueTask` has to point to an object that will complete later.
The `Socket` pattern is to reuse that object.
In [QuoteFeed.cs](QuoteFeed.cs) every `ReceiveAsync` takes an operation object from a pool (or creates one, and counts it) and returns a `ValueTask` built on top of it.
The "network" answers on a thread-pool thread, with fixed prices (MSFT 412.10, AAPL 228.70): a wrong result shows up immediately.

The operation object implements `IValueTaskSource<decimal>` by delegating everything to `ManualResetValueTaskSourceCore<decimal>`, which handles the result, the continuation and the version:

```csharp
sealed class QuoteOperation(PooledQuoteFeed owner) : IValueTaskSource<decimal>, IThreadPoolWorkItem
{
    // Mutable struct: never make this field readonly.
    ManualResetValueTaskSourceCore<decimal> core;
    string symbol = "";

    public ValueTask<decimal> Start(string symbol)
    {
        this.symbol = symbol;
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        // The token is the version: it changes on every Reset, so a stale ValueTask is detected.
        return new ValueTask<decimal>(this, core.Version);
    }

    // The "network" answers on a thread-pool thread.
    void IThreadPoolWorkItem.Execute() => core.SetResult(Prices.Of(symbol));

    public decimal GetResult(short token)
    {
        try
        {
            return core.GetResult(token);
        }
        finally
        {
            // Consumed: reset (version++) and back to the pool, ready for the next ReceiveAsync.
            core.Reset();
            owner.Return(this);
        }
    }

    public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        core.OnCompleted(continuation, state, token, flags);
}
```

**Broken.** Three parts, in [ValueTaskSourceDemo.cs](ValueTaskSourceDemo.cs).

*One `TaskCompletionSource` per operation* (`TcsQuoteFeed`): every `ReceiveAsync` creates an operation object, its `TaskCompletionSource` and that source's `Task`.
Here the completions run on thread-pool threads, so `MeasureAsync` counts the allocations of the whole process with `GC.GetTotalAllocatedBytes(precise: true)`:

```text
── A TaskCompletionSource per operation
  TaskCompletionSource feed         136.0 bytes/operation   (13,601,672 bytes)
```

*The same `ValueTask` awaited twice*, on the correct pooled feed:

```csharp
var pending = feed.ReceiveAsync("MSFT");
Out.Log($"MSFT = {await pending}");
try
{
    Out.Log($"MSFT again = {await pending}");
}
catch (InvalidOperationException ex)
{
    // …
}
```

```text
── Pooled feed: the same ValueTask awaited twice
    97 ms [t  8] MSFT = 412.10
✘ InvalidOperationException: the token is stale, the operation object was already reset and pooled
```

The first `await` goes through `GetResult`, which calls `Reset()` and puts the object back in the pool.
The second one presents the token of the previous version: `ManualResetValueTaskSourceCore` does not recognize it and throws `InvalidOperationException`, which the demo catches and prints.

*A naive source that ignores the token* (`NaivePooledQuoteFeed`): same pool, but the token is always 0 and `GetResult` returns whatever price the object holds at that moment.

```csharp
public ValueTask<decimal> Start(string symbol)
{
    // …
    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    return new ValueTask<decimal>(this, 0);
}

// …

public decimal GetResult(short token)
{
    var result = price;
    owner.Return(this);
    return result;
}
```

The demo awaits the MSFT `ValueTask`, asks for AAPL (the same object, taken back out of the pool), then awaits the first one again:

```csharp
var first = naive.ReceiveAsync("MSFT");
Out.Log($"MSFT = {await first}");
Out.Log($"AAPL = {await naive.ReceiveAsync("AAPL")}   (same pooled object, reused)");
var again = await first;
```

```text
── Naive pooled feed (token ignored): await twice, with a reuse in between
    99 ms [t  9] MSFT = 412.10
    99 ms [t  9] AAPL = 228.70   (same pooled object, reused)
✘ MSFT again = 228.70  ← that's AAPL's price
```

No exception: the second `await` on `first` reads the state of the reused object and returns AAPL's price as if it were MSFT's.
And `GetResult` puts the object back in the pool this time too: now it is in there twice, and the next two `ReceiveAsync` calls would compete for the same object.

**Why.** A `ValueTask` built on an `IValueTaskSource` is good for a single await of that object's current operation.
Once consumed, the object goes back to the pool and serves another operation: the token is the only way to tell the two apart.
With the token the mistake becomes an exception; without it, it becomes wrong data.

**Fixed.** The pooled feed consumed the way it is meant to be, one `await` per `ValueTask`; when you need several awaits or a combinator, you pay for a `Task`, and only there.

```csharp
await MeasureAsync("Pooled IValueTaskSource feed", async () =>
{
    for (var i = 0; i < Operations; i++)
        await feed.ReceiveAsync("MSFT");
});
// …
var preserved = feed.ReceiveAsync("MSFT").Preserve();
Out.Log($"MSFT = {await preserved}, again = {await preserved}   (Preserve(): Task-backed copy)");

var both = await Task.WhenAll(feed.ReceiveAsync("MSFT").AsTask(), feed.ReceiveAsync("AAPL").AsTask());
```

```text
── Pooled IValueTaskSource feed
  Pooled IValueTaskSource feed        0.0 bytes/operation   (352 bytes)
  Operation objects ever created   1

── Need it twice, or in WhenAll? Pay for a Task only then
   104 ms [t 10] MSFT = 412.10, again = 412.10   (Preserve(): Task-backed copy)
✔ WhenAll via AsTask(): MSFT = 412.10, AAPL = 228.70
```

The 100,000 operations complete on a thread-pool thread and use a single operation object: each `await` returns it to the pool before the next request starts.
The total, 352 bytes over 100,000 operations (128 in the state machine build), is measured across the whole process.
`Preserve()` returns a `Task`-backed `ValueTask` that can be awaited multiple times; `AsTask()` is what lets you pass the operations to `WhenAll`.

| Release | State machine | Runtime Async |
|---|---|---|
| `TaskCompletionSource` per operation | 136 bytes/operation | 136 bytes/operation |
| pooled `IValueTaskSource` | 0 bytes/operation, 1 object | 0 bytes/operation, 1 object |
| same `ValueTask` awaited twice | `InvalidOperationException` | `InvalidOperationException` |
| source without a token | 228.70 returned as MSFT | 228.70 returned as MSFT |

Here the compilation model changes nothing: bytes per operation depend on the source, not on how the awaiting method is compiled.
A pooled `IValueTaskSource` makes an operation that really suspends free, with Runtime Async too; in exchange, the `ValueTask` must be consumed only once, and whoever writes the source has to check the token.

## Benchmarks

In [benchmarks/BeyondAsyncAwait.Benchmarks](../../benchmarks/BeyondAsyncAwait.Benchmarks) every benchmark runs twice: the `StateMachine` job (baseline) and the `RuntimeAsync` job.

```bash
dotnet run -c Release --project benchmarks/BeyondAsyncAwait.Benchmarks -- --filter '*ValueTask*'
```

In [ValueTaskBenchmarks.cs](../../benchmarks/BeyondAsyncAwait.Benchmarks/ValueTaskBenchmarks.cs):

- `Hit_Task_Awaited` and `Hit_ValueTask_Awaited`: 100 cache hits awaited directly inside an async method; with `OperationsPerInvoke` the numbers are per hit.
- `Hit_Task_Materialized`: the cache hit's `Task` kept in a field, so it has to exist as an object.
- `Suspend_ValueTask` and `Suspend_PoolingValueTask`: an `async ValueTask<int>` that really suspends (`Task.Yield`), with the standard builder and with `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]`.

**Indicative** results (`--quick`, i7-11800H, .NET 11.0.0): rerun them on your own machine, without `--quick`.

| Benchmark | StateMachine | RuntimeAsync |
|---|---|---|
| cache hit, `Task<T>` awaited | 18 ns, 81 B | **11 ns, 0 B** |
| cache hit, `ValueTask<T>` awaited | 17 ns, 1 B | 11 ns, 0 B |
| cache hit, `Task<T>` kept in a field | 22 ns, 80 B | 24 ns, 80 B |
| suspending, `ValueTask<T>` | 426 ns, 104 B | 342 ns, 104 B |
| suspending, `PoolingAsyncValueTaskMethodBuilder` | 422 ns, **0 B** | 353 ns, **104 B** |

- **Awaited hit**: with state machines `Task<T>` pays the 80 B of its `Task` on every hit, `ValueTask<T>` does not. The extra byte on both rows is the `Task<decimal>` of the benchmark method, itself `async`, spread over the 100 hits. With Runtime Async both drop to 0 B and 11 ns: for a direct await, `Task<T>` and `ValueTask<T>` are equivalent.
- **`Task` in a field**: 80 B in both jobs, with similar timings. When the `Task` has to exist as an object, Runtime Async cannot avoid it: this is the "kept as an object" line of scenario 3.1.
- **Suspending**: when the method really suspends, the inline result is of no use, and with state machines `ValueTask<T>` allocates 104 B. The pooling builder reuses the state machine boxes and gets down to 0 B. In the RuntimeAsync job the two rows line up at 104 B: methods with `PoolingAsyncValueTaskMethodBuilder` drop out of Runtime Async, and the benefit of pooling no longer shows. Runtime Async is faster in both cases, but the two optimizations do not add up.

The BenchmarkDotNet harness is compiled with the same `RuntimeAsync` setting as the code being measured: that is why `Hit_Task_Materialized` keeps the `Task` in a field.
Simply returning it would not be enough: the harness would await it directly, and with Runtime Async the `Task` would not be materialized.

## What to know before using ValueTask

- **`Task` is still the default.** The `ValueTask<TResult>` documentation recommends switching to `ValueTask` only when performance analysis justifies it. A `ValueTask` has more fields than a reference: it costs more to copy, and it makes the state machine of the caller that awaits it larger.
- **When it still pays off.** With Runtime Async, for a synchronous hot path, when the caller receives the result through a delegate or stores it in a field before awaiting it (scenario 3.1). In code compiled without Runtime Async, which is opt-in in .NET 11 (in this repo it is turned on by [Directory.Build.targets](../../Directory.Build.targets)), the classic advice holds. Operations that really suspend need a reusable `IValueTaskSource`, as `Socket` and `NetworkStream` do (scenario 3.2).
- **A `ValueTask` can be consumed only once.** The documentation lists what you should never do: await it more than once, call `AsTask()` more than once, use `.Result` or `.GetAwaiter().GetResult()` before the operation has completed (or more than once), or combine any of these. Stephen Toub adds: never two awaits in parallel, because the object behind it supports only one consumer at a time. The result is undefined: with `ManualResetValueTaskSourceCore` an exception, with a badly written source another operation's data.
- **Need more? Convert it once.** `AsTask()` to pass it to `WhenAll` / `WhenAny` or to await it more than once (from then on you use the `Task`); `Preserve()` returns a `ValueTask` that can be used at any time. You pay for a `Task`, only where you need one.
- **CA2012** ("Use ValueTasks correctly") flags `ValueTask`s that are discarded, consumed more than once, or read before they have completed, and asks you to await them directly instead of keeping them in variables or fields. In .NET 11 it is enabled as a suggestion; in this repo it is disabled ([Directory.Build.props](../../Directory.Build.props)), because scenario 3.2 misuses a `ValueTask` on purpose.
- **To write an `IValueTaskSource`**, delegate to `ManualResetValueTaskSourceCore<T>`: it is a mutable struct (the field must never be declared `readonly`), the `ValueTask` token is its `Version`, and `Reset()` must be called after the result has been consumed, before the object goes back to the pool. A pooled object serves one operation at a time: with concurrent operations, the pool in [QuoteFeed.cs](QuoteFeed.cs) creates one for each operation in flight.
- **`PoolingAsyncValueTaskMethodBuilder`** makes the method drop out of Runtime Async: box pooling and Runtime Async do not add up (see the benchmarks).

## References

- [Stephen Toub — Understanding the Whys, Whats, and Whens of ValueTask](https://devblogs.microsoft.com/dotnet/understanding-the-whys-whats-and-whens-of-valuetask/)
- [`ValueTask<TResult>` — API documentation](https://learn.microsoft.com/dotnet/api/system.threading.tasks.valuetask-1)
- [`ManualResetValueTaskSourceCore<TResult>` — API documentation](https://learn.microsoft.com/dotnet/api/system.threading.tasks.sources.manualresetvaluetasksourcecore-1)
- [CA2012: Use ValueTasks correctly](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2012)
