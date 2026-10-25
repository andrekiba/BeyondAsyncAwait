# Beyond async/await

Codice e demo della sessione deep dive **WPC 2026**: primitive asincrone in .NET, dalle macchine a stati del compilatore a Runtime Async di .NET 11, fino all'integrazione tra `Task` (awaitable) e stream Rx.NET (observable).

> Da C# 5 a oggi, async/await è stato riscritto dal compilatore in macchine a stati.
> Con Runtime Async di .NET 11, è il CLR a gestirlo direttamente: meno allocazioni, più veloce, IL più semplice, debug migliore.
> Partendo da questi internals, la sessione affronta problemi e scenari reali di coordinamento e concorrenza... e come risolverli.

## Requisiti

- **.NET 11 SDK** (RC2 o successivo: `global.json` accetta qualsiasi feature band 11.0.1xx, GA inclusa)
- nessuna dipendenza esterna: il "provider di quotazioni" lento e instabile è simulato (`FakeQuoteProvider`)

## Come funzionano le demo

Ogni Demo è un'app console, e quasi ogni scenario al suo interno ha una variante **broken** (il bug, in esecuzione, con un numero a schermo) e una **fixed**.

```bash
dotnet run --project demos/01-TaskCompletionSource                          # menu interattivo
dotnet run --project demos/01-TaskCompletionSource -- list                  # elenco demo
dotnet run --project demos/01-TaskCompletionSource -- 1.1                   # broken, Invio, fixed
dotnet run --project demos/01-TaskCompletionSource -- rpc-correlation --fixed
dotnet run --project demos/01-TaskCompletionSource -- all --no-pause        # smoke test
```

Ogni riga di log riporta millisecondi trascorsi e thread id: nelle demo async il *quando* e il *dove* sono il punto.

### Runtime Async on/off

Tutti i progetti compilano con **Runtime Async attivo** (`<Features>runtime-async=on</Features>`, vedi [Directory.Build.targets](Directory.Build.targets)). Per confrontare con le macchine a stati del compilatore:

```bash
dotnet run --project demos/04-AsyncContext -p:RuntimeAsync=false -- 4.1
```

Il banner di ogni demo dice con quale modello è stato compilato l'assembly (`runtime async` / `compiler state machines`).

## Struttura

| Cartella | Contenuto |
|---|---|
| [src/Demo.Shared](src/Demo.Shared) | `DemoHost` (riga di comando e menu), `Out` (log con tempo e thread), `FakeQuoteProvider` |
| [demos/00-RuntimeAsync](demos/00-RuntimeAsync) | stessi sorgenti ([Code](demos/00-RuntimeAsync/Code)) compilati in due progetti: `Classic` e `Native` |
| [demos/01-…06-](demos) | un progetto per Demo |
| [benchmarks](benchmarks/BeyondAsyncAwait.Benchmarks) | BenchmarkDotNet: ogni benchmark gira due volte, `StateMachine` vs `RuntimeAsync` |

## Scaletta

Scenario unico: **QuoteHub**, un gateway che chiede quotazioni a provider esterni lenti e instabili, mette in cache i dati di riferimento, parla con un message bus e pubblica stream di prezzi.
Il filo conduttore va **dal singolo valore allo stream**: Task → TCS → AsyncLazy → ValueTask → Channel → IObservable.

★ = scenari core, gli altri sono bonus/backup.

### Demo 0 — Runtime Async: 4 promesse, 4 prove

L'abstract promette IL più semplice, debug migliore, meno allocazioni, più velocità: ogni scenario ne dimostra una.

| Id | Nome | Cosa mostra |
|---|---|---|
| ★ 0.1 | `il` | Classic: metodo stub + state machine con `MoveNext` (63 + 248 byte di IL, 9 state machine nell'assembly). Native: 0 state machine, il metodo è marcato `MethodImplAttributes.Async (0x2000)` e l'`await` diventa `call AsyncHelpers::Await` (52 byte). |
| ★ 0.2 | `stack` | Stack trace *live* di una catena di 3 metodi async: 19 frame contro 8. Dopo una sospensione reale (`Task.Yield`) in entrambi i modelli i chiamanti spariscono dallo stack: riprende la continuation. |
| 0.3 | `elision` | `return task` senza `await` dentro un `using`: `ObjectDisposedException`. Con Runtime Async il motivo di performance per l'elision si riduce, la trappola resta. |

```bash
dotnet run --project demos/00-RuntimeAsync/RuntimeAsync.Classic -- il
dotnet run --project demos/00-RuntimeAsync/RuntimeAsync.Native  -- il
dotnet run -c Release --project benchmarks/BeyondAsyncAwait.Benchmarks -- --filter *AwaitChain* *Elision*
```

> Le state machine sono `class` in Debug e `struct` in Release: per mostrare la versione "vera" usa `-c Release`.

Risultati **indicativi** (BenchmarkDotNet `--quick`, i7-11800H, .NET 11 RC2: rifalli sulla macchina della demo, senza `--quick`):

| Benchmark | StateMachine | RuntimeAsync |
|---|---|---|
| 10 await che completano in modo sincrono | 64 ns, 0 B | **35 ns**, 0 B |
| 10 await con sospensione reale | 1324 ns, 1213 B | **535 ns, 672 B** |
| pass-through con `await` | 591 ns, 191 B | 378 ns, 168 B |
| pass-through elided (`return task`) | 408 ns, 96 B | 352 ns, 104 B |

Con le state machine l'elision faceva risparmiare ~30%; con Runtime Async la differenza si riduce a pochi punti, e restano solo le trappole semantiche (demo 0.3).

### Demo 1 — TaskCompletionSource: il mattone

[demos/01-TaskCompletionSource](demos/01-TaskCompletionSource)

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 1.1 | `inline-continuations` | `SetResult` esegue la continuation *sul thread del read loop, dentro il suo lock*: 5/5 handler sul loop, ritardo massimo ~550 ms. Con `RunContinuationsAsynchronously`: 0/5, ~15 ms. |
| ★ 1.2 | `rpc-correlation` | Correlazione richiesta/risposta su un bus (1000 richieste × 3 round, 10% perse, 20% in ritardo). Broken: 600 eccezioni lanciate nel bus dalle risposte tardive, 3000 entry pendenti, heap +900 KB. Fixed (`TrySet*`, dispose della registration, remove in `finally`): 0, 0, +5 KB. |
| 1.3 | `manual-reset-event` | `AsyncManualResetEvent`: un `Reset` ingenuo con la pipeline già in pausa perde i waiter (0/3 ripartono); con `CompareExchange` 3/3. |

### Demo 2 — Un valore, molti consumatori: AsyncLazy e async caching

[demos/02-AsyncCaching](demos/02-AsyncCaching) — l'`AsyncLazy<T>` finale ([AsyncLazy.cs](demos/02-AsyncCaching/AsyncLazy.cs)) risolve tutte e tre le trappole.

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 2.1 | `cache-stampede` | 200 richieste concorrenti su chiave fredda: cache del valore → 200 chiamate al provider; `GetOrAdd` sul Task → ~16 (la factory gira più volte). `Lazy<Task<T>>` → 1. |
| ★ 2.2 | `failure-caching` | Un Task fallito resta in cache per sempre: 5/5 richieste falliscono. AsyncLazy che si resetta sul fault: 1/5. |
| ★ 2.3 | `shared-cancellation` | Il token del primo chiamante cancella il caricamento condiviso: 0/2 serviti, e la cancellazione resta in cache. Load senza token + `WaitAsync(ct)` per chiamante: 2/2. |
| 2.4 | `factory-context` | La factory gira nell'ExecutionContext del primo chiamante: i log del load condiviso portano il correlation id di `req-A` anche quando servono `req-B`. Con `SuppressFlow`: `(none)`. |
| 2.5 | `hybrid-cache` | `HybridCache` (.NET 9+): stampede → 1 chiamata. Se cancella *un* chiamante il load prosegue per gli altri; se cancellano *tutti* il load condiviso viene cancellato (e non messo in cache). |

### Demo 3 — Hot path: ValueTask

[demos/03-ValueTask](demos/03-ValueTask) — da eseguire in Release: `dotnet run -c Release --project demos/03-ValueTask -- 3.1`

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 3.1 | `cache-hit` | Byte per cache hit (100.000 hit). **State machine**: `Task<T>` 80 byte in ogni caso, `ValueTask<T>` 0. **Runtime Async**: `Task<T>` awaitato direttamente → **0 byte** (il Task non viene mai materializzato), 80 appena diventa un oggetto (delegate, campo, `WhenAll`). Confronta con `-p:RuntimeAsync=false`. |
| ★ 3.2 | `value-task-source` | `IValueTaskSource<T>` pooled su `ManualResetValueTaskSourceCore<T>`: 0 byte/operazione contro 136 di una TCS. Awaitare due volte lo stesso ValueTask → `InvalidOperationException` (token scaduto); con una source ingenua senza token → il prezzo di AAPL restituito come MSFT. Fixed: `Preserve()` / `AsTask()` quando serve più di un await. |

> Il messaggio per il palco: con Runtime Async l'argomento classico "ValueTask sul hot path" si ridimensiona. Resta decisivo quando il Task diventa un oggetto e per le sorgenti riusabili (`IValueTaskSource`).

`ValueTaskBenchmarks`, risultati **indicativi** (`--quick`, stessa macchina):

| Benchmark | StateMachine | RuntimeAsync |
|---|---|---|
| cache hit, `Task<T>` awaitato | 19 ns, 81 B | **12 ns, 0 B** |
| cache hit, `ValueTask<T>` awaitato | 19 ns, 1 B | 12 ns, 0 B |
| cache hit, `Task<T>` tenuto in un campo | 21 ns, 80 B | 21 ns, 80 B |
| sospensione, `ValueTask<T>` | 388 ns, 104 B | 343 ns, 104 B |
| sospensione, `PoolingAsyncValueTaskMethodBuilder` | 410 ns, **0 B** | 352 ns, **104 B** |

L'ultima riga: i metodi con il pooling builder escono da Runtime Async, e nel job RuntimeAsync il vantaggio del pooling non si vede più. Le due ottimizzazioni non si sommano.

### Demo 4 — Contesto e ciclo di vita: AsyncLocal e IAsyncDisposable

[demos/04-AsyncContext](demos/04-AsyncContext)

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 4.1 | `asynclocal-flow` | Un valore impostato in un metodo `async` non torna al chiamante: `(null)`, anche se impostato prima del primo `await`. Fixed: setter sincrono, il chiamante assegna il risultato, oppure holder mutabile (il pattern di `IHttpContextAccessor`). Semantica identica con `-p:RuntimeAsync=false`. |
| ★ 4.2 | `context-leak` | Un singleton avvia lavoro in background durante la prima richiesta e si porta dietro il suo tenant: 4/4 righe di background etichettate `acme`. Con `ExecutionContext.SuppressFlow()` e `UnsafeRegister`: 0/4. |
| 4.3 | `async-dispose` | Uno scope DI disposto in modo sincrono con un servizio solo `IAsyncDisposable` → `InvalidOperationException`, 20/25 pubblicati; un `DisposeAsync` che non attende il flush → 20/25 allo shutdown. Fixed: `CreateAsyncScope` + `DisposeAsync` che attende il loop, idempotente → 25/25. |

### Demo 5 — Molti valori, pull: Channels

[demos/05-Channels](demos/05-Channels)

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 5.1 | `backpressure` | Producer ~1640 tick/s, consumer ~164. Unbounded: 3690 tick in coda dopo 2,5 s, prezzo persistito vecchio di 2,2 s, memoria in crescita. Bounded(100) `Wait`: il producer rallenta a ~200 tick/s. Bounded(100) `DropOldest`: il producer resta veloce, ~3590 scarti contati da `itemDropped`, prezzo fresco. È una decisione di business. |
| ★ 5.2 | `pipeline` | parse → enrich → persist, 4 worker per stadio. Broken: il primo worker che finisce chiude il canale → "completato" ma 156/160 persistiti; con il provider giù la pipeline si blocca. Fixed: lo stadio completa l'output solo quando tutti i worker hanno finito, l'errore viaggia con `Complete(ex)`: 159 + 1 in dead letter, oppure la `ProviderException` originale in ~26 ms. |
| 5.3 | `when-each` | `Task.WhenEach` (risultati in ordine di arrivo invece che tutti alla fine) e `Channel.CreateUnboundedPrioritized` (gli ordini urgenti scavalcano la coda). |

### Demo 6 — Molti valori, push: Task ↔ Rx.NET, e AsyncRx

[demos/06-RxInterop](demos/06-RxInterop)

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 6.1 | `task-observable` | `await observable` = *ultimo* valore, `InvalidOperationException` se vuoto, mai se infinito → `FirstAsync()` / `LastOrDefaultAsync()`. `task.ToObservable().Retry(3)` = 3 tentativi visti ma **1 sola chiamata** → `Observable.FromAsync(ct => …).Retry(3)` = 3 chiamate, e il dispose cancella il token. |
| ★ 6.2 | `async-in-pipeline` | `Subscribe(async …)`: 6 in volo, `OnCompleted` a ~400 ms con lavoro fino a ~670 ms. `SelectMany(async …)`: concorrenza illimitata, ordine casuale, nessuna cancellazione. Fixed: `Select(FromAsync)` + `Concat()` (1 in volo, in ordine) / `Merge(4)` (max 4) / `Switch()` (vince l'ultimo, 7 chiamate cancellate). |
| ★ 6.3 | `rx-to-channel` | Ponte Rx → `IAsyncEnumerable` con `ToAsyncEnumerable()` di Ix: coda nascosta illimitata (~500.000 elementi, +34 MB, prezzo vecchio di 2 s). Fixed: Channel bounded(8) `DropOldest`: backlog 7, scarti contati, prezzo fresco. |
| 6.4 | `refreshing-cache` | `Timer → FromAsync → Switch → Replay(1)`. Con `RefCount()` ogni `await FirstAsync()` riconnette, risponde il v1 replayato e il timer non scatta mai: **stale per sempre**. Con `AutoConnect()`: 1 connessione, refresh dal timer. |
| ★ 6.5 | `asyncrx-backpressure` | AsyncRx.NET: lo stesso consumer su Rx classico (5 in volo, `OnCompleted` che mente) e su `IAsyncObservable` (1 in volo, il timer rallenta al passo del consumer, `OnCompletedAsync` onesto). |
| ★ 6.6 | `asyncrx-bridge` | `Subject<T>.ToAsyncObservable()` blocca il producer hot (~1,5 s in totale: sync-over-async, con il commento `REVIEW` del sorgente AsyncRx a schermo). Fixed: un confine esplicito (Channel bounded, `DropOldest`) → producer bloccato 1 ms. |
| 6.7 | `asyncrx-lifecycle` | `Observable.Create` async: alla fine di `Subscribe` non sei connesso, alla fine di `Dispose` gli offset non sono committati. `AsyncObservable.Create` + `IAsyncDisposable`: setup e teardown awaitable. |

> Dove stanno le conversioni in Rx/Ix 7: `IObservable<T>.ToAsyncEnumerable()` è in `System.Interactive.Async` (`System.Linq.AsyncEnumerableEx`), non in `System.Reactive` né nel `System.Linq.AsyncEnumerable` della BCL.
> Con `runtime-async=on` le uniche state machine rimaste nell'assembly sono quelle dei lambda `async void`: Runtime Async non copre `async void`.

## Note

- **Runtime Async** in .NET 11 è ancora una *preview feature* (opt-in con `Features`); le librerie del runtime sono già compilate così.
- **AsyncRx.NET** (`System.Reactive.Async`) è sperimentale: l'unica build su NuGet è `6.0.0-alpha.18` (2023), che funziona con Rx 7.
- **BenchmarkDotNet 0.15.x non riconosce .NET 11** (`GetRuntimeVersion not implemented for NotRecognized`): il repo usa `0.16.0-preview.2`.
- I benchmark girano nella stessa build del harness di BenchmarkDotNet, che quindi è compilato anch'esso con o senza Runtime Async: un `Task` restituito al harness viene awaitato direttamente e con Runtime Async non viene materializzato.
- [nuget.config](nuget.config) usa solo nuget.org, così il repo compila ovunque indipendentemente dai feed configurati sulla macchina.
