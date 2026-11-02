import Foundation
import AIROOMCore

/// Plays LiveEvents one at a time: emotion hotkey → TTS with lip sync → next.
/// The live loop awaits `perform`, so two co-hosts never talk over each other.
@MainActor
final class LiveStageController: ObservableObject {
    enum Connection: Equatable {
        case off, connecting, connected(model: String), failed(String)
    }

    @Published private(set) var connection: Connection = .off
    @Published private(set) var hotkeys: [VTSHotkey] = []
    @Published private(set) var speaking: AgentKind?
    @Published private(set) var subtitle = ""
    @Published private(set) var lastWarning: String?

    private let client = VTubeStudioClient()
    private let speech = SpeechPlayer()
    /// Expression hotkey currently switched on (VTS expressions toggle).
    private var activeExpression: String?
    private var stopped = false

    var isConnected: Bool { if case .connected = connection { return true } else { return false } }

    // MARK: VTube Studio

    func connect(port: Int, token: String, onNewToken: @escaping (String) -> Void) {
        connection = .connecting
        Task {
            do {
                let result = try await client.connect(port: port, token: token)
                if result.token != token { onNewToken(result.token) }
                connection = .connected(model: result.model.loaded ? result.model.name : "")
                await refreshHotkeys()
            } catch {
                connection = .failed(error.localizedDescription)
            }
        }
    }

    func disconnect() {
        activeExpression = nil
        hotkeys = []
        connection = .off
        Task { await client.disconnect() }
    }

    func refreshHotkeys() async {
        do { hotkeys = try await client.hotkeys() }
        catch { lastWarning = error.localizedDescription }
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
            let avatar = stage.vtsEnabled && character.drivesAvatar && isConnected
            if avatar { await show(event.emotion, for: character) }
            speaking = event.speaker
            subtitle = event.utterance
            guard stage.speechEnabled else { continue }
            let mouth = character.mouthParameter
            let client = client
            await speech.speak(event.utterance, character: character, language: language) { value in
                if avatar { await client.inject([(mouth, value)]) }
            }
        }
        speaking = nil
    }

    /// Clears any expression left on by the previous show.
    func resetAvatar() async {
        guard let active = activeExpression else { return }
        activeExpression = nil
        try? await client.trigger(hotkeyID: active)
    }

    func rehearse(_ agent: AgentKind, name: String, stage: LiveStageSettings, language: InterfaceLanguage) {
        let line = language == .english
            ? "[happy] Hi everyone, I'm \(name)! [thinking] This is a voice and expression test. [surprised] Can you see my mouth move?"
            : "[happy] 大家好，我是\(name)！[thinking] 這是聲音和表情的測試。[surprised] 有看到我的嘴巴在動嗎？"
        Task {
            await perform(LiveCueParser.events(from: LiveCueParser.parse(line), speaker: agent),
                          stage: stage, language: language)
            await resetAvatar()
        }
    }

    private func show(_ emotion: LiveEmotion, for character: LiveCharacterStage) async {
        let target = emotion == .neutral ? nil : character.emotionHotkeys[emotion.rawValue]
        guard target != activeExpression else { return }
        do {
            if let active = activeExpression, hotkeys.first(where: { $0.id == active })?.toggles == true {
                try await client.trigger(hotkeyID: active) // switch the old expression off
            }
            activeExpression = nil
            if let target {
                try await client.trigger(hotkeyID: target)
                if hotkeys.first(where: { $0.id == target })?.toggles == true { activeExpression = target }
            }
        } catch {
            lastWarning = error.localizedDescription
            if case VTSError.disconnected = error { connection = .failed(error.localizedDescription) }
        }
    }
}
