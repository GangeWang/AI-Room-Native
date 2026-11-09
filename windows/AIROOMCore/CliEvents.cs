using System.Text.Json;

namespace AIROOMCore;

/// <summary>
/// Convert each vendor's official JSONL output into displayable final text.
/// Do not show tool calls or raw JSON events as if they were assistant answers.
/// </summary>
public sealed class CliEventAccumulator
{
    readonly Executor agent;
    string claudeDeltas = "";
    string streamed = "";

    public CliEventAccumulator(Executor agent) => this.agent = agent;

    public string Text { get; private set; } = "";
    public string? Failure { get; private set; }

    /// <summary>Model reported by the CLI itself (Claude's `system/init` event).</summary>
    public string? Model { get; private set; }

    /// <summary>The session this run belongs to (Claude --resume, Codex thread).</summary>
    public string? SessionID { get; private set; }

    /// <summary>The run ended in error without a single model turn (e.g. a missing session).</summary>
    public bool EndedBeforeModel { get; private set; }

    /// <summary>Consumes one JSONL line; returns displayable text when it changed.</summary>
    public string? Consume(string line)
    {
        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(line);
            value = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object) return null;

        var type = value.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString() : null;

        // agy stream-json: one init, step_update events, then one result. Text
        // between tool steps streams as it comes; the result's response is the
        // final answer. Its events are keyed "event", not "type".
        if (agent == Executor.Antigravity)
        {
            var eventName = value.TryGetProperty("event", out var eventProp) && eventProp.ValueKind == JsonValueKind.String
                ? eventProp.GetString() : null;
            switch (eventName)
            {
                case "init":
                    if (value.TryGetProperty("conversation_id", out var conversation) && conversation.ValueKind == JsonValueKind.String
                        && conversation.GetString() is { Length: > 0 } conversationID)
                    {
                        SessionID = conversationID;
                    }
                    return null;
                case "step_update":
                    if (value.TryGetProperty("step_update", out var step) && step.ValueKind == JsonValueKind.Object
                        && step.TryGetProperty("step_type", out var stepType) && stepType.ValueKind == JsonValueKind.String
                        && stepType.GetString() == "agent_response"
                        && step.TryGetProperty("text_delta", out var textDelta) && textDelta.ValueKind == JsonValueKind.String
                        && textDelta.GetString() is { Length: > 0 } delta)
                    {
                        streamed += delta;
                        Text = streamed;
                        return Text;
                    }
                    return null;
                case "result":
                    if (!value.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return null;
                    if (result.TryGetProperty("conversation_id", out var resultConversation) && resultConversation.ValueKind == JsonValueKind.String
                        && resultConversation.GetString() is { Length: > 0 } resultID)
                    {
                        SessionID = resultID;
                    }
                    var status = result.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String
                        ? statusProp.GetString() : null;
                    if (status != "SUCCESS")
                    {
                        Failure = result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                            ? error.GetString() : $"Antigravity 沒有完成這一輪（{status ?? "未知狀態"}）";
                        return null;
                    }
                    if (result.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.String
                        && response.GetString() is { Length: > 0 } final)
                    {
                        Text = final;
                        return Text;
                    }
                    return null;
                default:
                    return null;
            }
        }

        switch (agent)
        {
            case Executor.Codex:
            {
                // `codex exec --json` opens with thread.started carrying the
                // thread id; keep it so the next run can `exec resume <id>`.
                if (type == "thread.started"
                    && value.TryGetProperty("thread_id", out var threadID)
                    && threadID.ValueKind == JsonValueKind.String
                    && threadID.GetString() is { Length: > 0 } thread)
                {
                    SessionID = thread;
                    return null;
                }
                if (type is "item.completed" or "item.updated"
                    && value.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var itemType) && itemType.ValueKind == JsonValueKind.String
                    && itemType.GetString() == "agent_message"
                    && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    Text = text.GetString() ?? "";
                    return Text;
                }
                if (type is "error" or "turn.failed")
                {
                    var source = value.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                        ? message.GetString() : null;
                    Failure = source ?? (value.TryGetProperty("message", out var fallback)
                        && fallback.ValueKind == JsonValueKind.String ? fallback.GetString() : null)
                        ?? "Codex 執行失敗";
                }
                return null;
            }
            case Executor.Claude:
            {
                if (value.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String
                    && session.GetString() is { Length: > 0 } sessionID)
                {
                    SessionID = sessionID;
                }
                if (type == "system"
                    && value.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String
                    && subtype.GetString() == "init"
                    && value.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                {
                    Model = model.GetString();
                    return null;
                }
                if (type == "stream_event"
                    && value.TryGetProperty("event", out var evt) && evt.ValueKind == JsonValueKind.Object
                    && evt.TryGetProperty("type", out var eventType) && eventType.ValueKind == JsonValueKind.String
                    && eventType.GetString() == "content_block_delta"
                    && evt.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("type", out var deltaType) && deltaType.ValueKind == JsonValueKind.String
                    && deltaType.GetString() == "text_delta"
                    && delta.TryGetProperty("text", out var deltaText) && deltaText.ValueKind == JsonValueKind.String)
                {
                    claudeDeltas += deltaText.GetString();
                    Text = claudeDeltas;
                    return Text;
                }
                if (type == "assistant"
                    && value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var completed = string.Join("\n", content.EnumerateArray()
                        .Where(block => block.ValueKind == JsonValueKind.Object
                                        && block.TryGetProperty("type", out var blockType)
                                        && blockType.ValueKind == JsonValueKind.String
                                        && blockType.GetString() == "text"
                                        && block.TryGetProperty("text", out var blockText)
                                        && blockText.ValueKind == JsonValueKind.String)
                        .Select(block => block.GetProperty("text").GetString() ?? ""));
                    if (completed.Length > 0)
                    {
                        Text = completed;
                        return Text;
                    }
                }
                if (type == "result")
                {
                    // An error result carries its reason (e.g. a usage limit) in
                    // `result`; it must never be mistaken for an answer.
                    if (value.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                    {
                        EndedBeforeModel = value.TryGetProperty("num_turns", out var turns) && turns.ValueKind == JsonValueKind.Number
                            && turns.GetInt32() == 0
                            && value.TryGetProperty("duration_api_ms", out var duration) && duration.ValueKind == JsonValueKind.Number
                            && duration.GetInt64() == 0;
                        var reason = value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
                            ? result.GetString() : null;
                        var resultSubtype = value.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String
                            ? st.GetString() : null;
                        Failure = !string.IsNullOrEmpty(reason) ? reason
                            : resultSubtype is not null ? $"Claude 執行失敗：{resultSubtype}"
                            : "Claude 執行失敗";
                        return null;
                    }
                    if (value.TryGetProperty("result", out var answer) && answer.ValueKind == JsonValueKind.String
                        && answer.GetString() is { Length: > 0 } final)
                    {
                        Text = final;
                        return Text;
                    }
                }
                return null;
            }
            default:
                return null;
        }
    }
}
