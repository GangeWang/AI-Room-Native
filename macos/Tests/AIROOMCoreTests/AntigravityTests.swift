import XCTest
@testable import AIROOMCore

/// Event lines below are shaped like real `agy` 1.5.1 stream-json output.
final class AntigravityTests: XCTestCase {
    private let initLine = #"{"event":"init","conversation_id":"c15f5fa4-f902","init":{"cwd":"/tmp/x","tools":["read"]}}"#

    private func delta(_ text: String, type: String = "agent_response") -> String {
        #"{"event":"step_update","step_update":{"conversation_id":"c15f5fa4-f902","step_index":1,"state":"ACTIVE","step_type":"\#(type)","text_delta":"\#(text)"}}"#
    }

    func testReadOnlyRunsCannotEdit() {
        for access in [ExecutionAccess.readOnly, .readFiles] {
            let args = Antigravity.arguments(access: access, options: CLIRunOptions())
            // Default mode: edits and commands need an approval, which headless runs refuse.
            XCTAssertFalse(args.contains("--mode"), "plan mode reads through refused commands")
            XCTAssertTrue(args.contains("--sandbox"))
            XCTAssertFalse(args.contains("--disable-slash-commands"))
            let settings = args[args.firstIndex(of: "--settings")! + 1]
            XCTAssertTrue(settings.contains(#""toolPermission":"request-review""#))
            XCTAssertFalse(settings.contains("allow\""), "no tool is pre-approved")
        }
    }

    func testWritingRunsStaySandboxedAndNeverSkipPermissions() {
        let args = Antigravity.arguments(access: .stagedWrite, options: CLIRunOptions())
        XCTAssertEqual(args[args.firstIndex(of: "--mode")! + 1], "accept-edits")
        XCTAssertTrue(args.contains("--sandbox"))
        XCTAssertFalse(args.contains("--dangerously-skip-permissions"))
        XCTAssertFalse(args.contains("--disable-slash-commands"), "it makes agy ignore --mode")
        let settings = args[args.firstIndex(of: "--settings")! + 1]
        XCTAssertTrue(settings.contains("request-review"))
        XCTAssertTrue(settings.contains(#""allowNonWorkspaceAccess":false"#))
    }

    func testThePromptNeverGoesOnTheCommandLine() throws {
        let secret = "只有 stdin 看得到"
        var options = CLIRunOptions()
        options.model = "gemini-3.8-flash-high"
        options.session = SessionPlan(resumeID: "c15f5fa4-f902", fallbackPrompt: secret)
        let args = CommandPlan.arguments(for: .antigravity, access: .readOnly, options: options)
        XCTAssertFalse(args.joined(separator: " ").contains(secret))
        XCTAssertEqual(args[args.firstIndex(of: "--model")! + 1], "gemini-3.8-flash-high")
        XCTAssertEqual(args[args.firstIndex(of: "--conversation")! + 1], "c15f5fa4-f902")

        let line = try Antigravity.userMessage(prompt: "第一行\n第二行「引號」\"x\"")
        XCTAssertEqual(line.filter { $0 == 0x0A }.count, 1, "one message must stay on one line")
        let value = try XCTUnwrap(JSONSerialization.jsonObject(with: line) as? [String: Any])
        XCTAssertEqual(value["event"] as? String, "user")
        XCTAssertEqual((value["message"] as? [String: Any])?["content"] as? String, "第一行\n第二行「引號」\"x\"")
    }

    func testStreamsTextAndPrefersTheFinalResponse() {
        var parser = CLIEventAccumulator(agent: .antigravity)
        XCTAssertNil(parser.consume(line: initLine))
        XCTAssertEqual(parser.sessionID, "c15f5fa4-f902")
        XCTAssertEqual(parser.consume(line: delta("先讀檔。")), "先讀檔。")
        XCTAssertNil(parser.consume(line: delta("ls", type: "tool")), "tool steps aren't answers")
        XCTAssertEqual(parser.consume(line: delta("收到")), "先讀檔。收到")
        XCTAssertEqual(parser.consume(line: #"{"event":"result","result":{"conversation_id":"c15f5fa4-f902","status":"SUCCESS","response":"收到\n"}}"#), "收到\n")
        XCTAssertEqual(parser.text, "收到\n")
        XCTAssertNil(parser.failure)
    }

    func testAFailedResultIsAFailure() {
        var parser = CLIEventAccumulator(agent: .antigravity)
        parser.consume(line: #"{"event":"result","result":{"status":"ERROR","response":"","error":"invalid model selection"}}"#)
        XCTAssertEqual(parser.failure, "invalid model selection")

        var canceled = CLIEventAccumulator(agent: .antigravity)
        canceled.consume(line: #"{"event":"result","result":{"status":"CANCELED"}}"#)
        XCTAssertTrue(canceled.failure?.contains("CANCELED") == true)
    }

    func testAnEmptySuccessKeepsWhatWasStreamed() {
        var parser = CLIEventAccumulator(agent: .antigravity)
        parser.consume(line: delta("部分內容"))
        parser.consume(line: #"{"event":"result","result":{"status":"SUCCESS","response":""}}"#)
        XCTAssertEqual(parser.text, "部分內容")
    }

    func testASilentlyReplacedConversationCountsAsLost() {
        XCTAssertTrue(Antigravity.lostConversation(resumeID: "old", reported: "new"))
        XCTAssertTrue(Antigravity.lostConversation(resumeID: "old", reported: nil))
        XCTAssertFalse(Antigravity.lostConversation(resumeID: "same", reported: "same"))
        XCTAssertFalse(Antigravity.lostConversation(resumeID: nil, reported: "new"))
    }

    func testModelListAndSignInStatus() {
        let output = "Fetching available models...\ngemini-3.8-flash-high\tGemini 3.8 Flash (High)\nclaude-sonnet-4-6\tClaude Sonnet 4.6 (Thinking)\n"
        XCTAssertEqual(Antigravity.parseModels(output).map(\.id), ["gemini-3.8-flash-high", "claude-sonnet-4-6"])
        XCTAssertEqual(Antigravity.parseModels(output).first?.title, "Gemini 3.8 Flash (High)")
        XCTAssertEqual(Antigravity.status(modelsOutput: Data(output.utf8)), .authenticated)
        let signedOut = "Fetching available models...\nError: Please sign in to view available models.\n"
        XCTAssertEqual(Antigravity.status(modelsOutput: Data(signedOut.utf8)), .notLoggedIn)
    }

    func testOlderSettingsStillLoadAndTheGeminiLookUsesIt() throws {
        let old = try JSONDecoder().decode(NativeSettings.self, from: Data(#"{"claudePath":"/x"}"#.utf8))
        XCTAssertEqual(old.antigravityPath, "")
        var settings = old
        settings.setPath("/opt/agy", for: .antigravity)
        XCTAssertEqual(settings.configuredPath(for: .antigravity), "/opt/agy")
        XCTAssertEqual(MemberTemplate.named("gemini")?.suggestedExecutor, .antigravity)
        XCTAssertNil(MemberTemplate.named("gemini")?.plannedProgram)
        XCTAssertFalse(Executor.antigravity.acceptsFiles)
    }
}
