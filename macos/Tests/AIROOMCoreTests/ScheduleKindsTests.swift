import XCTest
@testable import AIROOMCore

final class ScheduleKindsTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    func testOldDailySchedulesStillDecode() throws {
        let old = #"{"id":"\#(UUID().uuidString)","name":"n","action":"reminder","text":"t","hour":9,"minute":30,"weekdays":[],"enabled":true}"#
        let item = try JSONDecoder().decode(ScheduledActivity.self, from: Data(old.utf8))
        XCTAssertEqual(item.kind, .daily)
        XCTAssertEqual(item.intervalMinutes, 60)
        XCTAssertNil(item.onceAt)
    }

    func testOnceFiresInItsWindowThenSwitchesOff() {
        var item = ScheduledActivity(name: "n", action: .reminder, text: "t", hour: 0, minute: 0, kind: .once, onceAt: now)
        XCTAssertEqual(LocalAutomation.evaluate(item, now: now.addingTimeInterval(-1)), .notYet)
        XCTAssertEqual(LocalAutomation.evaluate(item, now: now.addingTimeInterval(30)), .fire)
        XCTAssertEqual(LocalAutomation.evaluate(item, now: now.addingTimeInterval(LocalAutomation.grace + 1)), .missed,
                       "a time passed while the app was closed is skipped, not replayed")
        LocalAutomation.advance(&item, now: now)
        XCTAssertFalse(item.enabled)
        XCTAssertEqual(LocalAutomation.evaluate(item, now: now), .notYet)
    }

    func testIntervalMovesPastNowWithoutCatchingUp() {
        var item = ScheduledActivity(name: "n", action: .reminder, text: "t", hour: 0, minute: 0,
                                     kind: .interval, intervalMinutes: 30, nextAt: now)
        XCTAssertEqual(LocalAutomation.evaluate(item, now: now), .fire)
        let later = now.addingTimeInterval(3 * 3_600 + 60)
        XCTAssertEqual(LocalAutomation.evaluate(item, now: later), .missed)
        LocalAutomation.advance(&item, now: later)
        XCTAssertEqual(item.nextAt, now.addingTimeInterval(7 * 1_800), "skips every missed run, one step after now")
        XCTAssertEqual(ScheduledActivity(name: "n", action: .reminder, text: "t", hour: 0, minute: 0, kind: .interval, intervalMinutes: 1).intervalMinutes,
                       ScheduledActivity.intervalRange.lowerBound)
    }

    func testDailyNextSkipsATimeAlreadyFiredToday() {
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(identifier: "UTC")!
        let parts = calendar.dateComponents([.hour, .minute], from: now)
        var item = ScheduledActivity(name: "n", action: .reminder, text: "t", hour: parts.hour!, minute: parts.minute!)
        XCTAssertEqual(LocalAutomation.evaluate(item, now: now, calendar: calendar), .fire)
        LocalAutomation.advance(&item, now: now, calendar: calendar)
        XCTAssertEqual(LocalAutomation.evaluate(item, now: now, calendar: calendar), .notYet)
        let next = LocalAutomation.next(item, now: now, calendar: calendar)
        XCTAssertEqual(next.map { calendar.dateComponents([.day], from: now, to: $0).day }, 1)
    }

    func testMeetingRoomCards() {
        let round = FlowRound(key: "r1", title: "第 1 輪", goal: "g")
        XCTAssertTrue(ProjectFlowEngine.startCard(version: 2, level: .deep, round: round, note: "小心").contains("第 2 版，深入複核"))
        XCTAssertTrue(ProjectFlowEngine.acceptanceCard(round, accepted: false, opinion: "再改").hasSuffix("駁回：再改"))
        var task = FlowTask(key: "t", roundKey: "r1", title: "T", member: .claude, goal: "")
        let finding = FlowFinding(id: "f1", severity: "blocking", location: "a", request: "加一行")
        task.rulings = [FlowRuling(finding: finding, reason: "r", verdict: .review)]
        XCTAssertTrue(ProjectFlowEngine.rulingCard(task, rulings: task.rulings).contains("維持複核：加一行"))
        XCTAssertTrue(ProjectFlowEngine.completionCard(String(repeating: "字", count: 3_000)).hasSuffix("……"))
    }
}
