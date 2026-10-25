namespace BeyondAsyncAwait.Shared;

public static class InterlockedEx
{
    /// <summary>Atomically raise <paramref name="target"/> to <paramref name="value"/> if it is larger.</summary>
    public static void Max(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
