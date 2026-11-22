import SwiftUI
import AppKit
import Vision
import CoreImage
import AIROOMCore

/// Portraits with their background lifted off, made on this Mac with Vision's subject
/// lifting (the same engine as Finder's "Remove Background"). Kept in memory only;
/// nothing is written to disk and the saved portrait is never changed.
@MainActor
final class PortraitCutouts: ObservableObject {
    static let shared = PortraitCutouts()
    @Published private(set) var images: [String: NSImage] = [:]
    private var started: Set<String> = []

    /// The cut-out for this portrait, or nil while it is being made (or if no subject was found).
    func image(for key: String, source: NSImage) -> NSImage? {
        if let done = images[key] { return done }
        guard !started.contains(key),
              let cg = source.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        started.insert(key)
        let size = source.size
        Task.detached(priority: .utility) {
            guard let cut = Self.liftSubject(cg) else { return }
            await MainActor.run {
                let image = NSImage(cgImage: cut, size: size)
                withAnimation(.easeOut(duration: 0.35)) { self.images[key] = image }
            }
        }
        return nil
    }

    /// Same pixel size as the source, so a member's saved framing still lines up.
    nonisolated private static func liftSubject(_ image: CGImage) -> CGImage? {
        let request = VNGenerateForegroundInstanceMaskRequest()
        let handler = VNImageRequestHandler(cgImage: image)
        do {
            try handler.perform([request])
            guard let result = request.results?.first, !result.allInstances.isEmpty else { return nil }
            let buffer = try result.generateMaskedImage(ofInstances: result.allInstances, from: handler,
                                                        croppedToInstancesExtent: false)
            let ci = CIImage(cvPixelBuffer: buffer)
            return CIContext().createCGImage(ci, from: ci.extent)
        } catch {
            return nil
        }
    }
}

extension WorkbenchModel {
    /// Identifies the picture `portraitImage` returns, for caching its cut-out.
    func portraitKey(_ agent: MemberID) -> String {
        if let upload = card(agent)?.portrait, memberImage(upload) != nil { return "upload:\(upload.id)" }
        return "art:\(agent.templateArt ?? agent.rawValue)"
    }
}

/// A character card face in relief: the member's colour as card stock, their silhouette
/// pressed into it, and the cut-out figure standing in front with a soft rim light.
/// Shows the plain portrait until the cut-out is ready.
struct StudioReliefPortrait: View {
    @EnvironmentObject private var model: WorkbenchModel
    @ObservedObject private var cutouts = PortraitCutouts.shared
    @Environment(\.colorScheme) private var scheme
    let agent: MemberID
    /// Space above the figure's head.
    var topInset: CGFloat = 14

    var body: some View {
        let color = agent.studioColor
        let source = model.portraitImage(agent)
        let cut = source.flatMap { cutouts.image(for: model.portraitKey(agent), source: $0) }
        ZStack {
            stock(color)
            if let cut {
                relief(cut, color: color)
                    .transition(.opacity)
            }
            spotlight(color)
            Group {
                if let cut {
                    StudioPortrait(agent: agent, image: cut)
                        // Rim light in the member's colour, then a drop shadow onto the card.
                        .shadow(color: color.opacity(scheme == .dark ? 0.55 : 0.4), radius: 10)
                        .shadow(color: .black.opacity(scheme == .dark ? 0.5 : 0.28), radius: 8, x: 3, y: 7)
                        .transition(.opacity)
                } else {
                    StudioPortrait(agent: agent)
                }
            }
            .padding(.top, topInset)
            .padding(.horizontal, 8)
        }
        .compositingGroup()
        .allowsHitTesting(false)
    }

    /// Tinted card stock with a faint engraved line texture.
    private func stock(_ color: Color) -> some View {
        ZStack {
            StudioTheme.surface(scheme)
            LinearGradient(colors: [color.opacity(scheme == .dark ? 0.34 : 0.26), color.opacity(scheme == .dark ? 0.16 : 0.1)],
                           startPoint: .top, endPoint: .bottom)
            Canvas { ctx, size in
                var lines = Path()
                let step: CGFloat = 7
                var x = -size.height
                while x < size.width {
                    lines.move(to: CGPoint(x: x, y: size.height))
                    lines.addLine(to: CGPoint(x: x + size.height, y: 0))
                    x += step
                }
                ctx.stroke(lines, with: .color(.white.opacity(scheme == .dark ? 0.035 : 0.16)), lineWidth: 0.6)
            }
        }
    }

    /// The figure's own shape pressed into the stock, set about a sixth of a body-width
    /// to the side: it reads as the figure's thickness, like an acrylic stand.
    private func relief(_ cut: NSImage, color: Color) -> some View {
        let base = LinearGradient(colors: [color.opacity(scheme == .dark ? 0.32 : 0.26), color.opacity(scheme == .dark ? 0.22 : 0.16)],
                                  startPoint: .top, endPoint: .bottom)
        // The character fills roughly two thirds of the picture's width, so a sixth of her body ≈ 0.11 of it.
        let shift: CGFloat = 0.11
        func echo(_ style: some ShapeStyle) -> some View {
            StudioPortrait(agent: agent, image: cut, silhouette: AnyShapeStyle(style), shift: shift)
        }
        return ZStack {
            echo(Color.white.opacity(scheme == .dark ? 0.22 : 0.7)).offset(x: -0.8, y: -0.8)
            echo(Color.black.opacity(scheme == .dark ? 0.65 : 0.28)).offset(x: 0.9, y: 0.9)
            echo(StudioTheme.surface(scheme))
            echo(base)
        }
        .padding(.top, topInset)
        .padding(.horizontal, 8)
        .accessibilityHidden(true)
    }

    private func spotlight(_ color: Color) -> some View {
        RadialGradient(colors: [Color.white.opacity(scheme == .dark ? 0.1 : 0.4), .clear],
                       center: UnitPoint(x: 0.5, y: 0.3), startRadius: 0, endRadius: 150)
    }
}

/// A raised rim for a card: light along the top edge, shade along the bottom.
struct StudioBevel: ViewModifier {
    @Environment(\.colorScheme) private var scheme
    var radius: CGFloat
    func body(content: Content) -> some View {
        content.overlay {
            RoundedRectangle(cornerRadius: radius, style: .continuous)
                .inset(by: 1)
                .strokeBorder(LinearGradient(colors: [.white.opacity(scheme == .dark ? 0.16 : 0.75), .clear, .black.opacity(scheme == .dark ? 0.35 : 0.08)],
                                             startPoint: .top, endPoint: .bottom), lineWidth: 1.2)
                .allowsHitTesting(false)
        }
    }
}

extension View {
    func studioBevel(radius: CGFloat) -> some View { modifier(StudioBevel(radius: radius)) }
}
