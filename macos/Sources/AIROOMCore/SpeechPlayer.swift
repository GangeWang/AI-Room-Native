#if canImport(AVFoundation)
import AVFoundation

/// Synthesises each line to PCM first so the mouth follows real loudness, then
/// plays it on the default output device (OBS picks it up as desktop audio).
@MainActor
public final class SpeechPlayer {
    public static let mouthFrameRate = 30.0

    private let synthesizer = AVSpeechSynthesizer()
    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private var connectedFormat: AVAudioFormat?
    private var generation = 0
    /// Ends whichever phase (synthesis or playback) is waiting, exactly once.
    private var interrupt: (() -> Void)?

    public init() { engine.attach(player) }

    public static func voices() -> [AVSpeechSynthesisVoice] {
        AVSpeechSynthesisVoice.speechVoices()
            .filter { ["zh", "en", "ja"].contains(where: $0.language.hasPrefix) }
            .sorted { ($0.language, $0.name) < ($1.language, $1.name) }
    }

    public func stop() {
        generation += 1
        synthesizer.stopSpeaking(at: .immediate)
        player.stop()
        interrupt?(); interrupt = nil
    }

    /// Returns once playback finishes or `stop()` is called. `onMouth` receives
    /// 0...1 openness at ~30 Hz and a final 0.
    public func speak(_ text: String, character: LiveCharacterStage, language: InterfaceLanguage,
               onMouth: @escaping (Double) async -> Void) async {
        let spoken = LiveSpeechText.speakable(text)
        guard !spoken.isEmpty else { return }
        generation += 1
        let token = generation
        let utterance = AVSpeechUtterance(string: spoken)
        utterance.voice = AVSpeechSynthesisVoice(identifier: character.voiceIdentifier)
            ?? AVSpeechSynthesisVoice(language: language == .english ? "en-US" : "zh-TW")
        utterance.rate = Float(character.speechRate)
        utterance.pitchMultiplier = Float(character.pitch)

        // Stream: play each chunk as soon as it is synthesized. Waiting for the
        // whole utterance stalled every line, because on current macOS the
        // empty end-of-stream buffer often never arrives (we waited 30 s for it).
        let stream = SpeechStream()
        let done = OnceFlag()
        let started = Date()
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            let finish = { done.run { continuation.resume() } }
            interrupt = finish
            synthesizer.write(utterance) { [weak self] buffer in
                guard let pcm = buffer as? AVAudioPCMBuffer else { return }
                if pcm.frameLength == 0 {
                    stream.endSynthesis()
                    if stream.isDrained { finish() }
                    return
                }
                guard let mono = Self.mixdown([pcm]) else { return }
                DispatchQueue.main.async {
                    guard let self, token == self.generation, !done.isDone else { return }
                    if !stream.isStarted {
                        if self.connectedFormat != mono.format {
                            self.engine.disconnectNodeOutput(self.player)
                            self.engine.connect(self.player, to: self.engine.mainMixerNode, format: mono.format)
                            self.connectedFormat = mono.format
                        }
                        do { if !self.engine.isRunning { try self.engine.start() } } catch { finish(); return }
                        self.player.stop() // restart the timeline so sample time indexes this line
                        stream.start(window: max(1, Int(mono.format.sampleRate / Self.mouthFrameRate)))
                    }
                    stream.add(Self.rms(mono, window: stream.window))
                    self.player.scheduleBuffer(mono, completionCallbackType: .dataPlayedBack) { _ in
                        stream.played()
                        if stream.isFinished { finish() }
                    }
                    if !self.player.isPlaying { self.player.play() }
                }
            }
            // Watchdog: end when no new audio arrived for a while and all of it
            // has played, or when synthesis never produced anything.
            Task { @MainActor [player] in
                while !done.isDone {
                    try? await Task.sleep(nanoseconds: UInt64(1_000_000_000 / Self.mouthFrameRate))
                    guard token == self.generation else { finish(); break }
                    if stream.isStarted, let now = player.lastRenderTime, let time = player.playerTime(forNodeTime: now) {
                        await onMouth(stream.mouth(atSample: Int(time.sampleTime)))
                    }
                    if stream.isIdle(for: 0.8) && stream.isDrained { finish() }
                    if !stream.isStarted && Date().timeIntervalSince(started) > 10 { finish() }
                }
                await onMouth(0)
            }
        }
        interrupt = nil
    }

    /// Mono float32 copy of every synthesized chunk (Int16 or Float32 input).
    private static func mixdown(_ buffers: [AVAudioPCMBuffer]) -> AVAudioPCMBuffer? {
        guard let rate = buffers.first?.format.sampleRate,
              let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: rate, channels: 1, interleaved: false) else { return nil }
        let total = buffers.reduce(0) { $0 + Int($1.frameLength) }
        guard total > 0, let output = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(total)),
              let target = output.floatChannelData?[0] else { return nil }
        var offset = 0
        for buffer in buffers where buffer.format.sampleRate == rate {
            let frames = Int(buffer.frameLength)
            let stride = buffer.format.isInterleaved ? Int(buffer.format.channelCount) : 1
            if let samples = buffer.floatChannelData?[0] {
                for index in 0..<frames { target[offset + index] = samples[index * stride] }
            } else if let samples = buffer.int16ChannelData?[0] {
                for index in 0..<frames { target[offset + index] = Float(samples[index * stride]) / 32_768 }
            } else { continue }
            offset += frames
        }
        output.frameLength = AVAudioFrameCount(offset)
        return offset > 0 ? output : nil
    }

    private static func rms(_ buffer: AVAudioPCMBuffer, window: Int) -> [Float] {
        guard let samples = buffer.floatChannelData?[0] else { return [] }
        let frames = Int(buffer.frameLength)
        return stride(from: 0, to: frames, by: window).map { start in
            let end = min(frames, start + window)
            var sum: Float = 0
            for index in start..<end { sum += samples[index] * samples[index] }
            return (sum / Float(end - start)).squareRoot()
        }
    }
}

/// One line's streaming state, shared by the synthesizer callback, the
/// player's completion callbacks and the mouth loop.
private final class SpeechStream: @unchecked Sendable {
    private let lock = NSLock()
    private var levels: [Float] = []
    private var peak: Float = 0
    private var scheduled = 0, finished = 0
    private var synthesisEnded = false
    private var lastArrival = Date()
    private var started = false
    private var windowSize = 1

    var window: Int { lock.lock(); defer { lock.unlock() }; return windowSize }
    var isStarted: Bool { lock.lock(); defer { lock.unlock() }; return started }
    func start(window: Int) { lock.lock(); windowSize = window; started = true; lock.unlock() }
    func add(_ rms: [Float]) {
        lock.lock(); levels += rms; peak = max(peak, rms.max() ?? 0); scheduled += 1; lastArrival = Date(); lock.unlock()
    }
    func played() { lock.lock(); finished += 1; lock.unlock() }
    func endSynthesis() { lock.lock(); synthesisEnded = true; lock.unlock() }
    var isDrained: Bool { lock.lock(); defer { lock.unlock() }; return started && finished >= scheduled }
    var isFinished: Bool { lock.lock(); defer { lock.unlock() }; return synthesisEnded && started && finished >= scheduled }
    func isIdle(for seconds: TimeInterval) -> Bool {
        lock.lock(); defer { lock.unlock() }; return started && Date().timeIntervalSince(lastArrival) > seconds
    }
    func mouth(atSample sample: Int) -> Double {
        lock.lock(); defer { lock.unlock() }
        let index = sample / windowSize
        guard index >= 0, index < levels.count else { return 0 }
        return LipSync.mouth(level: levels[index], peak: peak)
    }
}

private final class OnceFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var fired = false
    var isDone: Bool { lock.lock(); defer { lock.unlock() }; return fired }
    func run(_ action: () -> Void) {
        lock.lock()
        guard !fired else { lock.unlock(); return }
        fired = true
        lock.unlock()
        action()
    }
}
#endif
