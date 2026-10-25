using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Act05;

/// <summary>
/// 5.2 — source → parse → enrich (provider lookup) → persist, 4 workers per stage, bounded channels in between.
/// Broken: every worker completes the stage output when IT finishes, and workers are fire-and-forget:
/// items get lost, a poison message vanishes, a provider outage hangs the whole pipeline.
/// Fixed: a stage completes its output only when ALL its workers are done; item errors go to a dead-letter queue;
/// a stage failure cancels the pipeline and travels downstream with Complete(ex) up to the caller.
/// </summary>
sealed class PipelineDemo : Demo
{
    const int Messages = 160;
    const int PoisonId = 100;
    const int Workers = 4;
    const int ChannelCapacity = 20;

    public override string Id => "5.2";
    public override string Name => "pipeline";
    public override string Title => "Multi-stage pipeline: completion and errors";

    public override async Task RunAsync(Variant variant)
    {
        Out.Dim($"{Messages} messages, poison message #{PoisonId}, {Workers} workers per stage, Bounded({ChannelCapacity}) between stages");
        Out.Dim("Stage channels: SingleReader/SingleWriter = false (4 workers each side),");
        Out.Dim("AllowSynchronousContinuations = false (the default: same trap as RunContinuationsAsynchronously in Act 1).");

        Out.Section("Scenario 1: a poison message, then a normal end of input");
        await RunScenarioAsync(variant, providerDown: false);

        Out.Section("Scenario 2: the quote provider goes down");
        await RunScenarioAsync(variant, providerDown: true);
    }

    static async Task RunScenarioAsync(Variant variant, bool providerDown)
    {
        var provider = new FakeQuoteProvider { Latency = TimeSpan.FromMilliseconds(20) };
        if (providerDown)
            provider.FailNext(int.MaxValue);

        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            Interlocked.Add(ref unobserved, e.Exception.InnerExceptions.Count);
            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += onUnobserved;

        var context = new PipelineContext();
        var raw = Bounded<string>();
        var trades = Bounded<Trade>();
        var priced = Bounded<PricedTrade>();
        var receipts = Bounded<Receipt>();

        // Source: a single writer that completes its channel when the input ends.
        var source = Task.Run(async () =>
        {
            try
            {
                for (var id = 1; id <= Messages; id++)
                {
                    await raw.Writer.WriteAsync(RawMessage(id), context.Token);
                    context.OnProduced();
                }

                raw.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                raw.Writer.TryComplete(ex);
            }
        });

        Func<string, CancellationToken, Task<Trade>> parse = (line, _) => Task.FromResult(Parse(line));
        Func<Trade, CancellationToken, Task<PricedTrade>> enrich = async (trade, ct) =>
        {
            var quote = await provider.GetQuoteAsync(trade.Symbol, ct);
            return new PricedTrade(trade, quote.Price);
        };
        Func<PricedTrade, CancellationToken, Task<Receipt>> persist = async (trade, ct) =>
        {
            await Task.Delay(5, ct);   // the database insert
            context.Database[trade.Trade.Id] = trade;
            return new Receipt(trade.Trade.Id);
        };

        Task[] stages;
        if (variant == Variant.Broken)
        {
            StartStageBroken(raw.Reader, trades.Writer, parse);
            StartStageBroken(trades.Reader, priced.Writer, enrich);
            StartStageBroken(priced.Reader, receipts.Writer, persist);
            stages = [source];
        }
        else
        {
            stages =
            [
                source,
                RunStageAsync("parse", raw.Reader, trades.Writer, parse, context),
                RunStageAsync("enrich", trades.Reader, priced.Writer, enrich, context),
                RunStageAsync("persist", priced.Reader, receipts.Writer, persist, context)
            ];
        }

        // The caller: reads the receipts until the last channel completes.
        var stopwatch = Stopwatch.StartNew();
        var confirmed = 0;
        var consume = Task.Run(async () =>
        {
            await foreach (var _ in receipts.Reader.ReadAllAsync())
                confirmed++;
        });

        try
        {
            await consume.WaitAsync(TimeSpan.FromSeconds(3));
            Out.Good($"Caller: pipeline completed in {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (TimeoutException)
        {
            Out.Bad($"Caller: no completion after {stopwatch.ElapsedMilliseconds} ms — the pipeline hangs (and its blocked tasks leak)");
        }
        catch (Exception ex)
        {
            Out.Good($"Caller: pipeline failed in {stopwatch.ElapsedMilliseconds} ms with {ex.GetType().Name}: {ex.Message}");
            if (context.FirstError is { } first)
                Out.Info($"  root cause, from stage '{context.FailedStage}': {first.GetType().Name}: {first.Message}");
        }

        if (variant == Variant.Fixed)
        {
            // Graceful shutdown: every stage task has ended, nothing is left running.
            await Task.WhenAll(stages).ContinueWith(_ => { }, TaskScheduler.Default);
            Out.Metric("Stage tasks still running", stages.Count(s => !s.IsCompleted), stages.All(s => s.IsCompleted));
        }

        // Faulted tasks nobody awaited are only discovered when the GC finalizes them (give stragglers a moment first).
        await Task.Delay(200);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        TaskScheduler.UnobservedTaskException -= onUnobserved;

        var accounted = context.Database.Count + context.DeadLetters.Count;
        Out.Metric("Produced", context.Produced);
        Out.Metric("Persisted", context.Database.Count, providerDown ? null : context.Database.Count == Messages - 1);
        Out.Metric("Dead-lettered", context.DeadLetters.Count == 0 ? "0" : $"{context.DeadLetters.Count} ({string.Join(", ", context.DeadLetters)})",
            providerDown ? null : context.DeadLetters.Count == 1);
        if (!providerDown)
            Out.Metric("Unaccounted for (lost)", context.Produced - accounted, context.Produced == accounted);
        Out.Metric("Exceptions nobody observed", unobserved, unobserved == 0);
    }

    // Broken: looks reasonable in a code review.
    static void StartStageBroken<TIn, TOut>(ChannelReader<TIn> input, ChannelWriter<TOut> output, Func<TIn, CancellationToken, Task<TOut>> transform)
    {
        for (var i = 0; i < Workers; i++)
        {
            _ = Task.Run(async () =>                         // fire-and-forget: a failing worker dies in silence
            {
                await foreach (var item in input.ReadAllAsync())
                    await output.WriteAsync(await transform(item, CancellationToken.None));

                output.Complete();                           // the FIRST worker to finish closes the channel for all the others
            });
        }
    }

    // Fixed: the stage owns its workers and completes its output exactly once.
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
                        context.DeadLetters.Enqueue(ex.MessageId);   // item error: park it and keep going
                        continue;
                    }

                    await output.WriteAsync(result, context.Token);
                }
            }
            catch (Exception ex)
            {
                context.Fail(name, ex);   // stage error: cancel everything, upstream would block on a full channel forever
                throw;
            }
        })).ToArray();

        try
        {
            await Task.WhenAll(workers);
            output.TryComplete();                                     // only when ALL workers are done
        }
        catch (Exception ex)
        {
            output.TryComplete(context.FirstError ?? ex);             // downstream sees the root cause when it reads
        }
    }

    static Channel<T> Bounded<T>() => Channel.CreateBounded<T>(new BoundedChannelOptions(ChannelCapacity)
    {
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    static string RawMessage(int id) => id == PoisonId
        ? $"{id}|MSFT|ten"
        : $"{id}|{(id % 3) switch { 0 => "MSFT", 1 => "AAPL", _ => "NVDA" }}|{id % 50 + 1}";

    static Trade Parse(string line)
    {
        var parts = line.Split('|');
        var id = int.Parse(parts[0]);
        return int.TryParse(parts[2], out var quantity)
            ? new Trade(id, parts[1], quantity)
            : throw new PoisonMessageException(id, $"Invalid quantity '{parts[2]}' in message #{id}");
    }

    sealed record Trade(int Id, string Symbol, int Quantity);

    sealed record PricedTrade(Trade Trade, decimal Price);

    sealed record Receipt(int TradeId);

    sealed class PoisonMessageException(int messageId, string message) : FormatException(message)
    {
        public int MessageId { get; } = messageId;
    }

    sealed class PipelineContext
    {
        readonly CancellationTokenSource _cts = new();
        int _produced;
        Exception? _firstError;

        public CancellationToken Token => _cts.Token;
        public int Produced => Volatile.Read(ref _produced);
        public ConcurrentDictionary<int, PricedTrade> Database { get; } = new();
        public ConcurrentQueue<int> DeadLetters { get; } = new();
        public Exception? FirstError => Volatile.Read(ref _firstError);
        public string? FailedStage { get; private set; }

        public void OnProduced() => Interlocked.Increment(ref _produced);

        public void Fail(string stage, Exception error)
        {
            if (error is OperationCanceledException)
                return;

            if (Interlocked.CompareExchange(ref _firstError, error, null) is null)
            {
                FailedStage = stage;
                _cts.Cancel();
            }
        }
    }
}
