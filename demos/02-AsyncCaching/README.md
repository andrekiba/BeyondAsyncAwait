# Demo 2 — Un valore, molti consumatori: AsyncLazy e async caching

QuoteHub chiede quotazioni a provider esterni lenti e instabili, e mette in cache i dati di riferimento.
Quando una chiave è fredda e arrivano molte richieste insieme, tutte vogliono lo stesso valore nello stesso momento: un valore, molti consumatori.

"Metti in cache il `Task`, non il valore" è la risposta nota, e risolve solo il primo problema.
Un `Task` in cache è una promessa condivisa: porta a tutti i chiamanti il suo esito, anche quando è un errore o una cancellazione, e la sua origine, cioè il token e il contesto di chi l'ha avviato.
Sono trappole che non si vedono con un chiamante alla volta e un provider che risponde sempre: escono sotto carico, quando il provider ha un errore transitorio o un client si disconnette.

La Demo parte dallo stampede (2.1) e arriva a `Lazy<Task<T>>`.
Poi ne mostra le tre trappole (2.2–2.4), che [AsyncLazy.cs](AsyncLazy.cs) risolve una per una in una cinquantina di righe.
L'ultimo scenario (2.5) fa le stesse domande a `HybridCache`, che ha la protezione dallo stampede già dentro.

## Come si lancia

```bash
dotnet run --project demos/02-AsyncCaching -- 2.1                          # broken, Invio, fixed
dotnet run --project demos/02-AsyncCaching -- shared-cancellation --fixed  # solo fixed, per nome
dotnet run --project demos/02-AsyncCaching -- all --no-pause               # tutti gli scenari
```

Da IDE ci sono i profili `menu`, `2.1 cache-stampede`, `2.2 failure-caching`, `2.3 shared-cancellation`, `2.4 factory-context` e `2.5 hybrid-cache`.
Lo scenario 2.5 non ha varianti broken/fixed: gira una volta sola.

Il provider è il [FakeQuoteProvider](../../src/Demo.Shared/FakeQuoteProvider.cs) condiviso da tutte le Demo: 200 ms di latenza, `FailNext(n)` per far fallire le prossime `n` chiamate, contatori di chiamate, cancellazioni e fallimenti che ogni scenario stampa alla fine.
I prezzi sono deterministici (un hash stabile del simbolo più un `Random` con seed fisso): per questo MSFT vale 281.34 in tutti gli output qui sotto.
Due helper completano la scena: [Concurrently.cs](Concurrently.cs) fa partire N chiamate nello stesso istante sul thread pool, come una raffica di richieste HTTP su una chiave fredda; [Caller.cs](Caller.cs) è un client con il suo `CancellationTokenSource`, che può disconnettersi dopo un ritardo e stampa `cancelled — but it never asked to!` se viene cancellato senza averlo chiesto.

Gli output qui sotto vengono da una run in Debug su .NET 11.0.0: millisecondi e thread id cambiano a ogni run.

## ★ 2.1 `cache-stampede` — 200 richieste, una chiave fredda

In [CacheStampedeDemo.cs](CacheStampedeDemo.cs) 200 richieste chiedono insieme `MSFT`, che non è in cache.
Il loader simula il prefisso sincrono che ogni loader reale ha prima di arrivare all'I/O:

```csharp
// Un loader reale ha un prefisso sincrono (costruire la richiesta, prendere una connessione dal pool...):
// è la finestra in cui i chiamanti concorrenti vedono tutti un miss.
static Task<Quote> LoadAsync(FakeQuoteProvider provider, string symbol)
{
    Thread.Sleep(5);
    return provider.GetQuoteAsync(symbol);
}
```

La variante broken prova due cache sbagliate in sequenza.
**A** mette in cache il valore: controlla, awaita, salva.

```csharp
if (cache.TryGetValue(symbol, out var cached))
    return cached;

var quote = await LoadAsync(provider, symbol);
cache[symbol] = quote;
return quote;
```

**B** mette in cache il `Task`, con `ConcurrentDictionary.GetOrAdd`:

```csharp
var tasks = new ConcurrentDictionary<string, Task<Quote>>();
await Concurrently.RunAsync(Requests, () => tasks.GetOrAdd("MSFT", symbol => LoadAsync(provider, symbol)));
```

```text
── A. Cache the value: check, await, store
  Provider calls                   200

── B. Cache the Task with ConcurrentDictionary.GetOrAdd
  Provider calls                   16
```

In A il valore entra in cache solo quando la prima chiamata completa, 200 ms dopo: fino ad allora ogni richiesta vede un miss e chiama il provider.

B è meglio, ma non basta.
`GetOrAdd` chiama la factory fuori dai lock del dizionario, e con chiamate concorrenti può chiamarla più volte: nel dizionario entra un solo valore, gli altri vengono scartati.
Con un `Task` scartare non basta, perché crearlo vuol dire avviare la chiamata: le 15 chiamate in più sono partite davvero, e il loro risultato non lo legge nessuno.
Il 16 dipende dalla macchina: è quanti thread del pool entrano nel prefisso sincrono di `LoadAsync` prima che il primo `Task` venga pubblicato.

**Fixed.** In cache va un `Lazy<Task<T>>`:

```csharp
var lazies = new ConcurrentDictionary<string, Lazy<Task<Quote>>>();
var created = 0;
await Concurrently.RunAsync(Requests, () => lazies.GetOrAdd("MSFT", symbol =>
{
    Interlocked.Increment(ref created);
    return new Lazy<Task<Quote>>(() => LoadAsync(provider, symbol));
}).Value);
```

```text
── Cache Lazy<Task<T>>: GetOrAdd may build several Lazy, only the published one runs
  Lazy instances created           16
  Provider calls                   1
```

La factory di `GetOrAdd` gira ancora 16 volte, ma adesso costruisce un `Lazy`, che non avvia nulla.
Tutti ricevono il `Lazy` pubblicato, e il suo `Value` esegue il loader una volta sola: il costruttore `Lazy<T>(Func<T>)` usa `LazyThreadSafetyMode.ExecutionAndPublication`, cioè un solo thread esegue la factory e gli altri ne ricevono il risultato.

Il messaggio: in cache va la promessa, non il valore, e la promessa va creata in modo che crearla non avvii il lavoro. Un `Task` è già partito, un `Lazy<Task<T>>` no.

## Da `Lazy<Task<T>>` ad `AsyncLazy<T>`

Il fix di 2.1 è il punto di partenza.
`Lazy<Task<T>>` garantisce una sola chiamata, ma il `Task` che conserva nasce dal primo chiamante e ne conserva l'esito:

| Scenario | Il problema con `Lazy<Task<T>>` | Cosa fa `AsyncLazy<T>` |
|---|---|---|
| ★ 2.2 | un fallimento resta in cache per sempre | se il load fallisce, mette al suo posto un `Lazy` nuovo |
| ★ 2.3 | il token del primo chiamante cancella il load di tutti, e la cancellazione resta in cache | il load non riceve token; ogni chiamante cancella solo la propria attesa con `WaitAsync(ct)` |
| 2.4 | il load condiviso gira nel contesto (`AsyncLocal`) del primo chiamante | avvia il load con `Task.Run`, senza far fluire l'`ExecutionContext` |

Nei tre scenari la variante fixed usa la stessa classe: ogni scenario mostra il pezzo che risolve la sua trappola, e il codice completo è nella sezione dopo lo scenario 2.4.

## ★ 2.2 `failure-caching` — il fallimento resta in cache

In [FailureCachingDemo.cs](FailureCachingDemo.cs) il provider fallisce una volta e poi si riprende.
Cinque richieste in sequenza, con una pausa di 50 ms tra una e l'altra, chiedono lo stesso valore.

```csharp
var provider = new FakeQuoteProvider { Latency = TimeSpan.FromMilliseconds(100) };
provider.FailNext(1);
// …
var lazy = new Lazy<Task<Quote>>(() => provider.GetQuoteAsync("MSFT"));
getQuote = () => lazy.Value;
```

```text
✘ request 1: Provider failed for MSFT (call #1)
✘ request 2: Provider failed for MSFT (call #1)
✘ request 3: Provider failed for MSFT (call #1)
✘ request 4: Provider failed for MSFT (call #1)
✘ request 5: Provider failed for MSFT (call #1)

── Results
  Failed requests                  5/5
  Provider calls                   1
  Provider failures                1
```

La factory del `Lazy` non ha sollevato eccezioni: ha restituito un `Task`, che 100 ms dopo è fallito.
Per il `Lazy` è un valore come un altro, e resta lì finché vive il `Lazy`.
Il provider si è ripreso dopo la prima chiamata, ma la cache non se ne accorge: tutte e cinque le richieste ricevono l'errore della chiamata #1, e il provider non viene più chiamato.

Nemmeno `LazyThreadSafetyMode.PublicationOnly`, che non mette in cache le eccezioni, servirebbe: qui la factory non solleva eccezioni.
E `PublicationOnly` lascia girare la factory su più thread insieme, cioè riporta lo stampede di 2.1.

**Fixed.** In `AsyncLazy`, se il load fallisce, il `catch` sostituisce il `Lazy` prima di sollevare di nuovo l'eccezione:

```csharp
async Task<T> LoadAsync(Lazy<Task<T>> instance)
{
    try
    {
        return await factory();
    }
    catch
    {
        // Non mettere in cache il fallimento: metti al suo posto un Lazy nuovo, se nessuno l'ha già fatto.
        Interlocked.CompareExchange(ref current, CreateInstance(), instance);
        throw;
    }
}
```

```text
✘ request 1: Provider failed for MSFT (call #1)
✔ request 2: MSFT = 281.34
✔ request 3: MSFT = 281.34
…
── Results
  Failed requests                  1/5
  Provider calls                   2
  Provider failures                1
```

Chi stava aspettando il load fallito riceve l'errore: la richiesta 1 fallisce comunque, perché `AsyncLazy` non fa retry.
La richiesta 2 trova un `Lazy` nuovo e fa partire la seconda chiamata, che stavolta risponde; le richieste 3–5 trovano il valore in cache.
Il `CompareExchange` sostituisce il `Lazy` solo se `current` è ancora quello che ha fallito: un load vecchio non butta via quello nuovo.

Il messaggio: un `Task` in cache mette in cache il suo esito, qualunque sia. Decidi quali esiti tenere, di solito solo il successo. Il retry è un'altra decisione, da prendere sopra la cache.

## ★ 2.3 `shared-cancellation` — il token del primo chiamante cancella tutti

In [SharedCancellationDemo.cs](SharedCancellationDemo.cs) tre client chiedono MSFT insieme: `web-1` si disconnette dopo 50 ms, `web-2` e `web-3` aspettano.
Poi arriva `web-4`, per la stessa chiave.

```csharp
var cache = new ConcurrentDictionary<string, Lazy<Task<Quote>>>();
getQuote = ct => cache.GetOrAdd("MSFT",
    symbol => new Lazy<Task<Quote>>(() => provider.GetQuoteAsync(symbol, ct))).Value;
```

La lambda cattura il `ct` di chi crea il `Lazy`, cioè di `web-1`, che arriva per primo.

```text
     1 ms [t 19] provider → MSFT (call #1)
    63 ms [t 19] provider ⊘ MSFT (call #1) cancelled
✘ web-2: cancelled — but it never asked to!
✘ web-3: cancelled — but it never asked to!
! web-1: cancelled (client disconnected)

── A minute later, a new request for the same key
✘ web-4: cancelled — but it never asked to!

── Results
  Patient callers served           0/2
  Later request served             False
  Provider calls / cancelled       1 / 1
```

Quando `web-1` si disconnette, il suo token cancella la chiamata al provider e il `Task` condiviso finisce `Canceled`.
`web-2` e `web-3` awaitavano quel `Task`: ricevono una `OperationCanceledException` senza averla chiesta.
E il `Task` cancellato resta in cache: `web-4`, che arriva dopo con un token mai cancellato, viene cancellato subito, senza nessuna chiamata al provider.

**Fixed.** La factory non riceve il token di nessuno, e ogni chiamante passa il suo a `GetValueAsync`:

```csharp
var cache = new ConcurrentDictionary<string, AsyncLazy<Quote>>();
getQuote = ct => cache.GetOrAdd("MSFT",
    symbol => new AsyncLazy<Quote>(() => provider.GetQuoteAsync(symbol))).GetValueAsync(ct);
```

In `AsyncLazy` il token del chiamante arriva solo a `WaitAsync`:

```csharp
public Task<T> GetValueAsync(CancellationToken cancellationToken = default) =>
    Volatile.Read(ref current).Value.WaitAsync(cancellationToken);
```

```text
     0 ms [t 17] provider → MSFT (call #1)
! web-1: cancelled (client disconnected)
   200 ms [t 19] provider ← MSFT = 281.34 (call #1)
✔ web-2: MSFT = 281.34
✔ web-3: MSFT = 281.34

── A minute later, a new request for the same key
✔ web-4: MSFT = 281.34

── Results
  Patient callers served           2/2
  Later request served             True
  Provider calls / cancelled       1 / 0
```

`WaitAsync(ct)` restituisce un `Task` che completa quando completa il `Task` condiviso, oppure quando il token viene cancellato: cancellarlo interrompe l'attesa di quel chiamante, non il load.
`web-1` smette di aspettare, il load prosegue per gli altri e completa a 200 ms, `web-4` trova il valore in cache: una sola chiamata, nessuna cancellata.
Vale ancora la lezione di 2.1: `GetOrAdd` può costruire più `AsyncLazy`, ma il costruttore non avvia nulla.

Il messaggio: nel lavoro condiviso la cancellazione va divisa in due. Il token del chiamante cancella la sua attesa; il lavoro ha un ciclo di vita suo, che non dipende da chi è arrivato per primo.
Il prezzo: se se ne vanno tutti i chiamanti, il load continua lo stesso. Nello scenario 2.5 `HybridCache` fa la scelta opposta.

## 2.4 `factory-context` — il load condiviso nel contesto del primo chiamante

In [FactoryContextDemo.cs](FactoryContextDemo.cs) due richieste, `req-A` e `req-B`, hanno ciascuna il proprio correlation id in un `AsyncLocal` e chiedono lo stesso valore.
Il loader condiviso logga il correlation id che vede quando parte e quando completa.

```csharp
static readonly AsyncLocal<string?> CorrelationId = new();
// …
var lazy = new Lazy<Task<Quote>>(() => LoadAsync(provider));
getQuote = () => lazy.Value;
// …
await Task.WhenAll(RequestAsync("req-A", getQuote), RequestAsync("req-B", getQuote));

static async Task RequestAsync(string correlationId, Func<Task<Quote>> getQuote)
{
    CorrelationId.Value = correlationId;
    Out.Log($"{correlationId}: request started");
    var quote = await getQuote();
    Out.Log($"{correlationId}: MSFT = {quote.Price}");
}
```

```text
     0 ms [t 19] req-A: request started
✘ shared load started — correlation id: req-A [t19]
     1 ms [t 19] req-B: request started
✘ shared load completed — correlation id: req-A [t19]
   203 ms [t 19] req-A: MSFT = 281.34
   203 ms [t 17] req-B: MSFT = 281.34
```

`Lazy` esegue la factory in modo sincrono, sul thread di chi chiama `Value` per primo e dentro il suo `ExecutionContext`.
`LoadAsync` parte quindi con il correlation id di `req-A`, e l'`await` al suo interno porta lo stesso contesto alla continuation.
Il load serve anche `req-B`, ma i suoi log, e qualsiasi altro `AsyncLocal` della prima richiesta (tenant, `Activity` per il tracing), raccontano che appartiene solo a `req-A`.

**Fixed.** `AsyncLazy` avvia il load sul thread pool con il flusso dell'`ExecutionContext` sospeso:

```csharp
Task<T> StartLoad(Lazy<Task<T>> instance)
{
    // Il load condiviso non appartiene a nessun chiamante: i valori AsyncLocal del primo chiamante non devono fluire qui dentro.
    using (ExecutionContext.SuppressFlow())
        return Task.Run(() => LoadAsync(instance));
}
```

```text
     0 ms [t 17] req-A: request started
     0 ms [t 17] req-B: request started
✔ shared load started — correlation id: (none), it belongs to no request [t17]
✔ shared load completed — correlation id: (none), it belongs to no request [t17]
   202 ms [t 17] req-A: MSFT = 281.34
   202 ms [t 17] req-B: MSFT = 281.34
```

Con il flusso sospeso `Task.Run` non cattura l'`ExecutionContext` del chiamante: il load parte con un contesto vuoto, e gli `await` al suo interno si portano dietro quello.
`SuppressFlow` restituisce un `AsyncFlowControl` che va chiuso sullo stesso thread che l'ha aperto: per questo nel blocco `using` c'è solo la chiamata sincrona a `Task.Run`, senza `await`.

`Task.Run` ha anche un secondo effetto: il prefisso sincrono della factory non gira più sul thread del primo chiamante, dentro `Value`.
Nell'output fixed `req-B` parte prima che il load cominci.

Il messaggio: chi esegue il lavoro condiviso ne decide il contesto. Il lavoro condiviso non appartiene a nessuna richiesta, quindi va avviato senza il contesto di chi l'ha innescato.
È la stessa regola del lavoro in background avviato durante una richiesta: vedi lo scenario 4.2 in [Demo 4](../04-AsyncContext).

## `AsyncLazy<T>` completo

Le tre correzioni insieme, da [AsyncLazy.cs](AsyncLazy.cs):

```csharp
public sealed class AsyncLazy<T>
{
    readonly Func<Task<T>> factory;
    Lazy<Task<T>> current;

    public AsyncLazy(Func<Task<T>> factory)
    {
        this.factory = factory;
        current = CreateInstance();
    }

    public Task<T> GetValueAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref current).Value.WaitAsync(cancellationToken);

    Lazy<Task<T>> CreateInstance()
    {
        Lazy<Task<T>>? instance = null;
        instance = new Lazy<Task<T>>(() => StartLoad(instance!));
        return instance;
    }

    Task<T> StartLoad(Lazy<Task<T>> instance)
    {
        // Il load condiviso non appartiene a nessun chiamante: i valori AsyncLocal del primo chiamante non devono fluire qui dentro.
        using (ExecutionContext.SuppressFlow())
            return Task.Run(() => LoadAsync(instance));
    }

    async Task<T> LoadAsync(Lazy<Task<T>> instance)
    {
        try
        {
            return await factory();
        }
        catch
        {
            // Non mettere in cache il fallimento: metti al suo posto un Lazy nuovo, se nessuno l'ha già fatto.
            Interlocked.CompareExchange(ref current, CreateInstance(), instance);
            throw;
        }
    }
}
```

- Dentro c'è ancora un `Lazy<Task<T>>`, quindi la garanzia di 2.1 resta: costruire un `AsyncLazy` non avvia nulla, e ogni `Lazy` esegue il load una volta sola.
- `CreateInstance` passa a ogni `Lazy` un riferimento a sé stesso: quando un load fallisce, il `catch` sa quale `Lazy` togliere.
- Il `catch` senza filtro tratta allo stesso modo errori e cancellazioni: nessuno dei due resta in cache.
- Cosa non fa: niente scadenza né invalidazione, il valore resta finché vive l'istanza. E se tutti i chiamanti cancellano la loro attesa, il load prosegue e il risultato resta in cache.

## 2.5 `hybrid-cache` — le stesse domande a HybridCache

[HybridCacheDemo.cs](HybridCacheDemo.cs) usa `HybridCache` di `Microsoft.Extensions.Caching.Hybrid` (qui 11.0.0, vedi [Directory.Packages.props](../../Directory.Packages.props)), arrivato con .NET 9.
È registrato senza cache distribuita, quindi lavora solo in memoria.
Non ci sono varianti broken/fixed: lo scenario fa a `HybridCache` le domande che costruire `AsyncLazy` ha insegnato a fare.

```csharp
var services = new ServiceCollection();
services.AddHybridCache();
await using var serviceProvider = services.BuildServiceProvider();
var cache = serviceProvider.GetRequiredService<HybridCache>();
// …
Func<CancellationToken, Task<Quote>> Get(string symbol) => ct =>
    cache.GetOrCreateAsync($"quote:{symbol}", token => new ValueTask<Quote>(provider.GetQuoteAsync(symbol, token)),
        cancellationToken: ct).AsTask();
```

La factory riceve un token suo (`token`), diverso da quello del chiamante (`ct`).

```text
── 200 concurrent requests on a cold key
  Provider calls                   1

── One caller disconnects after 50 ms, two wait
   287 ms [t 15] provider → AAPL (call #1)
! web-1: cancelled (client disconnected)
   501 ms [t 13] provider ← AAPL = 192.28 (call #1)
✔ web-2: AAPL = 192.28
✔ web-3: AAPL = 192.28
  Patient callers served           2/2
  Provider calls / cancelled       1 / 0
  → one caller leaving cancels only its own wait, like AsyncLazy + WaitAsync

── Every caller disconnects (50 ms and 80 ms)
   503 ms [t 15] provider → GOOG (call #1)
! web-1: cancelled (client disconnected)
! web-2: cancelled (client disconnected)
   595 ms [t 15] provider ⊘ GOOG (call #1) cancelled
  Provider calls / cancelled       1 / 1
  → when the last caller leaves, HybridCache cancels the shared load (AsyncLazy lets it finish)

── A new request for the same key
   861 ms [t 12] provider → GOOG (call #2)
  1063 ms [t 12] provider ← GOOG = 656.25 (call #2)
✔ web-3: GOOG = 656.25
  Provider calls / cancelled       2 / 1
  → and the cancelled load was not cached: a new call
```

- **Stampede**: 200 richieste su una chiave fredda, 1 chiamata al provider. È il risultato di 2.1 fixed, senza scrivere nulla.
- **Un chiamante se ne va**: come `AsyncLazy` con `WaitAsync`. `web-1` smette di aspettare, il load prosegue per `web-2` e `web-3`, nessuna chiamata cancellata.
- **Se ne vanno tutti**: qui `HybridCache` fa diversamente da `AsyncLazy` e cancella il load condiviso. Non quando se ne va il primo chiamante (50 ms), ma dopo l'ultimo (80 ms): la chiamata viene cancellata a 595 ms, 92 ms dopo essere partita. La documentazione lo dice così: il token passato alla factory viene cancellato quando tutti i chiamanti in attesa dell'operazione condivisa hanno cancellato.
- **Una richiesta dopo**: la cancellazione non è rimasta in cache, `web-3` fa partire una chiamata nuova (#2). È la trappola di 2.3, evitata.

Le due scelte sul caso "se ne vanno tutti" sono entrambe ragionevoli: `AsyncLazy` non butta mai una chiamata già partita e scalda la cache per chi arriva dopo, `HybridCache` risparmia il lavoro che nessuno aspetta più.

Il messaggio: costruire `AsyncLazy` a mano serve a sapere quali domande fare a una libreria. Quante volte gira la factory? Cosa resta in cache se fallisce o viene cancellata? Cosa cancella il token del chiamante?

## Cosa sapere

- **In cache il `Task`, creato con `Lazy`.** `GetOrAdd` chiama la factory fuori dai lock e, con chiamate concorrenti, anche più volte: se la factory avvia lavoro, lo avvia più volte. Con `Lazy<Task<T>>` creare il valore non costa nulla, e parte solo il `Lazy` pubblicato.
- **Decidi quali esiti tenere.** Per qualsiasi cache di `Task` un `Task` fallito o cancellato è un valore valido come gli altri. Di solito va tenuto solo il successo.
- **Il lavoro condiviso non appartiene a nessun chiamante**: niente token né `ExecutionContext` del primo che passa. Ogni chiamante cancella la propria attesa con `WaitAsync(ct)`, disponibile da .NET 6.
- **`HybridCache` per i dati con una chiave e una scadenza**, come i dati di riferimento di QuoteHub: protezione dallo stampede, scadenza, cache distribuita opzionale (Redis, SQL Server…), invalidazione per tag. `AsyncLazy<T>` resta utile per un valore unico che vive quanto il suo proprietario e non scade.
- **La protezione dallo stampede di `HybridCache` vale per istanza**: non si estende alle altre istanze, nemmeno se condividono la stessa cache distribuita. Con più server dietro un load balancer, una chiave fredda può costare una chiamata per server.
- **Istanze condivise.** `AsyncLazy<T>` dà a tutti i chiamanti lo stesso oggetto, che quindi deve essere immutabile o thread-safe. `HybridCache` per default si comporta come `IDistributedCache` e non riusa le istanze; per riusarle il tipo deve essere `sealed` e marcato `[ImmutableObject(true)]`.

## Riferimenti

- [HybridCache library in ASP.NET Core](https://learn.microsoft.com/aspnet/core/performance/caching/hybrid)
- [ConcurrentDictionary.GetOrAdd — la factory e la concorrenza](https://learn.microsoft.com/dotnet/api/system.collections.concurrent.concurrentdictionary-2.getoradd)
- [Stephen Toub — `AsyncLazy<T>`](https://devblogs.microsoft.com/pfxteam/asynclazyt/)
- [Stephen Toub — ExecutionContext vs SynchronizationContext](https://devblogs.microsoft.com/dotnet/executioncontext-vs-synchronizationcontext/)
