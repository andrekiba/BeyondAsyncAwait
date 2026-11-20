using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Demo01;

/// <summary>
/// 1.3 — An async manual-reset event (pause/resume for a pipeline) built on TaskCompletionSource.
/// Reset must swap in a new TCS only if the current one is completed, atomically:
/// a blind swap orphans the waiters already awaiting the old task, and they never wake up.
/// </summary>
sealed class ManualResetEventDemo : Demo
{
    const int Workers = 3;

    public override string Id => "1.3";
    public override string Name => "manual-reset-event";
    public override string Title => "AsyncManualResetEvent: Reset without losing waiters";

    public override async Task RunAsync(Variant variant)
    {
        IAsyncEvent gate = variant == Variant.Broken ? new NaiveAsyncManualResetEvent() : new AsyncManualResetEvent();
        var resumed = 0;

        // The pipeline starts paused: the workers wait at the gate.
        var workers = Enumerable.Range(1, Workers).Select(async id =>
        {
            Out.Log($"worker {id}: waiting at the gate");
            await gate.WaitAsync();
            Interlocked.Increment(ref resumed);
            Out.Log($"worker {id}: resumed");
        }).ToArray();

        await Task.Delay(50);
        Out.Log("operator: Pause (the pipeline is already paused)");
        gate.Reset();

        await Task.Delay(50);
        Out.Log("operator: Resume");
        gate.Set();

        await Task.WhenAny(Task.WhenAll(workers), Task.Delay(500));

        Out.Section("Results");
        Out.Metric("Workers resumed", $"{resumed}/{Workers}", resumed == Workers);
    }
}

interface IAsyncEvent
{
    Task WaitAsync();
    void Set();
    void Reset();
}

/// <summary>Broken: Reset always swaps the TCS, even when nobody has set it yet.</summary>
sealed class NaiveAsyncManualResetEvent : IAsyncEvent
{
    TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync() => tcs.Task;
    public void Set() => tcs.TrySetResult();
    public void Reset() => tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Fixed: swap only a completed TCS, and only if nobody swapped it in the meantime.</summary>
sealed class AsyncManualResetEvent : IAsyncEvent
{
    TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync() => Volatile.Read(ref tcs).Task;

    public void Set() => Volatile.Read(ref tcs).TrySetResult();

    public void Reset()
    {
        while (true)
        {
            var current = Volatile.Read(ref tcs);
            if (!current.Task.IsCompleted)
                return;

            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref tcs, next, current) == current)
                return;
        }
    }
}
