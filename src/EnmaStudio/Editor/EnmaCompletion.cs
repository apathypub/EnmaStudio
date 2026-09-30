using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Enma;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace EnmaStudio.Editor;

/// <summary>
/// Code hints: keywords, the runtime API, members after "timer." / "time." / "s:" etc., and names
/// already used in the file. Opens on Ctrl+Space, after "." or ":" following a known table, and
/// after typing the first two letters of a name.
/// </summary>
public sealed partial class EnmaCompletion
{
    private static readonly string[] Keywords =
    [
        "let", "const", "fn", "return", "if", "elif", "else", "while", "for", "in", "step", "break", "continue",
        "true", "false", "nil", "and", "or", "not", "class", "on", "every", "after", "self",
    ];

    private static readonly Dictionary<string, (string Name, string Help)[]> Members = new()
    {
        ["timer"] = [("after", "timer.after(seconds, fn) -> t   run fn once"), ("every", "timer.every(seconds, fn) -> t   run fn repeatedly")],
        ["time"] = [("ms", "time.ms()   milliseconds since start"), ("now", "time.now()   unix seconds"),
                    ("format", "time.format(\"HH:mm:ss\")   .NET date pattern")],
        ["json"] = [("encode", "json.encode(table) -> text"), ("decode", "json.decode(text) -> table")],
        ["script"] = [("name", "script.name   this program's name")],
        ["math"] = [("clamp", "math.clamp(x, min, max)"), ("lerp", "math.lerp(a, b, t)"), ("round", "math.round(x, decimals)"),
                    ("floor", "math.floor(x)"), ("ceil", "math.ceil(x)"), ("abs", "math.abs(x)"), ("max", "math.max(...)"),
                    ("min", "math.min(...)"), ("random", "math.random([m [, n]])"), ("sqrt", "math.sqrt(x)"), ("pi", "math.pi")],
        ["string"] = [("format", "string.format(fmt, ...)"), ("rep", "string.rep(s, n)"), ("sub", "string.sub(s, i [, j])"),
                      ("upper", "string.upper(s)"), ("lower", "string.lower(s)"), ("len", "string.len(s)")],
        ["table"] = [("insert", "table.insert(list, [pos,] value)"), ("remove", "table.remove(list [, pos])"),
                     ("concat", "table.concat(list [, sep])"), ("sort", "table.sort(list [, less])"), ("unpack", "table.unpack(list)")],
    };

    // Methods offered after "value:" (strings and timer handles are the common receivers).
    private static readonly (string Name, string Help)[] MethodMembers =
    [
        ("split", "s:split(sep) -> list"), ("trim", "s:trim()"), ("starts_with", "s:starts_with(x)"), ("ends_with", "s:ends_with(x)"),
        ("contains", "s:contains(x)"), ("upper", "s:upper()"), ("lower", "s:lower()"), ("sub", "s:sub(i [, j])"),
        ("format", "s:format(...)"), ("cancel", "t:cancel()   stop a timer"), ("active", "t:active()   is the timer still pending"),
    ];

    private static readonly Dictionary<string, string> GlobalHelp = new()
    {
        ["log"] = "log(...)   write to the Output panel",
        ["clock"] = "clock()   current time as HH:mm:ss",
        ["on_tick"] = "on_tick(fn)   fn runs every 50 ms",
        ["on_unload"] = "on_unload(fn)   fn runs when the program stops",
    };

    private readonly TextEditor _editor;
    private readonly FrameworkElement _resources;
    private CompletionWindow? _window;

    public EnmaCompletion(TextEditor editor, FrameworkElement resources)
    {
        _editor = editor;
        _resources = resources;
        editor.TextArea.TextEntered += TextEntered;
    }

    public bool IsOpen => _window != null;

    public void Show() => Open(explicitRequest: true);

    private void TextEntered(object sender, TextCompositionEventArgs e)
    {
        if (_window != null || e.Text.Length != 1) return;
        char c = e.Text[0];
        if (c is '.' or ':' || char.IsLetter(c) || c == '_') Open(explicitRequest: false);
    }

    private void Open(bool explicitRequest)
    {
        var document = _editor.Document;
        if (document == null || _window != null) return;
        int caret = _editor.CaretOffset;

        int start = caret;
        while (start > 0 && IsNameChar(document.GetCharAt(start - 1))) start--;
        string prefix = document.GetText(start, caret - start);

        char before = start > 0 ? document.GetCharAt(start - 1) : '\0';
        List<CompletionItem> items;
        if (before is '.' or ':')
        {
            int ownerEnd = start - 1, ownerStart = ownerEnd;
            while (ownerStart > 0 && IsNameChar(document.GetCharAt(ownerStart - 1))) ownerStart--;
            string owner = document.GetText(ownerStart, ownerEnd - ownerStart);
            if (before == '.' && Members.TryGetValue(owner, out var members))
                items = members.Select(m => new CompletionItem(m.Name, m.Help, 2)).ToList();
            else if (before == ':' || explicitRequest)
                items = MethodMembers.Select(m => new CompletionItem(m.Name, m.Help, 1)).ToList();
            else
                return;
        }
        else
        {
            // Only offer names after two letters when typing; Ctrl+Space shows everything.
            if (!explicitRequest && prefix.Length < 2) return;
            if (InStringOrComment(document, start)) return;
            items = [.. Keywords.Select(k => new CompletionItem(k, "keyword", 1)),
                     .. EnmaCompiler.KnownGlobals.Select(g => new CompletionItem(g, GlobalHelp.GetValueOrDefault(g, "global"), 2)),
                     .. NamesInDocument(document.Text, prefix)
                         .Where(n => !Keywords.Contains(n) && !EnmaCompiler.KnownGlobals.Contains(n))
                         .Select(n => new CompletionItem(n, "in this file", 3))];
        }

        if (!explicitRequest && prefix.Length > 0 && !items.Any(i => i.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return;

        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = start,
            EndOffset = caret,
            Background = (Brush)_resources.FindResource("Ed.Field"),
            BorderBrush = (Brush)_resources.FindResource("Ed.Line"),
            Foreground = (Brush)_resources.FindResource("Ui.Text"),
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = false,
            SizeToContent = SizeToContent.Height,
            Width = 300,
        };
        var list = window.CompletionList.ListBox;
        list.Background = window.Background;
        list.Foreground = window.Foreground;
        list.BorderThickness = new Thickness(0);
        if (_resources.TryFindResource("CompletionRow") is Style row) list.ItemContainerStyle = row;

        foreach (var item in items.GroupBy(i => i.Text).Select(g => g.First()).OrderBy(i => i.Text, StringComparer.Ordinal))
            window.CompletionList.CompletionData.Add(item);
        if (prefix.Length > 0) window.CompletionList.SelectItem(prefix);

        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Rough check so typing inside "text" or // comments doesn't pop the list.</summary>
    private static bool InStringOrComment(TextDocument document, int offset)
    {
        var line = document.GetLineByOffset(offset);
        string text = document.GetText(line.Offset, offset - line.Offset);
        int comment = text.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0) return true;
        return text.Count(c => c == '"') % 2 == 1 && !text.Contains('{');
    }

    private static IEnumerable<string> NamesInDocument(string text, string prefix) =>
        NameRegex().Matches(text).Select(m => m.Value).Where(n => n.Length > 2 && n != prefix).Distinct();

    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex NameRegex();

    private sealed class CompletionItem(string text, string description, double priority) : ICompletionData
    {
        public ImageSource? Image => null;
        public string Text { get; } = text;
        public object Content => Text;
        public object Description { get; } = description;
        public double Priority { get; } = priority;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Text);
    }
}
