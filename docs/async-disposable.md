# `IAsyncDisposable`: when closing has to wait

**English** · [Italiano](async-disposable-it.md)

`IAsyncDisposable` (since .NET Core 3.0 and C# 8) is the async counterpart of `IDisposable`: a single method, `DisposeAsync`, that returns a `ValueTask`.
It exists for resources whose closing has to wait for something: a buffer to flush, offsets to commit, a background loop or a callback to finish.
With `IDisposable` that wait either blocks a thread or doesn't happen at all.

It shows up in several places in this repo:
- in scenario 1.2 of [Demo 1](../demos/01-TaskCompletionSource/README.md) the `CancellationTokenRegistration`s are disposed with `await using`;
- in scenario 4.3 of [Demo 4](../demos/04-AsyncContext/README.md) it's the whole point: a publisher, inside a DI scope, that has to flush its last batch on shutdown;
- in scenarios 6.6 and 6.7 of [Demo 6](../demos/06-RxInterop/README.md) the AsyncRx subscriptions are closed with `DisposeAsync`, which returns only once the teardown has finished.

## The API

```csharp
public interface IAsyncDisposable
{
    ValueTask DisposeAsync();
}
```

It returns a `ValueTask` because most of the time there's nothing to wait for: the object is already disposed, the buffer is empty, the callback isn't running.
In those cases `DisposeAsync` completes synchronously and returns `default`, with no allocation.

## What `await using` becomes

```csharp
await using (var publisher = new BatchingPublisher(waitForFlush: true))
{
    await publisher.PublishAsync(quote);
}
```

The compiler turns it, more or less, into a `try`/`finally` that awaits `DisposeAsync`:

```csharp
var publisher = new BatchingPublisher(waitForFlush: true);
try
{
    await publisher.PublishAsync(quote);
}
finally
{
    if (publisher != null)
        await publisher.DisposeAsync();
}
```

What follows from it, each point checked on .NET 11:

- **The declaration form** `await using var x = …;` disposes at the end of the enclosing block; with several declarations, in reverse order.
- **If the type implements both interfaces**, `await using` calls only `DisposeAsync`, and `using` only `Dispose`. If it implements only `IAsyncDisposable`, `using` doesn't compile: *CS8418 … Did you mean 'await using' rather than 'using'?*
- **It goes by pattern**: a `DisposeAsync()` method that returns something awaitable is enough, even without the interface. That's what makes `ConfigureAwait` work (below).
- **A `null` resource is skipped**, as with `using`.
- **If both the body and `DisposeAsync` throw**, the exception that comes out is the one from `DisposeAsync`: the body's is lost, as with any exception thrown in a `finally`.

### `ConfigureAwait`

`await using` awaits `DisposeAsync` like any other `await`, capturing the `SynchronizationContext` if there is one.
In library code:

```csharp
var publisher = new BatchingPublisher(waitForFlush: true);
await using (publisher.ConfigureAwait(false))
{
    await publisher.PublishAsync(quote).ConfigureAwait(false);
}
```

`ConfigureAwait(this IAsyncDisposable, bool)` returns a `ConfiguredAsyncDisposable`: a struct that doesn't implement `IAsyncDisposable`, but has a `DisposeAsync()` that returns a `ConfiguredValueTaskAwaitable`, and `await using` accepts it by pattern.
That's why the resource is declared first: with `await using var x = publisher.ConfigureAwait(false);`, `x` would be the wrapper, not the publisher.
The demos are console apps, with no `SynchronizationContext`, and don't need it.

## Who implements it, and what it waits for

| Type | `Dispose()` | `DisposeAsync()` |
|---|---|---|
| `CancellationTokenRegistration` | unregisters the callback; if the callback is running on another thread, **blocks** until it finishes | same, but returns a `ValueTask` that completes when the callback finishes |
| `Timer` | stops the timer and returns: a callback already running carries on | completes when the callbacks already queued or running have finished |
| `FileStream`, `BufferedStream` | writes the buffer and closes, blocking the thread | `FlushAsync`, then closes |
| `Stream` (base class) | closes | calls `Dispose()` and returns: it's async only where a derived class overrides it |
| DI scope and `ServiceProvider` | disposes the services in reverse order; a service that only implements `IAsyncDisposable` → `InvalidOperationException` | for each service, `DisposeAsync` if it has one, otherwise `Dispose` |
| `IAsyncEnumerator<T>` | — | `await foreach` calls it at the end, on `break` too: in an async iterator it runs the pending `finally` blocks |

`CancellationTokenSource` only implements `IDisposable`: nothing in its disposal has to wait.

`BufferedStream.DisposeAsync` ([BufferedStream.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/IO/BufferedStream.cs)) shows the shape of a good one: first the I/O, then the resource it owns, both awaited, and the state cleaned up in any case.

```csharp
public override async ValueTask DisposeAsync()
{
    try
    {
        if (_stream != null)
        {
            try
            {
                await FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
    finally
    {
        _stream = null;
        _buffer = null;
        _writePos = 0;
    }
}
```

## Writing one

`BatchingPublisher` in Demo 4 ([BatchingPublisher.cs](../demos/04-AsyncContext/BatchingPublisher.cs)) accumulates quotes in batches of 10 and flushes them from a background loop:

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

Four rules come out of it:

- **Return when the work is over.** Completing the input starts the last flush, but doesn't wait for it: the broken variant of 4.3 returns first, and the last batch lands after whoever was shutting down has moved on. Awaiting `loop` is what makes `await using` mean "closed".
- **Idempotent.** The DI scope, an `await using` and a shutdown hook may all call it: the second call must find nothing left to do. Here, a flag set with `Interlocked.Exchange`.
- **Don't throw for an expected failure.** An exception from `DisposeAsync` replaces the one from the body. `ConfigureAwaitOptions.SuppressThrowing` (.NET 8) waits for the loop without rethrowing its exception; the code checks `IsFaulted` and logs it.
- **Only `IAsyncDisposable`, or both?** Only async when there's no honest synchronous close, as here: a synchronous owner then fails loudly (the container's `InvalidOperationException`) instead of losing the batch silently. Both when the type also has synchronous consumers, accepting that `Dispose` blocks; in a class that isn't `sealed`, the documented pattern adds a `protected virtual ValueTask DisposeAsyncCore()` (see [Implement a DisposeAsync method](https://learn.microsoft.com/dotnet/standard/garbage-collection/implementing-disposeasync)).

## Async owners all the way up

A resource that closes asynchronously needs whoever owns it to close asynchronously too: the class that holds it in a field implements `IAsyncDisposable` in turn, the DI scope is created with `CreateAsyncScope()`, the container is disposed with `DisposeAsync`.
A synchronous link in the chain can only do one of three things, all wrong:

- **throw**: `scope.Dispose()` with a service that only implements `IAsyncDisposable` → `InvalidOperationException`, and the publisher's `DisposeAsync` never runs (4.3);
- **block**: `DisposeAsync().AsTask().GetAwaiter().GetResult()` is sync-over-async: a thread stuck waiting, and a deadlock if the continuation needs the very thread you're blocking (a single-threaded `SynchronizationContext`);
- **forget**: `_ = DisposeAsync()`, or a `void Dispose()` that starts the close and returns. That's the classic Rx teardown in 6.7: the offsets are committed 200 ms after `Dispose` has returned.

## Details worth knowing

- **`DisposeAsync` doesn't promise asynchrony.** The `Stream` base class calls `Dispose()` and returns an already completed `ValueTask`; on a `MemoryStream` it completes synchronously.
- **It's a `ValueTask`**: await it once, and don't keep it around. The rules are those of [Demo 3](../demos/03-ValueTask/README.md).
- **`CancellationTokenRegistration.Dispose` only waits in a race**: when `Cancel()` is running that very callback on another thread. Called from inside the callback itself, it doesn't wait: it would deadlock, and the source says it chooses to return instead.
- **An async iterator's enumerator can't be disposed while `MoveNextAsync` is pending**: `DisposeAsync` throws `NotSupportedException`. To stop a wait in progress you need the token (`WithCancellation`, `[EnumeratorCancellation]`).
- **Runtime Async changes none of this**: scenario 4.3 produces the same output with `-p:RuntimeAsync=false`.

## References

- [Implement a DisposeAsync method](https://learn.microsoft.com/dotnet/standard/garbage-collection/implementing-disposeasync)
- [`IAsyncDisposable` — API documentation](https://learn.microsoft.com/dotnet/api/system.iasyncdisposable)
- dotnet/runtime, release/11.0: [CancellationTokenRegistration.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/Threading/CancellationTokenRegistration.cs), [Timer.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/Threading/Timer.cs), [BufferedStream.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/IO/BufferedStream.cs), [ServiceProviderEngineScope.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/ServiceProviderEngineScope.cs)
