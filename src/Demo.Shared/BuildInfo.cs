using System.Reflection;

namespace BeyondAsyncAwait.Shared;

/// <summary>Tells the audience which async model the running demo was compiled with.</summary>
public static class BuildInfo
{
    const BindingFlags AllMethods = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>
    /// True when the assembly was compiled with <c>runtime-async=on</c>: its async methods carry
    /// <see cref="MethodImplAttributes.Async"/> (0x2000) instead of a compiler-generated state machine.
    /// Counting state machines is not enough: async void methods keep theirs even with runtime async.
    /// </summary>
    public static bool IsRuntimeAsync(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(t => t.GetMethods(AllMethods))
            .Any(m => (m.MethodImplementationFlags & MethodImplAttributes.Async) != 0);

    public static string Description
    {
        get
        {
            var entry = Assembly.GetEntryAssembly();
            var model = entry is null ? "?" : IsRuntimeAsync(entry) ? "runtime async" : "compiler state machines";
            return $"{model}, .NET {Environment.Version}";
        }
    }
}
