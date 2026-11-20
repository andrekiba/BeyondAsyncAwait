# Demo 1 — TaskCompletionSource: il mattone

Un metodo `async` produce un `Task` che si completa quando il metodo finisce.
`TaskCompletionSource<T>` (TCS) separa le due cose: il `Task` lo restituisci subito, e a completarlo ci pensa qualcun altro, quando il dato arriva.
È il ponte tra un mondo a callback (un thread che legge da una connessione, l'handler di un message bus, un comando dell'operatore) e un chiamante che vuole solo fare `await`.

In QuoteHub, il gateway che chiede quotazioni a provider esterni lenti e instabili, la TCS compare ogni volta che il gateway non chiama un metodo ma aspetta un messaggio: la risposta su una connessione, la reply correlata su un bus, il segnale che fa ripartire una pipeline in pausa.
È anche il mattone con cui si costruiscono primitive di coordinamento su misura, come l'evento dello scenario 1.3.

Chi completa la TCS lo fa sul proprio thread, magari con un lock in mano, e magari quando nessuno aspetta più.
I tre scenari sono le tre domande da porsi ogni volta: *dove* gira il codice che aspetta (1.1), *chi* vince quando risposta e timeout arrivano insieme, e chi pulisce dopo (1.2), *quando* si può sostituire una TCS con una nuova (1.3).
Nessuno dei tre bug arriva al chiamante come eccezione: si vedono come latenza su un thread che non è tuo, memoria che cresce round dopo round, waiter che non si svegliano più.

## Come si lancia

```bash
dotnet run --project demos/01-TaskCompletionSource -- 1.1                   # broken, Invio, fixed
dotnet run --project demos/01-TaskCompletionSource -- rpc-correlation --fixed
dotnet run --project demos/01-TaskCompletionSource -- all --no-pause        # tutti gli scenari, entrambe le varianti
```

Da IDE ci sono i profili `menu`, `1.1 inline-continuations`, `1.2 rpc-correlation` e `1.3 manual-reset-event`.

Ogni riga di log riporta i millisecondi dall'inizio della variante e il thread id (`[t  4]`): in questa Demo il thread è quasi sempre il punto.
Gli output qui sotto vengono da una build Debug su .NET 11.0.0; i millisecondi cambiano da una macchina all'altra.

## ★ 1.1 `inline-continuations` — `SetResult` esegue il tuo codice sul read loop

Una connessione con un thread dedicato, il *read loop*, che legge i frame in arrivo e completa la richiesta pendente corrispondente ([InlineContinuationsDemo.cs](InlineContinuationsDemo.cs)).
Cinque richieste aspettano la risposta; i frame arrivano ogni 20 ms, e ogni handler lavora 150 ms sulla risposta.

```csharp
static async Task HandleAsync(FakeConnection connection, int id, Action countOnReadLoop)
{
    var reply = await connection.SendAsync(id);
    // …
    // Lavoro CPU-bound sulla risposta: deserializzare, validare, mappare.
    Thread.Sleep(HandlerWork);
}

// Il read loop: completare dentro il lock è comune.
lock (gate)
{
    pending.Remove(id, out var tcs);
    tcs!.SetResult(new Frame(id));
}
```

**Broken.** La TCS è creata con `TaskCreationOptions.None`:

```text
    35 ms [t  4] read loop: frame 1 dispatched, 6 ms after it arrived
✘ request 1: handling reply 1 ON THE READ LOOP (inside its lock: True)
   194 ms [t  4] read loop: frame 2 dispatched, 146 ms after it arrived
✘ request 2: handling reply 2 ON THE READ LOOP (inside its lock: True)
  …
   668 ms [t  4] read loop: frame 5 dispatched, 560 ms after it arrived
✘ request 5: handling reply 5 ON THE READ LOOP (inside its lock: True)

── Results
  Handlers run on the read loop    5/5
  Max frame delay in the read loop 560 ms
  All replies handled after        829 ms
```

**Perché.** Con le opzioni di default `SetResult` può eseguire la continuation dell'`await` direttamente, sul thread che lo chiama, prima di restituire: qui lo fa tutte e cinque le volte.
Il resto di `HandleAsync`, `Thread.Sleep` compreso, gira quindi sul read loop (`t 4`) e dentro il suo `lock`.
Per 150 ms il loop non legge niente: ogni frame viene smistato circa 140 ms più tardi del precedente rispetto al suo arrivo, e il ritardo si accumula fino a 560 ms.
Il lock poi resta preso per tutta la durata dell'handler, e `SendAsync` prende lo stesso lock per registrare una nuova richiesta: chi vuole solo inviare, da un altro thread, resta fermo anche lui.

**Fixed.** Cambia solo l'opzione con cui nasce la TCS:

```csharp
var options = variant == Variant.Broken
    ? TaskCreationOptions.None
    : TaskCreationOptions.RunContinuationsAsynchronously;
// …
var tcs = new TaskCompletionSource<Frame>(completionOptions);
```

Con `RunContinuationsAsynchronously` la continuation viene eseguita in modo asincrono, su un thread del pool, e `SetResult` torna subito al loop:

```text
    30 ms [t  8] read loop: frame 1 dispatched, 10 ms after it arrived
✔ request 1: handling reply 1 on a pool thread (inside the loop's lock: False)
  …
   108 ms [t  8] read loop: frame 5 dispatched, 8 ms after it arrived
✔ request 5: handling reply 5 on a pool thread (inside the loop's lock: False)

── Results
  Handlers run on the read loop    0/5
  Max frame delay in the read loop 12 ms
  All replies handled after        264 ms
```

Gli handler girano in parallelo, il loop resta al passo dei frame, e il totale scende da 829 a 264 ms: l'ultimo frame a 108 ms, più i 150 ms del suo handler.

Il messaggio: chi chiama `SetResult` presta il proprio thread a codice che non conosce.
Se quel thread è un loop di I/O, un timer o il dispatcher di un bus, o se tiene un lock, la TCS va creata con `RunContinuationsAsynchronously`.

## ★ 1.2 `rpc-correlation` — request/response su un message bus

QuoteHub chiede una quotazione pubblicando un messaggio su un bus, e la risposta arriva come un altro messaggio con lo stesso id ([RpcCorrelationDemo.cs](RpcCorrelationDemo.cs)).
Il client tiene una TCS per richiesta in un dizionario indicizzato per id: l'handler delle risposte la ritrova e la completa.

Il `FakeBus` è deterministico: su 10 messaggi 1 va perso, 2 ricevono risposta dopo 250 ms, 7 entro 40 ms.
Il timeout è di 100 ms, quindi le risposte lente arrivano quando il chiamante ha già rinunciato.
Lo scenario fa 3 round da 1000 richieste concorrenti, e dopo ogni round misura l'heap con un GC completo.

**Broken.** La prima versione che si scrive:

```csharp
public Task<RpcReply> RequestAsync(string symbol, TimeSpan timeout, CancellationToken cancellationToken)
{
    var id = Guid.NewGuid();
    var tcs = new TaskCompletionSource<RpcReply>();
    pending[id] = tcs;

    // Mai disposta: resta registrata sul token della connessione, e tiene viva la tcs.
    cancellationToken.Register(() => tcs.SetCanceled());

    // Mai disposta nemmeno questa.
    var timeoutCts = new CancellationTokenSource(timeout);
    timeoutCts.Token.Register(() =>
    {
        if (!tcs.Task.IsCompleted)
            tcs.SetCanceled();
    });
    // …
}

void OnReply(RpcReply reply)
{
    // Mai rimossa; lancia se il timeout ha già completato il task.
    if (pending.TryGetValue(reply.Id, out var tcs))
        tcs.SetResult(reply);
}
```

```text
✘ bus: reply handler threw InvalidOperationException: An attempt was made to transition a task to a final state when it had already completed.
   425 ms [t 15] round 1: 1000 requests (timeout 100 ms), pending entries 1000, heap 785 KB
   852 ms [t 12] round 2: 1000 requests (timeout 100 ms), pending entries 2000, heap 1,236 KB
  1273 ms [t 15] round 3: 1000 requests (timeout 100 ms), pending entries 3000, heap 1,723 KB

── Results
  Replies received                 2100
  Timed out                        900
  Late replies (after the timeout) 600
  Exceptions thrown into the bus   600
  Pending entries left             3000
  Heap growth, round 1 → 3         +938 KB
```

**Perché.** Tre errori diversi, che si sommano.

- **`SetResult` su una TCS già completata lancia.** Per ogni risposta tardiva il timeout ha già chiamato `SetCanceled`, e `SetResult` lancia `InvalidOperationException`. L'eccezione non arriva al chiamante, che ha già avuto il suo timeout: esce dall'handler ed entra nel codice del bus. Qui `FakeBus` la cattura e la conta, come farebbe una libreria client: 600 eccezioni, una per risposta tardiva. Anche il controllo `IsCompleted` prima di `SetCanceled` non protegge: tra il controllo e la chiamata la risposta può arrivare.
- **Nessuno toglie le entry dal dizionario.** `OnReply` usa `TryGetValue` e non `TryRemove`, e nessun altro rimuove: ogni round lascia 1000 entry in più, comprese quelle completate con successo.
- **Le registration restano attaccate al token della connessione.** `Register` restituisce una `CancellationTokenRegistration` che nessuno dispone: la lambda, e con lei la TCS, resta tra i callback del token finché la connessione vive. Anche togliendo le entry dal dizionario, la TCS resterebbe raggiungibile da lì. E la `CancellationTokenSource` del timeout non viene mai disposta.

Il risultato è un heap che cresce di 450-490 KB a round, su una connessione che in produzione vive per ore.

**Fixed.**

```csharp
public async Task<RpcReply> RequestAsync(string symbol, TimeSpan timeout, CancellationToken cancellationToken)
{
    var id = Guid.NewGuid();
    var tcs = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
    pending[id] = tcs;

    try
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var registration = timeoutCts.Token.Register(
            static state => ((TaskCompletionSource<RpcReply>)state!).TrySetCanceled(), tcs);

        bus.Publish(id, symbol);
        return await tcs.Task;
        // Alternativa più corta per la parte di timeout: await tcs.Task.WaitAsync(timeout, cancellationToken)
    }
    finally
    {
        pending.TryRemove(id, out _);
    }
}

void OnReply(RpcReply reply)
{
    // Una risposta tardiva non trova niente: viene ignorata, non lanciata.
    if (pending.TryRemove(reply.Id, out var tcs))
        tcs.TrySetResult(reply);
}
```

```text
   420 ms [t 10] round 1: 1000 requests (timeout 100 ms), pending entries 0, heap 449 KB
   840 ms [t 11] round 2: 1000 requests (timeout 100 ms), pending entries 0, heap 449 KB
  1260 ms [t 20] round 3: 1000 requests (timeout 100 ms), pending entries 0, heap 449 KB

── Results
  Replies received                 2100
  Timed out                        900
  Late replies (after the timeout) 600
  Exceptions thrown into the bus   0
  Pending entries left             0
  Heap growth, round 1 → 3         0 KB
```

- **`TrySetResult` e `TrySetCanceled`.** Risposta, timeout e cancellazione della connessione competono per completare la stessa TCS: vince il primo, gli altri ricevono `false` invece di un'eccezione.
- **L'entry esce dal dizionario in due punti**: in `OnReply` con `TryRemove` quando arriva la risposta, nel `finally` in caso di timeout o cancellazione. Una risposta tardiva non trova niente e viene ignorata.
- **Timeout e registration vivono quanto la chiamata.** La linked CTS unisce il timeout al token della connessione, e la registration sta sul suo token, non su quello della connessione; i due `using` li rilasciano entrambi a fine chiamata. La lambda è `static` e riceve la TCS come stato: nessuna closure per richiesta.
- **`RunContinuationsAsynchronously`**, per lo stesso motivo dello scenario 1.1: `TrySetResult` gira nel codice di consegna del bus.

Per il chiamante le due versioni sono indistinguibili: 2100 risposte e 900 timeout in entrambe.
Cambia tutto quello che resta indietro: 0 eccezioni nel bus, 0 entry pendenti, heap fermo a 449 KB.

Il messaggio: una TCS ha un ciclo di vita che il compilatore non gestisce per te.
Ogni posto in cui la registri (un dizionario, un token) ha bisogno di un'uscita in `finally`, e quando più parti possono completarla si usa `TrySet*`.

## 1.3 `manual-reset-event` — `Reset` senza perdere i waiter

Un evento manual-reset asincrono fa da cancello per una pipeline: `WaitAsync` aspetta che sia aperto, `Set` lo apre, `Reset` lo richiude ([ManualResetEventDemo.cs](ManualResetEventDemo.cs)).
L'implementazione classica tiene una TCS: `WaitAsync` restituisce il suo `Task`, `Set` la completa, `Reset` la sostituisce con una nuova.
Lo scenario: la pipeline parte in pausa con 3 worker fermi al cancello, l'operatore preme Pause (la pipeline è già in pausa), poi Resume.

**Broken.**

```csharp
// Broken: Reset sostituisce sempre la TCS, anche quando nessuno l'ha ancora completata.
public Task WaitAsync() => tcs.Task;
public void Set() => tcs.TrySetResult();
public void Reset() => tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
```

```text
     2 ms [t 20] worker 1: waiting at the gate
     2 ms [t 20] worker 2: waiting at the gate
     3 ms [t 20] worker 3: waiting at the gate
    60 ms [t 20] operator: Pause (the pipeline is already paused)
   123 ms [t 20] operator: Resume

── Results
  Workers resumed                  0/3
```

**Perché.** I worker aspettano il `Task` della prima TCS.
`Reset` la sostituisce anche se nessuno l'ha completata, e `Set` completa la nuova.
Della vecchia l'evento non ha più il riferimento: nessuno potrà più completarla, e i suoi tre waiter restano sospesi per sempre (lo scenario smette di aspettarli dopo 500 ms).
Nessuna eccezione e nessun thread bloccato da trovare in un dump: solo tre operazioni che non ripartono.

**Fixed.** `Reset` sostituisce la TCS solo se è già completata, e solo se nel frattempo nessun altro l'ha sostituita:

```csharp
public Task WaitAsync() => Volatile.Read(ref tcs).Task;

public void Set() => Volatile.Read(ref tcs).TrySetResult();

public void Reset()
{
    while (true)
    {
        var current = Volatile.Read(ref tcs);
        if (!current.Task.IsCompleted)
            return;

        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref tcs, next, current) == current)
            return;
    }
}
```

```text
    62 ms [t 20] operator: Pause (the pipeline is already paused)
   124 ms [t 20] operator: Resume
   125 ms [t 16] worker 1: resumed
   125 ms [t 20] worker 3: resumed
   125 ms [t 14] worker 2: resumed

── Results
  Workers resumed                  3/3
```

Un `Reset` su un evento già chiuso non fa niente, quindi i waiter restano sulla TCS che `Set` completerà.
Il `CompareExchange` copre la corsa tra due `Reset` concorrenti: senza, entrambi potrebbero vedere la TCS completata e installarne una nuova a testa, e chi ha chiamato `WaitAsync` tra le due sostituzioni resterebbe orfano.
È lo stesso schema dell'`AsyncManualResetEvent` di Stephen Toub (vedi i riferimenti), con la TCS non generica di .NET 5 al posto di `TaskCompletionSource<bool>`.

I tre worker riprendono su thread diversi (`t 16`, `t 20`, `t 14`) perché anche qui la TCS nasce con `RunContinuationsAsynchronously`.
Senza, riprenderebbero uno dopo l'altro dentro la chiamata a `Set` dell'operatore, come gli handler dello scenario 1.1.

Il messaggio: una TCS si completa una volta sola.
Se una primitiva la ricicla, la sostituzione va fatta solo quando è completata, e in modo atomico.

## Cosa sapere

- **Crea le TCS con `RunContinuationsAsynchronously`** quando a completarle è codice che non controlli o che tiene un lock: un read loop, un timer, l'handler di un bus. Senza, quel codice esegue le tue continuation (1.1).
- **Usa `TrySet*` quando più parti possono completare la stessa TCS**: risposta, timeout, cancellazione, shutdown. `Set*` va bene solo se sei certo di essere l'unico, e un controllo `IsCompleted` prima non basta (1.2).
- **Ogni registrazione ha la sua uscita.** Entry in un dizionario → `TryRemove` in `finally`; `CancellationToken.Register` → dispose della `CancellationTokenRegistration`; linked CTS → `using`. Un token che vive quanto la connessione tiene vivo tutto quello che ci registri sopra.
- **`Task.WaitAsync(timeout, cancellationToken)`** (.NET 6+) è l'alternativa più corta a linked CTS e registration, indicata nel sorgente di 1.2. Smette di aspettare senza completare la TCS, quindi la rimozione nel `finally` serve lo stesso. La stessa API, applicata a ogni chiamante, risolve la cancellazione condivisa di [Demo 2](../02-AsyncCaching).
- **Una TCS non si resetta.** Per riusare un segnale la si sostituisce, solo quando è completata e con `CompareExchange` (1.3). Per riusare la sorgente stessa senza allocare un `Task` a operazione c'è `IValueTaskSource` su `ManualResetValueTaskSourceCore<T>`: vedi [Demo 3](../03-ValueTask).
- Gli output di questa pagina vengono da una build con Runtime Async attivo, il default del repo ([Directory.Build.targets](../../Directory.Build.targets)): le continuation inline dello scenario 1.1 ci sono anche lì.

## Riferimenti

- [TaskCreationOptions — RunContinuationsAsynchronously](https://learn.microsoft.com/dotnet/api/system.threading.tasks.taskcreationoptions)
- [Sergey Tepliakov — The danger of `TaskCompletionSource<T>` class](https://devblogs.microsoft.com/premier-developer/the-danger-of-taskcompletionsourcet-class/)
- [Stephen Toub — Building Async Coordination Primitives, Part 1: AsyncManualResetEvent](https://devblogs.microsoft.com/dotnet/building-async-coordination-primitives-part-1-asyncmanualresetevent/)
- [Cancellation in managed threads](https://learn.microsoft.com/dotnet/standard/threading/cancellation-in-managed-threads)
