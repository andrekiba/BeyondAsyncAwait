# Beyond async/await

**English** · [Italiano](README-it.md)

Code and demos for the **WPC 2026** deep-dive session: async primitives in .NET, from compiler-generated state machines to .NET 11 Runtime Async, all the way to integrating `Task` (awaitable) with Rx.NET streams (observable).

> Since C# 5, the compiler has rewritten async/await into state machines.
> With .NET 11 Runtime Async, the CLR handles it directly: fewer allocations, faster code, simpler IL, better debugging.
> Starting from these internals, the session tackles real-world coordination and concurrency problems and scenarios... and how to solve them.

## Requirements

- **.NET 11 SDK** 11.0.100 or later (`global.json` accepts any stable 11.0 SDK, no previews)
- no external dependencies: the slow, unreliable "quote provider" is simulated (`FakeQuoteProvider`)

## How the demos work

Each Demo is a console app, and almost every scenario in it has a **broken** variant (the bug, running, with a number on screen) and a **fixed** one.

```bash
dotnet run --project demos/01-TaskCompletionSource                          # interactive menu
dotnet run --project demos/01-TaskCompletionSource -- list                  # list the demos
dotnet run --project demos/01-TaskCompletionSource -- 1.1                   # broken, Enter, fixed
dotnet run --project demos/01-TaskCompletionSource -- rpc-correlation --fixed
dotnet run --project demos/01-TaskCompletionSource -- all --no-pause        # smoke test
```

From **Visual Studio / Rider / VS Code**: open `BeyondAsyncAwait.slnx`, set the Demo's project (`Demo01.TaskCompletionSource`…) as the startup project, and pick the scenario from the profile menu next to the run button. Each project has, in `Properties/launchSettings.json`, a `menu` profile (the first one, with no arguments) and one profile per scenario (`1.2 rpc-correlation`…). From a terminal, the same profile is selected with `--launch-profile`:

```bash
dotnet run --project demos/01-TaskCompletionSource --launch-profile "1.2 rpc-correlation"
```

Every log line shows the elapsed milliseconds and the thread id: in async demos, *when* and *where* are the whole point.

### Runtime Async on/off

Every project builds with **Runtime Async on** (`<Features>runtime-async=on</Features>`, see [Directory.Build.targets](Directory.Build.targets)). To compare with the compiler's state machines:

```bash
dotnet run --project demos/04-AsyncContext -p:RuntimeAsync=false -- 4.1
```

Each demo's banner tells which model the assembly was built with (`runtime async` / `compiler state machines`).

## Layout

| Folder | Contents |
|---|---|
| [src/Demo.Shared](src/Demo.Shared) | `DemoHost` (command line and menu), `Out` (logging with time and thread), `FakeQuoteProvider` |
| [demos/00-RuntimeAsync](demos/00-RuntimeAsync) | the same sources ([Code](demos/00-RuntimeAsync/Code)) built by two projects: `Classic` and `Native` |
| [demos/01-…06-](demos) | one project per Demo |
| [benchmarks](benchmarks/BeyondAsyncAwait.Benchmarks) | BenchmarkDotNet: every benchmark runs twice, `StateMachine` vs `RuntimeAsync` |
| [docs](docs) | deep dives that span more than one Demo |

Each Demo folder has its full documentation, in English (`README.md`) and in Italian (`README-it.md`): the code of the bug and of the fix, the real output, the why, the benchmarks and the references. This README is the index.

## Outline

A single scenario: **QuoteHub**, a gateway that requests quotes from slow, unreliable external providers, caches reference data, talks to a message bus and publishes price streams.
The common thread goes **from a single value to a stream**: Task → TCS → AsyncLazy → ValueTask → Channel → IObservable.

★ = core scenarios, the others are bonus/backup.

### Demo 0 — Runtime Async: 4 promises, 4 proofs

[demos/00-RuntimeAsync](demos/00-RuntimeAsync): explanation, output and benchmarks in the [Demo's README](demos/00-RuntimeAsync/README.md).

The abstract promises simpler IL, better debugging, fewer allocations, more speed: each scenario or benchmark proves one of them. The same sources are built by two projects, `Classic` and `Native`.

| Id | Name | What it shows |
|---|---|---|
| ★ 0.1 | `il` | Classic: stub + state machine with `MoveNext`. Native: no state machine, the method is flagged `Async (0x2000)` and `await` becomes `call AsyncHelpers::Await`. |
| ★ 0.2 | `stack` | *Live* stack of a chain of 3 async methods: 19 frames versus 8. |
| 0.3 | `elision` | `return task` inside a `using`: `ObjectDisposedException` in both models. |

Benchmarks: 10 awaits with a real suspension go from 1286 ns and 1204 B to 508 ns and 672 B.

### Demo 1 — TaskCompletionSource: the building block

[demos/01-TaskCompletionSource](demos/01-TaskCompletionSource): explanation, code and output in the [Demo's README](demos/01-TaskCompletionSource/README.md).

| Id | Name | Broken → Fixed |
|---|---|---|
| ★ 1.1 | `inline-continuations` | `SetResult` runs the handlers on the read loop, inside its lock: 5/5, delay up to 560 ms. With `RunContinuationsAsynchronously`: 0/5, 12 ms. |
| ★ 1.2 | `rpc-correlation` | Request/response over a bus: 600 exceptions thrown into the bus, 3000 pending entries, heap +995 KB. With `TrySet*`, the token passed to `TrySetCanceled`, the registrations disposed and `TryRemove` in `finally`: 0, 0, +2 KB. |
| 1.3 | `manual-reset-event` | A naive `Reset` loses its waiters: 0/3 resume. With `CompareExchange`: 3/3. |

### Demo 2 — One value, many consumers: AsyncLazy and async caching

[demos/02-AsyncCaching](demos/02-AsyncCaching): explanation, code and output in the [Demo's README](demos/02-AsyncCaching/README.md). The final `AsyncLazy<T>` ([AsyncLazy.cs](demos/02-AsyncCaching/AsyncLazy.cs)) fixes the three traps of 2.2–2.4.

| Id | Name | Broken → Fixed |
|---|---|---|
| ★ 2.1 | `cache-stampede` | 200 requests on a cold key: caching the value → 200 provider calls, `GetOrAdd` on the `Task` → 16. With `Lazy<Task<T>>`: 1. |
| ★ 2.2 | `failure-caching` | A failed `Task` stays in the cache: 5/5 requests fail. An `AsyncLazy` that resets on failure: 1/5. |
| ★ 2.3 | `shared-cancellation` | The first caller's token cancels everyone's load: 0/2 served, and the cancellation stays in the cache. A load with no token + `WaitAsync(ct)`: 2/2. |
| 2.4 | `factory-context` | The shared load runs in the first caller's context (correlation id `req-A`). With `SuppressFlow`: `(none)`. |
| 2.5 | `hybrid-cache` | `HybridCache`: stampede → 1 call. If one caller leaves, the load goes on; if they all leave, it is cancelled and not cached. |

### Demo 3 — Hot path: ValueTask

[demos/03-ValueTask](demos/03-ValueTask): explanation, code, output and benchmarks in the [Demo's README](demos/03-ValueTask/README.md). Run it in Release: `dotnet run -c Release --project demos/03-ValueTask -- 3.1`.

| Id | Name | Broken → Fixed |
|---|---|---|
| ★ 3.1 | `cache-hit` | Bytes per cache hit. State machines: `Task<T>` 80, `ValueTask<T>` 0. Runtime Async: a `Task<T>` awaited directly **0**, 80 as soon as it becomes an object (delegate, field, array). |
| ★ 3.2 | `value-task-source` | Pooled `IValueTaskSource`: 0 bytes/operation versus 136 for a TCS. Two awaits on the same `ValueTask` → `InvalidOperationException`; a source without a token returns AAPL's price as MSFT. |

Benchmarks: the pooling builder brings a suspension down to 0 B, but it takes the method out of Runtime Async: the two optimizations don't add up.

### Demo 4 — Context and lifetime: AsyncLocal and IAsyncDisposable

[demos/04-AsyncContext](demos/04-AsyncContext): explanation, code and output in the [Demo's README](demos/04-AsyncContext/README.md).

| Id | Name | Broken → Fixed |
|---|---|---|
| ★ 4.1 | `asynclocal-flow` | An `AsyncLocal` set in an `async` method doesn't flow back to the caller: `(null)`, even before the first `await`. Fixed: a synchronous setter, the caller assigns the result, a mutable holder. Same with `-p:RuntimeAsync=false`. |
| ★ 4.2 | `context-leak` | The background loop started in the first request keeps its tenant: 4/4 lines tagged `acme`. With `SuppressFlow` and `UnsafeRegister`: 0/4. |
| 4.3 | `async-dispose` | A DI scope disposed synchronously → `InvalidOperationException`; a `DisposeAsync` that doesn't wait for the flush → 20/25 at shutdown. With `CreateAsyncScope` and a `DisposeAsync` that waits: 25/25. |

### Demo 5 — Many values, pull: Channels

[demos/05-Channels](demos/05-Channels): explanation, code and output in the [Demo's README](demos/05-Channels/README.md).

| Id | Name | Broken → Fixed |
|---|---|---|
| ★ 5.1 | `backpressure` | Producer ~1640 ticks/s, consumer 164. Unbounded: ~3700 ticks queued after 2.5 s, persisted price 2.2 s old. Bounded `Wait`: the producer slows down to ~200 ticks/s. Bounded `DropOldest`: ~3600 drops counted by `itemDropped`, fresh price. |
| ★ 5.2 | `pipeline` | The first worker to finish closes the channel: "completed" with 156/160 persisted; with the provider down the pipeline hangs. Fixed: completion after all the workers, the error travels with `Complete(ex)`: 159 + 1 dead-lettered, or the original `ProviderException` in ~25 ms. |
| 5.3 | `when-each` | `Task.WhenEach` (results in completion order) and `Channel.CreateUnboundedPrioritized` (urgent orders jump the queue). |

### Demo 6 — Many values, push: Task ↔ Rx.NET, and AsyncRx

[demos/06-RxInterop](demos/06-RxInterop): explanation, code and output in the [Demo's README](demos/06-RxInterop/README.md).

| Id | Name | Broken → Fixed |
|---|---|---|
| ★ 6.1 | `task-observable` | `await observable` gives the *last* value, throws if empty, never returns if infinite → `FirstAsync()`. `task.ToObservable().Retry(3)`: 3 attempts, **only 1 call** → `Observable.FromAsync(ct => …)`: 3 calls, and disposing cancels the token. |
| ★ 6.2 | `async-in-pipeline` | `Subscribe(async …)`: 6 in flight, `OnCompleted` at ~400 ms with work running until ~670 ms; `SelectMany(async …)`: no limit and no cancellation. Fixed: `Select(FromAsync)` + `Concat()` / `Merge(4)` / `Switch()` (7 calls cancelled). |
| ★ 6.3 | `rx-to-channel` | Ix's `ToAsyncEnumerable()`: a hidden unbounded queue (~500,000 items, +34 MB, price 2 s old). With a bounded(8) `DropOldest` `Channel`: backlog 7, drops counted, fresh price. |
| 6.4 | `refreshing-cache` | `Replay(1).RefCount()` read with `await FirstAsync()`: 12 connections, the timer never fires, **stale forever**. With `AutoConnect()`: 1 connection, refreshed by the timer. |
| ★ 6.5 | `asyncrx-backpressure` | Classic Rx: 5 in flight, `OnCompleted` before the work is done. AsyncRx: 1 in flight, the timer slows down to the consumer's pace, an honest `OnCompletedAsync`. |
| ★ 6.6 | `asyncrx-bridge` | `Subject<T>.ToAsyncObservable()` blocks the hot producer for ~1.5 s (sync-over-async, `REVIEW` comment in the AsyncRx source). With a bounded `DropOldest` `Channel` at the boundary: 1 ms. |
| 6.7 | `asyncrx-lifecycle` | Async `Observable.Create`: when `Subscribe` returns you aren't connected, when `Dispose` returns the offsets aren't committed. `AsyncObservable.Create` + `IAsyncDisposable`: awaitable setup and teardown. |

## Deep dives

- [`Task.WaitAsync`: stop waiting without stopping the work](docs/wait-async.md): the shortcuts, how it tears itself down, and why it cancels the wait and not the work. Used in scenarios 1.2 and 2.3.

## Notes

- **Runtime Async** in .NET 11 GA is still *opt-in* for your own code: without `<Features>runtime-async=on</Features>` the compiler still emits state machines (`EnablePreviewFeatures` is no longer needed). The runtime libraries are already compiled this way. The docs also mention `<UseRuntimeAsync>false</UseRuntimeAsync>` as an opt-out, but SDK 11.0.100 doesn't use it: this repo uses `-p:RuntimeAsync=false`.
- **AsyncRx.NET** (`System.Reactive.Async`) is experimental: the only build on NuGet is `6.0.0-alpha.18` (2023), which works with Rx 7.
- **BenchmarkDotNet 0.15.x doesn't recognize .NET 11** (`GetRuntimeVersion not implemented for NotRecognized`): the repo uses `0.16.0-preview.2`.
- The benchmarks run in the same build as the BenchmarkDotNet harness, so the harness is also compiled with or without Runtime Async: a `Task` returned to the harness is awaited directly, and with Runtime Async it is never materialized.
- [nuget.config](nuget.config) uses only nuget.org, so the repo builds anywhere regardless of the feeds configured on the machine.
