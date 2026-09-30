using System.Diagnostics;
using Enma;

const string Usage =
    """
    enmac check <file.enma>...          report errors and warnings
    enmac build <file.enma> [-o out.lua] compile to Lua (default: next to the source)
    enmac run <file.enma|file.lua> [--seconds N]
                                        run; keeps ticking while the program has on_tick
                                        callbacks or timers (at most N seconds, default 30)
    """;

if (args.Length < 2)
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 0 : 2;
}

string command = args[0];
try
{
    return command switch
    {
        "check" => Check(args[1..]),
        "build" => Build(args[1..]),
        "run" => Run(args[1..]),
        _ => Fail($"unknown command '{command}'\n\n{Usage}"),
    };
}
catch (IOException ex)
{
    return Fail(ex.Message);
}
catch (UnauthorizedAccessException ex)
{
    return Fail(ex.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

static void Print(string file, EnmaResult result)
{
    foreach (var d in result.Diagnostics)
    {
        Console.ForegroundColor = d.Severity == EnmaSeverity.Error ? ConsoleColor.Red : ConsoleColor.Yellow;
        Console.WriteLine($"{file}:{d}");
    }
    Console.ResetColor();
}

static int Check(string[] files)
{
    int failed = 0;
    foreach (string file in files)
    {
        var result = EnmaCompiler.Compile(File.ReadAllText(file));
        Print(file, result);
        if (!result.Success) failed++;
    }
    Console.WriteLine(failed == 0 ? $"{files.Length} file(s) OK" : $"{failed} of {files.Length} file(s) have errors");
    return failed == 0 ? 0 : 1;
}

static int Build(string[] rest)
{
    string file = rest[0];
    int o = Array.IndexOf(rest, "-o");
    string output = o >= 0 && o + 1 < rest.Length ? rest[o + 1] : Path.ChangeExtension(file, ".lua");

    var result = EnmaCompiler.Compile(File.ReadAllText(file));
    Print(file, result);
    if (result.Lua is not { } lua) return 1;
    File.WriteAllText(output, $"-- Compiled from {Path.GetFileName(file)} by Enma {EnmaCompiler.Version}\n" + lua);
    Console.WriteLine($"wrote {output}");
    return 0;
}

static int Run(string[] rest)
{
    string file = rest[0];
    int s = Array.IndexOf(rest, "--seconds");
    double limit = s >= 0 && s + 1 < rest.Length && double.TryParse(rest[s + 1], out double n) ? n : 30;

    using var runtime = new EnmaRuntime(Path.GetFileNameWithoutExtension(file), Console.WriteLine);
    string source = File.ReadAllText(file);
    bool started = Path.GetExtension(file).Equals(".lua", StringComparison.OrdinalIgnoreCase)
        ? runtime.StartLua(source)
        : runtime.Start(source);
    if (!started) return 1;

    bool interrupted = false;
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        interrupted = true;
    };

    var clock = Stopwatch.StartNew();
    while (runtime.HasWork && !interrupted && clock.Elapsed.TotalSeconds < limit)
    {
        Thread.Sleep(50);
        runtime.Tick();
    }
    bool ok = runtime.IsRunning;
    runtime.Stop();
    return ok ? 0 : 1;
}
