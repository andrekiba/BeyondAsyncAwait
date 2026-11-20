using BeyondAsyncAwait.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondAsyncAwait.Demo04;

/// <summary>
/// 4.3 — Graceful shutdown of a batching publisher. Two ways to lose the last batch: dispose the DI scope
/// synchronously (a service that only implements IAsyncDisposable makes it throw), or write a DisposeAsync that
/// completes the input but doesn't wait for the flush. Fixed: await using the async scope, and a DisposeAsync that
/// completes input, awaits the loop (SuppressThrowing) and is idempotent.
/// </summary>
sealed class AsyncDisposeDemo : Demo
{
    const int Quotes = 25;

    public override string Id => "4.3";
    public override string Name => "async-dispose";
    public override string Title => "IAsyncDisposable: flush on shutdown";

    public override async Task RunAsync(Variant variant)
    {
        if (variant == Variant.Broken)
        {
            Out.Section("Sync dispose of the DI scope");
            BatchingPublisher? publisher = null;
            await using (var services = BuildServices(waitForFlush: true))
            {
                try
                {
                    using var scope = services.CreateScope();
                    publisher = scope.ServiceProvider.GetRequiredService<BatchingPublisher>();
                    await HandleRequestAsync(publisher);
                }
                catch (InvalidOperationException ex)
                {
                    Out.Bad(ex.Message);
                }
            }

            Out.Metric("Published at shutdown", $"{publisher!.Published}/{Quotes}", false);

            Out.Section("DisposeAsync that doesn't wait for the flush");
            await using (var services = BuildServices(waitForFlush: false))
            {
                await using var scope = services.CreateAsyncScope();
                publisher = scope.ServiceProvider.GetRequiredService<BatchingPublisher>();
                await HandleRequestAsync(publisher);
            }

            Out.Metric("Published at shutdown", $"{publisher.Published}/{Quotes}", false);
            await Task.Delay(150);
            Out.Warn($"{publisher.Published}/{Quotes} only after DisposeAsync returned: in a real shutdown the process is already gone");
        }
        else
        {
            BatchingPublisher publisher;
            await using (var services = BuildServices(waitForFlush: true))
            {
                await using var scope = services.CreateAsyncScope();
                publisher = scope.ServiceProvider.GetRequiredService<BatchingPublisher>();
                await HandleRequestAsync(publisher);
            }

            Out.Metric("Published at shutdown", $"{publisher.Published}/{Quotes}", publisher.Published == Quotes);
            await publisher.DisposeAsync(); // a second dispose must be harmless
        }
    }

    static ServiceProvider BuildServices(bool waitForFlush) =>
        new ServiceCollection()
            .AddScoped(_ => new BatchingPublisher(waitForFlush))
            .BuildServiceProvider();

    static async Task HandleRequestAsync(BatchingPublisher publisher)
    {
        for (var i = 0; i < Quotes; i++)
            await publisher.PublishAsync(new Quote("MSFT", 412.10m + i, DateTimeOffset.UtcNow));

        Out.Log($"request: {Quotes} quotes published");
        await Task.Delay(300); // the rest of the request: two full batches get flushed meanwhile
        Out.Log("request: done, disposing the scope");
    }
}
