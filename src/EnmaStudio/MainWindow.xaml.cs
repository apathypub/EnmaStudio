using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Enma;
using EnmaStudio.Editor;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;

namespace EnmaStudio;

/// <summary>
/// The editor: workspace explorer, tabs, syntax highlighting, find/replace, Run/Stop with the built-in
/// Enma runtime, and the Studio panel (see MainWindow.Studio.cs).
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<EditorDocument> _documents = [];

    private readonly SearchHighlighter _highlighter = new();
    private readonly EnmaCompletion _completion;
    private List<ISearchResult> _matches = [];
    private ISearchStrategy? _findStrategy;

    private string _workspace = AppPaths.LoadWorkspace();
    private readonly FileTools _fileTools;
    private string? _apiKey = KeyStore.Load();

    // Running programs by file path; ticked every 50 ms.
    private readonly Dictionary<string, EnmaRuntime> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _runTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    public MainWindow()
    {
        ScriptLanguages.RegisterHighlighting();
        InitializeComponent();

        AppPaths.SeedSamples(_workspace);
        TabList.ItemsSource = _documents;
        OutputLines.ItemsSource = OutputLog.Lines;
        _fileTools = new FileTools(_workspace, FindOpen, OpenForAgent);
        Code.TextArea.TextView.BackgroundRenderers.Add(_highlighter);
        _completion = new EnmaCompletion(Code, this);
        Code.TextChanged += (_, _) =>
        {
            if (FindBar.IsVisible) UpdateMatches();
        };
        StyleEditor();
        UpdateKeyPanel();
        InitStudio();

        _runTimer.Tick += (_, _) => TickPrograms();

        SourceInitialized += (_, _) => NativeMethods.DisableRoundedCorners(new WindowInteropHelper(this).Handle);
        StateChanged += (_, _) =>
        {
            bool maximized = WindowState == WindowState.Maximized;
            // A maximized WindowChrome window hangs past the screen edges by the resize border.
            Root.Margin = maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = maximized ? "" : "";
        };
        Activated += (_, _) => RefreshFiles();
        Code.TextArea.Caret.PositionChanged += (_, _) => UpdateCaret();
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            _runTimer.Stop();
            foreach (var runtime in _running.Values) runtime.Stop();
        };

        RefreshFiles();
        ShowDocument();
    }

    private EditorDocument? Current => TabList.SelectedItem as EditorDocument;

    private void StyleEditor()
    {
        var accent = ((SolidColorBrush)FindResource("Ui.Accent")).Color;
        var area = Code.TextArea;
        area.SelectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)));
        area.SelectionBorder = null;
        area.SelectionForeground = null;
        area.SelectionCornerRadius = 2;
        area.Caret.CaretBrush = (Brush)FindResource("Ui.Text");
        area.TextView.CurrentLineBackground = Frozen(new SolidColorBrush(Color.FromRgb(0x19, 0x19, 0x19)));
        area.TextView.CurrentLineBorder = Frozen(new Pen(Brushes.Transparent, 0));

        Code.Options.HighlightCurrentLine = true;
        Code.Options.ConvertTabsToSpaces = true;
        Code.Options.IndentationSize = 4;
        Code.Options.EnableHyperlinks = false;
        Code.Options.EnableEmailHyperlinks = false;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // ---------------- Files ----------------

    private void RefreshFiles()
    {
        string? selected = (FileList.SelectedItem as ScriptFile)?.Path;
        List<ScriptFile> files = Directory.Exists(_workspace)
            ? Directory.EnumerateFiles(_workspace)
                .Select(path => (path, language: ScriptLanguages.ForPath(path)))
                .Where(f => f.language != null)
                .Select(f => new ScriptFile(Path.GetFileName(f.path), f.path, f.language!, _running.ContainsKey(f.path)))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        FileList.ItemsSource = files;
        FileList.SelectedItem = files.FirstOrDefault(f => f.Path == selected);
        FileCount.Text = files.Count.ToString();
        WorkspaceName.Text = Path.GetFileName(_workspace.TrimEnd(Path.DirectorySeparatorChar)).ToUpperInvariant();
        WorkspaceName.ToolTip = _workspace;
    }

    private EditorDocument? FindOpen(string path) =>
        _documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens (or switches to) a file's tab; null if it couldn't be read.</summary>
    private EditorDocument? OpenFile(string path)
    {
        var doc = FindOpen(path);
        if (doc == null)
        {
            try
            {
                doc = new EditorDocument(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus($"Can't open {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
            var opened = doc;
            opened.PropertyChanged += (_, _) =>
            {
                if (opened == Current) UpdateState();
            };
            _documents.Add(opened);
        }
        TabList.SelectedItem = doc;
        return doc;
    }

    // Agents edit through the file's tab so their changes show up live and Ctrl+Z undoes them.
    private EditorDocument? OpenForAgent(string path)
    {
        var doc = OpenFile(path);
        RefreshFiles();
        return doc;
    }

    private void CreateFile()
    {
        string name = NewName.Text.Trim();
        if (name.Length == 0)
        {
            NewName.Focus();
            return;
        }
        if (!Path.HasExtension(name)) name += ScriptLanguages.Default.Extension;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SetStatus("That file name isn't allowed.");
            return;
        }
        if (ScriptLanguages.ForPath(name) is not { } language)
        {
            SetStatus("Use .enma or .lua.");
            return;
        }

        string path = Path.Combine(_workspace, name);
        if (!File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(_workspace);
                File.WriteAllText(path, language.Template);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus($"Can't create {name}: {ex.Message}");
                return;
            }
            SetStatus($"Created {name}");
        }

        NewName.Clear();
        RefreshFiles();
        OpenFile(path);
    }

    private void DeleteSelected()
    {
        if (FileList.SelectedItem is not ScriptFile file) return;
        if (ConfirmDialog.Ask(this, "Delete file", $"Delete {file.Name}? This can't be undone.", "Delete", "Cancel") != true)
            return;

        if (FindOpen(file.Path) is { } open) RemoveTab(open);
        StopProgram(file.Path);

        try
        {
            File.Delete(file.Path);
            SetStatus($"Deleted {file.Name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Can't delete {file.Name}: {ex.Message}");
        }
        RefreshFiles();
    }

    private void ChooseWorkspace()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the workspace folder", InitialDirectory = _workspace };
        if (dialog.ShowDialog(this) != true) return;
        if (_documents.Any(d => d.IsDirty) && !SaveAll()) return;

        foreach (var runtime in _running.Values) runtime.Stop();
        _running.Clear();
        _documents.Clear();
        _workspace = dialog.FolderName;
        AppPaths.SaveWorkspace(_workspace);
        _fileTools.Folder = _workspace;
        RefreshFiles();
        SetStatus($"Workspace: {_workspace}");
    }

    // ---------------- Tabs ----------------

    private void ShowDocument()
    {
        var doc = Current;
        Code.Document = doc?.Document;
        Code.SyntaxHighlighting = ScriptLanguages.Highlighting(doc?.Language);
        Code.Visibility = doc == null ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = doc == null ? Visibility.Visible : Visibility.Collapsed;
        if (doc == null) FindBar.Visibility = Visibility.Collapsed;
        else if (FindBar.Visibility == Visibility.Visible) UpdateMatches();
        UpdateState();
        UpdateCaret();
        RefreshDiagnostics();
        if (doc != null) Code.Focus();
    }

    /// <summary>Closes a tab, asking about unsaved changes; false when the user cancelled.</summary>
    private bool CloseTab(EditorDocument doc)
    {
        if (doc.IsDirty)
        {
            var answer = ConfirmDialog.Ask(this, "Unsaved changes", $"Save changes to {doc.Name}?", "Save", "Don't save", "Cancel");
            if (answer == null || (answer == true && !Save(doc))) return false;
        }
        RemoveTab(doc);
        return true;
    }

    private void RemoveTab(EditorDocument doc)
    {
        int index = _documents.IndexOf(doc);
        bool wasCurrent = doc == Current;
        _documents.Remove(doc);
        if (wasCurrent && _documents.Count > 0) TabList.SelectedIndex = Math.Min(index, _documents.Count - 1);
    }

    private bool Save(EditorDocument doc)
    {
        try
        {
            doc.Save();
            SetStatus($"Saved {doc.Name}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Can't save {doc.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Saves every changed tab; false if any failed.</summary>
    private bool SaveAll() => _documents.Where(d => d.IsDirty).ToList().Count(d => !Save(d)) == 0;

    // ---------------- Run ----------------

    private void Run()
    {
        if (Current is not { } doc) return;
        if (doc.IsDirty && !Save(doc)) return;

        StopProgram(doc.Path, quiet: true);
        var runtime = new EnmaRuntime(doc.ScriptName, message => OutputLog.Write(message));
        string source = doc.Document.Text;
        bool started = doc.Language?.Extension == ".lua" ? runtime.StartLua(source) : runtime.Start(source);
        if (!started)
        {
            SetStatus($"{doc.Name} failed, see Output");
        }
        else if (runtime.HasWork)
        {
            _running[doc.Path] = runtime;
            _runTimer.Start();
            OutputLog.Write($"[{doc.ScriptName}] running", LogLevel.Success);
            SetStatus($"Running {doc.Name}");
        }
        else
        {
            runtime.Stop();
            OutputLog.Write($"[{doc.ScriptName}] finished", LogLevel.Success);
            SetStatus($"Ran {doc.Name}");
        }
        RefreshFiles();
        UpdateState();
    }

    private void StopProgram(string path, bool quiet = false)
    {
        if (!_running.Remove(path, out var runtime)) return;
        runtime.Stop();
        if (!quiet) OutputLog.Write($"[{runtime.Name}] stopped", LogLevel.Success);
        if (_running.Count == 0) _runTimer.Stop();
        RefreshFiles();
        UpdateState();
    }

    private void Stop()
    {
        if (Current is not { } doc || !_running.ContainsKey(doc.Path)) return;
        StopProgram(doc.Path);
        SetStatus($"Stopped {doc.Name}");
    }

    private void TickPrograms()
    {
        foreach (var (path, runtime) in _running.ToArray())
        {
            runtime.Tick();
            if (!runtime.IsRunning)
            {
                // Stopped itself with an error (already in the Output).
                _running.Remove(path);
            }
            else if (!runtime.HasWork)
            {
                runtime.Stop();
                _running.Remove(path);
                OutputLog.Write($"[{runtime.Name}] finished", LogLevel.Success);
            }
            else continue;
            RefreshFiles();
            UpdateState();
        }
        if (_running.Count == 0) _runTimer.Stop();
    }

    // ---------------- Find / replace ----------------

    private void OpenFind(bool replace)
    {
        if (Current == null) return;
        FindBar.Visibility = Visibility.Visible;
        if (replace) SetReplaceVisible(true);
        if (Code.SelectionLength > 0 && !Code.SelectedText.Contains('\n')) FindBox.Text = Code.SelectedText;

        if (replace && FindBox.Text.Length > 0) ReplaceBox.Focus();
        else
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }
        UpdateMatches();
    }

    private void SetReplaceVisible(bool visible)
    {
        ReplaceRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ReplaceToggle.Content = visible ? "" : "";
    }

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _matches = [];
        _findStrategy = null;
        Repaint(null);
        Code.Focus();
    }

    /// <summary>Searches the whole document again (after typing, an option change or an edit) and repaints.</summary>
    private void UpdateMatches()
    {
        _matches = [];
        _findStrategy = null;
        FindStatus.Foreground = (Brush)FindResource("Ui.TextMuted");
        if (Current != null && FindBox.Text.Length > 0)
        {
            try
            {
                _findStrategy = SearchStrategyFactory.Create(FindBox.Text, ignoreCase: MatchCase.IsChecked != true,
                    matchWholeWords: WholeWord.IsChecked == true, UseRegex.IsChecked == true ? SearchMode.RegEx : SearchMode.Normal);
                _matches = _findStrategy.FindAll(Code.Document, 0, Code.Document.TextLength).ToList();
            }
            catch (SearchPatternException)
            {
                Repaint(null);
                FindStatus.Text = "Bad regex";
                FindStatus.Foreground = (Brush)FindResource("Ed.Red");
                return;
            }
        }
        Repaint(_matches.FirstOrDefault(m => m.Offset == Code.SelectionStart && m.Length == Code.SelectionLength));
    }

    private void Repaint(ISearchResult? current)
    {
        _highlighter.Matches = _matches;
        _highlighter.Current = current;
        int index = current == null ? -1 : _matches.IndexOf(current);
        FindStatus.Text = FindBox.Text.Length == 0 ? ""
            : _matches.Count == 0 ? "No results"
            : index >= 0 ? $"{index + 1} of {_matches.Count}"
            : $"{_matches.Count} found";
        Code.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>Selects the next (or previous) match after the selection, wrapping around the document.</summary>
    private void FindNext(bool backwards, bool fromSelectionStart = false)
    {
        if (_matches.Count == 0) return;
        int start = Code.SelectionStart;
        int end = fromSelectionStart ? start : start + Code.SelectionLength;
        var match = backwards
            ? _matches.LastOrDefault(m => m.Offset < start) ?? _matches[^1]
            : _matches.FirstOrDefault(m => m.Offset >= end) ?? _matches[0];

        Code.Select(match.Offset, match.Length);
        var location = Code.Document.GetLocation(match.Offset);
        Code.ScrollTo(location.Line, location.Column);
        Repaint(match);
    }

    private void ReplaceCurrent()
    {
        if (_findStrategy == null) return;
        var current = _matches.FirstOrDefault(m => m.Offset == Code.SelectionStart && m.Length == Code.SelectionLength);
        if (current == null)
        {
            FindNext(backwards: false);
            return;
        }

        string replacement = current.ReplaceWith(ReplaceBox.Text);
        Code.Document.Replace(current.Offset, current.Length, replacement);
        Code.Select(current.Offset + replacement.Length, 0);
        UpdateMatches();
        FindNext(backwards: false);
    }

    private void ReplaceAll()
    {
        if (_findStrategy == null || _matches.Count == 0) return;
        int count = _matches.Count;
        using (Code.Document.RunUpdate()) // a single Ctrl+Z undoes all of it
        {
            foreach (var match in _matches.OrderByDescending(m => m.Offset).ToList())
                Code.Document.Replace(match.Offset, match.Length, match.ReplaceWith(ReplaceBox.Text));
        }
        UpdateMatches();
        SetStatus($"Replaced {count} match{(count == 1 ? "" : "es")}");
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateMatches();
        FindNext(backwards: false, fromSelectionStart: true); // jump to the first match as you type
    }

    private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        FindNext(backwards: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void ReplaceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) ReplaceAll();
        else ReplaceCurrent();
    }

    private void FindOption_Click(object sender, RoutedEventArgs e) => UpdateMatches();

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext(backwards: false);

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindNext(backwards: true);

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    private void ReplaceToggle_Click(object sender, RoutedEventArgs e) =>
        SetReplaceVisible(ReplaceRow.Visibility != Visibility.Visible);

    private void Replace_Click(object sender, RoutedEventArgs e) => ReplaceCurrent();

    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => ReplaceAll();

    // ---------------- API key ----------------

    private void UpdateKeyPanel()
    {
        KeyPanel.Visibility = _apiKey == null ? Visibility.Visible : Visibility.Collapsed;
        RemoveKeyButton.Visibility = _apiKey == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void KeyToggle_Click(object sender, RoutedEventArgs e)
    {
        KeyPanel.Visibility = KeyPanel.Visibility == Visibility.Visible && _apiKey != null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (KeyPanel.Visibility == Visibility.Visible) KeyBox.Focus();
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        string key = KeyBox.Password.Trim();
        if (key.Length == 0) return;
        if (!KeyStore.Save(key)) SetStatus("Couldn't save the key to disk; it works until Enma Studio closes.");
        _apiKey = key;
        KeyBox.Clear();
        UpdateKeyPanel();
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        KeyStore.Clear();
        _apiKey = null;
        UpdateKeyPanel();
    }

    /// <summary>The request plus the open file / selection, for the agents.</summary>
    private string BuildPrompt(string request, bool includeFile)
    {
        var prompt = new StringBuilder(request);
        if (includeFile && Current is { } doc)
        {
            string fence = doc.Language?.Name.ToLowerInvariant() ?? "";
            prompt.Append($"\n\nOpen file: {doc.Name}\n```{fence}\n{doc.Document.Text}\n```");
            if (Code.SelectionLength > 0) prompt.Append($"\n\nSelected code:\n```{fence}\n{Code.SelectedText}\n```");
        }
        return prompt.ToString();
    }

    // ---------------- State ----------------

    private void UpdateState()
    {
        var doc = Current;
        bool running = doc != null && _running.ContainsKey(doc.Path);

        SaveButton.IsEnabled = doc != null;
        RunButton.IsEnabled = doc != null;
        StopButton.IsEnabled = running;
        RunButton.ToolTip = running ? "Restart (F5)" : "Run (F5)";
        RunningText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        LanguageText.Text = doc?.Language is { } language
            ? language.Extension == ".enma" ? $"Enma {EnmaCompiler.Version} → Lua 5.2" : "Lua 5.2"
            : "";
        TitleFile.Text = doc == null ? "" : $"{doc.Name}{(doc.IsDirty ? "  ●" : "")}";
    }

    private void UpdateCaret()
    {
        var caret = Code.TextArea.Caret;
        CaretText.Text = Current == null ? "" : $"Ln {caret.Line}, Col {caret.Column}";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ---------------- Events ----------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool handled = true;

        switch (key)
        {
            case Key.S when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                SaveAll();
                break;
            case Key.S when modifiers == ModifierKeys.Control:
                if (Current is { } doc) Save(doc);
                break;
            case Key.N when modifiers == ModifierKeys.Control:
                NewName.Focus();
                NewName.SelectAll();
                break;
            case Key.W when modifiers == ModifierKeys.Control:
                if (Current is { } open) CloseTab(open);
                break;
            case Key.E when modifiers == ModifierKeys.Control:
                ToggleStudio();
                break;
            case Key.B when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                ShowStudio(TabLua);
                break;
            case Key.F8 when modifiers == ModifierKeys.None:
                NextProblem();
                break;
            case Key.F when modifiers == ModifierKeys.Control:
                OpenFind(replace: false);
                break;
            case Key.H when modifiers == ModifierKeys.Control:
                OpenFind(replace: true);
                break;
            case Key.F3 when FindBar.IsVisible:
                FindNext(backwards: modifiers == ModifierKeys.Shift);
                break;
            case Key.Space when modifiers == ModifierKeys.Control && Code.TextArea.IsKeyboardFocusWithin:
                _completion.Show();
                break;
            case Key.Escape when FindBar.IsVisible && !_completion.IsOpen:
                CloseFind();
                break;
            case Key.F5 when modifiers == ModifierKeys.Shift:
                Stop();
                break;
            case Key.F5 when modifiers == ModifierKeys.None:
                Run();
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        var dirty = _documents.Where(d => d.IsDirty).ToList();
        if (dirty.Count == 0) return;

        string names = string.Join(", ", dirty.Select(d => d.Name));
        var answer = ConfirmDialog.Ask(this, "Unsaved changes", $"Save changes to {names}?", "Save all", "Don't save", "Cancel");
        if (answer == null || (answer == true && !SaveAll())) e.Cancel = true;
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDocument();

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EditorDocument doc) CloseTab(doc);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (NewName.Text.Trim().Length > 0) CreateFile();
        else NewName.Focus();
    }

    private void NewName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CreateFile();
        else if (e.Key == Key.Escape && Current != null) Code.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } doc) Save(doc);
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e) => SaveAll();

    private void Run_Click(object sender, RoutedEventArgs e) => Run();

    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();

    private void Find_Click(object sender, RoutedEventArgs e) => OpenFind(replace: false);

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is ScriptFile file) OpenFile(file.Path);
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FileList.SelectedItem is ScriptFile file) OpenFile(file.Path);
        else if (e.Key == Key.Delete) DeleteSelected();
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is ScriptFile file) OpenFile(file.Path);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshFiles();

    private void ChooseFolder_Click(object sender, RoutedEventArgs e) => ChooseWorkspace();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_workspace);
        Process.Start(new ProcessStartInfo(_workspace) { UseShellExecute = true });
    }

    private void TabDocs_Click(object sender, RoutedEventArgs e) => ShowStudio(TabDocs);

    private void ClearOutput_Click(object sender, RoutedEventArgs e) => OutputLog.Clear();

    private void Output_SizeChanged(object sender, SizeChangedEventArgs e) => OutputScroll.ScrollToBottom();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
