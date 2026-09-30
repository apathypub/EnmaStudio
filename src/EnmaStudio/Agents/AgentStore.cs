using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnmaStudio.Agents;

/// <summary>
/// The team's agent profiles and eval cases, saved as JSON in %APPDATA%\EnmaStudio\agents.
/// Missing or unreadable files fall back to the built-in defaults.
/// </summary>
public static class AgentStore
{
    public static string Folder { get; } = Path.Combine(AppPaths.Data, "agents");
    private static string ProfilesPath => Path.Combine(Folder, "profiles.json");
    private static string EvalsPath => Path.Combine(Folder, "evals.json");
    public static string HistoryPath => Path.Combine(Folder, "eval_history.jsonl");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ObservableCollection<AgentProfile> LoadProfiles()
    {
        var loaded = Read<List<AgentProfile>>(ProfilesPath);
        return loaded is { Count: > 0 } ? [.. loaded] : [.. DefaultProfiles()];
    }

    public static bool SaveProfiles(IEnumerable<AgentProfile> profiles) => Write(ProfilesPath, profiles.ToList());

    public static ObservableCollection<EvalCase> LoadEvals()
    {
        var loaded = Read<List<EvalCase>>(EvalsPath);
        return loaded is { Count: > 0 } ? [.. loaded] : [.. DefaultEvals()];
    }

    public static bool SaveEvals(IEnumerable<EvalCase> cases) => Write(EvalsPath, cases.ToList());

    public static void AppendHistory(EvalRunSummary summary)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(HistoryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions(Json) { WriteIndented = false }) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static List<EvalRunSummary> LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return [];
            return File.ReadAllLines(HistoryPath)
                .Where(l => l.Trim().Length > 0)
                .Select(l =>
                {
                    try { return JsonSerializer.Deserialize<EvalRunSummary>(l, Json); }
                    catch (JsonException) { return null; }
                })
                .OfType<EvalRunSummary>()
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Writes a profile's examples as JSONL chat transcripts (a dataset for other tools); returns the path.</summary>
    public static string ExportDataset(AgentProfile profile)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"dataset_{profile.Name.ToLowerInvariant().Replace(' ', '_')}.jsonl");
        var lines = profile.Examples.Select(e => JsonSerializer.Serialize(new
        {
            messages = new object[]
            {
                new { role = "user", content = e.Task },
                new { role = "assistant", content = e.Output },
            },
            agent = profile.Name,
            version = profile.Version,
        }));
        File.WriteAllLines(path, lines);
        return path;
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static AgentProfile DefaultFor(AgentRole role) => DefaultProfiles().First(p => p.Role == role);

    public static List<AgentProfile> DefaultProfiles()
    {
        var profiles = DefaultProfilesRaw();
        // The raw strings below are wrapped for the source file; the instructions box wraps by itself.
        foreach (var p in profiles) p.Instructions = System.Text.RegularExpressions.Regex.Replace(p.Instructions.Trim(), @"\r?\n(?!\r?\n)", " ");
        return profiles;
    }

    private static List<AgentProfile> DefaultProfilesRaw() =>
    [
        new()
        {
            Id = "architect", Name = "Architect", Role = AgentRole.Planner, Color = "#5DA9E9", Effort = "medium",
            Instructions =
                """
                Turn the user's request into a short, concrete plan for the Coder. Look at existing files with
                view when the request is about them. Name the file(s) to create or change, the language (Enma
                unless the user asks for Lua or edits a .lua file), which runtime API calls to use, and edge
                cases. Keep it under 15 bullet points. Do not write the full program.
                """,
        },
        new()
        {
            Id = "coder", Name = "Coder", Role = AgentRole.Coder, Color = "#E0457B", Effort = "medium",
            Instructions =
                """
                Implement the plan in the workspace folder with the file tool. After writing or editing a file,
                call check_script on it and fix every error before you finish. When you get review feedback,
                fix each point in the files. Finish with 2-4 sentences: what changed and how to use it.
                """,
        },
        new()
        {
            Id = "reviewer", Name = "Reviewer", Role = AgentRole.Reviewer, Color = "#D7BA3D", Effort = "medium",
            Instructions =
                """
                Review the Coder's work against the request and the plan. View the files and run check_script
                on them. Look for compile errors, API misuse (functions that don't exist in the runtime), logic
                bugs, callbacks that could block, and anything the request asked for but is missing.
                Be specific: file, line, what to change. Don't nitpick style.
                End with exactly one line: "VERDICT: APPROVE" if it's ready, or "VERDICT: CHANGES" followed by
                the list of required fixes above it.
                """,
        },
    ];

    public static List<EvalCase> DefaultEvals() =>
    [
        new()
        {
            Name = "Clock",
            Prompt = "Create clock.enma that logs the current time (HH:mm:ss) every second for ten seconds, then stops its timer.",
            Checks = "exists clock.enma\ncompiles clock.enma\ncontains clock.enma time.format\ncontains clock.enma cancel",
        },
        new()
        {
            Name = "Word count",
            Prompt = "Create words.enma with a function count_words(text) that returns a map of word -> count (lowercase, split on spaces), and log the counts for a sample sentence.",
            Checks = "exists words.enma\ncompiles words.enma\ncontains words.enma fn count_words",
        },
        new()
        {
            Name = "Class usage",
            Prompt = "Create cooldown.enma with a Cooldown class (init(seconds), ready(), trigger()) using time.ms(), and a tick handler that logs \"go\" whenever the cooldown is ready, triggering it again.",
            Checks = "exists cooldown.enma\ncompiles cooldown.enma\ncontains cooldown.enma class Cooldown\ncontains cooldown.enma on tick",
        },
    ];
}
