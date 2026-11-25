import Foundation

// MARK: Live stage — typed performance events for TTS and VTube Studio.
// A model may only *propose* an emotion with an inline tag such as `[happy]`.
// Tags are matched against a fixed whitelist; anything else is dropped and is
// never forwarded to VTube Studio, the shell or any other executor.

public enum LiveEmotion: String, Codable, CaseIterable, Identifiable, Sendable {
    case neutral, happy, excited, thinking, surprised, sad, angry, shy
    public var id: String { rawValue }

    public var zhTitle: String {
        switch self {
        case .neutral: return "平靜"
        case .happy: return "開心"
        case .excited: return "興奮"
        case .thinking: return "思考"
        case .surprised: return "驚訝"
        case .sad: return "難過"
        case .angry: return "生氣"
        case .shy: return "害羞"
        }
    }

    /// Accepts the English tag or a Traditional Chinese alias.
    public init?(tag: String) {
        let key = tag.trimmingCharacters(in: .whitespaces).lowercased()
        if let direct = LiveEmotion(rawValue: key) { self = direct; return }
        let aliases: [String: LiveEmotion] = [
            "平靜": .neutral, "中性": .neutral, "開心": .happy, "高興": .happy,
            "興奮": .excited, "思考": .thinking, "驚訝": .surprised, "難過": .sad,
            "傷心": .sad, "生氣": .angry, "害羞": .shy
        ]
        guard let alias = aliases[key] else { return nil }
        self = alias
    }
}

/// A pose keyframe an agent writes inline, e.g.
/// `[pose head=-12,6,8 gaze=left eyes=0.9 brows=0.7 smile=0.8 in=0.6]`.
/// Every field is optional (nil keeps the current value) and clamped; the
/// values are VTube Studio tracking inputs, so the model's own parameter
/// mapping decides what actually moves.
public struct LivePose: Codable, Equatable, Sendable {
    /// Degrees: turn left(-)/right(+), down(-)/up(+), tilt left(-)/right(+).
    public var headX: Double?
    public var headY: Double?
    public var headZ: Double?
    /// -1...1: look left(-)/right(+), down(-)/up(+).
    public var gazeX: Double?
    public var gazeY: Double?
    /// 0 closed ... 1 open.
    public var eyes: Double?
    /// 0 lowered/frowning ... 0.5 neutral ... 1 raised.
    public var brows: Double?
    /// 0 frown ... 0.35 neutral ... 1 big smile (also narrows the eyes and blushes on most rigs).
    public var smile: Double?
    /// -1...1 per side: upper arm (shoulder) and forearm (elbow). Sent as the
    /// member's own VTS custom inputs, which the model maps to its arm rig.
    public var armL: Double?
    public var armR: Double?
    public var elbowL: Double?
    public var elbowR: Double?
    /// -1 angry (inner ends down) ... 0 neutral ... 1 worried (inner ends up).
    /// Sent as the member's own VTS custom input; the model maps it to its
    /// brow-angle parameters. Only the agent's own body sends it.
    public var browAngle: Double?
    /// Seconds to move into this pose.
    public var duration: Double

    public static let defaultDuration = 0.5

    public init(headX: Double? = nil, headY: Double? = nil, headZ: Double? = nil, gazeX: Double? = nil,
                gazeY: Double? = nil, eyes: Double? = nil, brows: Double? = nil, smile: Double? = nil,
                armL: Double? = nil, armR: Double? = nil, elbowL: Double? = nil, elbowR: Double? = nil,
                browAngle: Double? = nil, duration: Double = LivePose.defaultDuration) {
        func clamp(_ value: Double?, _ low: Double, _ high: Double) -> Double? {
            guard let value, value.isFinite else { return nil }
            return min(high, max(low, value))
        }
        self.headX = clamp(headX, -30, 30); self.headY = clamp(headY, -20, 20); self.headZ = clamp(headZ, -30, 30)
        self.gazeX = clamp(gazeX, -1, 1); self.gazeY = clamp(gazeY, -1, 1)
        self.eyes = clamp(eyes, 0, 1); self.brows = clamp(brows, 0, 1); self.smile = clamp(smile, 0, 1)
        self.armL = clamp(armL, -1, 1); self.armR = clamp(armR, -1, 1)
        self.elbowL = clamp(elbowL, -1, 1); self.elbowR = clamp(elbowR, -1, 1)
        self.browAngle = clamp(browAngle, -1, 1)
        self.duration = clamp(duration, 0.1, 4) ?? LivePose.defaultDuration
    }

    public var isEmpty: Bool {
        [headX, headY, headZ, gazeX, gazeY, eyes, brows, smile, armL, armR, elbowL, elbowR, browAngle].allSatisfy { $0 == nil }
    }

    /// Parses the inside of a `[pose …]` tag. Unknown keys and bad numbers are
    /// dropped; a tag with nothing usable gives nil.
    public init?(arguments: String) {
        var headX: Double?, headY: Double?, headZ: Double?, gazeX: Double?, gazeY: Double?
        var eyes: Double?, brows: Double?, smile: Double?, duration = LivePose.defaultDuration
        var armL: Double?, armR: Double?, elbowL: Double?, elbowR: Double?
        func numbers(_ text: Substring) -> [Double] { text.split(separator: ",").compactMap { Double($0.trimmingCharacters(in: .whitespaces)) } }
        for pair in arguments.split(whereSeparator: \.isWhitespace).prefix(12) {
            let parts = pair.split(separator: "=", maxSplits: 1)
            guard parts.count == 2 else { continue }
            let key = parts[0].lowercased(), value = parts[1]
            switch key {
            case "head":
                let v = numbers(value)
                if v.count > 0 { headX = v[0] }; if v.count > 1 { headY = v[1] }; if v.count > 2 { headZ = v[2] }
            case "gaze":
                switch value.lowercased() {
                case "left": gazeX = -0.8; gazeY = 0
                case "right": gazeX = 0.8; gazeY = 0
                case "up": gazeX = 0; gazeY = 0.8
                case "down": gazeX = 0; gazeY = -0.8
                case "center", "camera", "viewer": gazeX = 0; gazeY = 0
                default:
                    let v = numbers(value)
                    if v.count > 0 { gazeX = v[0] }; if v.count > 1 { gazeY = v[1] }
                }
            case "eyes": eyes = Double(value)
            case "brows": brows = Double(value)
            case "smile": smile = Double(value)
            case "arms":
                let v = numbers(value)
                if v.count > 0 { armL = v[0] }; if v.count > 1 { armR = v[1] }
            case "elbows":
                let v = numbers(value)
                if v.count > 0 { elbowL = v[0] }; if v.count > 1 { elbowR = v[1] }
            case "arml": armL = Double(value)
            case "armr": armR = Double(value)
            case "elbowl": elbowL = Double(value)
            case "elbowr": elbowR = Double(value)
            case "in": duration = Double(value.trimmingCharacters(in: CharacterSet(charactersIn: "s"))) ?? duration
            default: continue
            }
        }
        let pose = LivePose(headX: headX, headY: headY, headZ: headZ, gazeX: gazeX, gazeY: gazeY,
                            eyes: eyes, brows: brows, smile: smile,
                            armL: armL, armR: armR, elbowL: elbowL, elbowR: elbowR, duration: duration)
        guard !pose.isEmpty else { return nil }
        self = pose
    }
}

extension LivePose {
    /// What the rig does for a bare emotion tag with no pose of its own.
    public static func preset(for emotion: LiveEmotion) -> LivePose {
        switch emotion {
        case .neutral:   return LivePose(headX: 0, headY: 0, headZ: 0, gazeX: 0, gazeY: 0, eyes: 1, brows: 0.5, smile: 0.35,
                                     armL: 0, armR: 0, elbowL: 0, elbowR: 0, duration: 0.6)
        case .happy:     return LivePose(headY: 3, headZ: 4, eyes: 1, brows: 0.65, smile: 0.9, armL: 0, armR: 0, elbowL: 0, elbowR: 0, duration: 0.5)
        case .excited:   return LivePose(headY: 6, headZ: -5, eyes: 1, brows: 0.85, smile: 1,
                                     armL: 0.7, armR: 0.7, elbowL: 0.6, elbowR: 0.6, duration: 0.3)
        case .thinking:  return LivePose(headX: -8, headY: 4, headZ: 9, gazeX: 0.5, gazeY: 0.6, eyes: 0.8, brows: 0.6, smile: 0.3, armL: 0, armR: 0.2, elbowL: 0, elbowR: 1, duration: 0.8)
        case .surprised: return LivePose(headY: 5, eyes: 1, brows: 1, smile: 0.4,
                                     armL: 0.3, armR: 0.3, elbowL: 0.9, elbowR: 0.9, duration: 0.2)
        case .sad:       return LivePose(headY: -8, headZ: -4, gazeY: -0.5, eyes: 0.7, brows: 0.2, smile: 0.1, armL: 0, armR: 0, elbowL: 0, elbowR: 0, duration: 1.0)
        case .angry:     return LivePose(headY: -4, eyes: 0.85, brows: 0, smile: 0.05, armL: 0, armR: 0, elbowL: 0, elbowR: 0, duration: 0.4)
        case .shy:       return LivePose(headX: 10, headY: -6, headZ: -8, gazeX: -0.6, gazeY: -0.4, eyes: 0.75, brows: 0.55, smile: 0.7,
                                     armL: 0, armR: 0, elbowL: 1, elbowR: 1, duration: 0.7)
        }
    }
}

/// One spoken segment. A reply becomes 1...`LiveCueParser.maxSegments` events,
/// played strictly in `sequence` order before the next speaker starts.
public struct LiveEvent: Identifiable, Codable, Equatable, Sendable {
    public var id: UUID
    public var turnID: UUID
    public var sequence: Int
    public var speaker: MemberID
    public var utterance: String
    public var emotion: LiveEmotion
    /// Moved into as this segment starts; nil keeps the current pose.
    public var pose: LivePose?
    public var timestamp: Date

    public init(id: UUID = UUID(), turnID: UUID, sequence: Int, speaker: MemberID,
                utterance: String, emotion: LiveEmotion, pose: LivePose? = nil, timestamp: Date = .now) {
        self.id = id; self.turnID = turnID; self.sequence = sequence; self.speaker = speaker
        self.utterance = utterance; self.emotion = emotion; self.pose = pose; self.timestamp = timestamp
    }
}

public struct ParsedLiveReply: Equatable, Sendable {
    public struct Segment: Equatable, Sendable {
        public var emotion: LiveEmotion
        public var text: String
        public var pose: LivePose?
        public init(emotion: LiveEmotion, text: String, pose: LivePose? = nil) {
            self.emotion = emotion; self.text = text; self.pose = pose
        }
    }
    /// Subtitle / transcript text with every recognised or invented tag removed.
    public var cleanText: String
    public var segments: [Segment]
}

public enum LiveCueParser {
    public static let maxSegments = 12
    public static let maxSegmentCharacters = 600

    // `[happy]`, `[ Happy ]`, `【開心】`, `[pose head=10 smile=0.8]`. ASCII words
    // are treated as cue attempts; short Han words are cues only when they
    // match an alias.
    private static let pattern = try! NSRegularExpression(
        pattern: #"[\[【]\s*((?i:pose)\s+[^\]】\n]{1,200}|[A-Za-z]{1,16}|\p{Han}{1,4})\s*[\]】]"#)

    public static func parse(_ reply: String) -> ParsedLiveReply {
        let source = reply as NSString
        var segments: [ParsedLiveReply.Segment] = []
        var clean = ""
        var pending = ""
        var emotion = LiveEmotion.neutral
        var pose: LivePose?
        var cursor = 0

        func flush() {
            let text = pending.trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty { segments.append(.init(emotion: emotion, text: text, pose: pose)); pose = nil }
            pending = ""
        }

        for match in pattern.matches(in: reply, range: NSRange(location: 0, length: source.length)) {
            let before = source.substring(with: NSRange(location: cursor, length: match.range.location - cursor))
            pending += before; clean += before
            cursor = match.range.location + match.range.length
            let word = source.substring(with: match.range(at: 1))
            if word.lowercased().hasPrefix("pose") && word.count > 4 {
                // A pose tag (valid or not) is a stage direction, never spoken.
                if let parsed = LivePose(arguments: String(word.dropFirst(4))) { flush(); pose = parsed }
            } else if let cue = LiveEmotion(tag: word) {
                flush(); emotion = cue
            } else if word.unicodeScalars.allSatisfy({ $0.isASCII }) {
                continue // invented ASCII tag: drop silently
            } else {
                let literal = source.substring(with: match.range) // e.g. 【注意】 is ordinary text
                pending += literal; clean += literal
            }
        }
        let tail = source.substring(from: cursor)
        pending += tail; clean += tail
        flush()

        if segments.count > maxSegments {
            let overflow = segments[(maxSegments - 1)...].map(\.text).joined(separator: " ")
            segments = Array(segments.prefix(maxSegments - 1))
                + [.init(emotion: segments[maxSegments - 1].emotion, text: overflow, pose: segments[maxSegments - 1].pose)]
        }
        segments = segments.map { .init(emotion: $0.emotion, text: String($0.text.prefix(maxSegmentCharacters)), pose: $0.pose) }
        return ParsedLiveReply(cleanText: normalizeSpacing(clean), segments: segments)
    }

    public static func events(from parsed: ParsedLiveReply, speaker: MemberID,
                              turnID: UUID = UUID(), now: Date = .now) -> [LiveEvent] {
        parsed.segments.enumerated().map { index, segment in
            LiveEvent(turnID: turnID, sequence: index, speaker: speaker,
                      utterance: segment.text, emotion: segment.emotion, pose: segment.pose, timestamp: now)
        }
    }

    private static func normalizeSpacing(_ text: String) -> String {
        text.split(separator: "\n", omittingEmptySubsequences: false)
            .map { $0.split(whereSeparator: { $0 == " " || $0 == "\t" }).joined(separator: " ") }
            .joined(separator: "\n")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

/// Text sent to the speech synthesizer: no markdown symbols, URLs or code.
public enum LiveSpeechText {
    private static let url = try! NSRegularExpression(pattern: #"https?://\S+"#)
    private static let code = try! NSRegularExpression(pattern: #"```[\s\S]*?```|`[^`]*`"#)

    public static func speakable(_ text: String, limit: Int = LiveCueParser.maxSegmentCharacters) -> String {
        var value = text
        for expression in [code, url] {
            value = expression.stringByReplacingMatches(
                in: value, range: NSRange(location: 0, length: (value as NSString).length), withTemplate: " ")
        }
        value = value.filter { !"*#_>|~".contains($0) }
        // Emoji are read out as their names ("smiling face") or render as tofu.
        value.unicodeScalars.removeAll { $0.properties.isEmojiPresentation || $0.value == 0xFE0F || $0.value == 0x200D }
        return String(value.split(whereSeparator: \.isWhitespace).joined(separator: " ").prefix(limit))
    }
}

// MARK: Persisted stage configuration (inside the encrypted state file)

public struct LiveCharacterStage: Codable, Equatable, Sendable {
    public var agent: MemberID
    /// AVSpeechSynthesisVoice identifier; empty means the system voice for the UI language.
    public var voiceIdentifier: String
    /// AVSpeechUtterance rate (0.3–0.65; 0.5 is the system default).
    public var speechRate: Double
    public var pitch: Double
    /// Whether this co-host has its own VTube Studio instance to drive.
    public var drivesAvatar: Bool
    /// Each co-host has a separate VTS instance; each new instance takes the
    /// next free port from 8001 up, in the order they are started.
    public var vtsPort: Int
    /// Plugin token issued by that VTS instance after the user clicks Allow.
    /// It only grants control of local VTS; stored with the encrypted state.
    public var vtsToken: String
    /// The VTS model this co-host wears. Instances share their settings, so
    /// both start on the same last-used model; this one is loaded on connect.
    /// Empty keeps whatever model the instance has open.
    public var vtsModelID: String
    public var vtsModelName: String
    /// The agent drives the rig (head, eyes, brows, smile) through VTS
    /// tracking inputs while connected, with idle motion between lines.
    public var aiRig: Bool
    public var mouthParameter: String
    /// LiveEmotion.rawValue → VTube Studio hotkeyID. Only IDs reported by the
    /// currently loaded model are ever triggered.
    public var emotionHotkeys: [String: String]

    public init(agent: MemberID, voiceIdentifier: String = "", speechRate: Double = 0.5,
                pitch: Double = 1.0, drivesAvatar: Bool = false, vtsPort: Int? = nil, vtsToken: String = "",
                vtsModelID: String = "", vtsModelName: String = "", aiRig: Bool = true, mouthParameter: String = "MouthOpen", emotionHotkeys: [String: String] = [:]) {
        self.agent = agent
        self.voiceIdentifier = String(voiceIdentifier.prefix(200))
        self.speechRate = min(0.65, max(0.3, speechRate))
        self.pitch = min(2.0, max(0.5, pitch))
        self.drivesAvatar = drivesAvatar
        self.vtsPort = min(65_535, max(1_024, vtsPort ?? Self.defaultPort(agent)))
        self.vtsToken = String(vtsToken.prefix(512))
        self.vtsModelID = String(vtsModelID.prefix(128))
        self.vtsModelName = String(vtsModelName.prefix(200))
        self.aiRig = aiRig
        let mouth = mouthParameter.filter { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_") }
        self.mouthParameter = mouth.isEmpty ? "MouthOpen" : String(mouth.prefix(32))
        self.emotionHotkeys = emotionHotkeys.filter { LiveEmotion(rawValue: $0.key) != nil && $0.value.count <= 128 }
    }

    private enum CodingKeys: String, CodingKey {
        case agent, voiceIdentifier, speechRate, pitch, drivesAvatar, vtsPort, vtsToken, vtsModelID, vtsModelName, aiRig, mouthParameter, emotionHotkeys
    }

    /// GPT's model went live first, so its VTS instance usually owns 8001.
    /// Each VTube Studio instance takes the next port; added members start at 8003.
    public static func defaultPort(_ agent: MemberID) -> Int { agent == .codex ? 8001 : agent == .claude ? 8002 : 8003 }
    public init(from decoder: Decoder) throws {
        let box = try decoder.container(keyedBy: CodingKeys.self)
        self.init(agent: try box.decode(MemberID.self, forKey: .agent),
                  voiceIdentifier: try box.decodeIfPresent(String.self, forKey: .voiceIdentifier) ?? "",
                  speechRate: try box.decodeIfPresent(Double.self, forKey: .speechRate) ?? 0.5,
                  pitch: try box.decodeIfPresent(Double.self, forKey: .pitch) ?? 1.0,
                  drivesAvatar: try box.decodeIfPresent(Bool.self, forKey: .drivesAvatar) ?? false,
                  vtsPort: try box.decodeIfPresent(Int.self, forKey: .vtsPort),
                  vtsToken: try box.decodeIfPresent(String.self, forKey: .vtsToken) ?? "",
                  vtsModelID: try box.decodeIfPresent(String.self, forKey: .vtsModelID) ?? "",
                  vtsModelName: try box.decodeIfPresent(String.self, forKey: .vtsModelName) ?? "",
                  aiRig: try box.decodeIfPresent(Bool.self, forKey: .aiRig) ?? true,
                  mouthParameter: try box.decodeIfPresent(String.self, forKey: .mouthParameter) ?? "MouthOpen",
                  emotionHotkeys: try box.decodeIfPresent([String: String].self, forKey: .emotionHotkeys) ?? [:])
    }
}

public struct LiveStageSettings: Codable, Equatable, Sendable {
    public var speechEnabled: Bool
    public var vtsEnabled: Bool
    public var characters: [LiveCharacterStage]
    /// Members on Claude Code or Codex speak and move through their own body
    /// tools during a show; AI Room only manages the floor.
    public var agentBodies: Bool

    public init(speechEnabled: Bool = false, vtsEnabled: Bool = false, characters: [LiveCharacterStage] = [],
                agentBodies: Bool = true) {
        self.speechEnabled = speechEnabled
        self.vtsEnabled = vtsEnabled
        self.agentBodies = agentBodies
        var unique: [LiveCharacterStage] = []
        for item in characters where !unique.contains(where: { $0.agent == item.agent }) { unique.append(item) }
        for agent in MemberID.builtIn where !unique.contains(where: { $0.agent == agent }) {
            unique.append(LiveCharacterStage(agent: agent, drivesAvatar: true))
        }
        self.characters = unique
    }

    private enum CodingKeys: String, CodingKey { case speechEnabled, vtsEnabled, characters, agentBodies }
    public init(from decoder: Decoder) throws {
        let box = try decoder.container(keyedBy: CodingKeys.self)
        self.init(speechEnabled: try box.decodeIfPresent(Bool.self, forKey: .speechEnabled) ?? false,
                  vtsEnabled: try box.decodeIfPresent(Bool.self, forKey: .vtsEnabled) ?? false,
                  characters: try box.decodeIfPresent([LiveCharacterStage].self, forKey: .characters) ?? [],
                  agentBodies: try box.decodeIfPresent(Bool.self, forKey: .agentBodies) ?? true)
    }

    public var performs: Bool { speechEnabled || vtsEnabled }

    public func character(_ agent: MemberID) -> LiveCharacterStage {
        characters.first(where: { $0.agent == agent }) ?? LiveCharacterStage(agent: agent)
    }

    public mutating func updateCharacter(_ agent: MemberID, _ change: (inout LiveCharacterStage) -> Void) {
        if !characters.contains(where: { $0.agent == agent }) { characters.append(LiveCharacterStage(agent: agent, drivesAvatar: true)) }
        guard let index = characters.firstIndex(where: { $0.agent == agent }) else { return }
        change(&characters[index])
        let edited = characters[index]
        characters[index] = LiveCharacterStage(agent: edited.agent, voiceIdentifier: edited.voiceIdentifier,
            speechRate: edited.speechRate, pitch: edited.pitch, drivesAvatar: edited.drivesAvatar,
            vtsPort: edited.vtsPort, vtsToken: edited.vtsToken,
            vtsModelID: edited.vtsModelID, vtsModelName: edited.vtsModelName, aiRig: edited.aiRig, mouthParameter: edited.mouthParameter, emotionHotkeys: edited.emotionHotkeys)
    }
}

// MARK: Hotkey auto-mapping

public enum LiveHotkeyMatcher {
    /// Maps each emotion to the first hotkey whose name contains the emotion's
    /// English tag or Chinese title ("開心 happy", "Happy face", "害羞"…).
    public static func match(_ hotkeys: [VTSHotkey]) -> [String: String] {
        var result: [String: String] = [:]
        for emotion in LiveEmotion.allCases where emotion != .neutral {
            let hit = hotkeys.first { hotkey in
                let name = hotkey.name.lowercased()
                return name.contains(emotion.rawValue) || name.contains(emotion.zhTitle)
            }
            if let hit { result[emotion.rawValue] = hit.id }
        }
        return result
    }
}

// MARK: Lip sync envelope

public enum LipSync {

    /// One window's openness against the loudest window heard so far (used
    /// while audio is still streaming in and the final peak is unknown).
    public static func mouth(level: Float, peak: Float, gate: Float = 0.08) -> Double {
        guard peak > 0 else { return 0 }
        let relative = level / peak
        return relative < gate ? 0 : Double(min(1, pow(relative, 0.6)))
    }
}
