# Demo 5 — Molti valori, pull: Channels

[English](README.md) · **Italiano**

Fino a Demo 4 ogni operazione asincrona produce un valore: un `Task<T>`, una `TaskCompletionSource`, un `AsyncLazy<T>`, un `ValueTask<T>`.
Qui QuoteHub riceve uno stream: tick di prezzo che arrivano più in fretta di quanto il database riesca a salvarli, e messaggi che attraversano più stadi di elaborazione.
Un `Channel<T>` è la coda asincrona tra chi produce e chi consuma, ed è *pull*: il consumer legge quando è pronto, e quando il producer va più veloce decide il canale cosa succede.
Demo 6 farà lo stesso viaggio in *push*, con Rx.NET.

Le API si imparano in pochi minuti: `WriteAsync`, `ReadAllAsync`, `Complete`.
Quello che conta in produzione sono le decisioni intorno: quanto può crescere la coda e cosa fare quando è piena (5.1), chi chiude un canale e come viaggia un errore tra gli stadi di una pipeline (5.2).
Sbagliarle non dà errori di compilazione, e il codice sbagliato in code review sembra ragionevole: dà una coda che si mangia la memoria, una pipeline che dice "completato" e intanto perde messaggi, o che resta appesa per sempre quando un servizio esterno va giù.
Lo scenario 5.3 aggiunge due strumenti di .NET 9 per gestire molti risultati nell'ordine giusto.

## Come si lancia

```bash
dotnet run --project demos/05-Channels                        # menu interattivo
dotnet run --project demos/05-Channels -- 5.1                 # broken, Invio, fixed
dotnet run --project demos/05-Channels -- pipeline --fixed    # solo la variante fixed
dotnet run --project demos/05-Channels -- all --no-pause      # tutti gli scenari, senza pause
```

Da IDE ci sono il profilo `menu` e uno per scenario: `5.1 backpressure`, `5.2 pipeline`, `5.3 when-each`.
Lo scenario 5.3 non ha varianti e gira una volta sola.

I numeri qui sotto vengono da un'esecuzione in Debug su .NET 11.0.0.
Quelli che dipendono dai tempi (elementi in coda, memoria, età del dato, millisecondi) cambiano da un'esecuzione all'altra: sono i valori di quell'esecuzione.

## ★ 5.1 `backpressure` — quando il producer va più veloce

In [BackpressureDemo.cs](BackpressureDemo.cs) il producer è il feed di mercato: 100 tick ogni 50 ms, ciascuno con il suo messaggio grezzo da 1 KB.
Il consumer li persiste: 10 tick, poi 50 ms di round-trip verso il database. Sulla carta 2000 tick al secondo contro 200.
Ogni scenario dura 2,5 secondi e ogni 500 ms stampa i tick prodotti, persistiti, in coda (`Reader.Count`) e scartati, l'età dell'ultimo tick persistito e la memoria trattenuta, misurata dopo una garbage collection completa.

Il producer è lo stesso in tutte le varianti, e non sa niente del canale che ha davanti:

```csharp
while (true)
{
    for (var i = 0; i < TicksPerBurst; i++)
    {
        var symbol = Symbols[sequence % Symbols.Length];
        await writer.WriteAsync(new Tick(++sequence, symbol, Stopwatch.GetTimestamp(), new byte[1024]), cancellationToken);
        stats.OnProduced();
    }

    await Task.Delay(Period, cancellationToken);
}
```

**Broken.** Il canale è unbounded:

```csharp
var channel = mode switch
{
    Mode.Unbounded => Channel.CreateUnbounded<Tick>(),
    // …
};
```

```text
── Channel.CreateUnbounded — the producer never waits
   509 ms [t  5] produced   900  persisted   90  queued   810  dropped     0  staleness   502 ms  memory +0.9 MB
  1023 ms [t  9] produced  1700  persisted  170  queued  1530  dropped     0  staleness   936 ms  memory +1.7 MB
  1538 ms [t  8] produced  2500  persisted  250  queued  2250  dropped     0  staleness  1375 ms  memory +2.4 MB
  2054 ms [t  5] produced  3300  persisted  330  queued  2970  dropped     0  staleness  1812 ms  memory +3.2 MB
  2566 ms [t  9] produced  4065  persisted  410  queued  3655  dropped     0  staleness  2248 ms  memory +4.0 MB
  Producer rate                    1626 ticks/s
  Consumer rate                    164 ticks/s
  …
✘ The queue grows without limit: memory and latency climb until the process dies.
```

Su un canale unbounded c'è sempre posto, quindi `WriteAsync` completa subito e il producer non aspetta mai.
In questa esecuzione il producer scrive 1626 tick al secondo e il consumer ne persiste 164: la differenza diventa coda, circa 700 tick in più ogni mezzo secondo.
Ogni tick in coda trattiene il suo KB, e la memoria sale di pari passo: +4,0 MB dopo 2,5 secondi.
E siccome il canale è FIFO, il consumer persiste tick sempre più vecchi: l'ultimo prezzo salvato ha 2,2 secondi.
Niente ferma la crescita: con un'esecuzione più lunga memoria e latenza continuano a salire.

**Fixed.** Lo stesso producer e lo stesso consumer, con un canale bounded da 100 elementi, in due modalità:

```csharp
var channel = mode switch
{
    // …
    Mode.Wait => Channel.CreateBounded<Tick>(new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait }),
    // itemDropped: l'unico modo per sapere (contare, loggare, fare dispose di) cosa ha buttato via DropOldest
    _ => Channel.CreateBounded<Tick>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest },
        itemDropped: _ => stats.OnDropped())
};
```

```text
── Bounded(100), FullMode.Wait — orders: nothing may be lost, so the producer slows down
   512 ms [t  9] produced   190  persisted   90  queued   100  dropped     0  staleness   492 ms  memory +0.1 MB
  …
  2542 ms [t  5] produced   510  persisted  410  queued   100  dropped     0  staleness   625 ms  memory +0.1 MB
  Producer rate                    204 ticks/s
  Consumer rate                    164 ticks/s
! Nothing lost, memory flat — but the producer now runs at the consumer's speed: upstream must cope.

── Bounded(100), FullMode.DropOldest — prices: only the latest one matters
   514 ms [t  8] produced   900  persisted   90  queued   100  dropped   710  staleness    62 ms  memory +0.1 MB
  …
  2558 ms [t  5] produced  4100  persisted  410  queued   100  dropped  3590  staleness    62 ms  memory +0.1 MB
  Producer rate                    1640 ticks/s
  Dropped (itemDropped callback)   3590
✔ Producer at full speed, memory flat, the data persisted is fresh — the old ticks are gone, and counted.
Which FullMode is right is a business decision, not a technical one.
```

| Dopo 2,5 s | Unbounded | Bounded(100) `Wait` | Bounded(100) `DropOldest` |
|---|---|---|---|
| Producer | 1626 tick/s | **204 tick/s** | 1640 tick/s |
| Consumer | 164 tick/s | 164 tick/s | 164 tick/s |
| In coda | 3655 | 100 | 100 |
| Scartati (`itemDropped`) | 0 | 0 | 3590 |
| Età dell'ultimo tick persistito | 2248 ms | 625 ms | **62 ms** |
| Memoria trattenuta | +4,0 MB | +0,1 MB | +0,1 MB |

- **`Wait`**: quando il canale è pieno, `WriteAsync` aspetta che si liberi un posto. È la backpressure: il producer rallenta al passo del consumer (510 prodotti = 410 persistiti + 100 in coda), la memoria resta piatta e non si perde niente. Ma ogni tick aspetta dietro agli altri 100: a 164 tick al secondo sono circa 0,6 secondi, i 625 ms dell'output. E il problema passa a monte: chi alimenta il producer deve saper aspettare, o accumulare da qualche altra parte. È la scelta giusta per gli ordini.
- **`DropOldest`**: la scrittura non aspetta mai. Quando il canale è pieno butta via l'elemento più vecchio per fare posto al nuovo. Il producer resta a piena velocità, la coda non supera 100 elementi, e siccome contiene solo i tick più recenti il prezzo persistito ha al massimo qualche decina di millisecondi. I 3590 tick scartati li conta `itemDropped`: senza il callback sparirebbero senza traccia. È la scelta giusta per i prezzi, dove un tick superato da uno più recente non vale più niente.

Un canale unbounded non è una scelta: rimanda il problema finché c'è memoria.
Con un canale bounded la domanda diventa *cosa fare quando è pieno*, e la risposta dipende dal dato, non dalla tecnica.

## ★ 5.2 `pipeline` — completamento ed errori tra gli stadi

In [PipelineDemo.cs](PipelineDemo.cs) i messaggi del bus attraversano quattro stadi: source → parse → enrich (il prezzo dal `FakeQuoteProvider`, 20 ms) → persist (un insert da 5 ms).
Ogni stadio ha 4 worker, e tra uno stadio e l'altro c'è un canale `Bounded(20)`.
Il chiamante legge le ricevute dall'ultimo canale con `await foreach` finché il canale non si completa, con un timeout di 3 secondi.

Ogni variante esegue due scenari:

1. 160 messaggi, tra cui un messaggio avvelenato, il #100 (`100|MSFT|ten`: la quantità non è un numero), poi la fine normale dell'input;
2. il provider di quotazioni va giù (`FailNext(int.MaxValue)`: ogni chiamata fallisce).

**Broken.** Ogni stadio lancia i suoi worker e se ne dimentica:

```csharp
// Broken: in code review sembra ragionevole, cancellazione compresa.
static void StartStageBroken<TIn, TOut>(ChannelReader<TIn> input, ChannelWriter<TOut> output,
    Func<TIn, CancellationToken, Task<TOut>> transform, CancellationToken cancellationToken)
{
    for (var i = 0; i < Workers; i++)
    {
        _ = Task.Run(async () =>                         // fire-and-forget: un worker che fallisce muore in silenzio
        {
            await foreach (var item in input.ReadAllAsync(cancellationToken))
                await output.WriteAsync(await transform(item, cancellationToken), cancellationToken);

            output.Complete();                           // il PRIMO worker che finisce chiude il canale per tutti gli altri
        });
    }
}
```

Il token di cancellazione c'è, passato a `ReadAllAsync`, `WriteAsync` e alla trasformazione come nella variante fixed, ma nessuno lo cancella mai: quello che manca è uno stadio che possieda i suoi worker.

Scenario 1, il messaggio avvelenato:

```text
── Scenario 1: a poison message, then a normal end of input
✔ Caller: pipeline completed in 1228 ms
  Produced                         160
  Persisted                        156
  Dead-lettered                    0
  Unaccounted for (lost)           4
  Exceptions nobody observed       9
```

Il ✔ mente: il chiamante vede la pipeline completata, ma nel database ci sono 156 messaggi su 160.
Il messaggio avvelenato uccide il worker di parse che lo riceve: l'eccezione resta nel suo `Task.Run`, che nessuno attende, e il messaggio sparisce senza finire da nessuna parte.
Poi, a fine input, in ogni stadio il primo worker che trova l'input vuoto e completato chiama `output.Complete()` per tutti.
Gli altri, in quel momento, o hanno ancora un elemento in mano, e il loro `WriteAsync` su un canale chiuso solleva `ChannelClosedException` (l'elemento è perso), oppure arrivano anche loro a `output.Complete()`, che su un canale già completato solleva anch'esso un'eccezione (`ChannelClosedException`, una `InvalidOperationException`).
In ogni stadio finisce bene un solo worker su quattro: tre stadi, 9 eccezioni, che nessuno vede finché il GC non finalizza i task falliti (`TaskScheduler.UnobservedTaskException`).
I 4 messaggi mancanti sono il #100 e quelli che altri worker avevano in mano quando il canale si è chiuso sotto di loro.

Scenario 2, il provider giù:

```text
── Scenario 2: the quote provider goes down
✘ Caller: no completion after 3015 ms — the pipeline hangs (and its blocked tasks leak)
  Produced                         48
  Persisted                        0
  Dead-lettered                    0
  Exceptions nobody observed       4
```

Ogni worker di enrich prende un trade, il provider solleva `ProviderException` e il worker muore: 4 eccezioni non osservate, e nessuno arriverà mai al `Complete()` del canale verso persist.
Persist e il chiamante aspettano un canale che non si completerà mai.
A monte nessuno legge più: il canale verso enrich si riempie, i worker di parse restano bloccati su `WriteAsync`, poi si riempie il canale del source e si blocca anche lui.
I 48 messaggi prodotti sono esattamente quelli che ci stanno: 4 nelle mani dei worker di enrich morti, 20 nel canale verso enrich, 4 nei worker di parse bloccati, 20 nel canale del source.
Senza il timeout del chiamante l'attesa non finirebbe mai, e i task bloccati restano lì.

**Fixed.** Lo stadio possiede i suoi worker, li attende tutti, e completa il suo output una volta sola:

```csharp
// Fixed: lo stadio possiede i suoi worker e completa il suo output esattamente una volta.
static async Task RunStageAsync<TIn, TOut>(string name, ChannelReader<TIn> input, ChannelWriter<TOut> output,
    Func<TIn, CancellationToken, Task<TOut>> transform, PipelineContext context)
{
    var workers = Enumerable.Range(0, Workers).Select(_ => Task.Run(async () =>
    {
        try
        {
            await foreach (var item in input.ReadAllAsync(context.Token))
            {
                TOut result;
                try
                {
                    result = await transform(item, context.Token);
                }
                catch (PoisonMessageException ex)
                {
                    context.DeadLetters.Enqueue(ex.MessageId);   // errore dell'elemento: parcheggialo e vai avanti
                    continue;
                }

                await output.WriteAsync(result, context.Token);
            }
        }
        catch (Exception ex)
        {
            context.Fail(name, ex);   // errore dello stadio: cancella tutto, a monte resterebbero bloccati su un canale pieno
            throw;
        }
    })).ToArray();

    try
    {
        await Task.WhenAll(workers);
        output.TryComplete();                                     // solo quando TUTTI i worker hanno finito
    }
    catch (Exception ex)
    {
        output.TryComplete(context.FirstError ?? ex);             // chi legge a valle vede la causa originale
    }
}
```

**Il completamento.** Lo stadio completa il suo output dopo `Task.WhenAll(workers)`, cioè quando tutti i worker sono usciti dal loro `await foreach`.
Così il completamento scende lungo la pipeline un passo alla volta: il source completa il primo canale dopo l'ultimo messaggio; i worker di parse escono quando quel canale è vuoto e completato, e solo allora lo stadio completa il canale verso enrich; e così via fino all'ultimo canale, dove termina l'`await foreach` del chiamante.
Completare un canale non butta via quello che contiene: chi legge riceve prima tutti gli elementi rimasti, poi la fine.

**Gli errori.** Un errore del singolo elemento (`PoisonMessageException`) non ferma lo stadio: il messaggio va in dead letter e il worker passa al successivo.
Qualsiasi altra eccezione è un errore dello stadio, e passa da `context.Fail`:

```csharp
public void Fail(string stage, Exception error)
{
    if (error is OperationCanceledException)
        return;

    if (Interlocked.CompareExchange(ref firstError, error, null) is null)
    {
        FailedStage = stage;
        cts.Cancel();
    }
}
```

**Cosa succede agli altri worker.** Il primo errore vero vince (`CompareExchange`) e cancella il token che tutti gli stadi passano a `ReadAllAsync`, `WriteAsync` e alla trasformazione.
Gli altri worker, a monte e a valle, escono per la cancellazione: anche quelli bloccati su un canale pieno, che nel broken, dove nessuno cancella il token, restavano lì per sempre.
`Fail` ignora le `OperationCanceledException`, così la causa registrata resta la `ProviderException` e non la cancellazione che ne è la conseguenza.
Ogni stadio, quando i suoi worker sono usciti, completa l'output con `context.FirstError`: l'errore viaggia a valle con il canale, e il chiamante riceve dal suo `await foreach` l'eccezione originale.

```text
── Scenario 1: a poison message, then a normal end of input
✔ Caller: pipeline completed in 1260 ms
  Stage tasks still running        0
  Produced                         160
  Persisted                        159
  Dead-lettered                    1 (100)
  Unaccounted for (lost)           0
  Exceptions nobody observed       0

── Scenario 2: the quote provider goes down
✔ Caller: pipeline failed in 27 ms with ProviderException: Provider failed for AAPL (call #4)
  root cause, from stage 'enrich': ProviderException: Provider failed for AAPL (call #4)
  Stage tasks still running        0
  Produced                         48
  Persisted                        0
  Dead-lettered                    0
  Exceptions nobody observed       0
```

Nel primo scenario il #100 finisce in dead letter e gli altri 159 arrivano nel database, in un tempo simile a quello del broken.
Nel secondo la pipeline fallisce in 27 ms invece di restare appesa, e il chiamante sa cosa è successo e in quale stadio.
In entrambi nessun task resta in esecuzione e nessuna eccezione va persa: ogni worker è atteso dal suo stadio, e ogni stadio trasforma il proprio errore nel completamento del canale.

**Le opzioni dei canali.** Prima degli scenari il programma stampa:

```text
160 messages, poison message #100, 4 workers per stage, Bounded(20) between stages
Stage channels: SingleReader/SingleWriter = false (4 workers each side),
AllowSynchronousContinuations = false (the default: same trap as RunContinuationsAsynchronously in Demo 1).
```

```csharp
static Channel<T> Bounded<T>() => Channel.CreateBounded<T>(new BoundedChannelOptions(ChannelCapacity)
{
    SingleReader = false,
    SingleWriter = false,
    AllowSynchronousContinuations = false
});
```

Sono tutti valori di default, scritti per esteso perché sono decisioni.
`SingleReader` e `SingleWriter = true` sono una promessa, al massimo un lettore (o uno scrittore) alla volta, in cambio della quale il canale può usare un'implementazione specializzata: con quattro worker per lato non si può fare.
`AllowSynchronousContinuations = true` permette a chi scrive di eseguire direttamente, dentro la sua `WriteAsync` o `TryWrite`, la continuation del lettore in attesa, invece di accodarla.
È la stessa trappola della `TaskCompletionSource` senza `RunContinuationsAsynchronously` in [Demo 1](../01-TaskCompletionSource/README-it.md) (scenario 1.1): il codice del consumer gira sul thread del producer, anche mentre quello tiene un lock.
La demo lascia il default, `false`.

In una pipeline il completamento è un protocollo, non una chiamata: il canale lo chiude chi possiede *tutti* i suoi scrittori, una volta sola.
Gli errori viaggiano a valle con `Complete(ex)` e a monte con la cancellazione, e i worker non si lanciano fire-and-forget.

## 5.3 `when-each` — in ordine di arrivo, o di priorità

Nessuna variante broken: lo scenario mostra due strumenti arrivati in .NET 9, da [WhenEachDemo.cs](WhenEachDemo.cs).

**`Task.WhenEach`.** Cinque richieste di quotazione partono insieme, ognuna con la sua latenza: GOOG 50 ms, AAPL 100, AMZN 200, MSFT 300, NVDA 500.

```csharp
var quotes = await Task.WhenAll(StartQuotes());
foreach (var quote in quotes)
    Out.Log($"{quote.Symbol} {quote.Price}");

// …

await foreach (var task in Task.WhenEach(StartQuotes()))
{
    var quote = await task;
    Out.Log($"{quote.Symbol} {quote.Price}");
}
```

```text
── Task.WhenAll — every quote appears when the slowest one arrives
   516 ms [t  8] MSFT 281.34
   516 ms [t  8] AAPL 193.34
   516 ms [t  8] NVDA 474.34
   516 ms [t  8] AMZN 337.34
   516 ms [t  8] GOOG 657.34

── Task.WhenEach — each quote as soon as it is ready (completion order)
    61 ms [t  8] GOOG 657.34
   108 ms [t 17] AAPL 193.34
   203 ms [t  8] AMZN 337.34
   313 ms [t 17] MSFT 281.34
   500 ms [t 17] NVDA 474.34
```

Con `WhenAll` tutte le righe compaiono a 516 ms, nell'ordine delle richieste: la quotazione di GOOG, pronta dopo 50 ms, aspetta quella di NVDA.
`Task.WhenEach` restituisce un `IAsyncEnumerable<Task<T>>` che produce i task man mano che completano, e ogni quotazione si mostra appena arriva.
Produce il task, non il valore: `await task` prende il risultato, o solleva di nuovo l'eccezione di quella sola chiamata, e non sospende perché il task è già completato.
Prima di .NET 9 lo stesso risultato richiedeva un ciclo che chiamava ripetutamente `Task.WhenAny` per prendere il prossimo task completato.

**`Channel.CreateUnboundedPrioritized`.** Un consumer elabora un ordine ogni 60 ms; arrivano sei ordini normali e, dopo 150 ms, due urgenti.

```csharp
// Il comparer decide l'ordine: viene letto per primo l'elemento più piccolo.
var channel = Channel.CreateUnboundedPrioritized(new UnboundedPrioritizedChannelOptions<Order>
{
    Comparer = Comparer<Order>.Create((a, b) => a.Priority != b.Priority
        ? a.Priority.CompareTo(b.Priority)
        : a.Id.CompareTo(b.Id))
});
// …
enum Priority { Urgent, Normal }
```

```text
── Channel.CreateUnboundedPrioritized — urgent orders jump the queue
     4 ms [t 17] 6 normal orders queued
     6 ms [t  8] processing order #1 (Normal)
    75 ms [t  8] processing order #2 (Normal)
   138 ms [t  8] processing order #3 (Normal)
   169 ms [t  8] 2 urgent orders queued
   201 ms [t  8] processing order #7 (Urgent)
   263 ms [t  8] processing order #8 (Urgent)
   326 ms [t  8] processing order #4 (Normal)
   388 ms [t  8] processing order #5 (Normal)
   451 ms [t  8] processing order #6 (Normal)
```

Un canale prioritizzato non è FIFO: il prossimo elemento letto è il più piccolo secondo il comparer (`Comparer<T>.Default` se non ne passi uno).
Qui `Urgent` vale 0 e `Normal` 1, e a parità di priorità decide l'Id: gli ordini normali restano nell'ordine d'arrivo.
I due urgenti, accodati a 169 ms, passano davanti a #4, #5 e #6, che erano in coda da prima.
L'ordine già in lavorazione (#3) non viene interrotto: la priorità decide solo quale elemento si legge dopo.
Il canale è unbounded: come in 5.1, nessuno frena il producer.

`WhenEach` serve quando hai N task e vuoi agire su ciascun risultato appena è pronto; il canale prioritizzato quando il consumer deve scegliere per importanza e non per ordine di arrivo.

## Cosa sapere

- **Bounded per default.** Con `CreateUnbounded`, e con `CreateUnboundedPrioritized`, nessuno frena il producer: se è più veloce del consumer, la differenza diventa memoria e latenza, senza limite (5.1).
- **Il `FullMode` lo sceglie il dato.** `Wait`, il default, quando niente può andare perso: il producer rallenta e il problema passa a monte. `DropOldest` quando conta solo il valore più recente. Esistono anche `DropNewest` (scarta l'elemento più recente nel canale) e `DropWrite` (scarta quello che si sta scrivendo).
- **Con le modalità drop passa `itemDropped`.** È l'unico modo per contare, loggare o fare dispose di quello che il canale butta via.
- **Il writer si completa una volta, quando hanno finito tutti.** Con più worker sullo stesso canale il completamento non spetta a un worker ma a chi li attende tutti (`Task.WhenAll`, poi `TryComplete`). `Complete` su un canale già completato solleva un'eccezione, `TryComplete` restituisce `false`.
- **Gli errori viaggiano nei due sensi.** A valle con `TryComplete(ex)`: l'`await foreach` di chi legge termina con quell'eccezione. A monte con un token di cancellazione condiviso: senza, chi scrive su un canale pieno che nessuno legge più resta bloccato per sempre. A chi chiama arriva l'errore originale, non la cancellazione che ne è la conseguenza.
- **Errore dell'elemento ≠ errore dello stadio.** Un messaggio malformato va in dead letter e la pipeline prosegue; un provider giù ferma tutto.
- **Niente worker fire-and-forget.** Un `Task` fallito che nessuno attende si scopre solo quando il GC lo finalizza.
- **Le opzioni sono promesse.** `SingleReader`/`SingleWriter = true` solo con un solo lettore o scrittore alla volta; `AllowSynchronousContinuations` resta `false` a meno di sapere esattamente dove gireranno le continuation.
- Lo stesso canale bounded con `DropOldest` torna in [Demo 6](../06-RxInterop/README-it.md) come confine esplicito tra uno stream Rx e un consumer async.

## Riferimenti

- [Channels — documentazione .NET](https://learn.microsoft.com/dotnet/core/extensions/channels)
- [Stephen Toub — An Introduction to System.Threading.Channels](https://devblogs.microsoft.com/dotnet/an-introduction-to-system-threading-channels/)
- [Task.WhenEach — API](https://learn.microsoft.com/dotnet/api/system.threading.tasks.task.wheneach)
- [Channel.CreateUnboundedPrioritized — API](https://learn.microsoft.com/dotnet/api/system.threading.channels.channel.createunboundedprioritized)
