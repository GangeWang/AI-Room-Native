import Foundation

// MARK: Call usage — counted receipts, as upstream AIROOM shows them:
// "累計嘗試 N 次 · 已發出 N 次 · 已完成 N 次", plus not-sent and unknown results.

public enum CallEvent: Sendable { case attempt, delivered, completed, notSent, unknown }

public struct CallUsage: Codable, Equatable, Sendable {
    public var total = 0
    public var delivered = 0
    public var completed = 0
    public var notSent = 0
    public var unknown = 0
    public init() {}

    public mutating func record(_ event: CallEvent) {
        switch event {
        case .attempt: total += 1
        case .delivered: delivered += 1
        case .completed: completed += 1
        case .notSent: notSent += 1
        case .unknown: unknown += 1
        }
    }

    public static func + (a: CallUsage, b: CallUsage) -> CallUsage {
        var sum = CallUsage()
        sum.total = a.total + b.total; sum.delivered = a.delivered + b.delivered
        sum.completed = a.completed + b.completed; sum.notSent = a.notSent + b.notSent
        sum.unknown = a.unknown + b.unknown
        return sum
    }

    /// AIROOM's own request count — not the subscription bill.
    public func label(_ language: InterfaceLanguage) -> String {
        var text = language == .english
            ? "Attempted \(total) · Sent \(delivered) · Completed \(completed)"
            : "累計嘗試 \(total) 次 · 已發出 \(delivered) 次 · 已完成 \(completed) 次"
        if notSent > 0 { text += language == .english ? " · Not sent \(notSent)" : " · 未發出失敗 \(notSent) 次" }
        if unknown > 0 { text += language == .english ? " · Unknown \(unknown)" : " · 結果未知 \(unknown) 次" }
        return text
    }
}

/// Per member, per day; old days are pruned so the state file stays small.
public struct CallLedger: Codable, Equatable, Sendable {
    public var days: [String: [String: CallUsage]] = [:] // agent → day → usage
    public init() {}

    public mutating func record(_ event: CallEvent, agent: AgentKind, day: String, keepDays: Int = 60) {
        days[agent.rawValue, default: [:]][day, default: CallUsage()].record(event)
        if let byDay = days[agent.rawValue], byDay.count > keepDays {
            for key in byDay.keys.sorted().prefix(byDay.count - keepDays) { days[agent.rawValue]?[key] = nil }
        }
    }

    public func usage(_ agent: AgentKind, day: String? = nil) -> CallUsage {
        let byDay = days[agent.rawValue] ?? [:]
        if let day { return byDay[day] ?? CallUsage() }
        return byDay.values.reduce(CallUsage(), +)
    }
}

// MARK: Needs-attention list (navigation only; opening an item never answers,
// resends, merges or accepts anything).

public enum AttentionKind: String, Sendable { case question, decision, unknown, login, missing, acceptance }

public struct AttentionItem: Identifiable, Equatable, Sendable {
    public enum Target: Equatable, Sendable { case room(UUID), project(UUID), settings, member(AgentKind) }
    public var id: String
    public var kind: AttentionKind
    public var title: String
    public var detail: String
    public var target: Target
}

public enum AttentionBuilder {
    public static func items(state: SavedState, cli: [AgentKind: OfficialCLIStatus]) -> [AttentionItem] {
        var items: [AttentionItem] = []
        let name = { (agent: AgentKind) in state.members.first(where: { $0.agent == agent })?.name ?? agent.rawValue }
        for agent in AgentKind.allCases where state.members.contains(where: { $0.agent == agent && $0.enabled }) {
            switch cli[agent] {
            case .notLoggedIn:
                items.append(AttentionItem(id: "login-\(agent.rawValue)", kind: .login, title: "\(name(agent)) 需要登入",
                    detail: "官方 CLI 尚未登入，成員暫時無法回覆。", target: .settings))
            case .missingExecutable:
                items.append(AttentionItem(id: "missing-\(agent.rawValue)", kind: .missing, title: "\(name(agent)) 缺少程式",
                    detail: "找不到官方 CLI，請在設定指定路徑或安裝。", target: .settings))
            default: break
            }
        }
        for room in state.rooms {
            for question in room.lounge.pendingQuestions {
                items.append(AttentionItem(id: "q-\(question.id)", kind: .question, title: "\(name(question.from)) 在「\(room.title)」問你",
                    detail: String(question.text.prefix(140)), target: .room(room.id)))
            }
            if room.lounge.phase == .awaitingHost {
                items.append(AttentionItem(id: "pick-\(room.id)", kind: .decision, title: "「\(room.title)」有人舉手",
                    detail: "請點名誰補充，或讓大家等你說話。", target: .room(room.id)))
            }
            // Only the latest member reply counts: an older failure was already seen.
            if let last = room.entries.last(where: { $0.speaker == .assistant }),
               last.delivery == .failed {
                items.append(AttentionItem(id: "unknown-\(last.id)", kind: .unknown, title: "「\(room.title)」有結果待核對",
                    detail: "\(last.displayName) 的回覆沒有確認完成，沒有自動重送。", target: .room(room.id)))
            }
        }
        for project in state.projects where project.phase == .reviewing || project.phase == .failed {
            items.append(AttentionItem(id: "project-\(project.id)", kind: project.phase == .reviewing ? .acceptance : .unknown,
                title: project.title, detail: project.phase == .reviewing ? "變更待驗收" : "執行失敗，請檢查結果",
                target: .project(project.id)))
        }
        return items
    }
}

// MARK: Signing in through the official CLI, in the user's own Terminal.

public enum LoginScript {
    /// A `.command` file Terminal runs. It contains only the CLI's own login
    /// command; AIROOM never sees or stores what the user types.
    public static func contents(agent: AgentKind, executable: URL) -> String {
        let quoted = "'" + executable.path.replacingOccurrences(of: "'", with: "'\\''") + "'"
        let command = agent == .claude ? "\(quoted) auth login" : "\(quoted) login"
        return """
        #!/bin/zsh
        echo "AIROOM：用官方程式登入 \(agent.rawValue)"
        echo
        \(command)
        echo
        echo "完成後可以關閉這個視窗，回到 AIROOM 按「重新檢查」。"
        """
    }
}
