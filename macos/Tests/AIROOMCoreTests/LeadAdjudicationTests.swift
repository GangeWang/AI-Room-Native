import XCTest
@testable import AIROOMCore

/// With three or more members the lead rules on review disputes, as upstream does.
final class LeadAdjudicationTests: XCTestCase {
    let ds = MemberID(rawValue: "m-ds0001")
    var three: [MemberID] { [.claude, .codex, ds] }

    func disputedTask(author: MemberID, reviewer: MemberID) -> FlowTask {
        var task = FlowTask(key: "t", roundKey: "r", title: "登入頁", member: author, goal: "做登入頁", criteria: ["有錯誤提示"])
        task.reviewer = reviewer
        task.openFindings = [FlowFinding(id: "f1", severity: "blocking", location: "login.html", request: "加上記住我"),
                             FlowFinding(id: "f2", severity: "should-fix", location: "login.css", request: "按鈕改成藍色")]
        task.disputes = [FlowDispute(findingID: "f1", reason: "需求沒有記住我"), FlowDispute(findingID: "f2", reason: "設計稿是橘色")]
        task.summary = "改好錯誤提示"
        task.returns = 1
        task.status = .disputed
        return task
    }

    func testWithThreeMembersTheLeadIsNotTheFirstReviewer() {
        let byCodex = FlowTask(key: "a", roundKey: "r", title: "a", member: .codex, goal: "")
        XCTAssertEqual(ProjectFlowEngine.reviewer(for: byCodex, lead: .claude, members: three), ds,
                       "a non-lead reviews, so the lead can rule on disputes")
        let byLead = FlowTask(key: "b", roundKey: "r", title: "b", member: .claude, goal: "")
        XCTAssertEqual(ProjectFlowEngine.reviewer(for: byLead, lead: .claude, members: three), .codex)
    }

    func testReviewsAreSpreadAndKept() {
        var reviewed = FlowTask(key: "a", roundKey: "r", title: "a", member: .claude, goal: "")
        reviewed.reviewer = .codex
        let next = FlowTask(key: "b", roundKey: "r", title: "b", member: .claude, goal: "")
        XCTAssertEqual(ProjectFlowEngine.reviewer(for: next, lead: .claude, members: three, assigned: [reviewed]), ds,
                       "the member with fewer reviews goes first")
        XCTAssertEqual(ProjectFlowEngine.reviewer(for: reviewed, lead: .claude, members: three, assigned: [reviewed]), .codex,
                       "a task keeps its reviewer")
        XCTAssertEqual(ProjectFlowEngine.reviewer(for: reviewed, lead: .claude, members: three, assigned: [reviewed], unavailable: [.codex]), ds,
                       "a reviewer out of quota is replaced")
        let byCodex = FlowTask(key: "c", roundKey: "r", title: "c", member: .codex, goal: "")
        XCTAssertEqual(ProjectFlowEngine.reviewer(for: byCodex, lead: .claude, members: three, unavailable: [ds]), .claude,
                       "the lead reviews when no one else can")
    }

    func testTwoMembersStillReviewEachOther() {
        let task = FlowTask(key: "t", roundKey: "r", title: "x", member: .claude, goal: "")
        XCTAssertEqual(ProjectFlowEngine.reviewer(for: task, lead: .claude, members: [.claude, .codex]), .codex)
        XCTAssertEqual(ProjectFlowEngine.reviewer(for: task, lead: .codex, members: [.claude, .codex]), .codex)
    }

    func testTheLeadRulesOnlyWhenNeitherAuthorNorReviewer() {
        XCTAssertTrue(ProjectFlowEngine.leadCanRule(disputedTask(author: .codex, reviewer: ds), lead: .claude))
        XCTAssertFalse(ProjectFlowEngine.leadCanRule(disputedTask(author: .claude, reviewer: ds), lead: .claude))
        XCTAssertFalse(ProjectFlowEngine.leadCanRule(disputedTask(author: .codex, reviewer: .claude), lead: .claude))
        XCTAssertFalse(ProjectFlowEngine.leadCanRule(disputedTask(author: .codex, reviewer: ds), lead: nil))
    }

    func testThePromptCarriesEachDisputeAndAsksForEveryRuling() {
        let names: [MemberID: String] = [.claude: "Claude", .codex: "GPT", ds: "藍色大肥魚"]
        let prompt = ProjectFlowEngine.adjudicationPrompt(disputedTask(author: .codex, reviewer: ds), lead: .claude, names: names, ownerName: "老闆")
        XCTAssertTrue(prompt.contains("你是 Claude，AIROOM 專案的負責人"))
        XCTAssertTrue(prompt.contains("GPT 修正任務「登入頁」時，對 藍色大肥魚 的部分複核意見提出異議"))
        XCTAssertTrue(prompt.contains("[f1][blocking] login.html：加上記住我"))
        XCTAssertTrue(prompt.contains("作者的理由：設計稿是橘色"))
        XCTAssertTrue(prompt.contains("只讀查看，不要修改檔案"))
        XCTAssertTrue(prompt.contains("交給老闆決定"))
        XCTAssertTrue(prompt.contains(LoungeEngine.quoteRule))
    }

    func testRulingsParseOnlyWhenEveryDisputeIsAnswered() throws {
        let disputes = disputedTask(author: .codex, reviewer: ds).disputes
        let full = try XCTUnwrap(ProjectFlowEngine.parseAdjudication(
            #"好的。{"rulings":[{"finding":"f1","verdict":"author","reason":"需求沒提"},{"finding":"f2","verdict":"review","reason":"設計稿是藍色"},{"finding":"f9","verdict":"author","reason":"多的"}]}"#,
            disputes: disputes))
        XCTAssertEqual(full.map(\.verdict), [.author, .review])
        XCTAssertEqual(full[1].reason, "設計稿是藍色")
        XCTAssertNil(ProjectFlowEngine.parseAdjudication(#"{"rulings":[{"finding":"f1","verdict":"author","reason":"x"}]}"#, disputes: disputes),
                     "a missing ruling hands everything to the owner")
        XCTAssertNil(ProjectFlowEngine.parseAdjudication("我覺得作者對", disputes: disputes))
        let unsure = try XCTUnwrap(ProjectFlowEngine.parseAdjudication(
            #"{"rulings":[{"finding":"f1","verdict":"unsure","reason":"要老闆取捨"},{"finding":"f2","verdict":"uphold-review","reason":"y"}]}"#,
            disputes: disputes))
        XCTAssertNil(unsure[0].verdict)
        XCTAssertEqual(unsure[1].verdict, .review)
    }

    func testTheLeadsRulingIsRecordedWithItsGrounds() {
        var task = disputedTask(author: .codex, reviewer: ds)
        ProjectFlowEngine.applyRulings(&task, verdicts: ["f1": .author, "f2": .review], judge: .claude,
                                       notes: ["f1": "需求沒提", "f2": "設計稿是藍色"])
        XCTAssertEqual(task.status, .fixing)
        XCTAssertEqual(task.rulings.map(\.judge), [.claude, .claude])
        XCTAssertEqual(task.rulings.last?.note, "設計稿是藍色")
        XCTAssertEqual([task.returns, task.waivers], [0, 1])
        let fix = ProjectFlowEngine.fixSection(task, ownerName: "老闆")
        XCTAssertTrue(fix.contains("裁定維持複核"))
        XCTAssertTrue(fix.contains("裁定理由：設計稿是藍色"))
        let card = ProjectFlowEngine.rulingCard(task, rulings: task.rulings, names: [.claude: "Claude"])
        XCTAssertTrue(card.contains("（負責人 Claude 裁定）"))
        XCTAssertTrue(card.contains("理由：需求沒提"))
        XCTAssertFalse(ProjectFlowEngine.rulingCard(task, rulings: [FlowRuling(finding: task.openFindings[0], reason: "r", verdict: .review)]).contains("負責人"),
                       "the owner's ruling names no lead")
    }

    func testOldRulingsAndDisputesStillDecode() throws {
        let ruling = try JSONDecoder().decode(FlowRuling.self, from: Data(
            #"{"finding":{"id":"f1","severity":"blocking","location":"a","request":"x"},"reason":"r","verdict":"author","at":0}"#.utf8))
        XCTAssertNil(ruling.judge)
        XCTAssertNil(ruling.note)
        let dispute = try JSONDecoder().decode(FlowDispute.self, from: Data(#"{"findingID":"f1","reason":"r"}"#.utf8))
        XCTAssertNil(dispute.leadNote)
    }
}
