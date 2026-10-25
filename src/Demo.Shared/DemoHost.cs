namespace BeyondAsyncAwait.Shared;

/// <summary>
/// Command line for every act:
/// <code>
/// dotnet run                       interactive menu
/// dotnet run -- list               list the demos
/// dotnet run -- 1.2                broken, then fixed
/// dotnet run -- rpc-correlation --fixed
/// dotnet run -- all --no-pause     every demo, both variants (smoke test)
/// </code>
/// </summary>
public static class DemoHost
{
    public static async Task<int> RunAsync(string[] args, params Demo[] demos)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var pause = !args.Contains("--no-pause") && !Console.IsInputRedirected;
        var variants = args.Contains("--broken") ? [Variant.Broken]
            : args.Contains("--fixed") ? [Variant.Fixed]
            : new[] { Variant.Broken, Variant.Fixed };
        var target = args.FirstOrDefault(a => !a.StartsWith("--"));

        if (target is null)
            return await MenuAsync(demos);

        if (target == "list")
        {
            List(demos);
            return 0;
        }

        var selected = target == "all"
            ? demos
            : demos.Where(d => d.Id == target || d.Name.Equals(target, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (selected.Length == 0)
        {
            Out.Bad($"Unknown demo '{target}'.");
            List(demos);
            return 1;
        }

        foreach (var demo in selected)
        {
            if (!await RunDemoAsync(demo, variants, pause))
                return 1;
        }

        return 0;
    }

    static async Task<int> MenuAsync(Demo[] demos)
    {
        while (true)
        {
            List(demos);
            Console.Write("Demo id or name (b = broken only, f = fixed only, empty = exit): ");
            var line = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line))
                return 0;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var demo = demos.FirstOrDefault(d => d.Id == parts[0] || d.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            if (demo is null)
            {
                Out.Bad($"Unknown demo '{parts[0]}'.");
                continue;
            }

            Variant[] variants = parts.ElementAtOrDefault(1) switch
            {
                "b" => [Variant.Broken],
                "f" => [Variant.Fixed],
                _ => [Variant.Broken, Variant.Fixed]
            };

            await RunDemoAsync(demo, variants, pause: true);
        }
    }

    static async Task<bool> RunDemoAsync(Demo demo, Variant[] variants, bool pause)
    {
        if (!demo.HasVariants)
            variants = [Variant.Fixed];

        for (var i = 0; i < variants.Length; i++)
        {
            var variant = variants[i];
            Out.Header(demo.HasVariants ? $"{demo.Id} {demo.Title} [{variant.ToString().ToUpperInvariant()}]" : $"{demo.Id} {demo.Title}");
            Out.ResetClock();

            try
            {
                await demo.RunAsync(variant);
            }
            catch (Exception ex)
            {
                Out.Bad($"Demo crashed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            if (pause && i < variants.Length - 1)
            {
                Out.Dim("Press Enter for the fixed version...");
                Console.ReadLine();
            }
        }

        return true;
    }

    static void List(Demo[] demos)
    {
        Out.Header($"{AppDomain.CurrentDomain.FriendlyName} ({BuildInfo.Description})");
        foreach (var demo in demos)
            Out.Info($"  {demo.Id,-5} {demo.Name,-28} {demo.Title}");
        Console.WriteLine();
    }
}
