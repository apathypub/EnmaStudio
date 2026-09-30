using System.Collections.ObjectModel;
using System.Text.Json.Serialization;


namespace EnmaStudio.Agents;

/// <summary>What an agent does in the team pipeline.</summary>
public enum AgentRole { Planner, Coder, Reviewer }

/// <summary>A good answer saved from the Team timeline (thumbs up); shown to the agent as an example.</summary>
public sealed record AgentExample(string Task, string Output, DateTime Added);

/// <summary>
/// One agent of the Enma Studio team: its model settings, role instructions and what it has been
/// "trained" with — lessons (from thumbs down / notes) and examples (from thumbs up). Training here is
/// prompt-level: lessons and examples go into the agent's system prompt; nothing is fine-tuned.
/// <see cref="Version"/> goes up on every change so eval runs can be compared across versions.
/// </summary>
public sealed class AgentProfile : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set => SetField(ref field, value); } = "Agent";
    public AgentRole Role { get; set => SetField(ref field, value); }
    public string Color { get; set => SetField(ref field, value); } = "#9AC527";
    public string Model { get; set => SetField(ref field, value); } = AgentModels.Default;
    public string Effort { get; set => SetField(ref field, value); } = "medium";
    public bool Enabled { get; set => SetField(ref field, value); } = true;
    public string Instructions { get; set => SetField(ref field, value); } = "";
    public int Version { get; set => SetField(ref field, value); } = 1;
    public ObservableCollection<string> Lessons { get; set; } = [];
    public ObservableCollection<AgentExample> Examples { get; set; } = [];

    [JsonIgnore]
    public string Label => $"{Name} · {Role}";

    public AgentProfile Clone() => new()
    {
        Id = Id, Name = Name, Role = Role, Color = Color, Model = Model, Effort = Effort, Enabled = Enabled,
        Instructions = Instructions, Version = Version, Lessons = [.. Lessons], Examples = [.. Examples],
    };
}

public static class AgentModels
{
    public const string Default = "claude-opus-5-5";

    public static IReadOnlyList<string> All { get; } = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5"];

    public static IReadOnlyList<string> Efforts { get; } = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Haiku 4.5 doesn't take the effort setting.</summary>
    public static bool SupportsEffort(string model) => !model.StartsWith("claude-haiku", StringComparison.Ordinal);
}
