import Foundation
import AIROOMCore

// A member's body during a live show. The member's own agent CLI starts this
// as its MCP server (stdio). It speaks and moves only when the agent calls a
// tool, and holds the last pose (VTube Studio drops an injected value after
// ~1 s without a resend). It has no behavior of its own: no idle motion.
//
// stdout is the MCP channel. Nothing else may ever be written to it.

setvbuf(stderr, nil, _IONBF, 0)
func note(_ text: String) { FileHandle.standardError.write(Data("[airoom-body] \(text)\n".utf8)) }

let arguments = CommandLine.arguments
func option(_ name: String) -> String? {
    guard let index = arguments.firstIndex(of: name), index + 1 < arguments.count else { return nil }
    return arguments[index + 1]
}
guard let memberArg = option("--member"), let socketPath = option("--socket") else {
    note("usage: airoom-body --member <id> --socket <path> [--english]")
    exit(64)
}
let englishArg = arguments.contains("--english")
let secret = ProcessInfo.processInfo.environment["AIROOM_BODY_SECRET"] ?? ""

/// The local link to AI Room (the floor manager).
final class RoomLink: @unchecked Sendable {
    private let fd: Int32
    private let lock = NSLock()
    init?(path: String) {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8)
        guard bytes.count < MemoryLayout.size(ofValue: address.sun_path) else { close(fd); return nil }
        withUnsafeMutableBytes(of: &address.sun_path) { raw in
            raw.copyBytes(from: bytes)
            raw[bytes.count] = 0
        }
        let size = socklen_t(MemoryLayout<sockaddr_un>.size)
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, size) }
        }
        guard result == 0 else { close(fd); return nil }
        self.fd = fd
    }

    func send(_ message: LiveBodyMessage) {
        guard let data = try? message.line() else { return }
        lock.lock(); defer { lock.unlock() }
        data.withUnsafeBytes { raw in
            var offset = 0
            while offset < raw.count {
                let written = write(fd, raw.baseAddress! + offset, raw.count - offset)
                if written <= 0 { return }
                offset += written
            }
        }
    }

    /// Blocks; calls `handle` for each message until the socket closes.
    func readLoop(_ handle: @escaping (LiveBodyMessage) -> Void) {
        var buffer = Data()
        var chunk = [UInt8](repeating: 0, count: 16_384)
        while true {
            let count = read(fd, &chunk, chunk.count)
            if count <= 0 { return }
            buffer.append(contentsOf: chunk[0..<count])
            while let newline = buffer.firstIndex(of: 0x0A) {
                let line = Data(buffer[buffer.startIndex..<newline])
                buffer.removeSubrange(buffer.startIndex...newline)
                if let message = LiveBodyMessage.parse(line) { handle(message) }
            }
        }
    }
}

@MainActor
final class Body {
    let member: MemberID
    var english: Bool
    let room: RoomLink?
    var config: LiveBodyConfig?
    let vts = VTubeStudioClient()
    var connected = false
    var modelName = ""
    var hotkeys: [VTSHotkey] = []
    var animator = LivePoseAnimator(idle: false)
    let clock = Date()
    var floor = false
    var speaking = false
    var vtsNote = ""
    #if canImport(AVFoundation)
    let speech = SpeechPlayer()
    #endif

    init(member: MemberID, english: Bool, room: RoomLink?) {
        self.member = member; self.english = english; self.room = room
    }

    var now: TimeInterval { Date().timeIntervalSince(clock) }
    func t(_ zh: String, _ en: String) -> String { english ? en : zh }

    // MARK: Room

    func received(_ message: LiveBodyMessage) {
        switch message.kind {
        case .config:
            guard config == nil, let config = message.config else { return }
            self.config = config
            english = config.english
            if config.port > 0 { Task { await self.connectVTS() } }
        case .floor:
            floor = message.granted ?? false
            #if canImport(AVFoundation)
            if !floor && speaking { speech.stop() }
            #endif
        default: break
        }
    }

    func roomClosed() {
        floor = false
        #if canImport(AVFoundation)
        speech.stop()
        #endif
    }

    // MARK: VTube Studio

    func connectVTS() async {
        guard let config else { return }
        var token = config.token
        var attempt = 0
        while !connected {
            do {
                let result = try await vts.connect(port: config.port, token: token, plugin: config.plugin,
                                                   mayAsk: attempt == 0 && token.isEmpty, approvalTimeout: 90)
                if result.token != token {
                    token = result.token
                    room?.send(LiveBodyMessage(kind: .token, member: member.rawValue, token: token))
                }
                modelName = result.model.name
                connected = true
                vtsNote = ""
            } catch {
                vtsNote = error.localizedDescription
                attempt += 1
                room?.send(LiveBodyMessage(kind: .status, member: member.rawValue,
                                           text: t("身體連不上 VTube Studio：", "Body cannot reach VTube Studio: ") + vtsNote))
                try? await Task.sleep(nanoseconds: UInt64(min(10, 1 + attempt)) * 1_000_000_000)
            }
        }
        await vts.setOnConnectionLost { [weak self] in
            Task { @MainActor in
                guard let self else { return }
                self.connected = false
                self.room?.send(LiveBodyMessage(kind: .status, member: self.member.rawValue,
                                                text: self.t("身體和 VTube Studio 斷線，重連中", "Body lost VTube Studio; reconnecting")))
                await self.connectVTS()
            }
        }
        let explain = ["AI Room: left arm", "AI Room: right arm", "AI Room: left elbow", "AI Room: right elbow"]
        for (name, text) in zip(config.arms, explain) {
            try? await vts.createParameter(name, explanation: text, min: -1, max: 1, defaultValue: 0)
        }
        hotkeys = (try? await vts.hotkeys()) ?? []
        room?.send(LiveBodyMessage(kind: .status, member: member.rawValue,
                                   text: t("身體已連上 VTube Studio（\(modelName)）", "Body connected to VTube Studio (\(modelName))")))
    }

    /// Holds the pose: VTube Studio needs every value at least once a second.
    func holdLoop() async {
        while true {
            try? await Task.sleep(nanoseconds: 33_000_000)
            guard connected, let config else { continue }
            let arms: LivePoseAnimator.ArmInputs? = config.arms.count == 4 ? {
                var inputs = LivePoseAnimator.ArmInputs(member: member)
                inputs.armL = config.arms[0]; inputs.armR = config.arms[1]
                inputs.elbowL = config.arms[2]; inputs.elbowR = config.arms[3]
                return inputs
            }() : nil
            await vts.inject(animator.frame(at: now, mouthParameter: config.mouthParameter, arms: arms))
        }
    }

    // MARK: Tools

    func call(_ name: String, _ arguments: [String: Any]) async -> (String, Bool) {
        switch LiveBodyTools.command(name: name, arguments: arguments) {
        case .failure(.unknownTool(let tool)): return (t("沒有這個工具：", "No such tool: ") + tool, true)
        case .failure(.badArguments(let why)): return (t("參數不對：", "Bad arguments: ") + why, true)
        case .success(.say(let text)): return await say(text)
        case .success(.pose(let pose)):
            animator.apply(pose, at: now)
            return (connected ? t("好，正在擺這個姿勢。", "OK, moving into the pose.")
                              : t("已記下姿勢，但身體還沒連上 VTube Studio，看不到。", "Pose kept, but VTube Studio is not connected, so it is not visible."), false)
        case .success(.expression(let wanted)):
            guard connected else { return (t("身體還沒連上 VTube Studio。", "VTube Studio is not connected."), true) }
            if hotkeys.isEmpty { hotkeys = (try? await vts.hotkeys()) ?? [] }
            guard let hotkey = LiveBodyTools.hotkey(named: wanted, in: hotkeys) else {
                return (t("找不到這個表情。可用的：", "No such expression. Available: ") + hotkeys.map(\.name).joined(separator: ", "), true)
            }
            do {
                try await vts.trigger(hotkeyID: hotkey.id)
                return (t("已觸發「\(hotkey.name)」", "Triggered \"\(hotkey.name)\"")
                        + (hotkey.toggles ? t("；再觸發一次會關掉。", "; trigger again to turn it off.") : "。"), false)
            } catch {
                return (error.localizedDescription, true)
            }
        case .success(.describe): return (describe(), false)
        }
    }

    func say(_ text: String) async -> (String, Bool) {
        guard floor else {
            return (t("現在不是你的發言時間，這句沒有說出來。等主持人說輪到你。", "You do not hold the floor; nothing was said. Wait until it is your turn."), true)
        }
        guard !speaking else { return (t("你正在說上一句，等它念完再說。", "You are still speaking; wait for that line to finish."), true) }
        speaking = true
        defer { speaking = false }
        room?.send(LiveBodyMessage(kind: .speech, member: member.rawValue, text: text))
        var completed = true
        #if canImport(AVFoundation)
        let character = LiveCharacterStage(agent: member, voiceIdentifier: config?.voiceIdentifier ?? "",
                                           speechRate: config?.speechRate ?? 0.5, pitch: config?.pitch ?? 1)
        await speech.speak(text, character: character, language: english ? .english : .zhTW) { [weak self] level in
            await MainActor.run { self?.animator.mouth = level }
        }
        animator.mouth = 0
        completed = floor
        #endif
        room?.send(LiveBodyMessage(kind: .speech, member: member.rawValue, text: text, done: true))
        return completed ? (t("說完了。", "Spoken."), false)
            : (t("說到一半被主持人收回了發言權。", "The host took the floor back mid-line."), true)
    }

    func describe() -> String {
        let v = animator.frame(at: now, mouthParameter: config?.mouthParameter ?? "MouthOpen")
        let pose = v.prefix(11).map { "\($0.0)=\(String(format: "%.2f", $0.1))" }.joined(separator: " ")
        var lines: [String] = []
        if config?.port ?? 0 == 0 {
            lines.append(t("你這次沒有 Live2D 模型，只有聲音；pose 和 expression 不會有畫面。", "You have no Live2D model this time, only a voice; pose and expression have no visible effect."))
        } else if connected {
            lines.append(t("VTube Studio：已連上，模型「\(modelName)」。", "VTube Studio: connected, model \"\(modelName)\"."))
        } else {
            lines.append(t("VTube Studio：尚未連上", "VTube Studio: not connected") + (vtsNote.isEmpty ? "" : "（\(vtsNote)）"))
        }
        lines.append(t("發言權：", "Floor: ") + (floor ? t("在你手上，可以 say。", "yours; you may say.") : t("不在你手上，say 會被拒絕；動作照樣可以做。", "not yours; say is refused, moving is fine.")))
        lines.append(t("表情熱鍵：", "Expression hotkeys: ") + (hotkeys.isEmpty ? t("（無）", "(none)") : hotkeys.map(\.name).joined(separator: ", ")))
        lines.append(t("目前姿勢：", "Current pose: ") + pose)
        lines.append(t("手臂：", "Arms: ") + ((config?.arms.count ?? 0) == 4 ? t("可以用 arm_left/arm_right/elbow_left/elbow_right。", "use arm_left/arm_right/elbow_left/elbow_right.") : t("這個模型沒有設定手臂。", "not set up on this model.")))
        lines.append(t("不是每個模型都做了所有部位的形變；擺了沒動，就是這個模型沒做那個部位。", "Not every model is rigged for every part; if a pose shows nothing, that part is not rigged."))
        return lines.joined(separator: "\n")
    }

    // MARK: MCP

    func rpc(_ message: [String: Any]) async {
        let method = message["method"] as? String
        guard let id = message["id"] else { return } // notifications need no reply
        switch method {
        case "initialize":
            let requested = (message["params"] as? [String: Any])?["protocolVersion"] as? String
            out(MCPWire.result(id: id, MCPWire.initializeResult(requested: requested, server: "airoom-body")))
        case "tools/list":
            out(MCPWire.result(id: id, ["tools": LiveBodyTools.definitions(english: english)]))
        case "tools/call":
            let params = message["params"] as? [String: Any] ?? [:]
            let (text, isError) = await call(params["name"] as? String ?? "", params["arguments"] as? [String: Any] ?? [:])
            out(MCPWire.result(id: id, MCPWire.toolResult(text, isError: isError)))
        case "ping":
            out(MCPWire.result(id: id, [:]))
        default:
            out(MCPWire.error(id: id, code: -32601, message: "Method not found"))
        }
    }

    func out(_ data: Data) { FileHandle.standardOutput.write(data) }
}

let room = RoomLink(path: socketPath)
if room == nil { note("cannot reach AI Room at \(socketPath); speaking is disabled") }
let body = MainActor.assumeIsolated { Body(member: MemberID(rawValue: memberArg), english: englishArg, room: room) }
room?.send(LiveBodyMessage(kind: .hello, member: memberArg, secret: secret))

if let room {
    Thread {
        room.readLoop { message in Task { @MainActor in body.received(message) } }
        Task { @MainActor in body.roomClosed() }
    }.start()
}

// MCP on stdin; each request is handled concurrently, so pose works mid-say.
Thread {
    var buffer = Data()
    var chunk = [UInt8](repeating: 0, count: 65_536)
    while true {
        let count = read(STDIN_FILENO, &chunk, chunk.count)
        if count <= 0 { break }
        buffer.append(contentsOf: chunk[0..<count])
        while let newline = buffer.firstIndex(of: 0x0A) {
            let line = Data(buffer[buffer.startIndex..<newline])
            buffer.removeSubrange(buffer.startIndex...newline)
            guard let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else { continue }
            Task { @MainActor in await body.rpc(object) }
        }
    }
    // The agent's CLI closed us: the show is over for this member.
    Task { @MainActor in
        body.roomClosed()
        await body.vts.disconnect()
        exit(0)
    }
}.start()

Task { @MainActor in await body.holdLoop() }
RunLoop.main.run()
