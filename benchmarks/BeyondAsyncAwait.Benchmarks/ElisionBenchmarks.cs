using BenchmarkDotNet.Attributes;

namespace BeyondAsyncAwait.Benchmarks;

/// <summary>
/// Demo 0 — does "async elision" (return the Task, don't await it) still pay off with runtime async?
/// A pass-through method in front of a leaf that really suspends.
/// </summary>
public class ElisionBenchmarks
{
    [Benchmark]
    public Task<int> Awaited() => AwaitedAsync();

    [Benchmark]
    public Task<int> Elided() => ElidedPassThrough();

    static async Task<int> AwaitedAsync() => await LeafAsync();

    static Task<int> ElidedPassThrough() => LeafAsync();

    static async Task<int> LeafAsync()
    {
        await Task.Yield();
        return 42;
    }
}
