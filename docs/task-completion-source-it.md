# `TaskCompletionSource`: un `Task` che completi tu

[English](task-completion-source.md) · **Italiano**

Di solito un `Task` nasce da codice che gira: un metodo `async`, `Task.Run`.
`TaskCompletionSource<T>` (TCS) ti dà un `Task` che nessun codice esegue: lo completi tu, quando decidi e dal thread su cui ti trovi.
È il modo per trasformare una callback, un evento o un messaggio in qualcosa su cui fare `await`, ed è il mattone delle primitive di coordinamento.

Compare in tutto il repo:
- [Demo 1](../demos/01-TaskCompletionSource/README-it.md) è dedicata a lei: le tre trappole di una TCS completata da qualcun altro;
- in [Demo 2](../demos/02-AsyncCaching/README-it.md) è il cancello di partenza che libera una raffica di chiamate tutte insieme;
- in [Demo 3](../demos/03-ValueTask/README-it.md) è il modo classico, una TCS per operazione, che `IValueTaskSource` sostituisce;
- in [Demo 6](../demos/06-RxInterop/README-it.md) trasforma la fine di una sottoscrizione (`OnCompleted`) in un `Task` da aspettare.

Questo documento copre le basi: cos'è una TCS, la sua API, i pattern in cui si usa, e quando usare altro.
Le trappole, con il loro codice e l'output reale, sono in Demo 1.

## Un `Task` senza codice dietro

Esistono due tipi di `Task` (Stephen Cleary li chiama *delegate task* e *promise task*):

| | delegate task | promise task |
|---|---|---|
| chi lo crea | `Task.Run`, `Task.Factory.StartNew` | un metodo `async`, `Task.Delay`, l'I/O, una `TaskCompletionSource` |
| cosa c'è dietro | un delegate che gira su un thread | niente che giri: un evento che succederà |
| chi lo completa | il delegate, quando ritorna | chi ha in mano l'evento: l'I/O, il timer, il codice che chiama `SetResult` |
| `Status` mentre aspetti | `WaitingToRun`, `Running` | `WaitingForActivation` |

Anche il `Task` di un metodo `async` è un promise task: viene completato quando il metodo ritorna, dal builder che genera il compilatore o, con Runtime Async, dal runtime.
Una TCS ti mette in mano la stessa cosa.

Aspettare un promise task non occupa un thread.
10.000 metodi `async` in `await` sulla stessa TCS sono 10.000 continuation in una lista: in una run di prova il thread pool è rimasto a 2 thread, e un solo `SetResult` li ha liberati tutti.

Una TCS ha due metà:
- `tcs.Task` è il lato che legge: puoi darlo a chiunque, perché un `Task` non ha metodi pubblici per completarlo;
- la TCS è il lato che scrive, e la tieni tu.

```csharp
sealed class Gateway
{
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => ready.Task;     // chi aspetta riceve il Task, non la TCS
}
```

## L'API

```csharp
public class TaskCompletionSource<TResult>
{
    public TaskCompletionSource();
    public TaskCompletionSource(TaskCreationOptions creationOptions);
    public TaskCompletionSource(object? state);                         // finisce in Task.AsyncState
    public TaskCompletionSource(object? state, TaskCreationOptions creationOptions);

    public Task<TResult> Task { get; }

    // Sollevano InvalidOperationException se il task è già completato.
    public void SetResult(TResult result);
    public void SetException(Exception exception);
    public void SetException(IEnumerable<Exception> exceptions);
    public void SetCanceled();
    public void SetCanceled(CancellationToken cancellationToken);       // .NET 5
    public void SetFromTask(Task<TResult> completedTask);               // .NET 9

    // Restituiscono false se il task è già completato.
    public bool TrySetResult(TResult result);
    public bool TrySetException(Exception exception);
    public bool TrySetException(IEnumerable<Exception> exceptions);
    public bool TrySetCanceled();
    public bool TrySetCanceled(CancellationToken cancellationToken);
    public bool TrySetFromTask(Task<TResult> completedTask);            // .NET 9
}
```

Da .NET 5 esiste anche `TaskCompletionSource` senza `<TResult>`, per un `Task` senza risultato: `SetResult()` non ha argomenti.
Prima l'idioma era una `TaskCompletionSource<bool>` con un risultato che nessuno leggeva.

I tre modi di completarla sono i tre stati finali di un `Task`:

| Chiamata | `Task.Status` | Cosa fa `await` |
|---|---|---|
| `SetResult(value)` | `RanToCompletion` | restituisce `value` |
| `SetException(ex)` | `Faulted` | solleva `ex` |
| `SetCanceled(token)` | `Canceled` | solleva `TaskCanceledException`, il cui `CancellationToken` è `token` |

- **Si completa una volta sola.** Vince la prima chiamata. Dopo, ogni `Set*` solleva `InvalidOperationException` ("An attempt was made to transition a task to a final state when it had already completed.") e ogni `TrySet*` restituisce `false`, qualunque esito provi a impostare: anche un `TrySetCanceled` dopo un `SetResult` restituisce `false`.
- **Tutti ricevono lo stesso esito.** Sul `Task` si può fare `await` molte volte: lo stesso risultato, o la stessa istanza di eccezione, per ogni chiamante, anche per chi arriva tardi.
- **`SetFromTask` copia l'esito di un task già completato**: risultato, eccezione o cancellazione. Con un task non ancora completato solleva `ArgumentException`.
- **Quando il risultato è già noto** la TCS non serve: `Task.FromResult(value)`, `Task.CompletedTask`, `Task.FromException(ex)`, `Task.FromCanceled(token)`.

## I pattern

### Da una callback a un `Task`

Un'API che comunica l'esito con delle callback diventa un metodo su cui fare `await`: ogni callback completa la TCS con il suo esito.

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

Le callback girano sul thread della libreria, da qui `RunContinuationsAsynchronously`.
`TrySet*`, perché niente garantisce che una libreria che non controlli richiami una volta sola.

### Da un evento a un `Task`: il prossimo

Un evento scatta molte volte; per aspettare la prossima occorrenza ti sottoscrivi, completi la TCS e ti togli.

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

- **`TrySetResult`**: l'evento può scattare di nuovo prima che l'handler venga tolto. Vince la prima quotazione, le successive ricevono `false`.
- **Il `-=` nel `finally`**, sia in caso di successo sia di cancellazione. Senza, l'handler, e con lui la TCS, resta attaccato al feed finché il feed vive: lo stesso leak delle registration sui token dello scenario 1.2.
- **La cancellazione con `TrySetCanceled(token)`**: il chiamante riceve una `TaskCanceledException` che porta il suo token.

### Un segnale

Il cancello di partenza di Demo 2 ([Concurrently.cs](../demos/02-AsyncCaching/Concurrently.cs)) trattiene una raffica di chiamate e le libera insieme:

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

Tutte le chiamate aspettano lo stesso `Task`, e un solo `SetResult` le libera.
Con `RunContinuationsAsynchronously` ognuna riparte su un thread del pool: senza, ripartirebbero una dopo l'altra, dentro `SetResult`.

Un segnale che dice "pronto" ha un dovere in più: deve completare su **ogni** percorso, compresi quelli che falliscono.

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
        ready.TrySetException(ex);    // senza, chi fa await su Ready aspetta per sempre
        throw;
    }
}
```

Se `StartAsync` fallisce senza completare `ready`, niente lo segnala: `Ready` resta `WaitingForActivation`, e chi lo aspetta non riparte più.
La cancellazione ha il suo `catch` perché `TrySetException` con una `OperationCanceledException` produce un task fallito, non cancellato (vedi i dettagli più sotto).

### Richiesta e risposta

Una TCS per richiesta, in un dizionario con l'id della richiesta come chiave: l'handler della risposta la cerca e la completa.
È il pattern in cui si incontrano tutte le regole qui sotto, ed è lo scenario 1.2 di [Demo 1](../demos/01-TaskCompletionSource/README-it.md): risposta e timeout fanno a gara per completarla, l'entry deve uscire dal dizionario, e le registration sui token vanno rilasciate.

## Le regole

I numeri sono gli scenari di [Demo 1](../demos/01-TaskCompletionSource/README-it.md), dove ogni regola viene prima violata e poi rispettata.

- **`RunContinuationsAsynchronously` quando a completarla è qualcun altro.** Altrimenti `SetResult` esegue il codice in attesa sul thread del chiamante, prima di ritornare: il read loop, il timer, l'handler del bus, con i lock che tiene in mano (1.1).
- **`TrySet*` quando più parti possono completarla**: risposta, timeout, cancellazione, shutdown. Controllare prima `IsCompleted` non serve: qualcun altro può completarla tra il controllo e la chiamata (1.2).
- **Passa il token a `SetCanceled(token)`**, così chi cattura l'eccezione può capire chi ha cancellato (1.2).
- **Ogni registrazione ha la sua uscita**: l'entry nel dizionario, l'handler dell'evento, la registration sul token. Ognuna tiene viva la TCS finché vive ciò su cui è registrata (1.2, e il pattern dell'evento qui sopra).
- **Non si resetta.** Per riusare un segnale si sostituisce la TCS, solo quando è completata e con `Interlocked.CompareExchange` (1.3).
- **Completala su ogni percorso.** Una TCS che nessuno completa non solleva niente e non blocca nessun thread: chi la aspetta semplicemente non riparte più (1.3, e il segnale "pronto" qui sopra).

## Invece di una TCS

- **Il risultato c'è già**: `Task.FromResult`, `Task.CompletedTask`, `Task.FromException`, `Task.FromCanceled`.
- **Ti basta smettere di aspettare**, con un timeout o un token, senza completare la sorgente: [`WaitAsync`](wait-async-it.md).
- **Un valore calcolato una volta e condiviso da molti chiamanti**: `AsyncLazy<T>`, costruito su `Lazy<Task<T>>` ([Demo 2](../demos/02-AsyncCaching/README-it.md)).
- **Molti valori, uno dopo l'altro**: `Channel<T>` ([Demo 5](../demos/05-Channels/README-it.md)).
- **Molte operazioni sulla stessa sorgente, senza allocare**: `IValueTaskSource` costruito su `ManualResetValueTaskSourceCore<T>`. In Demo 3 la versione con una TCS per operazione alloca 136 byte per operazione, la sorgente in pool 0 ([Demo 3](../demos/03-ValueTask/README-it.md)).
- **Mutua esclusione, o un limite alla concorrenza**: `SemaphoreSlim.WaitAsync`.

## Dettagli da sapere

- **`TaskContinuationOptions` al posto di `TaskCreationOptions` compila, e non fa niente.** `new TaskCompletionSource<T>(TaskContinuationOptions.RunContinuationsAsynchronously)` sceglie il costruttore che accetta un `object? state`: il valore finisce in `Task.AsyncState`, `CreationOptions` resta `None`, e le continuation girano inline sul thread che chiama `SetResult`. La build lo segnala con il warning [CA2247](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2247).
- **`SetException` con più eccezioni**: `await` solleva solo la prima; `Task.Exception`, e l'eccezione sollevata da `Wait()`, sono un'`AggregateException` che le contiene tutte.
- **`SetException(new OperationCanceledException())` produce un task `Faulted`, non `Canceled`.** `await` solleva la stessa eccezione, ma `IsCanceled` è `false`, e il codice che guarda lo stato vede un fallimento. Un metodo `async` che solleva `OperationCanceledException`, invece, finisce `Canceled`. Per cancellare una TCS si usa `SetCanceled`.
- **`SetCanceled()` senza token**: la `TaskCanceledException` porta `CancellationToken.None`, e il chiamante non può sapere se la causa è stato il suo token.
- **Una TCS fallita su cui nessuno fa `await`** non fa cadere il processo: la sua eccezione arriva a `TaskScheduler.UnobservedTaskException` solo quando il GC finalizza il task, magari molto più tardi.
- **Una TCS è il lato che scrive di un `AsyncSubject<T>`**: `SetResult` è `OnNext` più `OnCompleted`, `SetException` è `OnError`. Perché conta per le conversioni da e verso Rx è in [`Task<T>` e `IObservable<T>`](task-observable-it.md).

## Riferimenti

- [`TaskCompletionSource<TResult>` — documentazione API](https://learn.microsoft.com/dotnet/api/system.threading.tasks.taskcompletionsource-1)
- [Stephen Toub — The Nature of `TaskCompletionSource<TResult>`](https://devblogs.microsoft.com/dotnet/the-nature-of-taskcompletionsourcetresult/)
- [Stephen Cleary — A Tour of Task, Part 0: Overview (delegate task e promise task)](https://blog.stephencleary.com/2014/04/a-tour-of-task-part-0-overview.html)
- [Interoperabilità con altri modelli e tipi asincroni: da eventi e callback a `Task`](https://learn.microsoft.com/dotnet/standard/asynchronous-programming-patterns/interop-with-other-asynchronous-patterns-and-types)
- [Sergey Tepliakov — The danger of `TaskCompletionSource<T>` class](https://devblogs.microsoft.com/premier-developer/the-danger-of-taskcompletionsourcet-class/)
- [CA2247: Argument passed to `TaskCompletionSource` constructor should be `TaskCreationOptions` enum instead of `TaskContinuationOptions` enum](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2247)
