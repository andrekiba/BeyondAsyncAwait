# `Task.WaitAsync`: smettere di aspettare senza fermare il lavoro

[English](wait-async.md) · **Italiano**

`Task.WaitAsync` (da .NET 6) restituisce un nuovo `Task` che smette di aspettare quello originale quando scade un timeout o si cancella un token.
Non ferma l'operazione che c'è dietro: cancella l'**attesa**, non il **lavoro**.

In questo repo compare due volte:
- nello scenario 1.2 di [Demo 1](../demos/01-TaskCompletionSource/README-it.md) è l'alternativa più corta alla `TaskCompletionSource` completata a mano da timeout e cancellazione;
- nello scenario 2.3 di [Demo 2](../demos/02-AsyncCaching/README-it.md) è il fix: ogni chiamante cancella la propria attesa di un caricamento condiviso, senza cancellarlo per gli altri.

## L'API

```csharp
public Task WaitAsync(CancellationToken cancellationToken);
public Task WaitAsync(TimeSpan timeout);
public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
// .NET 8: con un TimeProvider, per controllare il tempo nei test
public Task WaitAsync(TimeSpan timeout, TimeProvider timeProvider);
public Task WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken);
// e le stesse su Task<TResult>, che restituiscono Task<TResult>
```

Il `Task` restituito completa al primo di tre eventi:

| Cosa succede prima | Il `Task` restituito |
|---|---|
| il task originale completa | ne riporta l'esito: risultato, eccezione o cancellazione |
| scade il timeout | fallisce con una **`TimeoutException`** |
| si cancella il token | viene cancellato, e la `TaskCanceledException` porta **quel token** |

Timeout e cancellazione restano quindi distinguibili: `catch (TimeoutException)` da una parte, `catch (OperationCanceledException ex) when (ex.CancellationToken == ct)` dall'altra.

## Come funziona dentro

Prima di allocare qualcosa, `WaitAsync` prova alcune scorciatoie ([Task.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs), .NET 11):

```csharp
if (IsCompleted || (!cancellationToken.CanBeCanceled && millisecondsTimeout == Timeout.UnsignedInfinite))
{
    return this;
}

if (cancellationToken.IsCancellationRequested)
{
    return FromCanceled(cancellationToken);
}

if (millisecondsTimeout == 0)
{
    return FromException(new TimeoutException());
}

return new CancellationPromise<VoidTaskResult>(this, millisecondsTimeout, timeProvider, cancellationToken);
```

- Se il task è già completato, o non c'è né un timeout né un token cancellabile, restituisce **il task stesso**: nessuna allocazione.
- Se il token è già cancellato, restituisce subito un task cancellato.
- Con timeout zero e il task non ancora completato, restituisce subito un task fallito con `TimeoutException`.

Altrimenti crea una `CancellationPromise`: un `Task` che si aggancia a tre cose.

1. una continuation sul task originale (`AddCompletionAction`);
2. un timer per il timeout, se c'è;
3. una registration sul token (`UnsafeRegister`).

Il primo che scatta completa la promise con `TrySet*` e chiama `Cleanup`, che smonta gli altri due: dispone la registration, ferma il timer e **toglie la continuation dal task originale**.
Da quel momento il task originale non ha più riferimenti alla promise.

Due dettagli: il timer è creato con `flowExecutionContext: false` e la registration con `UnsafeRegister`.
Né il timeout né la cancellazione si portano dietro l'`ExecutionContext` di chi ha chiamato `WaitAsync` (sull'`ExecutionContext` vedi [Demo 4](../demos/04-AsyncContext/README-it.md)).

È lo stesso meccanismo che il client fixed dello scenario 1.2 scrive a mano, con una CTS per il timeout e due registration, impacchettato in una chiamata.

## Cancella l'attesa, non il lavoro

```csharp
var task = provider.GetQuoteAsync("MSFT");        // la chiamata è partita

try
{
    var quote = await task.WaitAsync(TimeSpan.FromMilliseconds(100), ct);
}
catch (TimeoutException)
{
    // abbiamo smesso di aspettare, ma GetQuoteAsync sta ancora girando
}
catch (OperationCanceledException ex) when (ex.CancellationToken == ct)
{
    // ha cancellato il chiamante: anche qui l'operazione prosegue
}
```

Quando `WaitAsync` si arrende, il task originale continua per conto suo, e il suo esito non arriva più a chi aspettava.
Ne seguono due regole.

- **Se l'operazione accetta un token, passalo all'operazione.** `provider.GetQuoteAsync("MSFT", ct)` la ferma davvero, e libera la connessione, il timer, il lavoro. `WaitAsync` serve quando non puoi farlo: un'API che non accetta token, o un task condiviso.
- **Il task condiviso è il caso d'uso ideale.** Un solo caricamento, molti chiamanti, ognuno con il suo token: chi se ne va smette di aspettare, il caricamento continua per gli altri. Passare al caricamento il token di un chiamante, invece, cancellerebbe tutti.

È il cuore di `AsyncLazy<T>` in Demo 2 ([AsyncLazy.cs](../demos/02-AsyncCaching/AsyncLazy.cs)):

```csharp
public Task<T> GetValueAsync(CancellationToken cancellationToken = default) =>
    Volatile.Read(ref current).Value.WaitAsync(cancellationToken);
```

## `WaitAsync` o una `TaskCompletionSource` completata a mano

Per il chiamante la semantica è la stessa: `TimeoutException` per il timeout, `OperationCanceledException` con il suo token per la cancellazione.
Cambia cosa succede alla sorgente.

| | TCS completata da timeout e token (client fixed di 1.2) | `await tcs.Task.WaitAsync(timeout, ct)` |
|---|---|---|
| allo scadere del timeout | la TCS **viene completata** con `TimeoutException` | la TCS **resta incompleta**: smette di aspettarla solo chi ha chiamato `WaitAsync` |
| altri in attesa sulla stessa TCS | vedono anche loro il timeout | non se ne accorgono |
| cosa si scrive | una CTS, due registration con `await using` | una chiamata |

In entrambi i casi la TCS va tolta dal dizionario delle richieste pendenti in un `finally`.
Con `WaitAsync` è ancora più importante: la TCS non completerà mai, e senza la rimozione l'entry resterebbe lì per sempre, pronta a ricevere una risposta tardiva che nessuno aspetta più.

## Dettagli da sapere

- **Il timeout parte dalla chiamata a `WaitAsync`**, non da quando è partita l'operazione.
- **Un `Task` nuovo a ogni chiamata**, a parte le scorciatoie: la promise è un oggetto, più il timer e la registration. Anche con Runtime Async alloca.
- **Non esiste su `ValueTask`**: serve prima `AsTask()`, che a sua volta può allocare.
- **Le eccezioni dopo la resa non arrivano a nessuno.** La promise si è staccata dal task originale: se questo fallisce più tardi e nessun altro lo osserva, l'eccezione finisce solo in `TaskScheduler.UnobservedTaskException`, quando il GC finalizza il task.
- **Nei test**, l'overload con `TimeProvider` permette di far scattare il timeout senza aspettarlo davvero, con un `TimeProvider` finto come `FakeTimeProvider`.

## Riferimenti

- [`Task.WaitAsync` — documentazione API](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task.waitasync)
- [`Task.cs` in dotnet/runtime, release/11.0](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs): `WaitAsync` e `CancellationPromise<TResult>`
