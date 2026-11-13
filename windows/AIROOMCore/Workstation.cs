using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIROOMCore;

// MARK: Call usage — counted receipts, as upstream AIROOM shows them:
// "累計嘗試 N 次 · 已發出 N 次 · 已完成 N 次", plus not-sent and unknown results.

public enum CallEvent
{
    Attempt,
    Delivered,
    Completed,
    NotSent,
    Unknown,
    NotReached,
}

public sealed class CallUsage
{
    public int Total { get; set; }
    public int Delivered { get; set; }
    public int Completed { get; set; }
    public int NotSent { get; set; }
    public int Unknown { get; set; }

    public void Record(CallEvent @event)
    {
        switch (@event)
        {
            case CallEvent.Attempt: Total += 1; break;
            case CallEvent.Delivered: Delivered += 1; break;
            case CallEvent.Completed: Completed += 1; break;
            case CallEvent.NotSent: NotSent += 1; break;
            case CallEvent.Unknown: Unknown += 1; break;
            case CallEvent.NotReached:
                Delivered = Math.Max(0, Delivered - 1);
                NotSent += 1;
                break;
        }
    }

    public static CallUsage operator +(CallUsage a, CallUsage b) => new()
    {
        Total = a.Total + b.Total,
        Delivered = a.Delivered + b.Delivered,
        Completed = a.Completed + b.Completed,
        NotSent = a.NotSent + b.NotSent,
        Unknown = a.Unknown + b.Unknown,
    };

    /// <summary>AIROOM's own request count — not the subscription bill.</summary>
    public string Label(InterfaceLanguage language)
    {
        var text = language == InterfaceLanguage.English
            ? $"Attempted {Total} · Sent {Delivered} · Completed {Completed}"
            : $"累計嘗試 {Total} 次 · 已發出 {Delivered} 次 · 已完成 {Completed} 次";
        if (NotSent > 0) text += language == InterfaceLanguage.English ? $" · Not sent {NotSent}" : $" · 未發出失敗 {NotSent} 次";
        if (Unknown > 0) text += language == InterfaceLanguage.English ? $" · Unknown {Unknown}" : $" · 結果未知 {Unknown} 次";
        return text;
    }
}

/// <summary>Per member, per day; old days are pruned so the state file stays small.</summary>
public sealed class CallLedger
{
    public Dictionary<string, Dictionary<string, CallUsage>> Days { get; set; } = [];

    public void Record(CallEvent @event, MemberID agent, string day, int keepDays = 60)
    {
        var byDay = Days.TryGetValue(agent.RawValue, out var existing)
            ? existing : Days[agent.RawValue] = [];
        if (!byDay.TryGetValue(day, out var usage)) usage = byDay[day] = new CallUsage();
        usage.Record(@event);
        if (byDay.Count > keepDays)
        {
            foreach (var key in byDay.Keys.Order().Take(byDay.Count - keepDays).ToList())
            {
                byDay.Remove(key);
            }
        }
    }

    public CallUsage Usage(MemberID agent, string? day = null)
    {
        if (!Days.TryGetValue(agent.RawValue, out var byDay)) return new CallUsage();
        if (day is not null) return byDay.TryGetValue(day, out var usage) ? usage : new CallUsage();
        return byDay.Values.Aggregate(new CallUsage(), (sum, usage) => sum + usage);
    }
}

// MARK: Needs-attention list (navigation only; opening an item never answers,
// resends, merges or accepts anything).

public enum AttentionKind
{
    Question,
    Decision,
    UnknownResult,
    Login,
    Missing,
    Acceptance,
}

public sealed record AttentionTarget(Guid? Room, Guid? Roundtable, Guid? Project, bool Settings, MemberID? MemberCard);

public sealed record AttentionItem(string Id, AttentionKind Kind, string Title, string Detail, AttentionTarget Target)
{
    /// <summary>The version shown now. An ignored item comes back once this changes.</summary>
    public string Fingerprint => $"{KindString}|{Title}|{Detail}";

    string KindString => Kind switch
    {
        AttentionKind.Question => "question",
        AttentionKind.Decision => "decision",
        AttentionKind.UnknownResult => "unknown",
        AttentionKind.Login => "login",
        AttentionKind.Missing => "missing",
        AttentionKind.Acceptance => "acceptance",
        _ => Kind.ToString(),
    };
}

public static class AttentionBuilder
{
    /// <summary>Items not ignored in their current version.</summary>
    public static List<AttentionItem> Visible(IReadOnlyList<AttentionItem> items, IReadOnlyDictionary<string, string> dismissed) =>
        items.Where(item => !dismissed.TryGetValue(item.Id, out var fingerprint) || fingerprint != item.Fingerprint).ToList();

    /// <summary>
    /// Ignoring only hides the reminder; the work it points to is unchanged.
    /// Records of items that no longer exist are dropped.
    /// </summary>
    public static Dictionary<string, string> Dismissing(
        AttentionItem item, IReadOnlyList<AttentionItem> items, IReadOnlyDictionary<string, string> dismissed)
    {
        var live = items.Select(i => i.Id).ToHashSet();
        var next = dismissed.Where(pair => live.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (live.Contains(item.Id)) next[item.Id] = item.Fingerprint;
        return next;
    }

    /// <summary>MVP subset: CLI login/availability reminders and memory proposals.
    /// Room, roundtable and project items join in later phases.</summary>
    public static List<AttentionItem> Items(SavedState state, IReadOnlyDictionary<Executor, string> cliStatuses)
    {
        var items = new List<AttentionItem>();
        foreach (var executor in ExecutorList.All)
        {
            var names = state.Members
                .Where(member => member.Executor == executor && member.Enabled)
                .Select(member => member.Name).ToList();
            if (names.Count == 0) continue;
            if (!cliStatuses.TryGetValue(executor, out var status)) continue;
            var who = string.Join("、", names);
            switch (status)
            {
                case "notLoggedIn":
                    items.Add(new AttentionItem($"login-{ExecutorName.Of(executor)}", AttentionKind.Login,
                        $"{who} 需要登入", "官方 CLI 尚未登入，成員暫時無法回覆。",
                        new AttentionTarget(null, null, null, Settings: true, null)));
                    break;
                case "missingExecutable":
                    items.Add(new AttentionItem($"missing-{ExecutorName.Of(executor)}", AttentionKind.Missing,
                        $"{who} 缺少程式", "找不到官方 CLI，請在設定指定路徑或安裝。",
                        new AttentionTarget(null, null, null, Settings: true, null)));
                    break;
            }
        }
        foreach (var member in state.Members.Where(member => member.MemoryProposals.Count > 0))
        {
            items.Add(new AttentionItem($"memory-{member.Agent.RawValue}", AttentionKind.Decision,
                $"{member.Name} 提議了 {member.MemoryProposals.Count} 條記憶",
                member.MemoryProposals.FirstOrDefault()?.Text ?? "",
                new AttentionTarget(null, null, null, false, member.Agent)));
        }
        string NameOf(MemberID agent) =>
            state.Members.FirstOrDefault(member => member.Agent == agent)?.Name ?? agent.RawValue;
        foreach (var room in state.Rooms ?? [])
        {
            var target = room.RoundtableID is { } roundtable ? new AttentionTarget(null, roundtable, null, false, null)
                : room.ProjectID is { } project ? new AttentionTarget(null, null, project, false, null)
                : new AttentionTarget(room.Id, null, null, false, null);
            foreach (var question in room.Lounge.PendingQuestions)
            {
                items.Add(new AttentionItem($"q-{question.Id}", AttentionKind.Question,
                    $"{NameOf(question.From)} 在「{room.Title}」問你",
                    question.Text.Length > 140 ? question.Text[..140] : question.Text, target));
            }
            if (room.Lounge.Phase == LoungePhase.AwaitingHost)
            {
                items.Add(new AttentionItem($"pick-{room.Id}", AttentionKind.Decision,
                    $"「{room.Title}」有人舉手", "請點名誰補充，或讓大家等你說話。", target));
            }
            // Only the latest member reply counts: an older failure was already seen.
            if (room.Entries.LastOrDefault(entry => entry.Speaker == Speaker.Assistant) is { } last
                && last.Delivery == RoomDelivery.Failed)
            {
                items.Add(new AttentionItem($"unknown-{last.Id}", AttentionKind.UnknownResult,
                    $"「{room.Title}」有結果待核對", $"{last.DisplayName} 的回覆沒有確認完成，沒有自動重送。", target));
            }
        }
        foreach (var topic in state.RoundtableTopics ?? [])
        {
            if (topic.Status == RoundtableStatus.Conclusion)
            {
                items.Add(new AttentionItem($"roundtable-{topic.Id}", AttentionKind.Acceptance,
                    $"圓桌「{topic.Title}」的結論等你確認",
                    topic.Conclusion,
                    new AttentionTarget(null, topic.Id, null, false, null)));
            }
        }
        foreach (var flow in state.ProjectFlows ?? [])
        {
            var target = new AttentionTarget(null, null, flow.Id, false, null);
            if (flow.Step == FlowStep.Plan)
            {
                items.Add(new AttentionItem($"flow-plan-{flow.Id}", AttentionKind.Decision,
                    $"專案「{flow.Title}」的方案等你確認",
                    "看過方案後按「確認方案，開工」，或要求修改。", target));
            }
            else if (flow.ActiveRound is { } round && round.Status == FlowRoundStatus.AwaitingAcceptance)
            {
                items.Add(new AttentionItem($"flow-accept-{round.Id}", AttentionKind.Acceptance,
                    $"專案「{flow.Title}」：{round.Title} 等你驗收",
                    "查看成品和改動，選擇通過或駁回。", target));
            }
            else if (flow.Step == FlowStep.Executing && flow.ActiveRound is { } next && next.Status == FlowRoundStatus.Pending)
            {
                items.Add(new AttentionItem($"flow-next-{next.Id}", AttentionKind.Decision,
                    $"專案「{flow.Title}」：下一輪等你開始",
                    "按原計劃開始，或要求修改方案。", target));
            }
            else if (flow.Plan is { } plan && plan.Tasks.FirstOrDefault(task => task.Status == FlowTaskStatus.Disputed) is { } disputed)
            {
                items.Add(new AttentionItem($"flow-dispute-{disputed.Id}", AttentionKind.Decision,
                    $"專案「{flow.Title}」：「{disputed.Title}」的複核異議等你裁定",
                    "逐條選擇支持作者或維持複核。", target));
            }
            else if (flow.QuotaWaits.FirstOrDefault() is { } wait)
            {
                items.Add(new AttentionItem($"flow-quota-{wait.Id}-{wait.Ready}", AttentionKind.Decision,
                    wait.Ready ? $"專案「{flow.Title}」：{wait.Agent.RawValue} 的額度應該恢復了"
                               : $"專案「{flow.Title}」：{wait.Agent.RawValue} 的額度用完了",
                    wait.Ready ? "按「接著做」繼續，不會自動重送。"
                               : "可以等恢復、換人接手，其他成員的任務照常進行。", target));
            }
            else if (flow.Paused && flow.Plan is { } pausedPlan && pausedPlan.Tasks.Any(task => task.Status == FlowTaskStatus.Failed))
            {
                items.Add(new AttentionItem($"flow-failed-{flow.Id}", AttentionKind.UnknownResult,
                    $"專案「{flow.Title}」有任務沒有完成",
                    "檢查原因後可以繼續，沒有自動重送。", target));
            }
        }
        return items;
    }
}

// MARK: Signing in through the official CLI, in the user's own terminal.

public static class LoginScript
{
    /// <summary>
    /// Opens a visible terminal window running only the CLI's own login command.
    /// AIROOM never sees or stores what the user types.
    /// </summary>
    public static (string FileName, string Arguments) Command(Executor executor, string executablePath)
    {
        var quoted = Quote(executablePath);
        return executor switch
        {
            Executor.Claude => ("cmd.exe", $"/c {NoAutoUpdate}start \"AIROOM\" cmd /k {quoted} auth login"),
            Executor.Codex => ("cmd.exe", $"/c {NoAutoUpdate}start \"AIROOM\" cmd /k {quoted} login"),
            Executor.Antigravity => ("cmd.exe", $"/c {NoAutoUpdate}start \"AIROOM\" cmd /k {quoted}"),
            // ZCode signs in through browser authorization (login [zai|bigmodel]).
            Executor.ZCode => ("cmd.exe", $"/c {NoAutoUpdate}start \"AIROOM\" cmd /k \"node\" {quoted} login"),
            Executor.DeepSeek => throw new NotSupportedException("DeepSeek Harness 尚未支援此平台。"),
            _ => throw new ArgumentOutOfRangeException(nameof(executor)),
        };
    }

    static string Quote(string path) => '"' + path + '"';

    // The window inherits this from the outer cmd, so the Claude Code bundled with the
    // Claude app does not install a standalone copy. No space before && keeps the value "1".
    const string NoAutoUpdate = "set DISABLE_AUTOUPDATER=1&& ";
}

/// <summary>Read-only status probe result for one executor's official CLI.</summary>
public enum OfficialCLIStatus
{
    Checking,
    Authenticated,
    NotLoggedIn,
    NotConfirmed,
    MissingExecutable,
    TimedOut,
}

public static class OfficialCLIProbe
{
    /// <summary>
    /// Read-only status probe uses the vendor's own public CLI commands. The app
    /// never opens ~/.codex/auth.json, Claude credentials, browser cookies or
    /// credential stores. A successful CLI check does not prove the desktop GUI
    /// is signed into the same account.
    /// </summary>
    public static OfficialCLIStatus Check(Executor executor, NativeSettings settings,
                                          IReadOnlyDictionary<string, string>? environment = null, int timeoutMilliseconds = 8_000)
    {
        var executable = CommandPlan.ResolveExecutable(executor, settings, environment);
        if (executable is null) return OfficialCLIStatus.MissingExecutable;
        if (executor is Executor.DeepSeek) return OfficialCLIStatus.NotConfirmed;
        if (executor is Executor.ZCode) return ProbeZCode(executable);

        // `agy` has no status command: it lists models only once signed in.
        var arguments = executor == Executor.Antigravity ? new[] { "models" }
            : executor == Executor.Codex ? new[] { "login", "status" }
            : new[] { "auth", "status" };
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(' ', arguments.Select(a => '"' + a + '"')),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var pair in PrivacyGuard.CliEnvironment(
                     environment ?? new Dictionary<string, string>(), executable))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null) return OfficialCLIStatus.NotConfirmed;
            process.StandardInput.Close();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return OfficialCLIStatus.TimedOut;
            }
            var output = outputTask.GetAwaiter().GetResult();
            if (executor == Executor.Antigravity) return Antigravity.Status(output);
            return Interpret(process.ExitCode, output);
        }
        catch
        {
            return OfficialCLIStatus.NotConfirmed;
        }
    }

    /// <summary>ZCode's CLI runs as a Node script on a plain Node (v22+) or the
    /// desktop's own executable; neither present means the member cannot run.
    /// The desktop's shared credentials mean the account is signed in (the JWT
    /// is refreshed by the desktop app itself).</summary>
    static OfficialCLIStatus ProbeZCode(string executable)
    {
        // A .cjs launcher needs a runtime; without one the member cannot run.
        if (executable.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase) && ZCode.ResolveRuntime(executable) is null)
        {
            return OfficialCLIStatus.MissingExecutable;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ZCode.LoadAuth(home) is not null
            ? OfficialCLIStatus.Authenticated
            : OfficialCLIStatus.NotConfirmed;
    }

    /// <summary>
    /// Claude Desktop authenticates its own Code tab by passing credentials to
    /// the child it launches; a standalone invocation of the same executable
    /// reports "loggedIn": false until `claude auth login` has been run once.
    /// </summary>
    public static OfficialCLIStatus Interpret(int exitCode, string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output.Length > 64_000 ? output[..64_000] : output);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("loggedIn", out var loggedIn)
                && loggedIn.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return loggedIn.GetBoolean() ? OfficialCLIStatus.Authenticated : OfficialCLIStatus.NotLoggedIn;
            }
        }
        catch (JsonException)
        {
            // fall through to text matching
        }
        var text = (output.Length > 4_000 ? output[..4_000] : output).ToLowerInvariant();
        if (text.Contains("not logged in")) return OfficialCLIStatus.NotLoggedIn;
        return exitCode == 0 ? OfficialCLIStatus.Authenticated : OfficialCLIStatus.NotConfirmed;
    }
}
