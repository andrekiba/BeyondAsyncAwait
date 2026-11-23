namespace BeyondAsyncAwait.Demo02;

static class Concurrently
{
    /// <summary>
    /// Starts <paramref name="count"/> calls at the same instant on the thread pool,
    /// like a burst of HTTP requests for a key that isn't cached yet.
    /// </summary>
    public static async Task<T[]> RunAsync<T>(int count, Func<Task<T>> call)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, count).Select(async _ =>
        {
            await start.Task;
            return await call();
        }).ToArray();

        start.SetResult();
        return await Task.WhenAll(calls);
    }
}
