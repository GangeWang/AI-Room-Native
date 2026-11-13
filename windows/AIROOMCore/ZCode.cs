using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIROOMCore;

/// <summary>
/// ZCode — this agent's own CLI — runs headless through
/// `node zcode.cjs app-server`, a line protocol on stdio (not JSON-RPC: no
/// jsonrpc key, server asks back and 15s timeouts apply). The account provider
/// (`account:zai-start-plan`) is pushed from the shared credential store with
/// `provider/updateAccountConfig`, the model is bound with `session/setModel`,
/// and the model request auth (the ZCode JWT) is answered through the
/// `interaction/requestProviderRuntimeHeaders` round trip — verified live,
/// see windows/ZCODE-NOTES.md.
/// </summary>
public static class ZCode
{
    /// <summary>The desktop build version the plan gateway expects to see.</summary>
    public const string AppVersion = "3.17.2";
    public const string ProviderId = "account:zai-start-plan";
    public const string DefaultModelId = "GLM-5.3-Flash";

    /// <summary>The reasoning levels the plan models accept (the desktop sends max).</summary>
    public static readonly string[] Efforts = ["low", "medium", "high", "max"];

    /// <summary>Fallback model ids when the builtin config lists none for the provider.</summary>
    public static readonly string[] ModelIds =
    [
        "GLM-5.3", "GLM-5.3-Flash", "GLM-5.2", "GLM-5-Turbo", "GLM-5.1", "GLM-4.7", "GLM-4.6",
    ];

    /// <summary>Fallback model list; the live list comes from the builtin provider config.</summary>
    public static List<ModelChoice> ModelChoices() =>
    [
        new(DefaultModelId, "GLM-5.3-Flash", Efforts, true),
        new("GLM-5.3", "GLM-5.3", Efforts),
        new("GLM-5.2", "GLM-5.2", Efforts),
        new("GLM-5-Turbo", "GLM-5-Turbo", Efforts),
        new("GLM-5.1", "GLM-5.1", Efforts),
        new("GLM-4.7", "GLM-4.7", Efforts),
        new("GLM-4.6", "GLM-4.6", Efforts),
    ];

    /// <summary>Everything the app-server run needs from the shared state the
    /// desktop maintains. Null when the desktop has never signed in here.</summary>
    public sealed record Auth(string ZcodeJwt, string DeviceMid, string BuiltinFile, string BasedOnRevision, List<string> ModelIds);

    /// <summary>What runs zcode.cjs (macOS PR #14). ZCode's own executable can
    /// (as Node, via ELECTRON_RUN_AS_NODE), but then app-server and each tool
    /// server it starts — about six per turn — spawn from an Electron binary.
    /// A plain Node.js is lighter, so one is preferred when installed (v22 or
    /// newer); the desktop executable stays as the fallback.</summary>
    public sealed record Runtime(string Executable, bool IsElectron);

    /// <summary>"v26.7.0" → 26. Null when the output is not a version.</summary>
    public static int? NodeMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var digits = version.Trim().TrimStart('v');
        return int.TryParse(digits.Split('.')[0], out var major) && major > 0 ? major : null;
    }

    /// <summary>Runs `node --version`; null when it fails or hangs (5 s watchdog).</summary>
    public static string? InstalledNodeVersion(string nodeExe)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = nodeExe,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }
            return process.ExitCode == 0 ? outputTask.GetAwaiter().GetResult() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The runtime decision, in a testable shape: the first plain Node
    /// answering v22+ wins; otherwise the desktop executable (as Node).</summary>
    public static Runtime? PickRuntime(
        IEnumerable<string> nodeCandidates, string? desktopExe,
        Func<string, string?> nodeVersion, Func<string, bool> fileExists)
    {
        foreach (var node in nodeCandidates)
        {
            if (!fileExists(node)) continue;
            if (NodeMajor(nodeVersion(node)) is { } major && major >= 22)
            {
                return new Runtime(node, IsElectron: false);
            }
        }
        return desktopExe is not null && fileExists(desktopExe)
            ? new Runtime(desktopExe, IsElectron: true)
            : null;
    }

    /// <summary>Node lookup order: PATH first (the installer adds itself), then
    /// the two install locations a stale PATH sometimes misses.</summary>
    public static List<string> NodeCandidates()
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(dir, "node.exe"));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Volta", "bin", "node.exe"));
        return candidates;
    }

    /// <summary>The desktop executable that ships the CLI script:
    /// `…\ZCode\resources\glm\zcode.cjs` → `…\ZCode\ZCode.exe`. Null when the
    /// script does not sit in that layout (a custom path).</summary>
    public static string? DesktopExeFor(string cjsPath)
    {
        // glm → resources → the desktop folder.
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(cjsPath)));
        var exe = root is null ? null : Path.Combine(root, "ZCode.exe");
        return exe is not null && File.Exists(exe) ? exe : null;
    }

    /// <summary>Which process runs zcode.cjs for this install.</summary>
    public static Runtime? ResolveRuntime(string cjsPath) =>
        PickRuntime(NodeCandidates(), DesktopExeFor(cjsPath), InstalledNodeVersion, File.Exists);

    /// <summary>The session mode decides what a member may do. macOS PR #8
    /// found that without an explicit mode the session defaults to `build`,
    /// where every change asks for approval a headless run can never give —
    /// so members could not write at all. Read-only rooms use `plan`;
    /// writing rooms use `edit`.</summary>
    public static string ModeFor(ExecutionAccess access) =>
        access == ExecutionAccess.StagedWrite ? "edit" : "plan";

    /// <summary>The answer to any permission ask. ZCode validates answers
    /// strictly — `decision` is required, and an empty or extra-keyed object
    /// is rejected (macOS #8) — so the denial names both keys.</summary>
    public static Dictionary<string, object> PermissionDenial() => new()
    {
        ["decision"] = "deny",
        ["reason"] = "AIROOM 不允許這個動作：成員只能在自己的權限範圍內工作。",
    };

    /// <summary>
    /// Reads the shared state: `~/.zcode/v2/credentials.json` (values are
    /// `enc:v1:` AES-256-GCM with a key derived from the account-free machine
    /// string), `telemetry-state.json` (device id) and the newest downloaded
    /// builtin provider config (its path hashes into the account config's
    /// basedOn revision — a mismatch makes the registry drop the provider).
    /// </summary>
    public static Auth? LoadAuth(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var v2 = Path.Combine(home, ".zcode", "v2");
        string credentialsPath = Path.Combine(v2, "credentials.json");
        if (!File.Exists(credentialsPath)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(credentialsPath));
            if (record is null || record.Count == 0) return null;
            // os.platform() on the CLI side: "win32" / "darwin" / "linux".
            var platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
            var key = DeriveKey(platform, home, Environment.UserName);
            string? jwt = null;
            foreach (var (name, value) in record)
            {
                if (name != "zcodejwttoken") continue;
                jwt = DecryptValue(value, key);
                break;
            }
            if (string.IsNullOrWhiteSpace(jwt)) return null;

            var deviceMid = "";
            var telemetryPath = Path.Combine(v2, "telemetry-state.json");
            if (File.Exists(telemetryPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(telemetryPath));
                deviceMid = doc.RootElement.TryGetProperty("deviceMid", out var mid) ? mid.GetString() ?? "" : "";
            }

            var builtin = NewestBuiltinFile(v2);
            if (builtin is null) return null;
            var basedOn = BasedOnRevision(builtin);
            var modelIds = BuiltinModelIds(builtin, ProviderId) is { Count: > 0 } ids ? ids : [.. ModelIds];

            return new Auth(jwt, deviceMid, builtin, basedOn, modelIds);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The credential cipher key: sha256 of the machine string the CLI
    /// falls back to when ZCODE_CREDENTIAL_SECRET is unset.</summary>
    public static byte[] DeriveKey(string platform, string home, string user) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"zcode-credential-fallback:{platform}:{home}:{user}"));

    /// <summary>Decrypts one `enc:v1:<iv>.<tag>.<data>` base64url triple; plain
    /// values pass through untouched.</summary>
    public static string DecryptValue(string value, byte[] key)
    {
        const string prefix = "enc:v1:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return value;
        var parts = value[prefix.Length..].Split('.');
        if (parts.Length != 3) throw new CryptographicException("Credential decrypt failed: invalid ciphertext format");
        var nonce = Base64Url(parts[0]);
        var tag = Base64Url(parts[1]);
        var cipher = Base64Url(parts[2]);
        if (nonce.Length != 12) throw new CryptographicException("Credential decrypt failed: invalid IV length");
        var plaintext = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>The newest downloaded builtin provider config under
    /// `~/.zcode/v2/runtime/provider/&lt;platform&gt;/&lt;version&gt;/endpoint-*/`.</summary>
    public static string? NewestBuiltinFile(string v2Dir)
    {
        var runtime = Path.Combine(v2Dir, "runtime", "provider");
        if (!Directory.Exists(runtime)) return null;
        return Directory.EnumerateFiles(runtime, "zcode-builtin.json", SearchOption.AllDirectories)
            .Select(file => new FileInfo(file))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    /// <summary>`zcode-builtin:&lt;revision&gt;:&lt;sha256 of the resolved file path&gt;` —
    /// the exact string the registry compares the pushed account config against.</summary>
    public static string BasedOnRevision(string builtinFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(builtinFile));
        var revision = (int)(doc.RootElement.TryGetProperty("revision", out var value) ? value.GetDouble() : 0);
        var nonce = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(builtinFile)))).ToLowerInvariant();
        return $"zcode-builtin:{revision}:{nonce}";
    }

    /// <summary>The builtin rule's model ids for one account provider id.</summary>
    public static List<string>? BuiltinModelIds(string builtinFile, string providerId)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(builtinFile));
            if (!doc.RootElement.TryGetProperty("config", out var config)
                || !config.TryGetProperty("providerConfigRules", out var rules)
                || !rules.TryGetProperty("providerRules", out var list)
                || list.ValueKind != JsonValueKind.Array) return null;
            foreach (var rule in list.EnumerateArray())
            {
                if (!rule.TryGetProperty("providerId", out var id) || id.GetString() != providerId) continue;
                if (!rule.TryGetProperty("config", out var config2)
                    || !config2.TryGetProperty("builtinModelIds", out var models)
                    || models.ValueKind != JsonValueKind.Array) return null;
                var ids = models.EnumerateArray()
                    .Where(m => m.ValueKind == JsonValueKind.String)
                    .Select(m => m.GetString() ?? "")
                    .Where(s => s.Length > 0).ToList();
                return ids;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    static byte[] Base64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

}

/// <summary>
/// One app-server process per turn: session create (or resume), account config
/// push, setModel, send, then the interaction answers while the turn runs.
/// Sessions persist in the CLI's own database, so `session/resume` continues
/// earlier conversations across processes.
/// </summary>
public static class ZCodeAppServerClient
{
    public static async Task<CLIRunResult> RunAsync(
        ZCode.Runtime runtime, string cjsPath, ZCode.Auth auth, string prompt, string cwd,
        ExecutionAccess access, string modelId, string reasoningLevel, string? resumeId, Action delivered,
        Action<string>? onUpdate, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runtime.Executable,
            Arguments = $"\"{cjsPath}\" app-server",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = cwd,
        };
        // Only the desktop's own executable needs telling to act as Node.
        if (runtime.IsElectron) startInfo.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        startInfo.Environment["ZCODE_BUILTIN_PROVIDER_CONFIG_FILE"] = auth.BuiltinFile;
        startInfo.Environment["ZCODE_PERSONAL_PROVIDER_CONFIG_FILE"] =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode", "v2", "provider_config.json");
        startInfo.Environment["ZCODE_APP_VERSION"] = ZCode.AppVersion;
        if (auth.DeviceMid.Length > 0) startInfo.Environment["ZCODE_TELEMETRY_DEVICE_MID"] = auth.DeviceMid;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw NativeCLIError.ProcessFailed("ZCode app-server 無法啟動");

        var pending = new System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>();
        var nextId = 0;
        var turnEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? turnFailure = null;
        string? sessionId = resumeId;
        var sessionKnown = false;

        Task StdoutPump()
        {
            return Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.Length == 0) continue;
                    JsonElement msg;
                    try
                    {
                        msg = JsonDocument.Parse(line).RootElement.Clone();
                    }
                    catch
                    {
                        continue;
                    }
                    if (msg.TryGetProperty("id", out var idProp) && msg.TryGetProperty("result", out var result)
                        && idProp.TryGetInt32(out var requestId) && pending.TryRemove(requestId, out var waiter))
                    {
                        waiter.TrySetResult(result.Clone());
                        continue;
                    }
                    if (!msg.TryGetProperty("method", out var methodProp)) continue;
                    var method = methodProp.GetString();
                    switch (method)
                    {
                        case "session/requestRuntimePreferences":
                            if (!sessionKnown && msg.TryGetProperty("params", out var prefsParams)
                                && prefsParams.TryGetProperty("sessionId", out var prefsSession))
                            {
                                sessionId = prefsSession.GetString();
                                sessionKnown = true;
                            }
                            Respond(msg, new Dictionary<string, object> { ["nativeSearchEnhancementsEnabled"] = false });
                            break;
                        case "interaction/requestPermission":
                            // ZCode validates answers strictly: `decision` is
                            // required and an empty or extra-keyed object is
                            // rejected (macOS #8). Deny everything — members
                            // work inside their own access, never beyond it.
                            Respond(msg, ZCode.PermissionDenial());
                            break;
                        case "interaction/requestProviderRuntimeHeaders":
                            Respond(msg, new Dictionary<string, object>
                            {
                                ["headersApplied"] = true,
                                ["requestAuth"] = new Dictionary<string, object>
                                {
                                    ["apiKey"] = auth.ZcodeJwt,
                                    ["headers"] = new Dictionary<string, object>
                                    {
                                        ["Authorization"] = $"Bearer {auth.ZcodeJwt}",
                                    },
                                },
                            });
                            break;
                        case "state.updated":
                            // This build never announces turn.completed; a failed
                            // turn surfaces only as this reason on state.updated.
                            if (msg.TryGetProperty("params", out var failedState)
                                && failedState.TryGetProperty("reason", out var stateReason)
                                && stateReason.GetString() == "prompt_failed")
                            {
                                turnFailure ??= "模型請求失敗（app-server 回報 prompt_failed）";
                                turnEnded.TrySetResult(true);
                            }
                            break;
                        default:
                            if (msg.TryGetProperty("id", out _))
                            {
                                // interaction/requestOfficialMcpAuthHeaders and any
                                // other ask: an empty result is accepted.
                                Respond(msg, new Dictionary<string, object>());
                            }
                            else if (method is "turn.completed" or "turn.failed")
                            {
                                if (method == "turn.failed")
                                {
                                    turnFailure = msg.TryGetProperty("params", out var failed)
                                        ? PrivacyGuard.Redact(failed.ToString()) : null;
                                }
                                turnEnded.TrySetResult(true);
                            }
                            break;
                    }
                }
            }, cancellationToken);
        }

        void Write(object? id, Dictionary<string, object> payload)
        {
            var message = new Dictionary<string, object>();
            if (id is not null) message["id"] = id;
            foreach (var pair in payload) message[pair.Key] = pair.Value;
            // LF only: the server's line protocol mirrors the Node probes.
            process.StandardInput.BaseStream.Write(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(message) + "\n"));
            process.StandardInput.BaseStream.Flush();
        }

        void Respond(JsonElement request, Dictionary<string, object> result)
        {
            if (!request.TryGetProperty("id", out var id)) return;
            Write(id.Clone(), new Dictionary<string, object> { ["result"] = result });
        }

        Task<JsonElement> Send(string method, Dictionary<string, object> parameters)
        {
            var requestId = Interlocked.Increment(ref nextId);
            var wait = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[requestId] = wait;
            Write(requestId, new Dictionary<string, object>
            {
                ["method"] = method,
                ["params"] = parameters,
            });
            return wait.Task.WaitAsync(cancellationToken);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var pump = StdoutPump();
        try
        {
            if (resumeId is { Length: > 0 })
            {
                try
                {
                    await Send("session/resume", new Dictionary<string, object> { ["sessionId"] = resumeId });
                }
                catch
                {
                    // The earlier conversation is gone; start a fresh one and
                    // let the caller decide whether to restate the context.
                    throw new SessionUnavailable();
                }
            }
            else
            {
                await Send("session/create", new Dictionary<string, object>
                {
                    ["workspace"] = new Dictionary<string, object>
                    {
                        ["workspacePath"] = cwd,
                        ["workspaceKey"] = cwd.ToLowerInvariant(),
                    },
                });
            }
            if (string.IsNullOrEmpty(sessionId)) throw NativeCLIError.ProcessFailed("ZCode app-server 未回傳 session id");

            await Send("provider/updateAccountConfig", new Dictionary<string, object>
            {
                ["revision"] = $"airoom:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                ["basedOnZCodeBuiltinRevision"] = auth.BasedOnRevision,
                ["providers"] = new Dictionary<string, object>
                {
                    [ZCode.ProviderId] = new Dictionary<string, object>
                    {
                        ["builtinModelIds"] = auth.ModelIds,
                        ["access"] = new Dictionary<string, object>
                        {
                            ["type"] = "zhipu-account",
                            ["entitled"] = true,
                        },
                    },
                },
                ["states"] = new Dictionary<string, object>
                {
                    [ZCode.ProviderId] = new Dictionary<string, object>
                    {
                        ["availability"] = "available",
                        ["entitled"] = true,
                        ["current"] = true,
                    },
                },
            });

            await Send("session/setModel", new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["model"] = new Dictionary<string, object>
                {
                    ["providerId"] = ZCode.ProviderId,
                    ["modelId"] = modelId,
                    ["options"] = new Dictionary<string, object> { ["reasoningLevel"] = reasoningLevel },
                },
            });

            // Without an explicit mode the session sits in `build`, where every
            // change wants an approval headless runs can never give (macOS #8).
            await Send("session/setMode", new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["mode"] = ZCode.ModeFor(access),
            });

            // Subscribe must reach the server before the send, or the turn's
            // notifications never arrive.
            Write(null, new Dictionary<string, object>
            {
                ["method"] = "session/subscribe",
                ["params"] = new Dictionary<string, object> { ["sessionId"] = sessionId },
            });

            _ = Send("session/send", new Dictionary<string, object> { ["sessionId"] = sessionId, ["content"] = prompt })
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) turnEnded.TrySetException(t.Exception?.InnerException ?? t.Exception ?? new Exception("session/send failed"));
                }, CancellationToken.None);
            delivered();

            // The poll doubles as the completion detector and keeps the
            // bubbles growing while the turn streams.
            string? lastText = null;
            while (!turnEnded.Task.IsCompletedSuccessfully)
            {
                try
                {
                    if (await Task.WhenAny(turnEnded.Task, Task.Delay(900, cancellationToken)) == turnEnded.Task) break;
                }
                catch (OperationCanceledException)
                {
                    Write(null, new Dictionary<string, object>
                    {
                        ["method"] = "session/stop",
                        ["params"] = new Dictionary<string, object> { ["sessionId"] = sessionId },
                    });
                    throw;
                }
                if (sessionId is null) continue;
                try
                {
                    var snapshot = await Send("session/messages", new Dictionary<string, object> { ["sessionId"] = sessionId });
                    var (done, text) = ZCodeTurn.Result(snapshot);
                    if (done) turnEnded.TrySetResult(true);
                    if (onUpdate is not null && text.Length > 0 && text != lastText)
                    {
                        lastText = text;
                        onUpdate(text);
                    }
                }
                catch
                {
                    // Polling is best effort; the final fetch decides.
                }
            }

            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (turnFailure is not null)
            {
                var detail = turnFailure.Length > 2_000 ? turnFailure[..2_000] : turnFailure;
                throw NativeCLIError.ProcessFailed($"ZCode 回合失敗：{detail}");
            }

            var messages = await Send("session/messages", new Dictionary<string, object> { ["sessionId"] = sessionId });
            var finalText = ZCodeTurn.FinalText(messages);
            if (string.IsNullOrWhiteSpace(finalText))
            {
                throw NativeCLIError.NoResponse("ZCode");
            }
            return new CLIRunResult(PrivacyGuard.Redact(finalText), modelId, sessionId);
        }
        finally
        {
            pending.Clear();
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
        }

    }
}

/// <summary>Turn completion, read purely from the session's message list: this
/// server build never announces turn.completed, so the messages are the signal.
/// A turn's reply takes two shapes:
/// - plain assistant text;
/// - ZCode's plan mode: the model hands the plan to the ExitPlanMode tool call,
///   so the final assistant message has NO text — the plan hides in the tool
///   part's input.plan (seen live: GLM planned the whole flow that way).
/// Only the LAST assistant message decides: an intermediate narration before
/// tool calls is not the reply and must not complete the turn early.</summary>
public static class ZCodeTurn
{
    public static (bool Done, string Text) Result(JsonElement messages)
    {
        if (!messages.TryGetProperty("messages", out var list) || list.ValueKind != JsonValueKind.Array) return (false, "");
        long lastUserCreated = long.MinValue;
        foreach (var message in list.EnumerateArray())
        {
            if (message.TryGetProperty("info", out var info) && info.TryGetProperty("role", out var role)
                && role.GetString() == "user"
                && info.TryGetProperty("time", out var stamp)
                && stamp.TryGetProperty("created", out var at) && at.ValueKind == JsonValueKind.Number)
            {
                lastUserCreated = at.GetInt64();
            }
        }
        JsonElement? lastAssistant = null;
        string? handedPlan = null;
        foreach (var message in list.EnumerateArray())
        {
            if (!message.TryGetProperty("info", out var info)
                || !info.TryGetProperty("role", out var role)
                || role.GetString() != "assistant") continue;
            var stepCreated = info.TryGetProperty("time", out var step)
                && step.TryGetProperty("created", out var stepMade) && stepMade.ValueKind == JsonValueKind.Number
                    ? stepMade.GetInt64() : long.MinValue;
            if (stepCreated <= lastUserCreated) continue;
            lastAssistant = message.Clone();
            // A plan handed to ExitPlanMode outranks the narration that follows it.
            if (ToolPlan(message) is { Length: > 0 } handed) handedPlan = handed;
        }
        if (lastAssistant is not { } final) return (false, "");
        var created = final.TryGetProperty("info", out var finalInfo)
            && finalInfo.TryGetProperty("time", out var time)
            && time.TryGetProperty("created", out var made) && made.ValueKind == JsonValueKind.Number
                ? made.GetInt64() : long.MinValue;
        if (created <= lastUserCreated) return (false, "");
        var completed = finalInfo.TryGetProperty("time", out var span)
            && span.TryGetProperty("completed", out var finished) && finished.ValueKind == JsonValueKind.Number;
        var body = Text(final);
        if (!completed) return (false, body);
        if (handedPlan is { Length: > 0 } reply) return (true, reply);
        if (body.Length > 0) return (true, body);
        return (false, body);
    }

    /// <summary>The text to deliver once the poll gives up waiting for a completion
    /// announcement: the last assistant message's text, else a handed-over plan.</summary>
    public static string FinalText(JsonElement messages)
    {
        if (!messages.TryGetProperty("messages", out var list) || list.ValueKind != JsonValueKind.Array) return "";
        JsonElement? lastAssistant = null;
        foreach (var message in list.EnumerateArray())
        {
            if (!message.TryGetProperty("info", out var info)
                || !info.TryGetProperty("role", out var role)
                || role.GetString() != "assistant") continue;
            lastAssistant = message.Clone();
        }
        if (lastAssistant is not { } final) return "";
        string? handedPlan = null;
        foreach (var message in list.EnumerateArray())
        {
            if (!message.TryGetProperty("info", out var info)
                || !info.TryGetProperty("role", out var role)
                || role.GetString() != "assistant") continue;
            if (ToolPlan(message) is { Length: > 0 } handed) handedPlan = handed;
        }
        if (handedPlan is { Length: > 0 } reply) return reply;
        var body = Text(final);
        if (body.Length > 0) return body;
        return ToolPlan(final) ?? "";
    }

    static string Text(JsonElement message)
    {
        var builder = new StringBuilder();
        if (message.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && part.TryGetProperty("text", out var value)) builder.Append(value.GetString());
            }
        }
        return builder.ToString();
    }

    /// <summary>The plan a plan-mode model hands to ExitPlanMode. The AI SDK names
    /// tool parts `tool-<name>`; other shapes carry `toolName`. The plan travels
    /// as the tool call's input.plan.</summary>
    static string? ToolPlan(JsonElement message)
    {
        if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) return null;
        foreach (var part in parts.EnumerateArray())
        {
            if (!part.TryGetProperty("type", out var type)) continue;
            var kind = type.GetString() ?? "";
            var isTool = kind.StartsWith("tool-", StringComparison.Ordinal) || kind == "tool-call";
            if (!isTool && !(part.TryGetProperty("toolName", out var name) && name.GetString() == "ExitPlanMode")) continue;
            if (part.TryGetProperty("input", out var input) && input.TryGetProperty("plan", out var plan)
                && plan.ValueKind == JsonValueKind.String)
            {
                return plan.GetString();
            }
            if (part.TryGetProperty("plan", out var direct) && direct.ValueKind == JsonValueKind.String)
            {
                return direct.GetString();
            }
        }
        return null;
    }
}
