using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo03;

/// <summary>
/// 3.2 — A pooled IValueTaskSource (the Socket pattern) makes an async operation that really suspends
/// allocation-free. The price: a ValueTask is a ticket for ONE await. After it, the operation object is back
/// in the pool. ManualResetValueTaskSourceCore detects the stale ticket through the token (version); a naive
/// source hands you another operation's result. That's why CA2012 exists — and Preserve()/AsTask() when you
/// really need to await twice or combine.
/// </summary>
sealed class ValueTaskSourceDemo : Demo
{
    const int Operations = 100_000;

    public override string Id => "3.2";
    public override string Name => "value-task-source";
    public override string Title => "Pooled IValueTaskSource: zero allocations, one await";

    public override async Task RunAsync(Variant variant)
    {
        Out.Info(BuildInfo.Description);

        if (variant == Variant.Broken)
        {
            Out.Section("A TaskCompletionSource per operation");
            var tcsFeed = new TcsQuoteFeed();
            await MeasureAsync("TaskCompletionSource feed", async () =>
            {
                for (var i = 0; i < Operations; i++)
                    await tcsFeed.ReceiveAsync("MSFT");
            });

            Out.Section("Pooled feed: the same ValueTask awaited twice");
            var feed = new PooledQuoteFeed();
            var pending = feed.ReceiveAsync("MSFT");
            Out.Log($"MSFT = {await pending}");
            try
            {
                Out.Log($"MSFT again = {await pending}");
            }
            catch (InvalidOperationException ex)
            {
                Out.Bad($"{ex.GetType().Name}: the token is stale, the operation object was already reset and pooled");
            }

            Out.Section("Naive pooled feed (token ignored): await twice, with a reuse in between");
            var naive = new NaivePooledQuoteFeed();
            var first = naive.ReceiveAsync("MSFT");
            Out.Log($"MSFT = {await first}");
            Out.Log($"AAPL = {await naive.ReceiveAsync("AAPL")}   (same pooled object, reused)");
            var again = await first;
            Out.Bad($"MSFT again = {again}  ← that's AAPL's price");
        }
        else
        {
            Out.Section("Pooled IValueTaskSource feed");
            var feed = new PooledQuoteFeed();
            await MeasureAsync("Pooled IValueTaskSource feed", async () =>
            {
                for (var i = 0; i < Operations; i++)
                    await feed.ReceiveAsync("MSFT");
            });
            Out.Metric("Operation objects ever created", feed.OperationsCreated, feed.OperationsCreated < 10);

            Out.Section("Need it twice, or in WhenAll? Pay for a Task only then");
            var preserved = feed.ReceiveAsync("MSFT").Preserve();
            Out.Log($"MSFT = {await preserved}, again = {await preserved}   (Preserve(): Task-backed copy)");

            var both = await Task.WhenAll(feed.ReceiveAsync("MSFT").AsTask(), feed.ReceiveAsync("AAPL").AsTask());
            Out.Good($"WhenAll via AsTask(): MSFT = {both[0]}, AAPL = {both[1]}");
        }
    }

    // Completions run on thread-pool threads: count allocations of the whole process.
    static async Task MeasureAsync(string label, Func<Task> loop)
    {
        await loop(); // warm-up: JIT, tiering, pool

        var before = GC.GetTotalAllocatedBytes(precise: true);
        await loop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        var perOperation = (double)allocated / Operations;
        Out.Metric(label, $"{perOperation,6:F1} bytes/operation   ({allocated:N0} bytes)", perOperation < 1);
    }
}
