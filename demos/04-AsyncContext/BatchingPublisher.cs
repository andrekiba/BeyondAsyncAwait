using System.Threading.Channels;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo04;

/// <summary>
/// Publishes quotes downstream in batches of 10. Whatever is still buffered must be flushed on shutdown,
/// and flushing is I/O: the type implements only IAsyncDisposable.
/// </summary>
sealed class BatchingPublisher : IAsyncDisposable
{
    const int BatchSize = 10;

    readonly Channel<Quote> input = Channel.CreateUnbounded<Quote>(new UnboundedChannelOptions { SingleReader = true });
    readonly bool waitForFlush;
    readonly Task loop;
    int published, disposed;

    /// <param name="waitForFlush">False reproduces the bug: DisposeAsync returns before the last flush.</param>
    public BatchingPublisher(bool waitForFlush)
    {
        this.waitForFlush = waitForFlush;
        loop = LoopAsync();
    }

    public int Published => Volatile.Read(ref published);

    public ValueTask PublishAsync(Quote quote) => input.Writer.WriteAsync(quote);

    public async ValueTask DisposeAsync()
    {
        // Idempotent: DI containers, using blocks and shutdown hooks may all call it.
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            Out.Log("publisher: already disposed, nothing to do");
            return;
        }

        input.Writer.TryComplete();
        if (!waitForFlush)
            return;

        // Wait for the last flush; a failed flush is logged, it must not throw out of DisposeAsync.
        await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (loop.IsFaulted)
            Out.Bad($"publisher: last flush failed: {loop.Exception!.InnerException!.Message}");
        Out.Log("publisher: disposed, buffer flushed");
    }

    async Task LoopAsync()
    {
        var batch = new List<Quote>(BatchSize);
        // No token: the loop ends when the input is completed, so that DisposeAsync can drain the buffer.
        await foreach (var quote in input.Reader.ReadAllAsync())
        {
            batch.Add(quote);
            if (batch.Count == BatchSize)
                await FlushAsync(batch);
        }

        // Input completed: flush the remainder.
        if (batch.Count > 0)
            await FlushAsync(batch);
    }

    async Task FlushAsync(List<Quote> batch)
    {
        await Task.Delay(50); // the downstream write: no token, the last flush must not be cut short
        Interlocked.Add(ref published, batch.Count);
        Out.Log($"publisher: flushed a batch of {batch.Count}");
        batch.Clear();
    }
}
