using System.Diagnostics;

namespace BeyondAsyncAwait.Shared;

/// <summary>
/// Console output for the stage: every line carries elapsed time and managed thread id,
/// because in async demos "when" and "on which thread" are the whole point.
/// </summary>
public static class Out
{
    static readonly Lock Gate = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static long ElapsedMs => Clock.ElapsedMilliseconds;

    public static void ResetClock() => Clock.Restart();

    public static void Header(string text)
    {
        lock (Gate)
        {
            Console.WriteLine();
            Write(ConsoleColor.Cyan, $"══ {text} ".PadRight(78, '═'));
        }
    }

    public static void Section(string text)
    {
        lock (Gate)
        {
            Console.WriteLine();
            Write(ConsoleColor.Yellow, $"── {text}");
        }
    }

    /// <summary>Timestamped line: "  1234 ms [t 5] message".</summary>
    public static void Log(string message) =>
        Line(ConsoleColor.Gray, $"{Clock.ElapsedMilliseconds,6} ms [t{Environment.CurrentManagedThreadId,3}] {message}");

    public static void Info(string message) => Line(ConsoleColor.White, message);
    public static void Good(string message) => Line(ConsoleColor.Green, $"✔ {message}");
    public static void Bad(string message) => Line(ConsoleColor.Red, $"✘ {message}");
    public static void Warn(string message) => Line(ConsoleColor.DarkYellow, $"! {message}");
    public static void Dim(string message) => Line(ConsoleColor.DarkGray, message);

    /// <summary>A number the audience should look at. <paramref name="ok"/> colors it green/red.</summary>
    public static void Metric(string name, object? value, bool? ok = null)
    {
        var color = ok switch { true => ConsoleColor.Green, false => ConsoleColor.Red, _ => ConsoleColor.White };
        Line(color, $"  {name,-32} {value}");
    }

    static void Line(ConsoleColor color, string text)
    {
        lock (Gate) Write(color, text);
    }

    static void Write(ConsoleColor color, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}
