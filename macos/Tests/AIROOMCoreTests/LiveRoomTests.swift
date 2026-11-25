import XCTest
@testable import AIROOMCore

final class LiveRoomTests: XCTestCase {
    func testLiveRoomIsNotALoungeTopic() {
        XCTAssertTrue(TeamRoom().isLounge)
        XCTAssertFalse(TeamRoom(title: "直播間", isLiveRoom: true).isLounge)
    }

    func testRoomsSavedBeforeTheLiveRoomDecodeAsLoungeTopics() throws {
        var json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(TeamRoom())) as? [String: Any])
        json.removeValue(forKey: "isLiveRoom")
        let old = try JSONDecoder().decode(TeamRoom.self, from: JSONSerialization.data(withJSONObject: json))
        XCTAssertFalse(old.isLiveRoom)
        XCTAssertTrue(old.isLounge)
    }

    func testLiveRoomSurvivesSaving() throws {
        let room = TeamRoom(title: "直播間", participants: [.claude], isLiveRoom: true)
        let copy = try JSONDecoder().decode(TeamRoom.self, from: JSONEncoder().encode(room))
        XCTAssertTrue(copy.isLiveRoom)
        XCTAssertEqual(copy, room)
    }

    func testBackupCountsLeaveTheLiveRoomOut() {
        var state = SavedState()
        let lounges = state.rooms.filter(\.isLounge).count
        state.rooms.append(TeamRoom(title: "直播間", entries: [RoomEntry(speaker: .system, displayName: "AIROOM LIVE", body: "x")],
                                    isLiveRoom: true))
        XCTAssertEqual(state.rooms.filter(\.isLounge).count, lounges)
    }
}
