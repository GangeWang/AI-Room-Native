import Foundation

/// How a member's body is started as an MCP server by that member's own CLI.
public struct LiveBodyLaunch: Sendable, Equatable {
    public var command: String
    public var arguments: [String]
    public var environment: [String: String]
    public init(command: String, arguments: [String], environment: [String: String]) {
        self.command = command; self.arguments = arguments; self.environment = environment
    }
}

/// Command lines and configuration for a show-long agent session whose only
/// tools are its body. Pure, so the isolation rules are unit-tested.
public enum LiveAgentPlan {
    /// MCP tools can take as long as a spoken line.
    public static let toolTimeoutSeconds = 180

    public static func claudeArguments(body: LiveBodyLaunch, model: String, effort: String) -> [String] {
        var server: [String: Any] = ["command": body.command, "args": body.arguments]
        if !body.environment.isEmpty { server["env"] = body.environment }
        let config = ["mcpServers": [LiveBodyTools.server: server]]
        let json = (try? JSONSerialization.data(withJSONObject: config, options: [.sortedKeys, .withoutEscapingSlashes]))
            .flatMap { String(data: $0, encoding: .utf8) } ?? #"{"mcpServers":{}}"#
        // No built-in tools at all; only this MCP server, and only its tools are allowed.
        var args = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                    "--tools", "", "--strict-mcp-config", "--mcp-config", json,
                    "--allowedTools", "mcp__\(LiveBodyTools.server)"]
        if !model.isEmpty { args += ["--model", model] }
        if !effort.isEmpty { args += ["--effort", effort] }
        return args
    }

    /// Codex features that bring their own tools; all off for a live session.
    public static let codexFeaturesOff = [
        "plugins", "apps", "computer_use", "browser_use", "browser_use_external", "in_app_browser",
        "multi_agent", "shell_tool", "unified_exec", "image_generation", "code_mode_host", "hooks",
        "skill_mcp_dependency_install", "tool_suggest", "goals", "sleep_tool", "web_search_request",
    ]

    /// `thread/start` config. `userServers` are the MCP servers in the user's
    /// own Codex config (from `codex -c features.plugins=false mcp list --json`);
    /// each is turned off so the body is the only tool.
    public static func codexThreadConfig(body: LiveBodyLaunch, userServers: [String]) -> [String: Any] {
        var config: [String: Any] = ["web_search": "disabled"]
        for feature in codexFeaturesOff { config["features.\(feature)"] = false }
        for name in userServers where name != LiveBodyTools.server { config["mcp_servers.\(name).enabled"] = false }
        let key = "mcp_servers.\(LiveBodyTools.server)"
        config["\(key).command"] = body.command
        config["\(key).args"] = body.arguments
        if !body.environment.isEmpty { config["\(key).env"] = body.environment }
        config["\(key).tool_timeout_sec"] = toolTimeoutSeconds
        return config
    }

    /// Enabled servers from `codex mcp list --json`.
    public static func enabledCodexServers(fromListJSON data: Data) -> [String] {
        guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
        return list.compactMap { entry in
            guard let name = entry["name"] as? String, (entry["enabled"] as? Bool) ?? true else { return nil }
            return name
        }
    }

    public static func claudeUserMessage(_ text: String) -> Data {
        let object: [String: Any] = ["type": "user", "message": ["role": "user", "content": text]]
        var data = (try? JSONSerialization.data(withJSONObject: object, options: [.withoutEscapingSlashes])) ?? Data()
        data.append(0x0A)
        return data
    }
}

/// One member's agent, alive for the whole show. AI Room feeds it what
/// happens in the room; one message is in flight at a time and whatever
/// arrives meanwhile is delivered together when that turn ends.
public final class LiveAgentSession: @unchecked Sendable {
    public enum Kind: Sendable { case claude, codex }

    public let kind: Kind
    private let binary: URL
    private let cwd: URL
    private let body: LiveBodyLaunch
    private let model: String
    private let effort: String
    private let lock = NSLock()
    private var process: Process?
    private var input: FileHandle?
    private var ready = false
    private var busy = false
    private var stopped = false
    private var pending: [(id: UUID, text: String)] = []
    private var inFlight: [UUID] = []
    private var waiters: [UUID: CheckedContinuation<Bool, Never>] = [:]
    // Codex
    private var nextID = 10
    private var threadID: String?
    private var userServers: [String] = []

    /// Called on the session's reader thread.
    public var onExit: (@Sendable (String) -> Void)?
    public var onNote: (@Sendable (String) -> Void)?

    public init(kind: Kind, binary: URL, cwd: URL, body: LiveBodyLaunch, model: String = "", effort: String = "") {
        self.kind = kind; self.binary = binary; self.cwd = cwd; self.body = body
        self.model = model; self.effort = effort
    }

    public var isBusy: Bool { lock.lock(); defer { lock.unlock() }; return busy || !pending.isEmpty }

    public func start() throws {
        if kind == .codex { userServers = Self.codexUserServers(binary: binary, cwd: cwd) }
        let process = Process()
        process.executableURL = binary
        process.currentDirectoryURL = cwd
        var environment = PrivacyGuard.cliEnvironment(from: ProcessInfo.processInfo.environment, executable: binary)
        environment["MCP_TOOL_TIMEOUT"] = String(LiveAgentPlan.toolTimeoutSeconds * 1000)
        process.environment = environment
        switch kind {
        case .claude: process.arguments = LiveAgentPlan.claudeArguments(body: body, model: model, effort: effort)
        case .codex: process.arguments = ["app-server", "--listen", "stdio://"]
        }
        let stdin = Pipe(), stdout = Pipe()
        process.standardInput = stdin
        process.standardOutput = stdout
        process.standardError = FileHandle.nullDevice
        process.terminationHandler = { [weak self] proc in
            self?.finished("exited (\(proc.terminationStatus))")
        }
        try process.run()
        try? stdin.fileHandleForReading.close()
        try? stdout.fileHandleForWriting.close()
        lock.lock()
        self.process = process
        self.input = stdin.fileHandleForWriting
        lock.unlock()
        let fd = stdout.fileHandleForReading.fileDescriptor
        let reader = Thread { [weak self] in self?.readLoop(fd: fd) }
        reader.name = "AIROOM live agent"
        reader.start()
        switch kind {
        case .claude:
            lock.lock(); ready = true; lock.unlock()
            pump()
        case .codex:
            write(Self.codexLine(["id": 1, "method": "initialize",
                                   "params": ["clientInfo": ["name": "airoom_native", "title": "AI Room Native", "version": "0.2.0"]]]))
        }
    }

    /// Queues `text`; it is sent as soon as the agent is free.
    public func send(_ text: String) { _ = enqueue(text) }

    /// Sends `text` and waits for the end of the turn that carries it.
    /// Returns false on timeout or when the session ends first.
    public func sendAndWait(_ text: String, timeout: TimeInterval) async -> Bool {
        let id = enqueue(text)
        let timer = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(max(1, timeout) * 1_000_000_000))
            self?.resume(id, false)
        }
        defer { timer.cancel() }
        return await withCheckedContinuation { continuation in
            lock.lock()
            if stopped { lock.unlock(); continuation.resume(returning: false); return }
            waiters[id] = continuation
            lock.unlock()
        }
    }

    public func stop() {
        lock.lock()
        stopped = true
        let process = self.process
        let input = self.input
        let all = waiters; waiters = [:]
        lock.unlock()
        for (_, waiter) in all { waiter.resume(returning: false) }
        try? input?.close()
        if let process, process.isRunning {
            process.terminate()
            DispatchQueue.global().asyncAfter(deadline: .now() + 2) {
                if process.isRunning { kill(process.processIdentifier, SIGKILL) }
            }
        }
    }

    // MARK: Delivery

    private func enqueue(_ text: String) -> UUID {
        let id = UUID()
        lock.lock(); pending.append((id, text)); lock.unlock()
        pump()
        return id
    }

    private func pump() {
        lock.lock()
        guard ready, !busy, !stopped, !pending.isEmpty else { lock.unlock(); return }
        let batch = pending; pending = []
        busy = true
        inFlight = batch.map(\.id)
        let text = LiveBodyPrompt.batch(batch.map(\.text))
        lock.unlock()
        switch kind {
        case .claude:
            write(LiveAgentPlan.claudeUserMessage(text))
        case .codex:
            lock.lock()
            let id = nextID; nextID += 1
            let thread = threadID
            lock.unlock()
            guard let thread else { return }
            var params: [String: Any] = ["threadId": thread, "input": [["type": "text", "text": text]]]
            if !model.isEmpty { params["model"] = model }
            if !effort.isEmpty { params["effort"] = effort }
            write(Self.codexLine(["id": id, "method": "turn/start", "params": params]))
        }
    }

    private func turnEnded() {
        lock.lock()
        busy = false
        let done = inFlight; inFlight = []
        lock.unlock()
        for id in done { resume(id, true) }
        pump()
    }

    private func resume(_ id: UUID, _ value: Bool) {
        lock.lock()
        let waiter = waiters.removeValue(forKey: id)
        lock.unlock()
        waiter?.resume(returning: value)
    }

    private func finished(_ reason: String) {
        lock.lock()
        let wasStopped = stopped
        stopped = true
        let all = waiters; waiters = [:]
        lock.unlock()
        for (_, waiter) in all { waiter.resume(returning: false) }
        if !wasStopped { onExit?(reason) }
    }

    private func write(_ data: Data) {
        lock.lock(); let input = self.input; lock.unlock()
        do { try input?.write(contentsOf: data) } catch { onNote?("write failed: \(error.localizedDescription)") }
    }

    // MARK: Reading

    private func readLoop(fd: Int32) {
        var buffer = Data()
        var chunk = [UInt8](repeating: 0, count: 65_536)
        while true {
            let count = read(fd, &chunk, chunk.count)
            if count <= 0 { break }
            buffer.append(contentsOf: chunk[0..<count])
            while let newline = buffer.firstIndex(of: 0x0A) {
                let line = buffer[buffer.startIndex..<newline]
                buffer.removeSubrange(buffer.startIndex...newline)
                if let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any] { handle(object) }
            }
        }
    }

    private func handle(_ event: [String: Any]) {
        switch kind {
        case .claude:
            let type = event["type"] as? String
            if type == "result" { turnEnded() }
            if type == "system", event["subtype"] as? String == "init",
               let servers = event["mcp_servers"] as? [[String: Any]] {
                let body = servers.first { $0["name"] as? String == LiveBodyTools.server }
                if (body?["status"] as? String) != "connected" { onNote?("body tool not connected: \(body?["status"] ?? "missing")") }
            }
        case .codex:
            handleCodex(event)
        }
    }

    private func handleCodex(_ frame: [String: Any]) {
        if let method = frame["method"] as? String, let id = frame["id"] {
            // A server request (approval, elicitation): never granted here.
            if let line = try? CodexServerWire.rejectServerRequest(id: id, method: method) { write(line) }
            return
        }
        if let id = frame["id"] as? Int {
            if let error = frame["error"] as? [String: Any] {
                onNote?("codex error: \(error["message"] ?? error)")
                if id == 2 { finished("thread/start failed") }
                else { turnEnded() }
                return
            }
            if id == 1 {
                write(Self.codexLine(["method": "initialized"]))
                write(Self.codexLine(["id": 2, "method": "thread/start", "params": [
                    "cwd": cwd.path, "approvalPolicy": "never", "sandbox": "read-only", "ephemeral": true,
                    "config": LiveAgentPlan.codexThreadConfig(body: body, userServers: userServers),
                ]]))
            } else if id == 2, let result = frame["result"] as? [String: Any],
                      let thread = (result["thread"] as? [String: Any])?["id"] as? String {
                lock.lock(); threadID = thread; ready = true; lock.unlock()
                pump()
            }
            return
        }
        switch frame["method"] as? String {
        case "turn/completed": turnEnded()
        case "mcpServer/startupStatus/updated":
            if let params = frame["params"] as? [String: Any], params["name"] as? String == LiveBodyTools.server,
               params["status"] as? String == "failed" {
                onNote?("body tool failed to start")
            }
        default: break
        }
    }

    private static func codexLine(_ object: [String: Any]) -> Data {
        var data = (try? JSONSerialization.data(withJSONObject: object, options: [.withoutEscapingSlashes])) ?? Data()
        data.append(0x0A)
        return data
    }

    /// MCP servers in the user's own Codex config, so each can be switched off.
    private static func codexUserServers(binary: URL, cwd: URL) -> [String] {
        let process = Process()
        process.executableURL = binary
        process.currentDirectoryURL = cwd
        process.arguments = ["-c", "features.plugins=false", "mcp", "list", "--json"]
        process.environment = PrivacyGuard.cliEnvironment(from: ProcessInfo.processInfo.environment, executable: binary)
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        process.standardInput = FileHandle.nullDevice
        do { try process.run() } catch { return [] }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return LiveAgentPlan.enabledCodexServers(fromListJSON: data)
    }
}
