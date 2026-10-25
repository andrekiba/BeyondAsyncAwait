using System.Threading.Channels;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Act04;

/// <summary>
/// Publishes quotes downstream in batches of 10. Whatever is still buffered must be flushed on shutdown,
/// and flushing is I/O: the type implements only IAsyncDisposable.
/// </summary>
sealed class BatchingPublisher : IAsyncDisposable
{
    const int BatchSize = 10;

    readonly Channel<Quote> _input = Channel.CreateUnbounded<Quote>(new UnboundedChannelOptions { SingleReader = true });
    readonly bool _waitForFlush;
    readonly Task _loop;
    int _published, _disposed;

    /// <param name="waitForFlush">False reproduces the bug: DisposeAsync returns before the last flush.</param>
    public BatchingPublisher(bool waitForFlush)
    {
        _waitForFlush = waitForFlush;
        _loop = LoopAsync();
    }

    public int Published => Volatile.Read(ref _published);

    public ValueTask PublishAsync(Quote quote) => _input.Writer.WriteAsync(quote);

    public async ValueTask DisposeAsync()
    {
        // Idempotent: DI containers, using blocks and shutdown hooks may all call it.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            Out.Log("publisher: already disposed, nothing to do");
            return;
        }

        _input.Writer.TryComplete();
        if (!_waitForFlush)
            return;

        // Wait for the last flush; a failed flush is logged, it must not throw out of DisposeAsync.
        await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_loop.IsFaulted)
            Out.Bad($"publisher: last flush failed: {_loop.Exception!.InnerException!.Message}");
        Out.Log("publisher: disposed, buffer flushed");
    }

    async Task LoopAsync()
    {
        var batch = new List<Quote>(BatchSize);
        await foreach (var quote in _input.Reader.ReadAllAsync())
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
        await Task.Delay(50); // the downstream write
        Interlocked.Add(ref _published, batch.Count);
        Out.Log($"publisher: flushed a batch of {batch.Count}");
        batch.Clear();
    }
}
