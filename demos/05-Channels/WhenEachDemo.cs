using System.Threading.Channels;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Act05;

/// <summary>
/// 5.3 — Two .NET 9 tools for "many results, in the right order".
/// Task.WhenEach: an IAsyncEnumerable of tasks in completion order — show each quote as soon as it arrives
/// instead of waiting for the slowest one (WhenAll).
/// Channel.CreateUnboundedPrioritized: a channel that is not FIFO — urgent orders jump the queue.
/// </summary>
sealed class WhenEachDemo : Demo
{
    static readonly (string Symbol, int LatencyMs)[] Sources =
        [("MSFT", 300), ("AAPL", 100), ("NVDA", 500), ("AMZN", 200), ("GOOG", 50)];

    public override string Id => "5.3";
    public override string Name => "when-each";
    public override string Title => "Task.WhenEach and prioritized channels";
    public override bool HasVariants => false;

    public override async Task RunAsync(Variant variant)
    {
        Out.Section("Task.WhenAll — every quote appears when the slowest one arrives");
        Out.ResetClock();
        var quotes = await Task.WhenAll(StartQuotes());
        foreach (var quote in quotes)
            Out.Log($"{quote.Symbol} {quote.Price}");

        Out.Section("Task.WhenEach — each quote as soon as it is ready (completion order)");
        Out.ResetClock();
        await foreach (var task in Task.WhenEach(StartQuotes()))
        {
            var quote = await task;
            Out.Log($"{quote.Symbol} {quote.Price}");
        }

        Out.Section("Channel.CreateUnboundedPrioritized — urgent orders jump the queue");
        await PrioritizedAsync();
    }

    static Task<Quote>[] StartQuotes() => Sources
        .Select(s => new FakeQuoteProvider { Latency = TimeSpan.FromMilliseconds(s.LatencyMs) }.GetQuoteAsync(s.Symbol))
        .ToArray();

    static async Task PrioritizedAsync()
    {
        // The comparer decides the order: the smallest item is read first.
        var channel = Channel.CreateUnboundedPrioritized(new UnboundedPrioritizedChannelOptions<Order>
        {
            Comparer = Comparer<Order>.Create((a, b) => a.Priority != b.Priority
                ? a.Priority.CompareTo(b.Priority)
                : a.Id.CompareTo(b.Id))
        });

        Out.ResetClock();
        var consumer = Task.Run(async () =>
        {
            await foreach (var order in channel.Reader.ReadAllAsync())
            {
                Out.Log($"processing order #{order.Id} ({order.Priority})");
                await Task.Delay(60);
            }
        });

        for (var id = 1; id <= 6; id++)
            channel.Writer.TryWrite(new Order(id, Priority.Normal));
        Out.Log("6 normal orders queued");

        await Task.Delay(150);
        channel.Writer.TryWrite(new Order(7, Priority.Urgent));
        channel.Writer.TryWrite(new Order(8, Priority.Urgent));
        Out.Log("2 urgent orders queued");

        channel.Writer.Complete();
        await consumer;
    }

    enum Priority { Urgent, Normal }

    sealed record Order(int Id, Priority Priority);
}
