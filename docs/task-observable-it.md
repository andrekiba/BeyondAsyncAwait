# `Task<T>` e `IObservable<T>`: un valore o molti, pull o push

[English](task-observable.md) · **Italiano**

`Task<T>` è un valore che arriverà; `IObservable<T>` è una sequenza di valori che ti vengono consegnati.
Sembrano due mondi diversi, ma nascono dalla stessa idea, e vedere come sono legati spiega le regole delle conversioni tra l'uno e l'altro: perché `await observable` restituisce l'*ultimo* valore, perché `Retry` su un `ToObservable()` non ripete mai la chiamata, dove si accumula l'arretrato quando si passa da push a pull.

La sessione percorre questa strada da un capo all'altro:
- le Demo 1–4 trattano un valore solo: `Task<T>`, `TaskCompletionSource`, `AsyncLazy<T>`, `ValueTask<T>`;
- la [Demo 5](../demos/05-Channels/README-it.md) tratta molti valori in modalità pull: `Channel<T>`, `IAsyncEnumerable<T>`;
- la [Demo 6](../demos/06-RxInterop/README-it.md) tratta molti valori in modalità push: `IObservable<T>`, le conversioni da e verso `Task`, e AsyncRx.

Questo documento è la mappa; le trappole, con il codice e l'output reale, stanno nelle Demo.

## La mappa

| | un valore | una sequenza |
|---|---|---|
| **sincrono** | `T` | `IEnumerable<T>`: pull, il consumer chiama `MoveNext()` |
| **asincrono** | `Task<T>`, `ValueTask<T>` | `IAsyncEnumerable<T>`: pull, il consumer awaita `MoveNextAsync()`<br>`IObservable<T>`: push, il producer chiama `OnNext()` |

Per un valore solo, pull o push cambia poco: il valore è uno, e arriva una volta.
Per una sequenza è tutta la differenza, perché decide **chi detta il ritmo**: in pull il producer lavora solo quando il consumer chiede; in push i valori arrivano quando il producer li ha, che il consumer sia pronto o no.

## La dualità

Mettiamo a confronto le interfacce sincrone (semplificate: nella BCL `MoveNext` è dichiarato sull'`IEnumerator` non generico):

```csharp
// Pull: chiama il consumer, risponde il producer.
public interface IEnumerable<out T>
{
    IEnumerator<T> GetEnumerator();
}

public interface IEnumerator<out T> : IDisposable
{
    bool MoveNext();                // true: un valore in Current · false: la fine · oppure solleva un'eccezione
    T Current { get; }
}

// Push: chiama il producer, risponde il consumer.
public interface IObservable<out T>
{
    IDisposable Subscribe(IObserver<T> observer);
}

public interface IObserver<in T>
{
    void OnNext(T value);           // un valore
    void OnCompleted();             // la fine
    void OnError(Exception error);  // l'errore
}
```

Sono lo stesso protocollo con il verso delle chiamate invertito, ed è per questo che `IObservable<T>` si dice il *duale* di `IEnumerable<T>`:

| | pull: `IEnumerable<T>` | push: `IObservable<T>` |
|---|---|---|
| inizio | `GetEnumerator()` | `Subscribe(observer)` |
| un valore | `MoveNext()` restituisce `true`, il valore è in `Current` | `OnNext(value)` |
| la fine | `MoveNext()` restituisce `false` | `OnCompleted()` |
| un errore | `MoveNext()` solleva un'eccezione | `OnError(error)` |
| fermarsi prima | `Dispose()` dell'enumeratore | `Dispose()` della sottoscrizione |
| chi aspetta chi | il consumer, dentro `MoveNext()`, finché il valore non è pronto | il producer, dentro `OnNext()`, finché il consumer non l'ha gestito |

L'ultima riga è quella che conta con il codice async.
`IAsyncEnumerable<T>` trasforma `MoveNext()` in `ValueTask<bool> MoveNextAsync()`: il consumer awaita invece di bloccarsi.
Dal lato push `OnNext` resta `void`: un observer che fa lavoro async ritorna al suo primo `await`, e il producer va avanti senza aspettare (6.2, 6.5).
L'`IAsyncObserver<T>` di AsyncRx lo trasforma in `ValueTask OnNextAsync(T value)`, e il producer lo awaita: lo stesso passo, dall'altro lato della dualità.

### `Task<T>` è un observable con un solo valore

Un `Task<T>` finisce in uno di tre modi, e ognuno ha il suo corrispondente in un observable che produce al massimo un valore:

| `Task<T>` | observable |
|---|---|
| completa con un risultato | `OnNext(result)`, poi `OnCompleted()` |
| fallisce | `OnError(exception)` |
| viene cancellato | `OnError(TaskCanceledException)` |
| si può awaitare più volte: stesso risultato, o stessa eccezione | `AsyncSubject<T>`: tiene l'ultimo valore e lo consegna al completamento a ogni subscriber, anche a quelli arrivati dopo |

Rx usa questa equivalenza nei due sensi:
- `await observable` passa da `GetAwaiter()`, che restituisce un `AsyncSubject<T>`: per questo aspetta il completamento, restituisce l'*ultimo* valore, solleva `InvalidOperationException` se la sequenza è vuota, e non ritorna mai se è infinita (6.1);
- `task.ToObservable()` si comporta come un `AsyncSubject<T>`: una sola esecuzione, e ogni subscriber riceve lo stesso risultato, o la stessa eccezione, anche dopo il completamento.

## Dove sono diversi

La dualità sta nel protocollo; le differenze stanno in quando parte il lavoro, chi lo condivide e chi lo ferma.

| | `Task<T>` | `IObservable<T>` | `IAsyncEnumerable<T>` | `ChannelReader<T>` |
|---|---|---|---|---|
| **quando parte il lavoro** | prima che tu abbia il task: un metodo async esegue fino al suo primo `await` | cold (`FromAsync`, `Interval`, `Create`): a ogni `Subscribe`; hot (`Subject`, eventi): che ci sia qualcuno sottoscritto o no | al primo `MoveNextAsync()`: un iteratore async prima non fa niente | quando il writer scrive, che qualcuno legga o no |
| **due consumer** | lo stesso risultato, una sola esecuzione | cold: due esecuzioni; hot: entrambi ricevono i valori dal momento in cui si sottoscrivono | due esecuzioni: ogni `GetAsyncEnumerator()` ricomincia da capo | si dividono gli elementi: ognuno va a un solo reader |
| **chi detta il ritmo** | — | il producer: `OnNext` è `void` | il consumer: l'iteratore si ferma su `yield return` fino al `MoveNextAsync()` successivo | il buffer: un canale bounded fa aspettare il writer, o scarta (Demo 5) |
| **come si cancella** | con il token passato all'operazione; da fuori puoi solo smettere di aspettare ([`WaitAsync`](wait-async-it.md)) | `Dispose()` della sottoscrizione; con `FromAsync` cancella il token | il token (`WithCancellation`, `[EnumeratorCancellation]`), o `break`, che dispone l'enumeratore | il token passato a `ReadAllAsync` cancella la lettura, non il writer |
| **errori** | conservati nel task: ogni `await` solleva di nuovo la stessa eccezione | `OnError`, terminale, a ogni observer | sollevati da `MoveNextAsync()` | `Complete(ex)` sul writer, sollevata di nuovo da `ReadAllAsync` e da `Completion` |
| **la fine** | nessun segnale a parte: il task completa insieme al risultato | `OnCompleted()` | `MoveNextAsync()` restituisce `false` | `Complete()`: `ReadAllAsync` finisce quando il buffer è vuoto |

## Le conversioni

Ogni conversione deve rispondere alle domande della tabella qui sopra: quando parte il lavoro, se è condiviso, cosa cancella il dispose, dove aspettano i valori.

| Da → a | Come | Cosa fa davvero | Dove si vede |
|---|---|---|---|
| `Task<T>` → `IObservable<T>` | `task.ToObservable()` | avvolge un task già in esecuzione: una sola esecuzione, lo stesso risultato o la stessa eccezione per ogni subscriber; `Retry` rivede lo stesso errore, il dispose non cancella niente | 6.1 |
| operazione → `IObservable<T>` | `Observable.FromAsync(ct => …)` | lazy: una chiamata per sottoscrizione, e il dispose della sottoscrizione cancella il token | 6.1, 6.2 |
| operazione → `IObservable<T>` | `Observable.StartAsync(ct => …)` | parte subito ed è condiviso, come `ToObservable()`; ma il token è legato a ogni sottoscrizione: il dispose di *una* cancella la chiamata per tutti, e gli altri ricevono `TaskCanceledException` | — |
| `IObservable<T>` → `Task<T>` | `await observable`, `ToTask()` | l'ultimo valore, quando la sequenza completa. `ToTask(ct)`: il token annulla la sottoscrizione e cancella il task | 6.1 |
| `IObservable<T>` → un valore | `await observable.FirstAsync()`, `LastOrDefaultAsync()`, … | restituiscono un `IObservable<T>`, non un `Task`: ogni `await` è una nuova sottoscrizione | 6.1, 6.4 |
| `IObservable<T>` → `IAsyncEnumerable<T>` | `ToAsyncEnumerable()` (System.Interactive.Async) | si sottoscrive, e tiene quello che il consumer non ha ancora letto in una coda senza limite | 6.3 |
| `IAsyncEnumerable<T>` → `IObservable<T>` | `ToObservable()` (System.Interactive.Async) | lazy: un'enumerazione per sottoscrizione, il dispose la ferma | — |
| `IObservable<T>` → `Channel<T>` | `Subscribe(x => writer.TryWrite(x))` su un canale bounded | l'arretrato in un buffer di cui hai scelto dimensione e politica | 6.3, 6.6 |
| `IObservable<T>` → `IAsyncObservable<T>` | `ToAsyncObservable()` (System.Reactive.Async) | su una sorgente hot, il thread del producer si blocca finché l'observer async non ha finito | 6.6 |
| `IAsyncObservable<T>` → `Task<T>` | `await`, `ForEachAsync` | come in Rx classico, `await` restituisce l'ultimo valore. AsyncRx 6.0.0-alpha.18 non ha una strada per tornare a `IObservable<T>` o a `IAsyncEnumerable<T>` | — |

Ne escono tre regole:

- **Un `Task` è un risultato, un observable è una ricetta.** `ToObservable()` avvolge un risultato che esiste già, e ogni subscriber vede quello. Per ritentare, ripetere o cancellare serve la ricetta: `FromAsync(ct => …)`.
- **Da molti valori a uno, scegli quale.** `await` su un observable sceglie l'ultimo valore. `FirstAsync()`, `LastOrDefaultAsync()` e `ToTask()` rendono la scelta visibile a chi legge il codice.
- **Da push a pull, l'arretrato sta da qualche parte.** In una coda nascosta e senza limite (`ToAsyncEnumerable()`), sul thread bloccato del producer (`ToAsyncObservable()`), o in un canale bounded di cui hai scelto la politica: `DropOldest` quando conta l'ultimo valore, `Wait` quando il producer può rallentare.

## Quale scegliere

- **Un valore**: `Task<T>`; `ValueTask<T>` quando completa spesso in modo sincrono, su un hot path ([Demo 3](../demos/03-ValueTask/README-it.md)).
- **Una sequenza che il consumer legge al suo ritmo**: `IAsyncEnumerable<T>` quando producer e consumer sono lo stesso flusso di codice; `Channel<T>` quando sono indipendenti e serve un buffer di dimensione scelta da te ([Demo 5](../demos/05-Channels/README-it.md)).
- **Valori che arrivano quando vogliono**, da combinare nel tempo (`Throttle`, `Buffer`, `CombineLatest`, `Switch`) o da consegnare a più subscriber: `IObservable<T>` ([Demo 6](../demos/06-RxInterop/README-it.md)).
- **Una sequenza push con un consumer async che deve frenare il producer**: un `Channel<T>` bounded in mezzo, oppure `IAsyncObservable<T>`, che è ancora sperimentale (6.5–6.7).

## Dettagli da sapere

- **`ValueTask<T>` non rientra nel quadro dell'`AsyncSubject`**: si awaita una volta sola, e non è un risultato condiviso. Per condividerlo, `AsTask()` ([Demo 3](../demos/03-ValueTask/README-it.md)).
- **Un `Task` senza risultato diventa un `IObservable<Unit>`**: `Unit` è il tipo di Rx per "nessun valore".
- **`await` su un observable richiede `using System.Reactive.Linq;`**: `GetAwaiter` è un extension method.
- **Hot e cold riguardano la sorgente, non il tipo.** Lo stesso `IObservable<T>` può avviare una nuova esecuzione per ogni subscriber o condividerne una già in corso; lo dice solo il codice che lo costruisce. `Publish`, `Replay` e `RefCount` trasformano una sorgente cold in una condivisa, con le trappole della 6.4.

## Riferimenti

- [Introduction to Rx.NET — Key types: `IObservable<T>` e `IEnumerable<T>`, push e pull](https://introtorx.com/chapters/key-types.html)
- [Introduction to Rx.NET — Creating Observable Sequences: da `Task` a `IObservable`, `ToObservable` e `FromAsync`](https://introtorx.com/chapters/creating-observable-sequences.html)
- [Introduction to Rx.NET — Leaving Rx's World: `await` su un `IObservable<T>`, `ToTask`](https://introtorx.com/chapters/leaving-reactive-extensions-world.html)
- [Generate and consume async streams](https://learn.microsoft.com/dotnet/csharp/asynchronous-programming/generate-consume-asynchronous-stream)
- [dotnet/reactive — Rx.NET, Ix.NET e AsyncRx.NET](https://github.com/dotnet/reactive)
