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
            Executor.Claude => ("cmd.exe", $"/c start \"AIROOM\" cmd /k {quoted} auth login"),
            Executor.Codex => ("cmd.exe", $"/c start \"AIROOM\" cmd /k {quoted} login"),
            Executor.Antigravity => ("cmd.exe", $"/c start \"AIROOM\" cmd /k {quoted}"),
            Executor.DeepSeek => throw new NotSupportedException("DeepSeek Harness 尚未支援此平台。"),
            _ => throw new ArgumentOutOfRangeException(nameof(executor)),
        };
    }

    static string Quote(string path) => '"' + path + '"';
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
        if (executor is Executor.DeepSeek or Executor.Antigravity) return OfficialCLIStatus.NotConfirmed;

        var arguments = executor == Executor.Codex ? new[] { "login", "status" } : new[] { "auth", "status" };
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
            return Interpret(process.ExitCode, output);
        }
        catch
        {
            return OfficialCLIStatus.NotConfirmed;
        }
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
