# Demo 3 — Hot path: ValueTask

[English](README.md) · **Italiano**

Nel gateway QuoteHub i prezzi stanno in una cache davanti al provider lento.
Quando il prezzo c'è, il metodo async ritorna senza mai sospendere: è il hot path, la chiamata che il gateway ripete di continuo.
Con le state machine del compilatore anche questo ritorno sincrono ha un costo: un `async Task<decimal>` deve consegnare il risultato dentro un `Task`, e per un prezzo ne alloca uno nuovo a ogni chiamata.
`ValueTask<T>` esiste per questo: è una struct che contiene il risultato quando è già pronto, e un `Task<T>` o una `IValueTaskSource<T>` quando non lo è.

Runtime Async ([Demo 0](../00-RuntimeAsync/README-it.md)) cambia il consiglio classico.
Un `Task<T>` awaitato direttamente non viene materializzato, quindi il cache hit costa 0 byte anche senza `ValueTask`.
L'allocazione torna appena il `Task` diventa un oggetto: passa da un delegate, finisce in un campo, entra in un `WhenAll`.
Lo scenario 3.1 misura i due modelli a confronto. Il 3.2 mostra dove `ValueTask` resta decisivo, le sorgenti riusabili (`IValueTaskSource`), e la regola che impone: un `ValueTask` si consuma una volta sola.

## Come si lancia

I numeri da guardare sono i byte per chiamata, e vanno presi in Release:

```bash
dotnet run -c Release --project demos/03-ValueTask -- 3.1                         # Runtime Async, il default del repo
dotnet run -c Release --project demos/03-ValueTask -p:RuntimeAsync=false -- 3.1   # state machine del compilatore
dotnet run -c Release --project demos/03-ValueTask -- 3.2
```

Ogni scenario esegue la variante broken e poi la fixed; con `--broken` o `--fixed` ne esegue una sola.
Il banner (`runtime async, .NET 11.0.0` oppure `compiler state machines, .NET 11.0.0`) dice con quale modello è stato compilato l'assembly.
Da IDE ci sono i profili `menu`, `3.1 cache-hit (Release)` e `3.2 value-task-source`.

**Debug o Release.** Con Runtime Async attivo, una run in Debug dà gli stessi byte per chiamata e gli stessi esiti della Release: cambiano solo i totali di 3.2, che contano le allocazioni di tutto il processo.
Con `-p:RuntimeAsync=false`, invece, in Debug le state machine del compilatore sono `class` e allocano a ogni chiamata: lo scenario 3.1 se ne accorge e stampa un avviso (`Debug build: compiler state machines are classes in Debug and allocate on every call. Use -c Release.`).
Per confrontare i due modelli servono quindi due build Release.
Il profilo `3.1 cache-hit (Release)` non imposta la configurazione: il suffisso ricorda di scegliere Release nell'IDE.

## ★ 3.1 `cache-hit` — quanto costa un cache hit

[QuoteCache.cs](QuoteCache.cs): stesso codice, due tipi di ritorno.

```csharp
public async Task<decimal> GetPriceAsTaskAsync(string symbol)
{
    if (prices.TryGetValue(symbol, out var price))
        return price;

    var quote = await provider.GetQuoteAsync(symbol);
    return prices[symbol] = quote.Price;
}

public async ValueTask<decimal> GetPriceAsValueTaskAsync(string symbol)
{
    // … stesso corpo
}
```

Lo scenario ([CacheHitDemo.cs](CacheHitDemo.cs)) scalda la cache con una prima chiamata, un miss che va al provider, e poi misura 100.000 hit in tre modi di consumare il risultato.
`MeasureAsync` esegue il ciclo una volta a vuoto (JIT, tiering), poi lo riesegue leggendo `GC.GetAllocatedBytesForCurrentThread()` prima e dopo: ogni hit completa in modo sincrono, quindi tutte le allocazioni avvengono sul thread corrente.

**Broken: `Task<T>`.**

```csharp
Func<string, Task<decimal>> viaDelegate = cache.GetPriceAsTaskAsync;

await MeasureAsync("Task<T>: await call", async () =>
{
    for (var i = 0; i < Calls; i++)
        await cache.GetPriceAsTaskAsync(Symbol);
});

await MeasureAsync("Task<T>: await via delegate", async () =>
{
    for (var i = 0; i < Calls; i++)
        await viaDelegate(Symbol);
});

// Un Task in una variabile locale in Release può ancora sparire: in un campo o in un array no.
await MeasureAsync("Task<T>: kept as an object", async () =>
{
    var pending = new Task<decimal>[1];
    for (var i = 0; i < Calls; i++)
    {
        pending[0] = cache.GetPriceAsTaskAsync(Symbol);
        await pending[0];
    }
});
```

Con le state machine del compilatore (`-p:RuntimeAsync=false`), 80 byte a hit in tutti e tre i casi:

```text
compiler state machines, .NET 11.0.0
    76 ms [t  5] cold call (miss): 281.34
    76 ms [t  5] cache is warm, now 100,000 hits per measurement
  Task<T>: await call               80.0 bytes/call   (8,000,000 bytes)
  Task<T>: await via delegate       80.0 bytes/call   (8,000,000 bytes)
  Task<T>: kept as an object        80.0 bytes/call   (8,000,032 bytes)
```

Con Runtime Async l'await diretto scende a zero, gli altri due no:

```text
runtime async, .NET 11.0.0
    57 ms [t  5] cold call (miss): 281.34
    57 ms [t  5] cache is warm, now 100,000 hits per measurement
  Task<T>: await call                0.0 bytes/call   (0 bytes)
  Task<T>: await via delegate       80.0 bytes/call   (8,000,000 bytes)
  Task<T>: kept as an object        80.0 bytes/call   (8,000,032 bytes)
```

I 32 byte in più dell'ultima riga sono l'array da un elemento, creato una volta per misura.

**Perché.** Con le state machine, un metodo `async Task<decimal>` che ritorna in modo sincrono consegna comunque il risultato dentro un `Task` completato.
Il runtime tiene in cache `Task` già completati solo per pochi valori (bool, interi piccoli, `null`): un prezzo non è tra questi, quindi ogni hit alloca un `Task<decimal>` nuovo, 80 byte.
Con Runtime Async, quando un metodo async awaita direttamente la chiamata, il risultato arriva al chiamante senza passare da un oggetto `Task`.
Se invece il `Task` deve esistere come valore (qui il ritorno di un delegate e un elemento di array, altrove un campo o un `WhenAll`), viene creato e costa come prima.

**Fixed: `ValueTask<T>`.** Gli stessi tre cicli su `GetPriceAsValueTaskAsync`, con un `Func<string, ValueTask<decimal>>` e un `ValueTask<decimal>[1]`.
L'output è identico nei due modelli:

```text
  ValueTask<T>: await call           0.0 bytes/call   (0 bytes)
  ValueTask<T>: await via delegate   0.0 bytes/call   (0 bytes)
  ValueTask<T>: kept as an object    0.0 bytes/call   (56 bytes)
```

`ValueTask<decimal>` è una struct: il risultato sincrono viaggia dentro il valore, che passi da un delegate o da un array. I 56 byte sono, di nuovo, l'array.

| Byte per hit, Release | State machine | Runtime Async |
|---|---|---|
| `Task<T>`, await diretto | 80 | **0** |
| `Task<T>`, await via delegate | 80 | 80 |
| `Task<T>`, tenuto in un array | 80 | 80 |
| `ValueTask<T>`, tutti e tre i casi | 0 | 0 |

Con Runtime Async l'argomento classico, `ValueTask` sul hot path, si ridimensiona: un `Task<T>` awaitato subito non alloca più.
`ValueTask<T>` resta decisivo quando il risultato diventa un oggetto prima di essere awaitato, e nel codice compilato senza Runtime Async.

## ★ 3.2 `value-task-source` — zero allocazioni, un solo await

Il cache hit non sospende mai. Un'operazione che sospende davvero, come una ricezione dalla rete, non ha un risultato da portare inline: il `ValueTask` deve puntare a un oggetto che completerà più tardi.
Il pattern di `Socket` è riusare quell'oggetto.
In [QuoteFeed.cs](QuoteFeed.cs) ogni `ReceiveAsync` prende un oggetto operazione da un pool (o ne crea uno, e lo conta) e restituisce un `ValueTask` costruito su di esso.
La "rete" risponde su un thread del thread pool, con prezzi fissi (MSFT 412.10, AAPL 228.70): un risultato sbagliato si vede subito.

L'oggetto operazione implementa `IValueTaskSource<decimal>` delegando tutto a `ManualResetValueTaskSourceCore<decimal>`, che gestisce risultato, continuation e versione:

```csharp
sealed class QuoteOperation(PooledQuoteFeed owner) : IValueTaskSource<decimal>, IThreadPoolWorkItem
{
    // Struct mutabile: questo campo non va mai dichiarato readonly.
    ManualResetValueTaskSourceCore<decimal> core;
    string symbol = "";

    public ValueTask<decimal> Start(string symbol)
    {
        this.symbol = symbol;
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        // Il token è la versione: cambia a ogni Reset, così un ValueTask scaduto viene riconosciuto.
        return new ValueTask<decimal>(this, core.Version);
    }

    // La "rete" risponde su un thread del thread pool.
    void IThreadPoolWorkItem.Execute() => core.SetResult(Prices.Of(symbol));

    public decimal GetResult(short token)
    {
        try
        {
            return core.GetResult(token);
        }
        finally
        {
            // Consumato: reset (version++) e ritorno nel pool, pronto per la prossima ReceiveAsync.
            core.Reset();
            owner.Return(this);
        }
    }

    public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        core.OnCompleted(continuation, state, token, flags);
}
```

**Broken.** Tre parti, in [ValueTaskSourceDemo.cs](ValueTaskSourceDemo.cs).

*Una `TaskCompletionSource` per operazione* (`TcsQuoteFeed`): ogni `ReceiveAsync` crea un oggetto operazione, la sua `TaskCompletionSource` e il `Task` di questa.
Qui le completion girano su thread del thread pool, quindi `MeasureAsync` conta le allocazioni di tutto il processo con `GC.GetTotalAllocatedBytes(precise: true)`:

```text
── A TaskCompletionSource per operation
  TaskCompletionSource feed         136.0 bytes/operation   (13,601,672 bytes)
```

*Lo stesso `ValueTask` awaitato due volte*, sul feed pooled corretto:

```csharp
var pending = feed.ReceiveAsync("MSFT");
Out.Log($"MSFT = {await pending}");
try
{
    Out.Log($"MSFT again = {await pending}");
}
catch (InvalidOperationException ex)
{
    // …
}
```

```text
── Pooled feed: the same ValueTask awaited twice
    97 ms [t  8] MSFT = 412.10
✘ InvalidOperationException: the token is stale, the operation object was already reset and pooled
```

Il primo `await` passa da `GetResult`, che fa `Reset()` e rimette l'oggetto nel pool.
Il secondo presenta il token della versione precedente: `ManualResetValueTaskSourceCore` non lo riconosce e solleva `InvalidOperationException`, che la demo cattura e stampa.

*Una sorgente ingenua che ignora il token* (`NaivePooledQuoteFeed`): stesso pool, ma il token è sempre 0 e `GetResult` restituisce il prezzo che l'oggetto ha in quel momento.

```csharp
public ValueTask<decimal> Start(string symbol)
{
    // …
    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    return new ValueTask<decimal>(this, 0);
}

// …

public decimal GetResult(short token)
{
    var result = price;
    owner.Return(this);
    return result;
}
```

La demo awaita il `ValueTask` di MSFT, chiede AAPL (lo stesso oggetto, ripreso dal pool), poi awaita di nuovo il primo:

```csharp
var first = naive.ReceiveAsync("MSFT");
Out.Log($"MSFT = {await first}");
Out.Log($"AAPL = {await naive.ReceiveAsync("AAPL")}   (same pooled object, reused)");
var again = await first;
```

```text
── Naive pooled feed (token ignored): await twice, with a reuse in between
    99 ms [t  9] MSFT = 412.10
    99 ms [t  9] AAPL = 228.70   (same pooled object, reused)
✘ MSFT again = 228.70  ← that's AAPL's price
```

Nessuna eccezione: il secondo `await` su `first` legge lo stato dell'oggetto riusato e restituisce il prezzo di AAPL come se fosse MSFT.
E `GetResult` rimette l'oggetto nel pool anche questa volta: ora ci compare due volte, e le prossime due `ReceiveAsync` si contenderebbero lo stesso oggetto.

**Perché.** Un `ValueTask` costruito su una `IValueTaskSource` vale per un solo await dell'operazione corrente di quell'oggetto.
Dopo il consumo l'oggetto torna nel pool e serve un'altra operazione: il token è l'unico modo di distinguere le due.
Con il token l'errore diventa un'eccezione, senza diventa un dato sbagliato.

**Fixed.** Il feed pooled consumato come va consumato, un `await` per ogni `ValueTask`; quando servono più await o un combinatore si paga un `Task`, solo lì.

```csharp
await MeasureAsync("Pooled IValueTaskSource feed", async () =>
{
    for (var i = 0; i < Operations; i++)
        await feed.ReceiveAsync("MSFT");
});
// …
var preserved = feed.ReceiveAsync("MSFT").Preserve();
Out.Log($"MSFT = {await preserved}, again = {await preserved}   (Preserve(): Task-backed copy)");

var both = await Task.WhenAll(feed.ReceiveAsync("MSFT").AsTask(), feed.ReceiveAsync("AAPL").AsTask());
```

```text
── Pooled IValueTaskSource feed
  Pooled IValueTaskSource feed        0.0 bytes/operation   (352 bytes)
  Operation objects ever created   1

── Need it twice, or in WhenAll? Pay for a Task only then
   104 ms [t 10] MSFT = 412.10, again = 412.10   (Preserve(): Task-backed copy)
✔ WhenAll via AsTask(): MSFT = 412.10, AAPL = 228.70
```

Le 100.000 operazioni completano su un thread del thread pool e usano un solo oggetto operazione: ogni `await` lo restituisce al pool prima che parta la richiesta successiva.
Il totale, 352 byte su 100.000 operazioni (128 nella build a state machine), è misurato su tutto il processo.
`Preserve()` restituisce un `ValueTask` basato su un `Task`, che si può awaitare più volte; `AsTask()` serve per passare le operazioni a `WhenAll`.

| Release | State machine | Runtime Async |
|---|---|---|
| `TaskCompletionSource` per operazione | 136 byte/operazione | 136 byte/operazione |
| `IValueTaskSource` pooled | 0 byte/operazione, 1 oggetto | 0 byte/operazione, 1 oggetto |
| stesso `ValueTask` awaitato due volte | `InvalidOperationException` | `InvalidOperationException` |
| sorgente senza token | 228.70 restituito come MSFT | 228.70 restituito come MSFT |

Qui il modello di compilazione non cambia nulla: i byte per operazione dipendono dalla sorgente, non da come è compilato il metodo che awaita.
Una `IValueTaskSource` pooled rende gratis un'operazione che sospende davvero, anche con Runtime Async; in cambio il `ValueTask` va consumato una volta sola, e chi scrive la sorgente deve controllare il token.

## Benchmark

In [benchmarks/BeyondAsyncAwait.Benchmarks](../../benchmarks/BeyondAsyncAwait.Benchmarks) ogni benchmark gira due volte: job `StateMachine` (baseline) e job `RuntimeAsync`.

```bash
dotnet run -c Release --project benchmarks/BeyondAsyncAwait.Benchmarks -- --filter '*ValueTask*'
```

In [ValueTaskBenchmarks.cs](../../benchmarks/BeyondAsyncAwait.Benchmarks/ValueTaskBenchmarks.cs):

- `Hit_Task_Awaited` e `Hit_ValueTask_Awaited`: 100 cache hit awaitati direttamente dentro un metodo async; con `OperationsPerInvoke` i numeri sono per hit.
- `Hit_Task_Materialized`: il `Task` del cache hit tenuto in un campo, quindi deve esistere come oggetto.
- `Suspend_ValueTask` e `Suspend_PoolingValueTask`: un `async ValueTask<int>` che sospende davvero (`Task.Yield`), con il builder standard e con `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]`.

Risultati **indicativi** (`--quick`, i7-11800H, .NET 11.0.0): rifalli sulla tua macchina, senza `--quick`.

| Benchmark | StateMachine | RuntimeAsync |
|---|---|---|
| cache hit, `Task<T>` awaitato | 18 ns, 81 B | **11 ns, 0 B** |
| cache hit, `ValueTask<T>` awaitato | 17 ns, 1 B | 11 ns, 0 B |
| cache hit, `Task<T>` tenuto in un campo | 22 ns, 80 B | 24 ns, 80 B |
| sospensione, `ValueTask<T>` | 426 ns, 104 B | 342 ns, 104 B |
| sospensione, `PoolingAsyncValueTaskMethodBuilder` | 422 ns, **0 B** | 353 ns, **104 B** |

- **Hit awaitato**: con le state machine `Task<T>` paga gli 80 B del suo `Task` a ogni hit, `ValueTask<T>` no. Il byte in più su entrambe le righe è il `Task<decimal>` del metodo di benchmark, anch'esso `async`, diviso sui 100 hit. Con Runtime Async entrambi scendono a 0 B e 11 ns: per un await diretto `Task<T>` e `ValueTask<T>` si equivalgono.
- **`Task` in un campo**: 80 B in entrambi i job, con tempi simili. Quando il `Task` deve esistere come oggetto Runtime Async non lo può evitare: è la riga "kept as an object" dello scenario 3.1.
- **Sospensione**: quando il metodo sospende davvero il risultato inline non serve, e con le state machine `ValueTask<T>` alloca 104 B. Il pooling builder riusa i box delle state machine e scende a 0 B. Nel job RuntimeAsync le due righe si allineano a 104 B: i metodi con `PoolingAsyncValueTaskMethodBuilder` escono da Runtime Async, e il vantaggio del pooling non si vede più. Runtime Async è più veloce in entrambi i casi, ma le due ottimizzazioni non si sommano.

Il harness di BenchmarkDotNet è compilato con lo stesso `RuntimeAsync` del codice misurato: per questo `Hit_Task_Materialized` tiene il `Task` in un campo.
Restituirlo e basta non servirebbe: il harness lo awaiterebbe direttamente, e con Runtime Async il `Task` non verrebbe materializzato.

## Cosa sapere prima di usare ValueTask

- **Il default resta `Task`.** La documentazione di `ValueTask<TResult>` consiglia di passare a `ValueTask` solo quando un'analisi delle prestazioni lo giustifica. Un `ValueTask` ha più campi di un riferimento: costa di più copiarlo e ingrandisce la state machine del chiamante che lo awaita.
- **Quando ripaga ancora.** Con Runtime Async, per un hot path sincrono, quando il chiamante riceve il risultato attraverso un delegate o lo conserva in un campo prima di awaitarlo (scenario 3.1). Nel codice compilato senza Runtime Async, che in .NET 11 è opt-in (in questo repo lo attiva [Directory.Build.targets](../../Directory.Build.targets)), vale il consiglio classico. Per le operazioni che sospendono davvero serve una `IValueTaskSource` riusabile, come fanno `Socket` e `NetworkStream` (scenario 3.2).
- **Un `ValueTask` si consuma una volta sola.** La documentazione elenca cosa non fare mai: awaitarlo più volte, chiamare `AsTask()` più volte, usare `.Result` o `.GetAwaiter().GetResult()` prima che l'operazione sia completata (o più volte), combinare più di questi modi. Stephen Toub aggiunge: mai due await in parallelo, perché l'oggetto dietro supporta un solo consumatore alla volta. Il risultato è indefinito: con `ManualResetValueTaskSourceCore` un'eccezione, con una sorgente scritta male il dato di un'altra operazione.
- **Serve di più? Convertilo una volta.** `AsTask()` per passarlo a `WhenAll` / `WhenAny` o per awaitarlo più volte (da lì in poi si usa il `Task`); `Preserve()` restituisce un `ValueTask` utilizzabile in qualsiasi momento. Si paga un `Task`, solo dove serve.
- **CA2012** ("Use ValueTasks correctly") segnala i `ValueTask` scartati, consumati più volte o letti prima di essere completati, e chiede di awaitarli direttamente invece di tenerli in variabili o campi. In .NET 11 è attiva come suggerimento; in questo repo è disattivata ([Directory.Build.props](../../Directory.Build.props)), perché lo scenario 3.2 usa male un `ValueTask` apposta.
- **Per scrivere una `IValueTaskSource`** delega a `ManualResetValueTaskSourceCore<T>`: è una struct mutabile (il campo non va mai dichiarato `readonly`), il token del `ValueTask` è la sua `Version`, e `Reset()` va chiamato dopo aver consumato il risultato, prima di rimettere l'oggetto nel pool. Un oggetto pooled serve un'operazione alla volta: con operazioni concorrenti il pool di [QuoteFeed.cs](QuoteFeed.cs) ne crea uno per ogni operazione in volo.
- **`PoolingAsyncValueTaskMethodBuilder`** fa uscire il metodo da Runtime Async: il pooling dei box e Runtime Async non si sommano (vedi i benchmark).

## Riferimenti

- [Stephen Toub — Understanding the Whys, Whats, and Whens of ValueTask](https://devblogs.microsoft.com/dotnet/understanding-the-whys-whats-and-whens-of-valuetask/)
- [`ValueTask<TResult>` — documentazione API](https://learn.microsoft.com/dotnet/api/system.threading.tasks.valuetask-1)
- [`ManualResetValueTaskSourceCore<TResult>` — documentazione API](https://learn.microsoft.com/dotnet/api/system.threading.tasks.sources.manualresetvaluetasksourcecore-1)
- [CA2012: Use ValueTasks correctly](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2012)
