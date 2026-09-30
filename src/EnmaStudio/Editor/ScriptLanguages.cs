using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace EnmaStudio.Editor;

/// <summary>A language the editor opens. <see cref="Badge"/> / <see cref="Color"/> mark its files in the explorer and tabs.</summary>
public sealed record ScriptLanguage(string Name, string Extension, string Template, string Badge, string Color);

public static class ScriptLanguages
{
    private const string EnmaTemplate =
        """
        // F5 runs this file; Ctrl+Shift+B shows the Lua it compiles to.
        // Problems are underlined while you type (F8 jumps to the next one).
        let greeting = "hello"
        let ticks = 0

        log("{greeting} from {script.name}")

        on tick {
            ticks += 1 // every 50 ms
        }

        every 1 {
            log("{ticks} ticks so far")
        }

        """;

    private const string LuaTemplate =
        """
        -- F5 runs this file with the same API as Enma: log, on_tick, timer, time, json.
        log("hello from " .. script.name)

        local ticks = 0
        on_tick(function()
            ticks = ticks + 1
        end)

        """;

    public static IReadOnlyList<ScriptLanguage> All { get; } =
    [
        new("Enma", ".enma", EnmaTemplate, "ENMA", "#E0457B"),
        new("Lua",  ".lua",  LuaTemplate,  "LUA",  "#4A9BE8"),
    ];

    public static ScriptLanguage Default => All[0];

    public static ScriptLanguage? ForPath(string path) =>
        All.FirstOrDefault(l => string.Equals(l.Extension, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));

    private static bool _registered;

    /// <summary>Loads the embedded .xshd definitions (Editor/Highlighting) into AvalonEdit.</summary>
    public static void RegisterHighlighting()
    {
        if (_registered) return;
        _registered = true;

        var assembly = typeof(ScriptLanguages).Assembly;
        foreach (var language in All)
        {
            string resource = $"EnmaStudio.Editor.Highlighting.{language.Name}.xshd";
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing embedded resource {resource}.");
            using var reader = XmlReader.Create(stream);
            var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(language.Name, [language.Extension], definition);
        }
    }

    public static IHighlightingDefinition? Highlighting(ScriptLanguage? language) =>
        language is null ? null : HighlightingManager.Instance.GetDefinition(language.Name);
}
