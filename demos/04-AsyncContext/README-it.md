# Demo 4 — Contesto e ciclo di vita: AsyncLocal e IAsyncDisposable

[English](README.md) · **Italiano**

Il codice async non porta con sé solo i parametri: porta anche un **contesto**.
Ogni flusso logico ha un `ExecutionContext`, e gli `AsyncLocal<T>` vivono lì dentro: è così che tenant e correlation id seguono una richiesta attraverso `await`, thread pool e continuation senza passare da nessuna firma.
La regola si enuncia in una riga e si sbaglia facilmente: il contesto scende verso il lavoro che avvii, non risale verso chi ti ha chiamato, e resta attaccato a tutto ciò che lo ha catturato, anche quando la richiesta è finita da un pezzo.

L'altra metà della Demo riguarda le risorse con un ciclo di vita async.
Un oggetto che per chiudersi deve fare I/O implementa `IAsyncDisposable`, e chi lo possiede deve disporlo in modo async e aspettare che abbia davvero finito.

In QuoteHub sono tre situazioni quotidiane: richieste multi-tenant che tengono il tenant in un `AsyncLocal`, un singleton che rinfresca i prezzi in background, un publisher che manda le quotazioni al bus a blocchi e deve svuotare il buffer allo shutdown.
Sono bug che i test difficilmente vedono: dentro il metodo il valore c'è, i log hanno un tenant (quello sbagliato), lo shutdown può terminare senza errori e perdere comunque l'ultimo batch.

## Come si lancia

```bash
dotnet run --project demos/04-AsyncContext -- 4.1                    # broken, Invio, fixed
dotnet run --project demos/04-AsyncContext -- context-leak --fixed
dotnet run --project demos/04-AsyncContext -- all --no-pause         # tutti gli scenari, entrambe le varianti
```

Da IDE ci sono i profili `menu`, `4.1 asynclocal-flow`, `4.2 context-leak` e `4.3 async-dispose`; da terminale lo stesso profilo si sceglie con `--launch-profile "4.1 asynclocal-flow"`.

**Runtime Async on/off.** Come tutto il repo, la Demo compila con Runtime Async attivo ([Directory.Build.targets](../../Directory.Build.targets)). Per le state machine del compilatore:

```bash
dotnet run --project demos/04-AsyncContext -p:RuntimeAsync=false -- 4.1
```

Lo scenario 4.1 stampa nel banner il modello con cui è compilato l'assembly: `runtime async, .NET 11.0.0` oppure `compiler state machines, .NET 11.0.0`.
Gli output qui sotto vengono da .NET 11.0.0 in Debug. A parte il banner e i millisecondi, l'output dei tre scenari è identico nei due modelli: Runtime Async cambia come viene eseguito un metodo async, non come fluisce il contesto.

## ★ 4.1 `asynclocal-flow` — il valore non torna al chiamante

Il tenant della richiesta sta in un `AsyncLocal<string?>` ([AsyncLocalFlowDemo.cs](AsyncLocalFlowDemo.cs)).
La variante broken lo imposta dentro un metodo async in due modi: dopo un `await`, come farebbe un lookup, e prima di qualunque `await`.

```csharp
static readonly AsyncLocal<string?> Tenant = new();

// …

static async Task SetTenantAsync(string tenant)
{
    await Task.Delay(10); // per esempio un lookup
    Tenant.Value = tenant;
    Out.Log($"inside SetTenantAsync: tenant = {Tenant.Value}");
}

static async Task SetTenantBeforeAnyAwaitAsync(string tenant)
{
    Tenant.Value = tenant;
    Out.Log($"inside SetTenantBeforeAnyAwaitAsync: tenant = {Tenant.Value}");
    await Task.Delay(10);
}
```

Il chiamante azzera il valore, chiama il metodo e lo rilegge:

```csharp
Tenant.Value = null;
await SetTenantAsync("acme");
Out.Metric("after await SetTenantAsync()", Tenant.Value ?? "(null)", ok: false);
```

```text
runtime async, .NET 11.0.0
    21 ms [t  5] inside SetTenantAsync: tenant = acme
  after await SetTenantAsync()     (null)
    21 ms [t  5] inside SetTenantBeforeAnyAwaitAsync: tenant = acme
  set before its first await       (null)
```

Dentro il metodo il valore c'è, al chiamante arriva `(null)`.
Anche nel secondo caso, dove l'assegnazione avviene nella parte sincrona del metodo, prima che sospenda.

**Perché.** Un metodo async lavora su una copia del contesto (copy-on-write): quello che imposta vale per il metodo stesso e per il lavoro che avvia da lì in poi, e sparisce quando il controllo torna al chiamante.
Con le state machine lo garantisce il builder: `AsyncTaskMethodBuilder.Start` salva l'`ExecutionContext` corrente, esegue la parte sincrona del metodo e poi lo ripristina, proprio per evitare che i dati ambientali escano dal metodo verso il chiamante.
Per questo conta anche l'assegnazione fatta prima del primo `await`.
Con Runtime Async il metodo non passa più da `Start` (vedi [Demo 0](../00-RuntimeAsync/README-it.md)), ma la semantica è la stessa, riga per riga:

```text
compiler state machines, .NET 11.0.0
    30 ms [t  5] inside SetTenantAsync: tenant = acme
  after await SetTenantAsync()     (null)
    31 ms [t  5] inside SetTenantBeforeAnyAwaitAsync: tenant = acme
  set before its first await       (null)
```

Un metodo sincrono invece non ha questo confine: scrive direttamente nel contesto di chi lo chiama.

**Fixed.** Tre modi, a seconda di chi deve conoscere il valore.

Un setter sincrono, che non apre una copia del contesto:

```csharp
static void SetTenant(string tenant) => Tenant.Value = tenant;
```

Il metodo async restituisce il valore e lo assegna il chiamante, nel proprio contesto:

```csharp
Tenant.Value = await LookupTenantAsync("acme");
```

Oppure un holder mutabile: l'`AsyncLocal` viene impostato una volta sola, all'inizio della richiesta, e il codice async muta l'oggetto invece di sostituire il valore.

```csharp
Holder.Value = new TenantHolder(); // impostato una volta, all'inizio della richiesta
await ResolveIntoHolderAsync("acme");

// …

static async Task ResolveIntoHolderAsync(string tenant)
{
    await Task.Delay(10);
    Holder.Value!.Name = tenant; // muta l'oggetto condiviso, non sostituisce il valore dell'AsyncLocal
}
```

```text
runtime async, .NET 11.0.0
  after SetTenant() (sync)         acme
  caller assigns the result        acme
  holder mutated by async code     acme
```

Il terzo funziona perché ogni copia del contesto punta alla stessa istanza dell'holder: la mutazione è visibile a tutte.
È per la stessa ragione che `IHttpContextAccessor` tiene nel suo `AsyncLocal` un holder mutabile.
Il rovescio: quell'istanza la vede anche tutto il lavoro avviato dalla richiesta, compreso quello che le sopravvive (vedi 4.2).

Il messaggio: `AsyncLocal` scende, non risale. Se un metodo async deve produrre contesto per il chiamante, lo restituisce; altrimenti il contesto si stabilisce prima, al punto d'ingresso del flusso.

## ★ 4.2 `context-leak` — il background si tiene il tenant della prima richiesta

`PriceRefresher` ([ContextLeakDemo.cs](ContextLeakDemo.cs)) è un singleton con un loop che rinfresca la cache dei prezzi ogni 150 ms.
Parte in modo lazy, dentro la prima richiesta che ne ha bisogno: `EnsureStarted` avvia il loop e registra sul token di shutdown un callback che svuota le sottoscrizioni.

```csharp
static async Task RequestAsync(string tenant, PriceRefresher refresher, CancellationToken shutdown)
{
    Tenant.Value = tenant;
    refresher.EnsureStarted(shutdown);
    await Task.Delay(50);
    Out.Log($"[tenant={Tenant.Value}] request: served quotes");
}

// …

async Task LoopAsync()
{
    while (!stop.IsCancellationRequested)
    {
        await Task.Delay(150, stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!stop.IsCancellationRequested)
            Background("refreshing price cache");
    }
}
```

Tre richieste, tre tenant: `acme` sveglia il singleton, poi `globex` e `initech` in parallelo.
`Background` legge `Tenant.Value` e conta le righe scritte con un tenant: nel lavoro di background non dovrebbe essercene nessuno.
Nella variante broken `EnsureStarted` fa quello che verrebbe naturale:

```csharp
loop = Task.Run(LoopAsync);
shutdown.Register(() => Background("flushing subscriptions on shutdown"));
```

```text
    61 ms [t  5] [tenant=acme] request: served quotes
   124 ms [t  5] [tenant=initech] request: served quotes
   124 ms [t  7] [tenant=globex] request: served quotes
✘ [tenant=acme] background: refreshing price cache
✘ [tenant=acme] background: refreshing price cache
✘ [tenant=acme] background: refreshing price cache
   527 ms [t  7] application stopping
✘ [tenant=acme] background: flushing subscriptions on shutdown
  Background lines tagged 'acme'   4/4
```

Le richieste vedono ciascuna il proprio tenant, anche le due concorrenti: ognuna lavora sulla sua copia del contesto, come in 4.1.
Il background invece è `acme` per sempre.

**Perché.** `Task.Run` cattura l'`ExecutionContext` corrente, quello della richiesta di `acme`, e ci esegue il delegate.
Il loop vive in quel contesto finché l'applicazione non si ferma, e ogni `await` dentro il loop riprende nello stesso contesto.
`CancellationToken.Register` fa lo stesso: cattura il contesto al momento della registrazione e lo usa per il callback, che parte allo shutdown e con `acme` non ha niente a che fare.
Il risultato sono log sbagliati, metriche attribuite al tenant sbagliato e il contesto di quella richiesta tenuto in vita quanto il loop, con tutto ciò a cui puntano i suoi `AsyncLocal`.
E quale tenant "vince" dipende da quale richiesta arriva per prima dopo l'avvio.

Vale lo stesso per un `Timer` creato dentro una richiesta.
In generale catturano il contesto quasi tutte le API che avviano lavoro asincrono, `ThreadPool.QueueUserWorkItem` compreso; fanno eccezione quelle con prefisso `Unsafe`, che per costruzione non lo fanno fluire.

**Fixed.** Il lavoro che sopravvive alla richiesta parte senza il suo contesto.

```csharp
if (suppressFlow)
{
    // Niente della richiesta corrente deve fluire nel lavoro che le sopravvive.
    using (ExecutionContext.SuppressFlow())
        loop = Task.Run(LoopAsync);
    shutdown.UnsafeRegister(_ => Background("flushing subscriptions on shutdown"), null);
}
```

- `Task.Run` non ha una variante `Unsafe`: si sospende il flusso intorno alla chiamata. `ExecutionContext.SuppressFlow()` restituisce un `AsyncFlowControl`, e il `using` ripristina il flusso appena il task è partito.
- Per il token esiste `CancellationToken.UnsafeRegister`, che non cattura l'`ExecutionContext` e non lo fa fluire nel callback.

```text
    61 ms [t  7] [tenant=acme] request: served quotes
   123 ms [t  7] [tenant=initech] request: served quotes
   123 ms [t  8] [tenant=globex] request: served quotes
   153 ms [t  8] [tenant=-] background: refreshing price cache
   309 ms [t  8] [tenant=-] background: refreshing price cache
   466 ms [t  8] [tenant=-] background: refreshing price cache
   529 ms [t  7] application stopping
   529 ms [t  7] [tenant=-] background: flushing subscriptions on shutdown
  Background lines tagged 'acme'   0/4
```

Il loop parte senza il contesto della richiesta: se gliene serve uno suo, lo stabilisce esplicitamente.

Il messaggio: tutto ciò che vive più a lungo della richiesta (loop, timer, callback di shutdown, cache condivise) non deve nascere nel suo contesto.
Lo stesso problema, con una cache condivisa invece di un loop, è lo scenario 2.4 di [Demo 2](../02-AsyncCaching/README-it.md): la factory dell'`AsyncLazy` gira nel contesto del primo chiamante, e anche lì il fix è `SuppressFlow`.

## 4.3 `async-dispose` — lo shutdown che perde l'ultimo batch

[BatchingPublisher.cs](BatchingPublisher.cs) pubblica le quotazioni verso valle a blocchi di 10: un `Channel` in ingresso e un loop che accumula e fa flush.
Quello che resta nel buffer va scritto allo shutdown, e scrivere è I/O: il tipo implementa solo `IAsyncDisposable`.

```csharp
async Task LoopAsync()
{
    var batch = new List<Quote>(BatchSize);
    await foreach (var quote in input.Reader.ReadAllAsync())
    {
        batch.Add(quote);
        if (batch.Count == BatchSize)
            await FlushAsync(batch);
    }

    // Input completato: flush del resto.
    if (batch.Count > 0)
        await FlushAsync(batch);
}
```

In [AsyncDisposeDemo.cs](AsyncDisposeDemo.cs) il publisher è un servizio scoped. La richiesta pubblica 25 quotazioni e prosegue per altri 300 ms, durante i quali partono i due batch pieni; poi lo scope viene chiuso.
Gli ultimi 5 dipendono interamente dal dispose. La variante broken li perde in due modi.

**Dispose sincrono dello scope.**

```csharp
using var scope = services.CreateScope();
publisher = scope.ServiceProvider.GetRequiredService<BatchingPublisher>();
await HandleRequestAsync(publisher);
```

```text
── Sync dispose of the DI scope
    19 ms [t  8] request: 25 quotes published
    77 ms [t  8] publisher: flushed a batch of 10
   139 ms [t  7] publisher: flushed a batch of 10
   325 ms [t  7] request: done, disposing the scope
✘ 'BeyondAsyncAwait.Demo04.BatchingPublisher' type only implements IAsyncDisposable. Use DisposeAsync to dispose the container.
  Published at shutdown            20/25
```

Il `Dispose()` dello scope trova un servizio che implementa solo `IAsyncDisposable` e solleva `InvalidOperationException`.
Il `DisposeAsync` del publisher non parte mai: nell'output non c'è né il batch da 5 né la riga `publisher: disposed`, e gli ultimi 5 restano nel buffer.
È il rischio che la documentazione segnala per i tipi solo `IAsyncDisposable`: se il consumer chiama solo `Dispose`, `DisposeAsync` non viene mai chiamato.

**Un `DisposeAsync` che non aspetta il flush.** Lo scope ora è async, ma il publisher chiude l'input e torna subito:

```csharp
input.Writer.TryComplete();
if (!waitForFlush)
    return;
```

```text
── DisposeAsync that doesn't wait for the flush
   331 ms [t  7] request: 25 quotes published
   388 ms [t  7] publisher: flushed a batch of 10
   449 ms [t  7] publisher: flushed a batch of 10
   636 ms [t  7] request: done, disposing the scope
  Published at shutdown            20/25
   700 ms [t  7] publisher: flushed a batch of 5
! 25/25 only after DisposeAsync returned: in a real shutdown the process is already gone
```

`TryComplete` chiude l'input, il loop esce dal `foreach` e avvia l'ultimo flush, ma `DisposeAsync` è già tornato.
Scope, container e chi gestisce lo shutdown considerano il publisher chiuso mentre l'ultimo batch è ancora in volo: arriva a 700 ms, dopo che il chiamante ha letto 20/25.
La demo aspetta 150 ms apposta per mostrarlo; un processo in chiusura non aspetta.

**Fixed.** Scope async per tutta la catena, e un `DisposeAsync` che torna solo a lavoro finito.

```csharp
await using (var services = BuildServices(waitForFlush: true))
{
    await using var scope = services.CreateAsyncScope();
    publisher = scope.ServiceProvider.GetRequiredService<BatchingPublisher>();
    await HandleRequestAsync(publisher);
}

Out.Metric("Published at shutdown", $"{publisher.Published}/{Quotes}", publisher.Published == Quotes);
await publisher.DisposeAsync(); // un secondo dispose deve essere innocuo
```

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

```text
     0 ms [t  7] request: 25 quotes published
    61 ms [t  8] publisher: flushed a batch of 10
   123 ms [t  8] publisher: flushed a batch of 10
   310 ms [t  8] request: done, disposing the scope
   372 ms [t  8] publisher: flushed a batch of 5
   372 ms [t  8] publisher: disposed, buffer flushed
  Published at shutdown            25/25
   373 ms [t  8] publisher: already disposed, nothing to do
```

- `CreateAsyncScope` con `await using`: lo scope viene disposto con `DisposeAsync`, che arriva fino al publisher. Anche il `ServiceProvider` è in un `await using`.
- `DisposeAsync` completa l'input e poi **aspetta il loop**: l'ultimo flush e `disposed, buffer flushed` cadono nello stesso millisecondo, prima che il chiamante legga 25/25.
- `ConfigureAwaitOptions.SuppressThrowing`: un flush fallito non esce da `DisposeAsync` come eccezione; il codice controlla `loop.IsFaulted` e lo logga.
- Idempotente: il secondo `DisposeAsync` trova il flag già impostato da `Interlocked.Exchange` e non fa nulla.

Il messaggio: una risorsa che si chiude in modo async vuole proprietari async per tutta la catena (`await using`, `CreateAsyncScope`, il container disposto con `DisposeAsync`) e un `DisposeAsync` che torni solo quando il lavoro che chiude è davvero finito.
Implementare solo `IAsyncDisposable` è legittimo quando una chiusura sincrona non è possibile, e qui ha anche un effetto utile: il dispose sincrono il batch lo perde comunque, ma con un'eccezione, non in silenzio.

## Cosa sapere

- **`AsyncLocal` scende, non risale.** Un metodo async che deve produrre contesto lo restituisce. Il contesto si imposta al punto d'ingresso del flusso, in codice sincrono o prima di chiamare il metodo async.
- **Un holder mutabile** si imposta una volta, all'inizio del flusso. Ogni copia del contesto vede la stessa istanza, compreso il lavoro avviato dalla richiesta.
- **Catturano il contesto** `Task.Run`, `ThreadPool.QueueUserWorkItem`, `Timer`, `CancellationToken.Register`, e ogni `await` lo fa fluire alla continuation. Per il lavoro che sopravvive alla richiesta: `ExecutionContext.SuppressFlow()` intorno all'avvio, oppure le varianti `Unsafe*` (`CancellationToken.UnsafeRegister`, `ThreadPool.UnsafeQueueUserWorkItem`).
- **`ConfigureAwait(false)` non c'entra**: riguarda il `SynchronizationContext`, non l'`ExecutionContext`. Non esiste un modo, a livello di `await`, per non far fluire l'`ExecutionContext`.
- **Un tipo solo `IAsyncDisposable`** richiede che tutti i suoi proprietari siano async: `await using`, `CreateAsyncScope()`, il `ServiceProvider` disposto con `DisposeAsync`.
- **`DisposeAsync`** deve essere idempotente, aspettare il lavoro che chiude e non sollevare eccezioni per un flush fallito.
- **Runtime Async non cambia nulla di tutto questo**: i tre scenari danno lo stesso output con `-p:RuntimeAsync=false`.

## Riferimenti

- [Stephen Toub — ExecutionContext vs SynchronizationContext](https://devblogs.microsoft.com/dotnet/executioncontext-vs-synchronizationcontext/)
- [Stephen Toub — How Async/Await Really Works in C#](https://devblogs.microsoft.com/dotnet/how-async-await-really-works/) (il salvataggio e ripristino dell'`ExecutionContext` in `Start`)
- [`AsyncLocal<T>` Class](https://learn.microsoft.com/dotnet/api/system.threading.asynclocal-1)
- [Implement a DisposeAsync method](https://learn.microsoft.com/dotnet/standard/garbage-collection/implementing-disposeasync)
