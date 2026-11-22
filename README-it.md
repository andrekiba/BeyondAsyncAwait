# Beyond async/await

[English](README.md) · **Italiano**

Codice e demo della sessione deep dive **WPC 2026**: primitive asincrone in .NET, dalle macchine a stati del compilatore a Runtime Async di .NET 11, fino all'integrazione tra `Task` (awaitable) e stream Rx.NET (observable).

> Da C# 5 a oggi, async/await è stato riscritto dal compilatore in macchine a stati.
> Con Runtime Async di .NET 11, è il CLR a gestirlo direttamente: meno allocazioni, più veloce, IL più semplice, debug migliore.
> Partendo da questi internals, la sessione affronta problemi e scenari reali di coordinamento e concorrenza... e come risolverli.

## Requisiti

- **.NET 11 SDK** 11.0.100 o successivo (`global.json` accetta qualsiasi SDK 11.0 stabile, niente preview)
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

Da **Visual Studio / Rider / VS Code**: apri `BeyondAsyncAwait.slnx`, scegli il progetto della Demo (`Demo01.TaskCompletionSource`…) come startup project e lo scenario dal menu dei profili accanto al tasto di avvio. Ogni progetto ha in `Properties/launchSettings.json` un profilo `menu` (il primo, senza argomenti) e uno per scenario (`1.2 rpc-correlation`…). Da terminale lo stesso profilo si sceglie con `--launch-profile`:

```bash
dotnet run --project demos/01-TaskCompletionSource --launch-profile "1.2 rpc-correlation"
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
| [docs](docs) | approfondimenti che attraversano più Demo |

Ogni cartella di Demo ha la sua documentazione completa, in inglese (`README.md`) e in italiano (`README-it.md`): il codice del bug e del fix, l'output reale, il perché, i benchmark e i riferimenti. Questo README è l'indice.

## Scaletta

Scenario unico: **QuoteHub**, un gateway che chiede quotazioni a provider esterni lenti e instabili, mette in cache i dati di riferimento, parla con un message bus e pubblica stream di prezzi.
Il filo conduttore va **dal singolo valore allo stream**: Task → TCS → AsyncLazy → ValueTask → Channel → IObservable.

★ = scenari core, gli altri sono bonus/backup.

### Demo 0 — Runtime Async: 4 promesse, 4 prove

[demos/00-RuntimeAsync](demos/00-RuntimeAsync): spiegazione, output e benchmark nel [README della Demo](demos/00-RuntimeAsync/README-it.md).

L'abstract promette IL più semplice, debug migliore, meno allocazioni, più velocità: ogni scenario o benchmark ne dimostra una. Gli stessi sorgenti sono compilati in due progetti, `Classic` e `Native`.

| Id | Nome | Cosa mostra |
|---|---|---|
| ★ 0.1 | `il` | Classic: stub + state machine con `MoveNext`. Native: nessuna state machine, il metodo è marcato `Async (0x2000)` e l'`await` diventa `call AsyncHelpers::Await`. |
| ★ 0.2 | `stack` | Stack *live* di una catena di 3 metodi async: 19 frame contro 8. |
| 0.3 | `elision` | `return task` dentro un `using`: `ObjectDisposedException` in entrambi i modelli. |

Benchmark: 10 await con sospensione reale passano da 1286 ns e 1204 B a 508 ns e 672 B.

### Demo 1 — TaskCompletionSource: il mattone

[demos/01-TaskCompletionSource](demos/01-TaskCompletionSource): spiegazione, codice e output nel [README della Demo](demos/01-TaskCompletionSource/README-it.md).

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 1.1 | `inline-continuations` | `SetResult` esegue gli handler sul read loop, dentro il suo lock: 5/5, ritardo fino a 560 ms. Con `RunContinuationsAsynchronously`: 0/5, 12 ms. |
| ★ 1.2 | `rpc-correlation` | Request/response su un bus: 600 eccezioni nel bus, 3000 entry pendenti, heap +995 KB. Con `TrySet*`, il token passato a `TrySetCanceled`, registration disposte e `TryRemove` in `finally`: 0, 0, +2 KB. |
| 1.3 | `manual-reset-event` | Un `Reset` ingenuo perde i waiter: 0/3 ripartono. Con `CompareExchange`: 3/3. |

### Demo 2 — Un valore, molti consumatori: AsyncLazy e async caching

[demos/02-AsyncCaching](demos/02-AsyncCaching): spiegazione, codice e output nel [README della Demo](demos/02-AsyncCaching/README-it.md). L'`AsyncLazy<T>` finale ([AsyncLazy.cs](demos/02-AsyncCaching/AsyncLazy.cs)) risolve le tre trappole di 2.2–2.4.

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 2.1 | `cache-stampede` | 200 richieste su una chiave fredda: cache del valore → 200 chiamate al provider, `GetOrAdd` sul `Task` → 16. Con `Lazy<Task<T>>`: 1. |
| ★ 2.2 | `failure-caching` | Un `Task` fallito resta in cache: 5/5 richieste falliscono. `AsyncLazy` che si resetta sul fallimento: 1/5. |
| ★ 2.3 | `shared-cancellation` | Il token del primo chiamante cancella il load di tutti: 0/2 serviti, e la cancellazione resta in cache. Load senza token + `WaitAsync(ct)`: 2/2. |
| 2.4 | `factory-context` | Il load condiviso gira nel contesto del primo chiamante (correlation id `req-A`). Con `SuppressFlow`: `(none)`. |
| 2.5 | `hybrid-cache` | `HybridCache`: stampede → 1 chiamata. Se se ne va un chiamante il load prosegue; se se ne vanno tutti viene cancellato, e non resta in cache. |

### Demo 3 — Hot path: ValueTask

[demos/03-ValueTask](demos/03-ValueTask): spiegazione, codice, output e benchmark nel [README della Demo](demos/03-ValueTask/README-it.md). Da eseguire in Release: `dotnet run -c Release --project demos/03-ValueTask -- 3.1`.

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 3.1 | `cache-hit` | Byte per cache hit. State machine: `Task<T>` 80, `ValueTask<T>` 0. Runtime Async: `Task<T>` awaitato direttamente **0**, 80 appena diventa un oggetto (delegate, campo, array). |
| ★ 3.2 | `value-task-source` | `IValueTaskSource` pooled: 0 byte/operazione contro 136 di una TCS. Due await sullo stesso `ValueTask` → `InvalidOperationException`; una sorgente senza token restituisce il prezzo di AAPL come MSFT. |

Benchmark: il pooling builder porta la sospensione a 0 B, ma fa uscire il metodo da Runtime Async: le due ottimizzazioni non si sommano.

### Demo 4 — Contesto e ciclo di vita: AsyncLocal e IAsyncDisposable

[demos/04-AsyncContext](demos/04-AsyncContext): spiegazione, codice e output nel [README della Demo](demos/04-AsyncContext/README-it.md).

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 4.1 | `asynclocal-flow` | Un `AsyncLocal` impostato in un metodo `async` non torna al chiamante: `(null)`, anche prima del primo `await`. Fixed: setter sincrono, risultato assegnato dal chiamante, holder mutabile. Identico con `-p:RuntimeAsync=false`. |
| ★ 4.2 | `context-leak` | Il loop di background avviato nella prima richiesta si tiene il suo tenant: 4/4 righe `acme`. Con `SuppressFlow` e `UnsafeRegister`: 0/4. |
| 4.3 | `async-dispose` | Scope DI disposto in modo sincrono → `InvalidOperationException`; `DisposeAsync` che non aspetta il flush → 20/25 allo shutdown. Con `CreateAsyncScope` e un `DisposeAsync` che aspetta: 25/25. |

### Demo 5 — Molti valori, pull: Channels

[demos/05-Channels](demos/05-Channels): spiegazione, codice e output nel [README della Demo](demos/05-Channels/README-it.md).

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 5.1 | `backpressure` | Producer ~1640 tick/s, consumer 164. Unbounded: ~3700 tick in coda dopo 2,5 s, prezzo persistito vecchio di 2,2 s. Bounded `Wait`: il producer rallenta a ~200 tick/s. Bounded `DropOldest`: ~3600 scarti contati da `itemDropped`, prezzo fresco. |
| ★ 5.2 | `pipeline` | Il primo worker che finisce chiude il canale: "completato" con 156/160 persistiti; con il provider giù la pipeline resta appesa. Fixed: completamento dopo tutti i worker, errore con `Complete(ex)`: 159 + 1 in dead letter, oppure la `ProviderException` originale in ~25 ms. |
| 5.3 | `when-each` | `Task.WhenEach` (risultati in ordine di arrivo) e `Channel.CreateUnboundedPrioritized` (gli ordini urgenti scavalcano la coda). |

### Demo 6 — Molti valori, push: Task ↔ Rx.NET, e AsyncRx

[demos/06-RxInterop](demos/06-RxInterop): spiegazione, codice e output nel [README della Demo](demos/06-RxInterop/README-it.md).

| Id | Nome | Broken → Fixed |
|---|---|---|
| ★ 6.1 | `task-observable` | `await observable` dà l'*ultimo* valore, solleva un'eccezione se vuoto, non torna se infinito → `FirstAsync()`. `task.ToObservable().Retry(3)`: 3 tentativi, **1 sola chiamata** → `Observable.FromAsync(ct => …)`: 3 chiamate, e il dispose cancella il token. |
| ★ 6.2 | `async-in-pipeline` | `Subscribe(async …)`: 6 in volo, `OnCompleted` a ~400 ms con lavoro fino a ~670 ms; `SelectMany(async …)`: nessun limite né cancellazione. Fixed: `Select(FromAsync)` + `Concat()` / `Merge(4)` / `Switch()` (7 chiamate cancellate). |
| ★ 6.3 | `rx-to-channel` | `ToAsyncEnumerable()` di Ix: coda nascosta senza limite (~500.000 elementi, +34 MB, prezzo vecchio di 2 s). Con un `Channel` bounded(8) `DropOldest`: backlog 7, scarti contati, prezzo fresco. |
| 6.4 | `refreshing-cache` | `Replay(1).RefCount()` letto con `await FirstAsync()`: 12 connessioni, il timer non scatta mai, **stale per sempre**. Con `AutoConnect()`: 1 connessione, refresh dal timer. |
| ★ 6.5 | `asyncrx-backpressure` | Rx classico: 5 in volo, `OnCompleted` prima della fine del lavoro. AsyncRx: 1 in volo, il timer rallenta al passo del consumer, `OnCompletedAsync` onesto. |
| ★ 6.6 | `asyncrx-bridge` | `Subject<T>.ToAsyncObservable()` blocca il producer hot per ~1,5 s (sync-over-async, commento `REVIEW` nel sorgente AsyncRx). Con un `Channel` bounded `DropOldest` al confine: 1 ms. |
| 6.7 | `asyncrx-lifecycle` | `Observable.Create` async: a fine `Subscribe` non sei connesso, a fine `Dispose` gli offset non sono committati. `AsyncObservable.Create` + `IAsyncDisposable`: setup e teardown awaitable. |

## Approfondimenti

- [`Task.WaitAsync`: smettere di aspettare senza fermare il lavoro](docs/wait-async-it.md): le scorciatoie, come si smonta, e perché cancella l'attesa e non il lavoro. Usato negli scenari 1.2 e 2.3.

## Note

- **Runtime Async** in .NET 11 GA resta *opt-in* per il tuo codice: senza `<Features>runtime-async=on</Features>` il compilatore genera ancora le state machine (non serve più `EnablePreviewFeatures`). Le librerie del runtime sono già compilate così. La documentazione cita anche `<UseRuntimeAsync>false</UseRuntimeAsync>` come opt-out, ma l'SDK 11.0.100 non lo usa: in questo repo si usa `-p:RuntimeAsync=false`.
- **AsyncRx.NET** (`System.Reactive.Async`) è sperimentale: l'unica build su NuGet è `6.0.0-alpha.18` (2023), che funziona con Rx 7.
- **BenchmarkDotNet 0.15.x non riconosce .NET 11** (`GetRuntimeVersion not implemented for NotRecognized`): il repo usa `0.16.0-preview.2`.
- I benchmark girano nella stessa build del harness di BenchmarkDotNet, che quindi è compilato anch'esso con o senza Runtime Async: un `Task` restituito al harness viene awaitato direttamente e con Runtime Async non viene materializzato.
- [nuget.config](nuget.config) usa solo nuget.org, così il repo compila ovunque indipendentemente dai feed configurati sulla macchina.
