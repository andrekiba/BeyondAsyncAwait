using System.Reflection;
using System.Runtime.CompilerServices;
using BeyondAsyncAwait.Shared;

namespace BeyondAsyncAwait.RuntimeAsyncDemos;

/// <summary>
/// 0.1 — What the compiler emits for one await.
/// Classic: a stub method + a nested state machine struct whose MoveNext holds the real code.
/// Runtime async: the method itself holds the code, flagged as async for the runtime.
/// </summary>
sealed class IlDemo : Demo
{
    public override string Id => "0.1";
    public override string Name => "il";
    public override string Title => "IL: compiler state machine vs runtime async";
    public override bool HasVariants => false;

    public override Task RunAsync(Variant variant)
    {
        var assembly = typeof(IlDemo).Assembly;
        var method = typeof(QuotePricing).GetMethod(nameof(QuotePricing.GetGrossPriceAsync))!;

        Out.Info($"Assembly: {assembly.GetName().Name} — {BuildInfo.Description}");

        var stateMachines = assembly.GetTypes().Where(t => typeof(IAsyncStateMachine).IsAssignableFrom(t)).ToList();
        Out.Metric("Compiler-generated state machines", stateMachines.Count, stateMachines.Count == 0);

        Out.Section($"{nameof(QuotePricing)}.{method.Name}");
        var stateMachineAttribute = method.GetCustomAttribute<AsyncStateMachineAttribute>();
        Out.Metric("[AsyncStateMachine]", stateMachineAttribute?.StateMachineType.Name ?? "(none)");
        Out.Metric("MethodImplAttributes", $"{method.MethodImplementationFlags} (0x{(int)method.MethodImplementationFlags:x4})");
        PrintIl(method, callsOnly: false);

        if (stateMachineAttribute is not null)
        {
            var type = stateMachineAttribute.StateMachineType;
            Out.Section($"State machine {type.Name} ({(type.IsValueType ? "struct" : "class")})");
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                Out.Info($"  field {IlReader.TypeName(field.FieldType),-40} {field.Name}");

            var moveNext = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            Out.Section($"{type.Name}.MoveNext — calls only");
            PrintIl(moveNext, callsOnly: true);
        }

        return Task.CompletedTask;
    }

    static void PrintIl(MethodBase method, bool callsOnly)
    {
        var il = IlReader.Read(method);
        Out.Metric("IL size / instructions", $"{method.GetMethodBody()?.GetILAsByteArray()?.Length} bytes / {il.Count}");

        foreach (var instruction in il)
        {
            var isCall = instruction.OpCode.Name is "call" or "callvirt" or "newobj";
            if (callsOnly && !isCall)
                continue;

            if (isCall)
                Out.Info($"  {instruction}");
            else
                Out.Dim($"  {instruction}");
        }
    }
}
