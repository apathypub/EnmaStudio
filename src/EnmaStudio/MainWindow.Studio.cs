using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Anthropic;
using Anthropic.Exceptions;
using EnmaStudio.Agents;
using EnmaStudio.Editor;
using Enma;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace EnmaStudio;

/// <summary>
/// The Studio panel: live Enma diagnostics in the editor, and the side panel with the multi-agent team,
/// agent training (profiles, lessons, examples), evals, the compiled Lua and the language docs.
/// </summary>
public partial class MainWindow
{
    private readonly DiagnosticsRenderer _diagnosticsRenderer = new();
    private readonly DispatcherTimer _diagnosticsTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private EnmaResult? _lastCompile;
    private ToolTip? _diagnosticTip;

    private readonly ObservableCollection<AgentProfile> _profiles = AgentStore.LoadProfiles();
    private readonly ObservableCollection<EvalCase> _evalCases = AgentStore.LoadEvals();
    private readonly ObservableCollection<TeamEntry> _teamEntries = [];
    private readonly ObservableCollection<EvalResult> _evalResults = [];
    // Last saved state of each profile (without the version), to tell which ones changed on Save.
    private readonly Dictionary<string, string> _savedProfiles = [];
    private CancellationTokenSource? _teamCts;
    private CancellationTokenSource? _evalCts;
    private AnthropicClient? _studioClient;
    private string? _studioClientKey;

    private void InitStudio()
    {
        Code.TextArea.TextView.BackgroundRenderers.Add(_diagnosticsRenderer);
        _diagnosticsTimer.Tick += (_, _) =>
        {
            _diagnosticsTimer.Stop();
            RefreshDiagnostics();
        };
        Code.TextChanged += (_, _) =>
        {
            _diagnosticsTimer.Stop();
            _diagnosticsTimer.Start();
        };
        Code.MouseHover += Code_MouseHover;
        Code.MouseHoverStopped += (_, _) =>
        {
            if (_diagnosticTip != null) _diagnosticTip.IsOpen = false;
        };

        StudioVersionText.Text = $"Enma {EnmaCompiler.Version}";
        DocsText.Text = EnmaDocs.Reference + "\nExample\n\n" + EnmaDocs.Example;
        LuaPreview.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("Lua");
        LuaPreview.TextArea.TextView.CurrentLineBackground = Brushes.Transparent;

        TeamMessages.ItemsSource = _teamEntries;
        TrainAgents.ItemsSource = _profiles;
        TrainAgents.SelectedIndex = 0;
        EvalList.ItemsSource = _evalCases;
        EvalList.SelectedIndex = 0;
        EvalResults.ItemsSource = _evalResults;
        RefreshEvalHistory();

        foreach (var profile in _profiles)
        {
            _savedProfiles[profile.Id] = Snapshot(profile);
            profile.PropertyChanged += (_, _) => RefreshPipeline();
        }
        RefreshPipeline();

        Closed += (_, _) =>
        {
            _teamCts?.Cancel();
            _evalCts?.Cancel();
            _diagnosticsTimer.Stop();
        };
    }

    private AnthropicClient Client(string key)
    {
        if (_studioClient == null || _studioClientKey != key)
        {
            _studioClient = new AnthropicClient { ApiKey = key };
            _studioClientKey = key;
        }
        return _studioClient;
    }

    /// <summary>A user-facing message for an API failure; null when the exception isn't one.</summary>
    private static string? AiErrorText(Exception ex) => ex switch
    {
        OperationCanceledException => "Stopped.",
        AnthropicUnauthorizedException => "The API key was rejected. Change it with the key icon in the Studio panel.",
        AnthropicRateLimitException => "Rate limit reached. Wait a moment and try again.",
        AnthropicApiException api when api.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase) =>
            "Your Anthropic account has no API credits. Add some at console.anthropic.com (Plans & Billing).",
        AnthropicApiException api => $"API error: {api.Message}",
        AnthropicIOException or HttpRequestException => $"Can't reach the API: {ex.Message}",
        InvalidOperationException => ex.Message,
        _ => null,
    };

    // ---------------- Side panel ----------------

    /// <summary>Shows <paramref name="panel"/> (the Studio) in the right column, or closes it.</summary>
    private void ShowSide(FrameworkElement? panel, double width)
    {
        StudioPanel.Visibility = panel == StudioPanel ? Visibility.Visible : Visibility.Collapsed;
        bool open = panel != null;
        AiSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AiSplitterColumn.Width = new GridLength(open ? 1 : 0);
        AiColumn.MinWidth = open ? 300 : 0;
        AiColumn.Width = new GridLength(open ? width : 0);
    }

    private void ToggleStudio()
    {
        if (StudioPanel.Visibility == Visibility.Visible) ShowSide(null, 0);
        else ShowStudio(null);
    }

    private void ShowStudio(RadioButton? tab)
    {
        ShowSide(StudioPanel, 440);
        if (tab != null) tab.IsChecked = true;
        UpdateKeyPanel();
        if (TabTeam.IsChecked == true) TeamInput.Focus();
        if (TabLua.IsChecked == true) UpdateLuaPreview();
    }

    private void StudioTab_Checked(object sender, RoutedEventArgs e)
    {
        if (TeamView == null) return; // during InitializeComponent
        TeamView.Visibility = TabTeam.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TrainView.Visibility = TabTrain.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EvalsView.Visibility = TabEvals.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LuaView.Visibility = TabLua.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DocsView.Visibility = TabDocs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabLua.IsChecked == true) UpdateLuaPreview();
    }

    private void StudioToggle_Click(object sender, RoutedEventArgs e) => ToggleStudio();

    // ---------------- Enma diagnostics ----------------

    private static bool IsEnma(EditorDocument? doc) => doc?.Language?.Extension == ".enma";

    private void RefreshDiagnostics()
    {
        var doc = Current;
        if (!IsEnma(doc))
        {
            _lastCompile = null;
            _diagnosticsRenderer.Diagnostics = [];
            ProblemsButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            _lastCompile = EnmaCompiler.Compile(doc!.Document.Text);
            _diagnosticsRenderer.Diagnostics = _lastCompile.Diagnostics;
            ErrorCountText.Text = _lastCompile.Errors.Count().ToString();
            WarningCountText.Text = _lastCompile.Warnings.Count().ToString();
            ProblemsButton.Visibility = Visibility.Visible;
        }
        Code.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        if (LuaView.Visibility == Visibility.Visible && StudioPanel.Visibility == Visibility.Visible) UpdateLuaPreview();
    }

    private void Code_MouseHover(object sender, MouseEventArgs e)
    {
        if (_lastCompile is not { Diagnostics.Count: > 0 } || Code.Document == null) return;
        var position = Code.GetPositionFromPoint(e.GetPosition(Code));
        if (position == null) return;
        int offset = Code.Document.GetOffset(position.Value.Location);
        var hits = _lastCompile.Diagnostics.Where(d =>
        {
            var (start, length) = DiagnosticsRenderer.Range(Code.Document, d);
            return offset >= start && offset <= start + length;
        }).ToList();
        if (hits.Count == 0) return;

        _diagnosticTip ??= new ToolTip { PlacementTarget = Code, Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
        _diagnosticTip.Content = string.Join("\n", hits.Select(d => $"{(d.Severity == EnmaSeverity.Error ? "Error" : "Warning")}: {d.Message}"));
        _diagnosticTip.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Selects the next problem after the caret (wrapping around).</summary>
    private void NextProblem()
    {
        if (_lastCompile is not { Diagnostics.Count: > 0 } || Code.Document == null) return;
        var ordered = _lastCompile.Diagnostics
            .Select(d => (d, range: DiagnosticsRenderer.Range(Code.Document, d)))
            .OrderBy(x => x.range.Start).ToList();
        var next = ordered.FirstOrDefault(x => x.range.Start > Code.CaretOffset);
        if (next.d == null) next = ordered[0];
        Code.Select(next.range.Start, next.range.Length);
        Code.ScrollTo(next.d.Line, next.d.Column);
        Code.Focus();
        SetStatus($"{(next.d.Severity == EnmaSeverity.Error ? "Error" : "Warning")} {next.d.Line}:{next.d.Column}: {next.d.Message}");
    }

    private void Problems_Click(object sender, RoutedEventArgs e) => NextProblem();

    // ---------------- Lua preview ----------------

    private void UpdateLuaPreview()
    {
        var doc = Current;
        if (!IsEnma(doc))
        {
            LuaViewTitle.Text = "Open an .enma file to see the Lua it compiles to.";
            LuaPreview.Text = "";
            return;
        }
        _lastCompile ??= EnmaCompiler.Compile(doc!.Document.Text);
        double scroll = LuaPreview.VerticalOffset;
        if (_lastCompile.Lua is { } lua)
        {
            LuaViewTitle.Text = $"{doc!.Name} → Lua 5.2 (same line numbers)";
            LuaPreview.Text = lua;
        }
        else
        {
            LuaViewTitle.Text = $"{doc!.Name}: {_lastCompile.Errors.Count()} error(s)";
            LuaPreview.Text = "-- Fix these to see the Lua:\n" + string.Join("\n", _lastCompile.Errors.Select(d => "-- " + d));
        }
        LuaPreview.ScrollToVerticalOffset(scroll);
    }

    private void LuaCopy_Click(object sender, RoutedEventArgs e)
    {
        if (LuaPreview.Text.Length == 0) return;
        Clipboard.SetText(LuaPreview.Text);
        SetStatus("Copied the compiled Lua");
    }

    private void LuaExport_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } doc || !IsEnma(doc) || _lastCompile?.Lua is not { } lua)
        {
            SetStatus("Open an .enma file without errors to export its Lua.");
            return;
        }
        // A different name than the .enma, so both can't run as the same script.
        string path = Path.Combine(Path.GetDirectoryName(doc.Path)!, doc.ScriptName + "_compiled.lua");
        if (File.Exists(path) &&
            ConfirmDialog.Ask(this, "Export Lua", $"Replace {Path.GetFileName(path)}?", "Replace", "Cancel") != true)
            return;
        try
        {
            File.WriteAllText(path, $"-- Compiled from {doc.Name} by Enma {EnmaCompiler.Version}\n" + lua);
            SetStatus($"Exported {Path.GetFileName(path)}");
            RefreshFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Can't export: {ex.Message}");
        }
    }

    private void NewEnma_Click(object sender, RoutedEventArgs e)
    {
        string name = "script.enma";
        for (int i = 2; File.Exists(Path.Combine(_workspace, name)); i++) name = $"script_{i}.enma";
        NewName.Text = name;
        CreateFile();
    }

    // ---------------- Team ----------------

    private int ReviewRounds => int.TryParse(ReviewRoundsList?.SelectedItem as string, out int n) ? n : 2;

    private void RefreshPipeline()
    {
        if (PipelineList == null || TeamCostHint == null) return; // during InitializeComponent
        var enabled = _profiles.Where(p => p.Enabled).OrderBy(p => p.Role).ToList();
        PipelineList.ItemsSource = enabled.Select((p, i) => new PipelineItem(p.Name, p.Color,
            i == 0 ? "" : p.Role == AgentRole.Reviewer ? "⇄" : "→",
            $"{p.Role} · {p.Model} · effort {p.Effort} · v{p.Version} · {p.Lessons.Count} lessons, {p.Examples.Count} examples")).ToList();

        bool planner = enabled.Any(p => p.Role == AgentRole.Planner);
        bool reviewer = enabled.Any(p => p.Role == AgentRole.Reviewer) && ReviewRounds > 0;
        int turns = (planner ? 1 : 0) + 1 + (reviewer ? ReviewRounds * 2 - 1 : 0);
        TeamCostHint.Text = $"Enter to run · up to {turns} agent turns per task (each may use several tool steps), billed to your API key.";
    }

    private void ReviewRounds_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPipeline();

    private async void RunTeam(string task)
    {
        task = task.Trim();
        if (_teamCts != null || task.Length == 0) return;
        if (_apiKey is not { } key)
        {
            KeyPanel.Visibility = Visibility.Visible;
            return;
        }

        string message = BuildPrompt(task, TeamIncludeFile.IsChecked == true);
        _teamEntries.Add(new TeamEntry(TeamEventKind.Text, task, isUser: true));
        var tools = new AgentTools(_fileTools, _workspace, path => FindOpen(path)?.Document.Text);

        _teamCts = new CancellationTokenSource();
        TeamSendButton.Content = "";
        TeamSendButton.ToolTip = "Stop";
        try
        {
            var outcome = await TeamHarness.RunAsync(Client(key), _profiles.ToList(), tools, message, ReviewRounds, e =>
            {
                _teamEntries.Add(new TeamEntry(e.Kind, e.Text, e.Agent, e.FilePath, e.Prompt));
                if (e.Kind == TeamEventKind.Action) SetStatus($"{e.Agent?.Name}: {e.Text}");
            }, _teamCts.Token);
            SetStatus(outcome.Summary);
            RefreshDiagnostics();
        }
        catch (Exception ex) when (AiErrorText(ex) is { } text)
        {
            _teamEntries.Add(new TeamEntry(TeamEventKind.Error, text));
        }
        finally
        {
            _teamCts.Dispose();
            _teamCts = null;
            TeamSendButton.Content = "";
            TeamSendButton.ToolTip = "Run the team (Enter)";
        }
    }

    private void TeamSend_Click(object sender, RoutedEventArgs e)
    {
        if (_teamCts != null)
        {
            _teamCts.Cancel();
            return;
        }
        RunTeam(TeamInput.Text);
        TeamInput.Clear();
    }

    private void TeamInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (_teamCts != null) return;
        RunTeam(TeamInput.Text);
        TeamInput.Clear();
    }

    private void TeamSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string task) RunTeam(task);
    }

    private void TeamWelcome_Click(object sender, RoutedEventArgs e) => ShowStudio(TabTeam);

    private void TeamAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TeamEntry { FilePath: { } path } && File.Exists(path)) OpenFile(path);
    }

    private void TeamMessage_SizeChanged(object sender, SizeChangedEventArgs e) => TeamScroll.ScrollToBottom();

    // ---------------- Training feedback from the timeline ----------------

    private AgentProfile? ProfileOf(TeamEntry entry) =>
        entry.Agent == null ? null : _profiles.FirstOrDefault(p => p.Id == entry.Agent.Id);

    private void TeamRateUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TeamEntry entry || ProfileOf(entry) is not { } profile) return;
        profile.Examples.Add(new AgentExample(entry.Prompt ?? "", entry.Text, DateTime.Now));
        BumpAndSave(profile);
        entry.IsRated = true;
        entry.RatingText = $"Saved as an example for {profile.Name} (v{profile.Version})";
    }

    private void TeamRateDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TeamEntry entry) entry.IsCritiquing = !entry.IsCritiquing;
    }

    private void TeamSaveLesson_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TeamEntry entry || ProfileOf(entry) is not { } profile) return;
        string lesson = entry.Critique.Trim();
        if (lesson.Length == 0) return;
        profile.Lessons.Add(lesson);
        BumpAndSave(profile);
        entry.IsCritiquing = false;
        entry.IsRated = true;
        entry.RatingText = $"Lesson saved for {profile.Name} (v{profile.Version})";
    }

    // ---------------- Train tab ----------------

    private AgentProfile? SelectedProfile => TrainAgents.SelectedItem as AgentProfile;

    private static string Snapshot(AgentProfile profile)
    {
        var copy = profile.Clone();
        copy.Version = 0;
        return JsonSerializer.Serialize(copy, AgentStore.Json);
    }

    private void BumpAndSave(AgentProfile profile)
    {
        profile.Version++;
        SaveProfiles();
    }

    private void SaveProfiles()
    {
        foreach (var profile in _profiles) _savedProfiles[profile.Id] = Snapshot(profile);
        TrainStatus.Text = AgentStore.SaveProfiles(_profiles)
            ? $"Saved to {Path.Combine("agents", "profiles.json")}."
            : "Couldn't write the agents file.";
        RefreshPipeline();
    }

    private void TrainSave_Click(object sender, RoutedEventArgs e)
    {
        var changed = _profiles.Where(p => !_savedProfiles.TryGetValue(p.Id, out var saved) || saved != Snapshot(p)).ToList();
        foreach (var profile in changed) profile.Version++;
        SaveProfiles();
        if (changed.Count > 0)
            TrainStatus.Text += " New version: " + string.Join(", ", changed.Select(p => $"{p.Name} v{p.Version}")) + ".";
    }

    private void TrainAgents_SelectionChanged(object sender, SelectionChangedEventArgs e) => NewLessonBox?.Clear();

    private void TrainEnabled_Click(object sender, RoutedEventArgs e) => RefreshPipeline();

    private void AddLesson()
    {
        string lesson = NewLessonBox.Text.Trim();
        if (lesson.Length == 0 || SelectedProfile is not { } profile) return;
        profile.Lessons.Add(lesson);
        NewLessonBox.Clear();
        TrainStatus.Text = "Lesson added — Save to keep it.";
    }

    private void TrainAddLesson_Click(object sender, RoutedEventArgs e) => AddLesson();

    private void NewLesson_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddLesson();
    }

    private void TrainRemoveLesson_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string lesson) SelectedProfile?.Lessons.Remove(lesson);
    }

    private void TrainRemoveExample_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentExample example) SelectedProfile?.Examples.Remove(example);
    }

    private void TrainReset_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile) return;
        var defaults = AgentStore.DefaultFor(profile.Role);
        profile.Instructions = defaults.Instructions;
        profile.Model = defaults.Model;
        profile.Effort = defaults.Effort;
        TrainStatus.Text = $"{profile.Name}: default instructions restored (lessons and examples kept). Save to keep it.";
    }

    private void TrainExport_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile) return;
        if (profile.Examples.Count == 0)
        {
            TrainStatus.Text = "No examples yet: rate agent answers with thumbs up in the Team tab.";
            return;
        }
        try
        {
            string path = AgentStore.ExportDataset(profile);
            TrainStatus.Text = $"Wrote {profile.Examples.Count} examples to {Path.GetFileName(path)}.";
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrainStatus.Text = $"Can't export: {ex.Message}";
        }
    }

    /// <summary>Asks the agent's model to rewrite its role instructions with the lessons folded in.</summary>
    private async void TrainDistill_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile) return;
        if (profile.Lessons.Count == 0)
        {
            TrainStatus.Text = "No lessons to distill yet.";
            return;
        }
        if (_apiKey is not { } key)
        {
            TrainStatus.Text = "Add your API key first (key icon at the top of this panel).";
            return;
        }

        var button = (Button)sender;
        button.IsEnabled = false;
        TrainStatus.Text = $"Rewriting {profile.Name}'s instructions…";
        try
        {
            const string system =
                "You maintain the role instructions of an AI agent in a coding team (planner, coder, reviewer) that writes " +
                "scripts for a desktop app. Rewrite the instructions so they include what the lessons teach. Keep what still " +
                "applies, drop what a lesson contradicts, stay concise (at most ~20 lines), write in English, and output only " +
                "the new instructions text with no preamble.";
            string prompt = $"Agent: {profile.Name} ({profile.Role})\n\nCurrent instructions:\n{profile.Instructions}\n\n" +
                            "Lessons from user feedback:\n" + string.Join("\n", profile.Lessons.Select(l => "- " + l));
            string rewritten = await AgentSession.CompleteAsync(Client(key), profile.Model, system, prompt, CancellationToken.None);
            if (rewritten.Length == 0) throw new InvalidOperationException("The model returned nothing.");

            int folded = profile.Lessons.Count;
            profile.Instructions = rewritten;
            profile.Lessons.Clear();
            TrainStatus.Text = $"Folded {folded} lesson(s) into the instructions. Review them, then Save — or Reset agent to undo.";
        }
        catch (Exception ex) when (AiErrorText(ex) is { } text)
        {
            TrainStatus.Text = text;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ---------------- Evals tab ----------------

    private void RefreshEvalHistory() =>
        EvalHistory.ItemsSource = AgentStore.LoadHistory().AsEnumerable().Reverse().Take(15).ToList();

    private void EvalAdd_Click(object sender, RoutedEventArgs e)
    {
        var evalCase = new EvalCase
        {
            Name = "New case",
            Prompt = "Create example.enma that ...",
            Checks = "exists example.enma\ncompiles example.enma\ncontains example.enma ui.",
        };
        _evalCases.Add(evalCase);
        EvalList.SelectedItem = evalCase;
    }

    private void EvalDelete_Click(object sender, RoutedEventArgs e)
    {
        if (EvalList.SelectedItem is EvalCase evalCase) _evalCases.Remove(evalCase);
    }

    private void EvalSave_Click(object sender, RoutedEventArgs e) =>
        EvalProgress.Text = AgentStore.SaveEvals(_evalCases) ? "Cases saved." : "Couldn't write the evals file.";

    private void EvalOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EvalResult { Folder: var folder } && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private async void EvalRun_Click(object sender, RoutedEventArgs e)
    {
        if (_evalCts != null)
        {
            _evalCts.Cancel();
            return;
        }
        if (_apiKey is not { } key)
        {
            KeyPanel.Visibility = Visibility.Visible;
            return;
        }
        if (_evalCases.Count == 0) return;

        AgentStore.SaveEvals(_evalCases);
        _evalResults.Clear();
        _evalCts = new CancellationTokenSource();
        EvalRunText.Text = "Stop";
        EvalRunGlyph.Text = "";
        int rounds = ReviewRounds;
        try
        {
            var results = await EvalRunner.RunAsync(Client(key), _profiles.ToList(), _evalCases.ToList(), rounds,
                text => EvalProgress.Text = text, _evalResults.Add, _evalCts.Token);

            int passed = results.Count(r => r.Passed);
            var versions = _profiles.Where(p => p.Enabled).ToDictionary(p => p.Name, p => p.Version);
            AgentStore.AppendHistory(new EvalRunSummary(DateTime.Now, $"team, {rounds} review round(s)", passed, results.Count, versions));
            RefreshEvalHistory();
            EvalProgress.Text = $"Score: {passed}/{results.Count} cases passed";
        }
        catch (Exception ex) when (AiErrorText(ex) is { } text)
        {
            EvalProgress.Text = text;
        }
        finally
        {
            _evalCts.Dispose();
            _evalCts = null;
            EvalRunText.Text = "Run all cases";
            EvalRunGlyph.Text = "";
        }
    }
}
