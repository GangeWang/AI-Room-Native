#if os(macOS)
import XCTest
@testable import AIROOMCore

final class ZCodeTests: XCTestCase {
    private func home() throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("airoom-zcode-\(UUID())")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        return dir
    }

    /// A home laid out like the desktop leaves it, with a made-up token.
    private func fakeDesktop(_ home: URL, token: String = "test-token-not-real", user: String = "tester") throws -> URL {
        let v2 = home.appendingPathComponent(".zcode/v2")
        let endpoint = v2.appendingPathComponent("runtime/provider/darwin-aarch64/3.17.2/endpoint-abc")
        try FileManager.default.createDirectory(at: endpoint, withIntermediateDirectories: true)
        let sealed = try ZCode.encrypt(token, home: home.path, user: user)
        try JSONSerialization.data(withJSONObject: ["zcodejwttoken": sealed, "oauth:active_provider": "plain"])
            .write(to: v2.appendingPathComponent("credentials.json"))
        try JSONSerialization.data(withJSONObject: ["deviceMid": "mid-1"]).write(to: v2.appendingPathComponent("telemetry-state.json"))
        let builtin: [String: Any] = ["revision": 43, "config": ["providerConfigRules": ["providerRules": [
            ["providerId": ZCode.providerID, "config": ["builtinModelIds": ["GLM-5.3-Flash", "GLM-5.2"]]],
            ["providerId": "account:other", "config": ["builtinModelIds": ["X"]]],
        ]]]]
        let file = endpoint.appendingPathComponent("zcode-builtin.json")
        try JSONSerialization.data(withJSONObject: builtin).write(to: file)
        return file
    }

    func testOpensTheDesktopSignInAndReadsThePlan() throws {
        let home = try home()
        let file = try fakeDesktop(home)
        let auth = try XCTUnwrap(ZCode.loadAuth(home: home, user: "tester", appVersion: "3.17.2"))
        XCTAssertEqual(auth.token, "test-token-not-real")
        XCTAssertEqual(auth.deviceMid, "mid-1")
        XCTAssertEqual(auth.modelIDs, ["GLM-5.3-Flash", "GLM-5.2"])
        XCTAssertEqual(auth.basedOnRevision, "zcode-builtin:43:" + ZCode.sha256Hex(Data(file.standardizedFileURL.path.utf8)))
        XCTAssertTrue(ZCode.isSignedIn(home: home, user: "tester"))
        XCTAssertEqual(ZCode.modelChoices(home: home).map(\.id), ["GLM-5.3-Flash", "GLM-5.2"])
        XCTAssertEqual(ZCode.modelChoices(home: home).first?.isDefault, true)
    }

    func testTheTokenNeverShowsInDescriptions() throws {
        let home = try home()
        _ = try fakeDesktop(home)
        let auth = try XCTUnwrap(ZCode.loadAuth(home: home, user: "tester", appVersion: "3.17.2"))
        XCTAssertFalse(String(describing: auth).contains("test-token-not-real"))
        XCTAssertFalse(String(reflecting: auth).contains("test-token-not-real"))
        XCTAssertFalse("\(auth)".contains("test-token-not-real"))
    }

    func testAnotherUsersKeyOrNoSignInOpensNothing() throws {
        let home = try home()
        _ = try fakeDesktop(home)
        XCTAssertNil(ZCode.loadAuth(home: home, user: "someone-else", appVersion: ""))
        XCTAssertFalse(ZCode.isSignedIn(home: try self.home(), user: "tester"))
    }

    func testReadOnlyRunsPlanAndWritingRunsAskFirst() {
        XCTAssertEqual(ZCode.mode(for: .readOnly), "plan")
        XCTAssertEqual(ZCode.mode(for: .readFiles), "plan")
        // Never "edit": that mode applies edits to any path without asking.
        XCTAssertEqual(ZCode.mode(for: .stagedWrite), "build")
    }

    func testOnlyFileEditsInsideTheWorkspaceOfAWritingTaskAreAllowed() throws {
        let ws = try home().appendingPathComponent("desk")
        try FileManager.default.createDirectory(at: ws.appendingPathComponent("notes"), withIntermediateDirectories: true)
        func allows(_ tool: String, _ path: String, _ access: ExecutionAccess = .stagedWrite) -> Bool {
            ZCode.allows(tool: tool, input: ["file_path": path, "content": "x"], workspace: ws, access: access)
        }
        XCTAssertTrue(allows("Write", ws.path + "/a.txt"))
        XCTAssertTrue(allows("Edit", ws.path + "/notes/new/b.md"), "folders that don't exist yet are fine")
        XCTAssertTrue(allows("Write", "notes/c.txt"), "relative paths resolve inside the workspace")
        // Outside, by any spelling.
        XCTAssertFalse(allows("Write", NSHomeDirectory() + "/.zshrc"))
        XCTAssertFalse(allows("Write", ws.path + "/../escape.txt"))
        XCTAssertFalse(allows("Write", "../escape.txt"))
        XCTAssertFalse(allows("Write", ws.path + "-sibling/a.txt"), "a folder that only shares the prefix")
        XCTAssertFalse(allows("Write", ws.path), "the workspace itself is not a file in it")
        XCTAssertFalse(allows("Write", ""))
        // Read-only tasks, and every other tool.
        XCTAssertFalse(allows("Write", ws.path + "/a.txt", .readOnly))
        XCTAssertFalse(allows("Write", ws.path + "/a.txt", .readFiles))
        XCTAssertFalse(allows("Bash", ws.path + "/a.txt"))
        XCTAssertFalse(ZCode.allows(tool: "Bash", input: ["command": "touch x"], workspace: ws, access: .stagedWrite))
        XCTAssertFalse(ZCode.allows(tool: "Write", input: [:], workspace: ws, access: .stagedWrite))
    }

    func testSymlinksAndHiddenPathsCannotBeWrittenThrough() throws {
        let base = try home()
        let ws = base.appendingPathComponent("desk"), outside = base.appendingPathComponent("outside")
        try FileManager.default.createDirectory(at: ws, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: ws.appendingPathComponent("link"), withDestinationURL: outside)
        try FileManager.default.createSymbolicLink(atPath: ws.appendingPathComponent("dangling").path,
                                                   withDestinationPath: outside.appendingPathComponent("not-yet.txt").path)
        XCTAssertFalse(ZCode.isWritable(ws.path + "/link/a.txt", in: ws), "a linked folder leads outside")
        XCTAssertFalse(ZCode.isWritable(ws.path + "/dangling", in: ws), "writing a dangling link creates its target")
        // Config a later turn would trust.
        XCTAssertFalse(ZCode.isWritable(ws.path + "/.zcode/settings.json", in: ws))
        XCTAssertFalse(ZCode.isWritable(ws.path + "/.claude-plugin/plugin.json", in: ws))
        XCTAssertFalse(ZCode.isWritable(ws.path + "/sub/.git/hooks/pre-commit", in: ws))
        XCTAssertFalse(ZCode.isWritable(ws.path + "/.env", in: ws))
        XCTAssertTrue(ZCode.isWritable(ws.path + "/sub/ok.txt", in: ws))
        // A workspace reached through a link (like /var → /private/var) still works by either name.
        let alias = base.appendingPathComponent("alias")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: ws)
        XCTAssertTrue(ZCode.isWritable(alias.path + "/a.txt", in: alias))
        XCTAssertTrue(ZCode.isWritable(ws.resolvingSymlinksInPath().path + "/a.txt", in: alias))
    }

    func testTheTurnReplyIsTheCompletedAssistantMessageAfterTheUserMessage() {
        func message(_ role: String, _ created: Int, completed: Bool, _ text: String) -> [String: Any] {
            var time: [String: Any] = ["created": created]
            if completed { time["completed"] = created + 5 }
            return ["info": ["role": role, "time": time], "parts": text.isEmpty ? [] : [["type": "text", "text": text]]]
        }
        let earlier = message("assistant", 10, completed: true, "上一輪的回覆")
        let greeting = message("assistant", 99, completed: true, "")
        let user = message("user", 100, completed: false, "這一輪")
        XCTAssertEqual(ZCode.turnResult(["messages": [earlier, greeting, user]]).done, false)
        let streaming = message("assistant", 101, completed: false, "寫到一半")
        XCTAssertEqual(ZCode.turnResult(["messages": [earlier, user, streaming]]).done, false)
        XCTAssertEqual(ZCode.turnResult(["messages": [earlier, user, streaming]]).text, "寫到一半")
        let done = message("assistant", 101, completed: true, "收到")
        XCTAssertEqual(ZCode.turnResult(["messages": [earlier, user, done]]).done, true)
        XCTAssertEqual(ZCode.turnResult(["messages": [earlier, user, done]]).text, "收到")
    }

    func testZCodeSessionIDsCanBeResumed() {
        XCTAssertEqual(SessionPlan(resumeID: "sess_3bf321fc-2447-4548-b571", fallbackPrompt: "").resumeID, "sess_3bf321fc-2447-4548-b571")
        XCTAssertNil(SessionPlan(resumeID: "--x", fallbackPrompt: "").resumeID)
        XCTAssertNil(SessionPlan(resumeID: "a_b; rm", fallbackPrompt: "").resumeID)
    }

    func testTheAppBundleResolvesToItsExecutableAndScript() throws {
        var settings = NativeSettings()
        settings.setPath("/Applications/ZCode.app", for: .zcode)
        let exe = URL(fileURLWithPath: "/Applications/ZCode.app/Contents/MacOS/ZCode")
        XCTAssertEqual(ZCode.script(for: exe).path, "/Applications/ZCode.app/Contents/Resources/glm/zcode.cjs")
        XCTAssertEqual(MemberTemplate.named("glm")?.suggestedExecutor, .zcode)
        XCTAssertFalse(Executor.zcode.acceptsFiles)
    }

    func testAPlainNodeIsPreferredSoToolServersStayOutOfTheDock() throws {
        let home = try home()
        let app = URL(fileURLWithPath: "/Applications/ZCode.app/Contents/MacOS/ZCode")
        let bin = home.appendingPathComponent(".volta/bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let node = bin.appendingPathComponent("node")
        try "#!/bin/sh\n".write(to: node, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: node.path)
        // Homebrew's real node may exist on this Mac; only the fake one answers here.
        func versions(_ answer: String?) -> (URL) -> String? { { $0.path == node.path ? answer : nil } }

        let modern = ZCode.runtime(app: app, home: home.path, nodeVersion: versions("v26.13.0\n"))
        XCTAssertEqual(modern, ZCode.Runtime(executable: node, isElectron: false))
        XCTAssertEqual(ZCode.runtime(app: app, home: home.path, nodeVersion: versions("v18.20.1")).isElectron, true, "too old")
        XCTAssertEqual(ZCode.runtime(app: app, home: home.path, nodeVersion: versions(nil)).executable, app, "broken node")
        XCTAssertEqual(ZCode.nodeMajor("v22.1.0"), 22)
        XCTAssertNil(ZCode.nodeMajor("garbage"))
    }

    // MARK: Live: the real ZCode app and the owner's sign-in. Skipped unless AIROOM_LIVE_CLI=1.

    private func live() throws -> (URL, ZCode.Auth) {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["AIROOM_LIVE_CLI"] == "1")
        let exe = URL(fileURLWithPath: "/Applications/ZCode.app/Contents/MacOS/ZCode")
        try XCTSkipUnless(FileManager.default.isExecutableFile(atPath: exe.path))
        return (exe, try XCTUnwrap(ZCode.loadAuth(appVersion: ZCode.appVersion(for: exe))))
    }

    private func turn(_ exe: URL, _ auth: ZCode.Auth, _ prompt: String, in dir: URL, access: ExecutionAccess,
                      resume: String? = nil) throws -> CLIRunResult {
        try ZCode.runTurn(executable: exe, auth: auth, prompt: prompt, cwd: dir, model: "", effort: "low", access: access,
                          resumeID: resume, environment: PrivacyGuard.cliEnvironment(from: ProcessInfo.processInfo.environment, executable: exe),
                          register: { _ in }, delivered: {}, onUpdate: { _ in })
    }

    func testLiveReplyAndResumeAcrossProcesses() throws {
        let (exe, auth) = try live()
        let dir = try home()
        let first = try turn(exe, auth, "記住暗號：檸檬。只回覆兩個字：收到", in: dir, access: .readOnly)
        print("LIVE first:", first.text, first.sessionID ?? "-")
        XCTAssertFalse(first.text.isEmpty)
        let id = try XCTUnwrap(first.sessionID)
        let second = try turn(exe, auth, "剛才的暗號是什麼？只回覆暗號", in: dir, access: .readOnly, resume: id)
        print("LIVE resume:", second.text)
        XCTAssertTrue(second.text.contains("檸檬"))
    }

    func testLiveReadOnlyCannotWriteOrRunCommands() throws {
        let (exe, auth) = try live()
        let dir = try home()
        try "secret: 42".write(to: dir.appendingPathComponent("note.txt"), atomically: true, encoding: .utf8)
        let read = try turn(exe, auth, "讀 note.txt，只回覆裡面的數字", in: dir, access: .readOnly)
        print("LIVE read:", read.text)
        XCTAssertTrue(read.text.contains("42"))
        _ = try? turn(exe, auth, "用工具建立檔案 hack.txt 內容 x，再用終端機執行 touch hack2.txt。做不到就回覆 BLOCKED", in: dir, access: .readOnly)
        XCTAssertFalse(FileManager.default.fileExists(atPath: dir.appendingPathComponent("hack.txt").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: dir.appendingPathComponent("hack2.txt").path))
    }

    func testLiveWritingRunsEditButNeverRunCommands() throws {
        let (exe, auth) = try live()
        let dir = try home()
        let reply = try? turn(exe, auth, "建立檔案 ok.txt，內容是 hi。接著用終端機執行 touch cmd.txt。完成後回覆 DONE", in: dir, access: .stagedWrite)
        print("LIVE write:", reply?.text ?? "(no reply)")
        XCTAssertEqual(try? String(contentsOf: dir.appendingPathComponent("ok.txt"), encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines), "hi")
        XCTAssertFalse(FileManager.default.fileExists(atPath: dir.appendingPathComponent("cmd.txt").path), "shell commands are refused")
    }

    func testLiveWritingRunsCannotWriteOutsideTheWorkspace() throws {
        let (exe, auth) = try live()
        let dir = try home(), outside = try home()
        let target = outside.appendingPathComponent("escaped.txt")
        let reply = try? turn(exe, auth, "建立檔案 \(target.path)，內容寫 hi；再建立檔案 inside.txt，內容寫 ok。完成後回覆 DONE", in: dir, access: .stagedWrite)
        print("LIVE outside:", reply?.text ?? "(no reply)")
        XCTAssertFalse(FileManager.default.fileExists(atPath: target.path), "a path outside the workspace is refused")
        XCTAssertTrue(FileManager.default.fileExists(atPath: dir.appendingPathComponent("inside.txt").path))
    }
}
#endif
