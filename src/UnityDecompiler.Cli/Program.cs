using UnityDecompiler.Core;

// Command-line version: unity-decompiler <game.exe | game folder | game.zip> [-o output] [--all]
string? input = null, output = null;
var includeFramework = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o" or "--output" when i + 1 < args.Length: output = args[++i]; break;
        case "--all": includeFramework = true; break;
        case "-h" or "--help": input = null; i = args.Length; break;
        default: input ??= args[i]; break;
    }
}

if (input == null)
{
    Console.WriteLine("Usage: unity-decompiler <game.exe | game folder | game.zip | Assembly.dll> [-o <output folder>] [--all]");
    Console.WriteLine("  --all   also decompile UnityEngine / System assemblies");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var lastLine = "";
var progress = new InlineProgress(p =>
{
    // Keep per-file counters on a single line.
    var transient = p.Message.Contains(" files") && p.Level == LogLevel.Info;
    if (transient)
    {
        Console.Write("\r" + p.Message.PadRight(Math.Max(lastLine.Length, p.Message.Length)));
        lastLine = p.Message;
        return;
    }
    if (lastLine.Length > 0) { Console.WriteLine(); lastLine = ""; }
    Console.ForegroundColor = p.Level switch
    {
        LogLevel.Success => ConsoleColor.Green,
        LogLevel.Warning => ConsoleColor.Yellow,
        LogLevel.Error => ConsoleColor.Red,
        _ => Console.ForegroundColor,
    };
    Console.WriteLine(p.Message);
    Console.ResetColor();
});

try
{
    var result = GameDecompiler.Run(new DecompileOptions
    {
        InputPath = input,
        OutputDirectory = output,
        IncludeFrameworkAssemblies = includeFramework,
    }, progress, cts.Token);
    Console.WriteLine($"Output: {result.OutputDirectory}");
    if (result.SolutionPath != null) Console.WriteLine($"Solution: {result.SolutionPath}");
    return result.AssembliesFailed == 0 ? 0 : 2;
}
catch (UnityGameNotFoundException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

sealed class InlineProgress(Action<DecompileProgress> handler) : IProgress<DecompileProgress>
{
    public void Report(DecompileProgress value) => handler(value);
}
