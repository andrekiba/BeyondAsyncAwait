using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace BeyondAsyncAwait.Benchmarks;

/// <summary>
/// Demo 3 — the cache-hit path (completes synchronously) and a path that really suspends.
/// Awaited: the hit is awaited directly inside an async method (with runtime async the Task may never exist).
/// Materialized: the Task is handed back to the caller as an object.
/// Suspend_*: the pooling builder reuses its boxes, but pooled methods opt out of runtime async.
/// </summary>
public class ValueTaskBenchmarks
{
    const int Hits = 100;
    readonly Dictionary<string, decimal> prices = new() { ["MSFT"] = 412.10m };

    [Benchmark(OperationsPerInvoke = Hits)]
    public async Task<decimal> Hit_Task_Awaited()
    {
        decimal sum = 0;
        for (var i = 0; i < Hits; i++)
            sum += await GetTaskAsync("MSFT");
        return sum;
    }

    [Benchmark(OperationsPerInvoke = Hits)]
    public async Task<decimal> Hit_ValueTask_Awaited()
    {
        decimal sum = 0;
        for (var i = 0; i < Hits; i++)
            sum += await GetValueTaskAsync("MSFT");
        return sum;
    }

    // Kept in a field: the Task has to exist as an object. (Just returning it isn't enough: the
    // BenchmarkDotNet harness is compiled with runtime async too and awaits it directly.)
    Task<decimal>? lastTask;
    ValueTask<decimal> lastValueTask;

    [Benchmark]
    public decimal Hit_Task_Materialized()
    {
        lastTask = GetTaskAsync("MSFT");
        return lastTask.Result;
    }

    [Benchmark]
    public decimal Hit_ValueTask_Materialized()
    {
        lastValueTask = GetValueTaskAsync("MSFT");
        return lastValueTask.Result;
    }

    [Benchmark]
    public ValueTask<int> Suspend_ValueTask() => SuspendAsync();

    [Benchmark]
    public ValueTask<int> Suspend_PoolingValueTask() => SuspendPooledAsync();

    async Task<decimal> GetTaskAsync(string symbol)
    {
        if (prices.TryGetValue(symbol, out var price))
            return price;

        await Task.Yield();
        return 0;
    }

    async ValueTask<decimal> GetValueTaskAsync(string symbol)
    {
        if (prices.TryGetValue(symbol, out var price))
            return price;

        await Task.Yield();
        return 0;
    }

    static async ValueTask<int> SuspendAsync()
    {
        await Task.Yield();
        return 42;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    static async ValueTask<int> SuspendPooledAsync()
    {
        await Task.Yield();
        return 42;
    }
}
