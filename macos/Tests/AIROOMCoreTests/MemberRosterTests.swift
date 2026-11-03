import XCTest
@testable import AIROOMCore

final class MemberRosterTests: XCTestCase {
    /// Written by build 0.2.49, before members and programs were separated.
    func testStateFromBeforeTheSplitStillReads() throws {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .appendingPathComponent("Fixtures/legacy-state-0.2.49.json")
        let state = try JSONDecoder().decode(SavedState.self, from: Data(contentsOf: url))
        XCTAssertEqual(state.members.map(\.agent), [.claude, .codex])
        XCTAssertEqual(state.members.map(\.executor), [.claude, .codex])
        XCTAssertEqual(state.members.map(\.template), ["claudia", "gptia"])
        XCTAssertEqual(state.members.map(\.name), ["Claudia", "GPTia"])
        XCTAssertEqual(state.conversations.first?.agent, .codex)
        XCTAssertEqual(state.rooms[0].participants, [.claude, .codex])
        XCTAssertEqual(state.rooms[0].entries.first?.agent, .codex)
        XCTAssertEqual(state.rooms[0].lounge.priority, [.codex])
        XCTAssertEqual(state.roundtableTopics.first?.lead, .codex)
        XCTAssertEqual(state.projectFlows.first?.lead, .codex)
        XCTAssertEqual(state.projectFlows.first?.plan?.tasks.first?.member, .claude)
        XCTAssertEqual(state.projectFlows.first?.quotaWaits.first?.agent, .claude)
        XCTAssertEqual(state.schedules.first?.target, .codex)
        XCTAssertEqual(state.dailyOpening.lastAgent, .codex)
        XCTAssertEqual(state.calls.usage(.claude, day: "2026-11-03").total, 1)
        XCTAssertEqual(Set(state.liveStage.characters.map(\.agent)), [.claude, .codex])
        XCTAssertTrue(state.formerMembers.isEmpty)
        // Re-encoding keeps the built-in ids as the same plain strings.
        let again = try JSONDecoder().decode(SavedState.self, from: JSONEncoder().encode(state))
        XCTAssertEqual(again, state)
    }

    func testAddedMemberHasItsOwnIdOnAnExistingProgram() throws {
        var state = SavedState()
        let deepseek = try XCTUnwrap(MemberTemplate.named("deepseek"))
        let card = MemberCard.added(from: deepseek, executor: .claude)
        state.addMember(card)
        XCTAssertEqual(state.members.count, 3)
        XCTAssertFalse(card.agent.isBuiltIn)
        XCTAssertTrue(card.agent.rawValue.hasPrefix("m-"))
        XCTAssertEqual(card.agent.handle, card.agent.rawValue)
        XCTAssertEqual(card.name, "DS")
        XCTAssertEqual(card.color, "#3c66d9")
        XCTAssertEqual(card.look?.art, "ds.webp")
        XCTAssertEqual(card.executor, .claude)
        XCTAssertTrue(state.conversations.contains { $0.agent == card.agent && !$0.archived })
        // Two members on Claude Code, each still themselves.
        XCTAssertEqual(state.members.filter { $0.executor == .claude }.map(\.agent), [.claude, card.agent])
        let decoded = try JSONDecoder().decode(SavedState.self, from: JSONEncoder().encode(state))
        XCTAssertEqual(decoded.members[2], state.members[2])
    }

    func testTemplatesCoverTheFourUpcomingPrograms() {
        let planned = MemberTemplate.all.compactMap(\.plannedProgram)
        XCTAssertEqual(planned, ["DeepSeek", "Gemini CLI", "Kimi CLI", "ZCode（GLM）"])
        for template in MemberTemplate.all {
            XCTAssertNotNil(template.color.range(of: #"^#[0-9a-f]{6}$"#, options: .regularExpression), template.id)
            XCTAssertLessThanOrEqual(template.motto.count, MemberCard.mottoLimit)
        }
        // Kimi and GLM have no artwork yet: the card shows an initial.
        XCTAssertNil(MemberTemplate.named("kimi")?.art)
        XCTAssertNil(MemberTemplate.named("glm")?.art)
        let blank = MemberCard.added(from: nil, executor: .codex)
        XCTAssertNil(blank.template)
        XCTAssertEqual(blank.executor, .codex)
    }

    func testRemovingAMemberKeepsTheirNameForHistory() throws {
        var state = SavedState()
        let card = MemberCard.added(from: MemberTemplate.named("gemini"), executor: .codex)
        state.addMember(card)
        state.rooms[0].participants.append(card.agent)
        state.rooms[0].sessions[card.agent.rawValue] = RoomSession(id: "s", through: UUID())
        state.schedules = [ScheduledActivity(name: "早安", action: .roomMessage, text: "早", hour: 9, minute: 0, target: card.agent)]
        state.liveStage.updateCharacter(card.agent) { $0.vtsPort = 8003 }
        XCTAssertEqual(state.liveStage.character(card.agent).vtsPort, 8003)

        try state.removeMember(card.agent)
        XCTAssertFalse(state.members.contains { $0.agent == card.agent })
        XCTAssertFalse(state.conversations.contains { $0.agent == card.agent })
        XCTAssertFalse(state.rooms[0].participants.contains(card.agent))
        XCTAssertNil(state.rooms[0].sessions[card.agent.rawValue])
        XCTAssertNil(state.schedules[0].target)
        XCTAssertFalse(state.liveStage.characters.contains { $0.agent == card.agent })
        XCTAssertEqual(state.memberName(card.agent), "Gemini")
        XCTAssertThrowsError(try state.removeMember(.claude)) { XCTAssertEqual($0 as? RosterError, .builtIn) }
    }

    func testAMemberInAnOpenProjectCannotBeRemoved() {
        var state = SavedState()
        let card = MemberCard.added(from: nil, executor: .claude)
        state.addMember(card)
        state.projectFlows = [ProjectFlow(title: "網站", folder: "/tmp/x", members: [.claude, card.agent])]
        XCTAssertThrowsError(try state.removeMember(card.agent)) { XCTAssertEqual($0 as? RosterError, .inProject("網站")) }
        state.projectFlows[0].step = .completed
        XCTAssertNoThrow(try state.removeMember(card.agent))
    }

    func testSwitchingProgramStartsTheConversationsAfresh() {
        var state = SavedState()
        state.conversations = [Conversation(agent: .claude), Conversation(agent: .codex)]
        state.conversations[0].sessionID = "claude-session"
        state.conversations[0].sessionNotes = ["undone"]
        state.conversations[1].sessionID = "codex-thread"
        state.rooms[0].sessions[MemberID.claude.rawValue] = RoomSession(id: "room", through: UUID())

        state.setExecutor(.codex, for: .claude)
        XCTAssertEqual(state.members[0].executor, .codex)
        XCTAssertNil(state.conversations[0].sessionID)
        XCTAssertTrue(state.conversations[0].sessionNotes.isEmpty)
        XCTAssertNil(state.rooms[0].sessions[MemberID.claude.rawValue])
        XCTAssertEqual(state.conversations[1].sessionID, "codex-thread", "other members keep theirs")
    }

    func testPlansAndMentionsReachAddedMembers() throws {
        let added = MemberID(rawValue: "m-1a2b3c")
        let plan = #"""
        {"objective":"o","scope":"s","deliverables":["d"],"acceptance":["a"],
         "rounds":[{"key":"r1","title":"一","goal":"g"}],
         "tasks":[{"key":"t1","round":"r1","title":"甲","member":"claude","goal":"g"},
                  {"key":"t2","round":"r1","title":"乙","member":"m-1a2b3c","goal":"g"}]}
        """#
        let parsed = try ProjectFlowEngine.parsePlan(plan, members: [.claude, added])
        XCTAssertEqual(parsed.tasks.map(\.member), [.claude, added])

        var card = MemberCard(agent: added, executor: .claude, name: "DS")
        card.enabled = true
        let mentions = LoungeEngine.mentions(in: "@DS 和 @Claude 看一下", members: [MemberCard(agent: .claude, name: "Claudia"), card])
        XCTAssertEqual(mentions, [added, .claude])

        var lounge = LoungeState()
        let reply = LoungeReply(action: .speak, body: "交給 DS", emoji: nil, next: "DS", questions: [], unstructured: false)
        _ = LoungeEngine.apply(reply, from: .claude, present: [.claude, added], state: &lounge, entryID: UUID(), names: [added: "DS"])
        XCTAssertEqual(lounge.priority, [added])

        XCTAssertEqual(RoundtableDecision.parseVote("Vote: m-1a2b3c\n理由", members: [.claude, added]), added)
    }

    func testOnlyTheBuiltInMembersMapToTheirProgramByDefault() {
        XCTAssertEqual(MemberID.claude.builtInExecutor, .claude)
        XCTAssertEqual(MemberID.codex.builtInExecutor, .codex)
        XCTAssertNil(MemberID.make().builtInExecutor)
        XCTAssertEqual(LiveCharacterStage.defaultPort(.codex), 8001)
        XCTAssertEqual(LiveCharacterStage.defaultPort(.claude), 8002)
        XCTAssertEqual(LiveCharacterStage.defaultPort(MemberID(rawValue: "m-000000")), 8003)
    }

    func testCLIRunnerUsesEachMembersOwnProgram() {
        let runner = CLIProcessRunner()
        let added = MemberID(rawValue: "m-abcdef")
        XCTAssertEqual(runner.executor(for: .codex), .codex)
        XCTAssertEqual(runner.executor(for: added), .claude, "unregistered added members fall back to Claude Code")
        runner.setDefaults(added, executor: .codex, model: "", effort: "")
        runner.setDefaults(.claude, executor: .codex, model: "", effort: "")
        XCTAssertEqual(runner.executor(for: added), .codex)
        XCTAssertEqual(runner.executor(for: .claude), .codex)
        runner.setDefaults(added, model: "gpt-5", effort: "")
        XCTAssertEqual(runner.executor(for: added), .codex, "updating defaults keeps the program")
    }
}
