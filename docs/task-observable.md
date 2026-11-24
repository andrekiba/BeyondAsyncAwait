# `Task<T>` and `IObservable<T>`: one value or many, pull or push

**English** · [Italiano](task-observable-it.md)

`Task<T>` is one value that will arrive; `IObservable<T>` is a sequence of values that is delivered to you.
They look like two different worlds, but they come from the same idea, and seeing how they're related explains the rules of the conversions between them: why `await observable` returns the *last* value, why `Retry` on a `ToObservable()` never repeats the call, where the backlog piles up when you go from push to pull.

The session follows this road from one end to the other:
- Demos 1–4 deal with one value: `Task<T>`, `TaskCompletionSource`, `AsyncLazy<T>`, `ValueTask<T>`;
- [Demo 5](../demos/05-Channels/README.md) deals with many values in pull mode: `Channel<T>`, `IAsyncEnumerable<T>`;
- [Demo 6](../demos/06-RxInterop/README.md) deals with many values in push mode: `IObservable<T>`, the conversions to and from `Task`, and AsyncRx.

This document is the map; the traps, with their code and real output, are in the Demos.

## The map

| | one value | a sequence |
|---|---|---|
| **synchronous** | `T` | `IEnumerable<T>`: pull, the consumer calls `MoveNext()` |
| **asynchronous** | `Task<T>`, `ValueTask<T>` | `IAsyncEnumerable<T>`: pull, the consumer awaits `MoveNextAsync()`<br>`IObservable<T>`: push, the producer calls `OnNext()` |

For a single value, pull or push makes little difference: there is one value, and it arrives once.
For a sequence it's the whole difference, because it decides **who sets the pace**: in pull the producer only works when the consumer asks; in push the values arrive when the producer has them, ready or not.

## The duality

Put the sync interfaces side by side (simplified: in the BCL `MoveNext` is declared on the non-generic `IEnumerator`):

```csharp
// Pull: the consumer calls, the producer answers.
public interface IEnumerable<out T>
{
    IEnumerator<T> GetEnumerator();
}

public interface IEnumerator<out T> : IDisposable
{
    bool MoveNext();                // true: a value in Current · false: the end · or it throws
    T Current { get; }
}

// Push: the producer calls, the consumer answers.
public interface IObservable<out T>
{
    IDisposable Subscribe(IObserver<T> observer);
}

public interface IObserver<in T>
{
    void OnNext(T value);           // a value
    void OnCompleted();             // the end
    void OnError(Exception error);  // the error
}
```

They're the same protocol with the direction of the calls reversed, which is why `IObservable<T>` is called the *dual* of `IEnumerable<T>`:

| | pull: `IEnumerable<T>` | push: `IObservable<T>` |
|---|---|---|
| start | `GetEnumerator()` | `Subscribe(observer)` |
| a value | `MoveNext()` returns `true`, the value is in `Current` | `OnNext(value)` |
| the end | `MoveNext()` returns `false` | `OnCompleted()` |
| an error | `MoveNext()` throws | `OnError(error)` |
| stop early | `Dispose()` the enumerator | `Dispose()` the subscription |
| who waits for whom | the consumer, inside `MoveNext()`, until the value is ready | the producer, inside `OnNext()`, until the consumer has handled it |

The last row is the one that matters with async code.
`IAsyncEnumerable<T>` turns `MoveNext()` into `ValueTask<bool> MoveNextAsync()`: the consumer awaits instead of blocking.
On the push side `OnNext` stays `void`: an observer doing async work returns at its first `await`, and the producer moves on without waiting (6.2, 6.5).
AsyncRx's `IAsyncObserver<T>` makes it `ValueTask OnNextAsync(T value)`, and the producer awaits it: the same step, on the other side of the duality.

### `Task<T>` is an observable with one value

A `Task<T>` ends in one of three ways, and each one has its counterpart in an observable that produces at most one value:

| `Task<T>` | observable |
|---|---|
| completes with a result | `OnNext(result)`, then `OnCompleted()` |
| faults | `OnError(exception)` |
| is cancelled | `OnError(TaskCanceledException)` |
| can be awaited many times: same result, or same exception | `AsyncSubject<T>`: keeps the last value and delivers it on completion to every subscriber, late ones too |

Rx uses this equivalence in both directions:
- `await observable` goes through `GetAwaiter()`, which returns an `AsyncSubject<T>`: that's why it waits for completion, returns the *last* value, throws `InvalidOperationException` if the sequence is empty, and never returns if it's infinite (6.1);
- `task.ToObservable()` behaves like an `AsyncSubject<T>`: one execution, and every subscriber gets the same result, or the same exception, even after completion.

## Where they differ

The duality is in the protocol; the differences are in when the work starts, who shares it, and who stops it.

| | `Task<T>` | `IObservable<T>` | `IAsyncEnumerable<T>` | `ChannelReader<T>` |
|---|---|---|---|---|
| **when the work starts** | before you get the task: an async method runs up to its first `await` | cold (`FromAsync`, `Interval`, `Create`): at every `Subscribe`; hot (`Subject`, events): whether or not anyone is subscribed | at the first `MoveNextAsync()`: an async iterator does nothing before | when the writer writes, whether or not anyone is reading |
| **two consumers** | the same result, one execution | cold: two executions; hot: both get the values from the moment they subscribe | two executions: every `GetAsyncEnumerator()` starts over | they share the items: each one goes to a single reader |
| **who sets the pace** | — | the producer: `OnNext` is `void` | the consumer: the iterator stops at `yield return` until the next `MoveNextAsync()` | the buffer: a bounded channel makes the writer wait, or drops (Demo 5) |
| **how you cancel** | with the token passed to the operation; from outside you can only stop waiting ([`WaitAsync`](wait-async.md)) | `Dispose()` the subscription; with `FromAsync` that cancels the token | the token (`WithCancellation`, `[EnumeratorCancellation]`), or `break`, which disposes the enumerator | the token passed to `ReadAllAsync` cancels the read, not the writer |
| **errors** | stored in the task: every `await` rethrows the same exception | `OnError`, terminal, to each observer | thrown by `MoveNextAsync()` | `Complete(ex)` on the writer, rethrown by `ReadAllAsync` and by `Completion` |
| **the end** | no separate signal: the task completes with its result | `OnCompleted()` | `MoveNextAsync()` returns `false` | `Complete()`: `ReadAllAsync` ends once the buffer is empty |

## The conversions

Every conversion has to answer the questions in the table above: when does the work start, is it shared, what does disposing cancel, where do the values wait.

| From → to | How | What it really does | Seen in |
|---|---|---|---|
| `Task<T>` → `IObservable<T>` | `task.ToObservable()` | wraps a task that's already running: one execution, the same result or exception for every subscriber; `Retry` sees the same error again, disposing cancels nothing | 6.1 |
| operation → `IObservable<T>` | `Observable.FromAsync(ct => …)` | lazy: one call per subscription, and disposing the subscription cancels the token | 6.1, 6.2 |
| operation → `IObservable<T>` | `Observable.StartAsync(ct => …)` | starts at once and is shared, like `ToObservable()`; but the token is tied to every subscription: disposing *one* cancels the call for everyone, and the others get `TaskCanceledException` | — |
| `IObservable<T>` → `Task<T>` | `await observable`, `ToTask()` | the last value, when the sequence completes. `ToTask(ct)`: the token unsubscribes and cancels the task | 6.1 |
| `IObservable<T>` → one value | `await observable.FirstAsync()`, `LastOrDefaultAsync()`, … | they return an `IObservable<T>`, not a `Task`: every `await` is a new subscription | 6.1, 6.4 |
| `IObservable<T>` → `IAsyncEnumerable<T>` | `ToAsyncEnumerable()` (System.Interactive.Async) | subscribes, and keeps whatever the consumer hasn't read yet in a queue with no limit | 6.3 |
| `IAsyncEnumerable<T>` → `IObservable<T>` | `ToObservable()` (System.Interactive.Async) | lazy: one enumeration per subscription, disposing stops it | — |
| `IObservable<T>` → `Channel<T>` | `Subscribe(x => writer.TryWrite(x))` on a bounded channel | the backlog in a buffer whose size and policy you chose | 6.3, 6.6 |
| `IObservable<T>` → `IAsyncObservable<T>` | `ToAsyncObservable()` (System.Reactive.Async) | on a hot source, the producer's thread blocks until the async observer has finished | 6.6 |
| `IAsyncObservable<T>` → `Task<T>` | `await`, `ForEachAsync` | as in classic Rx, `await` returns the last value. AsyncRx 6.0.0-alpha.18 has no way back to `IObservable<T>` or `IAsyncEnumerable<T>` | — |

Three rules come out of it:

- **A `Task` is a result, an observable is a recipe.** `ToObservable()` wraps a result that already exists, and every subscriber sees that one. To retry, repeat or cancel you need the recipe: `FromAsync(ct => …)`.
- **From many to one, say which one.** `await` on an observable picks the last value. `FirstAsync()`, `LastOrDefaultAsync()` and `ToTask()` make the choice visible to whoever reads the code.
- **From push to pull, the backlog lives somewhere.** In a hidden, unbounded queue (`ToAsyncEnumerable()`), on the producer's blocked thread (`ToAsyncObservable()`), or in a bounded channel whose policy you chose: `DropOldest` when the latest value is what counts, `Wait` when the producer can slow down.

## Which one to choose

- **One value**: `Task<T>`; `ValueTask<T>` when it often completes synchronously, on a hot path ([Demo 3](../demos/03-ValueTask/README.md)).
- **A sequence the consumer reads at its own pace**: `IAsyncEnumerable<T>` when producer and consumer are the same flow of code; `Channel<T>` when they're independent and need a buffer of a size you choose ([Demo 5](../demos/05-Channels/README.md)).
- **Values that arrive when they want**, to be combined over time (`Throttle`, `Buffer`, `CombineLatest`, `Switch`) or delivered to several subscribers: `IObservable<T>` ([Demo 6](../demos/06-RxInterop/README.md)).
- **A push sequence with an async consumer that must hold back the producer**: a bounded `Channel<T>` between the two, or `IAsyncObservable<T>`, which is still experimental (6.5–6.7).

## Details worth knowing

- **`ValueTask<T>` doesn't fit the `AsyncSubject` picture**: it can be awaited only once, and it isn't a shared result. To share it, `AsTask()` ([Demo 3](../demos/03-ValueTask/README.md)).
- **A `Task` without a result becomes an `IObservable<Unit>`**: `Unit` is Rx's type for "no value".
- **`await` on an observable needs `using System.Reactive.Linq;`**: `GetAwaiter` is an extension method.
- **Hot and cold are about the source, not the type.** The same `IObservable<T>` can start a new execution per subscriber or share one already under way; only the code that builds it says which. `Publish`, `Replay` and `RefCount` turn a cold source into a shared one, with the traps of 6.4.

## References

- [Introduction to Rx.NET — Key types: `IObservable<T>` and `IEnumerable<T>`, push and pull](https://introtorx.com/chapters/key-types.html)
- [Introduction to Rx.NET — Creating Observable Sequences: from `Task` to `IObservable`, `ToObservable` and `FromAsync`](https://introtorx.com/chapters/creating-observable-sequences.html)
- [Introduction to Rx.NET — Leaving Rx's World: `await` on an `IObservable<T>`, `ToTask`](https://introtorx.com/chapters/leaving-reactive-extensions-world.html)
- [Generate and consume async streams](https://learn.microsoft.com/dotnet/csharp/asynchronous-programming/generate-consume-asynchronous-stream)
- [dotnet/reactive — Rx.NET, Ix.NET and AsyncRx.NET](https://github.com/dotnet/reactive)
