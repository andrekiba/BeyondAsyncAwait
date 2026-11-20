using System.Collections.Concurrent;
using System.Threading.Tasks.Sources;

namespace BeyondAsyncAwait.Demo03;

/// <summary>Fixed prices, so a wrong result is obvious on screen.</summary>
static class Prices
{
    public static decimal Of(string symbol) => symbol switch
    {
        "MSFT" => 412.10m,
        "AAPL" => 228.70m,
        _ => 100m
    };
}

/// <summary>
/// Socket-style receiver: every ReceiveAsync rents a reusable operation object from a pool
/// and returns a ValueTask backed by it. No allocation per operation once the pool is warm.
/// </summary>
sealed class PooledQuoteFeed
{
    readonly ConcurrentQueue<QuoteOperation> _pool = new();
    int _created;

    public int OperationsCreated => Volatile.Read(ref _created);

    public ValueTask<decimal> ReceiveAsync(string symbol)
    {
        if (!_pool.TryDequeue(out var operation))
        {
            operation = new QuoteOperation(this);
            Interlocked.Increment(ref _created);
        }

        return operation.Start(symbol);
    }

    void Return(QuoteOperation operation) => _pool.Enqueue(operation);

    sealed class QuoteOperation(PooledQuoteFeed owner) : IValueTaskSource<decimal>, IThreadPoolWorkItem
    {
        // Mutable struct: never make this field readonly.
        ManualResetValueTaskSourceCore<decimal> _core;
        string _symbol = "";

        public ValueTask<decimal> Start(string symbol)
        {
            _symbol = symbol;
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            // The token is the version: it changes on every Reset, so a stale ValueTask is detected.
            return new ValueTask<decimal>(this, _core.Version);
        }

        // The "network" answers on a thread-pool thread.
        void IThreadPoolWorkItem.Execute() => _core.SetResult(Prices.Of(_symbol));

        public decimal GetResult(short token)
        {
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                // Consumed: reset (version++) and back to the pool, ready for the next ReceiveAsync.
                _core.Reset();
                owner.Return(this);
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);
    }
}

/// <summary>
/// The same pooled feed, written naively: the token is ignored.
/// Nothing tells a stale ValueTask from the current operation of the reused object.
/// </summary>
sealed class NaivePooledQuoteFeed
{
    readonly ConcurrentQueue<NaiveOperation> _pool = new();

    public ValueTask<decimal> ReceiveAsync(string symbol)
    {
        if (!_pool.TryDequeue(out var operation))
            operation = new NaiveOperation(this);

        return operation.Start(symbol);
    }

    void Return(NaiveOperation operation) => _pool.Enqueue(operation);

    sealed class NaiveOperation(NaivePooledQuoteFeed owner) : IValueTaskSource<decimal>, IThreadPoolWorkItem
    {
        readonly Lock _gate = new();
        string _symbol = "";
        decimal _result;
        bool _completed;
        Action<object?>? _continuation;
        object? _state;

        public ValueTask<decimal> Start(string symbol)
        {
            lock (_gate)
            {
                _symbol = symbol;
                _completed = false;
            }

            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            return new ValueTask<decimal>(this, 0);
        }

        void IThreadPoolWorkItem.Execute()
        {
            Action<object?>? continuation;
            object? state;
            lock (_gate)
            {
                _result = Prices.Of(_symbol);
                _completed = true;
                (continuation, state) = (_continuation, _state);
                _continuation = null;
            }

            continuation?.Invoke(state);
        }

        public decimal GetResult(short token)
        {
            var result = _result;
            owner.Return(this);
            return result;
        }

        public ValueTaskSourceStatus GetStatus(short token) =>
            Volatile.Read(ref _completed) ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            lock (_gate)
            {
                if (!_completed)
                {
                    (_continuation, _state) = (continuation, state);
                    return;
                }
            }

            continuation(state);
        }
    }
}

/// <summary>The classic way: a TaskCompletionSource (and its Task) per operation.</summary>
sealed class TcsQuoteFeed
{
    public Task<decimal> ReceiveAsync(string symbol)
    {
        var operation = new TcsOperation(symbol);
        ThreadPool.UnsafeQueueUserWorkItem(operation, preferLocal: false);
        return operation.Completion.Task;
    }

    sealed class TcsOperation(string symbol) : IThreadPoolWorkItem
    {
        public TaskCompletionSource<decimal> Completion { get; } = new();

        void IThreadPoolWorkItem.Execute() => Completion.SetResult(Prices.Of(symbol));
    }
}
