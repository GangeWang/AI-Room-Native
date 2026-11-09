import Foundation

/// One `zcode.cjs app-server` process per turn. Requests carry an id and get a
/// `result` (or `error`) back; the server also asks things of its host, and each
/// ask must be answered within 15 seconds with `{"id": <same>, "result": {…}}`.
final class ZCodeAppServer {
    struct Failure: Error, LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    private let process: Process
    private let input: FileHandle
    private let output: FileHandle
    private let auth: ZCode.Auth
    private let lock = NSLock()
    private var nextID = 0
    private var waiters: [Int: DispatchSemaphore] = [:]
    private var replies: [Int: Result<[String: Any], Error>] = [:]
    private var reportedSession: String?
    private var turnFailure: String?
    private var ended = false
    /// AIROOM_ZCODE_TRACE=1 prints each message's method and id to stderr (never contents).
    private let tracing = ProcessInfo.processInfo.environment["AIROOM_ZCODE_TRACE"] == "1"
    /// Status words only: error messages and lifecycle states, never message contents.
    private static func traceDetail(_ message: [String: Any]) -> String {
        if let error = message["error"] as? [String: Any] { return "error: \(error["message"] ?? error)" }
        let params = message["params"] as? [String: Any] ?? [:]
        if message["method"] as? String == "session/lifecycle/changed" {
            return "state=\(params["state"] ?? "-") lastError=\(params["lastError"] ?? "-")"
        }
        if message["method"] as? String == "state.updated" { return "reason=\(params["reason"] ?? "-")" }
        return ""
    }
    private func trace(_ direction: String, _ message: [String: Any]) {
        guard tracing else { return }
        FileHandle.standardError.write(Data("zcode \(direction) id=\(message["id"].map { "\($0)" } ?? "-") method=\(message["method"] as? String ?? "-") result=\(message["result"] != nil) error=\(message["error"] != nil) \(Self.traceDetail(message))\n".utf8))
    }

    init(executable: URL, script: URL, cwd: URL, auth: ZCode.Auth, environment: [String: String]) {
        self.auth = auth
        process = Process()
        process.executableURL = executable
        process.arguments = [script.path, "app-server"]
        process.currentDirectoryURL = cwd
        var env = environment
        env["ELECTRON_RUN_AS_NODE"] = "1"   // the desktop's own runtime runs the CLI as Node
        env["ZCODE_BUILTIN_PROVIDER_CONFIG_FILE"] = auth.builtinFile.path
        env["ZCODE_PERSONAL_PROVIDER_CONFIG_FILE"] = (environment["HOME"] ?? NSHomeDirectory()) + "/.zcode/v2/provider_config.json"
        env["ZCODE_APP_VERSION"] = auth.appVersion
        if !auth.deviceMid.isEmpty { env["ZCODE_TELEMETRY_DEVICE_MID"] = auth.deviceMid }
        process.environment = env
        let stdin = Pipe(), stdout = Pipe()
        process.standardInput = stdin
        process.standardOutput = stdout
        process.standardError = FileHandle.nullDevice
        input = stdin.fileHandleForWriting
        output = stdout.fileHandleForReading
    }

    var underlyingProcess: Process { process }
    var sessionFromServer: String? { lock.lock(); defer { lock.unlock() }; return reportedSession }
    var failure: String? { lock.lock(); defer { lock.unlock() }; return turnFailure }

    func start() throws {
        try process.run()
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in self?.readLoop() }
    }

    func stop() {
        if process.isRunning { process.terminate() }
        try? input.close()
    }

    // MARK: Outgoing

    @discardableResult
    func request(_ method: String, _ params: [String: Any], timeout: TimeInterval = 60) throws -> [String: Any] {
        let (id, signal) = try send(method, params)
        if signal.wait(timeout: .now() + timeout) == .timedOut {
            lock.lock(); waiters.removeValue(forKey: id); lock.unlock()
            throw Failure(message: "ZCode 沒有在 \(Int(timeout)) 秒內回應 \(method)")
        }
        lock.lock(); let reply = replies.removeValue(forKey: id); lock.unlock()
        switch reply {
        case .success(let value): return value
        case .failure(let error): throw error
        case nil: throw Failure(message: "ZCode 結束了，沒有回應 \(method)")
        }
    }

    /// Sends without waiting; a failure is recorded as the turn's failure.
    func post(_ method: String, _ params: [String: Any]) throws {
        let (id, signal) = try send(method, params)
        DispatchQueue.global(qos: .utility).async { [weak self] in
            _ = signal.wait(timeout: .now() + 3_600)
            guard let self else { return }
            self.lock.lock()
            if case .failure(let error) = self.replies.removeValue(forKey: id) { self.turnFailure = self.turnFailure ?? error.localizedDescription }
            self.lock.unlock()
        }
    }

    func notify(_ method: String, _ params: [String: Any]) throws {
        try write(["method": method, "params": params])
    }

    private func send(_ method: String, _ params: [String: Any]) throws -> (Int, DispatchSemaphore) {
        let signal = DispatchSemaphore(value: 0)
        lock.lock()
        nextID += 1
        let id = nextID
        waiters[id] = signal
        lock.unlock()
        try write(["id": id, "method": method, "params": params])
        return (id, signal)
    }

    private func write(_ message: [String: Any]) throws {
        trace(">>", message)
        var line = try JSONSerialization.data(withJSONObject: message)
        line.append(0x0A)
        try input.write(contentsOf: line)
    }

    // MARK: Incoming

    private func readLoop() {
        var buffer = Data()
        // availableData returns whatever has arrived; read(upToCount:) would wait for a
        // full buffer, and the server's 15-second questions would go unanswered.
        while case let chunk = output.availableData, !chunk.isEmpty {
            buffer.append(chunk)
            while let newline = buffer.firstIndex(of: 0x0A) {
                let line = buffer.prefix(upTo: newline)
                buffer.removeSubrange(...newline)
                if let message = try? JSONSerialization.jsonObject(with: line) as? [String: Any] { handle(message) }
            }
            if buffer.count > 16 * 1_024 * 1_024 { break } // a line that never ends
        }
        // The process is gone: wake everyone still waiting.
        lock.lock()
        ended = true
        let pending = waiters
        waiters.removeAll()
        lock.unlock()
        pending.values.forEach { $0.signal() }
    }

    private func handle(_ message: [String: Any]) {
        trace("<<", message)
        let method = message["method"] as? String
        if method == nil, let id = (message["id"] as? NSNumber)?.intValue {
            let reply: Result<[String: Any], Error>
            if let error = message["error"] as? [String: Any] {
                reply = .failure(Failure(message: PrivacyGuard.redact(error["message"] as? String ?? "ZCode 回報錯誤")))
            } else {
                reply = .success(message["result"] as? [String: Any] ?? [:])
            }
            lock.lock()
            let waiter = waiters.removeValue(forKey: id)
            if waiter != nil { replies[id] = reply }
            lock.unlock()
            waiter?.signal()
            return
        }
        guard let method else { return }
        let params = message["params"] as? [String: Any] ?? [:]
        if let id = message["id"] {
            answer(id: id, method: method, params: params)
        } else if method == "state.updated", params["reason"] as? String == "prompt_failed" {
            lock.lock(); turnFailure = turnFailure ?? "ZCode 這一輪的模型請求失敗（prompt_failed）"; lock.unlock()
        }
    }

    /// The server's asks. Permission requests are refused, so nothing beyond the
    /// session's mode (plan / edit) ever runs; questions to the user get no answer.
    private func answer(id: Any, method: String, params: [String: Any]) {
        let result: [String: Any]
        switch method {
        case "session/requestRuntimePreferences":
            if let session = params["sessionId"] as? String {
                lock.lock(); if reportedSession == nil { reportedSession = session }; lock.unlock()
            }
            result = ["nativeSearchEnhancementsEnabled": false]
        case "interaction/requestProviderRuntimeHeaders":
            result = ["headersApplied": true,
                      "requestAuth": ["apiKey": auth.token, "headers": ["Authorization": "Bearer \(auth.token)"]]]
        case "interaction/requestPermission":
            // Validated strictly: {decision, reason?, …}; an empty or extra-keyed answer is rejected.
            result = ["decision": "deny", "reason": "AIROOM 不允許這個動作：成員只能在自己的權限範圍內工作。"]
        default:
            result = [:]
        }
        try? write(["id": id, "result": result])
    }
}

extension ZCode {
    /// Runs one turn and returns its reply. `register` hands the process to the
    /// caller so Stop can end it.
    static func runTurn(executable: URL, auth: Auth, prompt: String, cwd: URL, model: String, effort: String,
                        access: ExecutionAccess, resumeID: String?, environment: [String: String],
                        register: (Process) -> Void, delivered: () -> Void,
                        onUpdate: (String) -> Void) throws -> CLIRunResult {
        let server = ZCodeAppServer(executable: executable, script: script(for: executable), cwd: cwd,
                                    auth: auth, environment: environment)
        try server.start()
        register(server.underlyingProcess)
        defer { server.stop() }

        var session: String
        if let resumeID {
            do { try server.request("session/resume", ["sessionId": resumeID]) }
            catch { throw SessionUnavailable() }
            session = resumeID
        } else {
            let created = try server.request("session/create", ["workspace": ["workspacePath": cwd.path,
                                                                                "workspaceKey": cwd.path.lowercased()]])
            // The id arrives in the runtime-preferences ask, not in create's result.
            let deadline = Date().addingTimeInterval(10)
            var found = server.sessionFromServer ?? (created["sessionId"] as? String)
            while found == nil, Date() < deadline {
                Thread.sleep(forTimeInterval: 0.1)
                found = server.sessionFromServer
            }
            guard let found else { throw ZCodeAppServer.Failure(message: "ZCode 沒有回傳工作階段 ID") }
            session = found
        }

        try server.request("provider/updateAccountConfig", [
            "revision": "airoom:\(Int64(Date().timeIntervalSince1970 * 1_000))",
            "basedOnZCodeBuiltinRevision": auth.basedOnRevision,
            "providers": [providerID: ["builtinModelIds": auth.modelIDs,
                                       "access": ["type": "zhipu-account", "entitled": true]]],
            "states": [providerID: ["availability": "available", "entitled": true, "current": true]],
        ])
        let chosen = model.isEmpty ? defaultModel : model
        let level = efforts.contains(effort) ? effort : "max"
        try server.request("session/setModel", ["sessionId": session, "persistAsWorkspaceLastUsed": false,
                                                "model": ["providerId": providerID, "modelId": chosen,
                                                          "options": ["reasoningLevel": level]]])
        try server.request("session/setMode", ["sessionId": session, "mode": mode(for: access)])
        // Subscribing must reach the server before the send, or the turn's notifications never arrive.
        try server.notify("session/subscribe", ["sessionId": session])
        try server.post("session/send", ["sessionId": session, "content": prompt])
        delivered()

        var last = ""
        var finished = false
        let deadline = Date().addingTimeInterval(60 * 60)
        while !finished {
            Thread.sleep(forTimeInterval: 0.9)
            if let failure = server.failure { throw NativeCLIError.processFailed("ZCode 執行失敗：\(failure)") }
            guard server.underlyingProcess.isRunning else { throw NativeCLIError.processFailed("ZCode 在回覆前就結束了") }
            guard Date() < deadline else { throw NativeCLIError.processFailed("ZCode 超過一小時沒有完成") }
            guard let snapshot = try? server.request("session/messages", ["sessionId": session], timeout: 30) else { continue }
            let (done, text) = turnResult(snapshot)
            if !text.isEmpty, text != last { last = text; onUpdate(PrivacyGuard.redact(text)) }
            finished = done
        }
        let final = turnResult(try server.request("session/messages", ["sessionId": session], timeout: 30)).text
        let reply = final.isEmpty ? last : final
        guard !reply.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw NativeCLIError.noResponse("ZCode") }
        return CLIRunResult(text: PrivacyGuard.redact(reply), model: chosen, sessionID: session)
    }
}
