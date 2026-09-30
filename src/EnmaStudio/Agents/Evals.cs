using System.IO;

namespace EnmaStudio.Agents;

/// <summary>
/// A test task for the agents: a prompt plus checks, one per line:
/// <c>exists file</c>, <c>compiles file</c>, <c>contains file text</c>, <c>not_contains file text</c>, <c>approved</c>.
/// </summary>
public sealed class EvalCase : ObservableObject
{
    public string Name { get; set => SetField(ref field, value); } = "New case";
    public string Prompt { get; set => SetField(ref field, value); } = "";
    public string Checks { get; set => SetField(ref field, value); } = "";
}

public sealed record EvalCheckResult(string Check, bool Passed, string Detail);

public sealed record EvalResult(string Case, bool Passed, List<EvalCheckResult> Checks, string Folder)
{
    public string Summary => $"{Checks.Count(c => c.Passed)}/{Checks.Count} checks";
    public string Details => string.Join("\n", Checks.Select(c => $"{(c.Passed ? "✓" : "✗")} {c.Check}{(c.Passed ? "" : "  — " + c.Detail)}"));
}

/// <summary>One eval run, appended to eval_history.jsonl so scores can be compared across agent versions.</summary>
public sealed record EvalRunSummary(DateTime Date, string Mode, int Passed, int Total, Dictionary<string, int> Versions)
{
    public string Text =>
        $"{Date:yyyy-MM-dd HH:mm}  {Mode}  {Passed}/{Total} ({(Total == 0 ? 0 : Passed * 100 / Total)}%)  " +
        string.Join(", ", Versions.Select(v => $"{v.Key} v{v.Value}"));
}

public static class EvalChecks
{
    /// <summary>Runs a case's checks against the files the agents left in <paramref name="tools"/>' folder.</summary>
    public static List<EvalCheckResult> Run(string checks, AgentTools tools, bool? approved)
    {
        var results = new List<EvalCheckResult>();
        foreach (string raw in checks.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            string kind = parts[0].ToLowerInvariant();
            string file = parts.Length > 1 ? Path.GetFileName(parts[1]) : "";
            string path = Path.Combine(tools.Folder, file);
            string? text = file.Length > 0 && File.Exists(path) ? File.ReadAllText(path) : null;
            string arg = parts.Length > 2 ? parts[2] : "";

            results.Add(kind switch
            {
                "approved" => new(line, approved == true, approved == null ? "no review ran" : "the reviewer asked for changes"),
                "exists" => new(line, text != null, "file not created"),
                "compiles" when text == null => new(line, false, "file not created"),
                "compiles" => Compiles(line, tools, file),
                "contains" => new(line, text?.Contains(arg, StringComparison.Ordinal) == true, text == null ? "file not created" : $"'{arg}' not found"),
                "not_contains" => new(line, text != null && !text.Contains(arg, StringComparison.Ordinal), text == null ? "file not created" : $"'{arg}' found"),
                _ => new(line, false, "unknown check (use exists, compiles, contains, not_contains, approved)"),
            });
        }
        return results;
    }

    private static EvalCheckResult Compiles(string line, AgentTools tools, string file)
    {
        var result = tools.Check(file);
        bool ok = !result.IsError && result.Content.StartsWith("OK", StringComparison.Ordinal)
                  || result.Content.Contains("compiles, with warnings", StringComparison.Ordinal);
        return new(line, ok, result.Content.Split('\n').Skip(1).FirstOrDefault()?.Trim() ?? result.Content);
    }
}
