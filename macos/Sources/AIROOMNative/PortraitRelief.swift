import SwiftUI
import AppKit
import Vision
import CoreImage
import CryptoKit
import AIROOMCore

/// Portraits with their background lifted off, for the relief card faces.
///
/// A cut-out is the portrait (at most 720 px high) through a subject mask:
/// - Built-in portraits use the masks baked into the app (`Scripts/bake-portrait-masks.swift`),
///   so showing them never loads Vision.
/// - Uploaded portraits get their mask from Vision's subject lifting (the same engine as
///   Finder's "Remove Background") the first time, and the mask is kept, sealed with the
///   app's Keychain key like the uploads themselves, in Application Support/AIROOMNative/
///   portrait-masks. A missing, damaged or out-of-date mask file is simply made again.
/// The saved portrait itself is never changed.
@MainActor
final class PortraitCutouts: ObservableObject {
    static let shared = PortraitCutouts()
    @Published private(set) var images: [String: NSImage] = [:]
    private var started: Set<String> = []

    /// The cut-out for this portrait, or nil while it is being made (or if no subject was found).
    func image(for key: String, source: NSImage, stateRoot: URL?) -> NSImage? {
        if let done = images[key] { return done }
        guard !started.contains(key),
              let cg = source.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        started.insert(key)
        let size = source.size
        var baked: URL?
        var cacheFile: URL?
        if key.hasPrefix("art:") {
            baked = StudioAsset.url(PortraitMasks.bakedName(forArt: String(key.dropFirst(4))))
        } else if key.hasPrefix("upload:"), let root = stateRoot {
            cacheFile = PortraitMasks.cacheFile(in: root, id: String(key.dropFirst(7)))
        }
        Task.detached(priority: .utility) { [baked, cacheFile] in
            guard let cut = PortraitMasks.cutout(cg, baked: baked, cacheFile: cacheFile) else { return }
            await MainActor.run {
                let image = NSImage(cgImage: cut, size: size)
                withAnimation(StudioMotion.easeOut(0.3)) { self.images[key] = image }
            }
        }
        return nil
    }
}

/// Making, keeping and applying subject masks. Everything here runs off the main thread.
enum PortraitMasks {
    /// Cut-outs and masks are kept at most this many pixels high; the cards show about 230 pt.
    static let maxHeight: CGFloat = 720
    private static let magic = Data("AIROOMMASK1\n".utf8)
    private static let context = CIContext(options: [.cacheIntermediates: false])

    static func bakedName(forArt art: String) -> String {
        ((art as NSString).lastPathComponent as NSString).deletingPathExtension + "-mask.png"
    }

    static func cacheFile(in root: URL, id: String) -> URL? {
        guard let uuid = UUID(uuidString: id) else { return nil }
        return root.appendingPathComponent("portrait-masks", isDirectory: true)
            .appendingPathComponent(uuid.uuidString + ".aesgcm")
    }

    static func cutout(_ image: CGImage, baked: URL?, cacheFile: URL?) -> CGImage? {
        let mask: CIImage
        if let baked, let found = CIImage(contentsOf: baked), fits(found, image) {
            mask = found
        } else if let cacheFile {
            let digest = Data(SHA256.hash(data: (image.dataProvider?.data as Data?) ?? Data()))
            if let kept = load(cacheFile, digest: digest), fits(kept, image) {
                mask = kept
            } else {
                guard let png = lift(image), let made = CIImage(data: png) else { return nil }
                store(png, digest: digest, to: cacheFile)
                mask = made
            }
        } else {
            guard let png = lift(image), let made = CIImage(data: png) else { return nil }
            mask = made
        }
        return apply(mask, to: image)
    }

    /// A mask made for another picture (e.g. after the artwork was replaced) is not used.
    private static func fits(_ mask: CIImage, _ image: CGImage) -> Bool {
        guard mask.extent.height > 0, image.height > 0 else { return false }
        let a = mask.extent.width / mask.extent.height, b = CGFloat(image.width) / CGFloat(image.height)
        return abs(a - b) < 0.01
    }

    /// Vision's subject mask as an 8-bit grayscale PNG, at most `maxHeight` high.
    private static func lift(_ image: CGImage) -> Data? {
        let request = VNGenerateForegroundInstanceMaskRequest()
        let handler = VNImageRequestHandler(cgImage: image)
        do {
            try handler.perform([request])
            guard let result = request.results?.first, !result.allInstances.isEmpty else { return nil }
            let buffer = try result.generateScaledMaskForImage(forInstances: result.allInstances, from: handler)
            var mask = CIImage(cvPixelBuffer: buffer)
            let scale = min(1, maxHeight / mask.extent.height)
            mask = mask.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
            return context.pngRepresentation(of: mask, format: .L8, colorSpace: CGColorSpace(name: CGColorSpace.linearGray)!)
        } catch {
            return nil
        }
    }

    /// The picture, scaled down to at most `maxHeight`, with everything outside the mask cleared.
    private static func apply(_ mask: CIImage, to image: CGImage) -> CGImage? {
        var picture = CIImage(cgImage: image)
        let scale = min(1, maxHeight / picture.extent.height)
        if scale < 1 {
            picture = picture.applyingFilter("CILanczosScaleTransform",
                                             parameters: [kCIInputScaleKey: scale, kCIInputAspectRatioKey: 1])
        }
        let target = CGRect(x: 0, y: 0, width: max(1, picture.extent.width.rounded(.down)),
                            height: max(1, picture.extent.height.rounded(.down)))
        let fitted = mask.transformed(by: CGAffineTransform(scaleX: target.width / mask.extent.width,
                                                            y: target.height / mask.extent.height))
        let cut = picture.applyingFilter("CIBlendWithMask", parameters: [kCIInputBackgroundImageKey: CIImage.empty(),
                                                                          kCIInputMaskImageKey: fitted])
        return context.createCGImage(cut, from: target)
    }

    /// File: magic, then (SHA-256 of the portrait's pixels + mask PNG) sealed with the app's key.
    private static func load(_ file: URL, digest: Data) -> CIImage? {
        guard let data = try? Data(contentsOf: file), data.starts(with: magic),
              let plain = try? KeychainStateProtector().open(Data(data.dropFirst(magic.count))),
              plain.count > digest.count, plain.prefix(digest.count) == digest else { return nil }
        return CIImage(data: Data(plain.dropFirst(digest.count)))
    }

    /// Best effort: if the mask can't be kept it is just made again next launch. Never written unsealed.
    private static func store(_ png: Data, digest: Data, to file: URL) {
        let folder = file.deletingLastPathComponent()
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
                                                    attributes: [.posixPermissions: 0o700])
            let sealed = try KeychainStateProtector().seal(digest + png)
            try (magic + sealed).write(to: file, options: [.atomic])
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        } catch {}
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
    /// Under the pointer: the figure rises off the card and its pressed shadow opens a little.
    var lifted = false

    var body: some View {
        let color = agent.studioColor
        let source = model.portraitImage(agent)
        let cut = source.flatMap { cutouts.image(for: model.portraitKey(agent), source: $0, stateRoot: model.stateRoot) }
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
                        .shadow(color: .black.opacity(scheme == .dark ? 0.5 : 0.28), radius: lifted ? 12 : 8, x: 3, y: lifted ? 11 : 7)
                        .offset(y: lifted ? -4 : 0)
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

    /// The figure's own shape pressed into the stock, set about an eighth of a body-width
    /// to the side: it reads as the figure's thickness, like an acrylic stand.
    private func relief(_ cut: NSImage, color: Color) -> some View {
        let base = LinearGradient(colors: [color.opacity(scheme == .dark ? 0.32 : 0.26), color.opacity(scheme == .dark ? 0.22 : 0.16)],
                                  startPoint: .top, endPoint: .bottom)
        // The character fills roughly two thirds of the picture's width, so an eighth of her body ≈ 0.08 of it.
        let shift: CGFloat = lifted ? 0.095 : 0.08
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
