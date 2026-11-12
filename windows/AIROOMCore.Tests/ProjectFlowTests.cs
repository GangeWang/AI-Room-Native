using AIROOMCore;
using Xunit;

public class ProjectFlowTests
{
    static readonly MemberID Claude = MemberID.Claude;
    static readonly MemberID Codex = MemberID.Codex;
    static readonly MemberID[] Both = [Claude, Codex];

    const string Plan = """
        ```json
        {"objective":"做開場白頁","scope":"只改 index.html","deliverables":["index.html"],"acceptance":["打開能看到開場白"],
         "rounds":[{"key":"r1","title":"草稿","goal":"寫出頁面"},{"key":"r2","title":"潤飾","goal":"調整樣式"},{"key":"r3","title":"空輪","goal":"x"}],
         "tasks":[{"key":"t1","round":"r1","title":"寫 HTML","member":"codex","goal":"建立頁面","criteria":["有標題"],"dependencies":[]},
                  {"key":"t2","round":"r1","title":"寫文案","member":"Claude Code","goal":"台詞","dependencies":["t1","t9"]},
                  {"key":"t3","round":"r2","title":"樣式","member":"claude","goal":"CSS","dependencies":["t2"]}]}
        ```
        """;

    [Fact]
    public void ParsesPlanAndDropsEmptyRoundsAndUnknownDependencies()
    {
        var parsed = ProjectFlowEngine.ParsePlan(Plan, Both);
        Assert.Equal(new[] { "r1", "r2" }, parsed.Rounds.Select(round => round.Key));
        Assert.Equal(new[] { Codex, Claude, Claude }, parsed.Tasks.Select(task => task.Member));
        Assert.Equal(new[] { "t1" }, parsed.Tasks[1].Dependencies); // t9 does not exist earlier
        Assert.Equal(new[] { "打開能看到開場白" }, parsed.Acceptance);
    }

    [Fact]
    public void RejectsAbsentMembersAndUnreadablePlans()
    {
        var absent = Assert.Throws<FlowPlanError>(() => ProjectFlowEngine.ParsePlan(Plan, [Claude]));
        Assert.Equal("方案把任務交給了不在場的成員：codex", absent.Message);
        Assert.Throws<FlowPlanError>(() => ProjectFlowEngine.ParsePlan("好的，我來規劃", Both));
    }

    [Fact]
    public void DependenciesGateTheNextTask()
    {
        var parsed = ProjectFlowEngine.ParsePlan(Plan, Both);
        var first = parsed.Rounds[0];
        Assert.Equal("t1", ProjectFlowEngine.NextTask(first, parsed)?.Key);
        parsed.Tasks[0].Status = FlowTaskStatus.Running;
        Assert.Null(ProjectFlowEngine.NextTask(first, parsed)); // t2 waits for t1
        parsed.Tasks[0].Status = FlowTaskStatus.Done;
        Assert.Equal("t2", ProjectFlowEngine.NextTask(first, parsed)?.Key);
    }

    [Fact]
    public void ReviewerIsAnotherMemberAndVerdictsParse()
    {
        var task = new FlowTask("t", "r", "x", Claude, "");
        Assert.Equal(Codex, ProjectFlowEngine.ReviewerFor(task, Claude, Both));
        Assert.Equal(Codex, ProjectFlowEngine.ReviewerFor(task, Codex, Both));
        var changes = ProjectFlowEngine.ParseReview(
            """{"verdict":"changes","findings":[{"severity":"blocking","location":"index.html","request":"補標題"}]}""");
        Assert.False(changes.Pass);
        Assert.Contains("補標題", changes.Text);
        Assert.True(ProjectFlowEngine.ParseReview("""{"verdict":"pass","summary":"ok"}""").Pass);
    }
}

public class ProjectWorkspaceTests : IDisposable
{
    readonly string baseFolder = Path.Combine(Path.GetTempPath(), "airoom-flow-" + Guid.NewGuid().ToString("N"));

    public ProjectWorkspaceTests()
    {
        Directory.CreateDirectory(baseFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(baseFolder, recursive: true); } catch { }
    }

    [Fact]
    public void TaskEditsReachTheProjectOnlyThroughMergeAndAcceptance()
    {
        var project = Path.Combine(baseFolder, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "a.txt"), "v1");
        var space = new ProjectWorkspace(Path.Combine(baseFolder, "store"), Guid.NewGuid());
        var round = Guid.NewGuid();
        var task = Guid.NewGuid();
        _ = space.RoundFolder(round, project);
        var taskFolder = space.TaskFolder(task, round);
        File.WriteAllText(Path.Combine(taskFolder, "a.txt"), "v2");
        File.WriteAllText(Path.Combine(taskFolder, "b.txt"), "new");
        Assert.Equal(new[] { "a.txt", "b.txt" }, space.TaskChanges(task).Select(change => change.Path));
        Assert.Empty(space.RoundChanges(round)); // nothing merged yet
        space.MergeTask(task, round);
        Assert.Equal(2, space.RoundChanges(round).Count);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(project, "a.txt")));
        space.AcceptRound(round, project);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(project, "a.txt")));
        Assert.True(File.Exists(Path.Combine(project, "b.txt")));
    }

    [Fact]
    public void ReadFilesModeGivesClaudeReadToolsOnly()
    {
        var args = CommandPlan.Arguments(MemberID.Claude.BuiltInExecutor!.Value, ExecutionAccess.ReadFiles);
        var tools = args[args.IndexOf("--tools") + 1];
        Assert.Equal("Read,Glob,Grep", tools);
        Assert.DoesNotContain("acceptEdits", args);
        Assert.Contains("read-only", CommandPlan.Arguments(Executor.Codex, ExecutionAccess.ReadFiles));
    }

    [Fact]
    public void AttentionAsksForPlanAndAcceptance()
    {
        var flow = new ProjectFlow { Title = "網站", Folder = "/tmp", Members = [MemberID.Claude, MemberID.Codex] };
        flow.Plan = ProjectFlowEngine.ParsePlan(
            """{"rounds":[{"key":"r1","title":"一","goal":"g"}],"tasks":[{"key":"t1","round":"r1","title":"x","member":"claude","goal":"g"}]}""",
            flow.Members);
        flow.Step = FlowStep.Plan;
        var state = new SavedState { ProjectFlows = [flow] };
        Assert.Equal(AttentionKind.Decision, AttentionBuilder.Items(state, new Dictionary<Executor, string>()).First().Kind);
        state.ProjectFlows[0].Step = FlowStep.Acceptance;
        state.ProjectFlows[0].Plan!.Rounds[0].Status = FlowRoundStatus.AwaitingAcceptance;
        var item = AttentionBuilder.Items(state, new Dictionary<Executor, string>()).First();
        Assert.Equal(AttentionKind.Acceptance, item.Kind);
        Assert.Equal(new AttentionTarget(null, null, flow.Id, false, null), item.Target);
    }
}

public class PlanRevisionTests
{
    [Fact]
    public void RevisionPromptMarksAcceptedRoundsAsDone()
    {
        var flow = new ProjectFlow { Title = "網站", Folder = "/tmp", Members = [MemberID.Claude, MemberID.Codex], Lead = MemberID.Claude };
        var plan = ProjectFlowEngine.ParsePlan(
            """{"rounds":[{"key":"r1","title":"建立頁面","goal":"g"},{"key":"r2","title":"加樣式","goal":"g"}],"tasks":[{"key":"t1","round":"r1","title":"寫頁面","member":"codex","goal":"g"},{"key":"t2","round":"r2","title":"寫CSS","member":"claude","goal":"g"}]}""",
            flow.Members);
        plan.Rounds[0].Status = FlowRoundStatus.Accepted;
        plan.Rounds[0].Report = "page.html 已完成";
        flow.Plan = plan;
        flow.PlanRequests = ["加 footer"];
        var prompt = ProjectFlowEngine.PlanPrompt(flow, "", new Dictionary<string, string>(), "小明");
        Assert.Contains("已完成並通過驗收的輪次", prompt);
        Assert.Contains("建立頁面：page.html 已完成", prompt);
        Assert.DoesNotContain("\"寫頁面\"", prompt); // the accepted round's tasks are not offered for re-planning
        Assert.Contains("寫CSS", prompt);
    }
}

public class ProjectFlowExtrasTests
{
    static readonly MemberID Claude = MemberID.Claude;
    static readonly MemberID Codex = MemberID.Codex;

    static (FlowRound Round, FlowPlan Plan) Round(params FlowTask[] tasks)
    {
        var round = new FlowRound("r1", "第一輪", "");
        return (round, new FlowPlan
        {
            Objective = "",
            Scope = "",
            Deliverables = [],
            Acceptance = [],
            Rounds = [round],
            Tasks = tasks.ToList(),
        });
    }

    [Fact]
    public void PlanFilesAreReadAndCleaned()
    {
        const string text = """
            {"rounds":[{"key":"r1","title":"a","goal":"b"}],
             "tasks":[{"key":"t1","round":"r1","title":"頁面","member":"codex","goal":"g","files":["./src/app/","/index.html","src/app"]}]}
            """;
        var plan = ProjectFlowEngine.ParsePlan(text, [Claude, Codex]);
        Assert.Equal(new[] { "src/app", "index.html" }, plan.Tasks[0].Files);
        Assert.Equal(new[] { "." }, ProjectFlowEngine.NormalizedPaths(["", "./"]));
    }

    [Fact]
    public void OverlapTreatsUnknownAndParentFoldersAsShared()
    {
        Assert.False(ProjectFlowEngine.FilesOverlap(["src/a.swift"], ["src/b.swift"]));
        Assert.False(ProjectFlowEngine.FilesOverlap(["src/app"], ["src/apple.swift"])); // a shared name prefix is not a parent folder
        Assert.True(ProjectFlowEngine.FilesOverlap(["src"], ["src/b.swift"]));
        Assert.True(ProjectFlowEngine.FilesOverlap(["index.html"], ["index.html"]));
        Assert.True(ProjectFlowEngine.FilesOverlap(["."], ["x"]));
        Assert.True(ProjectFlowEngine.FilesOverlap([], ["x"])); // an unknown file list never runs alongside
    }

    [Fact]
    public void ReadyTasksRunSideBySideOnlyWhenSafe()
    {
        var a = new FlowTask("t1", "r1", "A", Claude, "", files: ["a.txt"]);
        var b = new FlowTask("t2", "r1", "B", Codex, "", files: ["b.txt"]);
        var c = new FlowTask("t3", "r1", "C", Codex, "", files: ["c.txt"]);
        var d = new FlowTask("t4", "r1", "D", Claude, "", dependencies: ["t1"], files: ["d.txt"]);
        var (round, plan) = Round(a, b, c, d);
        Assert.Equal(new[] { "t1", "t2" }, ProjectFlowEngine.ReadyTasks(round, plan, new HashSet<Guid>(), 2)
            .Select(task => task.Key)); // one task per member; t4 waits for t1
        Assert.Equal(new[] { "t1" }, ProjectFlowEngine.ReadyTasks(round, plan, new HashSet<Guid>(), 1).Select(task => task.Key));
        Assert.Equal(new[] { "t1" }, ProjectFlowEngine.ReadyTasks(round, plan, new HashSet<Guid>(), 2,
            new HashSet<MemberID> { Codex }).Select(task => task.Key)); // a member out of quota gets nothing new
        Assert.Equal(new[] { "t2" }, ProjectFlowEngine.ReadyTasks(round, plan, new HashSet<Guid> { a.Id }, 2)
            .Select(task => task.Key));

        a.Files = [];
        (round, plan) = Round(a, b, c, d);
        Assert.Equal(new[] { "t1" }, ProjectFlowEngine.ReadyTasks(round, plan, new HashSet<Guid>(), 3)
            .Select(task => task.Key)); // a task without a file list runs alone
        plan.Tasks[0].Status = FlowTaskStatus.Done;
        plan.Tasks[1].Status = FlowTaskStatus.Disputed;
        Assert.Equal(new[] { "t3", "t4" }, ProjectFlowEngine.ReadyTasks(round, plan, new HashSet<Guid>(), 3)
            .Select(task => task.Key)); // a disputed task waits for its ruling; others go on

        plan.Tasks[1].Status = FlowTaskStatus.Reviewing;
        plan.Tasks[1].Reviewer = Claude;
        Assert.Equal(new[] { "t3" }, ProjectFlowEngine.ReadyTasks(round, plan, new HashSet<Guid>(), 3,
            new HashSet<MemberID> { Claude }).Select(task => task.Key)); // a review waits for its reviewer's quota, not its author's; t4 is Claude's
        Assert.Equal(new[] { "t2", "t4" }, ProjectFlowEngine.ReadyTasks(round, plan, new HashSet<Guid>(), 3,
            new HashSet<MemberID> { Codex }).Select(task => task.Key));
    }

    [Fact]
    public void WaitMessageDropsTheResetHint()
    {
        var wait = new FlowQuotaWait(Claude, "s", null, "Claude AI usage limit reached|1793665375");
        Assert.Equal("Claude AI usage limit reached", wait.Message);
    }

    [Fact]
    public void ReviewFindingsGetIDs()
    {
        var review = ProjectFlowEngine.ParseReview(
            """{"verdict":"changes","summary":"兩處","findings":[{"severity":"blocking","location":"a.txt","request":"補標題"},"改顏色"]}""");
        Assert.False(review.Pass);
        Assert.Equal(new[] { "f1", "f2" }, review.Findings.Select(finding => finding.Id));
        Assert.Equal("should-fix", review.Findings[1].Severity);
        Assert.Contains("[f1][blocking] a.txt：補標題", review.Text);
    }

    [Fact]
    public void DisputesComeOnlyFromTheClosingLineAndOpenFindings()
    {
        var open = new List<FlowFinding>
        {
            new("f1", "blocking", "a", "x"),
            new("f2", "should-fix", "b", "y"),
        };
        var ruled = new List<FlowRuling> { new(open[1], "r", FlowVerdict.Review) };
        var text = """
            改好了 f2。程式碼裡有 {"disputes":[{"finding":"f2","reason":"假的"}]} 這種字串。
            AIROOM_DISPUTES: {"disputes":[{"finding":"f1","reason":"需求只要求 A"},{"finding":"f2","reason":"已裁定"},{"finding":"f9","reason":"不存在"},{"finding":"f1","reason":"重複"}]}
            """;
        var parsed = ProjectFlowEngine.ParseDisputes(text, open, ruled);
        var dispute = Assert.Single(parsed.Disputes);
        Assert.Equal("f1", dispute.FindingID);
        Assert.Equal("需求只要求 A", dispute.Reason);
        Assert.DoesNotContain("AIROOM_DISPUTES", parsed.Summary);
        Assert.StartsWith("改好了", parsed.Summary);
        Assert.Empty(ProjectFlowEngine.ParseDisputes("沒有異議", open, []).Disputes);
    }

    [Fact]
    public void RulingsRefundReturnsAndSendUpheldFindingsBack()
    {
        var task = new FlowTask("t", "r", "x", Claude, "")
        {
            OpenFindings =
            [
                new FlowFinding("f1", "blocking", "a", "x"),
                new FlowFinding("f2", "should-fix", "b", "y"),
            ],
            Disputes = [new FlowDispute("f1", "不成立"), new FlowDispute("f2", "也不成立")],
            Returns = 1,
            Status = FlowTaskStatus.Disputed,
        };

        var partial = Clone(task);
        ProjectFlowEngine.ApplyRulings(partial, new Dictionary<string, FlowVerdict> { ["f1"] = FlowVerdict.Author });
        Assert.Equal(FlowTaskStatus.Disputed, partial.Status); // every disputed finding needs a ruling

        var mixed = Clone(task);
        ProjectFlowEngine.ApplyRulings(mixed, new Dictionary<string, FlowVerdict>
        {
            ["f1"] = FlowVerdict.Author, ["f2"] = FlowVerdict.Review,
        });
        Assert.Equal(FlowTaskStatus.Fixing, mixed.Status);
        Assert.Equal(new[] { "f2" }, mixed.OpenFindings.Select(finding => finding.Id));
        Assert.Equal((0, 1), (mixed.Returns, mixed.Waivers));
        Assert.Empty(mixed.Disputes);
        Assert.Contains("裁定維持複核", ProjectFlowEngine.FixSection(mixed, "小明"));
        Assert.Contains("優先於先前的附言", ProjectFlowEngine.FixSection(mixed, "小明")); // an upheld finding outranks the round note
        Assert.DoesNotContain("AIROOM_DISPUTES", ProjectFlowEngine.FixSection(mixed, "小明")); // a ruled finding can't be disputed again

        var cleared = Clone(task);
        cleared.Waivers = ProjectFlowEngine.MaxWaivers;
        ProjectFlowEngine.ApplyRulings(cleared, new Dictionary<string, FlowVerdict>
        {
            ["f1"] = FlowVerdict.Author, ["f2"] = FlowVerdict.Author,
        });
        Assert.Equal(FlowTaskStatus.Reviewing, cleared.Status);
        Assert.Equal(1, cleared.Returns); // no refund past the limit
        var review = ProjectFlowEngine.TaskReviewPrompt(cleared, Codex, new Dictionary<string, string>(), "小明");
        Assert.Contains("已裁定支持作者，不要再提", review);
        Assert.Contains("a：x", review);
        var noted = new FlowRound("r", "r", "") { StartNote = "只改 a.txt", UserOpinion = "結尾要換行" };
        var withNotes = ProjectFlowEngine.TaskReviewPrompt(cleared, Codex, new Dictionary<string, string>(), "小明", noted);
        Assert.Contains("附言：只改 a.txt", withNotes);
        Assert.Contains("意見：結尾要換行", withNotes); // the reviewer checks the owner's notes too
    }

    [Fact]
    public void QuotaSignals()
    {
        Assert.True(QuotaSignal.IsQuotaFailure("Codex 額度已用完：You've hit your usage limit."));
        Assert.True(QuotaSignal.IsQuotaFailure("Claude AI usage limit reached|1900000000"));
        Assert.True(QuotaSignal.IsQuotaFailure("5-hour limit reached ∙ resets 3pm"));
        Assert.False(QuotaSignal.IsQuotaFailure("Claude 執行失敗：error_during_execution"));

        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_003_600),
            QuotaSignal.ResetHint("usage limit reached|1800003600", now));
        Assert.Null(QuotaSignal.ResetHint("usage limit reached|1700000000", now)); // a past time is ignored

        var later = now.AddHours(1);
        var latest = now.AddHours(2);
        var quota = new AccountQuota(Executor.Codex, now,
        [
            new QuotaWindow("5h", "5 小時", 100, later),
            new QuotaWindow("week", "每週", 100, latest),
            new QuotaWindow("other", "x", 40, now.AddSeconds(99_999)),
        ]);
        Assert.Equal(latest, QuotaSignal.ResetAt(quota, now));
        Assert.True(QuotaSignal.Blocks(quota, now));
        Assert.False(QuotaSignal.Blocks(quota, now.AddMinutes(20))); // a stale sample blocks nothing
        var resetSoon = new AccountQuota(Executor.Codex, now, [new QuotaWindow("5h", "5 小時", 100, now.AddSeconds(300))]);
        Assert.False(QuotaSignal.Blocks(resetSoon, now.AddSeconds(301))); // a window past its reset no longer blocks

        var wait = new FlowQuotaWait(Codex, "執行任務", later, "") { Since = now };
        Assert.False(QuotaSignal.Recovered(wait, quota, now.AddSeconds(60)));
        Assert.True(QuotaSignal.Recovered(wait, null, later));
        wait.ResetAt = null;
        var fresh = new AccountQuota(Executor.Codex, now.AddSeconds(120),
            [new QuotaWindow("5h", "5 小時", 3, null)]);
        Assert.True(QuotaSignal.Recovered(wait, fresh, now.AddSeconds(180)));
    }

    [Fact]
    public void OldFlowsDecodeWithDefaults()
    {
        var flow = new ProjectFlow { Title = "舊", Folder = "/tmp", Members = [Claude, Codex] };
        flow.Plan = new FlowPlan
        {
            Objective = "",
            Scope = "",
            Deliverables = [],
            Acceptance = [],
            Rounds = [new FlowRound("r1", "a", "")],
            Tasks = [new FlowTask("t1", "r1", "x", Claude, "")],
        };
        var decoded = CoreJson.Deserialize<ProjectFlow>(CoreJson.Serialize(flow))!;
        Assert.Equal(2, decoded.MaxParallel);
        Assert.Empty(decoded.QuotaWaits);
        Assert.Empty(decoded.Plan!.Tasks[0].Files);
        Assert.Equal(0, decoded.Plan!.Tasks[0].Waivers);
        Assert.Equal(1, decoded.Segment);
        Assert.Equal(ReviewLevel.Standard, decoded.ReviewLevel);
    }

    [Fact]
    public void ParallelCopiesMergeUnlessTheyTouchTheSameFile()
    {
        var baseFolder = Path.Combine(Path.GetTempPath(), "airoom-parallel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Path.Combine(baseFolder, "project");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "a.txt"), "v1");
            File.WriteAllText(Path.Combine(project, "b.txt"), "v1");
            var space = new ProjectWorkspace(Path.Combine(baseFolder, "store"), Guid.NewGuid());
            var roundID = Guid.NewGuid();
            _ = space.RoundFolder(roundID, project);
            var (one, two, three) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var first = space.TaskFolder(one, roundID);
            var second = space.TaskFolder(two, roundID);
            var third = space.TaskFolder(three, roundID);
            File.WriteAllText(Path.Combine(first, "a.txt"), "claude");
            File.WriteAllText(Path.Combine(second, "b.txt"), "codex");
            File.WriteAllText(Path.Combine(third, "a.txt"), "clash");
            space.MergeTask(one, roundID);
            space.MergeTask(two, roundID);
            Assert.Throws<WorkspaceError>(() => space.MergeTask(three, roundID)); // the same file changed under it
            Assert.Equal(new[] { "a.txt", "b.txt" }, space.RoundChanges(roundID).Select(change => change.Path).ToHashSet());
            Assert.NotNull(space.ExistingTaskFolder(three)); // a conflicted copy stays for a look
        }
        finally
        {
            try { Directory.Delete(baseFolder, recursive: true); } catch { }
        }
    }

    static FlowTask Clone(FlowTask task) =>
        CoreJson.Deserialize<FlowTask>(CoreJson.Serialize(task))!;
}

/// <summary>With three or more members the lead rules on review disputes, as upstream does.</summary>
public class LeadAdjudicationTests
{
    static readonly MemberID Claude = MemberID.Claude;
    static readonly MemberID Codex = MemberID.Codex;
    static readonly MemberID Ds = new("m-ds0001");
    static readonly MemberID[] Three = [Claude, Codex, Ds];

    static FlowTask DisputedTask(MemberID author, MemberID reviewer) => new("t", "r", "登入頁", author, "做登入頁", ["有錯誤提示"])
    {
        Reviewer = reviewer,
        OpenFindings =
        [
            new FlowFinding("f1", "blocking", "login.html", "加上記住我"),
            new FlowFinding("f2", "should-fix", "login.css", "按鈕改成藍色"),
        ],
        Disputes = [new FlowDispute("f1", "需求沒有記住我"), new FlowDispute("f2", "設計稿是橘色")],
        Summary = "改好錯誤提示",
        Returns = 1,
        Status = FlowTaskStatus.Disputed,
    };

    [Fact]
    public void WithThreeMembersTheLeadIsNotTheFirstReviewer()
    {
        var byCodex = new FlowTask("a", "r", "a", Codex, "");
        Assert.Equal(Ds, ProjectFlowEngine.ReviewerFor(byCodex, Claude, Three)); // a non-lead reviews, so the lead can rule on disputes
        var byLead = new FlowTask("b", "r", "b", Claude, "");
        Assert.Equal(Codex, ProjectFlowEngine.ReviewerFor(byLead, Claude, Three));
    }

    [Fact]
    public void ReviewsAreSpreadAndKept()
    {
        var reviewed = new FlowTask("a", "r", "a", Claude, "") { Reviewer = Codex };
        var next = new FlowTask("b", "r", "b", Claude, "");
        Assert.Equal(Ds, ProjectFlowEngine.ReviewerFor(next, Claude, Three, [reviewed])); // the member with fewer reviews goes first
        Assert.Equal(Codex, ProjectFlowEngine.ReviewerFor(reviewed, Claude, Three, [reviewed])); // a task keeps its reviewer
        Assert.Equal(Ds, ProjectFlowEngine.ReviewerFor(reviewed, Claude, Three, [reviewed],
            new HashSet<MemberID> { Codex })); // a reviewer out of quota is replaced
        var byCodex = new FlowTask("c", "r", "c", Codex, "");
        Assert.Equal(Claude, ProjectFlowEngine.ReviewerFor(byCodex, Claude, Three, unavailable: new HashSet<MemberID> { Ds })); // the lead reviews when no one else can
    }

    [Fact]
    public void TwoMembersStillReviewEachOther()
    {
        var task = new FlowTask("t", "r", "x", Claude, "");
        Assert.Equal(Codex, ProjectFlowEngine.ReviewerFor(task, Claude, [Claude, Codex]));
        Assert.Equal(Codex, ProjectFlowEngine.ReviewerFor(task, Codex, [Claude, Codex]));
    }

    [Fact]
    public void TheLeadRulesOnlyWhenNeitherAuthorNorReviewer()
    {
        Assert.True(ProjectFlowEngine.LeadCanRule(DisputedTask(Codex, Ds), Claude));
        Assert.False(ProjectFlowEngine.LeadCanRule(DisputedTask(Claude, Ds), Claude));
        Assert.False(ProjectFlowEngine.LeadCanRule(DisputedTask(Codex, Claude), Claude));
        Assert.False(ProjectFlowEngine.LeadCanRule(DisputedTask(Codex, Ds), null));
    }

    [Fact]
    public void ThePromptCarriesEachDisputeAndAsksForEveryRuling()
    {
        var names = new Dictionary<string, string>
        {
            [Claude.RawValue] = "Claude", [Codex.RawValue] = "GPT", [Ds.RawValue] = "藍色大肥魚",
        };
        var prompt = ProjectFlowEngine.AdjudicationPrompt(DisputedTask(Codex, Ds), Claude, names, "老闆");
        Assert.Contains("你是 Claude，AIROOM 專案的負責人", prompt);
        Assert.Contains("GPT 修正任務「登入頁」時，對 藍色大肥魚 的部分複核意見提出異議", prompt);
        Assert.Contains("[f1][blocking] login.html：加上記住我", prompt);
        Assert.Contains("作者的理由：設計稿是橘色", prompt);
        Assert.Contains("只讀查看，不要修改檔案", prompt);
        Assert.Contains("交給老闆決定", prompt);
        Assert.Contains(LoungeEngine.QuoteRule, prompt);
    }

    [Fact]
    public void RulingsParseOnlyWhenEveryDisputeIsAnswered()
    {
        var disputes = DisputedTask(Codex, Ds).Disputes;
        var full = ProjectFlowEngine.ParseAdjudication(
            """好的。{"rulings":[{"finding":"f1","verdict":"author","reason":"需求沒提"},{"finding":"f2","verdict":"review","reason":"設計稿是藍色"},{"finding":"f9","verdict":"author","reason":"多的"}]}""",
            disputes);
        Assert.NotNull(full);
        Assert.Equal(new[] { FlowVerdict.Author, FlowVerdict.Review }, full!.Select(ruling => ruling.Verdict!.Value));
        Assert.Equal("設計稿是藍色", full[1].Reason);
        Assert.Null(ProjectFlowEngine.ParseAdjudication(
            """{"rulings":[{"finding":"f1","verdict":"author","reason":"x"}]}""", disputes)); // a missing ruling hands everything to the owner
        Assert.Null(ProjectFlowEngine.ParseAdjudication("我覺得作者對", disputes));
        var unsure = ProjectFlowEngine.ParseAdjudication(
            """{"rulings":[{"finding":"f1","verdict":"unsure","reason":"要老闆取捨"},{"finding":"f2","verdict":"uphold-review","reason":"y"}]}""",
            disputes);
        Assert.NotNull(unsure);
        Assert.Null(unsure![0].Verdict);
        Assert.Equal(FlowVerdict.Review, unsure[1].Verdict);
    }

    [Fact]
    public void TheLeadsRulingIsRecordedWithItsGrounds()
    {
        var task = DisputedTask(Codex, Ds);
        ProjectFlowEngine.ApplyRulings(task, new Dictionary<string, FlowVerdict>
        {
            ["f1"] = FlowVerdict.Author, ["f2"] = FlowVerdict.Review,
        }, Claude, new Dictionary<string, string> { ["f1"] = "需求沒提", ["f2"] = "設計稿是藍色" });
        Assert.Equal(FlowTaskStatus.Fixing, task.Status);
        Assert.All(task.Rulings, ruling => Assert.Equal(Claude, ruling.Judge));
        Assert.Equal("設計稿是藍色", task.Rulings.Last().Note);
        Assert.Equal((0, 1), (task.Returns, task.Waivers));
        var fix = ProjectFlowEngine.FixSection(task, "老闆");
        Assert.Contains("裁定維持複核", fix);
        Assert.Contains("裁定理由：設計稿是藍色", fix);
        var card = ProjectFlowEngine.RulingCard(task, task.Rulings, new Dictionary<string, string> { [Claude.RawValue] = "Claude" });
        Assert.Contains("（負責人 Claude 裁定）", card);
        Assert.Contains("理由：需求沒提", card);
        var ownerCard = ProjectFlowEngine.RulingCard(task, [new FlowRuling(task.OpenFindings[0], "r", FlowVerdict.Review)]);
        Assert.DoesNotContain("負責人", ownerCard); // the owner's ruling names no lead
    }
}
