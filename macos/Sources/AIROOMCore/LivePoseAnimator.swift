import Foundation

/// Turns pose keyframes into a steady stream of VTube Studio tracking inputs,
/// so an agent can drive a model's rig the way a face tracker would.
///
/// The agent only writes keyframes (`LivePose`); between them this eases
/// smoothly and adds an idle layer — blinking, breathing, a slight sway — so
/// the character never freezes. Mouth openness comes from the voice.
/// Everything is a pure function of time, so it is deterministic and testable.
public struct LivePoseAnimator: Sendable {
    /// VTube Studio default tracking inputs this animator writes.
    public enum Input {
        public static let headX = "FaceAngleX", headY = "FaceAngleY", headZ = "FaceAngleZ"
        public static let eyeOpenLeft = "EyeOpenLeft", eyeOpenRight = "EyeOpenRight"
        public static let eyeLeftX = "EyeLeftX", eyeLeftY = "EyeLeftY"
        public static let eyeRightX = "EyeRightX", eyeRightY = "EyeRightY"
        public static let brows = "Brows", smile = "MouthSmile"
    }

    /// The member's own VTS custom inputs for the arms (VTS requires unique,
    /// 4–32 character alphanumeric names; each member gets its own set so two
    /// members' plugins never claim the same parameter).
    public struct ArmInputs: Equatable, Sendable {
        public var armL, armR, elbowL, elbowR: String
        public init(member: MemberID) {
            let tag = String(member.handle.filter { $0.isASCII && ($0.isLetter || $0.isNumber) }.prefix(14))
            let base = "AIROOM" + (tag.isEmpty ? "member" : tag)
            armL = base + "ArmL"; armR = base + "ArmR"; elbowL = base + "ElbowL"; elbowR = base + "ElbowR"
        }
        public var all: [String] { [armL, armR, elbowL, elbowR] }
    }

    /// The member's own VTS custom input for the brow angle (-1 angry … 1
    /// worried). VTS has no built-in tracking input for it, so the model has
    /// to map this one to its brow-angle parameters.
    public static func browAngleInput(member: MemberID) -> String {
        let tag = String(member.handle.filter { $0.isASCII && ($0.isLetter || $0.isNumber) }.prefix(14))
        return "AIROOM" + (tag.isEmpty ? "member" : tag) + "BrowAngle"
    }

    /// Parameters a played motion (VTS hotkey animation) moves itself: the head
    /// and body angles and the arms. While a motion plays the body stops
    /// sending these so it does not fight the animation.
    public static func yieldingToMotion(_ frame: [(String, Double)], arms: ArmInputs?) -> [(String, Double)] {
        let held = Set(arms?.all ?? [])
        return frame.filter { name, _ in !name.hasPrefix("FaceAngle") && !held.contains(name) }
    }

    struct Values: Equatable, Sendable {
        var headX = 0.0, headY = 0.0, headZ = 0.0, gazeX = 0.0, gazeY = 0.0
        var eyes = 1.0, brows = 0.5, smile = 0.35
        var armL = 0.0, armR = 0.0, elbowL = 0.0, elbowR = 0.0, browAngle = 0.0

        func applying(_ pose: LivePose) -> Values {
            var next = self
            if let v = pose.headX { next.headX = v }; if let v = pose.headY { next.headY = v }
            if let v = pose.headZ { next.headZ = v }; if let v = pose.gazeX { next.gazeX = v }
            if let v = pose.gazeY { next.gazeY = v }; if let v = pose.eyes { next.eyes = v }
            if let v = pose.brows { next.brows = v }; if let v = pose.smile { next.smile = v }
            if let v = pose.armL { next.armL = v }; if let v = pose.armR { next.armR = v }
            if let v = pose.elbowL { next.elbowL = v }; if let v = pose.elbowR { next.elbowR = v }
            if let v = pose.browAngle { next.browAngle = v }
            return next
        }

        static func blend(_ a: Values, _ b: Values, _ t: Double) -> Values {
            func mix(_ x: Double, _ y: Double) -> Double { x + (y - x) * t }
            return Values(headX: mix(a.headX, b.headX), headY: mix(a.headY, b.headY), headZ: mix(a.headZ, b.headZ),
                          gazeX: mix(a.gazeX, b.gazeX), gazeY: mix(a.gazeY, b.gazeY), eyes: mix(a.eyes, b.eyes),
                          brows: mix(a.brows, b.brows), smile: mix(a.smile, b.smile),
                          armL: mix(a.armL, b.armL), armR: mix(a.armR, b.armR),
                          elbowL: mix(a.elbowL, b.elbowL), elbowR: mix(a.elbowR, b.elbowR),
                          browAngle: mix(a.browAngle, b.browAngle))
        }
    }

    private var from = Values()
    private var to = Values()
    private var start: TimeInterval = 0
    private var duration: TimeInterval = 0.001
    /// 0...1, set from the voice's loudness.
    public var mouth: Double = 0
    /// Idle motion on/off (tests turn it off to check exact values).
    public var idle: Bool
    /// Blinking alone, without the sway and breathing of `idle`. An agent's
    /// body uses this: the agent decides every movement, but nobody should
    /// have to spend a turn on blinking.
    public var blinks: Bool

    public init(idle: Bool = true, blinks: Bool = false) { self.idle = idle; self.blinks = blinks }

    /// Starts moving from wherever the rig is now toward `pose`.
    public mutating func apply(_ pose: LivePose, at time: TimeInterval) {
        let current = values(at: time)
        from = current
        to = current.applying(pose)
        start = time
        duration = max(0.001, pose.duration)
    }

    /// Eases back to the neutral resting pose.
    public mutating func relax(at time: TimeInterval, over seconds: Double = 1.0) {
        from = values(at: time)
        to = Values()
        start = time
        duration = max(0.001, seconds)
    }

    func values(at time: TimeInterval) -> Values {
        let raw = min(1, max(0, (time - start) / duration))
        let eased = raw * raw * (3 - 2 * raw) // smoothstep
        return Values.blend(from, to, eased)
    }

    /// Eyelid factor for a natural blink every ~3–5 s (0 = shut).
    static func blink(at time: TimeInterval) -> Double {
        let period = 4.2, length = 0.16
        // Vary the gap a little so blinks do not tick like a metronome.
        let shifted = time + 0.9 * sin(time * 0.37)
        let phase = shifted.truncatingRemainder(dividingBy: period)
        guard phase >= 0, phase < length else { return 1 }
        return abs(phase / length * 2 - 1)
    }

    /// The tracking inputs to send to VTube Studio at `time`.
    public func frame(at time: TimeInterval, mouthParameter: String = "MouthOpen",
                      arms: ArmInputs? = nil, browAngle: String? = nil) -> [(String, Double)] {
        var v = values(at: time)
        var eyes = v.eyes
        if idle {
            v.headY += 1.2 * sin(time * 2 * .pi / 4.5)                          // breathing
            v.headX += 1.5 * sin(time * 0.53) + 0.8 * sin(time * 1.31)         // sway
            v.headZ += 1.0 * sin(time * 0.41 + 1.7)
            eyes *= Self.blink(at: time)
            // Arms drift a little with the breath so they never look pinned.
            let breath = 0.04 * sin(time * 2 * .pi / 4.5)
            v.armL += breath; v.armR += breath
        } else if blinks {
            eyes *= Self.blink(at: time)
        }
        var frame: [(String, Double)] = [
            (Input.headX, min(30, max(-30, v.headX))), (Input.headY, min(20, max(-20, v.headY))),
            (Input.headZ, min(30, max(-30, v.headZ))),
            (Input.eyeOpenLeft, eyes), (Input.eyeOpenRight, eyes),
            (Input.eyeLeftX, v.gazeX), (Input.eyeRightX, v.gazeX),
            (Input.eyeLeftY, v.gazeY), (Input.eyeRightY, v.gazeY),
            (Input.brows, v.brows), (Input.smile, v.smile),
            (mouthParameter, min(1, max(0, mouth))),
        ]
        if let arms {
            func c(_ x: Double) -> Double { min(1, max(-1, x)) }
            frame += [(arms.armL, c(v.armL)), (arms.armR, c(v.armR)), (arms.elbowL, c(v.elbowL)), (arms.elbowR, c(v.elbowR))]
        }
        if let browAngle { frame.append((browAngle, min(1, max(-1, v.browAngle)))) }
        return frame
    }
}
