using System.ComponentModel;
using System.IO;
using ICSharpCode.AvalonEdit.Document;

namespace EnmaStudio.Editor;

/// <summary>A file in the workspace, as listed in the explorer.</summary>
public sealed record ScriptFile(string Name, string Path, ScriptLanguage Language, bool IsLoaded);

/// <summary>A file open in an editor tab. Each keeps its own text and undo history.</summary>
public sealed class EditorDocument : INotifyPropertyChanged
{
    public EditorDocument(string path)
    {
        Path = path;
        Language = ScriptLanguages.ForPath(path);
        Document = new TextDocument(File.ReadAllText(path));
        Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    /// <summary>File name without extension (the name scripts run under).</summary>
    public string ScriptName => System.IO.Path.GetFileNameWithoutExtension(Path);
    public ScriptLanguage? Language { get; }
    public TextDocument Document { get; }
    public bool IsDirty => !Document.UndoStack.IsOriginalFile;

    public void Save()
    {
        File.WriteAllText(Path, Document.Text);
        Document.UndoStack.MarkAsOriginalFile();
    }
}
