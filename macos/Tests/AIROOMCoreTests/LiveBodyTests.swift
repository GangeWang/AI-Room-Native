import XCTest
@testable import AIROOMCore

final class LiveBodyToolTests: XCTestCase {
    func testToolListIsTheBodyOnly() {
        let names = LiveBodyTools.definitions(english: false).compactMap { $0["name"] as? String }
        XCTAssertEqual(names, ["say", "pose", "expression", "motion", "describe_body"])
        for english in [true, false] {
            for tool in LiveBodyTools.definitions(english: english) {
                XCTAssertNotNil(tool["inputSchema"] as? [String: Any])
                XCTAssertTrue(JSONSerialization.isValidJSONObject(tool))
            }
        }
    }

    func testPoseTakesJSONNumbersAndClamps() throws {
        // JSONSerialization turns 1 into an NSNumber that also casts to Bool.
        let args = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(#"{"arm_left":1,"head_turn":99,"smile":0.8,"seconds":0}"#.utf8)) as? [String: Any])
        guard case .success(.pose(let pose)) = LiveBodyTools.command(name: "pose", arguments: args) else { return XCTFail() }
        XCTAssertEqual(pose.armL, 1)
        XCTAssertEqual(pose.headX, 30, "clamped")
        XCTAssertEqual(pose.smile, 0.8)
        XCTAssertEqual(pose.duration, 0.1, "clamped")
        XCTAssertNil(pose.armR)
    }

    func testPoseRejectsBooleansAndEmptyPoses() throws {
        let args = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(#"{"arm_left":true}"#.utf8)) as? [String: Any])
        XCTAssertEqual(LiveBodyTools.command(name: "pose", arguments: args), .failure(.badArguments("no pose keys given")))
        XCTAssertEqual(LiveBodyTools.command(name: "pose", arguments: [:]), .failure(.badArguments("no pose keys given")))
    }

    func testSayAndExpressionAndUnknown() {
        XCTAssertEqual(LiveBodyTools.command(name: "say", arguments: ["text": "  哈囉  "]), .success(.say("哈囉")))
        XCTAssertEqual(LiveBodyTools.command(name: "say", arguments: ["text": " "]), .failure(.badArguments("text is empty")))
        let long = String(repeating: "啊", count: 2_000)
        XCTAssertEqual(LiveBodyTools.command(name: "say", arguments: ["text": long]),
                       .success(.say(String(repeating: "啊", count: LiveBodyTools.maxLine))))
        XCTAssertEqual(LiveBodyTools.command(name: "expression", arguments: ["name": "開心"]), .success(.expression("開心", on: true)))
        XCTAssertEqual(LiveBodyTools.command(name: "describe_body", arguments: [:]), .success(.describe))
        XCTAssertEqual(LiveBodyTools.command(name: "run_shell", arguments: [:]), .failure(.unknownTool("run_shell")))
    }

    func testHotkeyLookup() {
        let keys = [VTSHotkey(id: "a1", name: "開心 happy", type: "ToggleExpression"),
                    VTSHotkey(id: "b2", name: "Shy", type: "ToggleExpression")]
        XCTAssertEqual(LiveBodyTools.hotkey(named: "shy", in: keys)?.id, "b2")
        XCTAssertEqual(LiveBodyTools.hotkey(named: "happy", in: keys)?.id, "a1", "partial match")
        XCTAssertEqual(LiveBodyTools.hotkey(named: "a1", in: keys)?.id, "a1", "by id")
        XCTAssertNil(LiveBodyTools.hotkey(named: "angry", in: keys))
    }

    func testExpressionOnOffAndMotionArguments() throws {
        func args(_ json: String) throws -> [String: Any] {
            try XCTUnwrap(JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
        }
        XCTAssertEqual(LiveBodyTools.command(name: "expression", arguments: try args(#"{"name":"happy","on":false}"#)),
                       .success(.expression("happy", on: false)))
        XCTAssertEqual(LiveBodyTools.command(name: "expression", arguments: try args(#"{"name":"happy","on":true}"#)),
                       .success(.expression("happy", on: true)))
        XCTAssertEqual(LiveBodyTools.command(name: "expression", arguments: ["name": "happy", "on": "off"]),
                       .success(.expression("happy", on: false)))
        XCTAssertEqual(LiveBodyTools.command(name: "motion", arguments: ["name": " wave "]),
                       .success(.motion("wave", seconds: LiveBodyTools.defaultMotionSeconds)))
        XCTAssertEqual(LiveBodyTools.command(name: "motion", arguments: try args(#"{"name":"nod","seconds":60}"#)),
                       .success(.motion("nod", seconds: 10)), "clamped")
        XCTAssertEqual(LiveBodyTools.command(name: "motion", arguments: [:]), .failure(.badArguments("name is empty")))
    }

    func testPoseTakesBrowAngle() throws {
        let args = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(#"{"brow_angle":-3}"#.utf8)) as? [String: Any])
        guard case .success(.pose(let pose)) = LiveBodyTools.command(name: "pose", arguments: args) else { return XCTFail() }
        XCTAssertEqual(pose.browAngle, -1, "clamped")
        XCTAssertFalse(pose.isEmpty, "brow angle alone makes a pose")
    }

    /// Names and files as the OrangeBlossom (claude) model has them.
    func testExpressionLookupByNameFileOrHotkey() {
        let expressions = ["happy", "sad", "shy"].map {
            VTSExpression(name: "AIROOM_\($0)", file: "AIROOM_\($0).exp3.json", active: $0 == "sad")
        }
        let keys = [VTSHotkey(id: "h", name: "開心 happy", type: "ToggleExpression", file: "AIROOM_happy.exp3.json"),
                    VTSHotkey(id: "w", name: "揮手打招呼 wave", type: "TriggerAnimation", file: "02_Wave.motion3.json")]
        XCTAssertEqual(LiveBodyTools.expression(named: "AIROOM_shy.exp3.json", in: expressions)?.file, "AIROOM_shy.exp3.json")
        XCTAssertEqual(LiveBodyTools.expression(named: "airoom_sad", in: expressions)?.file, "AIROOM_sad.exp3.json")
        XCTAssertEqual(LiveBodyTools.expression(named: "開心", in: expressions, hotkeys: keys)?.file, "AIROOM_happy.exp3.json",
                       "through the hotkey's name")
        XCTAssertEqual(LiveBodyTools.expression(named: "shy", in: expressions)?.file, "AIROOM_shy.exp3.json", "partial")
        XCTAssertNil(LiveBodyTools.expression(named: "wave", in: expressions, hotkeys: keys), "a motion is not an expression")
        XCTAssertNil(LiveBodyTools.expression(named: "angry", in: expressions, hotkeys: keys))
    }

    func testMotionLookupOnlyFindsAnimations() {
        let keys = [VTSHotkey(id: "h", name: "開心 happy", type: "ToggleExpression"),
                    VTSHotkey(id: "w", name: "揮手打招呼 wave", type: "TriggerAnimation"),
                    VTSHotkey(id: "n", name: "點頭 nod", type: "TriggerAnimation")]
        XCTAssertEqual(LiveBodyTools.motion(named: "wave", in: keys)?.id, "w")
        XCTAssertEqual(LiveBodyTools.motion(named: "點頭", in: keys)?.id, "n")
        XCTAssertNil(LiveBodyTools.motion(named: "happy", in: keys))
    }

    func testMCPFraming() throws {
        let line = MCPWire.result(id: 7, MCPWire.toolResult("ok"))
        XCTAssertEqual(line.last, 0x0A)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: line.dropLast()) as? [String: Any])
        XCTAssertEqual(object["id"] as? Int, 7)
        XCTAssertEqual(object["jsonrpc"] as? String, "2.0")
        let initialize = MCPWire.initializeResult(requested: "2024-11-05", server: "airoom-body")
        XCTAssertEqual(initialize["protocolVersion"] as? String, "2024-11-05")
    }

    func testWireMessagesRoundTrip() throws {
        let config = LiveBodyConfig(member: "claude", displayName: "Claude", port: 8001, token: "t", plugin: "AIROOM Native (claude)",
                                    mouthParameter: "MouthOpen", voiceIdentifier: "", speechRate: 0.5, pitch: 1, english: false,
                                    arms: LivePoseAnimator.ArmInputs(member: .claude).all)
        let message = LiveBodyMessage(kind: .config, config: config)
        let line = try message.line()
        XCTAssertEqual(line.last, 0x0A)
        XCTAssertEqual(LiveBodyMessage.parse(line.dropLast()), message)
        XCTAssertNil(LiveBodyMessage.parse(Data("{\"kind\":\"steal\"}".utf8)))
    }
}

final class LiveAgentPlanTests: XCTestCase {
    let body = LiveBodyLaunch(command: "/Apps/AI Room.app/Contents/MacOS/airoom-body",
                              arguments: ["--member", "claude", "--socket", "/tmp/x.sock"],
                              environment: ["AIROOM_BODY_SECRET": "s"])

    func testClaudeGetsNoBuiltInToolsAndOnlyTheBody() throws {
        let args = LiveAgentPlan.claudeArguments(body: body, model: "sonnet", effort: "")
        XCTAssertEqual(args[args.firstIndex(of: "--tools")! + 1], "")
        XCTAssertTrue(args.contains("--strict-mcp-config"))
        XCTAssertEqual(args[args.firstIndex(of: "--allowedTools")! + 1], "mcp__body")
        XCTAssertEqual(args[args.firstIndex(of: "--model")! + 1], "sonnet")
        XCTAssertFalse(args.contains("--effort"))
        XCTAssertFalse(args.contains { $0.contains("dangerously") || $0 == "bypassPermissions" })
        let json = args[args.firstIndex(of: "--mcp-config")! + 1]
        let config = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
        let servers = try XCTUnwrap(config["mcpServers"] as? [String: Any])
        XCTAssertEqual(Array(servers.keys), ["body"])
        let server = try XCTUnwrap(servers["body"] as? [String: Any])
        XCTAssertEqual(server["command"] as? String, body.command)
        XCTAssertEqual((server["env"] as? [String: String])?["AIROOM_BODY_SECRET"], "s")
    }

    func testCodexTurnsOffEveryOtherToolSource() {
        let config = LiveAgentPlan.codexThreadConfig(body: body, userServers: ["computer-use", "node_repl", "body"])
        for feature in ["plugins", "apps", "computer_use", "shell_tool", "unified_exec", "browser_use", "multi_agent"] {
            XCTAssertEqual(config["features.\(feature)"] as? Bool, false, feature)
        }
        XCTAssertEqual(config["mcp_servers.computer-use.enabled"] as? Bool, false)
        XCTAssertEqual(config["mcp_servers.node_repl.enabled"] as? Bool, false)
        XCTAssertNil(config["mcp_servers.body.enabled"], "never switch off our own body")
        XCTAssertEqual(config["mcp_servers.body.command"] as? String, body.command)
        XCTAssertEqual(config["web_search"] as? String, "disabled")
        XCTAssertTrue(JSONSerialization.isValidJSONObject(config))
    }

    func testCodexServerListParsing() {
        let json = Data(#"[{"name":"codex_app","enabled":false},{"name":"computer-use","enabled":true},{"name":"node_repl"}]"#.utf8)
        XCTAssertEqual(LiveAgentPlan.enabledCodexServers(fromListJSON: json), ["computer-use", "node_repl"])
        XCTAssertEqual(LiveAgentPlan.enabledCodexServers(fromListJSON: Data("oops".utf8)), [])
    }

    func testBriefTeachesTheBodyAndTheFloor() {
        for english in [true, false] {
            let duo = LiveBodyPrompt.brief(name: "Claude", topic: "t", others: ["GPT"], transcript: "", summary: "", english: english)
            let solo = LiveBodyPrompt.brief(name: "Claude", topic: "t", others: [], transcript: "", summary: "", english: english)
            for text in [duo, solo] {
                for tool in ["say", "pose", "expression", "describe_body"] { XCTAssertTrue(text.contains(tool), tool) }
                XCTAssertTrue(text.contains(LiveShowPolicy.hostLabel))
            }
            XCTAssertTrue(duo.contains("GPT"))
            XCTAssertNotEqual(duo, solo)
        }
        XCTAssertEqual(LiveBodyPrompt.heard("GPT", "嗨", english: false), "[GPT 說] 嗨")
    }

    func testStageSettingDefaultsOnAndSurvivesOldSaves() throws {
        XCTAssertTrue(LiveStageSettings().agentBodies)
        let old = try JSONDecoder().decode(LiveStageSettings.self, from: Data(#"{"speechEnabled":true,"vtsEnabled":true,"characters":[]}"#.utf8))
        XCTAssertTrue(old.agentBodies)
        let off = LiveStageSettings(agentBodies: false)
        let decoded = try JSONDecoder().decode(LiveStageSettings.self, from: JSONEncoder().encode(off))
        XCTAssertFalse(decoded.agentBodies)
    }
}

final class LiveAgentSessionTests: XCTestCase {
    /// A stand-in "claude" that ends a turn for every message it reads and
    /// records what it got.
    private func fakeClaude() throws -> (binary: URL, log: URL, folder: URL) {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("airoom-fake-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let log = folder.appendingPathComponent("got.txt")
        let script = folder.appendingPathComponent("claude")
        try """
        #!/bin/sh
        while IFS= read -r line; do
          printf '%s\\n' "$line" >> '\(log.path)'
          sleep 0.3
          printf '{"type":"result","subtype":"success"}\\n'
        done
        """.write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        return (script, log, folder)
    }

    func testOneMessageInFlightAndTheRestBatched() async throws {
        let fake = try fakeClaude()
        defer { try? FileManager.default.removeItem(at: fake.folder) }
        let session = LiveAgentSession(kind: .claude, binary: fake.binary, cwd: fake.folder,
                                       body: LiveBodyLaunch(command: "/bin/true", arguments: [], environment: [:]))
        try session.start()
        defer { session.stop() }
        session.send("first")
        session.send("second")
        session.send("third")
        let ended = await session.sendAndWait("your turn", timeout: 10)
        XCTAssertTrue(ended)
        let got = try String(contentsOf: fake.log, encoding: .utf8).split(separator: "\n")
        XCTAssertEqual(got.count, 2, "the first message alone, then everything that queued up behind it")
        XCTAssertTrue(got[0].contains("first"))
        XCTAssertTrue(got[1].contains("second") && got[1].contains("third") && got[1].contains("your turn"))
    }

    func testWaitEndsWhenTheAgentExits() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("airoom-fake-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let script = folder.appendingPathComponent("claude")
        try "#!/bin/sh\nread line\nexit 3\n".write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        let session = LiveAgentSession(kind: .claude, binary: script, cwd: folder,
                                       body: LiveBodyLaunch(command: "/bin/true", arguments: [], environment: [:]))
        try session.start()
        defer { session.stop() }
        let ended = await session.sendAndWait("hello", timeout: 10)
        XCTAssertFalse(ended)
    }
}
