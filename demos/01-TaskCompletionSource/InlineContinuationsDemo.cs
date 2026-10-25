using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.Act01;

/// <summary>
/// 1.1 — SetResult runs the awaiter's continuation inline, on the thread that completes the TCS.
/// Here that thread is a connection's read loop: every slow reply handler stalls all the frames behind it,
/// and the handler even runs while the loop holds its lock. Fix: RunContinuationsAsynchronously.
/// </summary>
sealed class InlineContinuationsDemo : Demo
{
    const int Requests = 5;
    static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(20);
    static readonly TimeSpan HandlerWork = TimeSpan.FromMilliseconds(150);

    public override string Id => "1.1";
    public override string Name => "inline-continuations";
    public override string Title => "SetResult runs your continuation on the read loop";

    public override async Task RunAsync(Variant variant)
    {
        var options = variant == Variant.Broken
            ? TaskCreationOptions.None
            : TaskCreationOptions.RunContinuationsAsynchronously;

        var connection = new FakeConnection(options);
        var onReadLoop = 0;

        var handlers = Enumerable.Range(1, Requests)
            .Select(id => HandleAsync(connection, id, () => Interlocked.Increment(ref onReadLoop)))
            .ToArray();

        connection.StartReadLoop(Requests, FrameInterval);
        await Task.WhenAll(handlers);
        await connection.ReadLoopCompletion;

        Out.Section("Results");
        Out.Metric("Handlers run on the read loop", $"{onReadLoop}/{Requests}", onReadLoop == 0);
        Out.Metric("Max frame delay in the read loop", $"{connection.MaxFrameDelayMs} ms", connection.MaxFrameDelayMs < 50);
        Out.Metric("All replies handled after", $"{Out.ElapsedMs} ms");
    }

    static async Task HandleAsync(FakeConnection connection, int id, Action countOnReadLoop)
    {
        var reply = await connection.SendAsync(id);

        var onLoop = Environment.CurrentManagedThreadId == connection.ReadLoopThreadId;
        if (onLoop)
        {
            countOnReadLoop();
            Out.Bad($"request {id}: handling reply {reply.Id} ON THE READ LOOP (inside its lock: {connection.IsLockHeldByCurrentThread})");
        }
        else
        {
            Out.Good($"request {id}: handling reply {reply.Id} on a pool thread (inside the loop's lock: {connection.IsLockHeldByCurrentThread})");
        }

        // CPU-bound work on the reply: deserialize, validate, map.
        Thread.Sleep(HandlerWork);
    }
}

sealed record Frame(int Id);

/// <summary>A connection with pending requests completed by a dedicated read loop thread.</summary>
sealed class FakeConnection(TaskCreationOptions completionOptions)
{
    readonly Lock _gate = new();
    readonly Dictionary<int, TaskCompletionSource<Frame>> _pending = [];
    readonly TaskCompletionSource _readLoopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ReadLoopThreadId { get; private set; }
    public long MaxFrameDelayMs { get; private set; }
    public bool IsLockHeldByCurrentThread => _gate.IsHeldByCurrentThread;
    public Task ReadLoopCompletion => _readLoopCompletion.Task;

    public Task<Frame> SendAsync(int id)
    {
        var tcs = new TaskCompletionSource<Frame>(completionOptions);
        lock (_gate)
            _pending.Add(id, tcs);
        return tcs.Task;
    }

    public void StartReadLoop(int frames, TimeSpan interval)
    {
        var thread = new Thread(() => ReadLoop(frames, interval)) { IsBackground = true, Name = "read-loop" };
        thread.Start();
    }

    void ReadLoop(int frames, TimeSpan interval)
    {
        ReadLoopThreadId = Environment.CurrentManagedThreadId;
        var start = Out.ElapsedMs;

        for (var id = 1; id <= frames; id++)
        {
            // Frames arrive on the wire at a fixed pace: if the loop is busy, they wait.
            var arrival = start + (long)(interval.TotalMilliseconds * id);
            var wait = arrival - Out.ElapsedMs;
            if (wait > 0)
                Thread.Sleep((int)wait);

            var delay = Out.ElapsedMs - arrival;
            MaxFrameDelayMs = Math.Max(MaxFrameDelayMs, delay);
            Out.Log($"read loop: frame {id} dispatched, {delay} ms after it arrived");

            // Completing inside the lock is common: with inline continuations, the handler runs in here too.
            lock (_gate)
            {
                _pending.Remove(id, out var tcs);
                tcs!.SetResult(new Frame(id));
            }
        }

        _readLoopCompletion.SetResult();
    }
}
