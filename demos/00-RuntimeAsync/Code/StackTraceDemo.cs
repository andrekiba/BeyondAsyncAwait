using System.Diagnostics;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.RuntimeAsyncDemos;

/// <summary>
/// 0.2 — The live stack (what debuggers, profilers and new StackTrace() see) of a 3-level async chain.
/// Exception stack traces were already cleaned up by ExceptionDispatchInfo: the difference is in the live stack.
/// </summary>
sealed class StackTraceDemo : Demo
{
    public override string Id => "0.2";
    public override string Name => "stack";
    public override string Title => "Live stack trace of an async chain";
    public override bool HasVariants => false;

    public override async Task RunAsync(Variant variant)
    {
        Out.Info(BuildInfo.Description);

        Out.Section("Chain that completes synchronously (await Task.CompletedTask)");
        await OuterAsync(suspend: false);

        Out.Section("Same chain after a real suspension (await Task.Yield) — who is on the stack now?");
        await OuterAsync(suspend: true);
    }

    static async Task OuterAsync(bool suspend)
    {
        await Task.CompletedTask;
        await MiddleAsync(suspend);
    }

    static async Task MiddleAsync(bool suspend)
    {
        await Task.CompletedTask;
        await InnerAsync(suspend);
    }

    static async Task InnerAsync(bool suspend)
    {
        if (suspend)
            await Task.Yield();
        else
            await Task.CompletedTask;

        var frames = new StackTrace(fNeedFileInfo: true).GetFrames();
        Out.Metric("Frames", frames.Length);
        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            var text = $"  at {IlReader.TypeName(method?.DeclaringType)}.{method?.Name}";
            var line = frame.GetFileLineNumber();
            if (line > 0)
                text += $" (line {line})";

            if (method?.DeclaringType?.Namespace?.StartsWith("System") == true)
                Out.Dim(text);
            else
                Out.Info(text);
        }
    }
}
