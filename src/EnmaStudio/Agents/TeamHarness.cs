using System.IO;
using System.Text.RegularExpressions;
using Anthropic;
using EnmaStudio.Editor;

namespace EnmaStudio.Agents;

public enum TeamEventKind { Stage, Text, Action, Verdict, Error, Done }

/// <summary>
/// Something that happened in a team run. <see cref="Prompt"/> is the message the agent was answering,
/// kept so a thumbs up can save the pair as a training example.
/// </summary>
public sealed record TeamEvent(AgentProfile? Agent, TeamEventKind Kind, string Text, string? FilePath = null, string? Prompt = null);

public sealed record TeamOutcome(bool? Approved, int ReviewRounds, string Summary);

/// <summary>
/// The multi-agent harness. Code (not a model) drives the pipeline:
/// Planner writes a plan → Coder implements it with the file tools → Reviewer checks the files and
/// answers APPROVE or CHANGES → on CHANGES the Coder fixes and the Reviewer looks again, up to
/// <c>maxReviewRounds</c>. Each agent keeps its own conversation for the whole run.
/// Disabled agents are skipped; with only the Coder enabled this is a single-agent run.
/// </summary>
public static class TeamHarness
{
    private static readonly Regex Verdict = new(@"VERDICT:\s*(APPROVE|CHANGES)", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);

    public static async Task<TeamOutcome> RunAsync(AnthropicClient client, IReadOnlyList<AgentProfile> profiles, AgentTools tools,
                                                    string task, int maxReviewRounds, Action<TeamEvent> emit, CancellationToken token)
    {
        AgentProfile? Pick(AgentRole role) => profiles.FirstOrDefault(p => p.Role == role && p.Enabled);
        var coderProfile = Pick(AgentRole.Coder)
            ?? throw new InvalidOperationException("Enable an agent with the Coder role (Train tab).");
        var plannerProfile = Pick(AgentRole.Planner);
        var reviewerProfile = maxReviewRounds > 0 ? Pick(AgentRole.Reviewer) : null;

        async Task<AgentReply> Ask(AgentSession session, string message)
        {
            token.ThrowIfCancellationRequested();
            emit(new TeamEvent(session.Profile, TeamEventKind.Stage, StageText(session.Profile)));
            var reply = await session.SendAsync(message,
                text => emit(new TeamEvent(session.Profile, TeamEventKind.Text, text, Prompt: message)),
                result => emit(new TeamEvent(session.Profile, result.IsError ? TeamEventKind.Error : TeamEventKind.Action, result.Action, result.FilePath)),
                token);
            if (reply.Refused) throw new InvalidOperationException($"{session.Profile.Name}: the model declined this request.");
            return reply;
        }

        // 1. Plan
        string plan = "";
        if (plannerProfile != null)
        {
            var planner = new AgentSession(plannerProfile, client, tools);
            plan = (await Ask(planner, $"User request:\n{task}\n\nWrite the plan for the Coder.")).Text;
        }

        // 2. Implement
        var coder = new AgentSession(coderProfile, client, tools);
        string coderMessage = plan.Length > 0
            ? $"User request:\n{task}\n\nPlan from {plannerProfile!.Name}:\n{plan}\n\nImplement it now."
            : $"User request:\n{task}\n\nImplement it now.";
        string work = (await Ask(coder, coderMessage)).Text;

        // 3. Review loop
        bool? approved = null;
        int rounds = 0;
        if (reviewerProfile != null)
        {
            var reviewer = new AgentSession(reviewerProfile, client, tools);
            string reviewMessage =
                $"User request:\n{task}\n\n{(plan.Length > 0 ? $"Plan:\n{plan}\n\n" : "")}The Coder says:\n{work}\n\n" +
                "Review the files now and end with the VERDICT line.";
            while (true)
            {
                rounds++;
                string review = (await Ask(reviewer, reviewMessage)).Text;
                var match = Verdict.Match(review);
                approved = match.Success && match.Groups[1].Value.Equals("APPROVE", StringComparison.OrdinalIgnoreCase);
                emit(new TeamEvent(reviewerProfile, TeamEventKind.Verdict,
                    approved == true ? "Approved" : match.Success ? "Changes requested" : "No verdict line — treated as changes requested"));
                if (approved == true || rounds >= maxReviewRounds) break;

                work = (await Ask(coder, $"{reviewerProfile.Name} reviewed your work and asks for changes:\n{review}\n\nFix every point in the files.")).Text;
                reviewMessage = $"The Coder made fixes and says:\n{work}\n\nReview the files again and end with the VERDICT line.";
            }
        }

        string summary = approved switch
        {
            true => $"Done — approved after {rounds} review round{(rounds == 1 ? "" : "s")}.",
            false => $"Done — the reviewer still wants changes after {rounds} round{(rounds == 1 ? "" : "s")}; see the last review.",
            null => "Done.",
        };
        emit(new TeamEvent(null, TeamEventKind.Done, summary));
        return new TeamOutcome(approved, rounds, summary);
    }

    private static string StageText(AgentProfile profile) => profile.Role switch
    {
        AgentRole.Planner => $"{profile.Name} is planning…",
        AgentRole.Coder => $"{profile.Name} is writing code…",
        _ => $"{profile.Name} is reviewing…",
    };
}

/// <summary>Runs every eval case through the team in its own sandbox folder and scores the checks.</summary>
public static class EvalRunner
{
    public static async Task<List<EvalResult>> RunAsync(AnthropicClient client, IReadOnlyList<AgentProfile> profiles,
                                                        IReadOnlyList<EvalCase> cases, int maxReviewRounds,
                                                        Action<string> progress, Action<EvalResult> onResult, CancellationToken token)
    {
        string runFolder = Path.Combine(AgentStore.Folder, "eval_runs", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var results = new List<EvalResult>();
        for (int i = 0; i < cases.Count; i++)
        {
            var evalCase = cases[i];
            token.ThrowIfCancellationRequested();
            progress($"[{i + 1}/{cases.Count}] {evalCase.Name}");

            string folder = Path.Combine(runFolder, Safe(evalCase.Name, i));
            Directory.CreateDirectory(folder);
            // Sandbox: the agents' files never touch the real workspace or the editor tabs.
            var files = new FileTools(folder, _ => null, path => File.Exists(path) ? new EditorDocument(path) : null);
            var tools = new AgentTools(files, folder, _ => null);

            bool? approved = null;
            string? failure = null;
            try
            {
                var outcome = await TeamHarness.RunAsync(client, profiles, tools, evalCase.Prompt, maxReviewRounds, e =>
                {
                    if (e.Kind is TeamEventKind.Stage or TeamEventKind.Action)
                        progress($"[{i + 1}/{cases.Count}] {evalCase.Name}: {e.Text}");
                }, token);
                approved = outcome.Approved;
            }
            catch (Exception ex) when (ex is OperationCanceledException or Anthropic.Exceptions.AnthropicUnauthorizedException)
            {
                throw; // stopping, or every other case would fail the same way
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            var checks = EvalChecks.Run(evalCase.Checks, tools, approved);
            if (failure != null) checks.Insert(0, new EvalCheckResult("run", false, failure));
            var result = new EvalResult(evalCase.Name, checks.Count > 0 && checks.All(c => c.Passed), checks, folder);
            results.Add(result);
            onResult(result);
        }
        return results;
    }

    private static string Safe(string name, int index)
    {
        string clean = new(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
        return $"{index + 1:00}_{clean.Trim('_')}";
    }
}
