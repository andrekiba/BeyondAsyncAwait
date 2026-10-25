using BenchmarkDotNet.Attributes;

namespace BeyondAsyncAwait.Benchmarks;

/// <summary>
/// Act 0 — a chain of <see cref="Depth"/> async methods, each awaiting the next.
/// CompletesSynchronously: the leaf never suspends (the fast path, no continuation needed).
/// Suspends: the leaf yields, so every level of the chain has to suspend and resume.
/// </summary>
public class AwaitChainBenchmarks
{
    [Params(10)]
    public int Depth { get; set; }

    [Benchmark]
    public Task CompletesSynchronously() => LevelAsync(Depth, suspend: false);

    [Benchmark]
    public Task Suspends() => LevelAsync(Depth, suspend: true);

    static async Task LevelAsync(int depth, bool suspend)
    {
        if (depth == 0)
        {
            if (suspend)
                await Task.Yield();
            return;
        }

        await LevelAsync(depth - 1, suspend);
    }
}
