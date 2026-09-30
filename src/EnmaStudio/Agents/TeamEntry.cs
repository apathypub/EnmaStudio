

namespace EnmaStudio.Agents;

/// <summary>
/// One row in the Team timeline: the user's task, an agent's stage/text/file action/verdict, an error
/// or the final summary. Agent text rows carry the thumbs up / down training feedback state.
/// </summary>
public sealed class TeamEntry(TeamEventKind kind, string text, AgentProfile? agent = null, string? filePath = null,
                              string? prompt = null, bool isUser = false) : ObservableObject
{
    public TeamEventKind Kind { get; } = kind;
    public bool IsUser { get; } = isUser;
    public AgentProfile? Agent { get; } = agent;
    public string? FilePath { get; } = filePath;
    /// <summary>The message the agent was answering (becomes the example's task on thumbs up).</summary>
    public string? Prompt { get; } = prompt;
    public string Text { get; set => SetField(ref field, value); } = text;

    public string AgentName => Agent?.Name ?? "";
    public string AgentColor => Agent?.Color ?? "#777777";

    public bool IsRated { get; set => SetField(ref field, value); }
    public string RatingText { get; set => SetField(ref field, value); } = "";
    public bool IsCritiquing { get; set => SetField(ref field, value); }
    public string Critique { get; set => SetField(ref field, value); } = "";
}

/// <summary>A chip in the pipeline strip ("→ Coder").</summary>
public sealed record PipelineItem(string Name, string Color, string Arrow, string Tip);
