using System.IO;
using System.Text.Json;
using Anthropic.Models.Messages;
using Enma;
using MoonSharp.Interpreter;
using EnmaStudio.Editor;

namespace EnmaStudio.Agents;

/// <summary>
/// The tools an agent gets: the text-editor tool over the workspace folder (view-only for planners and
/// reviewers) and <c>check_script</c>, which compiles an .enma file or syntax-checks a .lua file.
/// </summary>
public sealed class AgentTools(FileTools files, string folder, Func<string, string?> readText)
{
    public const string EditorToolName = "str_replace_based_edit_tool";
    public const string CheckToolName = "check_script";

    public static readonly Tool CheckTool = new()
    {
        Name = CheckToolName,
        Description = "Compile an .enma script (or syntax-check a .lua script) in the workspace folder without running it. " +
                      "Returns the errors and warnings with line:column, or OK. Use it after every change.",
        InputSchema = new()
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "File name, e.g. clock.enma" }),
            },
            Required = ["path"],
        },
    };

    public string Folder => folder;

    public ToolResult Execute(string name, IReadOnlyDictionary<string, JsonElement> input, bool readOnly)
    {
        if (name == CheckToolName)
            return Check(input.TryGetValue("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "");
        if (name != EditorToolName)
            return new ToolResult($"Unknown tool '{name}'.", true, $"Unknown tool {name}", null);

        string command = input.TryGetValue("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        if (readOnly && command != "view")
            return new ToolResult("You can only view files; the Coder makes the changes.", true, "Blocked a write (read-only agent)", null);
        return files.Execute(input);
    }

    /// <summary>Compiles or syntax-checks one script; also used by the eval checks.</summary>
    public ToolResult Check(string path)
    {
        string name = Path.GetFileName(path.Replace('\\', '/'));
        string full = Path.Combine(folder, name);
        string? text = name.Length == 0 ? null : readText(full) ?? (File.Exists(full) ? File.ReadAllText(full) : null);
        if (text == null) return new ToolResult($"{name} doesn't exist.", true, $"Check: {name} not found", null);

        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext == ".enma")
        {
            var result = EnmaCompiler.Compile(text);
            string report = result.Diagnostics.Count == 0
                ? $"OK: {name} compiles with no warnings."
                : (result.Success ? $"{name} compiles, with warnings:\n" : $"{name} does NOT compile:\n")
                  + string.Join('\n', result.Diagnostics.Select(d => "  " + d));
            int errors = result.Errors.Count();
            return new ToolResult(report, false,
                errors > 0 ? $"Check {name}: {errors} error(s)" : $"Check {name}: OK{(result.Diagnostics.Count > 0 ? $" ({result.Diagnostics.Count} warning(s))" : "")}",
                full);
        }
        if (ext == ".lua")
        {
            try
            {
                new Script(CoreModules.Preset_SoftSandbox).LoadString(text, codeFriendlyName: name);
                return new ToolResult($"OK: {name} has no syntax errors (API use isn't checked).", false, $"Check {name}: OK", full);
            }
            catch (SyntaxErrorException ex)
            {
                return new ToolResult($"{name} has a syntax error: {ex.DecoratedMessage}", false, $"Check {name}: syntax error", full);
            }
        }
        return new ToolResult($"Only .enma and .lua files can be checked.", true, $"Check: can't check {name}", null);
    }
}
