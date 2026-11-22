# `Task.WaitAsync`: stop waiting without stopping the work

**English** · [Italiano](wait-async-it.md)

`Task.WaitAsync` (since .NET 6) returns a new `Task` that stops waiting for the original one when a timeout expires or a token is cancelled.
It doesn't stop the operation behind it: it cancels the **wait**, not the **work**.

It shows up twice in this repo:
- in scenario 1.2 of [Demo 1](../demos/01-TaskCompletionSource/README.md) it's the shorter alternative to a `TaskCompletionSource` completed by hand on timeout and cancellation;
- in scenario 2.3 of [Demo 2](../demos/02-AsyncCaching/README.md) it's the fix: each caller cancels its own wait on a shared load, without cancelling the load for everyone else.

## The API

```csharp
public Task WaitAsync(CancellationToken cancellationToken);
public Task WaitAsync(TimeSpan timeout);
public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
// .NET 8: with a TimeProvider, to control time in tests
public Task WaitAsync(TimeSpan timeout, TimeProvider timeProvider);
public Task WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken);
// and the same on Task<TResult>, returning Task<TResult>
```

The returned `Task` completes on the first of three events:

| What happens first | The returned `Task` |
|---|---|
| the original task completes | carries over its outcome: result, exception or cancellation |
| the timeout expires | faults with a **`TimeoutException`** |
| the token is cancelled | is cancelled, and the `TaskCanceledException` carries **that token** |

So timeout and cancellation stay distinguishable: `catch (TimeoutException)` on one side, `catch (OperationCanceledException ex) when (ex.CancellationToken == ct)` on the other.

## How it works inside

Before allocating anything, `WaitAsync` tries a few shortcuts ([Task.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs), .NET 11):

```csharp
if (IsCompleted || (!cancellationToken.CanBeCanceled && millisecondsTimeout == Timeout.UnsignedInfinite))
{
    return this;
}

if (cancellationToken.IsCancellationRequested)
{
    return FromCanceled(cancellationToken);
}

if (millisecondsTimeout == 0)
{
    return FromException(new TimeoutException());
}

return new CancellationPromise<VoidTaskResult>(this, millisecondsTimeout, timeProvider, cancellationToken);
```

- If the task has already completed, or there's neither a timeout nor a cancelable token, it returns **the task itself**: no allocation.
- If the token is already cancelled, it returns a cancelled task right away.
- With a zero timeout and the task not yet completed, it returns a task faulted with `TimeoutException` right away.

Otherwise it creates a `CancellationPromise`: a `Task` that hooks into three things.

1. a continuation on the original task (`AddCompletionAction`);
2. a timer for the timeout, if there is one;
3. a registration on the token (`UnsafeRegister`).

Whichever fires first completes the promise with `TrySet*` and calls `Cleanup`, which tears down the other two: it disposes the registration, stops the timer and **removes the continuation from the original task**.
From then on, the original task holds no reference to the promise.

Two details: the timer is created with `flowExecutionContext: false` and the registration with `UnsafeRegister`.
Neither the timeout nor the cancellation carries the `ExecutionContext` of whoever called `WaitAsync` (for `ExecutionContext`, see [Demo 4](../demos/04-AsyncContext/README.md)).

It's the same mechanism the fixed client of scenario 1.2 writes by hand, with a timeout CTS and two registrations, packaged in a single call.

## Cancel the wait, not the work

```csharp
var task = provider.GetQuoteAsync("MSFT");        // the call has started

try
{
    var quote = await task.WaitAsync(TimeSpan.FromMilliseconds(100), ct);
}
catch (TimeoutException)
{
    // we stopped waiting, but GetQuoteAsync is still running
}
catch (OperationCanceledException ex) when (ex.CancellationToken == ct)
{
    // the caller cancelled: the operation goes on here too
}
```

When `WaitAsync` gives up, the original task carries on by itself, and its outcome no longer reaches whoever was waiting.
Two rules follow.

- **If the operation accepts a token, pass it to the operation.** `provider.GetQuoteAsync("MSFT", ct)` really stops it, and frees the connection, the timer, the work. `WaitAsync` is for when you can't: an API that takes no token, or a shared task.
- **A shared task is the ideal use case.** One load, many callers, each with its own token: whoever leaves stops waiting, and the load goes on for the others. Passing one caller's token to the load, instead, would cancel it for everyone.

It's the heart of `AsyncLazy<T>` in Demo 2 ([AsyncLazy.cs](../demos/02-AsyncCaching/AsyncLazy.cs)):

```csharp
public Task<T> GetValueAsync(CancellationToken cancellationToken = default) =>
    Volatile.Read(ref current).Value.WaitAsync(cancellationToken);
```

## `WaitAsync` or a `TaskCompletionSource` completed by hand

For the caller the semantics are the same: `TimeoutException` for the timeout, `OperationCanceledException` with its token for cancellation.
What changes is what happens to the source.

| | TCS completed by timeout and token (fixed client of 1.2) | `await tcs.Task.WaitAsync(timeout, ct)` |
|---|---|---|
| when the timeout expires | the TCS **is completed** with `TimeoutException` | the TCS **stays incomplete**: only the caller of `WaitAsync` stops waiting for it |
| others waiting on the same TCS | see the timeout too | don't notice |
| what you write | a CTS, two registrations with `await using` | one call |

In both cases the TCS must be removed from the dictionary of pending requests in a `finally`.
With `WaitAsync` it matters even more: the TCS will never complete, and without the removal the entry would stay there forever, ready to receive a late reply that nobody is waiting for anymore.

## Details worth knowing

- **The timeout starts when you call `WaitAsync`**, not when the operation started.
- **A new `Task` on every call**, shortcuts aside: the promise is an object, plus the timer and the registration. It allocates even with Runtime Async.
- **It doesn't exist on `ValueTask`**: you need `AsTask()` first, which can allocate in turn.
- **Exceptions after giving up reach no one.** The promise has detached from the original task: if that task faults later and nobody else observes it, the exception only ends up in `TaskScheduler.UnobservedTaskException`, when the GC finalizes the task.
- **In tests**, the overload with a `TimeProvider` lets the timeout fire without actually waiting for it, with a fake `TimeProvider` such as `FakeTimeProvider`.

## References

- [`Task.WaitAsync` — API documentation](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task.waitasync)
- [`Task.cs` in dotnet/runtime, release/11.0](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs): `WaitAsync` and `CancellationPromise<TResult>`
