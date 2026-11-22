# Demo 2 — One value, many consumers: AsyncLazy and async caching

**English** · [Italiano](README-it.md)

QuoteHub requests quotes from slow, flaky external providers, and caches reference data.
When a key is cold and many requests arrive together, they all want the same value at the same moment: one value, many consumers.

"Cache the `Task`, not the value" is the well-known answer, and it solves only the first problem.
A cached `Task` is a shared promise: it hands every caller its outcome, even when that outcome is an error or a cancellation, and its origin, that is, the token and the context of whoever started it.
These traps don't show up with one caller at a time and a provider that always responds: they surface under load, when the provider has a transient error or a client disconnects.

The Demo starts from the stampede (2.1) and arrives at `Lazy<Task<T>>`.
Then it shows its three traps (2.2–2.4), which [AsyncLazy.cs](AsyncLazy.cs) fixes one by one in about fifty lines.
The last scenario (2.5) asks the same questions of `HybridCache`, which has stampede protection built in.

## How to run

```bash
dotnet run --project demos/02-AsyncCaching -- 2.1                          # broken, Enter, fixed
dotnet run --project demos/02-AsyncCaching -- shared-cancellation --fixed  # fixed only, by name
dotnet run --project demos/02-AsyncCaching -- all --no-pause               # all scenarios
```

From an IDE there are the profiles `menu`, `2.1 cache-stampede`, `2.2 failure-caching`, `2.3 shared-cancellation`, `2.4 factory-context` and `2.5 hybrid-cache`.
Scenario 2.5 has no broken/fixed variants: it runs only once.

The provider is the [FakeQuoteProvider](../../src/Demo.Shared/FakeQuoteProvider.cs) shared by all the Demos: 200 ms of latency, `FailNext(n)` to make the next `n` calls fail, and counters of calls, cancellations and failures that each scenario prints at the end.
Prices are deterministic (a stable hash of the symbol plus a `Random` with a fixed seed): that's why MSFT is 281.34 in every output below.
Two helpers complete the picture: [Concurrently.cs](Concurrently.cs) starts N calls at the same instant on the thread pool, like a burst of HTTP requests on a cold key; [Caller.cs](Caller.cs) is a client with its own `CancellationTokenSource`, which can disconnect after a delay and prints `cancelled — but it never asked to!` if it gets cancelled without having asked for it.

The outputs below come from a Debug run on .NET 11.0.0: milliseconds and thread ids change on every run.

## ★ 2.1 `cache-stampede` — 200 requests, one cold key

In [CacheStampedeDemo.cs](CacheStampedeDemo.cs), 200 requests ask together for `MSFT`, which isn't in the cache.
The loader simulates the synchronous prefix that every real loader has before it gets to the I/O:

```csharp
// A real loader has a synchronous prefix (build the request, take a pooled connection...):
// that is the window in which concurrent callers all see a miss.
static Task<Quote> LoadAsync(FakeQuoteProvider provider, string symbol)
{
    Thread.Sleep(5);
    return provider.GetQuoteAsync(symbol);
}
```

The broken variant tries two wrong caches in sequence.
**A** caches the value: check, await, store.

```csharp
if (cache.TryGetValue(symbol, out var cached))
    return cached;

var quote = await LoadAsync(provider, symbol);
cache[symbol] = quote;
return quote;
```

**B** caches the `Task`, with `ConcurrentDictionary.GetOrAdd`:

```csharp
var tasks = new ConcurrentDictionary<string, Task<Quote>>();
await Concurrently.RunAsync(Requests, () => tasks.GetOrAdd("MSFT", symbol => LoadAsync(provider, symbol)));
```

```text
── A. Cache the value: check, await, store
  Provider calls                   200

── B. Cache the Task with ConcurrentDictionary.GetOrAdd
  Provider calls                   16
```

In A the value enters the cache only when the first call completes, 200 ms later: until then every request sees a miss and calls the provider.

B is better, but not enough.
`GetOrAdd` calls the factory outside the dictionary's locks, and with concurrent calls it can call it more than once: only one value goes into the dictionary, and the others are discarded.
With a `Task`, discarding isn't enough, because creating it means starting the call: the 15 extra calls really went out, and nobody reads their result.
The 16 depends on the machine: it's how many thread pool threads enter the synchronous prefix of `LoadAsync` before the first `Task` gets published.

**Fixed.** The cache holds a `Lazy<Task<T>>`:

```csharp
var lazies = new ConcurrentDictionary<string, Lazy<Task<Quote>>>();
var created = 0;
await Concurrently.RunAsync(Requests, () => lazies.GetOrAdd("MSFT", symbol =>
{
    Interlocked.Increment(ref created);
    return new Lazy<Task<Quote>>(() => LoadAsync(provider, symbol));
}).Value);
```

```text
── Cache Lazy<Task<T>>: GetOrAdd may build several Lazy, only the published one runs
  Lazy instances created           16
  Provider calls                   1
```

The `GetOrAdd` factory still runs 16 times, but now it builds a `Lazy`, which starts nothing.
Everyone gets the published `Lazy`, and its `Value` runs the loader only once: the `Lazy<T>(Func<T>)` constructor uses `LazyThreadSafetyMode.ExecutionAndPublication`, meaning that a single thread runs the factory and the others receive its result.

The takeaway: cache the promise, not the value, and create the promise so that creating it doesn't start the work. A `Task` has already started; a `Lazy<Task<T>>` hasn't.

## From `Lazy<Task<T>>` to `AsyncLazy<T>`

The fix from 2.1 is the starting point.
`Lazy<Task<T>>` guarantees a single call, but the `Task` it holds is created by the first caller and keeps its outcome:

| Scenario | The problem with `Lazy<Task<T>>` | What `AsyncLazy<T>` does |
|---|---|---|
| ★ 2.2 | a failure stays cached forever | if the load fails, it puts a fresh `Lazy` in its place |
| ★ 2.3 | the first caller's token cancels the load for everyone, and the cancellation stays cached | the load takes no token; each caller cancels only its own wait with `WaitAsync(ct)` |
| 2.4 | the shared load runs in the first caller's context (`AsyncLocal`) | it starts the load with `Task.Run`, without flowing the `ExecutionContext` |

In all three scenarios the fixed variant uses the same class: each scenario shows the piece that fixes its trap, and the complete code is in the section after scenario 2.4.

## ★ 2.2 `failure-caching` — the failure stays cached

In [FailureCachingDemo.cs](FailureCachingDemo.cs) the provider fails once and then recovers.
Five sequential requests, with a 50 ms pause between one and the next, ask for the same value.

```csharp
var provider = new FakeQuoteProvider { Latency = TimeSpan.FromMilliseconds(100) };
provider.FailNext(1);
// …
var lazy = new Lazy<Task<Quote>>(() => provider.GetQuoteAsync("MSFT"));
getQuote = () => lazy.Value;
```

```text
✘ request 1: Provider failed for MSFT (call #1)
✘ request 2: Provider failed for MSFT (call #1)
✘ request 3: Provider failed for MSFT (call #1)
✘ request 4: Provider failed for MSFT (call #1)
✘ request 5: Provider failed for MSFT (call #1)

── Results
  Failed requests                  5/5
  Provider calls                   1
  Provider failures                1
```

The `Lazy` factory didn't throw: it returned a `Task`, which failed 100 ms later.
To the `Lazy` that's a value like any other, and it stays there as long as the `Lazy` lives.
The provider recovered after the first call, but the cache doesn't notice: all five requests get the error from call #1, and the provider is never called again.

Not even `LazyThreadSafetyMode.PublicationOnly`, which doesn't cache exceptions, would help: here the factory doesn't throw.
And `PublicationOnly` lets the factory run on several threads at once, which brings back the stampede of 2.1.

**Fixed.** In `AsyncLazy`, if the load fails, the `catch` replaces the `Lazy` before rethrowing:

```csharp
async Task<T> LoadAsync(Lazy<Task<T>> instance)
{
    try
    {
        return await factory();
    }
    catch
    {
        // Don't cache the failure: swap in a fresh Lazy, unless someone already did.
        Interlocked.CompareExchange(ref current, CreateInstance(), instance);
        throw;
    }
}
```

```text
✘ request 1: Provider failed for MSFT (call #1)
✔ request 2: MSFT = 281.34
✔ request 3: MSFT = 281.34
…
── Results
  Failed requests                  1/5
  Provider calls                   2
  Provider failures                1
```

Whoever was waiting on the failed load gets the error: request 1 fails anyway, because `AsyncLazy` doesn't retry.
Request 2 finds a fresh `Lazy` and starts the second call, which succeeds this time; requests 3–5 find the value in the cache.
The `CompareExchange` replaces the `Lazy` only if `current` is still the one that failed: an old load doesn't throw away the new one.

The takeaway: a cached `Task` caches its outcome, whatever it is. Decide which outcomes to keep, usually only success. Retrying is a separate decision, to be made above the cache.

## ★ 2.3 `shared-cancellation` — the first caller's token cancels everyone

In [SharedCancellationDemo.cs](SharedCancellationDemo.cs) three clients ask for MSFT together: `web-1` disconnects after 50 ms, `web-2` and `web-3` wait.
Then `web-4` arrives, for the same key.

```csharp
var cache = new ConcurrentDictionary<string, Lazy<Task<Quote>>>();
getQuote = ct => cache.GetOrAdd("MSFT",
    symbol => new Lazy<Task<Quote>>(() => provider.GetQuoteAsync(symbol, ct))).Value;
```

The lambda captures the `ct` of whoever creates the `Lazy`, that is `web-1`, which arrives first.

```text
     1 ms [t 19] provider → MSFT (call #1)
    63 ms [t 19] provider ⊘ MSFT (call #1) cancelled
✘ web-2: cancelled — but it never asked to!
✘ web-3: cancelled — but it never asked to!
! web-1: cancelled (client disconnected)

── A minute later, a new request for the same key
✘ web-4: cancelled — but it never asked to!

── Results
  Patient callers served           0/2
  Later request served             False
  Provider calls / cancelled       1 / 1
```

When `web-1` disconnects, its token cancels the provider call and the shared `Task` ends up `Canceled`.
`web-2` and `web-3` were awaiting that `Task`: they get an `OperationCanceledException` they never asked for.
And the cancelled `Task` stays in the cache: `web-4`, which arrives later with a token that was never cancelled, gets cancelled immediately, without any call to the provider.

**Fixed.** The factory receives nobody's token, and each caller passes its own to `GetValueAsync`:

```csharp
var cache = new ConcurrentDictionary<string, AsyncLazy<Quote>>();
getQuote = ct => cache.GetOrAdd("MSFT",
    symbol => new AsyncLazy<Quote>(() => provider.GetQuoteAsync(symbol))).GetValueAsync(ct);
```

In `AsyncLazy` the caller's token reaches only `WaitAsync`:

```csharp
public Task<T> GetValueAsync(CancellationToken cancellationToken = default) =>
    Volatile.Read(ref current).Value.WaitAsync(cancellationToken);
```

```text
     0 ms [t 17] provider → MSFT (call #1)
! web-1: cancelled (client disconnected)
   200 ms [t 19] provider ← MSFT = 281.34 (call #1)
✔ web-2: MSFT = 281.34
✔ web-3: MSFT = 281.34

── A minute later, a new request for the same key
✔ web-4: MSFT = 281.34

── Results
  Patient callers served           2/2
  Later request served             True
  Provider calls / cancelled       1 / 0
```

`WaitAsync(ct)` returns a `Task` that completes when the shared `Task` completes, or when the token is cancelled: cancelling it ends that caller's wait, not the load.
How it works inside, and when it isn't enough: [deep dive on `WaitAsync`](../../docs/wait-async.md).
`web-1` stops waiting, the load goes on for the others and completes at 200 ms, and `web-4` finds the value in the cache: a single call, none cancelled.
The lesson of 2.1 still holds: `GetOrAdd` may build several `AsyncLazy` instances, but the constructor starts nothing.

The takeaway: in shared work, cancellation has to be split in two. The caller's token cancels its own wait; the work has a lifetime of its own, which doesn't depend on who arrived first.
The price: if every caller leaves, the load keeps going anyway. In scenario 2.5, `HybridCache` makes the opposite choice.

## 2.4 `factory-context` — the shared load in the first caller's context

In [FactoryContextDemo.cs](FactoryContextDemo.cs) two requests, `req-A` and `req-B`, each have their own correlation id in an `AsyncLocal` and ask for the same value.
The shared loader logs the correlation id it sees when it starts and when it completes.

```csharp
static readonly AsyncLocal<string?> CorrelationId = new();
// …
var lazy = new Lazy<Task<Quote>>(() => LoadAsync(provider));
getQuote = () => lazy.Value;
// …
await Task.WhenAll(RequestAsync("req-A", getQuote), RequestAsync("req-B", getQuote));

static async Task RequestAsync(string correlationId, Func<Task<Quote>> getQuote)
{
    CorrelationId.Value = correlationId;
    Out.Log($"{correlationId}: request started");
    var quote = await getQuote();
    Out.Log($"{correlationId}: MSFT = {quote.Price}");
}
```

```text
     0 ms [t 19] req-A: request started
✘ shared load started — correlation id: req-A [t19]
     1 ms [t 19] req-B: request started
✘ shared load completed — correlation id: req-A [t19]
   203 ms [t 19] req-A: MSFT = 281.34
   203 ms [t 17] req-B: MSFT = 281.34
```

`Lazy` runs the factory synchronously, on the thread of whoever calls `Value` first and inside that caller's `ExecutionContext`.
So `LoadAsync` starts with `req-A`'s correlation id, and the `await` inside it carries the same context over to the continuation.
The load serves `req-B` too, but its logs, and any other `AsyncLocal` of the first request (tenant, `Activity` for tracing), tell a story in which it belongs to `req-A` alone.

**Fixed.** `AsyncLazy` starts the load on the thread pool with `ExecutionContext` flow suppressed:

```csharp
Task<T> StartLoad(Lazy<Task<T>> instance)
{
    // The shared load belongs to no caller: don't let the first caller's AsyncLocal values flow into it.
    using (ExecutionContext.SuppressFlow())
        return Task.Run(() => LoadAsync(instance));
}
```

```text
     0 ms [t 17] req-A: request started
     0 ms [t 17] req-B: request started
✔ shared load started — correlation id: (none), it belongs to no request [t17]
✔ shared load completed — correlation id: (none), it belongs to no request [t17]
   202 ms [t 17] req-A: MSFT = 281.34
   202 ms [t 17] req-B: MSFT = 281.34
```

With flow suppressed, `Task.Run` doesn't capture the caller's `ExecutionContext`: the load starts with an empty context, and the `await`s inside it carry that one along.
`SuppressFlow` returns an `AsyncFlowControl` that must be disposed on the same thread that created it: that's why the `using` block contains only the synchronous call to `Task.Run`, with no `await`.

`Task.Run` also has a second effect: the factory's synchronous prefix no longer runs on the first caller's thread, inside `Value`.
In the fixed output, `req-B` starts before the load begins.

The takeaway: whoever runs the shared work decides its context. Shared work belongs to no request, so it has to be started without the context of whoever triggered it.
It's the same rule as for background work started during a request: see scenario 4.2 in [Demo 4](../04-AsyncContext/README.md).

## The complete `AsyncLazy<T>`

The three fixes together, from [AsyncLazy.cs](AsyncLazy.cs):

```csharp
public sealed class AsyncLazy<T>
{
    readonly Func<Task<T>> factory;
    Lazy<Task<T>> current;

    public AsyncLazy(Func<Task<T>> factory)
    {
        this.factory = factory;
        current = CreateInstance();
    }

    public Task<T> GetValueAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref current).Value.WaitAsync(cancellationToken);

    Lazy<Task<T>> CreateInstance()
    {
        Lazy<Task<T>>? instance = null;
        instance = new Lazy<Task<T>>(() => StartLoad(instance!));
        return instance;
    }

    Task<T> StartLoad(Lazy<Task<T>> instance)
    {
        // The shared load belongs to no caller: don't let the first caller's AsyncLocal values flow into it.
        using (ExecutionContext.SuppressFlow())
            return Task.Run(() => LoadAsync(instance));
    }

    async Task<T> LoadAsync(Lazy<Task<T>> instance)
    {
        try
        {
            return await factory();
        }
        catch
        {
            // Don't cache the failure: swap in a fresh Lazy, unless someone already did.
            Interlocked.CompareExchange(ref current, CreateInstance(), instance);
            throw;
        }
    }
}
```

- There is still a `Lazy<Task<T>>` inside, so the guarantee of 2.1 holds: building an `AsyncLazy` starts nothing, and each `Lazy` runs the load only once.
- `CreateInstance` hands each `Lazy` a reference to itself: when a load fails, the `catch` knows which `Lazy` to remove.
- The `catch` without a filter treats errors and cancellations the same way: neither one stays in the cache.
- What it doesn't do: no expiration or invalidation, so the value stays as long as the instance lives. And if every caller cancels its wait, the load goes on and the result stays in the cache.

## 2.5 `hybrid-cache` — the same questions, asked of HybridCache

[HybridCacheDemo.cs](HybridCacheDemo.cs) uses `HybridCache` from `Microsoft.Extensions.Caching.Hybrid` (11.0.0 here, see [Directory.Packages.props](../../Directory.Packages.props)), which shipped with .NET 9.
It's registered without a distributed cache, so it works in memory only.
There are no broken/fixed variants: the scenario asks `HybridCache` the questions that building `AsyncLazy` taught us to ask.

```csharp
var services = new ServiceCollection();
services.AddHybridCache();
await using var serviceProvider = services.BuildServiceProvider();
var cache = serviceProvider.GetRequiredService<HybridCache>();
// …
Func<CancellationToken, Task<Quote>> Get(string symbol) => ct =>
    cache.GetOrCreateAsync($"quote:{symbol}", token => new ValueTask<Quote>(provider.GetQuoteAsync(symbol, token)),
        cancellationToken: ct).AsTask();
```

The factory receives its own token (`token`), separate from the caller's (`ct`).

```text
── 200 concurrent requests on a cold key
  Provider calls                   1

── One caller disconnects after 50 ms, two wait
   287 ms [t 15] provider → AAPL (call #1)
! web-1: cancelled (client disconnected)
   501 ms [t 13] provider ← AAPL = 192.28 (call #1)
✔ web-2: AAPL = 192.28
✔ web-3: AAPL = 192.28
  Patient callers served           2/2
  Provider calls / cancelled       1 / 0
  → one caller leaving cancels only its own wait, like AsyncLazy + WaitAsync

── Every caller disconnects (50 ms and 80 ms)
   503 ms [t 15] provider → GOOG (call #1)
! web-1: cancelled (client disconnected)
! web-2: cancelled (client disconnected)
   595 ms [t 15] provider ⊘ GOOG (call #1) cancelled
  Provider calls / cancelled       1 / 1
  → when the last caller leaves, HybridCache cancels the shared load (AsyncLazy lets it finish)

── A new request for the same key
   861 ms [t 12] provider → GOOG (call #2)
  1063 ms [t 12] provider ← GOOG = 656.25 (call #2)
✔ web-3: GOOG = 656.25
  Provider calls / cancelled       2 / 1
  → and the cancelled load was not cached: a new call
```

- **Stampede**: 200 requests on a cold key, 1 provider call. That's the result of 2.1 fixed, without writing anything.
- **One caller leaves**: same as `AsyncLazy` with `WaitAsync`. `web-1` stops waiting, the load goes on for `web-2` and `web-3`, and no call is cancelled.
- **Everyone leaves**: here `HybridCache` behaves differently from `AsyncLazy` and cancels the shared load. Not when the first caller leaves (50 ms), but after the last one does (80 ms): the call is cancelled at 595 ms, 92 ms after it started. The documentation puts it this way: the token passed to the factory is cancelled when all the callers waiting on the shared operation have cancelled.
- **A later request**: the cancellation wasn't cached, so `web-3` starts a new call (#2). That's the trap of 2.3, avoided.

Both choices for the "everyone leaves" case are reasonable: `AsyncLazy` never throws away a call that has already started and warms the cache for whoever comes next, while `HybridCache` saves work that nobody is waiting for anymore.

The takeaway: building `AsyncLazy` by hand teaches you which questions to ask a library. How many times does the factory run? What stays in the cache if it fails or gets cancelled? What does the caller's token cancel?

## What to know

- **Cache the `Task`, created with `Lazy`.** `GetOrAdd` calls the factory outside the locks and, with concurrent calls, possibly more than once: if the factory starts work, it starts it more than once. With `Lazy<Task<T>>`, creating the value costs nothing, and only the published `Lazy` runs.
- **Decide which outcomes to keep.** To any cache of `Task`s, a failed or cancelled `Task` is as valid a value as any other. Usually only success should be kept.
- **Shared work belongs to no caller**: no token and no `ExecutionContext` from whoever happens to come first. Each caller cancels its own wait with `WaitAsync(ct)`, available since .NET 6.
- **`HybridCache` for data with a key and an expiration**, like QuoteHub's reference data: stampede protection, expiration, an optional distributed cache (Redis, SQL Server…), tag-based invalidation. `AsyncLazy<T>` is still useful for a single value that lives as long as its owner and never expires.
- **`HybridCache` stampede protection is per instance**: it doesn't extend to other instances, even when they share the same distributed cache. With several servers behind a load balancer, a cold key can cost one call per server.
- **Shared instances.** `AsyncLazy<T>` gives every caller the same object, which therefore must be immutable or thread-safe. By default `HybridCache` behaves like `IDistributedCache` and doesn't reuse instances; to reuse them, the type must be `sealed` and marked `[ImmutableObject(true)]`.

## References

- [HybridCache library in ASP.NET Core](https://learn.microsoft.com/aspnet/core/performance/caching/hybrid)
- [ConcurrentDictionary.GetOrAdd — the factory and concurrency](https://learn.microsoft.com/dotnet/api/system.collections.concurrent.concurrentdictionary-2.getoradd)
- [Stephen Toub — `AsyncLazy<T>`](https://devblogs.microsoft.com/pfxteam/asynclazyt/)
- [Stephen Toub — ExecutionContext vs SynchronizationContext](https://devblogs.microsoft.com/dotnet/executioncontext-vs-synchronizationcontext/)
