# Demo 0 — Runtime Async: 4 promesse, 4 prove

[English](README.md) · **Italiano**

Da C# 5 ogni metodo `async` viene riscritto dal compilatore in una **state machine**: un metodo stub, una struct con un `MoveNext` che contiene il codice vero, un builder che la avvia e la rimette in moto dopo ogni `await`.
Con **Runtime Async** di .NET 11 il compilatore smette di riscrivere: il metodo resta com'è, viene marcato come async, e sospensione e ripresa le gestisce il runtime.

L'abstract della sessione fa quattro promesse: IL più semplice, debug migliore, meno allocazioni, più velocità.
Questa Demo porta una prova per ciascuna: le prime due nell'app (scenari 0.1 e 0.2), le altre due nei benchmark.
Lo scenario 0.3 mostra invece cosa *non* cambia.

## Due progetti, stessi sorgenti

| Progetto | Compilato con | Nell'assembly |
|---|---|---|
| [RuntimeAsync.Classic](RuntimeAsync.Classic) | `RuntimeAsync=false` | state machine generate dal compilatore |
| [RuntimeAsync.Native](RuntimeAsync.Native) | `RuntimeAsync=true`, cioè `<Features>runtime-async=on</Features>` | metodi marcati `MethodImplAttributes.Async` |

Entrambi compilano i file di [Code](Code): si lancia lo stesso scenario nei due progetti e si confronta l'output.
Il banner dice con quale modello è stato compilato l'assembly.

```bash
dotnet run --project demos/00-RuntimeAsync/RuntimeAsync.Classic -- il
dotnet run --project demos/00-RuntimeAsync/RuntimeAsync.Native -- il
```

Da IDE ci sono i profili `0.1 il`, `0.2 stack` e `0.3 elision` in entrambi i progetti.

**Debug o Release.** `dotnet run` compila in Debug: la state machine è una `class` e l'IL contiene i `nop` per il debugger.
Per l'IL che gira in produzione aggiungi `-c Release` (state machine `struct`). Le tabelle qui sotto riportano entrambi.

## ★ 0.1 `il` — IL più semplice

Il metodo osservato, in [QuotePricing.cs](Code/QuotePricing.cs): un `await`, un risultato.

```csharp
public static async Task<decimal> GetGrossPriceAsync(FakeQuoteProvider provider, string symbol)
{
    var quote = await provider.GetQuoteAsync(symbol);
    return quote.Price * 1.22m;
}
```

Lo scenario legge l'IL via reflection con un piccolo decoder ([IlReader.cs](Code/IlReader.cs)), così non serve aprire ILSpy.

**Classic.** `GetGrossPriceAsync` diventa uno stub: inizializza la state machine `<GetGrossPriceAsync>d__0`, copia i parametri nei suoi campi, chiama `AsyncTaskMethodBuilder<decimal>.Start` e restituisce `builder.Task`.
Il codice vero sta in `MoveNext`, con il protocollo dell'awaiter scritto per esteso:

```text
GetQuoteAsync → GetAwaiter → get_IsCompleted
  no:  AwaitUnsafeOnCompleted         salva lo stato ed esce, riprenderà da qui
  sì:  GetResult → get_Price → op_Multiply → SetResult   (SetException nel catch)
```

I campi della state machine sono lo stato del metodo portato sull'heap: `<>1__state` (da dove riprendere), `<>t__builder`, i parametri `provider` e `symbol`, `<>u__1` (l'awaiter in sospeso).

**Native.** Nessuna state machine: il metodo contiene il codice come l'hai scritto, e l'`await` è una chiamata.

```text
IL_000b: callvirt     FakeQuoteProvider::GetQuoteAsync
IL_0010: call         AsyncHelpers::Await
IL_0015: callvirt     Quote::get_Price
  …
IL_0025: call         Decimal::op_Multiply
IL_002a: ret
```

Il metodo porta il flag `MethodImplAttributes.Async` (`0x2000`): è il segnale per il JIT, che genera lui il codice di sospensione e ripresa.

| | Classic | Native |
|---|---|---|
| State machine nell'assembly | 9 | 0 |
| `[AsyncStateMachine]` sul metodo | `<GetGrossPriceAsync>d__0` | nessuno |
| `MethodImplAttributes` | `IL (0x0000)` | `Async (0x2000)` |
| IL in Debug | 63 byte (stub) + 248 byte (`MoveNext`) | 52 byte |
| IL in Release | 63 byte (stub) + 188 byte (`MoveNext`) | 43 byte |

Il messaggio: il compilatore non riscrive più il metodo, e nell'IL trovi quello che hai scritto.

## ★ 0.2 `stack` — debug migliore

Una catena di tre metodi async, `OuterAsync` → `MiddleAsync` → `InnerAsync` ([StackTraceDemo.cs](Code/StackTraceDemo.cs)).
Il più interno stampa lo stack *live* con `new StackTrace()`, cioè quello che vedono debugger e profiler.
Due giri: prima la catena completa in modo sincrono (`await Task.CompletedTask`), poi la stessa dopo una sospensione vera (`await Task.Yield()`).

| Frame sullo stack | Classic, Debug | Classic, Release | Native |
|---|---|---|---|
| catena sincrona | 19 | 23 | 8 |
| dopo una sospensione | 7 | 10 | 6 |

In Classic ogni livello lascia tre frame: `MoveNext` della state machine, `AsyncMethodBuilderCore.Start` e lo stub.
In Release se ne aggiunge un quarto, `AsyncTaskMethodBuilder.Start`.

```text
at <InnerAsync>d__11.MoveNext (line 47)
at AsyncMethodBuilderCore.Start
at StackTraceDemo.InnerAsync
at <MiddleAsync>d__10.MoveNext (line 37)
at AsyncMethodBuilderCore.Start
at StackTraceDemo.MiddleAsync
…
```

In Native c'è un frame per metodo, con il numero di riga sul metodo stesso:

```text
at StackTraceDemo.InnerAsync (line 47)
at StackTraceDemo.MiddleAsync (line 37)
at StackTraceDemo.OuterAsync (line 31)
at StackTraceDemo.RunAsync (line 22)
…
```

Dopo una sospensione reale i due modelli si somigliano.
Sullo stack c'è solo il metodo che riprende, sopra i frame del thread pool: `AsyncStateMachineBox<…>.MoveNext` in Classic, `RuntimeAsyncTask<T>.DispatchContinuations` in Native.
I chiamanti non ci sono perché non sono in esecuzione: aspettano che `InnerAsync` completi per riprendere a loro volta, e Runtime Async non cambia questo.

Lo stack trace delle *eccezioni* era già ripulito in entrambi i modelli (`ExceptionDispatchInfo`): la differenza sta nello stack live.

## 0.3 `elision` — la trappola che resta

"Async elision" vuol dire restituire il `Task` invece di awaitarlo, per non pagare la state machine di un metodo che fa solo da passacarte.
Con Runtime Async il risparmio si riduce (vedi i benchmark), le trappole semantiche restano.

Da [ElisionDemo.cs](Code/ElisionDemo.cs):

```csharp
// Broken: il Task esce dallo scope del using.
static Task<Quote> GetQuoteElided(string symbol)
{
    using var session = new QuoteSession();
    return session.GetQuoteAsync(symbol);
}

// Fixed: il metodo resta vivo, e la sessione aperta, finché la chiamata non completa.
static async Task<Quote> GetQuoteAwaited(string symbol)
{
    using var session = new QuoteSession();
    return await session.GetQuoteAsync(symbol);
}
```

Nella versione broken la sessione viene chiusa mentre la chiamata è ancora in corso:

```text
     1 ms [t  6] session: request sent
     4 ms [t  6] session: disposed
   115 ms [t  6] session: response received
✘ ObjectDisposedException: the session was disposed while the call was still running
```

Nella versione fixed l'ordine è `request sent` → `response received` → `disposed`, e il prezzo arriva.

Il comportamento è identico in Classic e in Native: Runtime Async cambia come viene eseguito il metodo, non cosa significa.
Valgono allo stesso modo le altre trappole dell'elision: nel metodo senza `async` un'eccezione (per esempio la validazione degli argomenti) esce in modo sincrono invece di finire nel `Task`, e il metodo manca dallo stack trace.

## Benchmark — meno allocazioni, più veloce

In [benchmarks/BeyondAsyncAwait.Benchmarks](../../benchmarks/BeyondAsyncAwait.Benchmarks) ogni benchmark gira due volte: job `StateMachine` (baseline) e job `RuntimeAsync`.

```bash
dotnet run -c Release --project benchmarks/BeyondAsyncAwait.Benchmarks -- --filter '*AwaitChain*' '*Elision*'
```

- `AwaitChainBenchmarks`: una catena di 10 metodi async. In `CompletesSynchronously` la foglia non sospende mai; in `Suspends` fa `Task.Yield`, quindi ogni livello della catena sospende e riprende.
- `ElisionBenchmarks`: un metodo passacarte davanti a una foglia che sospende, con `await` (`Awaited`) e senza (`Elided`).

Risultati **indicativi** (`--quick`, i7-11800H, .NET 11.0.0): rifalli sulla macchina della demo, senza `--quick`.

| Benchmark | StateMachine | RuntimeAsync |
|---|---|---|
| 10 await che completano in modo sincrono | 62 ns, 0 B | **37 ns**, 0 B |
| 10 await con sospensione reale | 1286 ns, 1204 B | **508 ns, 672 B** |
| passacarte con `await` | 517 ns, 191 B | 364 ns, 168 B |
| passacarte elided (`return task`) | 411 ns, 96 B | 334 ns, 104 B |

- **Sincrono**: zero allocazioni in entrambi i modelli (un `Task` già completato non costa nulla), ma Runtime Async è più veloce perché non attraversa builder e `MoveNext` a ogni livello.
- **Con sospensione**: con le state machine ogni livello sospeso porta sull'heap la propria state machine, dentro un box che è anche il suo `Task`. Con Runtime Async sospensione e ripresa le gestisce il runtime: la stessa catena alloca circa la metà e impiega meno della metà del tempo.
- **Elision**: con le state machine faceva risparmiare circa il 20% del tempo, con Runtime Async meno del 10%. Restano solo le trappole dello scenario 0.3.

Il harness di BenchmarkDotNet è compilato con lo stesso `RuntimeAsync` del codice misurato: un `Task` che il harness awaita direttamente viene trattato come nel codice reale.

## Cosa sapere prima di usarlo

- In .NET 11 GA Runtime Async è **opt-in** per il tuo codice: serve `<Features>runtime-async=on</Features>`. In questo repo lo attiva [Directory.Build.targets](../../Directory.Build.targets), e `-p:RuntimeAsync=false` torna alle state machine. Le librerie del runtime sono già compilate così.
- `async void` resta una state machine anche con Runtime Async: in Demo 6 sono le uniche rimaste nell'assembly (le lambda passate a `Subscribe`).
- I metodi con `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]` escono da Runtime Async: vedi [Demo 3](../03-ValueTask/README-it.md).
- Per sapere se un assembly è compilato così, cerca il flag `MethodImplAttributes.Async` sui metodi, come fa [BuildInfo](../../src/Demo.Shared/BuildInfo.cs). Contare le state machine non basta, per via di `async void`.

## Riferimenti

- [What's new in the .NET 11 runtime — Runtime Async](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#runtime-async)
- [Runtime async — specifica di design (dotnet/runtime)](https://github.com/dotnet/runtime/blob/main/docs/design/specs/runtime-async.md)
- [Stephen Cleary — Eliding Async and Await](https://blog.stephencleary.com/2016/12/eliding-async-await.html)
