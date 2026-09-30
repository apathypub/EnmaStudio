using System.Text;
using Anthropic;
using Anthropic.Models.Messages;
using Enma;
using EnmaStudio.Editor;

namespace EnmaStudio.Agents;

/// <summary>What one agent turn produced.</summary>
public sealed record AgentReply(string Text, bool Refused);

/// <summary>
/// One agent's conversation. Each <see cref="SendAsync"/> runs the tool loop until the agent stops calling
/// tools. The system prompt is built once from the profile (role instructions, lessons, examples, the
/// Enma language and runtime references), so the conversation prefix stays cacheable.
/// </summary>
public sealed class AgentSession
{
    private const int MaxSteps = 25;

    private readonly AnthropicClient _client;
    private readonly AgentTools _tools;
    private readonly List<MessageParam> _history = [];
    private readonly string _system;

    public AgentSession(AgentProfile profile, AnthropicClient client, AgentTools tools)
    {
        Profile = profile;
        _client = client;
        _tools = tools;
        _system = BuildSystemPrompt(profile);
    }

    public AgentProfile Profile { get; }

    /// <summary>Planners and reviewers only look at files.</summary>
    public bool ReadOnly => Profile.Role != AgentRole.Coder;

    public static string BuildSystemPrompt(AgentProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are {profile.Name}, the {profile.Role.ToString().ToLowerInvariant()} in a small team of agents " +
                      "inside Enma Studio, an editor for the Enma language. The team is Planner -> Coder -> Reviewer; you " +
                      "only see messages addressed to you. Paths for the file tool are plain file names in the workspace " +
                      "folder (view \".\" lists them). Reply in the user's language.");
        sb.AppendLine();
        sb.AppendLine("Your job:");
        sb.AppendLine(profile.Instructions.Trim());
        if (profile.Role != AgentRole.Coder)
            sb.AppendLine("You can view files and run check_script, but not change files.");

        if (profile.Lessons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Lessons from earlier feedback (follow them):");
            foreach (string lesson in profile.Lessons) sb.AppendLine("- " + lesson.Trim());
        }

        if (profile.Examples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Examples of answers the user rated as good:");
            // The newest few keep the prompt short.
            foreach (var example in profile.Examples.TakeLast(4))
            {
                sb.AppendLine("<example>");
                sb.AppendLine("<task>" + Clip(example.Task, 1200) + "</task>");
                sb.AppendLine("<answer>" + Clip(example.Output, 2000) + "</answer>");
                sb.AppendLine("</example>");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Enma language reference:");
        sb.AppendLine(EnmaDocs.Reference);
        sb.AppendLine("Idiomatic Enma example:");
        sb.AppendLine(EnmaDocs.Example);
        sb.AppendLine();
        sb.AppendLine("Runtime API (the same for Lua and Enma files):");
        sb.AppendLine(EnmaDocs.Runtime);
        return sb.ToString();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    public async Task<AgentReply> SendAsync(string message, Action<string> onText, Action<ToolResult> onTool, CancellationToken token)
    {
        int start = _history.Count;
        _history.Add(new MessageParam { Role = Role.User, Content = message });
        var text = new StringBuilder();
        try
        {
            for (int step = 0; step < MaxSteps; step++)
            {
                var parameters = new MessageCreateParams
                {
                    Model = Profile.Model,
                    MaxTokens = 16000,
                    System = new List<TextBlockParam> { new() { Text = _system } },
                    CacheControl = new CacheControlEphemeral(),
                    Tools = [new ToolUnion(new ToolTextEditor20250728()), AgentTools.CheckTool],
                    Messages = [.. _history],
                };
                if (AgentModels.SupportsEffort(Profile.Model)) parameters = parameters with { OutputConfig = new OutputConfig { Effort = EffortFor(Profile.Effort) } };

                var response = await _client.Messages.Create(parameters, token);

                List<ContentBlockParam> assistant = [];
                List<ContentBlockParam> results = [];
                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out TextBlock? t))
                    {
                        assistant.Add(new TextBlockParam { Text = t.Text });
                        if (t.Text.Trim().Length > 0)
                        {
                            if (text.Length > 0) text.Append("\n\n");
                            text.Append(t.Text.Trim());
                            onText(t.Text.Trim());
                        }
                    }
                    else if (block.TryPickThinking(out ThinkingBlock? thinking))
                    {
                        assistant.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    }
                    else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                    {
                        assistant.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                    }
                    else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                    {
                        assistant.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                        var result = _tools.Execute(toolUse.Name, toolUse.Input, ReadOnly);
                        onTool(result);
                        results.Add(new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = result.Content, IsError = result.IsError });
                    }
                }

                _history.Add(new MessageParam { Role = Role.Assistant, Content = assistant });
                if (response.StopReason == StopReason.Refusal) return new AgentReply(text.ToString(), true);
                if (results.Count == 0) return new AgentReply(text.ToString(), false);
                _history.Add(new MessageParam { Role = Role.User, Content = results });
            }
            onText("(Stopped: too many steps in one turn.)");
            return new AgentReply(text.ToString(), false);
        }
        catch
        {
            // Keep the history valid for a retry.
            _history.RemoveRange(start, _history.Count - start);
            throw;
        }
    }

    private static Effort EffortFor(string effort) => effort switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };

    /// <summary>
    /// One-shot request without tools, used by the training tools (e.g. rewriting instructions from lessons).
    /// </summary>
    public static async Task<string> CompleteAsync(AnthropicClient client, string model, string system, string prompt, CancellationToken token)
    {
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 8000,
            System = new List<TextBlockParam> { new() { Text = system } },
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, token);
        if (response.StopReason == StopReason.Refusal) throw new InvalidOperationException("The model declined this request.");
        return string.Join("\n", response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
    }
}
