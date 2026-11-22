# Demo 6 — Molti valori, push: Task ↔ Rx.NET, e AsyncRx

La [Demo 5](../05-Channels) trattava molti valori in *pull*: il consumer legge da un `Channel` quando è pronto.
Qui i valori arrivano in *push*: decide il producer quando, e il consumer viene chiamato.

`Task<T>` e `IObservable<T>` sono i due estremi: un valore che chiedi e poi aspetti, contro una sequenza di valori che ti viene consegnata.
In QuoteHub convivono: le chiamate al provider di quotazioni sono `Task`, i prezzi pubblicati sono uno stream Rx, la configurazione si ricarica da sola con un timer, e chi consuma vuole a volte un prezzo solo (`await`), a volte un `IAsyncEnumerable`.
Rx converte in entrambe le direzioni, ma ogni conversione ha una semantica precisa, e gli scenari 6.1–6.4 mostrano dove sorprende: quale valore restituisce `await observable`, cosa ripete `Retry`, cosa cancella il dispose, dove si accumula l'arretrato.

Il limite di fondo di Rx classico è che `IObserver<T>.OnNext` è `void`: un observer che fa lavoro async non può dire al producer di aspettare, e anche `Subscribe` e `Dispose` sono sincroni.
**AsyncRx.NET** (`System.Reactive.Async`) definisce `IAsyncObservable<T>` e `IAsyncObserver<T>`: `OnNextAsync` restituisce un `ValueTask` che il producer awaita (backpressure per costruzione), `SubscribeAsync` si awaita, e la sottoscrizione si chiude con `DisposeAsync`.
È sperimentale, l'unica build su NuGet è un'alpha del 2023, ma gli scenari 6.5–6.7 mostrano il problema che risolve, e il 6.6 dove si ferma.

## Come si lancia

```bash
dotnet run --project demos/06-RxInterop -- 6.1                     # broken, Invio, fixed
dotnet run --project demos/06-RxInterop -- asyncrx-bridge --fixed
dotnet run --project demos/06-RxInterop -- all --no-pause          # tutti gli scenari, entrambe le varianti
```

Da IDE ci sono il profilo `menu` e un profilo per scenario, da `6.1 task-observable` a `6.7 asyncrx-lifecycle` ([launchSettings.json](Properties/launchSettings.json)).
Da terminale lo stesso profilo si sceglie con `--launch-profile "6.3 rx-to-channel"`.

I pacchetti ([Demo06.RxInterop.csproj](Demo06.RxInterop.csproj), versioni in [Directory.Packages.props](../../Directory.Packages.props)):

| Pacchetto | Versione | Cosa usa la Demo |
|---|---|---|
| `System.Reactive` | 7.0.0 | Rx.NET: `Observable`, `Subject<T>`, `ToObservable`/`ToTask`, `FromAsync`, `Concat`/`Merge`/`Switch`, `Replay`/`RefCount`/`AutoConnect` |
| `System.Interactive.Async` | 7.0.1 | Ix: `IObservable<T>.ToAsyncEnumerable()` (6.3) |
| `System.Reactive.Async` | 6.0.0-alpha.18 | AsyncRx.NET, sperimentale: `IAsyncObservable<T>`, `AsyncObservable.Create`/`Interval`, `ToAsyncObservable()` (6.5–6.7) |

Gli output qui sotto vengono da un'esecuzione in Debug su .NET 11.0.0.
Millisecondi, ordine di completamento e i conteggi che dipendono dai tempi cambiano da un'esecuzione all'altra: sono i valori di quella esecuzione.

## ★ 6.1 `task-observable` — await, Retry e cancellazione tra Task e IObservable

Tre conversioni fra i due mondi, in [TaskObservableDemo.cs](TaskObservableDemo.cs), con una semantica che non coincide con quella del `Task`.

**`await` su un observable.** `IObservable<T>` è awaitabile, ma l'`await` aspetta che la sequenza *completi* e restituisce l'*ultimo* valore.

```csharp
static IObservable<decimal> PriceTicks() =>
    Observable.Interval(TimeSpan.FromMilliseconds(100)).Take(3).Select(i => 100m + i);

var price = await PriceTicks();
// …
await Observable.Empty<Quote>();
// …
await Observable.Interval(TimeSpan.FromMilliseconds(100)).ToTask(giveUp.Token);
```

```text
✘ await prices → 102 after 330 ms: the LAST value, only when the stream completes
✘ await Observable.Empty() → InvalidOperationException: Sequence contains no elements.
✘ await Observable.Interval(...) → never completes (gave up after 500 ms)
```

Chi scrive `await prices` intende "il prezzo corrente" e ottiene il terzo e ultimo, dopo 330 ms.
Una sequenza vuota non ha un valore da restituire, quindi `InvalidOperationException`; una sequenza infinita non completa mai, e nella demo serve un token per smettere di aspettarla dopo 500 ms.

La correzione è dire quale valore si vuole:

```csharp
var price = await PriceTicks().FirstAsync();
// …
var none = await Observable.Empty<Quote>().LastOrDefaultAsync();
// …
var tick = await Observable.Interval(TimeSpan.FromMilliseconds(100)).FirstAsync();
```

```text
✔ await prices.FirstAsync() → 100 after 111 ms
✔ await Observable.Empty().LastOrDefaultAsync() → null (the empty case is explicit)
✔ await Observable.Interval(...).FirstAsync() → 0
```

**`Retry` su un Task.** Il provider fallisce le prime due chiamate, e la pipeline ritenta con `Retry(3)`.

```csharp
IObservable<Quote> quotes = variant == Variant.Broken
    ? provider.GetQuoteAsync("MSFT").ToObservable()                         // la chiamata parte qui, una volta
    : Observable.FromAsync(ct => provider.GetQuoteAsync("MSFT", ct));        // una chiamata nuova per ogni sottoscrizione

var quote = await quotes
    .Do(_ => { }, ex => Out.Log($"attempt {++attempts} failed: {ex.Message}"))
    .Retry(3);
```

```text
   856 ms [t  7] provider → MSFT (call #1)
   958 ms [t  7] provider ✘ MSFT (call #1) failed
   959 ms [t  7] attempt 1 failed: Provider failed for MSFT (call #1)
   962 ms [t  7] attempt 2 failed: Provider failed for MSFT (call #1)
   962 ms [t  7] attempt 3 failed: Provider failed for MSFT (call #1)
✘ Gave up: Provider failed for MSFT (call #1)
  Failed attempts seen by Retry    3
  Provider calls                   1
```

`GetQuoteAsync` viene chiamato mentre si costruisce l'observable, prima di qualsiasi sottoscrizione: `ToObservable()` avvolge un `Task` già partito.
`Retry` si risottoscrive, ma allo stesso `Task` già fallito: tre tentativi in 3 ms, sempre l'errore di `call #1`, una sola chiamata al provider.

Con `Observable.FromAsync` la factory gira a ogni sottoscrizione, e ogni tentativo è una chiamata vera:

```text
   223 ms [t  7] provider → MSFT (call #1)
   331 ms [t  7] provider ✘ MSFT (call #1) failed
   332 ms [t  7] attempt 1 failed: Provider failed for MSFT (call #1)
   332 ms [t  7] provider → MSFT (call #2)
   442 ms [t  7] provider ✘ MSFT (call #2) failed
   442 ms [t  7] attempt 2 failed: Provider failed for MSFT (call #2)
   442 ms [t  7] provider → MSFT (call #3)
   552 ms [t  7] provider ← MSFT = 281.34 (call #3)
✔ Got MSFT = 281.34
  Failed attempts seen by Retry    2
  Provider calls                   3
```

**Cancellazione.** Stesse due sorgenti, provider da 200 ms, dispose della sottoscrizione dopo 50 ms.

```csharp
var subscription = quote.Subscribe(q => Out.Log($"subscriber got {q.Price}"));
await Task.Delay(50);
subscription.Dispose();
```

Con `ToObservable()` il dispose smette di consegnare il risultato, ma la chiamata va avanti fino alla fine:

```text
   964 ms [t  7] provider → MSFT (call #1)
  1020 ms [t  7] subscription disposed
  1177 ms [t  7] provider ← MSFT = 281.34 (call #1)
  Provider calls cancelled         0
✘ Nobody is listening, but the call ran to completion anyway
```

Con `FromAsync(ct => …)` Rx cancella il token quando si fa dispose della sottoscrizione:

```text
   554 ms [t  7] provider → MSFT (call #1)
   614 ms [t  7] subscription disposed
   615 ms [t  8] provider ⊘ MSFT (call #1) cancelled
  Provider calls cancelled         1
✔ Disposing the subscription cancelled the CancellationToken
```

Il messaggio: `ToObservable()` adatta un `Task` che esiste già, un risultato solo e condiviso.
Un'*operazione* da ripetere o cancellare si porta in Rx con `Observable.FromAsync(ct => …)`: lazy, una chiamata per sottoscrizione, token legato al dispose.

## ★ 6.2 `async-in-pipeline` — chiamate async dentro una pipeline Rx

Otto richieste di prezzo, una ogni 50 ms; per ognuna una chiamata async che dura fra 110 e 400 ms, con durate diverse per richiesta, quindi l'ordine di completamento non è quello di arrivo.
Un probe in [AsyncInPipelineDemo.cs](AsyncInPipelineDemo.cs) conta le chiamate in volo, l'ordine di completamento e le cancellazioni.

**`Subscribe(async …)`.** Il modo che viene in mente per primo:

```csharp
Requests().Subscribe(
    async seq =>
    {
        // async void: Rx non lo aspetta. Se sollevasse un'eccezione, andrebbe al thread pool
        // e terminerebbe il processo: OnError non la vedrebbe mai.
        var quote = await probe.PriceAsync(seq);
        Out.Log($"#{seq} {quote.Symbol} = {quote.Price}");
    },
    () =>
    {
        Out.Log("OnCompleted");
        completed.SetResult(Out.ElapsedMs);
    });
```

```text
   267 ms [t  8] #1 AAPL = 193.34
   329 ms [t  8] #3 AMZN = 336.28
   409 ms [t  7] OnCompleted
   472 ms [t  7] #2 NVDA = 473.25
   487 ms [t  8] #0 MSFT = 281.05
   …
   675 ms [t  8] #7 ORCL = 301.03
  Max in flight                    6
  OnCompleted at                   409 ms
  Last result at                   675 ms
✘ The stream said "completed" while the work was still running
```

`OnNext` è `void`, quindi la lambda è `async void`: Rx la chiama, riprende il controllo al primo `await` e passa all'elemento successivo.
Nessun limite (6 chiamate in volo), nessun ordine, e `OnCompleted` arriva quando completa la *sorgente*, a 409 ms, mentre l'ultimo risultato arriva a 675 ms.

**`SelectMany(async …)`.** Aspetta i risultati, e il suo `OnCompleted` arriva dopo l'ultimo:

```csharp
var quotes = await Requests().SelectMany(async seq => await probe.PriceAsync(seq)).ToList();
```

```text
── SelectMany(async x => ...)
  Results                          8
  Max in flight                    6
  Completion order                 #1 #3 #2 #0 #6 #5 #4 #7

── SelectMany(async x => ...), unsubscribed after 200 ms
  1582 ms [t  8] subscription disposed
  Calls cancelled                  0
  Calls completed after Dispose    4
✘ Nobody listens any more, the calls run to the end anyway
```

Però è un merge senza limite: tutto in parallelo, risultati in ordine di completamento.
E la lambda non riceve un token: dopo il dispose 4 chiamate arrivano comunque in fondo.

**Fixed.** Ogni richiesta diventa un `Observable.FromAsync`, che non parte finché qualcuno non si sottoscrive.
Il risultato è un `IObservable<IObservable<Quote>>`, e la politica la sceglie l'operatore che lo appiattisce:

```csharp
await ComposeAsync("Select(FromAsync).Concat() — one at a time, in order", s => s.Concat());
await ComposeAsync("Select(FromAsync).Merge(4) — at most 4 in flight", s => s.Merge(4));
await ComposeAsync("Select(FromAsync).Switch() — latest wins, stale calls cancelled", s => s.Switch());

// … dentro ComposeAsync
var calls = Requests().Select(seq => Observable.FromAsync(ct => probe.PriceAsync(seq, ct)));
var quotes = await compose(calls).ToList();
```

| Operatore | Max in volo | Ordine di completamento | Cancellate | Tempo |
|---|---|---|---|---|
| `Concat()` | 1 | #0 #1 #2 #3 #4 #5 #6 #7 | 0 | 2047 ms |
| `Merge(4)` | 4 | #1 #3 #2 #0 #5 #6 #4 #7 | 0 | 755 ms |
| `Switch()` | 2 | #7 (un solo risultato) | 7 | 680 ms |

- `Concat()` si sottoscrive alla chiamata successiva solo quando la precedente ha completato: in ordine, una alla volta, e il tempo totale è circa la somma delle durate.
- `Merge(4)` tiene al massimo 4 chiamate aperte: il provider non vede più di 4 richieste concorrenti.
- `Switch()` a ogni nuova richiesta si disiscrive dalla precedente, e il dispose cancella il token di `FromAsync`. Le richieste arrivano ogni 50 ms e nessuna chiamata dura meno di 110 ms, quindi arriva solo l'ultima:

```text
  2912 ms [t  5] ⊘ #0 MSFT cancelled
  2960 ms [t  8] ⊘ #1 AAPL cancelled
  …
  3214 ms [t  9] ⊘ #6 TSLA cancelled
  Results                          1
  Max in flight                    2
  Completion order                 #7
  Calls cancelled                  7
```

Il messaggio: il lavoro async in una pipeline va espresso come observable (`FromAsync`), non come callback.
Così l'operatore decide concorrenza, ordine e cancellazione: in ordine, al massimo *n*, oppure solo l'ultimo.

## ★ 6.3 `rx-to-channel` — Rx → IAsyncEnumerable: chi tiene l'arretrato

Un `Subject<Quote>` hot (market data che arrivano dalla callback di un socket e non si possono rallentare) produce 500.000 tick in circa 2 secondi.
Il consumer è un `await foreach` che lavora 10 ms per elemento.
Push contro pull, in [RxToChannelDemo.cs](RxToChannelDemo.cs): la differenza va messa in coda o scartata da qualche parte.

```csharp
// Market data hot: arrivano dalla callback di un socket, non si possono rallentare.
var ticks = new Subject<Quote>();
// …
stream = ticks.ToAsyncEnumerable();
// …
await foreach (var quote in stream.WithCancellation(stop.Token))
{
    Volatile.Write(ref lastAgeMs, (DateTimeOffset.UtcNow - quote.Timestamp).TotalMilliseconds);
    Interlocked.Increment(ref consumed);
    await Task.Delay(ConsumerWork, stop.Token);
}
```

```text
ticks.ToAsyncEnumerable()  — AsyncEnumerableEx, System.Interactive.Async 7
   424 ms [t  5] produced 100,000 │ consumed  26 │ dropped       0 │ backlog  99,974 │ heap   +7.1 MB │ age of price in use   388 ms
   …
  2021 ms [t  8] produced 500,000 │ consumed 128 │ dropped       0 │ backlog 499,873 │ heap  +34.5 MB │ age of price in use 1,979 ms
  Backlog when we stopped          499,872
  Dropped (counted)                0
  Age of the last price processed  1,979 ms
✘ Unbounded hidden queue: memory grows and the consumer processes stale prices
```

`ToAsyncEnumerable()` non può rallentare il `Subject`, perché `OnNext` è `void`: tutto quello che il consumer non ha ancora chiesto finisce in una coda interna senza limite.
L'arretrato cresce con la produzione (quasi 500.000 elementi), lo heap di 34,5 MB in 2 secondi, e il consumer lavora su un prezzo vecchio di quasi 2 secondi.
Nessuna eccezione e nessun contatore lo segnalano.

La correzione mette al confine un `Channel` bounded, con una capacità e una politica scelte:

```csharp
var channel = Channel.CreateBounded<Quote>(
    new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
    itemDropped: _ => Interlocked.Increment(ref dropped));

bridge = ticks.Subscribe(
    quote => channel.Writer.TryWrite(quote),
    ex => channel.Writer.TryComplete(ex),
    () => channel.Writer.TryComplete());
stream = channel.Reader.ReadAllAsync();
```

```text
ticks → Channel.CreateBounded(8, DropOldest) → ReadAllAsync()
   394 ms [t  9] produced 100,000 │ consumed  26 │ dropped  99,967 │ backlog       8 │ heap   +0.0 MB │ age of price in use    14 ms
   …
  1953 ms [t  5] produced 500,000 │ consumed 126 │ dropped 499,867 │ backlog       7 │ heap   +0.0 MB │ age of price in use    14 ms
  Backlog when we stopped          7
  Dropped (counted)                499,867
  Age of the last price processed  14 ms
✔ Bounded buffer: the drop policy is explicit, memory is flat, prices are fresh
```

Il consumer elabora più o meno lo stesso numero di elementi (126 contro 128): la sua velocità non cambia.
Cambia *cosa* elabora: un prezzo di 14 ms fa invece che di 2 secondi fa, con memoria piatta e gli scarti contati da `itemDropped`.
Errori e completamento della sorgente passano al channel con `TryComplete`.

Il messaggio: passare da push a pull vuol dire tenere l'arretrato da qualche parte, e conviene che sia un buffer di cui hai scelto la dimensione.
Come in [Demo 5](../05-Channels), la politica è una decisione di business: per un prezzo conta l'ultimo, quindi `DropOldest`.

## 6.4 `refreshing-cache` — cache async che si aggiorna da sola: Replay(1) + RefCount vs AutoConnect

Il problema della [Demo 2](../02-AsyncCaching) (un valore, molti consumatori) in Rx, in [RefreshingCacheDemo.cs](RefreshingCacheDemo.cs).
Una configurazione si carica in modo async (100 ms) e un timer la ricarica ogni 500 ms; 12 consumatori, uno ogni 100 ms, la leggono con `await config.FirstAsync()`.

```csharp
var replayed = Observable.Defer(() =>
    {
        Interlocked.Increment(ref connections);
        return Observable.Timer(TimeSpan.Zero, RefreshPeriod);
    })
    .Select(_ => Observable.FromAsync(LoadAsync))
    .Switch()
    .Replay(1);

IDisposable? connection = null;
var config = variant == Variant.Broken
    ? replayed.RefCount()
    : replayed.AutoConnect(1, c => connection = c);
// …
var current = await config.FirstAsync();
```

Timer → un caricamento per tick (`FromAsync`) → `Switch` (un caricamento ancora in corso al tick successivo viene cancellato) → `Replay(1)`, che tiene l'ultima configurazione e la dà subito a chi si sottoscrive.
`Defer` conta quante volte la sorgente viene connessa.

Con `RefCount()`:

```text
12 consumers, one every 100 ms; the timer refreshes every 500 ms
   122 ms [t  9]   config load #1 completed
   122 ms [t  9] call  1 → config v1 (119 ms)
   232 ms [t  9] call  2 → config v1 (0 ms)
   …
  1324 ms [t  5] call 12 → config v1 (0 ms)
  Connections to the source        12
  Loads the timer asked for        3
  Loads completed                  1
  Loads cancelled                  0
✘ RefCount: every await reconnects, the replayed v1 answers first, the timer never ticks — stale forever
```

Ogni `await` su un observable è una sottoscrizione nuova, e `FirstAsync()` dura solo fino al primo valore.
`RefCount` connette la sorgente quando arriva il primo sottoscrittore e la disconnette quando non ne resta nessuno: qui succede a ogni chiamata, 12 connessioni.
Dalla seconda chiamata in poi `Replay(1)` ha già v1 e risponde subito (0 ms): il consumer se ne va, la connessione cade e il timer nuovo non fa in tempo a scattare.
Risultato: un caricamento su tre, nessuno cancellato, e la cache resta a v1 per sempre senza errori.

Con `AutoConnect(1, …)`:

```text
   109 ms [t  7]   config load #1 completed
   110 ms [t  7] call  1 → config v1 (110 ms)
   …
   545 ms [t  7] call  5 → config v1 (0 ms)
   623 ms [t  7]   config load #2 completed
   654 ms [t  7] call  6 → config v2 (0 ms)
   …
  1121 ms [t  7]   config load #3 completed
  1199 ms [t  7] call 11 → config v3 (0 ms)
  1309 ms [t  7] call 12 → config v3 (0 ms)
  Connections to the source        1
  Loads the timer asked for        3
  Loads completed                  3
  Loads cancelled                  0
✔ AutoConnect: one connection, refreshed by the timer, consumers get the replayed value
```

`AutoConnect` connette al primo sottoscrittore e non disconnette più da solo: il timer gira, i caricamenti arrivano ogni 500 ms, i consumatori leggono l'ultimo valore in 0 ms.
Il prezzo è che la connessione va chiusa a mano: la demo la riceve dal callback `c => connection = c` e alla fine chiama `connection?.Dispose()`.

Il messaggio: `Replay(1).RefCount()` va bene per sottoscrittori che restano; una cache letta con `await FirstAsync()` ha solo sottoscrizioni di un istante, e `RefCount` spegne il refresh.

## ★ 6.5 `asyncrx-backpressure` — AsyncRx: il producer aspetta il consumer

Nel 6.2 il problema di fondo era che `OnNext` è `void`: un observer async non può far aspettare la sorgente.
[AsyncRxBackpressureDemo.cs](AsyncRxBackpressureDemo.cs) mette lo stesso consumer (una chiamata al provider da 500 ms) su un timer da 100 ms, una volta con Rx classico e una con AsyncRx.

```csharp
async ValueTask HandleAsync(long tick)
{
    lock (starts)
        starts.Add(Out.ElapsedMs);
    Out.Log($"start #{tick} (in flight: {provider.InFlight + 1})");
    await provider.GetQuoteAsync("MSFT");
    Out.Log($"end   #{tick}");
    Interlocked.Exchange(ref lastEnd, Out.ElapsedMs);
}
```

Rx classico, con una lambda `async void`:

```csharp
Observable.Interval(Period).Take(Ticks).Subscribe(
    async tick => await HandleAsync(tick),
    () =>
    {
        Out.Log("OnCompleted");
        completed.SetResult(Out.ElapsedMs);
    });
```

```text
Observable.Interval(100 ms).Take(5).Subscribe(async x => ...)
   110 ms [t  7] start #0 (in flight: 1)
   217 ms [t  7] start #1 (in flight: 2)
   310 ms [t  7] start #2 (in flight: 3)
   420 ms [t  7] start #3 (in flight: 4)
   514 ms [t  7] start #4 (in flight: 5)
   514 ms [t  7] OnCompleted
   623 ms [t  5] end   #0
   …
  1028 ms [t  7] end   #4
  Max calls in flight              5
  Time between ticks (avg)         101 ms
  OnCompleted at                   515 ms
  Last work finished at            1028 ms
✘ No backpressure and a lying OnCompleted: close the DB connection now and the handlers break
```

Il timer va al suo passo, le chiamate si accumulano (5 in volo) e `OnCompleted` arriva a 515 ms, con il lavoro che finisce a 1028.
Chi chiude una risorsa su `OnCompleted` la chiude sotto i piedi degli handler ancora in corso.

AsyncRx, con lo stesso handler:

```csharp
await AsyncObservable.Interval(Period).Take(Ticks).SubscribeAsync(
    HandleAsync,
    ex =>
    {
        completed.TrySetException(ex);
        return ValueTask.CompletedTask;
    },
    () =>
    {
        Out.Log("OnCompletedAsync");
        completed.TrySetResult(Out.ElapsedMs);
        return ValueTask.CompletedTask;
    });
```

```text
AsyncObservable.Interval(100 ms).Take(5).SubscribeAsync(async x => ...)
   119 ms [t  9] start #0 (in flight: 1)
   630 ms [t  9] end   #0
   740 ms [t  9] start #1 (in flight: 1)
  1254 ms [t  9] end   #1
  1363 ms [t  7] start #2 (in flight: 1)
   …
  2610 ms [t  5] start #4 (in flight: 1)
  3111 ms [t  5] end   #4
  3112 ms [t  5] OnCompletedAsync
  Max calls in flight              1
  Time between ticks (avg)         623 ms
  OnCompleted at                   3112 ms
  Last work finished at            3111 ms
✔ Backpressure by await: the timer slowed down to the consumer's pace, OnCompleted is honest
```

`HandleAsync` passa così com'è: è l'`OnNextAsync` dell'observer, e restituisce un `ValueTask` che la sorgente awaita prima di emettere il valore successivo.
Una chiamata in volo alla volta, e il tick successivo arriva circa 100 ms dopo la fine del lavoro precedente (630 → 740, 1254 → 1363): il timer si è adeguato al passo del consumer, 623 ms in media fra un tick e l'altro invece di 101.
`OnCompletedAsync` arriva a 3112 ms, dopo l'ultimo lavoro (3111): ora dice il vero.

Il messaggio: in AsyncRx l'observer async fa parte del protocollo, non è una callback lanciata e dimenticata, e la backpressure è un `await`.
Funziona perché la sorgente, un timer AsyncRx, può aspettare: il 6.6 mostra cosa succede quando non può.

## ★ 6.6 `asyncrx-bridge` — AsyncRx: il confine sync → async

AsyncRx non fa sparire la backpressure, la sposta al confine.
In QuoteHub i prezzi arrivano da un `Subject<Quote>` di Rx classico: `OnNext` è `void`, e il producer non può awaitare niente.
In [AsyncRxBridgeDemo.cs](AsyncRxBridgeDemo.cs) il producer spinge 5 prezzi, uno ogni 50 ms, verso un observer AsyncRx che lavora 300 ms per prezzo.

```csharp
var ticks = new Subject<Quote>();
// …
source = ticks.ToAsyncObservable();
// …
await using var subscription = await source.SubscribeAsync(HandleAsync, OnError, OnCompleted);
await Task.Delay(100); // ToAsyncObservable si sottoscrive sul task pool

for (var i = 0; i < Pushes; i++)
{
    var before = Out.ElapsedMs;
    ticks.OnNext(new Quote("MSFT", 100m + i, DateTimeOffset.UtcNow));
    var elapsed = Out.ElapsedMs - before;
    // …
    await Task.Delay(PushInterval);
}
```

```text
ticks.ToAsyncObservable().SubscribeAsync(async q => ...)
   438 ms [t  9]   handled MSFT 100
   439 ms [t  5] producer: OnNext(100) returned after 316 ms
   812 ms [t  9]   handled MSFT 101
   812 ms [t  5] producer: OnNext(101) returned after 311 ms
   …
  1931 ms [t  9]   handled MSFT 104
  1931 ms [t  5] producer: OnNext(104) returned after 312 ms
  Producer blocked in OnNext (total) 1559 ms
  Quotes handled                   5
  Quotes dropped (counted)         0
✘ The hot producer's thread was blocked: sync-over-async inside the bridge. From the AsyncRx source:
  // REVIEW: Add a way to parameterize blocking behavior (e.g. blocking, fire-and-forget, async chaining).
```

Ogni `OnNext` ritorna solo quando l'handler ha finito, dopo circa 310 ms: il thread del producer (`t 5`) resta fermo mentre l'handler gira su un altro (`t 9`), 1559 ms in tutto per 5 prezzi.
Nel [sorgente di AsyncRx.NET](https://github.com/dotnet/reactive/blob/asyncrxnet-v6.0.0-alpha.18/AsyncRx.NET/System.Reactive.Async/Linq/Operators/ToAsyncObservable.cs) `ToAsyncObservable()` si sottoscrive alla sorgente classica con un `IObserver<T>` adattatore il cui `OnNext` fa `OnNextAsync(value).GetAwaiter().GetResult()`: sync-over-async.
Sopra l'adattatore c'è il commento `REVIEW` che la demo stampa: il comportamento al confine è un punto ancora aperto della libreria.

La correzione è quella della Demo 5 e del 6.3: un confine esplicito, un `Channel` bounded con `DropOldest`, e dall'altra parte un `IAsyncObservable` che legge dal channel e awaita l'observer prima di passare all'elemento successivo.

```csharp
var channel = Channel.CreateBounded<Quote>(
    new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest },
    itemDropped: q =>
    {
        Interlocked.Increment(ref dropped);
        Out.Log($"  dropped {q.Symbol} {q.Price}");
    });
bridge = ticks.Subscribe(q => channel.Writer.TryWrite(q), ex => channel.Writer.TryComplete(ex), () => channel.Writer.TryComplete());
source = FromChannel(channel.Reader);

// …
static IAsyncObservable<T> FromChannel<T>(ChannelReader<T> reader) =>
    AsyncObservable.Create<T>(observer =>
    {
        var stop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in reader.ReadAllAsync(stop.Token))
                    await observer.OnNextAsync(item);
                await observer.OnCompletedAsync();
            }
            // … catch: la cancellazione chiude in silenzio, gli altri errori vanno a OnErrorAsync
        });

        return ValueTask.FromResult(AsyncDisposable.Create(async () =>
        {
            await stop.CancelAsync();
            await pump;
            stop.Dispose();
        }));
    });
```

```text
ticks → Channel.CreateBounded(2, DropOldest) → IAsyncObservable paced by its observer
   117 ms [t  5] producer: OnNext(100) returned after 0 ms
   180 ms [t  8] producer: OnNext(101) returned after 0 ms
   244 ms [t  8] producer: OnNext(102) returned after 0 ms
   308 ms [t  8]   dropped MSFT 101
   308 ms [t  8] producer: OnNext(103) returned after 1 ms
   370 ms [t  8]   dropped MSFT 102
   370 ms [t  8] producer: OnNext(104) returned after 0 ms
   417 ms [t  8]   handled MSFT 100
   730 ms [t  8]   handled MSFT 103
  1045 ms [t  8]   handled MSFT 104
  Producer blocked in OnNext (total) 1 ms
  Quotes handled                   3
  Quotes dropped (counted)         2
✔ The producer never waits: the boundary has a size and a drop policy you chose
```

Il producer scrive con `TryWrite` e non aspetta mai: 1 ms in tutto.
Il prezzo 100 va subito all'handler, 101 e 102 aspettano nel buffer da 2, poi 103 e 104 li spingono fuori: elaborati 100, 103 e 104, scartati 101 e 102, contati e loggati.
La backpressure di AsyncRx lavora fra il channel e l'observer; la differenza con il producer la assorbe il channel, con la politica scelta.

Il messaggio: dentro il mondo async AsyncRx dà la backpressure con un `await`, ma al confine con una sorgente che non può aspettare qualcuno deve decidere dove va l'eccesso.
Meglio un buffer esplicito, limitato e contato che un thread del producer bloccato.

## 6.7 `asyncrx-lifecycle` — AsyncRx: subscribe e dispose async

Una sottoscrizione che possiede una risorsa con setup e teardown async, in [AsyncRxLifecycleDemo.cs](AsyncRxLifecycleDemo.cs): connettersi a un broker (150 ms) e, alla fine, committare gli offset e chiudere (200 ms).
L'app si sottoscrive, legge per 300 ms, fa dispose e "si spegne".

Con Rx classico `Observable.Create` accetta una lambda async, ma il contratto resta sincrono:

```csharp
var quotes = Observable.Create<Quote>(async (observer, cancellationToken) =>
{
    await broker.ConnectAsync(cancellationToken);
    _ = PumpAsync(q => { observer.OnNext(q); return ValueTask.CompletedTask; }, cancellationToken);
    return () => _ = broker.CommitAndCloseAsync(); // Dispose è void: fire-and-forget
});

var subscription = quotes.Subscribe(q => Out.Log($"  quote {q.Price}"));
// …
await Task.Delay(300);
subscription.Dispose();
```

```text
     4 ms [t  8] broker: connecting...
     5 ms [t  8] Subscribe returned
   166 ms [t  8] broker: connected
   166 ms [t  8]   quote 100
   227 ms [t  8]   quote 101
   291 ms [t  8]   quote 102
   307 ms [t  8] broker: committing offsets...
   307 ms [t  8] Dispose returned — the app shuts down now
  Connected when Subscribe returned False
  Offsets committed when Dispose returned False
   509 ms [t  8] broker: offsets committed, connection closed
✘ Dispose can't be awaited: the commit outlives the subscription (or is lost on exit)
```

`Subscribe` ritorna a 5 ms, la connessione esiste da 166 ms.
`IDisposable.Dispose` è `void`, quindi il commit si può solo lanciare e dimenticare: `Dispose` ritorna a 307 ms, gli offset sono committati a 509.
La demo aspetta altri 400 ms per mostrarlo; un'app che si spegne davvero il commit lo perde.

AsyncRx mette setup e teardown nel tipo:

```csharp
var quotes = AsyncObservable.Create<Quote>(async observer =>
{
    await broker.ConnectAsync();
    var stop = new CancellationTokenSource();
    var pump = PumpAsync(observer.OnNextAsync, stop.Token);
    return AsyncDisposable.Create(async () =>
    {
        await stop.CancelAsync();
        await pump;
        await broker.CommitAndCloseAsync();
        stop.Dispose();
    });
});

var subscription = await quotes.SubscribeAsync(q =>
{
    Out.Log($"  quote {q.Price}");
    return ValueTask.CompletedTask;
});
// …
await subscription.DisposeAsync();
```

```text
     0 ms [t  8] broker: connecting...
   156 ms [t  8] broker: connected
   157 ms [t  8]   quote 100
   158 ms [t  8] SubscribeAsync returned
   218 ms [t  8]   quote 101
   281 ms [t  8]   quote 102
   312 ms [t  5] broker: committing offsets...
   515 ms [t  5] broker: offsets committed, connection closed
   515 ms [t  5] DisposeAsync returned — the app shuts down now
  Connected when Subscribe returned True
  Offsets committed when Dispose returned True
✔ Setup and teardown are awaitable: the subscription's lifetime is the resource's lifetime
```

`SubscribeAsync` ritorna a connessione avvenuta (158 ms, connessa a 156).
Il teardown è un `IAsyncDisposable`: `DisposeAsync` ferma il pump, lo aspetta, committa, e ritorna solo alla fine (515 ms).
Un dettaglio: `quote 100` arriva prima che `SubscribeAsync` ritorni, perché il pump parte dentro la funzione di subscribe; l'observer deve essere pronto prima che l'`await` completi.

Il messaggio: quando una sottoscrizione possiede una risorsa con un ciclo di vita async, in AsyncRx `await SubscribeAsync` vuol dire "pronta" e `await DisposeAsync` vuol dire "chiusa".
In Rx classico il tipo non lo permette: `IDisposable.Dispose` è `void`.

## Cosa sapere

- Dove stanno le conversioni in Rx/Ix 7: `IObservable<T>.ToAsyncEnumerable()` è in `System.Interactive.Async` (`System.Linq.AsyncEnumerableEx`), non in `System.Reactive` né nel `System.Linq.AsyncEnumerable` della BCL.
- Con `runtime-async=on` le uniche state machine rimaste nell'assembly sono quelle dei lambda `async void`, cioè le lambda passate a `Subscribe` nel 6.2 e nel 6.5: Runtime Async non copre `async void` (vedi [Demo 0](../00-RuntimeAsync/README.md)).
- `await observable` restituisce l'*ultimo* valore al completamento, solleva `InvalidOperationException` se la sequenza è vuota e non ritorna mai se è infinita. Di' quale valore vuoi: `FirstAsync()`, `LastOrDefaultAsync()`. Ogni `await` è una sottoscrizione nuova, e con `RefCount` vuol dire una connessione nuova (6.4).
- `task.ToObservable()` adatta un `Task` già partito: un risultato condiviso, `Retry` rivede lo stesso errore, il dispose non cancella niente. `Observable.FromAsync(ct => …)` è lazy: una chiamata per sottoscrizione e token cancellato al dispose. Dove si ritenta o si cancella, `FromAsync`.
- Lavoro async in una pipeline: niente `Subscribe(async …)` (async void: nessun limite, `OnCompleted` in anticipo, eccezioni fuori da Rx). `SelectMany(async …)` è un merge senza limite e senza ordine; l'overload `SelectMany((x, ct) => …)` aggiunge la cancellazione, non un limite. Con `Select(x => Observable.FromAsync(ct => …))` scegli la politica: `Concat()` in ordine uno alla volta, `Merge(n)` al massimo *n*, `Switch()` solo l'ultimo con le altre cancellate.
- `Replay(1).RefCount()` disconnette quando i sottoscrittori scendono a zero, e riconnette al successivo: giusto per sottoscrittori che restano, sbagliato per una cache letta con `await FirstAsync()`. L'overload di `RefCount` con `disconnectDelay` rimanda la disconnessione, non la evita. `AutoConnect` resta connesso finché non chiudi la connessione che ti passa nel callback `Action<IDisposable>`: se non la tieni, resta aperta per sempre.
- Da push a pull (verso `IAsyncEnumerable` o verso AsyncRx) l'arretrato sta sempre da qualche parte: `ToAsyncEnumerable()` lo mette in una coda nascosta senza limite, `ToAsyncObservable()` su una sorgente hot lo scarica sul thread del producer, bloccandolo. Un `Channel` bounded lo mette in un buffer di cui hai scelto dimensione e politica: `DropOldest` quando conta l'ultimo valore, `Wait` quando il producer può rallentare (vedi [Demo 5](../05-Channels)).
- AsyncRx.NET è sperimentale: l'unica build su NuGet è `6.0.0-alpha.18` (2023), che funziona con Rx 7. Porta un protocollo async (backpressure con `await`, subscribe e dispose awaitabili); il confine con Rx classico (6.6) resta un problema tuo.

## Riferimenti

- [dotnet/reactive — Rx.NET, Ix.NET e AsyncRx.NET](https://github.com/dotnet/reactive)
- [Introduction to Rx.NET — Creating Observable Sequences: da `Task` a `IObservable`, `ToObservable` e `FromAsync`](https://introtorx.com/chapters/creating-observable-sequences.html)
- [Introduction to Rx.NET — Leaving Rx's World: `await` su un `IObservable<T>`, `ToTask`](https://introtorx.com/chapters/leaving-reactive-extensions-world.html)
- [Introduction to Rx.NET — Publishing Operators: `Replay`, `RefCount`, `AutoConnect`](https://introtorx.com/chapters/publishing-operators.html)
