using AIROOMCore;
using Xunit;

/// <summary>Drives the real GLM (ZCode) and Gemini (agy) CLIs through the project
/// flow — plan → tasks in copies with staged writes → conflict-checked merge →
/// round report → acceptance writing back — when AIROOM_LIVE_CLI=1 and those
/// members can run on this machine. Only pass/fail evidence, never guesses.</summary>
public class ProjectFlowLiveTests
{
    static bool LiveReady() => Environment.GetEnvironmentVariable("AIROOM_LIVE_CLI") == "1";

    [Fact]
    public async Task LivePlanExecuteMergeAndAccept()
    {
        if (!LiveReady()) return; // Skipped silently unless the live switch is on.
        var runner = new CliProcessRunner();
        var glm = new MemberCard(new MemberID("m-liveglm"), Executor.ZCode, name: "GLM");
        var gemini = new MemberCard(new MemberID("m-liveagy"), Executor.Antigravity, name: "Gemini");
        runner.SetDefaults(glm.Agent, Executor.ZCode, "", "");
        runner.SetDefaults(gemini.Agent, Executor.Antigravity, "", "");

        var baseFolder = Path.Combine(Path.GetTempPath(), "airoom-flow-live-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(baseFolder, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "index.html"), "<h1>v1</h1>\n");
        File.WriteAllText(Path.Combine(project, "style.css"), "body { color: black; }\n");
        try
        {
            var members = new List<MemberID> { glm.Agent, gemini.Agent };
            var names = new Dictionary<string, string>
            {
                [glm.Agent.RawValue] = glm.Name, [gemini.Agent.RawValue] = gemini.Name,
            };
            var flow = new ProjectFlow
            {
                Title = "登入頁小改",
                Folder = project,
                Members = members,
                Lead = glm.Agent,
                ReviewLevel = ReviewLevel.Standard,
            };

            // Steps 2–3: the lead reads the project files and drafts a plan in real JSON.
            var planPrompt = ProjectFlowEngine.PlanPrompt(flow, "", names, "測試主人");
            var planText = await Call(runner, glm.Agent, planPrompt, project, ExecutionAccess.ReadFiles);
            FlowPlan plan;
            try
            {
                plan = ProjectFlowEngine.ParsePlan(planText, members);
            }
            catch (FlowPlanError error)
            {
                // Show the reply so a real plan slip is readable from the test output.
                throw new Exception($"ParsePlan: {error.Message}\nreply: {planText[..Math.Min(500, planText.Length)]}");
            }
            Assert.True(plan.Rounds.Count >= 1 && plan.Tasks.Count >= 1,
                $"plan parsed to {plan.Rounds.Count} rounds / {plan.Tasks.Count} tasks");
            var round = plan.Rounds[0];
            round.Status = FlowRoundStatus.Executing;
            flow.Step = FlowStep.Executing;

            // Step 4: each ready task runs in its own copy with staged writes, then
            // merges into the round copy with the real conflict check.
            var workspace = new ProjectWorkspace(Path.Combine(baseFolder, "store"), flow.Id);
            _ = workspace.RoundFolder(round.Id, project);
            var ready = ProjectFlowEngine.ReadyTasks(round, plan, new HashSet<Guid>(), 2);
            Assert.True(ready.Count >= 1,
                $"no ready task in round {round.Key}; tasks: {string.Join(", ", plan.Tasks.Select(task => $"{task.Key}@{task.RoundKey}"))}");
            foreach (var task in ready)
            {
                var taskID = Guid.NewGuid();
                var taskFolder = workspace.TaskFolder(taskID, round.Id);
                var prompt = ProjectFlowEngine.TaskPrompt(task, flow, round, names, "測試主人");
                var reply = await Call(runner, task.Member, prompt, taskFolder, ExecutionAccess.StagedWrite);
                task.Summary = reply;
                task.Changes = workspace.TaskChanges(taskID);
                workspace.MergeTask(taskID, round.Id);
                task.Status = FlowTaskStatus.Done;
            }
            var roundChanges = workspace.RoundChanges(round.Id);
            Assert.True(roundChanges.Count >= 1, "no file changes merged into the round copy");
            // The project folder itself is untouched before acceptance.
            var before = Snapshot(project);
            Assert.Equal("body { color: black; }\n", File.ReadAllText(Path.Combine(project, "style.css")));

            // The lead writes the round report from the integrated copy.
            round.Changes = roundChanges;
            var reportPrompt = ProjectFlowEngine.RoundCheckPrompt(round, flow, plan.TasksIn(round), glm.Agent, names,
                verify: false);
            var report = await Call(runner, glm.Agent, reportPrompt, workspace.Rounds.WorkingDirectory(round.Id),
                ExecutionAccess.ReadFiles);
            round.Report = report;
            Assert.False(string.IsNullOrWhiteSpace(report));

            // Step 5: acceptance — only now does the project folder change.
            workspace.AcceptRound(round.Id, project);
            var after = Snapshot(project);
            Assert.True(after.Count > before.Count
                || after.Any(entry => !before.TryGetValue(entry.Key, out var content) || content != entry.Value),
                "acceptance wrote nothing into the project folder");
        }
        finally
        {
            try { Directory.Delete(baseFolder, recursive: true); } catch { }
        }
    }

    static async Task<string> Call(CliProcessRunner runner, MemberID agent, string prompt, string folder, ExecutionAccess access)
    {
        // The runner rejects prompts over the CLI ceiling; the model truncates as in the app.
        if (prompt.Length > CommandPlan.MaxPromptCharacters) prompt = prompt[..CommandPlan.MaxPromptCharacters];
        return await runner.RunAsync(agent: agent, prompt: prompt,
            settings: new NativeSettings { WorkspacePath = folder },
            fallbackWorkspace: folder, access: access);
    }

    static Dictionary<string, string> Snapshot(string folder) => Directory
        .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(folder, path), File.ReadAllText);
}
