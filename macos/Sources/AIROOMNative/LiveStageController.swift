import Foundation
import AIROOMCore

/// Plays LiveEvents one at a time: emotion hotkey → TTS with lip sync → next.
/// Each co-host drives its own VTube Studio instance (own port, own model).
/// The live loop awaits `perform`, so two co-hosts never talk over each other.
@MainActor
final class LiveStageController: ObservableObject {
    enum Connection: Equatable {
        case off, connecting, connected(model: String), reconnecting(attempt: Int), failed(String)
    }

    @Published private(set) var connections: [MemberID: Connection] = [:]
    @Published private(set) var hotkeys: [MemberID: [VTSHotkey]] = [:]
    @Published private(set) var models: [MemberID: [VTSModel]] = [:]
    @Published private(set) var speaking: MemberID?
    @Published private(set) var subtitle = ""
    @Published private(set) var lastWarning: String?
    /// Set while "Open VTube Studio" is starting instances.
    @Published private(set) var launchStatus: String?

    /// What a member's link was opened with, kept so a dropped link can be
    /// reopened. Removed when the user disconnects.
    private struct Link {
        var port: Int
        var token: String
        var modelID: String
        var aiRig: Bool
        var mouthParameter: String
        var onNewToken: (String) -> Void
    }

    private var clients: [MemberID: VTubeStudioClient] = [:]
    private var links: [MemberID: Link] = [:]
    private var retries: [MemberID: Task<Void, Never>] = [:]
    private let speech = SpeechPlayer()
    /// Expression hotkey currently switched on per model (VTS expressions toggle).
    private var activeExpression: [MemberID: String] = [:]
    private var stopped = false
    /// Agents whose rig the stage drives: keyframes from their lines plus idle
    /// motion, sent as VTS tracking inputs at 30 Hz while connected.
    private var animators: [MemberID: LivePoseAnimator] = [:]
    private var lastEmotion: [MemberID: LiveEmotion] = [:]
    private var rigTasks: [MemberID: Task<Void, Never>] = [:]
    private static func now() -> TimeInterval { ProcessInfo.processInfo.systemUptime }

    /// Waits before each automatic reconnect: quickly at first, then every
    /// 10 s for about five minutes.
    static let reconnectDelays: [TimeInterval] = [1, 2, 5] + Array(repeating: 10, count: 27)

    func connection(_ agent: MemberID) -> Connection { connections[agent] ?? .off }
    func hotkeys(_ agent: MemberID) -> [VTSHotkey] { hotkeys[agent] ?? [] }
    func models(_ agent: MemberID) -> [VTSModel] { models[agent] ?? [] }
    func isConnected(_ agent: MemberID) -> Bool {
        if case .connected = connection(agent) { return true } else { return false }
    }
    func isBusy(_ agent: MemberID) -> Bool {
        switch connection(agent) {
        case .connecting, .reconnecting: return true
        default: return false
        }
    }

    // MARK: VTube Studio

    func connect(_ agent: MemberID, port: Int, token: String, modelID: String, aiRig: Bool,
                 mouthParameter: String, onNewToken: @escaping (String) -> Void) {
        retries.removeValue(forKey: agent)?.cancel()
        links[agent] = Link(port: port, token: token, modelID: modelID, aiRig: aiRig,
                            mouthParameter: mouthParameter, onNewToken: onNewToken)
        connections[agent] = .connecting
        Task {
            do { try await open(agent, mayAsk: true) }
            catch { if links[agent] != nil { connections[agent] = .failed(error.localizedDescription) } }
        }
    }

    func disconnect(_ agent: MemberID) {
        links[agent] = nil
        stopRig(agent)
        retries.removeValue(forKey: agent)?.cancel()
        activeExpression[agent] = nil
        hotkeys[agent] = nil
        connections[agent] = .off
        if let client = clients[agent] { Task { await client.disconnect() } }
    }

    /// Switches the member's VTS to `modelID` now (when connected) and keeps
    /// it for later connects.
    func useModel(_ agent: MemberID, modelID: String) {
        links[agent]?.modelID = modelID
        guard !modelID.isEmpty, isConnected(agent), let client = clients[agent] else { return }
        Task {
            do {
                try await loadIfNeeded(agent, modelID: modelID, client: client, current: nil)
                await refreshHotkeys(agent)
            } catch { lastWarning = error.localizedDescription }
        }
    }

    /// Turns the agent's rig control on or off (and picks up a new mouth input).
    func setRig(_ agent: MemberID, on: Bool, mouthParameter: String) {
        links[agent]?.aiRig = on
        links[agent]?.mouthParameter = mouthParameter
        if on && isConnected(agent) { startRig(agent) } else { stopRig(agent) }
    }

    func drivesRig(_ agent: MemberID) -> Bool { rigTasks[agent] != nil }

    private func startRig(_ agent: MemberID) {
        guard rigTasks[agent] == nil, let client = clients[agent] else { return }
        if animators[agent] == nil { animators[agent] = LivePoseAnimator() }
        rigTasks[agent] = Task { [weak self] in
            while !Task.isCancelled {
                guard let self, let animator = self.animators[agent], let link = self.links[agent] else { return }
                await client.inject(animator.frame(at: Self.now(), mouthParameter: link.mouthParameter))
                try? await Task.sleep(nanoseconds: 33_000_000)
            }
        }
    }

    private func stopRig(_ agent: MemberID) {
        rigTasks.removeValue(forKey: agent)?.cancel()
    }

    func refreshHotkeys(_ agent: MemberID) async {
        guard let client = clients[agent] else { return }
        do { hotkeys[agent] = try await client.hotkeys() }
        catch { lastWarning = error.localizedDescription }
    }

    func refreshModels(_ agent: MemberID) async {
        guard let client = clients[agent] else { return }
        do { models[agent] = try await client.availableModels() }
        catch { lastWarning = error.localizedDescription }
    }

    private func client(for agent: MemberID) async -> VTubeStudioClient {
        if let existing = clients[agent] { return existing }
        let client = VTubeStudioClient()
        clients[agent] = client
        await client.setOnConnectionLost { [weak self] in
            Task { @MainActor in self?.connectionLost(agent) }
        }
        return client
    }

    /// Connects, authenticates, puts on the member's model and reads its hotkeys.
    private func open(_ agent: MemberID, mayAsk: Bool) async throws {
        guard let link = links[agent] else { return }
        let client = await client(for: agent)
        activeExpression[agent] = nil
        let result = try await client.connect(port: link.port, token: link.token,
                                              plugin: VTSProtocol.pluginName(for: agent), mayAsk: mayAsk)
        guard links[agent] != nil else { await client.disconnect(); return }
        if result.token != link.token {
            links[agent]?.token = result.token
            link.onNewToken(result.token)
        }
        await refreshModels(agent)
        let wanted = links[agent]?.modelID ?? ""
        try await loadIfNeeded(agent, modelID: wanted, client: client, current: result.model)
        await refreshHotkeys(agent)
        if links[agent]?.aiRig == true { startRig(agent) }
    }

    private func loadIfNeeded(_ agent: MemberID, modelID: String, client: VTubeStudioClient,
                              current: VTSModel?) async throws {
        var model: VTSModel
        if let current { model = current } else { model = try await client.currentModel() }
        if !modelID.isEmpty && model.id != modelID {
            if models(agent).isEmpty || models(agent).contains(where: { $0.id == modelID }) {
                activeExpression[agent] = nil
                // A freshly started VTS refuses while its startup model is
                // still loading (error 154), so retry for about 15 s.
                var attempt = 0
                while true {
                    do { try await client.loadModel(id: modelID); break }
                    catch VTSError.api(let code, let message) {
                        attempt += 1
                        guard attempt < 10 else {
                            throw VTSError.api(code: code, message: code == 154
                                ? "VTube Studio 目前不能換模型，通常是 VTS 視窗裡有對話框或設定視窗開著（例如 Auto-Setup 詢問）。關掉後再按「連線」。"
                                : message)
                        }
                        try await Task.sleep(nanoseconds: 1_500_000_000)
                    }
                }
                // VTS answers before the new model has finished loading; wait for it
                // so the status and the hotkey list belong to the new model.
                model = try await client.currentModel()
                for _ in 0..<20 where !(model.loaded && model.id == modelID) {
                    try await Task.sleep(nanoseconds: 500_000_000)
                    model = try await client.currentModel()
                }
            } else {
                lastWarning = "這個 VTube Studio 裡找不到指定的模型，先沿用目前開著的模型。"
            }
        }
        // CurrentModelRequest can report a model's original name after it was
        // renamed in VTS; the model list carries the current one.
        let listed = models(agent).first(where: { $0.id == model.id })?.name
        connections[agent] = .connected(model: model.loaded ? (listed ?? model.name) : "")
    }

    private func connectionLost(_ agent: MemberID) {
        guard links[agent] != nil, retries[agent] == nil else { return }
        stopRig(agent)
        activeExpression[agent] = nil
        hotkeys[agent] = nil
        retries[agent] = Task { [weak self] in
            for (index, delay) in Self.reconnectDelays.enumerated() {
                guard let self, self.links[agent] != nil else { return }
                self.connections[agent] = .reconnecting(attempt: index + 1)
                try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
                guard !Task.isCancelled, self.links[agent] != nil else { return }
                do {
                    try await self.open(agent, mayAsk: false)
                    self.retries[agent] = nil
                    return
                } catch VTSError.needsApproval {
                    self.connections[agent] = .failed(VTSError.needsApproval.localizedDescription)
                    self.retries[agent] = nil
                    return
                } catch {
                    continue
                }
            }
            guard let self, self.links[agent] != nil else { return }
            self.connections[agent] = .failed("VTube Studio 斷線，自動重連約 5 分鐘仍連不上。開好 VTS 後請按「連線」。")
            self.retries[agent] = nil
        }
    }

    /// Starts native VTube Studio instances for these ports, lowest first.
    func launchVTubeStudio(ports: [Int], then: @escaping () -> Void) {
        guard launchStatus == nil else { return }
        guard let app = VTubeStudioLauncher.installedApp() else {
            lastWarning = VTubeStudioLauncher.Failure.notInstalled.localizedDescription
            return
        }
        lastWarning = nil
        launchStatus = "檢查 VTube Studio…"
        Task {
            do {
                try await VTubeStudioLauncher().start(ports: ports, app: app) { port in
                    await MainActor.run { self.launchStatus = "正在開啟 VTube Studio（連接埠 \(port)）…" }
                }
                launchStatus = nil
                then()
            } catch {
                launchStatus = nil
                lastWarning = error.localizedDescription
            }
        }
    }

    // MARK: Performance

    func stop() {
        stopped = true
        speech.stop()
    }

    func perform(_ events: [LiveEvent], stage: LiveStageSettings, language: InterfaceLanguage) async {
        stopped = false
        lastWarning = nil
        for event in events.sorted(by: { $0.sequence < $1.sequence }) {
            guard !stopped else { break }
            let character = stage.character(event.speaker)
            let client = stage.vtsEnabled && character.drivesAvatar && isConnected(event.speaker)
                ? clients[event.speaker] : nil
            if let client { await show(event.emotion, for: character, on: client) }
            let rig = client != nil && drivesRig(event.speaker)
            if rig, event.pose != nil || event.emotion != lastEmotion[event.speaker] {
                animators[event.speaker]?.apply(event.pose ?? LivePose.preset(for: event.emotion), at: Self.now())
            }
            lastEmotion[event.speaker] = event.emotion
            speaking = event.speaker
            subtitle = event.utterance
            guard stage.speechEnabled else {
                // Without a voice, still give each pose time to be seen.
                if rig { try? await Task.sleep(nanoseconds: 1_200_000_000) }
                continue
            }
            let mouth = character.mouthParameter
            let agent = event.speaker
            await speech.speak(event.utterance, character: character, language: language) { value in
                if rig { await MainActor.run { self.animators[agent]?.mouth = value } }
                else { await client?.inject([(mouth, value)]) }
            }
        }
        // Close the mouth and settle back to rest after the line.
        for agent in Set(events.map(\.speaker)) where drivesRig(agent) {
            animators[agent]?.mouth = 0
            animators[agent]?.relax(at: Self.now() + 0.6, over: 1.2)
            lastEmotion[agent] = nil
        }
        speaking = nil
    }

    /// Clears any expression left on by the previous show.
    func resetAvatars() async {
        for (agent, active) in activeExpression {
            activeExpression[agent] = nil
            try? await clients[agent]?.trigger(hotkeyID: active)
        }
    }

    func rehearse(_ agent: MemberID, name: String, stage: LiveStageSettings, language: InterfaceLanguage) {
        let line = language == .english
            ? "[happy] Hi everyone, I'm \(name)! [thinking] [pose head=-14,6,10 gaze=up in=0.7] This is a voice and expression test. [pose head=16,-2,-6 gaze=left smile=0.6 in=0.5] Over here, [surprised] can you see my mouth move? [shy] [pose head=8,-8,-10 gaze=down smile=0.8 in=0.8] Thanks for watching."
            : "[happy] 大家好，我是\(name)！[thinking] [pose head=-14,6,10 gaze=up in=0.7] 這是聲音和表情的測試。[pose head=16,-2,-6 gaze=left smile=0.6 in=0.5] 往這邊看，[surprised] 有看到我的嘴巴在動嗎？[shy] [pose head=8,-8,-10 gaze=down smile=0.8 in=0.8] 謝謝收看。"
        Task {
            await perform(LiveCueParser.events(from: LiveCueParser.parse(line), speaker: agent),
                          stage: stage, language: language)
            await resetAvatars()
        }
    }

    private func show(_ emotion: LiveEmotion, for character: LiveCharacterStage, on client: VTubeStudioClient) async {
        let agent = character.agent
        let target = emotion == .neutral ? nil : character.emotionHotkeys[emotion.rawValue]
        guard target != activeExpression[agent] else { return }
        let toggles = { (id: String) in self.hotkeys(agent).first(where: { $0.id == id })?.toggles == true }
        do {
            if let active = activeExpression[agent], toggles(active) {
                try await client.trigger(hotkeyID: active) // switch the old expression off
            }
            activeExpression[agent] = nil
            if let target {
                try await client.trigger(hotkeyID: target)
                if toggles(target) { activeExpression[agent] = target }
            }
        } catch {
            // A dropped link reconnects by itself (see `connectionLost`).
            lastWarning = error.localizedDescription
        }
    }
}
