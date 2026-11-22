# `IAsyncDisposable`: quando chiudere richiede di aspettare

[English](async-disposable.md) · **Italiano**

`IAsyncDisposable` (da .NET Core 3.0 e C# 8) è la controparte async di `IDisposable`: un solo metodo, `DisposeAsync`, che restituisce un `ValueTask`.
Esiste per le risorse la cui chiusura deve aspettare qualcosa: un buffer da svuotare, degli offset da committare, un loop in background o una callback che devono finire.
Con `IDisposable` quell'attesa o blocca un thread, o non avviene affatto.

In questo repo compare in più punti:
- nello scenario 1.2 di [Demo 1](../demos/01-TaskCompletionSource/README-it.md) le `CancellationTokenRegistration` vengono disposte con `await using`;
- nello scenario 4.3 di [Demo 4](../demos/04-AsyncContext/README-it.md) è il tema centrale: un publisher, dentro uno scope DI, che allo shutdown deve scrivere l'ultimo batch;
- negli scenari 6.6 e 6.7 di [Demo 6](../demos/06-RxInterop/README-it.md) le subscription AsyncRx si chiudono con `DisposeAsync`, che ritorna solo quando il teardown è finito.

## L'API

```csharp
public interface IAsyncDisposable
{
    ValueTask DisposeAsync();
}
```

Restituisce un `ValueTask` perché quasi sempre non c'è niente da aspettare: l'oggetto è già disposto, il buffer è vuoto, la callback non è in esecuzione.
In quei casi `DisposeAsync` completa in modo sincrono e restituisce `default`, senza allocare.

## Cosa diventa `await using`

```csharp
await using (var publisher = new BatchingPublisher(waitForFlush: true))
{
    await publisher.PublishAsync(quote);
}
```

Il compilatore lo trasforma, più o meno, in un `try`/`finally` che awaita `DisposeAsync`:

```csharp
var publisher = new BatchingPublisher(waitForFlush: true);
try
{
    await publisher.PublishAsync(quote);
}
finally
{
    if (publisher != null)
        await publisher.DisposeAsync();
}
```

Quello che ne segue, ogni punto verificato su .NET 11:

- **La forma dichiarazione** `await using var x = …;` dispone alla fine del blocco che la contiene; con più dichiarazioni, in ordine inverso.
- **Se il tipo implementa entrambe le interfacce**, `await using` chiama solo `DisposeAsync`, e `using` solo `Dispose`. Se implementa solo `IAsyncDisposable`, `using` non compila: *CS8418 … Did you mean 'await using' rather than 'using'?*
- **Funziona per pattern**: basta un metodo `DisposeAsync()` che restituisce qualcosa di awaitable, anche senza l'interfaccia. È quello che fa funzionare `ConfigureAwait` (sotto).
- **Una risorsa `null` viene saltata**, come con `using`.
- **Se sollevano un'eccezione sia il corpo sia `DisposeAsync`**, quella che esce è di `DisposeAsync`: quella del corpo si perde, come per qualunque eccezione sollevata in un `finally`.

### `ConfigureAwait`

`await using` awaita `DisposeAsync` come qualunque altro `await`, catturando il `SynchronizationContext` se c'è.
Nel codice di libreria:

```csharp
var publisher = new BatchingPublisher(waitForFlush: true);
await using (publisher.ConfigureAwait(false))
{
    await publisher.PublishAsync(quote).ConfigureAwait(false);
}
```

`ConfigureAwait(this IAsyncDisposable, bool)` restituisce una `ConfiguredAsyncDisposable`: uno struct che non implementa `IAsyncDisposable`, ma ha un `DisposeAsync()` che restituisce un `ConfiguredValueTaskAwaitable`, e `await using` lo accetta per pattern.
Per questo la risorsa si dichiara prima: con `await using var x = publisher.ConfigureAwait(false);`, `x` sarebbe il wrapper, non il publisher.
Le demo sono app console, senza `SynchronizationContext`, e non ne hanno bisogno.

## Chi lo implementa, e cosa aspetta

| Tipo | `Dispose()` | `DisposeAsync()` |
|---|---|---|
| `CancellationTokenRegistration` | toglie la callback; se la callback sta girando su un altro thread, **blocca** finché non finisce | lo stesso, ma restituisce un `ValueTask` che completa quando la callback finisce |
| `Timer` | ferma il timer e ritorna: una callback già in esecuzione va avanti | completa quando le callback già in coda o in esecuzione sono finite |
| `FileStream`, `BufferedStream` | scrive il buffer e chiude, bloccando il thread | `FlushAsync`, poi chiude |
| `Stream` (classe base) | chiude | chiama `Dispose()` e ritorna: è async solo dove una classe derivata lo ridefinisce |
| scope DI e `ServiceProvider` | dispone i servizi in ordine inverso; un servizio che implementa solo `IAsyncDisposable` → `InvalidOperationException` | per ogni servizio, `DisposeAsync` se ce l'ha, altrimenti `Dispose` |
| `IAsyncEnumerator<T>` | — | lo chiama `await foreach` alla fine, anche con `break`: in un iteratore async esegue i blocchi `finally` in sospeso |

`CancellationTokenSource` implementa solo `IDisposable`: niente, nel suo dispose, deve aspettare.

`BufferedStream.DisposeAsync` ([BufferedStream.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/IO/BufferedStream.cs)) mostra la forma di un'implementazione fatta bene: prima l'I/O, poi la risorsa che possiede, entrambi awaitati, e lo stato ripulito in ogni caso.

```csharp
public override async ValueTask DisposeAsync()
{
    try
    {
        if (_stream != null)
        {
            try
            {
                await FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
    finally
    {
        _stream = null;
        _buffer = null;
        _writePos = 0;
    }
}
```

## Scriverne uno

`BatchingPublisher` in Demo 4 ([BatchingPublisher.cs](../demos/04-AsyncContext/BatchingPublisher.cs)) accumula le quotazioni in batch da 10 e le scrive da un loop in background:

```csharp
public async ValueTask DisposeAsync()
{
    // Idempotente: container DI, blocchi using e hook di shutdown possono chiamarlo tutti.
    if (Interlocked.Exchange(ref disposed, 1) == 1)
    {
        Out.Log("publisher: already disposed, nothing to do");
        return;
    }

    input.Writer.TryComplete();
    // …

    // Aspetta l'ultimo flush; un flush fallito viene loggato, non deve uscire da DisposeAsync come eccezione.
    await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    if (loop.IsFaulted)
        Out.Bad($"publisher: last flush failed: {loop.Exception!.InnerException!.Message}");
    Out.Log("publisher: disposed, buffer flushed");
}
```

Ne escono quattro regole:

- **Ritornare quando il lavoro è finito.** Completare l'input avvia l'ultimo flush, ma non lo aspetta: la variante broken di 4.3 ritorna prima, e l'ultimo batch arriva quando chi stava chiudendo è già andato avanti. È l'await su `loop` che fa sì che `await using` voglia dire "chiuso".
- **Idempotente.** Lo scope DI, un `await using` e un hook di shutdown possono chiamarlo tutti: la seconda chiamata non deve trovare più niente da fare. Qui, un flag impostato con `Interlocked.Exchange`.
- **Non sollevare eccezioni per un fallimento previsto.** Un'eccezione da `DisposeAsync` sostituisce quella del corpo. `ConfigureAwaitOptions.SuppressThrowing` (.NET 8) aspetta il loop senza sollevare di nuovo la sua eccezione; il codice controlla `IsFaulted` e la logga.
- **Solo `IAsyncDisposable`, o entrambe?** Solo async quando non esiste una chiusura sincrona onesta, come qui: un proprietario sincrono allora fallisce in modo evidente (l'`InvalidOperationException` del container) invece di perdere il batch in silenzio. Entrambe quando il tipo ha anche consumatori sincroni, accettando che `Dispose` blocchi; in una classe non `sealed`, il pattern documentato aggiunge un `protected virtual ValueTask DisposeAsyncCore()` (vedi [Implement a DisposeAsync method](https://learn.microsoft.com/dotnet/standard/garbage-collection/implementing-disposeasync)).

## Proprietari async fino in cima

Una risorsa che si chiude in modo asincrono richiede che anche chi la possiede si chiuda in modo asincrono: la classe che la tiene in un campo implementa a sua volta `IAsyncDisposable`, lo scope DI si crea con `CreateAsyncScope()`, il container si dispone con `DisposeAsync`.
Un anello sincrono nella catena può fare solo una di tre cose, tutte sbagliate:

- **sollevare un'eccezione**: `scope.Dispose()` con un servizio che implementa solo `IAsyncDisposable` → `InvalidOperationException`, e il `DisposeAsync` del publisher non gira mai (4.3);
- **bloccare**: `DisposeAsync().AsTask().GetAwaiter().GetResult()` è sync-over-async: un thread fermo ad aspettare, e un deadlock se la continuation ha bisogno proprio del thread che stai bloccando (un `SynchronizationContext` a thread singolo);
- **dimenticare**: `_ = DisposeAsync()`, o un `void Dispose()` che avvia la chiusura e ritorna. È il teardown di Rx classico in 6.7: gli offset vengono committati 200 ms dopo che `Dispose` è ritornato.

## Dettagli da sapere

- **`DisposeAsync` non promette asincronia.** La classe base `Stream` chiama `Dispose()` e restituisce un `ValueTask` già completato; su un `MemoryStream` completa in modo sincrono.
- **È un `ValueTask`**: si awaita una volta sola, e non si conserva. Le regole sono quelle di [Demo 3](../demos/03-ValueTask/README-it.md).
- **`CancellationTokenRegistration.Dispose` aspetta solo in una race**: quando `Cancel()` sta eseguendo proprio quella callback su un altro thread. Chiamato dall'interno della callback stessa, non aspetta: andrebbe in deadlock, e il sorgente dice che sceglie di ritornare.
- **L'enumeratore di un iteratore async non si può disporre mentre `MoveNextAsync` è in corso**: `DisposeAsync` solleva `NotSupportedException`. Per fermare un'attesa in corso serve il token (`WithCancellation`, `[EnumeratorCancellation]`).
- **Runtime Async non cambia nulla di tutto questo**: lo scenario 4.3 dà lo stesso output con `-p:RuntimeAsync=false`.

## Riferimenti

- [Implement a DisposeAsync method](https://learn.microsoft.com/dotnet/standard/garbage-collection/implementing-disposeasync)
- [`IAsyncDisposable` — documentazione API](https://learn.microsoft.com/dotnet/api/system.iasyncdisposable)
- dotnet/runtime, release/11.0: [CancellationTokenRegistration.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/Threading/CancellationTokenRegistration.cs), [Timer.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/Threading/Timer.cs), [BufferedStream.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/IO/BufferedStream.cs), [ServiceProviderEngineScope.cs](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/Microsoft.Extensions.DependencyInjection/src/ServiceLookup/ServiceProviderEngineScope.cs)
