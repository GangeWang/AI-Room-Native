using AIROOMCore;

namespace AIROOMApp.ViewModels;

/// <summary>
/// The project room (項目會議室), ported from the macOS WorkbenchModel's flow
/// section: one job at a time, every mutation on the state document, and a
/// failure pauses the flow instead of being retried by itself.
/// </summary>
public sealed partial class WorkbenchModel
{
    readonly object flowGate = new();
    volatile bool flowRunning;
    volatile bool cancelFlow;
    string? flowJobActivity;
    readonly Dictionary<Guid, string> flowActivities = [];
    readonly HashSet<Guid> flowRunIDs = [];
    Guid? pendingFlowRun;
    /// <summary>The lead currently ruling on a dispute, by task — the owner's card shows this.</summary>
    readonly Dictionary<Guid, MemberID> leadRulings = [];
    Guid? selectedFlowID;
    int flowVersion;
    System.Timers.Timer? quotaWaitTimer;

    /// <summary>Raised whenever flow state changes (tasks, rounds, waits).</summary>
    public int FlowVersion => flowVersion;

    public bool FlowRunning => flowRunning;

    public string? FlowActivity
    {
        get
        {
            lock (flowGate)
            {
                if (flowActivities.Count > 0) return string.Join("\n", flowActivities.Values.OrderBy(value => value));
                return flowJobActivity;
            }
        }
    }

    public Guid? SelectedFlowID
    {
        get => selectedFlowID;
        set => SetProperty(ref selectedFlowID, value);
    }

    /// <summary>Who is ruling on a disputed task right now, if anyone.</summary>
    public MemberID? LeadRulingFor(Guid taskID)
    {
        lock (flowGate) return leadRulings.GetValueOrDefault(taskID);
    }

    public List<ProjectFlow> Flows()
    {
        lock (Archive) return Archive.ProjectFlows is { } flows ? flows : [];
    }

    /// <summary>A deep clone the UI can read without touching live state.</summary>
    public ProjectFlow? FlowSnapshot(Guid id)
    {
        lock (Archive)
        {
            var flow = FindFlow(id);
            return flow is null ? null : CoreJson.Deserialize<ProjectFlow>(CoreJson.Serialize(flow))!;
        }
    }

    public ProjectFlow? FirstFlowSnapshot()
    {
        lock (Archive)
        {
            var first = Archive.ProjectFlows?.FirstOrDefault();
            return first is null ? null : CoreJson.Deserialize<ProjectFlow>(CoreJson.Serialize(first))!;
        }
    }

    void BumpFlows()
    {
        RunOnDispatcher(() =>
        {
            flowVersion++;
            OnPropertyChanged(nameof(FlowVersion));
        });
    }

    // MARK: Lifecycle

    /// <summary>Notifies the owner when an expired quota wait should be back; the work still waits for 接著做.</summary>
    void FlowQuotaTick()
    {
        List<(string Title, string Body)> posts = [];
        lock (Archive)
        {
            if (Archive.ProjectFlows is not { } flows) return;
            foreach (var flow in flows)
            {
                foreach (var wait in flow.QuotaWaits.Where(wait => !wait.Ready))
                {
                    if (!QuotaSignal.Recovered(wait, QuotaOf(wait.Agent))) continue;
                    wait.Ready = true;
                    posts.Add(($"{MemberName(wait.Agent)} 的額度應該恢復了",
                        $"專案「{flow.Title}」：按「接著做」繼續，不會自動重送。"));
                }
            }
        }
        foreach (var (title, body) in posts) Notify(title, body);
    }

    void StartQuotaWaitTimer()
    {
        lock (flowGate)
        {
            if (quotaWaitTimer is not null) return;
            quotaWaitTimer = new System.Timers.Timer(30_000) { AutoReset = true };
            quotaWaitTimer.Elapsed += (_, _) => FlowQuotaTick();
            quotaWaitTimer.Start();
        }
    }

    int FlowIndex(Guid id)
    {
        var flows = Archive.ProjectFlows;
        return flows is null ? -1 : flows.FindIndex(flow => flow.Id == id);
    }

    ProjectFlow? FindFlow(Guid id) => FlowIndex(id) is >= 0 and var index ? Archive.ProjectFlows![index] : null;

    ProjectWorkspace? FlowWorkspace(Guid id)
    {
        if (Persistence is null) return null;
        try { return new ProjectWorkspace(Persistence.Root, id); }
        catch { return null; }
    }

    public void CreateFlow(string title, string folder, IReadOnlyList<MemberID> members, ReviewLevel level)
    {
        if (!StorageAvailable) return;
        var path = folder.Trim().TrimEnd('\\', '/');
        if (path.Length == 0 || !Directory.Exists(path))
        {
            StatusMessage = "請選擇存在的專案資料夾。";
            return;
        }
        var chosen = EnabledMemberIDs.Where(members.Contains).ToList();
        if (chosen.Count < 2)
        {
            StatusMessage = "專案至少需要兩位已啟用的成員。";
            return;
        }
        var name = title.Trim();
        if (name.Length == 0) name = Path.GetFileName(path);
        if (name.Length > 80) name = name[..80];
        var flow = new ProjectFlow { Title = name, Folder = path, Members = chosen, ReviewLevel = level };
        // A hidden lounge meeting that may read the project files; no hand-raising.
        var room = new TeamRoom
        {
            Title = $"專案：{flow.Title}",
            Participants = chosen,
            Lounge = new LoungeState { Style = LoungeStyle.Meeting, HandRaise = false },
            ProjectID = flow.Id,
            ReadFolder = path,
        };
        flow.RoomID = room.Id;
        lock (Archive)
        {
            (Archive.ProjectFlows ??= []).Insert(0, flow);
            (Archive.Rooms ??= []).Add(room);
        }
        SelectedFlowID = flow.Id;
        StartQuotaWaitTimer();
        Persist();
        BumpFlows();
    }

    /// <summary>A line in the project's meeting room: the plan, the owner's decisions and the round
    /// reports are recorded there as the original does, so the room reads as one history.</summary>
    void PostFlowRecord(Guid id, MemberID? agent, string text)
    {
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null || flow.RoomID is not { } roomID) return;
            var room = FindRoom(roomID);
            if (room is null) return;
            room.Entries.Add(agent is { } who
                ? new RoomEntry { Speaker = Speaker.Assistant, Agent = who, DisplayName = MemberName(who), Body = text }
                : new RoomEntry { Speaker = Speaker.User, DisplayName = "你", Body = text });
            room.UpdatedAt = DateTimeOffset.Now;
        }
        PersistRooms();
    }

    /// <summary>Step 1: talk it through. Members may read the project files but not change them.</summary>
    public void SayInProject(Guid id, string text)
    {
        if (flowRunning) return;
        var value = text.Trim();
        if (value.Length == 0 || value.Length > 8_000) return;
        Guid roomID;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow?.RoomID is not { } found) return;
            roomID = found;
        }
        DispatchRoomMessage(roomID, value, null, "你");
    }

    /// <summary>Step 2 → 3: the chosen lead drafts the plan.</summary>
    public void ChooseFlowLead(Guid id, MemberID agent)
    {
        if (flowRunning) return;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null || !flow.Members.Contains(agent)) return;
            flow.Lead = agent;
            flow.Step = FlowStep.Leader;
        }
        Persist();
        BumpFlows();
        DraftPlan(id);
    }

    public void RequestPlanChange(Guid id, string note)
    {
        if (flowRunning) return;
        var value = note.Trim();
        if (value.Length == 0) return;
        if (value.Length > 4_000) value = value[..4_000];
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null) return;
            flow.PlanRequests.Add(value);
        }
        Persist();
        DraftPlan(id);
    }

    void DraftPlan(Guid id)
    {
        MemberID lead;
        string prompt, flowTitle;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null || flow.Lead is not { } theLead) return;
            lead = theLead;
            flowTitle = flow.Title;
            var discussion = flow.RoomID is { } roomID && FindRoom(roomID) is { } room
                ? RoomPromptBuilder.Snapshot(room, maxEntries: 60, maxCharacters: 14_000,
                    language: Archive.Settings.Language).Transcript
                : "";
            prompt = ProjectFlowEngine.PlanPrompt(flow, discussion, MemberNames(), Archive.Settings.UserName,
                WorkPersona(lead));
        }
        var members = FlowSnapshot(id)?.Members ?? [];
        FlowJob(id, $"{MemberName(lead)} 正在寫方案……", async () =>
        {
            var folder = FlowFolder(id);
            var text = await FlowCall(id, lead, prompt, folder, ExecutionAccess.ReadFiles, stage: "起草方案");
            lock (Archive)
            {
                var flow = FindFlow(id);
                if (flow is null) return;
                var version = (flow.Plan?.Version ?? 0) + 1;
                var plan = ProjectFlowEngine.ParsePlan(text, members, version);
                // A revision keeps accepted rounds; the new plan replaces what is left.
                if (flow.Plan is { } old)
                {
                    var done = old.Rounds.Where(round => round.Status == FlowRoundStatus.Accepted).ToList();
                    var doneKeys = done.Select(round => round.Key).ToHashSet();
                    var prefix = $"v{version}-";
                    plan.Rounds = done.Concat(plan.Rounds.Select(round =>
                    {
                        var copy = CoreJson.Deserialize<FlowRound>(CoreJson.Serialize(round))!;
                        copy.Key = prefix + round.Key;
                        return copy;
                    })).ToList();
                    plan.Tasks = old.Tasks.Where(task => doneKeys.Contains(task.RoundKey)).Concat(plan.Tasks.Select(task =>
                    {
                        var copy = CoreJson.Deserialize<FlowTask>(CoreJson.Serialize(task))!;
                        copy.Key = prefix + task.Key;
                        copy.RoundKey = prefix + task.RoundKey;
                        copy.Dependencies = task.Dependencies.Select(dependency => prefix + dependency).ToList();
                        return copy;
                    })).ToList();
                    flow.CurrentRound = done.Count;
                }
                flow.Plan = plan;
                flow.Step = FlowStep.Plan;
                PostFlowRecord(id, lead, ProjectFlowEngine.PlanCard(plan));
            }
            Notify("方案寫好了", flowTitle);
        });
    }

    /// <summary>Step 3 → 4: the user confirms the plan and starts work.</summary>
    public void ConfirmPlan(Guid id, ReviewLevel reviewLevel, string note = "")
    {
        if (flowRunning) return;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null || flow.Plan is null || flow.Step != FlowStep.Plan) return;
            flow.ReviewLevel = reviewLevel;
        }
        BumpFlows();
        StartRound(id, note);
    }

    /// <summary>按原計劃開始 (with an optional 附言 for this round).</summary>
    public void StartRound(Guid id, string note)
    {
        if (flowRunning) return;
        Guid roundID;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow?.Plan is not { } plan) return;
            var roundIndex = flow.CurrentRound;
            if (roundIndex < 0 || roundIndex >= plan.Rounds.Count) return;
            var round = plan.Rounds[roundIndex];
            round.StartNote = note.Trim();
            if (round.StartNote.Length > 4_000) round.StartNote = round.StartNote[..4_000];
            round.Status = FlowRoundStatus.Executing;
            roundID = round.Id;
            PostFlowRecord(id, null, ProjectFlowEngine.StartCard(plan.Version, flow.ReviewLevel, round, round.StartNote));
            flow.Step = FlowStep.Executing;
            flow.Paused = false;
            flow.QuotaWaits = [];
        }
        Persist();
        BumpFlows();
        RunRound(id);
    }

    public void PauseFlow(Guid id)
    {
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null) return;
            flow.Paused = true;
        }
        lock (flowGate) flowJobActivity = "目前的任務做完後就暫停，不再派新任務。";
        Persist();
        BumpFlows();
    }

    /// <summary>繼續 / 接著做: failed tasks go again (a fix or review resumes on its copy); quota waits are cleared.</summary>
    public void ResumeFlow(Guid id)
    {
        if (flowRunning) return;
        Guid? roundID = null;
        bool run;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null) return;
            flow.Paused = false;
            var executors = flow.QuotaWaits.Select(wait => ExecutorOf(wait.Agent)).ToHashSet();
            foreach (var executor in executors) RefreshQuota(executor);
            flow.QuotaWaits = [];
            // A round report that stopped part-way is written again from the finished tasks.
            if (flow.ActiveRound is { } active && active.Status == FlowRoundStatus.Checking)
            {
                active.Status = FlowRoundStatus.Executing;
            }
            // A failed task is retried only because the user pressed 繼續.
            var workspace = FlowWorkspace(id);
            if (flow.ActiveRound is { } round && flow.Plan is { } plan && workspace is not null)
            {
                roundID = round.Id;
                foreach (var task in plan.Tasks)
                {
                    if (task.RoundKey != round.Key) continue;
                    if (task.Status is FlowTaskStatus.Done or FlowTaskStatus.Disputed or FlowTaskStatus.Pending) continue;
                    var step = task.Status == FlowTaskStatus.Failed ? task.FailedAt : task.Status;
                    var keepsCopy = (step == FlowTaskStatus.Fixing || step == FlowTaskStatus.Reviewing)
                        && workspace.ExistingTaskFolder(task.Id) is not null;
                    task.Status = keepsCopy ? step!.Value : FlowTaskStatus.Pending;
                    task.FailedAt = null;
                }
            }
            run = flow.ActiveRound?.Status == FlowRoundStatus.Executing;
        }
        Persist();
        BumpFlows();
        if (run) RunRound(id);
    }

    public void StopFlow()
    {
        cancelFlow = true;
        lock (flowGate)
        {
            foreach (var runID in flowRunIDs) runner.Cancel(runID);
        }
        BumpFlows();
    }

    public void SetFlowParallel(Guid id, int value)
    {
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null) return;
            flow.MaxParallel = Math.Min(3, Math.Max(1, value));
        }
        Persist();
        BumpFlows();
    }

    void SetFlowActivity(Guid key, string? text)
    {
        lock (flowGate)
        {
            if (text is null) flowActivities.Remove(key);
            else flowActivities[key] = text;
        }
        BumpFlows();
    }

    // MARK: The round loop

    /// <summary>Members who get no new task: out of quota (a wait not yet recovered, or a fresh sample at 100%).</summary>
    HashSet<MemberID> UnavailableMembers(Guid id)
    {
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null) return [];
            var waits = flow.QuotaWaits;
            // Members on the same program share its account, so one member's wait holds them all.
            var waiting = waits.Where(wait => !wait.Ready).Select(wait => ExecutorOf(wait.Agent)).ToHashSet();
            var unavailable = waits.Where(wait => !wait.Ready).Select(wait => wait.Agent).ToHashSet();
            foreach (var member in flow.Members.Where(member => waiting.Contains(ExecutorOf(member))))
            {
                unavailable.Add(member);
            }
            foreach (var agent in flow.Members)
            {
                if (unavailable.Contains(agent) || !QuotaSignal.Blocks(QuotaOf(agent))) continue;
                if (!waits.Any(wait => wait.Agent == agent))
                {
                    AddQuotaWait(id, agent, "派新任務前", "最新的官方額度顯示已用完，沒有派新任務給 TA。");
                }
                unavailable.Add(agent);
            }
            return unavailable;
        }
    }

    void UpdateTask(Guid id, Guid taskID, Action<FlowTask> change)
    {
        lock (Archive)
        {
            var task = FindFlow(id)?.Plan?.Tasks.FirstOrDefault(candidate => candidate.Id == taskID);
            if (task is null) return;
            change(task);
        }
        Persist();
        BumpFlows();
    }

    FlowTask? TaskById(Guid id, Guid taskID)
    {
        lock (Archive)
        {
            return FindFlow(id)?.Plan?.Tasks.FirstOrDefault(candidate => candidate.Id == taskID);
        }
    }

    /// <summary>Step 4: ready tasks run side by side (one per member, files not overlapping, up to
    /// the flow's limit), each in its own copy. A failure doesn't stop the others.</summary>
    void RunRound(Guid id)
    {
        Guid roundID;
        int roundNumber;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow?.ActiveRound is not { } round) return;
            roundID = round.Id;
            roundNumber = flow.CurrentRound;
        }
        var workspace = FlowWorkspace(id);
        if (workspace is null) return;
        FlowJob(id, $"第 {roundNumber + 1} 輪執行中……", () => RunRoundBodyAsync(id, roundID, workspace));
    }

    async Task RunRoundBodyAsync(Guid id, Guid roundID, ProjectWorkspace workspace)
    {
        string original;
        lock (Archive) original = FindFlow(id)?.Folder ?? throw NativeCLIError.InvalidWorkspace("");
        _ = workspace.RoundFolder(roundID, original);
        var failures = new List<string>();
        var active = new HashSet<Guid>();
        var running = new List<Task<(Guid TaskID, string? Failure)>>();
        while (true)
        {
            FlowPlan? plan;
            bool paused;
            int maxParallel;
            HashSet<MemberID> unavailable;
            lock (Archive)
            {
                var flow = FindFlow(id);
                plan = flow?.Plan;
                paused = flow?.Paused ?? true;
                maxParallel = flow?.MaxParallel ?? 1;
                unavailable = UnavailableMembers(id);
            }
            if (!cancelFlow && !paused && plan is not null)
            {
                var round = plan.Rounds.FirstOrDefault(candidate => candidate.Id == roundID);
                if (round is not null)
                {
                    foreach (var task in ProjectFlowEngine.ReadyTasks(round, plan, active, maxParallel, unavailable))
                    {
                        active.Add(task.Id);
                        running.Add(Task.Run(() => RunTaskAsync(id, task.Id, roundID, workspace)));
                    }
                }
            }
            if (running.Count == 0) break;
            var finished = await Task.WhenAny(running);
            running.Remove(finished);
            var (taskID, failure) = await finished;
            active.Remove(taskID);
            SetFlowActivity(taskID, null);
            if (failure is not null)
            {
                UpdateTask(id, taskID, task =>
                {
                    if (task.Status != FlowTaskStatus.Failed)
                    {
                        task.FailedAt = task.Status;
                        task.Status = FlowTaskStatus.Failed;
                    }
                    task.Error = failure;
                });
                failures.Add(failure);
            }
        }
        lock (Archive)
        {
            var flow = FindFlow(id);
            var plan = flow?.Plan;
            if (flow is null || plan is null) return;
            var round = plan.Rounds.FirstOrDefault(candidate => candidate.Id == roundID);
            if (round is null) return;
            var tasks = plan.TasksIn(round);
            if (cancelFlow)
            {
                flow.Paused = true;
                return;
            }
            bool allDone;
            if (tasks.All(task => task.Status == FlowTaskStatus.Done))
            {
                allDone = true;
            }
            else if (failures.Count > 0)
            {
                flow.Paused = true;
                StatusMessage = $"專案工作暫停: {failures[0]}";
                return;
            }
            else if (tasks.Any(task => task.Status is not (FlowTaskStatus.Done or FlowTaskStatus.Disputed)))
            {
                // Only tasks of members out of quota are left; a disputed one waits for its ruling.
                flow.Paused = true;
                var names = flow.QuotaWaits.Select(wait => MemberName(wait.Agent)).ToHashSet();
                StatusMessage = $"專案工作暫停：等 {string.Join("、", names.Order())} 的額度恢復，再按「接著做」。";
                return;
            }
            else
            {
                allDone = false;
            }
            // The check runs inside this job: a failure pauses the flow like any other call.
        }
        await CheckRoundAsync(id, roundID, workspace);
    }

    /// <summary>One task: work → (深入複核) review → fix …, then merge into the round copy.
    /// Starts from where the task stands, so a ruled dispute or a failed fix resumes on its copy.</summary>
    async Task<(Guid TaskID, string? Failure)> RunTaskAsync(Guid id, Guid taskID, Guid roundID, ProjectWorkspace workspace)
    {
        try
        {
            await RunTaskBodyAsync(id, taskID, roundID, workspace);
            return (taskID, null);
        }
        catch (Exception error)
        {
            return (taskID, error.Message);
        }
    }

    async Task RunTaskBodyAsync(Guid id, Guid taskID, Guid roundID, ProjectWorkspace workspace)
    {
        MemberID lead;
        List<MemberID> members;
        ReviewLevel level;
        FlowRound round;
        string folder;
        bool needsWork;
        lock (Archive)
        {
            var flow = FindFlow(id) ?? throw NativeCLIError.ProcessFailed("專案已刪除");
            if (flow.Lead is not { } theLead) return;
            lead = theLead;
            members = flow.Members;
            level = flow.ReviewLevel;
            round = flow.Plan?.Rounds.FirstOrDefault(candidate => candidate.Id == roundID)
                ?? throw NativeCLIError.ProcessFailed("這一輪已不在方案中");
            var start = flow.Plan?.Tasks.FirstOrDefault(candidate => candidate.Id == taskID)
                ?? throw NativeCLIError.ProcessFailed("任務已不在方案中");
            if (start.Status != FlowTaskStatus.Pending && workspace.ExistingTaskFolder(taskID) is { } existing)
            {
                folder = existing;
                needsWork = start.Status != FlowTaskStatus.Reviewing;
            }
            else
            {
                folder = workspace.TaskFolder(taskID, roundID);
                start.Status = FlowTaskStatus.Running;
                needsWork = true;
            }
            start.Error = null;
            start.FailedAt = null;
        }
        Persist();
        BumpFlows();
        while (true)
        {
            FlowTask? current;
            FlowPlan? plan;
            HashSet<MemberID> unavailable;
            lock (Archive)
            {
                var flow = FindFlow(id);
                if (flow is null) return;
                current = flow.Plan?.Tasks.FirstOrDefault(candidate => candidate.Id == taskID);
                plan = flow.Plan;
                if (current is null || plan is null) return;
                unavailable = UnavailableMembers(id);
            }
            if (needsWork && unavailable.Contains(current.Member))
            {
                // Out of quota: the task waits where it stands; nothing is sent.
                if (current.Status == FlowTaskStatus.Running)
                {
                    UpdateTask(id, taskID, task => task.Status = FlowTaskStatus.Pending);
                }
                return;
            }
            if (needsWork)
            {
                SetFlowActivity(taskID, current.Status == FlowTaskStatus.Fixing
                    ? $"{MemberName(current.Member)} 正在修正「{current.Title}」……"
                    : $"{MemberName(current.Member)} 正在做「{current.Title}」……");
                var prompt = ProjectFlowEngine.TaskPrompt(current, FlowSnapshot(id)!, round, MemberNames(),
                    Archive.Settings.UserName, WorkPersona(current.Member));
                var reply = await FlowCall(id, current.Member, prompt, folder, ExecutionAccess.StagedWrite,
                    stage: $"執行任務「{current.Title}」");
                var parsed = level == ReviewLevel.Deep && current.Status == FlowTaskStatus.Fixing
                    ? ProjectFlowEngine.ParseDisputes(reply, current.OpenFindings, current.Rulings)
                    : (reply, new List<FlowDispute>());
                UpdateTask(id, taskID, task =>
                {
                    task.Summary = parsed.Item1;
                    task.Changes = workspace.TaskChanges(taskID);
                });
                if (parsed.Item2.Count > 0)
                {
                    // The copy waits as it is. With three or more members the lead (neither author
                    // nor reviewer) rules first; what the lead can't settle goes to the owner.
                    UpdateTask(id, taskID, task =>
                    {
                        task.Status = FlowTaskStatus.Disputed;
                        task.Disputes = parsed.Item2;
                    });
                    if (await LeadRulesAsync(id, taskID, roundID, folder)
                        && TaskById(id, taskID) is { } ruled && ruled.Status != FlowTaskStatus.Disputed)
                    {
                        needsWork = ruled.Status == FlowTaskStatus.Fixing;
                        continue;
                    }
                    Notify("有複核異議等你裁定", $"{FlowSnapshot(id)?.Title ?? "專案"}：{current.Title}");
                    return;
                }
            }
            needsWork = true;
            // 深入複核: another member checks each task; up to two returns.
            if (level != ReviewLevel.Deep) break;
            var checkedTask = TaskById(id, taskID);
            if (checkedTask is null) return;
            MemberID reviewer;
            lock (Archive)
            {
                var flow = FindFlow(id);
                reviewer = ProjectFlowEngine.ReviewerFor(checkedTask, lead, members,
                    flow?.Plan?.Tasks, unavailable);
            }
            UpdateTask(id, taskID, task =>
            {
                task.Status = FlowTaskStatus.Reviewing;
                task.Reviewer = reviewer;
            });
            lock (Archive)
            {
                if (UnavailableMembers(id).Contains(reviewer)) return;
            }
            SetFlowActivity(taskID, $"{MemberName(reviewer)} 正在複核「{checkedTask.Title}」……");
            var reviewPrompt = ProjectFlowEngine.TaskReviewPrompt(checkedTask, reviewer, MemberNames(),
                Archive.Settings.UserName, round, WorkPersona(reviewer));
            var review = ProjectFlowEngine.ParseReview(await FlowCall(id, reviewer, reviewPrompt, folder,
                ExecutionAccess.ReadFiles, stage: $"複核「{checkedTask.Title}」"));
            if (review.Pass || checkedTask.Returns >= ProjectFlowEngine.MaxReturns)
            {
                UpdateTask(id, taskID, task =>
                {
                    task.Findings = review.Pass ? "" : review.Text;
                    task.OpenFindings = review.Pass ? [] : review.Findings;
                });
                break;
            }
            UpdateTask(id, taskID, task =>
            {
                task.Status = FlowTaskStatus.Fixing;
                task.Findings = review.Text;
                task.OpenFindings = review.Findings;
                task.Returns += 1;
            });
        }
        try
        {
            workspace.MergeTask(taskID, roundID);
        }
        catch (WorkspaceError conflict) when (conflict.Message.StartsWith("原資料夾已有異動", StringComparison.Ordinal))
        {
            // Another task in this round changed the same file. 繼續 redoes this one on the latest copy.
            var message = "和同一輪另一項任務改到同一個檔案，沒有合併。按「繼續」會在最新的副本上重做這項任務。" +
                $"（{conflict.Message["原資料夾已有異動，未寫入：".Length..]}）";
            UpdateTask(id, taskID, task =>
            {
                task.Status = FlowTaskStatus.Failed;
                task.FailedAt = null;
                task.Error = message;
            });
            throw NativeCLIError.ProcessFailed(message);
        }
        UpdateTask(id, taskID, task => task.Status = FlowTaskStatus.Done);
    }

    /// <summary>The lead rules on the author's disputes when the lead is neither author nor reviewer
    /// (only possible with three or more members). True when every dispute was settled; false
    /// leaves them to the owner, with the lead's notes on any the lead couldn't decide.</summary>
    async Task<bool> LeadRulesAsync(Guid id, Guid taskID, Guid roundID, string folder)
    {
        MemberID lead;
        FlowTask current;
        string flowTitle;
        FlowRound round;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null || flow.Lead is not { } theLead) return false;
            lead = theLead;
            current = flow.Plan?.Tasks.FirstOrDefault(candidate => candidate.Id == taskID)
                ?? throw NativeCLIError.ProcessFailed("任務已不在方案中");
            if (!flow.Members.Contains(lead) || !ProjectFlowEngine.LeadCanRule(current, lead)) return false;
            flowTitle = flow.Title;
            round = flow.Plan?.Rounds.FirstOrDefault(candidate => candidate.Id == roundID) ?? new FlowRound("", "", "");
        }
        void HandBack(string note)
        {
            UpdateTask(id, taskID, task =>
            {
                foreach (var dispute in task.Disputes.Where(dispute => dispute.LeadNote is null))
                {
                    dispute.LeadNote = note;
                }
            });
        }
        if (UnavailableMembers(id).Contains(lead))
        {
            HandBack($"負責人 {MemberName(lead)} 的額度用完了，沒有裁定。");
            return false;
        }
        lock (flowGate) leadRulings[taskID] = lead;
        try
        {
            SetFlowActivity(taskID, $"負責人 {MemberName(lead)} 正在裁定「{current.Title}」的複核異議……");
            string reply;
            try
            {
                var prompt = ProjectFlowEngine.AdjudicationPrompt(current, lead, MemberNames(),
                    Archive.Settings.UserName, round, WorkPersona(lead));
                reply = await FlowCall(id, lead, prompt, folder, ExecutionAccess.ReadFiles,
                    stage: $"裁定「{current.Title}」的複核異議");
            }
            catch (Exception error)
            {
                HandBack($"負責人 {MemberName(lead)} 沒有完成裁定：{error.Message}");
                return false;
            }
            // The owner may have ruled meanwhile; the lead's answer then no longer applies.
            var latest = TaskById(id, taskID);
            if (latest is null || latest.Status != FlowTaskStatus.Disputed
                || latest.Disputes.Select(dispute => dispute.FindingID)
                    .SequenceEqual(current.Disputes.Select(dispute => dispute.FindingID)) is false)
            {
                return false;
            }
            var rulings = ProjectFlowEngine.ParseAdjudication(reply, current.Disputes);
            if (rulings is null)
            {
                HandBack($"負責人 {MemberName(lead)} 的裁定讀不出來，請你裁定。");
                return false;
            }
            if (rulings.Any(ruling => ruling.Verdict is null))
            {
                // Upstream hands the whole set to the owner when any one is unsure; the lead's view stays as a note.
                UpdateTask(id, taskID, task =>
                {
                    foreach (var dispute in task.Disputes)
                    {
                        var ruling = rulings.FirstOrDefault(entry => entry.FindingID == dispute.FindingID);
                        if (ruling is null) continue;
                        var view = ruling.Verdict == FlowVerdict.Author ? "傾向支持作者"
                            : ruling.Verdict == FlowVerdict.Review ? "傾向維持複核" : "無法確定";
                        dispute.LeadNote = $"{MemberName(lead)}（負責人）{view}：{ruling.Reason}";
                    }
                });
                return false;
            }
            var verdicts = rulings.Where(ruling => ruling.Verdict is { })
                .ToDictionary(ruling => ruling.FindingID, ruling => ruling.Verdict!.Value);
            var notes = rulings.ToDictionary(ruling => ruling.FindingID, ruling => ruling.Reason);
            UpdateTask(id, taskID, task => ProjectFlowEngine.ApplyRulings(task, verdicts, lead, notes));
            var ruledNow = TaskById(id, taskID);
            if (ruledNow is null || ruledNow.Status == FlowTaskStatus.Disputed) return false;
            var fresh = ruledNow.Rulings.Skip(latest.Rulings.Count).ToList();
            PostFlowRecord(id, lead, ProjectFlowEngine.RulingCard(ruledNow, fresh, MemberNames()));
            return true;
        }
        finally
        {
            lock (flowGate)
            {
                leadRulings.Remove(taskID);
            }
            BumpFlows();
        }
    }

    /// <summary>The owner rules on an author's disputes (支持作者 / 維持複核), then the task goes on.</summary>
    public void RuleDisputes(Guid id, Guid taskID, IReadOnlyDictionary<string, FlowVerdict> verdicts)
    {
        if (flowRunning) return;
        lock (flowGate)
        {
            if (leadRulings.ContainsKey(taskID)) return;
        }
        lock (Archive)
        {
            var current = FindFlow(id)?.Plan?.Tasks.FirstOrDefault(candidate => candidate.Id == taskID);
            if (current is null || current.Status != FlowTaskStatus.Disputed) return;
            var before = current.Rulings.Count;
            ProjectFlowEngine.ApplyRulings(current, verdicts);
            if (current.Status == FlowTaskStatus.Disputed)
            {
                StatusMessage = "請逐條裁定每一項異議。";
                return;
            }
            PostFlowRecord(id, null, ProjectFlowEngine.RulingCard(current, current.Rulings.Skip(before).ToList(), MemberNames()));
        }
        Persist();
        BumpFlows();
        ContinueRound(id);
    }

    /// <summary>換人接手: the member out of quota hands their unfinished tasks in this round to another member.</summary>
    public void HandOverQuotaWork(Guid id, MemberID agent, MemberID other)
    {
        if (agent == other) return;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null || !flow.Members.Contains(other) || flow.ActiveRound is not { } round) return;
            foreach (var task in flow.Plan?.Tasks ?? [])
            {
                if (task.RoundKey != round.Key || task.Member != agent
                    || task.Status is not (FlowTaskStatus.Pending or FlowTaskStatus.Failed or FlowTaskStatus.Fixing)) continue;
                task.Member = other;
                // A fix carries on in its copy; anything else starts over.
                if (task.Status != FlowTaskStatus.Fixing) task.Status = FlowTaskStatus.Pending;
                task.FailedAt = null;
                task.Error = null;
            }
            // The member is still out of quota: reviews that need them keep waiting.
            flow.Paused = false;
        }
        Persist();
        BumpFlows();
        ContinueRound(id);
    }

    void ContinueRound(Guid id)
    {
        bool execute;
        lock (Archive)
        {
            var flow = FindFlow(id);
            execute = flow is not null && flow.ActiveRound?.Status == FlowRoundStatus.Executing && !flow.Paused;
        }
        if (!execute) return;
        if (flowRunning)
        {
            lock (flowGate) pendingFlowRun = id;
        }
        else
        {
            RunRound(id);
        }
    }

    void AddQuotaWait(Guid id, MemberID agent, string stage, string message)
    {
        FlowQuotaWait? waitRecord;
        string flowTitle;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null) return;
            flowTitle = flow.Title;
            var reset = QuotaSignal.ResetHint(message) ?? QuotaSignal.ResetAt(QuotaOf(agent));
            var wait = new FlowQuotaWait(agent, stage, reset, message);
            flow.QuotaWaits.RemoveAll(existing => existing.Agent == agent);
            flow.QuotaWaits.Add(wait);
            waitRecord = wait;
        }
        Persist();
        BumpFlows();
        Notify($"{MemberName(agent)} 的額度用完了", $"專案「{flowTitle}」：TA 的工作先等著，不會自動重送。");
        if (waitRecord.ResetAt is not null) return;
        // Ask the official quota when it resets (no prompt is sent).
        var executor = ExecutorOf(agent);
        _ = Task.Run(async () =>
        {
            var latest = await quotaReader.QueryAsync(executor, Archive.Settings);
            RunOnDispatcher(() =>
            {
                lock (Archive)
                {
                    accountQuotas[executor] = latest;
                    var flow = FindFlow(id);
                    var wait = flow?.QuotaWaits.FirstOrDefault(entry => entry.Id == waitRecord.Id);
                    if (wait is not null) wait.ResetAt = QuotaSignal.ResetAt(latest);
                }
                quotaVersion++;
                OnPropertyChanged(nameof(QuotaVersion));
                Persist();
                BumpFlows();
            });
        });
    }

    /// <summary>Round report (checked against the files unless 快速推進), then the user's acceptance.</summary>
    async Task CheckRoundAsync(Guid id, Guid roundID, ProjectWorkspace workspace)
    {
        MemberID lead;
        bool verify;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null || flow.Lead is not { } theLead) return;
            lead = theLead;
            verify = flow.ReviewLevel != ReviewLevel.Fast;
            var round = flow.Plan?.Rounds.FirstOrDefault(candidate => candidate.Id == roundID);
            if (round is null) return;
            round.Status = FlowRoundStatus.Checking;
            round.Changes = workspace.RoundChanges(roundID);
        }
        Persist();
        BumpFlows();
        lock (flowGate) flowJobActivity = $"{MemberName(lead)} 正在整理本輪報告……";
        BumpFlows();
        string prompt;
        string roundTitle;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow?.Plan is not { } plan) return;
            var round = plan.Rounds.FirstOrDefault(candidate => candidate.Id == roundID);
            if (round is null) return;
            prompt = ProjectFlowEngine.RoundCheckPrompt(round, flow, plan.TasksIn(round), lead, MemberNames(),
                verify, WorkPersona(lead));
            roundTitle = round.Title;
        }
        var report = await FlowCall(id, lead, prompt, workspace.Rounds.WorkingDirectory(roundID), ExecutionAccess.ReadFiles,
            stage: "整理本輪報告");
        lock (Archive)
        {
            var flow = FindFlow(id);
            var round = flow?.Plan?.Rounds.FirstOrDefault(candidate => candidate.Id == roundID);
            if (flow is null || round is null) return;
            round.Report = report;
            round.Status = FlowRoundStatus.AwaitingAcceptance;
            PostFlowRecord(id, lead, ProjectFlowEngine.RoundReportCard(round));
            flow.Step = FlowStep.Acceptance;
        }
        Notify("專案等你驗收", $"{FlowSnapshot(id)?.Title ?? "專案"}：{roundTitle}");
    }

    public List<FileChange> FlowRoundChanges(Guid id)
    {
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow?.ActiveRound is not { } round) return [];
            return FlowWorkspace(id)?.RoundChanges(round.Id) ?? [];
        }
    }

    public (string Before, string After)? FlowPreview(Guid id, string path)
    {
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow?.ActiveRound is not { } round) return null;
            try { return FlowWorkspace(id)?.Preview(round.Id, path); }
            catch { return null; }
        }
    }

    /// <summary>Step 5: 通過 writes the round into the project folder (conflict-checked).</summary>
    public void AcceptRound(Guid id)
    {
        if (flowRunning) return;
        bool finish;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow?.ActiveRound is not { } round || round.Status != FlowRoundStatus.AwaitingAcceptance) return;
            var workspace = FlowWorkspace(id);
            if (workspace is null) return;
            try
            {
                workspace.AcceptRound(round.Id, flow.Folder);
            }
            catch (Exception error)
            {
                StatusMessage = $"拒絕寫入，請檢查差異及衝突: {error.Message}";
                return;
            }
            var roundIndex = flow.CurrentRound;
            flow.Plan!.Rounds[roundIndex].Status = FlowRoundStatus.Accepted;
            PostFlowRecord(id, null, ProjectFlowEngine.AcceptanceCard(round, accepted: true));
            if (roundIndex + 1 < flow.Plan.Rounds.Count)
            {
                flow.CurrentRound = roundIndex + 1;
                flow.Step = FlowStep.Executing; // waits for 按原計劃開始 / 要求修改方案
                finish = false;
            }
            else
            {
                finish = true;
            }
        }
        Persist();
        BumpFlows();
        if (finish) WriteCompletion(id);
    }

    /// <summary>駁回: the round goes back with the user's opinion; each member gets a fix task on the same copy.</summary>
    public void ReturnRound(Guid id, string opinion)
    {
        if (flowRunning) return;
        var text = opinion.Trim();
        if (text.Length == 0)
        {
            StatusMessage = "請寫下要修改什麼。";
            return;
        }
        if (text.Length > 4_000) text = text[..4_000];
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow?.Plan is not { } plan || flow.ActiveRound is not { } round
                || round.Status != FlowRoundStatus.AwaitingAcceptance) return;
            var roundIndex = flow.CurrentRound;
            plan.Rounds[roundIndex].UserOpinion = text;
            plan.Rounds[roundIndex].Status = FlowRoundStatus.Executing;
            PostFlowRecord(id, null, ProjectFlowEngine.AcceptanceCard(round, accepted: false, opinion: text));
            var owners = plan.TasksIn(round).Select(task => task.Member).Distinct()
                .OrderBy(member => member.RawValue, StringComparer.Ordinal).ToList();
            var added = 0;
            foreach (var member in owners)
            {
                // A fix covers the files this member's tasks listed, so fixes by different members can run at once.
                var mine = plan.TasksIn(round).Where(task => task.Member == member).ToList();
                var files = mine.Any(task => task.Files.Count == 0)
                    ? [] : mine.SelectMany(task => task.Files).ToList();
                plan.Tasks.Add(new FlowTask($"{round.Key}-fix{plan.Tasks.Count + added}", round.Key,
                    "依驗收意見修正", member, text,
                    ["驗收意見中提到的問題都已處理"], [], files));
                added++;
            }
            flow.Step = FlowStep.Executing;
        }
        Persist();
        BumpFlows();
        RunRound(id);
    }

    /// <summary>Step 6: the lead writes the whole-work summary.</summary>
    void WriteCompletion(Guid id)
    {
        MemberID lead;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null || flow.Lead is not { } theLead) return;
            lead = theLead;
        }
        FlowJob(id, $"{MemberName(lead)} 正在寫整段工作總結……", async () =>
        {
            string prompt;
            lock (Archive)
            {
                var flow = FindFlow(id);
                if (flow is null) return;
                prompt = ProjectFlowEngine.CompletionPrompt(flow, MemberNames(), lead, WorkPersona(lead));
            }
            var summary = await FlowCall(id, lead, prompt, FlowFolder(id), ExecutionAccess.ReadOnly, stage: "整段工作總結");
            lock (Archive)
            {
                var flow = FindFlow(id);
                if (flow is null) return;
                flow.CompletionReport = summary;
                flow.Step = FlowStep.Completed;
                PostFlowRecord(id, lead, ProjectFlowEngine.CompletionCard(summary));
            }
            Notify("專案完成", FlowSnapshot(id)?.Title ?? "專案");
        });
    }

    /// <summary>開始新的一段工作: keeps the room and carries this segment's summary forward.</summary>
    public void StartNewSegment(Guid id)
    {
        if (flowRunning) return;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null || flow.Step != FlowStep.Completed) return;
            flow.PreviousSummary = flow.CompletionReport;
            flow.CompletionReport = "";
            flow.Plan = null;
            flow.PlanRequests = [];
            flow.CurrentRound = 0;
            flow.Lead = null;
            flow.Segment += 1;
            flow.Step = FlowStep.Discussion;
        }
        Persist();
        BumpFlows();
    }

    /// <summary>@ a member at work: 只是問問 / 安排新任務 / 補充到他目前的任務.</summary>
    public void AddFlowTask(Guid id, MemberID member, string goal)
    {
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow?.Plan is not { } plan || flow.ActiveRound is not { } round
                || round.Status == FlowRoundStatus.Accepted) return;
            var text = goal.Trim();
            if (text.Length == 0) return;
            plan.Tasks.Add(new FlowTask($"{round.Key}-add{plan.Tasks.Count}", round.Key,
                text.Length > 40 ? text[..40] : text, member, text));
        }
        Persist();
        BumpFlows();
    }

    public void SupplementFlowTask(Guid id, MemberID member, string text)
    {
        lock (Archive)
        {
            var flow = FindFlow(id);
            var target = flow?.Plan?.Tasks.FirstOrDefault(task => task.Member == member
                && task.Status is FlowTaskStatus.Running or FlowTaskStatus.Fixing or FlowTaskStatus.Reviewing)
                ?? flow?.Plan?.Tasks.FirstOrDefault(task => task.Member == member && task.Status == FlowTaskStatus.Pending);
            if (target is null)
            {
                StatusMessage = "這位成員目前沒有進行中或待開始的任務。";
                return;
            }
            var value = text.Length > 2_000 ? text[..2_000] : text;
            target.Supplements.Add(value);
        }
        Persist();
        BumpFlows();
    }

    /// <summary>成員匯報: ask a member about their work (read-only, no task changes).</summary>
    public void AskFlowMember(Guid id, MemberID member, string question, Action<string> answer)
    {
        if (flowRunning) return;
        string prompt;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null) return;
            prompt = ProjectFlowEngine.AskMemberPrompt(question, member, flow, MemberNames(), WorkPersona(member));
        }
        FlowJob(id, $"{MemberName(member)} 正在回答……", async () =>
        {
            var reply = await FlowCall(id, member, prompt, FlowFolder(id), ExecutionAccess.ReadFiles, stage: "回答問題");
            answer(reply);
        });
    }

    public void DeleteFlow(Guid id)
    {
        if (flowRunning) return;
        lock (Archive)
        {
            var flow = FindFlow(id);
            if (flow is null) return;
            if (flow.RoomID is { } roomID) Archive.Rooms?.RemoveAll(room => room.Id == roomID);
            Archive.ProjectFlows?.RemoveAll(candidate => candidate.Id == id);
            if (selectedFlowID == id) selectedFlowID = Archive.ProjectFlows?.FirstOrDefault()?.Id;
        }
        try
        {
            if (Persistence is not null)
            {
                var directory = Path.Combine(Persistence.Root, "flows", id.ToString("D"));
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }
        catch { /* the copies are disposable; the record is gone either way */ }
        Persist();
        BumpFlows();
    }

    /// <summary>帶回專案: the confirmed conclusion becomes material for that project's next plan, once.</summary>
    public void HandoffTopicToFlow(Guid topicID, Guid flowID)
    {
        lock (Archive)
        {
            var topic = Archive.RoundtableTopics?.FirstOrDefault(entry => entry.Id == topicID);
            var flow = FindFlow(flowID);
            if (topic is null || flow is null || topic.Status != RoundtableStatus.Confirmed
                || topic.HandoffProjectID is not null) return;
            flow.Materials.Add(RoundtableFlow.Handoff(topic, Names()));
            topic.HandoffProjectID = flowID;
        }
        Persist();
        BumpFlows();
    }

    // MARK: plumbing

    string FlowFolder(Guid id)
    {
        lock (Archive) return FindFlow(id)?.Folder ?? "";
    }

    /// <summary>Runs one flow job; errors stop the job and are shown, never retried automatically.</summary>
    void FlowJob(Guid id, string activity, Func<Task> body)
    {
        if (flowRunning) return;
        flowRunning = true;
        cancelFlow = false;
        lock (flowGate) flowJobActivity = activity;
        BumpFlows();
        _ = Task.Run(async () =>
        {
            try
            {
                await body();
            }
            catch (Exception error)
            {
                lock (Archive)
                {
                    if (FindFlow(id) is { } flow) flow.Paused = true;
                }
                StatusMessage = cancelFlow ? null : $"專案工作暫停: {error.Message}";
            }
            flowRunning = false;
            lock (flowGate)
            {
                flowRunIDs.Clear();
                flowActivities.Clear();
                flowJobActivity = null;
            }
            Persist();
            BumpFlows();
            Guid? next;
            lock (flowGate)
            {
                next = pendingFlowRun;
                pendingFlowRun = null;
            }
            if (next is { } follow) ContinueRound(follow);
        });
    }

    async Task<string> FlowCall(Guid id, MemberID agent, string prompt, string folder, ExecutionAccess access,
        string stage = "")
    {
        if (cancelFlow) throw NativeCLIError.ProcessFailed("已停止");
        var runID = Guid.NewGuid();
        lock (flowGate) flowRunIDs.Add(runID);
        try
        {
            lock (Archive)
            {
                if (FindFlow(id) is { } flow) flow.Calls += 1;
            }
            // The runner rejects prompts over the CLI's own ceiling; the macOS port truncates instead.
            if (prompt.Length > CommandPlan.MaxPromptCharacters) prompt = prompt[..CommandPlan.MaxPromptCharacters];
            var settings = CloneSettingsWithWorkspace(folder);
            return await runner.RunAsync(runID, agent, prompt, settings,
                fallbackWorkspace: folder, access: access);
        }
        catch (Exception error)
        {
            // Out of quota: the member waits for a reset; nothing is re-sent by itself.
            if (QuotaSignal.IsQuotaFailure(error.Message))
            {
                string activity;
                lock (flowGate) activity = flowJobActivity ?? "專案工作";
                AddQuotaWait(id, agent, stage.Length > 0 ? stage : activity, error.Message);
            }
            throw;
        }
        finally
        {
            lock (flowGate) flowRunIDs.Remove(runID);
        }
    }
}
