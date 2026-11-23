import SwiftUI
import AIROOMCore

// Native marks drawn after each program's language-model logo (not the vendor apps' icons),
// so they stay sharp at any size and share one tile style.

/// Claude: a hand-drawn spark of rays that swell toward rounded tips.
struct ClaudeSparkShape: Shape {
    func path(in rect: CGRect) -> Path {
        let s = min(rect.width, rect.height)
        let c = CGPoint(x: rect.midX, y: rect.midY)
        // Slightly uneven lengths and angles give the spark its hand-made feel.
        let lengths: [CGFloat] = [0.49, 0.40, 0.46, 0.37, 0.50, 0.41, 0.45, 0.36, 0.48, 0.39, 0.47, 0.35]
        let jitter: [CGFloat] = [0, 4, -3, 5, -2, 3, -4, 2, 3, -3, 4, -2]
        var path = Path()
        for (i, length) in lengths.enumerated() {
            let angle = (CGFloat(i) * 30 + jitter[i]) * .pi / 180
            let base = s * 0.022, tip = s * 0.044, l = s * length
            var ray = Path()
            ray.move(to: CGPoint(x: -base, y: 0))
            ray.addLine(to: CGPoint(x: -tip, y: -l + tip))
            ray.addArc(center: CGPoint(x: 0, y: -l + tip), radius: tip, startAngle: .degrees(180), endAngle: .degrees(0), clockwise: false)
            ray.addLine(to: CGPoint(x: base, y: 0))
            ray.closeSubpath()
            var t = CGAffineTransform(translationX: c.x, y: c.y).rotated(by: angle)
            if let p = ray.cgPath.copy(using: &t) { path.addPath(Path(p)) }
        }
        path.addEllipse(in: CGRect(x: c.x - s * 0.06, y: c.y - s * 0.06, width: s * 0.12, height: s * 0.12))
        return path
    }
}

/// DeepSeek: a whale with a curled tail.
struct WhaleShape: Shape {
    func path(in rect: CGRect) -> Path {
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: rect.minX + x * rect.width, y: rect.minY + y * rect.height) }
        var path = Path()
        path.move(to: p(0.10, 0.52))
        path.addCurve(to: p(0.50, 0.24), control1: p(0.12, 0.34), control2: p(0.30, 0.22))
        path.addCurve(to: p(0.76, 0.42), control1: p(0.64, 0.25), control2: p(0.72, 0.33))
        // Tail rises and flicks back.
        path.addCurve(to: p(0.86, 0.20), control1: p(0.82, 0.38), control2: p(0.84, 0.28))
        path.addCurve(to: p(0.94, 0.34), control1: p(0.90, 0.24), control2: p(0.95, 0.28))
        path.addCurve(to: p(0.80, 0.56), control1: p(0.93, 0.44), control2: p(0.86, 0.52))
        path.addCurve(to: p(0.46, 0.78), control1: p(0.74, 0.70), control2: p(0.60, 0.79))
        path.addCurve(to: p(0.10, 0.52), control1: p(0.26, 0.78), control2: p(0.10, 0.68))
        path.closeSubpath()
        return path
    }
}

/// Gemini: a four-pointed star with softly concave sides.
struct GeminiStarShape: Shape {
    func path(in rect: CGRect) -> Path {
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: rect.minX + x * rect.width, y: rect.minY + y * rect.height) }
        var path = Path()
        path.move(to: p(0.5, 0.04))
        path.addQuadCurve(to: p(0.96, 0.5), control: p(0.55, 0.45))
        path.addQuadCurve(to: p(0.5, 0.96), control: p(0.55, 0.55))
        path.addQuadCurve(to: p(0.04, 0.5), control: p(0.45, 0.55))
        path.addQuadCurve(to: p(0.5, 0.04), control: p(0.45, 0.45))
        path.closeSubpath()
        return path
    }
}

/// GLM / Z.ai: a bold Z drawn as one stroke with rounded corners.
struct ZMarkShape: Shape {
    func path(in rect: CGRect) -> Path {
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: rect.minX + x * rect.width, y: rect.minY + y * rect.height) }
        var path = Path()
        path.move(to: p(0.22, 0.24)); path.addLine(to: p(0.78, 0.24))
        path.addLine(to: p(0.24, 0.76)); path.addLine(to: p(0.80, 0.76))
        return path
    }
}

enum ModelMark { case claude, gpt, deepseek, gemini, glm }

/// A rounded tile with the model's mark, drawn natively so it stays sharp at any size.
struct ModelLogoTile: View {
    let mark: ModelMark
    var size: CGFloat = 40
    var body: some View {
        let shape = RoundedRectangle(cornerRadius: size * 0.26, style: .continuous)
        ZStack {
            shape.fill(background)
            shape.fill(LinearGradient(colors: [.white.opacity(0.22), .clear], startPoint: .top, endPoint: .center))
            glyph.frame(width: size * 0.64, height: size * 0.64)
        }
        .frame(width: size, height: size)
        .overlay(shape.strokeBorder(.white.opacity(0.12), lineWidth: 0.6))
        .shadow(color: shadowTint.opacity(0.35), radius: size * 0.07, y: size * 0.03)
    }

    private var background: LinearGradient {
        switch mark {
        case .claude: return LinearGradient(colors: [Color(red: 0.89, green: 0.53, blue: 0.40), Color(red: 0.78, green: 0.39, blue: 0.27)], startPoint: .top, endPoint: .bottom)
        case .gpt: return LinearGradient(colors: [Color(red: 0.20, green: 0.20, blue: 0.22), Color(red: 0.06, green: 0.06, blue: 0.07)], startPoint: .top, endPoint: .bottom)
        case .deepseek: return LinearGradient(colors: [Color(red: 0.40, green: 0.52, blue: 1.0), Color(red: 0.25, green: 0.36, blue: 0.93)], startPoint: .top, endPoint: .bottom)
        case .gemini: return LinearGradient(colors: [Color(red: 0.13, green: 0.15, blue: 0.24), Color(red: 0.06, green: 0.07, blue: 0.13)], startPoint: .top, endPoint: .bottom)
        case .glm: return LinearGradient(colors: [Color(red: 0.24, green: 0.36, blue: 0.95), Color(red: 0.10, green: 0.16, blue: 0.55)], startPoint: .top, endPoint: .bottom)
        }
    }

    private var shadowTint: Color {
        switch mark {
        case .claude: return .orange
        case .gpt, .gemini: return .black
        case .deepseek, .glm: return .blue
        }
    }

    @ViewBuilder private var glyph: some View {
        switch mark {
        case .claude:
            ClaudeSparkShape().fill(Color(red: 0.99, green: 0.96, blue: 0.91))
        case .gpt:
            BlossomKnot(color: .white)
        case .deepseek:
            ZStack {
                WhaleShape().fill(Color.white)
                GeometryReader { g in
                    Circle().fill(Color(red: 0.30, green: 0.42, blue: 0.96))
                        .frame(width: g.size.width * 0.07, height: g.size.width * 0.07)
                        .position(x: g.size.width * 0.30, y: g.size.height * 0.46)
                }
            }
        case .gemini:
            GeminiStarShape().fill(LinearGradient(colors: [Color(red: 0.29, green: 0.56, blue: 0.98), Color(red: 0.58, green: 0.46, blue: 0.86), Color(red: 0.86, green: 0.43, blue: 0.52)],
                                                  startPoint: .topLeading, endPoint: .bottomTrailing))
        case .glm:
            ZMarkShape().stroke(Color.white, style: StrokeStyle(lineWidth: size * 0.1, lineCap: .round, lineJoin: .round))
        }
    }
}

/// GPT: six bands turned 60° apart. Each runs along one edge of the centre hexagon, folds
/// sharply at its corner, runs straight out, then swings round a wide half-circle.
struct BlossomKnot: View {
    var side: CGFloat = 0.12       // distance of the straight run from the centre
    var join: CGFloat = 0.17       // where it meets the arc (above centre)
    var radius: CGFloat = 0.28     // arc radius
    var endAngle: CGFloat = 0     // degrees below horizontal where the arc ends
    var line: CGFloat = 0.074
    var gap: CGFloat = 0.03
    var over: (CGFloat, CGFloat) = (0.52, 1)
    var color: Color = .white
    var body: some View {
        Canvas { ctx, size in
            let s = min(size.width, size.height) * 0.92
            let c = CGPoint(x: size.width / 2, y: size.height / 2)
            func piece(_ k: Int) -> Path {
                // Along one edge of the centre hexagon, a sharp 60° fold at its corner,
                // straight out, then the wide curve: folds inside, curves outside.
                let half = side * tan(.pi / 6)            // half an edge of the hexagon
                var p = Path()
                p.move(to: CGPoint(x: 0, y: s * side / cos(.pi / 6)))
                p.addLine(to: CGPoint(x: -s * side, y: s * half))
                p.addLine(to: CGPoint(x: -s * side, y: -s * join))
                p.addArc(center: CGPoint(x: -s * side + s * radius, y: -s * join), radius: s * radius,
                         startAngle: .degrees(180), endAngle: .degrees(360 + Double(endAngle)), clockwise: false)
                var t = CGAffineTransform(translationX: c.x, y: c.y).rotated(by: CGFloat(k) * .pi / 3)
                return Path(p.cgPath.copy(using: &t)!)
            }
            func draw(_ path: Path, erase: Path) {
                ctx.blendMode = .destinationOut
                ctx.stroke(erase, with: .color(.black), style: StrokeStyle(lineWidth: s * (line + gap * 2), lineCap: .butt))
                ctx.blendMode = .normal
                ctx.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: s * line, lineCap: .round, lineJoin: .round))
            }
            for k in 0..<6 {
                let p = piece(k)
                draw(p, erase: p.trimmedPath(from: over.0, to: over.1))
            }
            let first = piece(0).trimmedPath(from: over.0, to: over.1)
            draw(first, erase: first)
        }
        .compositingGroup()
    }
}

extension Executor {
    /// The language model each program runs: Claude Code → Claude, Codex → GPT,
    /// DeepSeek Harness → DeepSeek, Antigravity → Gemini, ZCode → GLM.
    var modelMark: ModelMark {
        switch self {
        case .claude: return .claude
        case .codex: return .gpt
        case .deepseek: return .deepseek
        case .antigravity: return .gemini
        case .zcode: return .glm
        }
    }
}

/// The program's model mark as a tile.
struct ExecutorLogo: View {
    let executor: Executor
    var size: CGFloat = 40
    var body: some View {
        ModelLogoTile(mark: executor.modelMark, size: size)
            .accessibilityHidden(true)
    }
}
