namespace BeyondAsyncAwait.Act02;

/// <summary>
/// One shared async value, loaded once on demand. Unlike a plain Lazy&lt;Task&lt;T&gt;&gt;:
/// - a failed or cancelled load is not cached: the next caller starts a new one (2.2);
/// - callers cancel their own wait, never the shared load (2.3);
/// - the load runs on the thread pool without the first caller's ExecutionContext (2.4).
/// </summary>
public sealed class AsyncLazy<T>
{
    readonly Func<Task<T>> _factory;
    Lazy<Task<T>> _instance;

    public AsyncLazy(Func<Task<T>> factory)
    {
        _factory = factory;
        _instance = CreateInstance();
    }

    public Task<T> GetValueAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _instance).Value.WaitAsync(cancellationToken);

    Lazy<Task<T>> CreateInstance()
    {
        Lazy<Task<T>>? instance = null;
        instance = new Lazy<Task<T>>(() => StartLoad(instance!));
        return instance;
    }

    Task<T> StartLoad(Lazy<Task<T>> instance)
    {
        // The shared load belongs to no caller: don't let the first caller's AsyncLocal values flow into it.
        using (ExecutionContext.SuppressFlow())
            return Task.Run(() => LoadAsync(instance));
    }

    async Task<T> LoadAsync(Lazy<Task<T>> instance)
    {
        try
        {
            return await _factory();
        }
        catch
        {
            // Don't cache the failure: swap in a fresh Lazy, unless someone already did.
            Interlocked.CompareExchange(ref _instance, CreateInstance(), instance);
            throw;
        }
    }
}
