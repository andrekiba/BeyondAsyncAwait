# `TaskCompletionSource`: a `Task` you complete yourself

**English** · [Italiano](task-completion-source-it.md)

A `Task` usually comes from code that runs: an `async` method, `Task.Run`.
`TaskCompletionSource<T>` (TCS) gives you a `Task` that no code runs: you complete it yourself, when you decide and from whatever thread you're on.
It's how a callback, an event or a message becomes something to `await`, and it's the building block of coordination primitives.

It shows up across the repo:
- [Demo 1](../demos/01-TaskCompletionSource/README.md) is all about it: the three traps of a TCS completed by someone else;
- in [Demo 2](../demos/02-AsyncCaching/README.md) it's the start gate that releases a burst of calls all at once;
- in [Demo 3](../demos/03-ValueTask/README.md) it's the classic way, one TCS per operation, that `IValueTaskSource` replaces;
- in [Demo 6](../demos/06-RxInterop/README.md) it turns the end of a subscription (`OnCompleted`) into a `Task` to await.

This document covers the basics: what a TCS is, its API, the patterns it's used for, and when to use something else.
The traps, with their code and real output, are in Demo 1.

## A `Task` with no code behind it

There are two kinds of `Task` (Stephen Cleary calls them *delegate tasks* and *promise tasks*):

| | delegate task | promise task |
|---|---|---|
| who creates it | `Task.Run`, `Task.Factory.StartNew` | an `async` method, `Task.Delay`, I/O, a `TaskCompletionSource` |
| what's behind it | a delegate that runs on a thread | nothing that runs: an event that will happen |
| who completes it | the delegate, when it returns | whoever has the event in hand: the I/O, the timer, the code that calls `SetResult` |
| `Status` while you wait | `WaitingToRun`, `Running` | `WaitingForActivation` |

The `Task` of an `async` method is a promise task too: it's completed when the method returns, by the builder that the compiler generates or, with Runtime Async, by the runtime.
A TCS puts the same thing in your hands.

Waiting on a promise task doesn't hold a thread.
10,000 `async` methods awaiting the same TCS are 10,000 continuations in a list: in a test run the thread pool stayed at 2 threads, and a single `SetResult` released them all.

A TCS has two halves:
- `tcs.Task` is the side that reads: you can give it to anyone, because a `Task` has no public method to complete it;
- the TCS is the side that writes, and you keep it.

```csharp
sealed class Gateway
{
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => ready.Task;     // whoever waits gets the Task, not the TCS
}
```

## The API

```csharp
public class TaskCompletionSource<TResult>
{
    public TaskCompletionSource();
    public TaskCompletionSource(TaskCreationOptions creationOptions);
    public TaskCompletionSource(object? state);                         // ends up in Task.AsyncState
    public TaskCompletionSource(object? state, TaskCreationOptions creationOptions);

    public Task<TResult> Task { get; }

    // Throw InvalidOperationException if the task has already completed.
    public void SetResult(TResult result);
    public void SetException(Exception exception);
    public void SetException(IEnumerable<Exception> exceptions);
    public void SetCanceled();
    public void SetCanceled(CancellationToken cancellationToken);       // .NET 5
    public void SetFromTask(Task<TResult> completedTask);               // .NET 9

    // Return false if the task has already completed.
    public bool TrySetResult(TResult result);
    public bool TrySetException(Exception exception);
    public bool TrySetException(IEnumerable<Exception> exceptions);
    public bool TrySetCanceled();
    public bool TrySetCanceled(CancellationToken cancellationToken);
    public bool TrySetFromTask(Task<TResult> completedTask);            // .NET 9
}
```

Since .NET 5 there's also `TaskCompletionSource` without `<TResult>`, for a `Task` with no result: `SetResult()` takes no arguments.
Before, the idiom was a `TaskCompletionSource<bool>` with a result nobody read.

The three ways to complete it are the three final states of a `Task`:

| Call | `Task.Status` | What `await` does |
|---|---|---|
| `SetResult(value)` | `RanToCompletion` | returns `value` |
| `SetException(ex)` | `Faulted` | throws `ex` |
| `SetCanceled(token)` | `Canceled` | throws `TaskCanceledException`, whose `CancellationToken` is `token` |

- **It completes only once.** The first call wins. After that, every `Set*` throws `InvalidOperationException` ("An attempt was made to transition a task to a final state when it had already completed.") and every `TrySet*` returns `false`, whatever the outcome it tries to set: a `TrySetCanceled` after a `SetResult` returns `false` too.
- **Everyone gets the same outcome.** The `Task` can be awaited many times: the same result, or the same exception instance, for every caller, late ones too.
- **`SetFromTask` copies the outcome of a task that has already completed**: result, exception or cancellation. Given one that hasn't completed yet, it throws `ArgumentException`.
- **When the result is already known**, you don't need a TCS: `Task.FromResult(value)`, `Task.CompletedTask`, `Task.FromException(ex)`, `Task.FromCanceled(token)`.

## The patterns

### From a callback to a `Task`

An API that reports the outcome with callbacks becomes a method to await: each callback completes the TCS with its outcome.

```csharp
public Task<Quote> GetQuoteAsync(string symbol)
{
    var tcs = new TaskCompletionSource<Quote>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.BeginQuote(symbol,
        onQuote: quote => tcs.TrySetResult(quote),
        onError: error => tcs.TrySetException(error));
    return tcs.Task;
}
```

The callbacks run on the library's thread, hence `RunContinuationsAsynchronously`.
`TrySet*`, because nothing guarantees that a library you don't control calls back only once.

### From an event to a `Task`: the next one

An event fires many times; to await the next occurrence you subscribe, complete the TCS, and unsubscribe.

```csharp
public static async Task<Quote> NextQuoteAsync(this QuoteFeed feed, string symbol, CancellationToken cancellationToken)
{
    var tcs = new TaskCompletionSource<Quote>(TaskCreationOptions.RunContinuationsAsynchronously);
    EventHandler<Quote> handler = (_, quote) =>
    {
        if (quote.Symbol == symbol)
            tcs.TrySetResult(quote);
    };

    feed.QuoteReceived += handler;
    try
    {
        await using var registration = cancellationToken.Register(
            static (state, token) => ((TaskCompletionSource<Quote>)state!).TrySetCanceled(token), tcs);
        return await tcs.Task;
    }
    finally
    {
        feed.QuoteReceived -= handler;
    }
}
```

- **`TrySetResult`**: the event can fire again before the handler is removed. The first quote wins, the next ones get `false`.
- **The `-=` in the `finally`**, on success and on cancellation alike. Without it the handler, and the TCS with it, stays attached to the feed for as long as the feed lives: the same leak as the token registrations in scenario 1.2.
- **Cancellation with `TrySetCanceled(token)`**: the caller gets a `TaskCanceledException` that carries its token.

### A signal

The start gate of Demo 2 ([Concurrently.cs](../demos/02-AsyncCaching/Concurrently.cs)) holds back a burst of calls and releases them together:

```csharp
var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var calls = Enumerable.Range(0, count).Select(async _ =>
{
    await start.Task;
    return await call();
}).ToArray();

start.SetResult();
return await Task.WhenAll(calls);
```

All the calls wait on the same `Task`, and a single `SetResult` releases them.
With `RunContinuationsAsynchronously` each one resumes on a pool thread: without it they would resume one after another, inside `SetResult`.

A signal that says "ready" has one more duty: it must complete on **every** path, the failed ones included.

```csharp
public async Task StartAsync(CancellationToken cancellationToken)
{
    try
    {
        await ConnectAsync(cancellationToken);
        ready.TrySetResult();
    }
    catch (OperationCanceledException ex)
    {
        ready.TrySetCanceled(ex.CancellationToken);
        throw;
    }
    catch (Exception ex)
    {
        ready.TrySetException(ex);    // without this, whoever awaits Ready waits forever
        throw;
    }
}
```

If `StartAsync` fails without completing `ready`, nothing reports it: `Ready` stays `WaitingForActivation`, and whoever awaits it never resumes.
Cancellation has its own `catch` because `TrySetException` with an `OperationCanceledException` makes a faulted task, not a cancelled one (see the details below).

### Request and response

One TCS per request, in a dictionary keyed by the request id: the reply handler looks it up and completes it.
It's the pattern where all the rules below come together, and it's scenario 1.2 of [Demo 1](../demos/01-TaskCompletionSource/README.md): the reply and the timeout race to complete it, the entry has to leave the dictionary, and the registrations on the tokens have to be released.

## The rules

The numbers are the scenarios of [Demo 1](../demos/01-TaskCompletionSource/README.md), where each rule is broken and then fixed.

- **`RunContinuationsAsynchronously` when someone else completes it.** Otherwise `SetResult` runs the awaiting code on the caller's thread before it returns: the read loop, the timer, the bus handler, with any lock it holds (1.1).
- **`TrySet*` when more than one party can complete it**: reply, timeout, cancellation, shutdown. Checking `IsCompleted` first doesn't help: someone else can complete it between the check and the call (1.2).
- **Pass the token to `SetCanceled(token)`**, so whoever catches the exception can tell who cancelled (1.2).
- **Every registration has its exit**: the dictionary entry, the event handler, the token registration. Each one keeps the TCS alive for as long as what it's registered on lives (1.2, and the event pattern above).
- **It can't be reset.** To reuse a signal you replace the TCS, only once it's completed and with `Interlocked.CompareExchange` (1.3).
- **Complete it on every path.** A TCS that nobody completes throws nothing and blocks no thread: its waiters just never resume (1.3, and the ready signal above).

## Instead of a TCS

- **The result is already there**: `Task.FromResult`, `Task.CompletedTask`, `Task.FromException`, `Task.FromCanceled`.
- **You only need to stop waiting**, with a timeout or a token, without completing the source: [`WaitAsync`](wait-async.md).
- **A value computed once and shared by many callers**: `AsyncLazy<T>`, built on `Lazy<Task<T>>` ([Demo 2](../demos/02-AsyncCaching/README.md)).
- **Many values, one after another**: `Channel<T>` ([Demo 5](../demos/05-Channels/README.md)).
- **Many operations on the same source, without allocating**: `IValueTaskSource` built on `ManualResetValueTaskSourceCore<T>`. In Demo 3 the version with a TCS per operation allocates 136 bytes per operation, the pooled source 0 ([Demo 3](../demos/03-ValueTask/README.md)).
- **Mutual exclusion, or a limit on concurrency**: `SemaphoreSlim.WaitAsync`.

## Details worth knowing

- **`TaskContinuationOptions` instead of `TaskCreationOptions` compiles, and does nothing.** `new TaskCompletionSource<T>(TaskContinuationOptions.RunContinuationsAsynchronously)` picks the constructor that takes an `object? state`: the value ends up in `Task.AsyncState`, `CreationOptions` stays `None`, and the continuations run inline on the thread that calls `SetResult`. The build flags it with warning [CA2247](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2247).
- **`SetException` with several exceptions**: `await` throws only the first; `Task.Exception`, and the exception that `Wait()` throws, are an `AggregateException` that holds all of them.
- **`SetException(new OperationCanceledException())` makes a `Faulted` task, not a `Canceled` one.** `await` throws the same exception, but `IsCanceled` is `false`, and code that checks the status sees a failure. An `async` method that throws `OperationCanceledException`, instead, ends `Canceled`. To cancel a TCS, use `SetCanceled`.
- **`SetCanceled()` without a token**: the `TaskCanceledException` carries `CancellationToken.None`, and the caller can't tell whether its own token was the cause.
- **A faulted TCS that nobody awaits** doesn't crash the process: its exception reaches `TaskScheduler.UnobservedTaskException` only when the GC finalizes the task, possibly much later.
- **A TCS is the write side of an `AsyncSubject<T>`**: `SetResult` is `OnNext` plus `OnCompleted`, `SetException` is `OnError`. Why that matters for the conversions to and from Rx is in [`Task<T>` and `IObservable<T>`](task-observable.md).

## References

- [`TaskCompletionSource<TResult>` — API documentation](https://learn.microsoft.com/dotnet/api/system.threading.tasks.taskcompletionsource-1)
- [Stephen Toub — The Nature of `TaskCompletionSource<TResult>`](https://devblogs.microsoft.com/dotnet/the-nature-of-taskcompletionsourcetresult/)
- [Stephen Cleary — A Tour of Task, Part 0: Overview (delegate tasks and promise tasks)](https://blog.stephencleary.com/2014/04/a-tour-of-task-part-0-overview.html)
- [Interop with other asynchronous patterns and types: from events and callbacks to `Task`](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/interop-with-other-asynchronous-patterns-and-types)
- [Sergey Tepliakov — The danger of `TaskCompletionSource<T>` class](https://devblogs.microsoft.com/premier-developer/the-danger-of-taskcompletionsourcet-class/)
- [CA2247: Argument passed to `TaskCompletionSource` constructor should be `TaskCreationOptions` enum instead of `TaskContinuationOptions` enum](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2247)
