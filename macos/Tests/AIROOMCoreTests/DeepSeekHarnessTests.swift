import XCTest
@testable import AIROOMCore

final class DeepSeekHarnessTests: XCTestCase {
    /// Recorded from `dsh --profile headless --json` 0.2.1-rc.5.
    let completed = [
        #"{"type":"session","sessionId":"session-ea01f5da","cwd":"/tmp/x"}"#,
        #"{"type":"status","phase":"turn_start","turn":1}"#,
        #"{"type":"thinking","text":""}"#,
        #"{"type":"text","text":"好"}"#,
        #"{"type":"status","phase":"step_end","turn":1,"step":1,"usage":{"inputTokens":5721,"outputTokens":2}}"#,
        #"{"type":"status","phase":"turn_end","turn":1,"reason":{"kind":"completed"}}"#,
        #"{"type":"final","text":"好"}"#,
    ]

    func testParsesHeadlessEvents() {
        var parser = CLIEventAccumulator(agent: .deepseek)
        for line in completed { parser.consume(line: line) }
        XCTAssertEqual(parser.text, "好")
        XCTAssertEqual(parser.sessionID, "session-ea01f5da")
        XCTAssertNil(parser.failure)
    }

    func testToolStepsStreamButTheFinalTextWins() {
        var parser = CLIEventAccumulator(agent: .deepseek)
        parser.consume(line: #"{"type":"text","text":"我先看看檔案。"}"#)
        parser.consume(line: #"{"type":"tool_call","callId":"c1","tool":"read","input":{"file_path":"a.txt"}}"#)
        parser.consume(line: #"{"type":"tool_result","callId":"c1","status":"completed","result":"x"}"#)
        XCTAssertEqual(parser.text, "我先看看檔案。")
        parser.consume(line: #"{"type":"final","text":"改好了"}"#)
        XCTAssertEqual(parser.text, "改好了", "tool calls are never shown as the answer")
    }

    func testAFailedTurnIsAFailure() {
        var parser = CLIEventAccumulator(agent: .deepseek)
        parser.consume(line: #"{"type":"status","phase":"turn_end","turn":1,"reason":{"kind":"error","error":{"message":"no API key","code":"MISSING_CREDENTIAL"}}}"#)
        parser.consume(line: #"{"type":"final","text":""}"#)
        XCTAssertEqual(parser.failure, "no API key")
        XCTAssertEqual(parser.text, "")
    }

    func testArgumentsUseTheHeadlessProfileAndStdin() {
        var options = CLIRunOptions()
        options.session = SessionPlan(resumeID: "session-1", fallbackPrompt: "x")
        XCTAssertEqual(CommandPlan.arguments(for: .deepseek, access: .stagedWrite, options: options, patch: "/tmp/p.yml"),
                       ["--profile", "headless", "--patch", "/tmp/p.yml", "--json", "--session-id", "session-1", "-"])
        XCTAssertEqual(CommandPlan.arguments(for: .deepseek), ["--profile", "headless", "--json", "-"])
    }

    func testPatchTurnsOnSavedCredentialsAndPicksAModel() {
        let flash = DeepSeekHarness.patch(model: "")
        XCTAssertTrue(flash.contains("- id: credentials\n  disabled: false"))
        XCTAssertTrue(flash.contains("provider: deepseek-official"))
        XCTAssertTrue(flash.contains(#"model: "deepseek-flash""#))
        XCTAssertTrue(DeepSeekHarness.patch(model: "deepseek-v4-pro").contains(#"model: "deepseek-v4-pro""#))
        // Anything that isn't a plain model id falls back to the default rather than reaching YAML.
        XCTAssertTrue(DeepSeekHarness.patch(model: "x\"\n- id: evil").contains(#"model: "deepseek-flash""#))
        XCTAssertFalse(flash.lowercased().contains("api_key"))
    }

    func testSandboxFollowsAccess() {
        let base = ["HOME": "/Users/me", "PATH": "/usr/bin"]
        XCTAssertEqual(DeepSeekHarness.environment(base: base, access: .readOnly)["DSH_PERMISSION_MODE"], "read-only")
        XCTAssertEqual(DeepSeekHarness.environment(base: base, access: .readFiles)["DSH_PERMISSION_MODE"], "read-only")
        XCTAssertEqual(DeepSeekHarness.environment(base: base, access: .stagedWrite)["DSH_PERMISSION_MODE"], "workspace-write")
        XCTAssertEqual(DeepSeekHarness.environment(base: base, access: .readOnly)["DSH_TELEMETRY_DISABLED"], "1")
        XCTAssertNil(DeepSeekHarness.environment(base: base, access: .readOnly)["DEEPSEEK_API_KEY"])
    }

    func testSettingsKeepADeepSeekPathAndOldSettingsStillRead() throws {
        var settings = NativeSettings()
        settings.setPath("/opt/dsh", for: .deepseek)
        XCTAssertEqual(settings.configuredPath(for: .deepseek), "/opt/dsh")
        XCTAssertEqual(settings.configuredPath(for: .claude), "")
        let old = try JSONDecoder().decode(NativeSettings.self, from: Data(#"{"claudePath":"/c","codexPath":"/x"}"#.utf8))
        XCTAssertEqual(old.deepseekPath, "")
        XCTAssertEqual(old.codexPath, "/x")
        XCTAssertTrue(CommandPlan.bundledDeepSeekPaths(home: "/Users/me").contains(
            "/Applications/DeepSeek Harness.app/Contents/Resources/runtime/cli/bin/dsh"))
    }

    /// A stand-in dsh that records its arguments and sandbox mode, reads stdin and answers.
    func testRunnerDrivesDshForADeepSeekMember() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let record = dir.appendingPathComponent("record.txt")
        let script = dir.appendingPathComponent("dsh")
        let source = """
        #!/bin/sh
        input=$(cat)
        printf '%s\\n' "$*" > '\(record.path)'
        printf 'mode=%s\\n' "$DSH_PERMISSION_MODE" >> '\(record.path)'
        printf 'patch=' >> '\(record.path)'; cat "$4" >> '\(record.path)'
        echo '{"type":"session","sessionId":"session-42","cwd":"/tmp"}'
        echo '{"type":"status","phase":"turn_start","turn":1}'
        echo '{"type":"text","text":"收到："}'
        echo "{\\"type\\":\\"final\\",\\"text\\":\\"收到：$input\\"}"
        """
        try Data(source.utf8).write(to: script)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)

        let ds = MemberID(rawValue: "m-d5d5d5")
        let runner = CLIProcessRunner()
        runner.setDefaults(ds, executor: .deepseek, model: "deepseek-v4-pro", effort: "")
        let result = try await runner.runDetailed(agent: ds, prompt: "宵夜", settings: NativeSettings(deepseekPath: script.path, workspacePath: dir.path),
                                                  fallbackWorkspace: dir, access: .stagedWrite) { _ in }
        XCTAssertEqual(result.text, "收到：宵夜")
        XCTAssertEqual(result.sessionID, "session-42")
        XCTAssertEqual(result.model, "deepseek-v4-pro")
        let recorded = try String(contentsOf: record, encoding: .utf8)
        XCTAssertTrue(recorded.hasPrefix("--profile headless --patch "))
        XCTAssertTrue(recorded.contains("--json -"))
        XCTAssertTrue(recorded.contains("mode=workspace-write"))
        XCTAssertTrue(recorded.contains(#"model: "deepseek-v4-pro""#))
        // The per-run overlay is removed afterwards.
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: NSTemporaryDirectory()).filter { $0.hasPrefix("airoom-dsh-") }
        XCTAssertTrue(leftovers.isEmpty)
    }

    func testImagesArePreventedForDeepSeek() async throws {
        let ds = MemberID(rawValue: "m-d5d5d5")
        let runner = CLIProcessRunner()
        runner.setDefaults(ds, executor: .deepseek, model: "", effort: "")
        let image = ChatAttachment(fileName: "a.png", kind: .image, mediaType: "image/png", byteCount: 4)
        var options = CLIRunOptions()
        options.attachments = [PreparedAttachment(attachment: image, file: URL(fileURLWithPath: "/tmp/a.png"))]
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let script = dir.appendingPathComponent("dsh")
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: script)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        do {
            _ = try await runner.runDetailed(agent: ds, prompt: "看圖", settings: NativeSettings(deepseekPath: script.path, workspacePath: dir.path),
                                             fallbackWorkspace: dir, options: options) { _ in }
            XCTFail("an image must not reach dsh")
        } catch let error as AttachmentError {
            XCTAssertEqual(error, .filesNotSupported("a.png"))
        }
    }

    func testAReadOnlyCopyStaysAtOnePathSoSessionsCanResume() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir.appendingPathComponent("project"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        try Data("x".utf8).write(to: dir.appendingPathComponent("project/a.txt"))
        let script = dir.appendingPathComponent("dsh")
        let source = """
        #!/bin/sh
        cat > /dev/null
        echo '{"type":"session","sessionId":"s","cwd":"/tmp"}'
        echo "{\\"type\\":\\"final\\",\\"text\\":\\"$(pwd -P)\\"}"
        """
        try Data(source.utf8).write(to: script)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        let runner = CLIProcessRunner()
        let ds = MemberID(rawValue: "m-aaaaaa"), other = MemberID(rawValue: "m-bbbbbb")
        runner.setDefaults(ds, executor: .deepseek, model: "", effort: "")
        runner.setDefaults(other, executor: .deepseek, model: "", effort: "")
        let settings = NativeSettings(deepseekPath: script.path, workspacePath: dir.appendingPathComponent("project").path)
        func cwd(_ member: MemberID) async throws -> String {
            try await runner.run(agent: member, prompt: "看", settings: settings, fallbackWorkspace: dir, access: .readFiles) { _ in }
        }
        let first = try await cwd(ds), second = try await cwd(ds), theirs = try await cwd(other)
        XCTAssertEqual(first, second, "same member, same folder: same copy path")
        XCTAssertNotEqual(first, theirs, "each member gets their own copy")
        XCTAssertTrue(first.contains("airoom-readonly-dsh-"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: first), "the copy is removed after each run")
        XCTAssertEqual(DeepSeekHarness.copyKey(member: ds, folder: dir), DeepSeekHarness.copyKey(member: ds, folder: dir))
    }

    func testASessionFromAnotherFolderRestartsWithTheFullPrompt() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let script = dir.appendingPathComponent("dsh")
        let source = """
        #!/bin/sh
        input=$(cat)
        case "$*" in
          *--session-id*)
            echo '{"type":"error","message":"session \\"s-old\\" was recorded in \\"/a\\", not \\"/b\\""}'
            echo 'dsh: session "s-old" was recorded in "/a", not "/b"' >&2
            exit 1 ;;
        esac
        echo '{"type":"session","sessionId":"s-new","cwd":"/b"}'
        echo "{\\"type\\":\\"final\\",\\"text\\":\\"$input\\"}"
        """
        try Data(source.utf8).write(to: script)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        let runner = CLIProcessRunner()
        let ds = MemberID(rawValue: "m-cccccc")
        runner.setDefaults(ds, executor: .deepseek, model: "", effort: "")
        var options = CLIRunOptions()
        options.session = SessionPlan(resumeID: "s-old", fallbackPrompt: "完整前情")
        let result = try await runner.runDetailed(agent: ds, prompt: "只有新的一句", settings: NativeSettings(deepseekPath: script.path, workspacePath: dir.path),
                                                  fallbackWorkspace: dir, options: options) { _ in }
        XCTAssertEqual(result.text, "完整前情")
        XCTAssertEqual(result.sessionID, "s-new")
        XCTAssertFalse(result.resumed)
    }

    func testAnErrorEventIsAFailure() {
        var parser = CLIEventAccumulator(agent: .deepseek)
        parser.consume(line: #"{"type":"error","message":"session \"s\" was recorded in \"/a\", not \"/b\""}"#)
        XCTAssertNotNil(parser.failure)
        XCTAssertTrue(DeepSeekHarness.isUnknownSession(#"dsh: session "s" was recorded in "/a", not "/b""#))
    }
}
