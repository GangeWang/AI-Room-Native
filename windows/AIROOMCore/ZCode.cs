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
        string nodeExe, string cjsPath, ZCode.Auth auth, string prompt, string cwd,
        string modelId, string reasoningLevel, string? resumeId, Action delivered,
        Action<string>? onUpdate, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = nodeExe,
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
                    var (done, text) = TurnResult(snapshot);
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
            var finalText = AssistantText(messages);
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

        /// <summary>Decides whether the turn has produced its reply, purely from
        /// server timestamps: an assistant message created after the turn's own
        /// user message, carrying a completion time and actual text. The session
        /// greeting bubble (created just before the user message, no text) and
        /// replies from earlier turns never qualify.</summary>
        static (bool Done, string Text) TurnResult(JsonElement messages)
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
            var done = false;
            string text = "";
            foreach (var message in list.EnumerateArray())
            {
                if (!message.TryGetProperty("info", out var info)
                    || !info.TryGetProperty("role", out var role)
                    || role.GetString() != "assistant") continue;
                var created = info.TryGetProperty("time", out var time)
                    && time.TryGetProperty("created", out var made) && made.ValueKind == JsonValueKind.Number
                        ? made.GetInt64() : long.MinValue;
                var completed = info.TryGetProperty("time", out var span)
                    && span.TryGetProperty("completed", out var finished) && finished.ValueKind == JsonValueKind.Number;
                var body = TextParts(message);
                if (created > lastUserCreated && completed && body.Length > 0)
                {
                    done = true;
                    text = body;
                }
            }
            return (done, text);
        }

        static string TextParts(JsonElement message)
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

        static string AssistantText(JsonElement messages)
        {
            if (!messages.TryGetProperty("messages", out var list) || list.ValueKind != JsonValueKind.Array) return "";
            string? text = null;
            foreach (var message in list.EnumerateArray())
            {
                if (!message.TryGetProperty("info", out var info)
                    || !info.TryGetProperty("role", out var role)
                    || role.GetString() != "assistant") continue;
                if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                var builder = new StringBuilder();
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "text"
                        && part.TryGetProperty("text", out var value)) builder.Append(value.GetString());
                }
                if (builder.Length > 0) text = builder.ToString();
            }
            return text ?? "";
        }
    }
}
