# Demo 4 — Context and lifetime: AsyncLocal and IAsyncDisposable

**English** · [Italiano](README-it.md)

Async code doesn't carry only its parameters: it also carries a **context**.
Every logical flow has an `ExecutionContext`, and `AsyncLocal<T>` values live inside it: that's how a tenant and a correlation id follow a request across `await`s, the thread pool and continuations without appearing in any signature.
The rule fits in one line and is easy to get wrong: context flows down into the work you start, never back up to whoever called you, and it stays attached to everything that captured it, even long after the request has ended.

The other half of the Demo is about resources with an async lifetime.
An object that has to do I/O to close implements `IAsyncDisposable`, and whoever owns it must dispose it asynchronously and wait until it has really finished.

In QuoteHub these are three everyday situations: multi-tenant requests that keep the tenant in an `AsyncLocal`, a singleton that refreshes prices in the background, and a publisher that sends quotes to the bus in batches and must drain its buffer on shutdown.
These are bugs that tests rarely catch: inside the method the value is there, the logs do have a tenant (the wrong one), and shutdown can complete without errors and still lose the last batch.

## How to run

```bash
dotnet run --project demos/04-AsyncContext -- 4.1                    # broken, Enter, fixed
dotnet run --project demos/04-AsyncContext -- context-leak --fixed
dotnet run --project demos/04-AsyncContext -- all --no-pause         # all scenarios, both variants
```

In the IDE there are the `menu`, `4.1 asynclocal-flow`, `4.2 context-leak` and `4.3 async-dispose` profiles; from the terminal you pick the same profile with `--launch-profile "4.1 asynclocal-flow"`.

**Runtime Async on/off.** Like the rest of the repo, the Demo builds with Runtime Async enabled ([Directory.Build.targets](../../Directory.Build.targets)). For the compiler's state machines:

```bash
dotnet run --project demos/04-AsyncContext -p:RuntimeAsync=false -- 4.1
```

Scenario 4.1 prints in its banner the model the assembly was compiled with: `runtime async, .NET 11.0.0` or `compiler state machines, .NET 11.0.0`.
The output below comes from .NET 11.0.0 in Debug. Apart from the banner and the milliseconds, the output of all three scenarios is identical under both models: Runtime Async changes how an async method is executed, not how context flows.

## ★ 4.1 `asynclocal-flow` — the value doesn't flow back to the caller

The request's tenant lives in an `AsyncLocal<string?>` ([AsyncLocalFlowDemo.cs](AsyncLocalFlowDemo.cs)).
The broken variant sets it inside an async method in two ways: after an `await`, as a lookup would, and before any `await`.

```csharp
static readonly AsyncLocal<string?> Tenant = new();

// …

static async Task SetTenantAsync(string tenant)
{
    await Task.Delay(10); // e.g. a lookup
    Tenant.Value = tenant;
    Out.Log($"inside SetTenantAsync: tenant = {Tenant.Value}");
}

static async Task SetTenantBeforeAnyAwaitAsync(string tenant)
{
    Tenant.Value = tenant;
    Out.Log($"inside SetTenantBeforeAnyAwaitAsync: tenant = {Tenant.Value}");
    await Task.Delay(10);
}
```

The caller clears the value, calls the method and reads it back:

```csharp
Tenant.Value = null;
await SetTenantAsync("acme");
Out.Metric("after await SetTenantAsync()", Tenant.Value ?? "(null)", ok: false);
```

```text
runtime async, .NET 11.0.0
    21 ms [t  5] inside SetTenantAsync: tenant = acme
  after await SetTenantAsync()     (null)
    21 ms [t  5] inside SetTenantBeforeAnyAwaitAsync: tenant = acme
  set before its first await       (null)
```

Inside the method the value is there; the caller gets `(null)`.
Even in the second case, where the assignment happens in the synchronous part of the method, before it suspends.

**Why.** An async method works on a copy of the context (copy-on-write): whatever it sets applies to the method itself and to the work it starts from that point on, and is gone when control returns to the caller.
With state machines the builder guarantees this: `AsyncTaskMethodBuilder.Start` saves the current `ExecutionContext`, runs the synchronous part of the method and then restores it, precisely to keep ambient data from leaking out of the method into the caller.
That's why the assignment made before the first `await` counts too.
With Runtime Async the method no longer goes through `Start` (see [Demo 0](../00-RuntimeAsync/README.md)), but the semantics are the same, line for line:

```text
compiler state machines, .NET 11.0.0
    30 ms [t  5] inside SetTenantAsync: tenant = acme
  after await SetTenantAsync()     (null)
    31 ms [t  5] inside SetTenantBeforeAnyAwaitAsync: tenant = acme
  set before its first await       (null)
```

A synchronous method, on the other hand, has no such boundary: it writes directly into its caller's context.

**Fixed.** Three ways, depending on who needs to know the value.

A synchronous setter, which doesn't open a copy of the context:

```csharp
static void SetTenant(string tenant) => Tenant.Value = tenant;
```

The async method returns the value and the caller assigns it, in its own context:

```csharp
Tenant.Value = await LookupTenantAsync("acme");
```

Or a mutable holder: the `AsyncLocal` is set only once, at the start of the request, and async code mutates the object instead of replacing the value.

```csharp
Holder.Value = new TenantHolder(); // set once, at the start of the request
await ResolveIntoHolderAsync("acme");

// …

static async Task ResolveIntoHolderAsync(string tenant)
{
    await Task.Delay(10);
    Holder.Value!.Name = tenant; // mutate the shared object, don't replace the AsyncLocal value
}
```

```text
runtime async, .NET 11.0.0
  after SetTenant() (sync)         acme
  caller assigns the result        acme
  holder mutated by async code     acme
```

The third one works because every copy of the context points to the same holder instance: the mutation is visible to all of them.
It's for the same reason that `IHttpContextAccessor` keeps a mutable holder in its `AsyncLocal`.
The flip side: that instance is also visible to all the work the request starts, including work that outlives it (see 4.2).

The takeaway: `AsyncLocal` flows down, never back up. If an async method has to produce context for its caller, it returns it; otherwise the context is established up front, at the entry point of the flow.

## ★ 4.2 `context-leak` — the background keeps the first request's tenant

`PriceRefresher` ([ContextLeakDemo.cs](ContextLeakDemo.cs)) is a singleton with a loop that refreshes the price cache every 150 ms.
It starts lazily, inside the first request that needs it: `EnsureStarted` starts the loop and registers a callback on the shutdown token that flushes the subscriptions.

```csharp
static async Task RequestAsync(string tenant, PriceRefresher refresher, CancellationToken shutdown)
{
    Tenant.Value = tenant;
    refresher.EnsureStarted(shutdown);
    await Task.Delay(50);
    Out.Log($"[tenant={Tenant.Value}] request: served quotes");
}

// …

async Task LoopAsync()
{
    while (!stop.IsCancellationRequested)
    {
        await Task.Delay(150, stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!stop.IsCancellationRequested)
            Background("refreshing price cache");
    }
}
```

Three requests, three tenants: `acme` wakes the singleton up, then `globex` and `initech` in parallel.
`Background` reads `Tenant.Value` and counts the lines written with a tenant: background work shouldn't have any.
In the broken variant `EnsureStarted` does what comes naturally:

```csharp
loop = Task.Run(LoopAsync, stop.Token);
shutdown.Register(() => Background("flushing subscriptions on shutdown"));
```

```text
    62 ms [t  4] [tenant=acme] request: served quotes
   124 ms [t  4] [tenant=initech] request: served quotes
   125 ms [t  6] [tenant=globex] request: served quotes
✘ [tenant=acme] background: refreshing price cache
✘ [tenant=acme] background: refreshing price cache
✘ [tenant=acme] background: refreshing price cache
   536 ms [t  6] application stopping
✘ [tenant=acme] background: flushing subscriptions on shutdown
  Background lines tagged 'acme'   4/4
```

Each request sees its own tenant, even the two concurrent ones: each one works on its own copy of the context, as in 4.1.
The background, however, is `acme` forever.

**Why.** `Task.Run` captures the current `ExecutionContext`, the one from `acme`'s request, and runs the delegate in it.
The loop lives in that context until the application stops, and every `await` inside the loop resumes in the same context.
`CancellationToken.Register` does the same: it captures the context at registration time and uses it for the callback, which runs at shutdown and has nothing to do with `acme`.
The result is wrong logs, metrics attributed to the wrong tenant, and that request's context kept alive as long as the loop, along with everything its `AsyncLocal`s point to.
And which tenant "wins" depends on which request arrives first after startup.

The same goes for a `Timer` created inside a request.
In general, almost every API that starts asynchronous work captures the context, `ThreadPool.QueueUserWorkItem` included; the exceptions are the ones with the `Unsafe` prefix, which by design don't flow it.

**Fixed.** Work that outlives the request starts without the request's context.

```csharp
if (suppressFlow)
{
    // Nothing of the current request flows into work that outlives it.
    using (ExecutionContext.SuppressFlow())
        loop = Task.Run(LoopAsync, stop.Token);
    shutdown.UnsafeRegister(_ => Background("flushing subscriptions on shutdown"), null);
}
```

- `Task.Run` has no `Unsafe` variant: you suppress the flow around the call. `ExecutionContext.SuppressFlow()` returns an `AsyncFlowControl`, and the `using` restores the flow as soon as the task has started.
- For the token there's `CancellationToken.UnsafeRegister`, which doesn't capture the `ExecutionContext` and doesn't flow it into the callback.
- The token passed to `Task.Run` is something else: `stop.Token`, the one the loop observes, and it only matters if it's cancelled before the loop starts. `Task.Run` registers on it with `UnsafeRegister`, so it doesn't capture the context, in either variant.

```text
    60 ms [t  4] [tenant=acme] request: served quotes
   122 ms [t  4] [tenant=initech] request: served quotes
   122 ms [t  8] [tenant=globex] request: served quotes
   155 ms [t  8] [tenant=(null)] background: refreshing price cache
   311 ms [t  8] [tenant=(null)] background: refreshing price cache
   468 ms [t  8] [tenant=(null)] background: refreshing price cache
   532 ms [t  8] application stopping
   532 ms [t  8] [tenant=(null)] background: flushing subscriptions on shutdown
  Background lines tagged 'acme'   0/4
```

The loop starts without the request's context: if it needs a context of its own, it establishes one explicitly.

The takeaway: anything that lives longer than the request (loops, timers, shutdown callbacks, shared caches) must not be born in its context.
The same problem, with a shared cache instead of a loop, is scenario 2.4 in [Demo 2](../02-AsyncCaching/README.md): the `AsyncLazy` factory runs in the first caller's context, and there too the fix is `SuppressFlow`.

## 4.3 `async-dispose` — the shutdown that loses the last batch

[BatchingPublisher.cs](BatchingPublisher.cs) publishes quotes downstream in batches of 10: a `Channel` for input and a loop that accumulates and flushes.
Whatever is left in the buffer has to be written on shutdown, and writing is I/O: the type implements only `IAsyncDisposable`.

```csharp
async Task LoopAsync()
{
    var batch = new List<Quote>(BatchSize);
    await foreach (var quote in input.Reader.ReadAllAsync())
    {
        batch.Add(quote);
        if (batch.Count == BatchSize)
            await FlushAsync(batch);
    }

    // Input completed: flush the remainder.
    if (batch.Count > 0)
        await FlushAsync(batch);
}
```

In [AsyncDisposeDemo.cs](AsyncDisposeDemo.cs) the publisher is a scoped service. The request publishes 25 quotes and keeps going for another 300 ms, during which the two full batches are flushed; then the scope is disposed.
The last 5 depend entirely on the dispose. The broken variant loses them in two ways.

**Synchronous dispose of the scope.**

```csharp
using var scope = services.CreateScope();
publisher = scope.ServiceProvider.GetRequiredService<BatchingPublisher>();
await HandleRequestAsync(publisher);
```

```text
── Sync dispose of the DI scope
    19 ms [t  8] request: 25 quotes published
    77 ms [t  8] publisher: flushed a batch of 10
   139 ms [t  7] publisher: flushed a batch of 10
   325 ms [t  7] request: done, disposing the scope
✘ 'BeyondAsyncAwait.Demo04.BatchingPublisher' type only implements IAsyncDisposable. Use DisposeAsync to dispose the container.
  Published at shutdown            20/25
```

The scope's `Dispose()` finds a service that implements only `IAsyncDisposable` and throws `InvalidOperationException`.
The publisher's `DisposeAsync` never runs: the output has neither the batch of 5 nor the `publisher: disposed` line, and the last 5 stay in the buffer.
This is the risk the documentation points out for `IAsyncDisposable`-only types: if the consumer calls only `Dispose`, `DisposeAsync` is never called.

**A `DisposeAsync` that doesn't wait for the flush.** The scope is async now, but the publisher closes the input and returns right away:

```csharp
input.Writer.TryComplete();
if (!waitForFlush)
    return;
```

```text
── DisposeAsync that doesn't wait for the flush
   331 ms [t  7] request: 25 quotes published
   388 ms [t  7] publisher: flushed a batch of 10
   449 ms [t  7] publisher: flushed a batch of 10
   636 ms [t  7] request: done, disposing the scope
  Published at shutdown            20/25
   700 ms [t  7] publisher: flushed a batch of 5
! 25/25 only after DisposeAsync returned: in a real shutdown the process is already gone
```

`TryComplete` closes the input, the loop leaves the `foreach` and starts the last flush, but `DisposeAsync` has already returned.
The scope, the container and whatever drives the shutdown all consider the publisher closed while the last batch is still in flight: it lands at 700 ms, after the caller has read 20/25.
The demo waits 150 ms on purpose to show it; a process that is shutting down doesn't wait.

**Fixed.** An async scope all the way through the chain, and a `DisposeAsync` that returns only once the work is done.

```csharp
await using (var services = BuildServices(waitForFlush: true))
{
    await using var scope = services.CreateAsyncScope();
    publisher = scope.ServiceProvider.GetRequiredService<BatchingPublisher>();
    await HandleRequestAsync(publisher);
}

Out.Metric("Published at shutdown", $"{publisher.Published}/{Quotes}", publisher.Published == Quotes);
await publisher.DisposeAsync(); // a second dispose must be harmless
```

```csharp
public async ValueTask DisposeAsync()
{
    // Idempotent: DI containers, using blocks and shutdown hooks may all call it.
    if (Interlocked.Exchange(ref disposed, 1) == 1)
    {
        Out.Log("publisher: already disposed, nothing to do");
        return;
    }

    input.Writer.TryComplete();
    // …

    // Wait for the last flush; a failed flush is logged, it must not throw out of DisposeAsync.
    await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    if (loop.IsFaulted)
        Out.Bad($"publisher: last flush failed: {loop.Exception!.InnerException!.Message}");
    Out.Log("publisher: disposed, buffer flushed");
}
```

```text
     0 ms [t  7] request: 25 quotes published
    61 ms [t  8] publisher: flushed a batch of 10
   123 ms [t  8] publisher: flushed a batch of 10
   310 ms [t  8] request: done, disposing the scope
   372 ms [t  8] publisher: flushed a batch of 5
   372 ms [t  8] publisher: disposed, buffer flushed
  Published at shutdown            25/25
   373 ms [t  8] publisher: already disposed, nothing to do
```

- `CreateAsyncScope` with `await using`: the scope is disposed with `DisposeAsync`, which reaches all the way to the publisher. The `ServiceProvider` is in an `await using` too.
- `DisposeAsync` completes the input and then **waits for the loop**: the last flush and `disposed, buffer flushed` land in the same millisecond, before the caller reads 25/25.
- `ConfigureAwaitOptions.SuppressThrowing`: a failed flush doesn't escape `DisposeAsync` as an exception; the code checks `loop.IsFaulted` and logs it.
- Idempotent: the second `DisposeAsync` finds the flag already set by `Interlocked.Exchange` and does nothing.

The takeaway: a resource that closes asynchronously needs async owners all the way through the chain (`await using`, `CreateAsyncScope`, the container disposed with `DisposeAsync`) and a `DisposeAsync` that returns only when the work it shuts down has really finished.
Implementing only `IAsyncDisposable` is legitimate when a synchronous close isn't possible, and here it also has a useful side effect: the synchronous dispose still loses the batch, but with an exception rather than silently.

How `await using` works, who implements `IAsyncDisposable` and how to write one: [deep dive on `IAsyncDisposable`](../../docs/async-disposable.md).

## What to know

- **`AsyncLocal` flows down, never back up.** An async method that has to produce context returns it. Context is set at the entry point of the flow, in synchronous code or before calling the async method.
- **A mutable holder** is set once, at the start of the flow. Every copy of the context sees the same instance, including the work the request starts.
- **Context is captured by** `Task.Run`, `ThreadPool.QueueUserWorkItem`, `Timer`, `CancellationToken.Register`, and every `await` flows it to the continuation. For work that outlives the request: `ExecutionContext.SuppressFlow()` around the start, or the `Unsafe*` variants (`CancellationToken.UnsafeRegister`, `ThreadPool.UnsafeQueueUserWorkItem`).
- **`ConfigureAwait(false)` has nothing to do with it**: it concerns the `SynchronizationContext`, not the `ExecutionContext`. There is no way, at the `await` level, to keep the `ExecutionContext` from flowing.
- **An `IAsyncDisposable`-only type** requires all of its owners to be async: `await using`, `CreateAsyncScope()`, the `ServiceProvider` disposed with `DisposeAsync`.
- **`DisposeAsync`** must be idempotent, wait for the work it shuts down, and not throw for a failed flush. See the [deep dive on `IAsyncDisposable`](../../docs/async-disposable.md).
- **Runtime Async changes none of this**: all three scenarios produce the same output with `-p:RuntimeAsync=false`.

## References

- [Stephen Toub — ExecutionContext vs SynchronizationContext](https://devblogs.microsoft.com/dotnet/executioncontext-vs-synchronizationcontext/)
- [Stephen Toub — How Async/Await Really Works in C#](https://devblogs.microsoft.com/dotnet/how-async-await-really-works/) (saving and restoring the `ExecutionContext` in `Start`)
- [`AsyncLocal<T>` Class](https://learn.microsoft.com/dotnet/api/system.threading.asynclocal-1)
- [Implement a DisposeAsync method](https://learn.microsoft.com/dotnet/standard/garbage-collection/implementing-disposeasync)
