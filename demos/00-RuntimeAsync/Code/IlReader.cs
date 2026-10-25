using System.Reflection;
using System.Reflection.Emit;

namespace BeyondAsyncAwait.RuntimeAsyncDemos;

/// <summary>
/// A tiny IL decoder, so the demo doesn't depend on ILSpy being open on stage.
/// </summary>
static class IlReader
{
    static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    public readonly record struct Instruction(int Offset, OpCode OpCode, string? Operand)
    {
        public override string ToString() => $"IL_{Offset:x4}: {OpCode.Name,-12} {Operand}";
    }

    public static List<Instruction> Read(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var result = new List<Instruction>();
        var i = 0;

        while (i < il.Length)
        {
            var offset = i;
            var value = (short)il[i++];
            if (value == 0xFE)
                value = unchecked((short)(0xFE00 | il[i++]));

            var opCode = OpCodesByValue[value];
            string? operand = null;

            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    operand = il[i].ToString();
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    operand = BitConverter.ToInt16(il, i).ToString();
                    i += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, i);
                    i += 4 + count * 4;
                    operand = $"({count} targets)";
                    break;
                default:
                    var token = BitConverter.ToInt32(il, i);
                    i += 4;
                    operand = Resolve(method, opCode.OperandType, token);
                    break;
            }

            result.Add(new Instruction(offset, opCode, operand));
        }

        return result;
    }

    static string? Resolve(MethodBase method, OperandType operandType, int token)
    {
        var module = method.Module;
        var typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

        try
        {
            return operandType switch
            {
                OperandType.InlineMethod => Format(module.ResolveMethod(token, typeArgs, methodArgs)),
                OperandType.InlineField => Format(module.ResolveField(token, typeArgs, methodArgs)),
                OperandType.InlineType => TypeName(module.ResolveType(token, typeArgs, methodArgs)),
                OperandType.InlineString => $"\"{module.ResolveString(token)}\"",
                OperandType.InlineTok => module.ResolveMember(token, typeArgs, methodArgs)?.Name,
                _ => $"0x{token:x8}"
            };
        }
        catch
        {
            return $"0x{token:x8}";
        }
    }

    static string Format(MemberInfo? member) => member is null ? "?" : $"{TypeName(member.DeclaringType)}::{member.Name}";

    public static string TypeName(Type? type)
    {
        if (type is null)
            return "?";
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }
}
