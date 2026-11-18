import XCTest
@testable import AIROOMCore

final class LivePoseTests: XCTestCase {
    func testPoseTagsSplitSegmentsAndAreNeverSpoken() {
        let reply = LiveCueParser.parse("[happy] 嗨！[pose head=-15,5,10 gaze=left in=0.4] 你看那邊，[pose smile=0.8] 騙你的。")
        XCTAssertEqual(reply.cleanText, "嗨！ 你看那邊， 騙你的。")
        XCTAssertEqual(reply.segments.map(\.text), ["嗨！", "你看那邊，", "騙你的。"])
        XCTAssertNil(reply.segments[0].pose)
        XCTAssertEqual(reply.segments[1].pose, LivePose(headX: -15, headY: 5, headZ: 10, gazeX: -0.8, gazeY: 0, duration: 0.4))
        XCTAssertEqual(reply.segments[2].pose?.smile, 0.8)
        XCTAssertEqual(reply.segments[2].emotion, .happy, "a pose keeps the current emotion")
        let events = LiveCueParser.events(from: reply, speaker: .claude)
        XCTAssertEqual(events[1].pose?.headX, -15)
    }

    func testPoseValuesAreClampedAndJunkDropped() {
        let pose = LivePose(arguments: " head=999,-999,5 gaze=3,-3 eyes=2 brows=-1 smile=nan in=60s volume=11 rm=-rf")
        XCTAssertEqual(pose?.headX, 30); XCTAssertEqual(pose?.headY, -20); XCTAssertEqual(pose?.headZ, 5)
        XCTAssertEqual(pose?.gazeX, 1); XCTAssertEqual(pose?.gazeY, -1)
        XCTAssertEqual(pose?.eyes, 1); XCTAssertEqual(pose?.brows, 0)
        XCTAssertNil(pose?.smile)
        XCTAssertEqual(pose?.duration, 4)
        XCTAssertNil(LivePose(arguments: " volume=11 shell=ls"))
        // An unusable pose tag is still removed from speech.
        XCTAssertEqual(LiveCueParser.parse("[pose nonsense] 你好").cleanText, "你好")
    }

    func testAnimatorEasesIntoPoseKeepingUnsetValues() {
        var rig = LivePoseAnimator(idle: false)
        rig.apply(LivePose(headX: 20, smile: 1, duration: 1), at: 10)
        func value(_ name: String, _ t: TimeInterval) -> Double { rig.frame(at: t).first { $0.0 == name }!.1 }
        XCTAssertEqual(value("FaceAngleX", 10), 0, accuracy: 1e-9)
        XCTAssertEqual(value("FaceAngleX", 10.5), 10, accuracy: 1e-9)
        XCTAssertEqual(value("FaceAngleX", 12), 20, accuracy: 1e-9)
        XCTAssertEqual(value("MouthSmile", 12), 1, accuracy: 1e-9)
        XCTAssertEqual(value("Brows", 12), 0.5, accuracy: 1e-9, "unset keys stay")
        rig.apply(LivePose(headY: -10, duration: 0.5), at: 12)
        XCTAssertEqual(value("FaceAngleX", 13), 20, accuracy: 1e-9, "a later pose keeps earlier values")
        XCTAssertEqual(value("FaceAngleY", 13), -10, accuracy: 1e-9)
        rig.relax(at: 13, over: 1)
        XCTAssertEqual(value("FaceAngleX", 14), 0, accuracy: 1e-9)
        rig.mouth = 3
        XCTAssertEqual(value("MouthOpen", 14), 1)
    }

    func testIdleBlinksAndStaysInRange() {
        let rig = LivePoseAnimator()
        var sawBlink = false
        for step in 0..<600 {
            let frame = Dictionary(uniqueKeysWithValues: rig.frame(at: Double(step) / 30))
            if frame["EyeOpenLeft"]! < 0.3 { sawBlink = true }
            XCTAssertLessThanOrEqual(abs(frame["FaceAngleX"]!), 30)
            XCTAssertLessThanOrEqual(abs(frame["FaceAngleY"]!), 20)
        }
        XCTAssertTrue(sawBlink, "blinks within 20 s")
    }

    func testEveryEmotionHasAPreset() {
        for emotion in LiveEmotion.allCases { XCTAssertFalse(LivePose.preset(for: emotion).isEmpty) }
    }

    func testPromptTeachesPosesOnlyWhenTheRigIsDriven() {
        let room = TeamRoom(entries: [])
        let plain = LiveShowPolicy.prompt(room: room, speaker: .claude, topic: "t", summary: "", language: .zhTW, cues: true)
        let rig = LiveShowPolicy.prompt(room: room, speaker: .claude, topic: "t", summary: "", language: .zhTW, cues: true, poses: true)
        XCTAssertFalse(plain.contains("[pose"))
        XCTAssertTrue(rig.contains("[pose head=X,Y,Z"))
        XCTAssertFalse(LiveShowPolicy.prompt(room: room, speaker: .claude, topic: "t", summary: "", language: .zhTW,
                                             cues: false, poses: true).contains("[pose"))
    }
}

final class LiveArmTests: XCTestCase {
    func testArmKeysParseAndClamp() {
        let pose = LivePose(arguments: " arms=0.8,-3 elbows=1 elbowR=-0.5")
        XCTAssertEqual(pose?.armL, 0.8); XCTAssertEqual(pose?.armR, -1)
        XCTAssertEqual(pose?.elbowL, 1); XCTAssertEqual(pose?.elbowR, -0.5)
        XCTAssertNotNil(LivePose(arguments: " armL=0.2"), "arms alone make a pose")
    }

    func testEachMemberGetsValidUniqueArmInputs() {
        let claude = LivePoseAnimator.ArmInputs(member: .claude)
        XCTAssertEqual(claude.all, ["AIROOMclaudeArmL", "AIROOMclaudeArmR", "AIROOMclaudeElbowL", "AIROOMclaudeElbowR"])
        let added = LivePoseAnimator.ArmInputs(member: MemberID(rawValue: "m-ab12cd"))
        XCTAssertTrue(Set(claude.all).isDisjoint(with: added.all))
        for name in claude.all + added.all {
            XCTAssertTrue((4...32).contains(name.count) && name.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber) }, name)
        }
    }

    func testArmsAreStreamedOnlyWithInputs() {
        var rig = LivePoseAnimator(idle: false)
        rig.apply(LivePose(armL: 0.6, elbowR: -1, duration: 0.5), at: 0)
        let inputs = LivePoseAnimator.ArmInputs(member: .claude)
        let frame = Dictionary(uniqueKeysWithValues: rig.frame(at: 1, arms: inputs))
        XCTAssertEqual(frame[inputs.armL]!, 0.6, accuracy: 1e-9)
        XCTAssertEqual(frame[inputs.elbowR]!, -1, accuracy: 1e-9)
        XCTAssertNil(Dictionary(uniqueKeysWithValues: rig.frame(at: 1))[inputs.armL])
        let sent = VTSProtocol.inject(id: "1", parameters: [(inputs.armL, -5)])
        XCTAssertEqual(sent.data?["parameterValues"]?.array?.first?["value"]?.number, -1)
    }
}
