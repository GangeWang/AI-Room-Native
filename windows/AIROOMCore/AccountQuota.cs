using System.Diagnostics;
using System.Text.Json;

namespace AIROOMCore;

/// <summary>Account subscription limits reported by the official executables, not
/// inferred from token usage. A missing or expired sample MUST NOT be presented
/// as zero quota.</summary>
public sealed record QuotaWindow(
    string Id,
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Max(0, 100 - UsedPercent);
    public bool Exhausted => UsedPercent >= 100;
}

public sealed record AccountQuota(Executor Agent, DateTimeOffset CheckedAt, IReadOnlyList<QuotaWindow> Windows)
{
    public QuotaStatus Status => Windows.Count == 0 ? QuotaStatus.Unavailable : QuotaStatus.Ready;

    public bool IsFresh(DateTimeOffset? at = null)
    {
        var elapsed = (at ?? DateTimeOffset.Now) - CheckedAt;
        return elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromMinutes(15);
    }
}

public enum QuotaStatus
{
    Ready,
    Unavailable,
}

/// <summary>Turns the vendors' own quota reports into quota windows. Malformed
/// values are dropped, never clamped to zero.</summary>
public static class QuotaProjection
{
    public static List<QuotaWindow> Codex(JsonElement source)
    {
        var result = Object(source);
        var data = Object(Field(result, "accountRateLimits") ?? source);
        var byID = Object(Field(data, "rateLimitsByLimitId"));
        List<(string Id, JsonElement Row)> rows;
        if (byID.ValueKind == JsonValueKind.Object && byID.EnumerateObject().Any())
        {
            rows = byID.EnumerateObject().Select(property => (property.Name, property.Value))
                .OrderBy(row => row.Name, StringComparer.Ordinal).ToList();
        }
        else
        {
            var single = Field(data, "rateLimits");
            rows = single is { ValueKind: JsonValueKind.Object } value ? [("codex", value)] : [];
        }
        var output = new List<QuotaWindow>();
        foreach (var (identifier, row) in rows.Take(40))
        {
            foreach (var key in new[] { "primary", "secondary" })
            {
                var item = Object(Field(row, key));
                if (ValidPercent(Field(item, "usedPercent")) is not { } used) continue;
                int? minutes = Field(item, "windowDurationMins") is { ValueKind: JsonValueKind.Number } durationElement
                    && durationElement.TryGetInt32(out var parsed) ? parsed : null;
                var duration = minutes == 300 ? "5 小時" : minutes == 10080 ? "每週" : minutes is { } value ? $"{value} 分鐘" : key;
                var name = Field(row, "limitName") is { ValueKind: JsonValueKind.String } nameElement
                    && nameElement.GetString() is { Length: <= 80 } limitName ? limitName : null;
                var title = rows.Count > 1 ? $"{name ?? identifier} · {duration}" : duration;
                output.Add(new QuotaWindow($"{identifier}:{key}", title, used, ResetTimestamp(Field(item, "resetsAt"))));
            }
        }
        return output;
    }

    public static List<QuotaWindow> Claude(JsonElement source)
    {
        var report = Object(source);
        if (Field(report, "rate_limits") is not { } limitsHolder
            || Field(limitsHolder, "limits") is not { ValueKind: JsonValueKind.Array } limits
            || limits.GetArrayLength() > 40)
        {
            return [];
        }
        var output = new List<QuotaWindow>();
        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var kind = entry.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
                ? kindElement.GetString() : null;
            if (kind is not ("session" or "weekly_all" or "weekly_scoped")) continue;
            if (ValidPercent(Field(entry, "percent")) is not { } used) continue;
            string? scopedName = null;
            if (entry.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                && Field(scope, "model") is { } model
                && Field(model, "display_name") is { ValueKind: JsonValueKind.String } displayName)
            {
                scopedName = displayName.GetString();
            }
            var label = kind == "session" ? "5 小時" : kind == "weekly_all" ? "每週" : $"每週 {(scopedName ?? "模型")[..Math.Min(60, (scopedName ?? "模型").Length)]}";
            DateTimeOffset? reset = null;
            if (entry.TryGetProperty("resets_at", out var resetsAt) && resetsAt.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(resetsAt.GetString(), out var parsed))
            {
                reset = parsed;
            }
            output.Add(new QuotaWindow(kind + (kind == "weekly_scoped" ? scopedName ?? "" : ""), label, used, reset));
        }
        return output;
    }

    static JsonElement? Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    static JsonElement Object(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } value ? value : JsonDocument.Parse("{}").RootElement.Clone();

    static double? ValidPercent(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Number } value) return null;
        var number = value.GetDouble();
        return double.IsFinite(number) && number is >= 0 and <= 100 ? number : null;
    }

    static DateTimeOffset? ResetTimestamp(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Number } value) return null;
        var seconds = value.GetDouble();
        return double.IsFinite(seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
    }
}

/// <summary>A small, read-only protocol state machine. It explicitly verifies
/// Claude's built-in /usage command before sending it, and refuses tool requests.</summary>
public sealed class QuotaWire
{
    public Executor Agent { get; }
    public List<QuotaWindow> Windows { get; private set; } = [];
    public bool Finished { get; private set; }
    public bool Failed { get; private set; }
    public bool SentQuery { get; private set; }
    public string RequestID { get; }

    public QuotaWire(Executor agent, string? requestID = null)
    {
        Agent = agent;
        RequestID = requestID ?? Guid.NewGuid().ToString("D");
    }

    /// <summary>Feeds one stdout line; returns the lines to write back.</summary>
    public List<string> Receive(string line)
    {
        JsonElement value;
        try
        {
            value = JsonDocument.Parse(line).RootElement.Clone();
        }
        catch (JsonException)
        {
            return [];
        }
        var replies = new List<string>();
        if (Agent == Executor.Codex)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.Number && idElement.GetInt32() == 2)
            {
                if (value.TryGetProperty("error", out _)) Failed = true;
                else
                {
                    Windows = QuotaProjection.Codex(
                        value.TryGetProperty("result", out var result) ? result : JsonDocument.Parse("{}").RootElement.Clone());
                    Finished = true;
                }
            }
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("method", out _)
                && value.TryGetProperty("id", out var requestElement))
            {
                replies.Add("{\"id\":" + requestElement.GetRawText() + ",\"error\":{\"code\":-32601,\"message\":\"Read-only usage query\"}}");
            }
            return replies;
        }
        var kind = value.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString() : null;
        if (kind == "control_response"
            && value.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("request_id", out var responseID) && responseID.ValueKind == JsonValueKind.String
            && responseID.GetString() == RequestID && !SentQuery)
        {
            var supported = response.TryGetProperty("response", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("commands", out var commands)
                && commands.ValueKind == JsonValueKind.Array
                ? commands : default;
            var hasBuiltinUsage = supported.ValueKind == JsonValueKind.Array && supported.EnumerateArray()
                .Any(command => command.ValueKind == JsonValueKind.Object
                    && command.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    && name.GetString() == "usage"
                    && command.TryGetProperty("builtin", out var builtin)
                    && builtin.ValueKind == JsonValueKind.True);
            if (response.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String
                && subtype.GetString() == "success" && hasBuiltinUsage)
            {
                SentQuery = true;
                replies.Add("{\"type\":\"user\",\"uuid\":\"" + Guid.NewGuid().ToString("D") + "\",\"parent_tool_use_id\":null,\"session_id\":\"\",\"message\":{\"role\":\"user\",\"content\":\"/usage\"}}");
            }
            else
            {
                Failed = true;
            }
            return replies;
        }
        if (kind == "control_request")
        {
            Failed = true;
            var original = value.TryGetProperty("request_id", out var originalID) && originalID.ValueKind == JsonValueKind.String
                ? originalID.GetString() : "";
            replies.Add("{\"type\":\"control_response\",\"response\":{\"subtype\":\"error\",\"request_id\":\"" + original + "\",\"error\":\"Usage query does not permit tools\"}}");
            return replies;
        }
        if (SentQuery && kind == "assistant")
        {
            var content = value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.Array ? contentElement : default;
            if (content.ValueKind == JsonValueKind.Array && content.EnumerateArray()
                .Any(part => part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("type", out var partType) && partType.ValueKind == JsonValueKind.String
                    && partType.GetString() == "tool_use"))
            {
                Failed = true;
                return [];
            }
            if (value.TryGetProperty("usage_report", out var report))
            {
                Windows = QuotaProjection.Claude(report);
            }
        }
        if (SentQuery && kind == "result")
        {
            var ok = value.TryGetProperty("subtype", out var resultSubtype) && resultSubtype.ValueKind == JsonValueKind.String
                && resultSubtype.GetString() == "success"
                && (!value.TryGetProperty("is_error", out var isError) || isError.ValueKind != JsonValueKind.True)
                && value.TryGetProperty("local_command", out var localCommand)
                && localCommand.ValueKind == JsonValueKind.String && localCommand.GetString() == "usage"
                && value.TryGetProperty("num_turns", out var turns) && turns.ValueKind == JsonValueKind.Number
                && turns.GetInt32() == 0;
            if (ok) Finished = true; else Failed = true;
        }
        return replies;
    }
}

/// <summary>Runs the official executable's own quota report with restricted
/// flags. Nothing outside this query is sent; failure yields no windows
/// (never a guessed 0% remaining).</summary>
public sealed class AccountQuotaReader
{
    public async Task<AccountQuota> QueryAsync(Executor agent, NativeSettings settings,
        int timeoutMilliseconds = 15_000)
    {
        // DeepSeek's API is pay-as-you-go, and agy/ZCode have no quota report.
        if (agent is not (Executor.Claude or Executor.Codex))
        {
            return new AccountQuota(agent, DateTimeOffset.Now, []);
        }
        var binary = CommandPlan.ResolveExecutable(agent, settings);
        if (binary is null)
        {
            return new AccountQuota(agent, DateTimeOffset.Now, []);
        }
        var windows = await Task.Run(() => QueryBlocking(agent, binary, timeoutMilliseconds));
        return new AccountQuota(agent, DateTimeOffset.Now, windows);
    }

    static List<QuotaWindow> QueryBlocking(Executor agent, string binary, int timeoutMilliseconds)
    {
        const string codexArguments = "app-server --listen stdio://";
        const string claudeArguments = "--restricted -p --verbose --input-format stream-json --output-format stream-json --no-session-persistence --tools \"\" --strict-mcp-config --mcp-config {\"mcpServers\":{}}";
        var arguments = agent == Executor.Codex ? codexArguments : claudeArguments;
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        // npm-shimmed CLIs arrive as .cmd (run through cmd) or .js (run on node).
        if (binary.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "node.exe";
            startInfo.Arguments = $"\"{binary}\" {arguments}".TrimEnd();
        }
        else if (binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || binary.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/c \"{binary}\" {arguments}";
        }
        else
        {
            startInfo.FileName = binary;
            startInfo.Arguments = arguments;
        }
        foreach (var pair in PrivacyGuard.CliEnvironment(
                     new Dictionary<string, string>(
                         Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                             .Select(entry => new KeyValuePair<string, string>((string)entry.Key, (string?)entry.Value ?? ""))), binary))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null) return [];
        }
        catch
        {
            return []; // Not started: report "unavailable", never a guessed quota.
        }
        try
        {
            _ = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } errLine)
                {
                    if (Environment.GetEnvironmentVariable("AIROOM_QUOTA_DEBUG") is not null)
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "airoom-quota-debug.log"), "ERR: " + errLine + Environment.NewLine);
                    }
                }
            });
            var wire = new QuotaWire(agent);
            var send = (string line) => process.StandardInput.WriteLine(line);
            var pump = ConsumeOutput(process, wire, send, agent);
            if (agent == Executor.Codex)
            {
                send("""{"id":1,"method":"initialize","params":{"clientInfo":{"name":"airoom_native","title":"AI Room Native","version":"0.3.0"}}}""");
            }
            else
            {
                send("{\"type\":\"control_request\",\"request_id\":\"" + wire.RequestID + "\",\"request\":{\"subtype\":\"initialize\"}}");
            }
            var finished = pump.Wait(timeoutMilliseconds);
            if (Environment.GetEnvironmentVariable("AIROOM_QUOTA_DEBUG") is not null)
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "airoom-quota-debug.log"),
                    $"OUT: finished={finished} wireFinished={wire.Finished} failed={wire.Failed} windows={wire.Windows.Count}" + Environment.NewLine);
            }
            return finished && wire.Finished && !wire.Failed ? wire.Windows.ToList() : [];
        }
        catch
        {
            return [];
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            process.Dispose();
        }
    }

    /// <summary>Reads stdout lines until the wire finishes; answers the wire's
    /// requests inline. Ends when the stream closes or the line cap is hit.</summary>
    static async Task ConsumeOutput(Process process, QuotaWire wire, Action<string> send, Executor agent)
    {
        var buffer = new System.Text.StringBuilder();
        var initialized = false;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (Environment.GetEnvironmentVariable("AIROOM_QUOTA_DEBUG") is not null)
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "airoom-quota-debug.log"), "IN: " + line + Environment.NewLine);
            }
            if (agent == Executor.Codex && !initialized)
            {
                JsonElement packet;
                try { packet = JsonDocument.Parse(line).RootElement.Clone(); }
                catch (JsonException) { packet = default; }
                if (packet.ValueKind == JsonValueKind.Object
                    && packet.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 1)
                {
                    if (packet.TryGetProperty("error", out _)) return;
                    initialized = true;
                    send("""{"method":"initialized"}""");
                    send("""{"id":2,"method":"account/rateLimits/read","params":{}}""");
                    continue;
                }
            }
            foreach (var reply in wire.Receive(line))
            {
                send(reply);
            }
            if (wire.Failed || wire.Finished) return;
            if (buffer.Length > 2 * 1_024 * 1_024) return;
        }
    }
}
