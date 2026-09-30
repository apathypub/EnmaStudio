using System.IO;
using System.Text;
using System.Text.Json;

namespace EnmaStudio.Editor;

/// <summary>Outcome of one tool call: what goes back to the model, and the row shown in the Team timeline.</summary>
public sealed record ToolResult(string Content, bool IsError, string Action, string? FilePath);

/// <summary>
/// Runs the Anthropic text-editor tool (view / create / str_replace / insert) inside the workspace folder.
/// Paths come from the model, so they are reduced to a bare file name with an .enma or .lua extension;
/// nothing outside the folder can be touched. Changes go through the editor's open document
/// (Ctrl+Z undoes them) and are saved to disk.
/// </summary>
public sealed class FileTools(string folder, Func<string, EditorDocument?> findOpen, Func<string, EditorDocument?> open)
{
    /// <summary>The workspace the tool works in (changes when the user picks another folder).</summary>
    public string Folder { get; set; } = folder;

    public ToolResult Execute(IReadOnlyDictionary<string, JsonElement> input)
    {
        string command = Str(input, "command");
        string path = Str(input, "path");
        try
        {
            if (IsFolder(path))
                return command == "view"
                    ? ListFiles()
                    : Fail("Give a file name such as clock.enma; the folder itself can't be edited.");

            if (Resolve(path) is not { } file)
                return Fail($"'{path}' isn't allowed. Use a plain file name ending in .enma or .lua.");

            return command switch
            {
                "view" => View(file, input),
                "create" => Create(file, Str(input, "file_text")),
                "str_replace" => Replace(file, Str(input, "old_str"), Str(input, "new_str")),
                "insert" => Insert(file, input),
                _ => Fail($"Unknown command '{command}'."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or FormatException or KeyNotFoundException)
        {
            return Fail(ex.Message);
        }
    }

    private static bool IsFolder(string path) =>
        path.Replace('\\', '/').Trim().Trim('/') is "" or "." or "scripts";

    /// <summary>Full path of a file directly in the workspace folder, or null when the name isn't allowed.</summary>
    private string? Resolve(string path)
    {
        string name = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
        if (name is "" or "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        if (ScriptLanguages.ForPath(name) == null) return null;

        string root = Path.GetFullPath(Folder);
        string full = Path.GetFullPath(Path.Combine(root, name));
        return string.Equals(Path.GetDirectoryName(full), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    private ToolResult ListFiles()
    {
        var names = Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder).Select(Path.GetFileName).OfType<string>()
                .Where(n => ScriptLanguages.ForPath(n) != null).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        string content = names.Count == 0 ? "The workspace folder is empty." : "Files in the workspace folder:\n" + string.Join('\n', names);
        return new ToolResult(content, false, "Listed scripts", null);
    }

    private ToolResult View(string file, IReadOnlyDictionary<string, JsonElement> input)
    {
        string name = Path.GetFileName(file);
        string? text = findOpen(file)?.Document.Text ?? (File.Exists(file) ? File.ReadAllText(file) : null);
        if (text == null) return Fail($"{name} doesn't exist.");

        var lines = text.Replace("\r\n", "\n").Split('\n');
        int from = 1, to = lines.Length;
        if (input.TryGetValue("view_range", out var range) && range.ValueKind == JsonValueKind.Array && range.GetArrayLength() == 2)
        {
            from = Math.Clamp(range[0].GetInt32(), 1, lines.Length);
            int end = range[1].GetInt32();
            to = end < 0 ? lines.Length : Math.Clamp(end, from, lines.Length);
        }

        var numbered = new StringBuilder();
        for (int i = from; i <= to; i++) numbered.Append(i).Append('\t').Append(lines[i - 1]).Append('\n');
        return new ToolResult(numbered.ToString(), false, $"Read {name}", file);
    }

    private ToolResult Create(string file, string text)
    {
        string name = Path.GetFileName(file);
        bool existed = File.Exists(file);
        if (!existed)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(file, "");
        }

        var doc = OpenOrThrow(file);
        doc.Document.Text = text;
        doc.Save();
        return new ToolResult(existed ? $"Overwrote {name}." : $"Created {name}.", false,
                                existed ? $"Rewrote {name}" : $"Created {name}", file);
    }

    private ToolResult Replace(string file, string oldText, string newText)
    {
        string name = Path.GetFileName(file);
        if (!File.Exists(file)) return Fail($"{name} doesn't exist.");
        if (oldText.Length == 0) return Fail("old_str is empty.");

        var doc = OpenOrThrow(file);
        string text = doc.Document.Text;
        // The model writes "\n"; files typed in the editor use "\r\n".
        if (text.Contains("\r\n") && !oldText.Contains("\r\n"))
        {
            oldText = oldText.Replace("\n", "\r\n");
            newText = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        int first = text.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0) return Fail($"old_str wasn't found in {name}. View the file and copy the exact text.");
        if (text.IndexOf(oldText, first + 1, StringComparison.Ordinal) >= 0)
            return Fail($"old_str appears more than once in {name}; include more surrounding lines so it is unique.");

        doc.Document.Replace(first, oldText.Length, newText);
        doc.Save();
        return new ToolResult($"Edited {name}.", false, $"Edited {name}", file);
    }

    private ToolResult Insert(string file, IReadOnlyDictionary<string, JsonElement> input)
    {
        string name = Path.GetFileName(file);
        if (!File.Exists(file)) return Fail($"{name} doesn't exist.");

        var doc = OpenOrThrow(file);
        int line = input["insert_line"].GetInt32();
        if (line < 0 || line > doc.Document.LineCount) return Fail($"insert_line must be between 0 and {doc.Document.LineCount}.");

        string newline = doc.Document.Text.Contains("\r\n") ? "\r\n" : "\n";
        string insert = Str(input, "insert_text").Replace("\r\n", "\n").Replace("\n", newline);
        if (line == 0) doc.Document.Insert(0, insert + newline);
        else doc.Document.Insert(doc.Document.GetLineByNumber(line).EndOffset, newline + insert);
        doc.Save();
        return new ToolResult($"Inserted text after line {line} of {name}.", false, $"Edited {name}", file);
    }

    private EditorDocument OpenOrThrow(string file) =>
        open(file) ?? throw new IOException($"Couldn't open {Path.GetFileName(file)} in the editor.");

    private static ToolResult Fail(string message) => new(message, true, message, null);

    private static string Str(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
