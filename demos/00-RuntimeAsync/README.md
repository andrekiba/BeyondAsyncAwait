# Demo 0 — Runtime Async: 4 promises, 4 proofs

**English** · [Italiano](README-it.md)

Since C# 5, the compiler rewrites every `async` method into a **state machine**: a stub method, a struct with a `MoveNext` that holds the real code, and a builder that starts it and gets it moving again after every `await`.
With .NET 11 **Runtime Async**, the compiler stops rewriting: the method stays as it is, it is marked as async, and the runtime takes care of suspension and resumption.

The session abstract makes four promises: simpler IL, better debugging, fewer allocations, more speed.
This Demo offers one proof for each: the first two in the app (scenarios 0.1 and 0.2), the other two in the benchmarks.
Scenario 0.3, on the other hand, shows what does *not* change.

## Two projects, same sources

| Project | Compiled with | In the assembly |
|---|---|---|
| [RuntimeAsync.Classic](RuntimeAsync.Classic) | `RuntimeAsync=false` | compiler-generated state machines |
| [RuntimeAsync.Native](RuntimeAsync.Native) | `RuntimeAsync=true`, i.e. `<Features>runtime-async=on</Features>` | methods marked `MethodImplAttributes.Async` |

Both compile the files in [Code](Code): run the same scenario in the two projects and compare the output.
The banner tells you which model the assembly was compiled with.

```bash
dotnet run --project demos/00-RuntimeAsync/RuntimeAsync.Classic -- il
dotnet run --project demos/00-RuntimeAsync/RuntimeAsync.Native -- il
```

In the IDE, both projects have the `0.1 il`, `0.2 stack` and `0.3 elision` profiles.

**Debug or Release.** `dotnet run` builds in Debug: the state machine is a `class` and the IL contains `nop`s for the debugger.
For the IL that runs in production, add `-c Release` (`struct` state machine). The tables below show both.

## ★ 0.1 `il` — simpler IL

The method we look at, in [QuotePricing.cs](Code/QuotePricing.cs): one `await`, one result.

```csharp
public static async Task<decimal> GetGrossPriceAsync(FakeQuoteProvider provider, string symbol)
{
    var quote = await provider.GetQuoteAsync(symbol);
    return quote.Price * 1.22m;
}
```

The scenario reads the IL through reflection with a small decoder ([IlReader.cs](Code/IlReader.cs)), so you don't need to open ILSpy.

**Classic.** `GetGrossPriceAsync` becomes a stub: it initializes the `<GetGrossPriceAsync>d__0` state machine, copies the parameters into its fields, calls `AsyncTaskMethodBuilder<decimal>.Start` and returns `builder.Task`.
The real code lives in `MoveNext`, with the awaiter protocol spelled out in full:

```text
GetQuoteAsync → GetAwaiter → get_IsCompleted
  no:  AwaitUnsafeOnCompleted         saves the state and returns; it will resume from here
  yes: GetResult → get_Price → op_Multiply → SetResult   (SetException in the catch)
```

The state machine's fields are the method's state, moved onto the heap: `<>1__state` (where to resume from), `<>t__builder`, the `provider` and `symbol` parameters, `<>u__1` (the pending awaiter).

**Native.** No state machine: the method contains the code as you wrote it, and the `await` is a call.

```text
IL_000b: callvirt     FakeQuoteProvider::GetQuoteAsync
IL_0010: call         AsyncHelpers::Await
IL_0015: callvirt     Quote::get_Price
  …
IL_0025: call         Decimal::op_Multiply
IL_002a: ret
```

The method carries the `MethodImplAttributes.Async` flag (`0x2000`): that is the signal to the JIT, which generates the suspension and resumption code itself.

| | Classic | Native |
|---|---|---|
| State machines in the assembly | 9 | 0 |
| `[AsyncStateMachine]` on the method | `<GetGrossPriceAsync>d__0` | none |
| `MethodImplAttributes` | `IL (0x0000)` | `Async (0x2000)` |
| IL in Debug | 63 bytes (stub) + 248 bytes (`MoveNext`) | 52 bytes |
| IL in Release | 63 bytes (stub) + 188 bytes (`MoveNext`) | 43 bytes |

The takeaway: the compiler no longer rewrites the method, and what you find in the IL is what you wrote.

## ★ 0.2 `stack` — better debugging

A chain of three async methods, `OuterAsync` → `MiddleAsync` → `InnerAsync` ([StackTraceDemo.cs](Code/StackTraceDemo.cs)).
The innermost one prints the *live* stack with `new StackTrace()`, which is what debuggers and profilers see.
Two passes: first the chain completing synchronously (`await Task.CompletedTask`), then the same chain after a real suspension (`await Task.Yield()`).

| Frames on the stack | Classic, Debug | Classic, Release | Native |
|---|---|---|---|
| synchronous chain | 19 | 23 | 8 |
| after a suspension | 7 | 10 | 6 |

In Classic, each level leaves three frames: the state machine's `MoveNext`, `AsyncMethodBuilderCore.Start` and the stub.
Release adds a fourth one, `AsyncTaskMethodBuilder.Start`.

```text
at <InnerAsync>d__11.MoveNext (line 47)
at AsyncMethodBuilderCore.Start
at StackTraceDemo.InnerAsync
at <MiddleAsync>d__10.MoveNext (line 37)
at AsyncMethodBuilderCore.Start
at StackTraceDemo.MiddleAsync
…
```

In Native there is one frame per method, with the line number on the method itself:

```text
at StackTraceDemo.InnerAsync (line 47)
at StackTraceDemo.MiddleAsync (line 37)
at StackTraceDemo.OuterAsync (line 31)
at StackTraceDemo.RunAsync (line 22)
…
```

After a real suspension, the two models look alike.
The only method on the stack is the one that resumes, on top of the thread pool frames: `AsyncStateMachineBox<…>.MoveNext` in Classic, `RuntimeAsyncTask<T>.DispatchContinuations` in Native.
The callers aren't there because they aren't running: they are waiting for `InnerAsync` to complete so they can resume in turn, and Runtime Async doesn't change that.

*Exception* stack traces were already cleaned up in both models (`ExceptionDispatchInfo`): the difference is in the live stack.

## 0.3 `elision` — the trap that remains

"Async elision" means returning the `Task` instead of awaiting it, so you don't pay for the state machine of a method that is only a pass-through.
With Runtime Async the saving shrinks (see the benchmarks), but the semantic traps remain.

From [ElisionDemo.cs](Code/ElisionDemo.cs):

```csharp
// Broken: no async/await, the Task escapes the using scope.
static Task<Quote> GetQuoteElided(string symbol)
{
    using var session = new QuoteSession();
    return session.GetQuoteAsync(symbol);
}

// Fixed: the method stays alive (and the session open) until the call completes.
static async Task<Quote> GetQuoteAwaited(string symbol)
{
    using var session = new QuoteSession();
    return await session.GetQuoteAsync(symbol);
}
```

In the broken version, the session is disposed while the call is still in progress:

```text
     1 ms [t  6] session: request sent
     4 ms [t  6] session: disposed
   115 ms [t  6] session: response received
✘ ObjectDisposedException: the session was disposed while the call was still running
```

In the fixed version the order is `request sent` → `response received` → `disposed`, and the price comes through.

The behavior is identical in Classic and Native: Runtime Async changes how the method runs, not what it means.
The other elision traps apply just the same: in the method without `async`, an exception (from argument validation, for example) is thrown synchronously instead of ending up in the `Task`, and the method is missing from the stack trace.

## Benchmarks — fewer allocations, more speed

In [benchmarks/BeyondAsyncAwait.Benchmarks](../../benchmarks/BeyondAsyncAwait.Benchmarks) every benchmark runs twice: a `StateMachine` job (the baseline) and a `RuntimeAsync` job.

```bash
dotnet run -c Release --project benchmarks/BeyondAsyncAwait.Benchmarks -- --filter '*AwaitChain*' '*Elision*'
```

- `AwaitChainBenchmarks`: a chain of 10 async methods. In `CompletesSynchronously` the leaf never suspends; in `Suspends` it awaits `Task.Yield`, so every level of the chain suspends and resumes.
- `ElisionBenchmarks`: a pass-through method in front of a leaf that suspends, with `await` (`Awaited`) and without it (`Elided`).

**Indicative** results (`--quick`, i7-11800H, .NET 11.0.0): rerun them on the demo machine, without `--quick`.

| Benchmark | StateMachine | RuntimeAsync |
|---|---|---|
| 10 awaits that complete synchronously | 62 ns, 0 B | **37 ns**, 0 B |
| 10 awaits with a real suspension | 1286 ns, 1204 B | **508 ns, 672 B** |
| pass-through with `await` | 517 ns, 191 B | 364 ns, 168 B |
| elided pass-through (`return task`) | 411 ns, 96 B | 334 ns, 104 B |

- **Synchronous**: zero allocations in both models (an already completed `Task` costs nothing), but Runtime Async is faster because it doesn't go through the builder and `MoveNext` at every level.
- **With suspension**: with state machines, every suspended level moves its own state machine onto the heap, inside a box that is also its `Task`. With Runtime Async the runtime takes care of suspension and resumption: the same chain allocates about half as much and takes less than half the time.
- **Elision**: with state machines it saved about 20% of the time, with Runtime Async less than 10%. Only the traps from scenario 0.3 are left.

The BenchmarkDotNet harness is compiled with the same `RuntimeAsync` setting as the code being measured: a `Task` that the harness awaits directly is treated the same way as in real code.

## What to know before using it

- In .NET 11 GA, Runtime Async is **opt-in** for your code: you need `<Features>runtime-async=on</Features>`. In this repo [Directory.Build.targets](../../Directory.Build.targets) turns it on, and `-p:RuntimeAsync=false` goes back to state machines. The runtime libraries are already compiled this way.
- `async void` remains a state machine even with Runtime Async: in Demo 6 those are the only ones left in the assembly (the lambdas passed to `Subscribe`).
- Methods with `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]` opt out of Runtime Async: see [Demo 3](../03-ValueTask/README.md).
- To find out whether an assembly is compiled this way, look for the `MethodImplAttributes.Async` flag on its methods, as [BuildInfo](../../src/Demo.Shared/BuildInfo.cs) does. Counting state machines isn't enough, because of `async void`.

## References

- [What's new in the .NET 11 runtime — Runtime Async](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#runtime-async)
- [Runtime async — design spec (dotnet/runtime)](https://github.com/dotnet/runtime/blob/main/docs/design/specs/runtime-async.md)
- [Stephen Cleary — Eliding Async and Await](https://blog.stephencleary.com/2016/12/eliding-async-await.html)
