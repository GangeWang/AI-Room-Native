using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AIROOMCore;

// MARK: VTube Studio public API (local WebSocket, default ws://127.0.0.1:8001).
// Ported from macOS VTubeStudio.swift. Only a fixed set of message types is
// ever sent. The connection is loopback-only; a model's output can select an
// emotion, never a raw request or hotkey ID.

public sealed record VTSHotkey(string Id, string Name, string Type)
{
    /// <summary>VTS expression hotkeys toggle; triggering one again turns it off.</summary>
    public bool Toggles => Type == "ToggleExpression";
}

public sealed record VTSModel(bool Loaded, string Name, string Id);

/// <summary>VTS failure kinds; the message texts match the macOS side.</summary>
public enum VTSErrorKind
{
    Api, NotAuthenticated, TimedOut, Disconnected, UnexpectedResponse, UnknownHotkey, NeedsApproval,
}

public sealed class VTSErrorException(VTSErrorKind kind, string message) : Exception(message)
{
    public readonly VTSErrorKind Kind = kind;

    public static VTSErrorException Api(int code, string message) => new(VTSErrorKind.Api, $"VTube Studio 錯誤 {code}：{message}");
    public static VTSErrorException NotAuthenticated(string reason) => new(VTSErrorKind.NotAuthenticated, $"VTube Studio 未授權外掛：{reason}");
    public static VTSErrorException TimedOut(string type) => new(VTSErrorKind.TimedOut, $"VTube Studio 沒有回應（{type}）。請確認 VTS 已開啟並啟用 API（設定 → Start API）。");
    public static VTSErrorException Disconnected() => new(VTSErrorKind.Disconnected, "未連線到 VTube Studio。");
    public static VTSErrorException UnexpectedResponse(string type) => new(VTSErrorKind.UnexpectedResponse, $"VTube Studio 回應格式不符：{type}");
    public static VTSErrorException UnknownHotkey(string id) => new(VTSErrorKind.UnknownHotkey, $"目前模型沒有這個熱鍵：{id}");
    public static VTSErrorException NeedsApproval() => new(VTSErrorKind.NeedsApproval, "VTube Studio 授權已失效，請按「連線」並在 VTS 按 Allow。");
}

public sealed record VTSEnvelope(string RequestID, string MessageType, JsonElement? Data)
{
    public static VTSEnvelope Of(string requestID, string messageType, object? data = null) =>
        new(requestID, messageType, data is null ? null : JsonSerializer.SerializeToElement(data));
}

public static class VTSProtocol
{
    public const string ApiName = "VTubeStudioPublicAPI";
    public const string ApiVersion = "1.0";
    public const string PluginDeveloper = "AIROOM";

    /// <summary>One plugin name per member. VTube Studio keeps one grant per
    /// plugin name, and every instance started from the same app shares those
    /// grants: with a single name, clicking Allow for the second member revoked
    /// the first member's token. VTS accepts 3–32 characters, so the handle is
    /// kept ASCII.</summary>
    public static string PluginName(MemberID member)
    {
        var handle = new string(member.Handle.Where(character => char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_').Take(16).ToArray());
        return handle.Length == 0 ? "AIROOM Native" : $"AIROOM Native ({handle})";
    }

    static Dictionary<string, object?> Plugin(string name) =>
        new() { ["pluginName"] = name, ["pluginDeveloper"] = PluginDeveloper };

    public static VTSEnvelope TokenRequest(string id, string pluginName) =>
        VTSEnvelope.Of(id, "AuthenticationTokenRequest", Plugin(pluginName));

    public static VTSEnvelope Authenticate(string id, string token, string pluginName)
    {
        var data = Plugin(pluginName);
        data["authenticationToken"] = token;
        return VTSEnvelope.Of(id, "AuthenticationRequest", data);
    }

    /// <summary>A custom input this plugin owns (VTS keeps it; creating it again
    /// just updates it). Names must be 4–32 letters/digits.</summary>
    public static VTSEnvelope CreateParameter(string id, string name, string explanation,
        double min, double max, double defaultValue) =>
        VTSEnvelope.Of(id, "ParameterCreationRequest", new Dictionary<string, object?>
        {
            ["parameterName"] = name,
            ["explanation"] = explanation.Length > 200 ? explanation[..200] : explanation,
            ["min"] = min, ["max"] = max, ["defaultValue"] = defaultValue,
        });

    public static VTSEnvelope AvailableModels(string id) => VTSEnvelope.Of(id, "AvailableModelsRequest");
    public static VTSEnvelope LoadModel(string id, string modelID) =>
        VTSEnvelope.Of(id, "ModelLoadRequest", new Dictionary<string, object?> { ["modelID"] = modelID });
    public static VTSEnvelope CurrentModel(string id) => VTSEnvelope.Of(id, "CurrentModelRequest");
    public static VTSEnvelope Hotkeys(string id) => VTSEnvelope.Of(id, "HotkeysInCurrentModelRequest");
    public static VTSEnvelope Trigger(string id, string hotkeyID) =>
        VTSEnvelope.Of(id, "HotkeyTriggerRequest", new Dictionary<string, object?> { ["hotkeyID"] = hotkeyID });

    /// <summary>`faceFound: true` makes VTS treat the injected values as a
    /// tracked face; with `false` it treats tracking as lost and eases the head
    /// back to rest, so injected head angles never show. Values are clamped:
    /// head angles to ±30, gaze to ±1, everything else to 0…1.</summary>
    public static VTSEnvelope Inject(string id, (string, double)[] parameters)
    {
        var values = parameters.Select(parameter =>
        {
            var (name, value) = parameter;
            var range = name.StartsWith("FaceAngle", StringComparison.Ordinal) ? (-30.0, 30.0)
                : name.StartsWith("FacePosition", StringComparison.Ordinal) ? (-15.0, 15.0)
                : name.StartsWith("AIROOM", StringComparison.Ordinal) ? (-1.0, 1.0)
                : name.StartsWith("EyeLeft", StringComparison.Ordinal) || name.StartsWith("EyeRight", StringComparison.Ordinal) ? (-1.0, 1.0)
                : (0.0, 1.0);
            return new Dictionary<string, object?>
            {
                ["id"] = name,
                ["value"] = Math.Min(range.Item2, Math.Max(range.Item1, value)),
            };
        }).ToArray();
        return VTSEnvelope.Of(id, "InjectParameterDataRequest", new Dictionary<string, object?>
        {
            ["faceFound"] = true, ["mode"] = "set", ["parameterValues"] = values,
        });
    }

    public static string Encode(VTSEnvelope envelope)
    {
        var data = envelope.Data is { } element
            ? JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["apiName"] = ApiName, ["apiVersion"] = ApiVersion,
                ["requestID"] = envelope.RequestID, ["messageType"] = envelope.MessageType, ["data"] = element,
            })
            : JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["apiName"] = ApiName, ["apiVersion"] = ApiVersion,
                ["requestID"] = envelope.RequestID, ["messageType"] = envelope.MessageType,
            });
        return data;
    }

    public static VTSEnvelope? Decode(string text)
    {
        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(text);
            var requestID = element.TryGetProperty("requestID", out var request) && request.ValueKind == JsonValueKind.String
                ? request.GetString()! : "";
            var messageType = element.TryGetProperty("messageType", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()! : "";
            JsonElement? data = null;
            if (element.TryGetProperty("data", out var payload)) data = payload.Clone();
            return new VTSEnvelope(requestID, messageType, data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Converts an APIError response into a thrown error; otherwise returns data.</summary>
    public static JsonElement Payload(VTSEnvelope envelope, string expecting)
    {
        if (envelope.MessageType == "APIError")
        {
            throw VTSErrorException.Api(
                envelope.Data is { } error && error.TryGetProperty("errorID", out var code) && code.ValueKind == JsonValueKind.Number
                    ? code.GetInt32() : -1,
                envelope.Data is { } body && body.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString() ?? "unknown" : "unknown");
        }
        if (envelope.MessageType != expecting) throw VTSErrorException.UnexpectedResponse(envelope.MessageType);
        return envelope.Data ?? JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
    }

    public static List<VTSHotkey> ParseHotkeys(JsonElement data)
    {
        var result = new List<VTSHotkey>();
        if (data.TryGetProperty("availableHotkeys", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("hotkeyID", out var id) || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { } hotkeyID || hotkeyID.Length == 0) continue;
                var name = item.TryGetProperty("name", out var hotkeyName) && hotkeyName.ValueKind == JsonValueKind.String
                    ? hotkeyName.GetString() ?? hotkeyID : hotkeyID;
                var type = item.TryGetProperty("type", out var hotkeyType) && hotkeyType.ValueKind == JsonValueKind.String
                    ? hotkeyType.GetString() ?? "" : "";
                result.Add(new VTSHotkey(hotkeyID, name, type));
            }
        }
        return result;
    }

    public static List<VTSModel> ParseModels(JsonElement data)
    {
        var result = new List<VTSModel>();
        if (data.TryGetProperty("availableModels", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("modelID", out var id) || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { } modelID || modelID.Length == 0) continue;
                var name = item.TryGetProperty("modelName", out var modelName) && modelName.ValueKind == JsonValueKind.String
                    ? modelName.GetString() ?? modelID : modelID;
                var loaded = item.TryGetProperty("modelLoaded", out var flag) && flag.ValueKind == JsonValueKind.True;
                result.Add(new VTSModel(loaded, name, modelID));
            }
        }
        return result;
    }
}

// MARK: Transport

/// <summary>Seam for tests: a scripted transport drives the client without a socket.</summary>
public interface IVTSTransport : IDisposable
{
    Task SendAsync(string text, CancellationToken token = default);
    Task<string> ReceiveAsync(CancellationToken token = default);
    void Close();
}

/// <summary>Loopback only; the port is the one shown in VTS → Settings → "Start API".</summary>
public sealed class WebSocketVTSTransport : IVTSTransport
{
    readonly ClientWebSocket socket = new();
    readonly ArraySegment<byte> buffer = new(new byte[512 * 1024]);

    public WebSocketVTSTransport(int port)
    {
        var clamped = Math.Min(65_535, Math.Max(1_024, port));
        socket.ConnectAsync(new Uri($"ws://127.0.0.1:{clamped}"), CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task SendAsync(string text, CancellationToken token = default) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, token);

    public async Task<string> ReceiveAsync(CancellationToken token = default)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, token);
            if (received.MessageType == WebSocketMessageType.Close) throw new IOException("VTS WebSocket 已關閉");
            await message.WriteAsync(buffer.Array!.AsMemory(0, received.Count), token);
            if (received.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(message.ToArray());
    }

    public void Close()
    {
        try
        {
            using var closed = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closed.Token).GetAwaiter().GetResult();
        }
        catch { /* a dropped socket is as closed as it needs to be */ }
        socket.Dispose();
    }

    public void Dispose() => Close();
}

// MARK: Client

/// <summary>Request/response client over a VTS WebSocket. One instance per
/// member: its own transport, its own pending map. Fire-and-forget injections
/// carry no waiter; a resulting APIError lands in LastUnmatchedError.</summary>
public sealed class VTubeStudioClient : IDisposable
{
    public Func<int, IVTSTransport> MakeTransport { get; init; } = port => new WebSocketVTSTransport(port);

    IVTSTransport? transport;
    CancellationTokenSource? receiver;
    readonly ConcurrentDictionary<string, TaskCompletionSource<VTSEnvelope>> pending = new();
    readonly object sending = new();
    volatile string? lastUnmatchedError;
    volatile int injectedFrames;
    volatile bool authenticated;
    readonly Dictionary<string, VTSHotkey> knownHotkeys = [];

    /// <summary>Called once when an authenticated link drops by itself (not on Disconnect).</summary>
    public event Action? ConnectionLost;

    public bool Authenticated => authenticated;
    public int InjectedFrames => injectedFrames;
    public string? LastUnmatchedError => lastUnmatchedError;

    /// <summary>Connects and authenticates. With no (or a revoked) token, VTS
    /// shows an "Allow plugin" dialog; this waits up to `approvalTimeoutSeconds`
    /// for the user. With `mayAsk: false` (automatic reconnects) it never shows
    /// that dialog and throws NeedsApproval instead. Returns the token to
    /// persist and the currently loaded model.</summary>
    public async Task<(string Token, VTSModel Model)> ConnectAsync(int port, string token, string plugin,
        bool mayAsk = true, int approvalTimeoutSeconds = 90, CancellationToken cancellation = default)
    {
        Disconnect();
        var link = MakeTransport(port);
        transport = link;
        receiver = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!receiver!.IsCancellationRequested)
            {
                try
                {
                    var text = await link.ReceiveAsync(receiver.Token);
                    if (VTSProtocol.Decode(text) is { } envelope) Deliver(envelope);
                }
                catch
                {
                    ConnectionWasLost(link);
                    return;
                }
            }
        }, CancellationToken.None);
        var granted = token.ToString();
        string newToken;
        if (granted.Length > 0 && await AuthenticateAsync(granted, plugin, cancellation))
        {
            newToken = granted;
        }
        else
        {
            if (!mayAsk) throw VTSErrorException.NeedsApproval();
            var reply = await RequestAsync(VTSProtocol.TokenRequest(NewID(), plugin), approvalTimeoutSeconds, cancellation);
            var data = VTSProtocol.Payload(reply, "AuthenticationTokenResponse");
            if (!data.TryGetProperty("authenticationToken", out var issued) || issued.ValueKind != JsonValueKind.String
                || issued.GetString() is not { } value || value.Length == 0)
            {
                throw VTSErrorException.UnexpectedResponse("AuthenticationTokenResponse");
            }
            newToken = value;
            if (!await AuthenticateAsync(newToken, plugin, cancellation))
            {
                throw VTSErrorException.NotAuthenticated("使用者拒絕或 Token 無效");
            }
        }
        return (newToken, await CurrentModelAsync(cancellation));
    }

    public void Disconnect()
    {
        receiver?.Cancel();
        receiver = null;
        transport?.Close();
        transport = null;
        authenticated = false;
        lock (knownHotkeys) knownHotkeys.Clear();
        FailPending(VTSErrorException.Disconnected());
    }

    public void Dispose() => Disconnect();

    public async Task<VTSModel> CurrentModelAsync(CancellationToken token = default)
    {
        var data = VTSProtocol.Payload(await RequestAsync(VTSProtocol.CurrentModel(NewID()), token: token), "CurrentModelResponse");
        return new VTSModel(
            data.TryGetProperty("modelLoaded", out var flag) && flag.ValueKind == JsonValueKind.True,
            data.TryGetProperty("modelName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "",
            data.TryGetProperty("modelID", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "");
    }

    public async Task CreateParameterAsync(string name, string explanation, double min, double max, double defaultValue,
        CancellationToken token = default)
    {
        if (!authenticated) throw VTSErrorException.Disconnected();
        _ = VTSProtocol.Payload(await RequestAsync(
            VTSProtocol.CreateParameter(NewID(), name, explanation, min, max, defaultValue), token: token),
            "ParameterCreationResponse");
    }

    public async Task<List<VTSModel>> AvailableModelsAsync(CancellationToken token = default)
    {
        var data = VTSProtocol.Payload(await RequestAsync(VTSProtocol.AvailableModels(NewID()), token: token), "AvailableModelsResponse");
        return VTSProtocol.ParseModels(data);
    }

    /// <summary>Loads a model by id. Hotkeys belong to the model, so the
    /// whitelist is cleared until HotkeysAsync is called again.</summary>
    public async Task LoadModelAsync(string modelID, CancellationToken token = default)
    {
        if (!authenticated) throw VTSErrorException.Disconnected();
        _ = VTSProtocol.Payload(await RequestAsync(VTSProtocol.LoadModel(NewID(), modelID), timeoutSeconds: 20, token: token), "ModelLoadResponse");
        lock (knownHotkeys) knownHotkeys.Clear();
    }

    /// <summary>Refreshes the whitelist of hotkeys that TriggerAsync may fire.</summary>
    public async Task<List<VTSHotkey>> HotkeysAsync(CancellationToken token = default)
    {
        var data = VTSProtocol.Payload(await RequestAsync(VTSProtocol.Hotkeys(NewID()), token: token), "HotkeysInCurrentModelResponse");
        var list = VTSProtocol.ParseHotkeys(data);
        lock (knownHotkeys)
        {
            knownHotkeys.Clear();
            foreach (var hotkey in list) knownHotkeys.TryAdd(hotkey.Id, hotkey);
        }
        return list;
    }

    public VTSHotkey? Hotkey(string id)
    {
        lock (knownHotkeys) return knownHotkeys.TryGetValue(id, out var hotkey) ? hotkey : null;
    }

    public async Task TriggerAsync(string hotkeyID, CancellationToken token = default)
    {
        if (!authenticated) throw VTSErrorException.Disconnected();
        if (Hotkey(hotkeyID) is null) throw VTSErrorException.UnknownHotkey(hotkeyID);
        _ = VTSProtocol.Payload(await RequestAsync(VTSProtocol.Trigger(NewID(), hotkeyID), token: token), "HotkeyTriggerResponse");
    }

    /// <summary>Fire-and-forget at animation rate; responses are ignored.</summary>
    public async void Inject((string, double)[] parameters)
    {
        var link = transport;
        if (!authenticated || link is null) return;
        try
        {
            var text = VTSProtocol.Encode(VTSProtocol.Inject(NewID(), parameters));
            lock (sending) { /* one writer at a time so frames stay ordered */ }
            await link.SendAsync(text);
            injectedFrames += 1;
        }
        catch (Exception error)
        {
            lastUnmatchedError = "送出參數失敗：" + error.Message;
        }
    }

    async Task<bool> AuthenticateAsync(string token, string plugin, CancellationToken cancellation)
    {
        var reply = await RequestAsync(VTSProtocol.Authenticate(NewID(), token, plugin), token: cancellation);
        var data = VTSProtocol.Payload(reply, "AuthenticationResponse");
        authenticated = data.TryGetProperty("authenticated", out var flag) && flag.ValueKind == JsonValueKind.True;
        return authenticated;
    }

    async Task<VTSEnvelope> RequestAsync(VTSEnvelope envelope, int timeoutSeconds = 5, CancellationToken token = default)
    {
        var link = transport ?? throw VTSErrorException.Disconnected();
        var id = envelope.RequestID;
        var waiter = new TaskCompletionSource<VTSEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiter;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await link.SendAsync(VTSProtocol.Encode(envelope), timeout.Token);
            var finished = await Task.WhenAny(waiter.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
            if (finished != waiter.Task)
            {
                pending.TryRemove(id, out _);
                throw token.IsCancellationRequested && !timeout.IsCancellationRequested
                    ? new OperationCanceledException(token)
                    : VTSErrorException.TimedOut(envelope.MessageType);
            }
            return await waiter.Task;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    void Deliver(VTSEnvelope envelope)
    {
        if (!pending.ContainsKey(envelope.RequestID) && envelope.MessageType == "APIError")
        {
            var code = envelope.Data is { } error && error.TryGetProperty("errorID", out var id) && id.ValueKind == JsonValueKind.Number
                ? id.GetInt32() : -1;
            var message = envelope.Data is { } body && body.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? "" : "";
            lastUnmatchedError = $"VTube Studio 錯誤 {code}：{message}";
        }
        if (pending.TryRemove(envelope.RequestID, out var waiter)) waiter.TrySetResult(envelope);
    }

    void ConnectionWasLost(IVTSTransport link)
    {
        if (!ReferenceEquals(transport, link)) return;
        var wasUp = authenticated;
        transport = null;
        authenticated = false;
        lock (knownHotkeys) knownHotkeys.Clear();
        FailPending(VTSErrorException.Disconnected());
        receiver?.Cancel();
        receiver = null;
        if (wasUp) ConnectionLost?.Invoke();
    }

    void FailPending(Exception error)
    {
        foreach (var pair in pending)
        {
            if (pending.TryRemove(pair.Key, out var waiter)) waiter.TrySetException(error);
        }
    }

    static string NewID() => Guid.NewGuid().ToString("N")[..16];
}
