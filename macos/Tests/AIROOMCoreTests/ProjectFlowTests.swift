import XCTest
@testable import AIROOMCore

final class ProjectFlowTests: XCTestCase {
    let both: [AgentKind] = [.claude, .codex]
    let plan = #"""
    ```json
    {"objective":"做開場白頁","scope":"只改 index.html","deliverables":["index.html"],"acceptance":["打開能看到開場白"],
     "rounds":[{"key":"r1","title":"草稿","goal":"寫出頁面"},{"key":"r2","title":"潤飾","goal":"調整樣式"},{"key":"r3","title":"空輪","goal":"x"}],
     "tasks":[{"key":"t1","round":"r1","title":"寫 HTML","member":"codex","goal":"建立頁面","criteria":["有標題"],"dependencies":[]},
              {"key":"t2","round":"r1","title":"寫文案","member":"Claude Code","goal":"台詞","dependencies":["t1","t9"]},
              {"key":"t3","round":"r2","title":"樣式","member":"claude","goal":"CSS","dependencies":["t2"]}]}
    ```
    """#

    func testParsesPlanAndDropsEmptyRoundsAndUnknownDependencies() throws {
        let parsed = try ProjectFlowEngine.parsePlan(plan, members: both)
        XCTAssertEqual(parsed.rounds.map(\.key), ["r1", "r2"])
        XCTAssertEqual(parsed.tasks.map(\.member), [.codex, .claude, .claude])
        XCTAssertEqual(parsed.tasks[1].dependencies, ["t1"], "t9 does not exist earlier")
        XCTAssertEqual(parsed.acceptance, ["打開能看到開場白"])
    }

    func testRejectsAbsentMembersAndUnreadablePlans() {
        XCTAssertThrowsError(try ProjectFlowEngine.parsePlan(plan, members: [.claude])) { error in
            XCTAssertEqual(error as? FlowPlanError, .unknownMember("codex"))
        }
        XCTAssertThrowsError(try ProjectFlowEngine.parsePlan("好的，我來規劃", members: both))
    }

    func testDependenciesGateTheNextTask() throws {
        var parsed = try ProjectFlowEngine.parsePlan(plan, members: both)
        let first = parsed.rounds[0]
        XCTAssertEqual(ProjectFlowEngine.nextTask(in: first, plan: parsed)?.key, "t1")
        parsed.tasks[0].status = .running
        XCTAssertNil(ProjectFlowEngine.nextTask(in: first, plan: parsed), "t2 waits for t1")
        parsed.tasks[0].status = .done
        XCTAssertEqual(ProjectFlowEngine.nextTask(in: first, plan: parsed)?.key, "t2")
    }

    func testReviewerIsAnotherMemberAndVerdictsParse() {
        let task = FlowTask(key: "t", roundKey: "r", title: "x", member: .claude, goal: "")
        XCTAssertEqual(ProjectFlowEngine.reviewer(for: task, lead: .claude, members: both), .codex)
        XCTAssertEqual(ProjectFlowEngine.reviewer(for: task, lead: .codex, members: both), .codex)
        let changes = ProjectFlowEngine.parseVerdict(#"{"verdict":"changes","findings":[{"severity":"blocking","location":"index.html","request":"補標題"}]}"#)
        XCTAssertFalse(changes.pass)
        XCTAssertTrue(changes.findings.contains("補標題"))
        XCTAssertTrue(ProjectFlowEngine.parseVerdict(#"{"verdict":"pass","summary":"ok"}"#).pass)
    }
}
