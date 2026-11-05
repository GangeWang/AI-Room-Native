import Foundation

/// Convert each vendor's official JSONL output into displayable final text.
/// Do not show tool calls or raw JSON events as if they were assistant answers.
public struct CLIEventAccumulator {
    public let agent: Executor
    public private(set) var text = ""
    public private(set) var failure: String?
    /// Model reported by the CLI itself (Claude's `system/init` event).
    public private(set) var model: String?
    /// The session this run belongs to (Claude --resume, DSH --session-id).
    public private(set) var sessionID: String?
    /// The run ended in error without a single model turn (e.g. a missing session).
    public private(set) var endedBeforeModel = false
    private var claudeDeltas = ""
    private var streamed = ""

    public init(agent: Executor) { self.agent = agent }

    /// agy stream-json: one init, step_update events, then one result. Text between
    /// tool steps streams as it comes; the result's response is the final answer.
    private mutating func consumeAntigravity(_ value: [String: Any]) -> String? {
        switch value["event"] as? String {
        case "init":
            if let id = value["conversation_id"] as? String, !id.isEmpty { sessionID = id }
        case "step_update":
            guard let step = value["step_update"] as? [String: Any], step["step_type"] as? String == "agent_response",
                  let part = step["text_delta"] as? String, !part.isEmpty else { return nil }
            streamed += part
            text = streamed
            return text
        case "result":
            guard let result = value["result"] as? [String: Any] else { return nil }
            if let id = result["conversation_id"] as? String, !id.isEmpty { sessionID = id }
            guard result["status"] as? String == "SUCCESS" else {
                failure = (result["error"] as? String) ?? "Antigravity 沒有完成這一輪（\(result["status"] as? String ?? "未知狀態")）"
                return nil
            }
            if let final = result["response"] as? String, !final.isEmpty { text = final; return text }
        default: break
        }
        return nil
    }

    @discardableResult public mutating func consume(line: String) -> String? {
        guard let data = line.data(using: .utf8),
              let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        if agent == .antigravity { return consumeAntigravity(value) }
        guard let type = value["type"] as? String else { return nil }

        switch agent {
        case .antigravity:
            break // handled above: its events are keyed "event", not "type"
        case .deepseek:
            // dsh headless --json: session, status, thinking, text, tool_call, tool_result, final.
            switch type {
            case "session":
                if let id = value["sessionId"] as? String, !id.isEmpty { sessionID = id }
            case "text":
                if let part = value["text"] as? String, !part.isEmpty {
                    streamed += part
                    text = streamed
                    return text
                }
            case "final":
                if let final = value["text"] as? String, !final.isEmpty { text = final; return text }
            case "error":
                failure = (value["message"] as? String) ?? "DeepSeek Harness 執行失敗"
            case "status":
                if value["phase"] as? String == "turn_end",
                   let reason = value["reason"] as? [String: Any], let kind = reason["kind"] as? String, kind != "completed" {
                    let message = (reason["error"] as? [String: Any])?["message"] as? String
                    failure = message ?? "DeepSeek Harness 沒有完成這一輪（\(kind)）"
                }
            default: break
            }
        case .codex:
            if ["item.completed", "item.updated"].contains(type),
               let item = value["item"] as? [String: Any],
               item["type"] as? String == "agent_message",
               let content = item["text"] as? String {
                text = content
                return text
            }
            if type == "error" || type == "turn.failed" {
                let source = (value["error"] as? [String: Any])?["message"] as? String
                failure = source ?? (value["message"] as? String) ?? "Codex 執行失敗"
            }
        case .claude:
            if let session = value["session_id"] as? String, !session.isEmpty { sessionID = session }
            if type == "system", value["subtype"] as? String == "init", let name = value["model"] as? String {
                model = name
                return nil
            }
            if type == "stream_event", let event = value["event"] as? [String: Any],
               event["type"] as? String == "content_block_delta",
               let delta = event["delta"] as? [String: Any],
               delta["type"] as? String == "text_delta",
               let part = delta["text"] as? String {
                claudeDeltas += part
                text = claudeDeltas
                return text
            }
            if type == "assistant", let message = value["message"] as? [String: Any],
               let blocks = message["content"] as? [[String: Any]] {
                let completed = blocks.compactMap { block -> String? in
                    guard block["type"] as? String == "text" else { return nil }
                    return block["text"] as? String
                }.joined(separator: "\n")
                if !completed.isEmpty { text = completed; return text }
            }
            if type == "result" {
                // An error result carries its reason (e.g. a usage limit) in
                // `result`; it must never be mistaken for an answer.
                if value["is_error"] as? Bool == true {
                    endedBeforeModel = (value["num_turns"] as? Int) == 0 && (value["duration_api_ms"] as? Int) == 0
                    let reason = (value["result"] as? String).flatMap { $0.isEmpty ? nil : $0 }
                    failure = reason ?? (value["subtype"] as? String).map { "Claude 執行失敗：\($0)" } ?? "Claude 執行失敗"
                    return nil
                }
                if let result = value["result"] as? String, !result.isEmpty {
                    text = result
                    return text
                }
            }
        }
        return nil
    }
}
