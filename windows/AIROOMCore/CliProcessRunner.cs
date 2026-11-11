using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AIROOMCore;

/// <summary>
/// One-shot CLI bridge. Does not depend on a shell, Node, or CLI internals.
/// It intentionally does not claim equivalence to AIROOM's original persistent
/// sessions. The prompt is transmitted through stdin, never as an argument
/// visible to other processes.
/// </summary>
public sealed class CliProcessRunner
{
    const int MaxStderrBytes = 32_768;
    const long MaxSingleLineBytes = 4 * 1024 * 1024;

    readonly object gate = new();
    readonly Dictionary<MemberID, (Executor Executor, string Model, string Effort)> memberDefaults = [];
    readonly ConcurrentDictionary<Guid, Process> active = [];

    /// <summary>Receives one attempt, at most one delivered, then completed /
    /// notSent / unknown for every request. Called from background threads.</summary>
    public Action<MemberID, CallEvent>? Observer { get; set; }

    /// <summary>Each member's program, default model and effort (used where a call doesn't choose).</summary>
    public void SetDefaults(MemberID agent, Executor? executor, string model, string effort)
    {
        lock (gate)
        {
            memberDefaults.TryGetValue(agent, out var existing);
            var resolved = executor
                ?? (existing.Executor != default ? existing.Executor : agent.BuiltInExecutor)
                ?? Executor.Claude;
            memberDefaults[agent] = (resolved, model, effort);
        }
    }

    (Executor, string, string) Defaults(MemberID agent)
    {
        lock (gate)
        {
            if (memberDefaults.TryGetValue(agent, out var existing)) return existing;
        }
        return (agent.BuiltInExecutor ?? Executor.Claude, "", "");
    }

    /// <summary>The program a member runs on.</summary>
    public Executor ExecutorFor(MemberID agent) => Defaults(agent).Item1;

    public async Task<string> RunAsync(
        Guid? id = null, MemberID agent = default, string prompt = "", NativeSettings? settings = null,
        string fallbackWorkspace = "", ExecutionAccess access = ExecutionAccess.ReadOnly,
        CLIRunOptions? options = null, Action<string>? onUpdate = null, CancellationToken cancellationToken = default)
    {
        var result = await RunDetailedAsync(id ?? Guid.NewGuid(), agent, prompt, settings ?? new NativeSettings(),
            fallbackWorkspace, access, options, onUpdate, cancellationToken);
        return result.Text;
    }

    /// <summary>Like RunAsync, with per-topic model/effort, image/PDF attachments,
    /// and the model the CLI reported back.</summary>
    public async Task<CLIRunResult> RunDetailedAsync(
        Guid id, MemberID agent, string prompt, NativeSettings settings,
        string fallbackWorkspace, ExecutionAccess access = ExecutionAccess.ReadOnly,
        CLIRunOptions? options = null, Action<string>? onUpdate = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > CommandPlan.MaxPromptCharacters)
        {
            throw NativeCLIError.InvalidPrompt;
        }
        PrivacyGuard.ValidatePrompt(prompt);
        var (executor, defaultModel, defaultEffort) = Defaults(agent);
        var effective = (options ?? new CLIRunOptions()).Inheriting(defaultModel, defaultEffort);
        try
        {
            return await RunObservedAsync(id, agent, executor, prompt, settings, fallbackWorkspace, access, effective, onUpdate, cancellationToken);
        }
        catch (SessionUnavailable)
        {
            // The earlier conversation is gone and the model was never reached:
            // start a new one with the full prompt. Any other error is not retried.
            if (effective.Session is null) throw;
            var fresh = new CLIRunOptions(effective.Model, effective.Effort, effective.Attachments,
                new SessionPlan(null, effective.Session.FallbackPrompt));
            return await RunDetailedAsync(id, agent, effective.Session.FallbackPrompt, settings,
                fallbackWorkspace, access, fresh, onUpdate, cancellationToken);
        }
    }

    async Task<CLIRunResult> RunObservedAsync(
        Guid id, MemberID agent, Executor executor, string prompt, NativeSettings settings,
        string fallbackWorkspace, ExecutionAccess access, CLIRunOptions options,
        Action<string>? onUpdate, CancellationToken cancellationToken)
    {
        var observer = Observer;
        var delivered = false;
        observer?.Invoke(agent, CallEvent.Attempt);
        try
        {
            var result = await PerformAsync(id, agent, executor, prompt, settings, fallbackWorkspace,
                access, options, () => { if (!delivered) { delivered = true; observer?.Invoke(agent, CallEvent.Delivered); } },
                onUpdate, cancellationToken);
            observer?.Invoke(agent, CallEvent.Completed);
            return result with { Resumed = options.Session?.ResumeID is { Length: > 0 } };
        }
        catch (SessionUnavailable)
        {
            // The model was not reached: sent but refused before it.
            if (delivered) observer?.Invoke(agent, CallEvent.NotReached);
            throw;
        }
        catch (OperationCanceledException)
        {
            observer?.Invoke(agent, delivered ? CallEvent.Unknown : CallEvent.NotSent);
            throw;
        }
        catch
        {
            // Sent but unconfirmed is "unknown": it may have been processed.
            observer?.Invoke(agent, delivered ? CallEvent.Unknown : CallEvent.NotSent);
            throw;
        }
    }

    async Task<CLIRunResult> PerformAsync(
        Guid id, MemberID agent, Executor executor, string prompt, NativeSettings settings,
        string fallbackWorkspace, ExecutionAccess access, CLIRunOptions options,
        Action delivered, Action<string>? onUpdate, CancellationToken cancellationToken)
    {
        if (!executor.AcceptsFiles() && options.Attachments.Count > 0)
        {
            throw AttachmentError.FilesNotSupported(options.Attachments[0].Attachment.FileName);
        }
        var binary = CommandPlan.ResolveExecutable(executor, settings)
            ?? throw NativeCLIError.ExecutableMissing(executor.ExecutableName());
        var requestedCwd = CommandPlan.WorkingDirectory(settings, fallbackWorkspace);

        // Read-only prompts execute against a temporary sanitised work copy.
        // This reduces accidental inclusion of .env/SSH/cloud files; it is NOT
        // an OS sandbox and cannot prevent a CLI reading other home paths.
        var actualCwd = requestedCwd;
        string? scratch = null;
        if (access != ExecutionAccess.StagedWrite)
        {
            scratch = Path.Combine(Path.GetTempPath(), "airoom-readonly-" + Guid.NewGuid().ToString("D"));
            try
            {
                var copy = new WorkspaceStaging(scratch);
                actualCwd = copy.Prepare(Guid.NewGuid(), requestedCwd);
            }
            catch
            {
                TryDelete(scratch);
                throw;
            }
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExecuteAsync(id, agent, executor, binary, actualCwd, prompt, access, options, delivered, onUpdate, cancellationToken);
            if (executor == Executor.Antigravity
                && Antigravity.LostConversation(options.Session?.ResumeID, result.SessionID))
            {
                // `agy` silently started a fresh conversation: the model never saw
                // the earlier context, so this run cannot count as a continuation.
                throw new SessionUnavailable();
            }
            return result;
        }
        finally
        {
            if (scratch is not null) TryDelete(scratch);
        }
    }

    async Task<CLIRunResult> ExecuteAsync(
        Guid id, MemberID agent, Executor executor, string binary, string cwd, string prompt,
        ExecutionAccess access, CLIRunOptions options, Action delivered,
        Action<string>? onUpdate, CancellationToken cancellationToken)
    {
        if (executor == Executor.ZCode)
        {
            // The app-server speaks its own stdio protocol: zcode.cjs on a
            // plain Node (v22+) or, failing that, the desktop's own executable,
            // account config pushed per run, model auth answered as an interaction.
            var auth = ZCode.LoadAuth()
                ?? throw NativeCLIError.ProcessFailed("ZCode 需要先安裝並登入桌面版（找不到 ~/.zcode/v2/credentials.json）");
            var runtime = ZCode.ResolveRuntime(binary)
                ?? throw NativeCLIError.ProcessFailed("ZCode 的 CLI 需要 Node.js 22 以上，或安裝 ZCode 桌面版");
            var modelId = options.Model.Length > 0 ? options.Model : ZCode.DefaultModelId;
            var reasoning = options.Effort.Length > 0 ? options.Effort : "max";
            var zcodeResult = await ZCodeAppServerClient.RunAsync(runtime, binary, auth, prompt, cwd, access, modelId,
                reasoning, options.Session?.ResumeID, delivered, onUpdate, cancellationToken);
            return zcodeResult with { Resumed = options.Session?.ResumeID is { Length: > 0 } };
        }

        var arguments = CommandPlan.Arguments(executor, access, options);
        var startInfo = BuildStartInfo(binary, arguments, cwd);
        foreach (var pair in PrivacyGuard.CliEnvironment(
                     Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                         .ToDictionary(e => (string)e.Key, e => e.Value as string ?? ""), binary))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw NativeCLIError.ProcessFailed($"{executor.ShortName()} 無法啟動");
        active[id] = process;
        try
        {
            // Prompt is transmitted through stdin, not as an argument visible to other processes.
            var input = executor == Executor.Antigravity ? Antigravity.UserMessage(prompt)
                : options.Attachments.Count > 0 ? ClaudeStreamInput.UserMessage(prompt, options.Attachments)
                : Encoding.UTF8.GetBytes(prompt);
            await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken);
            delivered();
            process.StandardInput.Close();

            var parser = new CliEventAccumulator(executor);
            var stderrTask = process.StandardError.ReadToEndAsync();
            var readTask = PumpStdoutAsync(process, parser, onUpdate, cancellationToken);

            await readTask.WaitAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stderrText = await stderrTask.WaitAsync(cancellationToken);

            if (process.ExitCode != 0 || parser.Failure is not null)
            {
                if (options.Session?.ResumeID is { Length: > 0 }
                    && (stderrText.Contains("No conversation found", StringComparison.OrdinalIgnoreCase)
                        || parser.EndedBeforeModel && parser.Text.Length == 0))
                {
                    throw new SessionUnavailable();
                }
                var message = parser.Failure ?? stderrText.Trim();
                throw NativeCLIError.ProcessFailed(
                    $"{executor.ShortName()} 執行失敗（exit {process.ExitCode}）：{PrivacyGuard.Redact(message).Truncate(2_000)}");
            }
            if (string.IsNullOrWhiteSpace(parser.Text))
            {
                var detail = stderrText.Trim();
                throw NativeCLIError.NoResponse(executor.ShortName())
                    .With(detail.Length > 0 ? $"（{PrivacyGuard.Redact(detail).Truncate(300)}）" : "");
            }
            return new CLIRunResult(PrivacyGuard.Redact(parser.Text), parser.Model, parser.SessionID);
        }
        finally
        {
            active.TryRemove(id, out _);
        }
    }

    /// <summary>Reads stdout lines until EOF, emitting parsed text as it arrives.
    /// Bound untrusted output if a program never emits a newline.</summary>
    async Task PumpStdoutAsync(Process process, CliEventAccumulator parser, Action<string>? onUpdate,
                               CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(64 * 1024);
        var stream = process.StandardOutput.BaseStream;
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            buffer.AddRange(chunk[..read]);
            int newline;
            while ((newline = buffer.IndexOf((byte)0x0A)) >= 0)
            {
                var line = Encoding.UTF8.GetString(buffer.Take(newline).ToArray());
                buffer.RemoveRange(0, newline + 1);
                Emit(parser, line, onUpdate);
            }
            if (buffer.Count > MaxSingleLineBytes)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw NativeCLIError.ProcessFailed("CLI 輸出單行過長，已停止執行");
            }
        }
        if (buffer.Count > 0)
        {
            Emit(parser, Encoding.UTF8.GetString(buffer.ToArray()), onUpdate);
        }
    }

    static void Emit(CliEventAccumulator parser, string line, Action<string>? onUpdate)
    {
        if (parser.Consume(line) is { } text)
        {
            onUpdate?.Invoke(PrivacyGuard.Redact(text));
        }
    }

    static ProcessStartInfo BuildStartInfo(string binary, List<string> arguments, string cwd)
    {
        var extension = Path.GetExtension(binary).ToLowerInvariant();
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (extension is ".cmd" or ".bat")
        {
            // npm-style shims must run through the shell; `call` returns the
            // shim's own exit code instead of cmd.exe's.
            startInfo.FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            startInfo.Arguments = "/d /c call \"" + binary + "\" " + string.Join(' ', arguments.Select(QuoteArgument));
        }
        else
        {
            startInfo.FileName = binary;
            startInfo.Arguments = string.Join(' ', arguments.Select(QuoteArgument));
        }
        return startInfo;
    }

    static string QuoteArgument(string argument) => '"' + argument.Replace("\"", "\\\"") + '"';

    static void TryDelete(string? directory)
    {
        if (directory is null) return;
        try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Asks Claude Code for its model catalog through the SDK `initialize`
    /// control request. No user message is sent and no tokens are spent.
    /// </summary>
    public async Task<List<ModelChoice>> ClaudeModelsAsync(NativeSettings settings, int timeoutMilliseconds = 10_000)
    {
        var binary = CommandPlan.ResolveExecutable(Executor.Claude, settings)
            ?? throw NativeCLIError.ExecutableMissing("claude");
        var arguments = new List<string>
        {
            "-p", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json",
            "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
        };
        var startInfo = BuildStartInfo(binary, arguments, Path.GetTempPath());
        foreach (var pair in PrivacyGuard.CliEnvironment(
                     Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                         .ToDictionary(e => (string)e.Key, e => e.Value as string ?? ""), binary))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw NativeCLIError.ProcessFailed("Claude 無法啟動");
        var requestID = Guid.NewGuid().ToString("D");
        var request = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = requestID,
            request = new { subtype = "initialize" },
        }) + "\n";
        await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(request));
        process.StandardInput.Close();

        var lines = ChannelOfLines(process.StandardOutput.BaseStream);
        var deadline = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        using var timeout = new CancellationTokenSource(deadline);
        while (true)
        {
            string? line;
            try
            {
                line = await ReadLineAsync(lines, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw NativeCLIError.ProcessFailed("讀取模型清單逾時");
            }
            if (line is null) break;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var type) || type.GetString() != "control_response"
                    || !root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
                    || !response.TryGetProperty("request_id", out var responseID)
                    || responseID.GetString() != requestID)
                {
                    continue;
                }
                if (response.TryGetProperty("response", out var inner) && inner.ValueKind == JsonValueKind.Object
                    && inner.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                {
                    return ModelCatalog.ClaudeModelsFrom(models.Clone());
                }
                return [];
            }
            catch (JsonException)
            {
                // not the frame we are waiting for
            }
        }
        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        throw NativeCLIError.ProcessFailed("讀取模型清單失敗");
    }

    /// <summary>Stops the direct CLI process. Descendant cleanup is best-effort:
    /// Claude Code may have its own children; entireProcessTree covers them.</summary>
    public bool Cancel(Guid id)
    {
        if (active.TryRemove(id, out var process) && !process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); return true; }
            catch { return false; }
        }
        return false;
    }

    public void CancelAll()
    {
        foreach (var id in active.Keys.ToList()) Cancel(id);
    }

    // -- line channel helpers -------------------------------------------------

    static System.Threading.Channels.Channel<string> ChannelOfLines(Stream stream)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>(new System.Threading.Channels.UnboundedChannelOptions
        {
            SingleReader = true,
        });
        _ = Task.Run(async () =>
        {
            var builder = new StringBuilder();
            var buffer = new byte[8192];
            try
            {
                while (await stream.ReadAsync(buffer) is { } read && read > 0)
                {
                    builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    while (true)
                    {
                        var text = builder.ToString();
                        var newline = text.IndexOf('\n');
                        if (newline < 0) break;
                        channel.Writer.TryWrite(text[..newline].TrimEnd('\r'));
                        builder.Clear();
                        builder.Append(text[(newline + 1)..]);
                    }
                }
                var rest = builder.ToString();
                if (rest.Length > 0) channel.Writer.TryWrite(rest);
            }
            catch
            {
                // stream closed
            }
            channel.Writer.Complete();
        });
        return channel;
    }

    static async Task<string?> ReadLineAsync(System.Threading.Channels.Channel<string> channel, CancellationToken cancellationToken)
    {
        var reader = channel.Reader;
        if (await reader.WaitToReadAsync(cancellationToken) && reader.TryRead(out var line)) return line;
        return null;
    }
}

file static class StringExtensions
{
    public static string Truncate(this string text, int max) =>
        text.Length <= max ? text : text[..max];
}
