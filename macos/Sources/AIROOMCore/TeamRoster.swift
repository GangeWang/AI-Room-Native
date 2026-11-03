import Foundation

public enum RosterError: LocalizedError, Equatable {
    case builtIn
    case unknown
    case inProject(String)

    public var errorDescription: String? {
        switch self {
        case .builtIn: return "內建的兩位成員不能刪除，可以改用「隱藏人物卡」或關掉「啟用成員」。"
        case .unknown: return "找不到這位成員。"
        case .inProject(let title): return "這位成員還在專案「\(title)」裡，請先完成或改由別人負責。"
        }
    }
}

/// Adding, removing and re-seating members. Every member has their own id;
/// several may run on the same program.
public extension SavedState {
    /// Adds a member with an open desk topic.
    mutating func addMember(_ card: MemberCard) {
        guard !members.contains(where: { $0.agent == card.agent }) else { return }
        members.append(card.normalized())
        conversations.append(Conversation(agent: card.agent))
    }

    /// Removes an added member and their desk topics. Room and project history
    /// keep the lines they wrote, under the name they had.
    mutating func removeMember(_ agent: MemberID) throws {
        guard !agent.isBuiltIn else { throw RosterError.builtIn }
        guard let index = members.firstIndex(where: { $0.agent == agent }) else { throw RosterError.unknown }
        if let flow = projectFlows.first(where: { $0.step != .completed && $0.members.contains(agent) }) {
            throw RosterError.inProject(flow.title)
        }
        formerMembers[agent.rawValue] = members[index].name
        members.remove(at: index)
        conversations.removeAll { $0.agent == agent }
        for room in rooms.indices {
            rooms[room].participants.removeAll { $0 == agent }
            rooms[room].sessions[agent.rawValue] = nil
        }
        liveStage.characters.removeAll { $0.agent == agent }
        for schedule in schedules.indices where schedules[schedule].target == agent { schedules[schedule].target = nil }
    }

    /// Moves a member to another program. Their CLI conversations belonged to
    /// the old program, so each topic and room starts a new one next time
    /// (with the full context, as on a first message).
    mutating func setExecutor(_ executor: Executor, for agent: MemberID) {
        guard let index = members.firstIndex(where: { $0.agent == agent }), members[index].executor != executor else { return }
        members[index].executor = executor
        for topic in conversations.indices where conversations[topic].agent == agent {
            conversations[topic].sessionID = nil
            conversations[topic].sessionThrough = nil
            conversations[topic].sessionProfile = nil
            conversations[topic].sessionNotes = []
        }
        for room in rooms.indices { rooms[room].sessions[agent.rawValue] = nil }
    }

    /// A member's name, including members who were removed.
    func memberName(_ agent: MemberID) -> String {
        members.first(where: { $0.agent == agent })?.name ?? formerMembers[agent.rawValue] ?? agent.rawValue
    }
}
