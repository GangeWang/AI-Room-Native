import Foundation

public enum AgentKind: String, Codable, CaseIterable, Identifiable, Hashable, Sendable {
    case claude = "Claude Code"
    case codex = "Codex"

    public var id: String { rawValue }
    public var executableName: String { self == .claude ? "claude" : "codex" }
    public var symbol: String { self == .claude ? "brain.head.profile" : "chevron.left.forwardslash.chevron.right" }
}

public enum MessageRole: String, Codable, Sendable { case user, assistant }

public struct ChatMessage: Identifiable, Codable, Equatable, Sendable {
    public var id: UUID
    public var role: MessageRole
    public var body: String
    public var createdAt: Date

    public init(id: UUID = UUID(), role: MessageRole, body: String, createdAt: Date = .now) {
        self.id = id
        self.role = role
        self.body = body
        self.createdAt = createdAt
    }
}

public struct Conversation: Identifiable, Codable, Equatable, Sendable {
    public var id: UUID
    public var agent: AgentKind
    public var title: String
    public var messages: [ChatMessage]
    public var updatedAt: Date

    public init(id: UUID = UUID(), agent: AgentKind, title: String = "新對話", messages: [ChatMessage] = [], updatedAt: Date = .now) {
        self.id = id
        self.agent = agent
        self.title = title
        self.messages = messages
        self.updatedAt = updatedAt
    }
}

public struct RoundtableResult: Identifiable, Codable, Equatable, Sendable {
    public var id: UUID
    public var agent: AgentKind
    public var text: String
    public var error: String?

    public init(id: UUID = UUID(), agent: AgentKind, text: String = "", error: String? = nil) {
        self.id = id
        self.agent = agent
        self.text = text
        self.error = error
    }
}

public enum PromptBuilder {
    /// MVP uses fresh read-only CLI executions, so explicit prior turns preserve conversational context.
    /// This is not a native CLI session resume and can consume more tokens.
    public static func build(history: [ChatMessage], newQuestion: String, maxTurns: Int = 8, language: InterfaceLanguage = .zhTW) -> String {
        let tail = Array(history.suffix(max(0, maxTurns * 2)))
        var lines = language == .english
            ? ["You are the selected AI assistant. Here is recent conversation history. Reply in English unless the user asks otherwise.", "This is a read-only conversation. Do not modify files or execute changes."]
            : ["你是使用者選擇的 AI 助手。以下是本工作台保存的近期對話。請使用繁體中文回覆，除非使用者另有要求。", "本次是唯讀對話，不要修改檔案或執行變更。"]
        for message in tail where !message.body.isEmpty {
            let name = language == .english
                ? (message.role == .user ? "User" : "Assistant")
                : (message.role == .user ? "使用者" : "助理")
            lines.append("\(name): \(message.body)")
        }
        lines.append("\(language == .english ? "User" : "使用者")：\(newQuestion)")
        return lines.joined(separator: "\n\n")
    }
}
