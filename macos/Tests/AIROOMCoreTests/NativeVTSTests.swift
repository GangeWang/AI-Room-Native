#if os(macOS)
import XCTest
@testable import AIROOMCore

/// Grants as VTube Studio keeps them: one token per plugin name, in a folder
/// every instance of the app shares. Issuing a token for a name revokes the
/// name's previous token, on every instance.
private final class SharedGrants: @unchecked Sendable {
    private let lock = NSLock()
    private var tokens: [String: String] = [:]
    private var issued = 0
    func issue(for plugin: String) -> String {
        lock.lock(); defer { lock.unlock() }
        issued += 1
        let token = "tok-\(issued)"
        tokens[plugin] = token
        return token
    }
    func accepts(_ token: String, for plugin: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return tokens[plugin] == token
    }
}

/// One VTS instance on its own port, answering from the shared grants.
private final class FakeInstance: VTSTransport, @unchecked Sendable {
    let grants: SharedGrants
    private let lock = NSLock()
    private var inbox: [String] = []
    private var waiters: [CheckedContinuation<String, Error>] = []
    private var closed = false
    private(set) var types: [String] = []
    var loadedModel = "akari"
    /// Expression file → on/off, as the loaded model has them.
    var expressionsOn: [String: Bool] = ["AIROOM_happy.exp3.json": false, "AIROOM_sad.exp3.json": true]

    init(grants: SharedGrants) { self.grants = grants }

    func send(_ text: String) async throws {
        let request = try VTSProtocol.decode(text)
        lock.lock(); types.append(request.messageType); lock.unlock()
        let plugin = request.data?["pluginName"]?.string ?? ""
        let data: [String: JSONValue]
        let type: String
        switch request.messageType {
        case "AuthenticationTokenRequest":
            type = "AuthenticationTokenResponse"; data = ["authenticationToken": .string(grants.issue(for: plugin))]
        case "AuthenticationRequest":
            let ok = grants.accepts(request.data?["authenticationToken"]?.string ?? "", for: plugin)
            type = "AuthenticationResponse"; data = ["authenticated": .bool(ok)]
        case "CurrentModelRequest":
            type = "CurrentModelResponse"
            data = ["modelLoaded": .bool(true), "modelName": .string(loadedModel), "modelID": .string("id-" + loadedModel)]
        case "AvailableModelsRequest":
            type = "AvailableModelsResponse"
            data = ["availableModels": .array(["akari", "hiyori"].map {
                .object(["modelName": .string($0), "modelID": .string("id-" + $0), "modelLoaded": .bool($0 == loadedModel)])
            })]
        case "ExpressionStateRequest":
            type = "ExpressionStateResponse"
            lock.lock(); let states = expressionsOn; lock.unlock()
            data = ["modelLoaded": .bool(true), "expressions": .array(states.keys.sorted().map { file in
                .object(["name": .string(String(file.dropLast(10))), "file": .string(file), "active": .bool(states[file]!)])
            })]
        case "ExpressionActivationRequest":
            let file = request.data?["expressionFile"]?.string ?? ""
            lock.lock(); let known = expressionsOn[file] != nil
            if known { expressionsOn[file] = request.data?["active"]?.bool ?? false }
            lock.unlock()
            if known { type = "ExpressionActivationResponse"; data = [:] }
            else { type = "APIError"; data = ["errorID": .number(400), "message": .string("no such expression")] }
        case "ModelLoadRequest":
            loadedModel = String((request.data?["modelID"]?.string ?? "").dropFirst(3))
            type = "ModelLoadResponse"; data = ["modelID": request.data?["modelID"] ?? .null]
        default:
            type = "APIError"; data = ["errorID": .number(1), "message": .string("nope")]
        }
        push(try VTSProtocol.encode(VTSEnvelope(requestID: request.requestID, messageType: type, data: .object(data))))
    }
    func receive() async throws -> String {
        try await withCheckedThrowingContinuation { continuation in
            lock.lock()
            if closed { lock.unlock(); continuation.resume(throwing: VTSError.disconnected) }
            else if inbox.isEmpty { waiters.append(continuation); lock.unlock() }
            else { let next = inbox.removeFirst(); lock.unlock(); continuation.resume(returning: next) }
        }
    }
    func close() {
        lock.lock(); closed = true; let pending = waiters; waiters = []; lock.unlock()
        pending.forEach { $0.resume(throwing: VTSError.disconnected) }
    }
    /// VTS quitting or crashing.
    func drop() { close() }
    private func push(_ text: String) {
        lock.lock()
        if waiters.isEmpty { inbox.append(text); lock.unlock() }
        else { let waiter = waiters.removeFirst(); lock.unlock(); waiter.resume(returning: text) }
    }
    var sentTypes: [String] { lock.lock(); defer { lock.unlock() }; return types }
}

final class NativeVTSTests: XCTestCase {
    func testEachMemberHasItsOwnValidPluginName() {
        let names = [MemberID.claude, .codex, MemberID(rawValue: "m-abc123")].map(VTSProtocol.pluginName(for:))
        XCTAssertEqual(Set(names).count, 3)
        XCTAssertEqual(names[0], "AIROOM Native (claude)")
        XCTAssertEqual(names[1], "AIROOM Native (codex)")
        for name in names { XCTAssertTrue((3...32).contains(name.count) && name.allSatisfy(\.isASCII), name) }
        let odd = VTSProtocol.pluginName(for: MemberID(rawValue: String(repeating: "長x", count: 40)))
        XCTAssertLessThanOrEqual(odd.count, 32)
    }

    /// The bug from the native-VTS handoff: approving the second member on the
    /// other instance revoked the first member's token.
    func testApprovingOneMemberNoLongerRevokesTheOther() async throws {
        let grants = SharedGrants()
        let port8001 = FakeInstance(grants: grants), port8002 = FakeInstance(grants: grants)
        let gpt = VTubeStudioClient(makeTransport: { _ in port8001 })
        let claude = VTubeStudioClient(makeTransport: { _ in port8002 })
        let gptToken = try await gpt.connect(port: 8001, token: "", plugin: VTSProtocol.pluginName(for: .codex)).token
        let claudeToken = try await claude.connect(port: 8002, token: "", plugin: VTSProtocol.pluginName(for: .claude)).token

        // Restarting AI Room: both stored tokens still work, no Allow dialog.
        let again8001 = FakeInstance(grants: grants), again8002 = FakeInstance(grants: grants)
        let gpt2 = VTubeStudioClient(makeTransport: { _ in again8001 })
        let claude2 = VTubeStudioClient(makeTransport: { _ in again8002 })
        _ = try await gpt2.connect(port: 8001, token: gptToken, plugin: VTSProtocol.pluginName(for: .codex), mayAsk: false)
        _ = try await claude2.connect(port: 8002, token: claudeToken, plugin: VTSProtocol.pluginName(for: .claude), mayAsk: false)
        XCTAssertFalse(again8001.sentTypes.contains("AuthenticationTokenRequest"))
        XCTAssertFalse(again8002.sentTypes.contains("AuthenticationTokenRequest"))

        // The old single shared name reproduces the handoff's failure.
        let legacy = SharedGrants()
        let first = legacy.issue(for: "AIROOM Native")
        _ = legacy.issue(for: "AIROOM Native")
        XCTAssertFalse(legacy.accepts(first, for: "AIROOM Native"))
    }

    func testAutomaticReconnectNeverShowsTheAllowDialog() async throws {
        let instance = FakeInstance(grants: SharedGrants())
        let client = VTubeStudioClient(makeTransport: { _ in instance })
        do {
            _ = try await client.connect(port: 8001, token: "revoked", plugin: "AIROOM Native (claude)", mayAsk: false)
            XCTFail("expected needsApproval")
        } catch { XCTAssertEqual(error as? VTSError, .needsApproval) }
        XCTAssertFalse(instance.sentTypes.contains("AuthenticationTokenRequest"))
    }

    func testExpressionsAreReadAndSwitchedExplicitly() async throws {
        let instance = FakeInstance(grants: SharedGrants())
        let client = VTubeStudioClient(makeTransport: { _ in instance })
        _ = try await client.connect(port: 8001, token: "", plugin: "AIROOM Native (claude)")
        do {
            try await client.setExpression(file: "AIROOM_happy.exp3.json", active: true)
            XCTFail("files must come from expressions() first")
        } catch { XCTAssertEqual(error as? VTSError, .unknownHotkey("AIROOM_happy.exp3.json")) }
        let list = try await client.expressions()
        XCTAssertEqual(list.map(\.file), ["AIROOM_happy.exp3.json", "AIROOM_sad.exp3.json"])
        XCTAssertEqual(list.filter(\.active).map(\.name), ["AIROOM_sad"])
        try await client.setExpression(file: "AIROOM_happy.exp3.json", active: true)
        try await client.setExpression(file: "AIROOM_happy.exp3.json", active: true)
        let after = try await client.expressions()
        XCTAssertEqual(after.first { $0.file == "AIROOM_happy.exp3.json" }?.active, true, "on twice stays on")
        do {
            try await client.setExpression(file: "../evil.exp3.json", active: true)
            XCTFail("unknown files are refused")
        } catch { XCTAssertEqual(error as? VTSError, .unknownHotkey("../evil.exp3.json")) }
        XCTAssertFalse(instance.sentTypes.contains("HotkeyTriggerRequest"))
    }

    func testExpressionRequestsAndHotkeyFiles() throws {
        let on = VTSProtocol.expressionActivation(id: "1", file: "AIROOM_shy.exp3.json", active: true, fadeTime: 9)
        XCTAssertEqual(on.messageType, "ExpressionActivationRequest")
        XCTAssertEqual(on.data?["expressionFile"]?.string, "AIROOM_shy.exp3.json")
        XCTAssertEqual(on.data?["active"]?.bool, true)
        XCTAssertEqual(on.data?["fadeTime"]?.number, 2, "clamped")
        let keys = VTSProtocol.parseHotkeys(.object(["availableHotkeys": .array([
            .object(["hotkeyID": .string("w"), "name": .string("揮手打招呼 wave"), "type": .string("TriggerAnimation"),
                     "file": .string("02_Wave.motion3.json")])])]))
        XCTAssertEqual(keys.first?.file, "02_Wave.motion3.json")
        XCTAssertEqual(keys.first?.playsAnimation, true)
        XCTAssertEqual(keys.first?.toggles, false)
    }

    func testModelsAreListedAndLoaded() async throws {
        let instance = FakeInstance(grants: SharedGrants())
        let client = VTubeStudioClient(makeTransport: { _ in instance })
        _ = try await client.connect(port: 8001, token: "", plugin: "AIROOM Native (codex)")
        let models = try await client.availableModels()
        XCTAssertEqual(models.map(\.name), ["akari", "hiyori"])
        XCTAssertEqual(models.first(where: \.loaded)?.id, "id-akari")
        try await client.loadModel(id: "id-hiyori")
        let now = try await client.currentModel()
        XCTAssertEqual(now.name, "hiyori")
    }

    func testDropIsReportedButADeliberateDisconnectIsNot() async throws {
        let lost = expectation(description: "lost")
        let instance = FakeInstance(grants: SharedGrants())
        let client = VTubeStudioClient(makeTransport: { _ in instance })
        await client.setOnConnectionLost { lost.fulfill() }
        _ = try await client.connect(port: 8001, token: "", plugin: "AIROOM Native (claude)")
        instance.drop()
        await fulfillment(of: [lost], timeout: 2)
        let authenticated = await client.authenticated
        XCTAssertFalse(authenticated)

        let quiet = expectation(description: "not reported"); quiet.isInverted = true
        let other = FakeInstance(grants: SharedGrants())
        let calm = VTubeStudioClient(makeTransport: { _ in other })
        await calm.setOnConnectionLost { quiet.fulfill() }
        _ = try await calm.connect(port: 8002, token: "", plugin: "AIROOM Native (codex)")
        await calm.disconnect()
        await fulfillment(of: [quiet], timeout: 0.5)
    }

    func testOldSavedStageGainsEmptyModel() throws {
        let json = #"{"agent":"Claude Code","vtsPort":8002,"vtsToken":"t"}"#
        let character = try JSONDecoder().decode(LiveCharacterStage.self, from: Data(json.utf8))
        XCTAssertEqual(character.vtsModelID, "")
        XCTAssertEqual(character.vtsToken, "t")
        var settings = LiveStageSettings()
        settings.updateCharacter(.claude) { $0.vtsModelID = "id-hiyori"; $0.vtsModelName = "hiyori" }
        let round = try JSONDecoder().decode(LiveStageSettings.self, from: JSONEncoder().encode(settings))
        XCTAssertEqual(round.character(.claude).vtsModelID, "id-hiyori")
    }
}

private actor Ports {
    var open: Set<Int>
    var launched: [Int] = []
    init(_ open: Set<Int>) { self.open = open }
    func isOpen(_ port: Int) -> Bool { open.contains(port) }
    /// A new instance takes the lowest free port from 8001 up.
    func launch() { var port = 8001; while open.contains(port) { port += 1 }; open.insert(port); launched.append(port) }
}

final class VTubeStudioLauncherTests: XCTestCase {
    private let app = URL(fileURLWithPath: "/Applications/VTubeStudio.app")

    func testStartsMissingInstancesLowestPortFirst() async throws {
        let ports = Ports([])
        let launcher = VTubeStudioLauncher(isListening: { await ports.isOpen($0) },
                                           launch: { _ in Task { await ports.launch() } },
                                           sleep: { _ in await Task.yield() })
        let started = try await launcher.start(ports: [8002, 8001], app: app)
        XCTAssertEqual(started, [8001, 8002])
        let launched = await ports.launched
        XCTAssertEqual(launched, [8001, 8002])
    }

    func testSkipsInstancesAlreadyRunning() async throws {
        let ports = Ports([8001])
        let launcher = VTubeStudioLauncher(isListening: { await ports.isOpen($0) },
                                           launch: { _ in Task { await ports.launch() } },
                                           sleep: { _ in await Task.yield() })
        let started = try await launcher.start(ports: [8001, 8002], app: app)
        XCTAssertEqual(started, [8002])
    }

    func testAPIThatNeverStartsIsReported() async {
        let launcher = VTubeStudioLauncher(isListening: { _ in false }, launch: { _ in }, sleep: { _ in })
        do {
            _ = try await launcher.start(ports: [8001], app: app, timeout: 2)
            XCTFail("expected a timeout")
        } catch { XCTAssertEqual(error as? VTubeStudioLauncher.Failure, .portNeverOpened(8001)) }
    }

    func testPortProbe() {
        XCTAssertFalse(VTubeStudioLauncher.portIsOpen(1))
    }
}
#endif

#if os(macOS)
/// Opens two real native VTube Studio instances. Opt in with AIROOM_LIVE_VTS=1;
/// needs "Start API" on in VTS. Leaves the instances running.
final class NativeVTSLiveTests: XCTestCase {
    func testLiveLauncherBringsUpBothPortsInOrder() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["AIROOM_LIVE_VTS"] == "1")
        let app = try XCTUnwrap(VTubeStudioLauncher.installedApp())
        try await VTubeStudioLauncher().start(ports: [8001, 8002], app: app, timeout: 90)
        XCTAssertTrue(VTubeStudioLauncher.portIsOpen(8001))
        XCTAssertTrue(VTubeStudioLauncher.portIsOpen(8002))
    }
}
#endif

final class LiveHotkeyMatcherTests: XCTestCase {
    func testMatchesByEnglishOrChineseName() {
        let keys = [VTSHotkey(id: "a", name: "開心 happy", type: "ToggleExpression"),
                    VTSHotkey(id: "b", name: "Shy face", type: "ToggleExpression"),
                    VTSHotkey(id: "c", name: "生氣", type: "ToggleExpression"),
                    VTSHotkey(id: "d", name: "Remove Expressions", type: "RemoveAllExpressions")]
        let map = LiveHotkeyMatcher.match(keys)
        XCTAssertEqual(map, ["happy": "a", "shy": "b", "angry": "c"])
    }
}
