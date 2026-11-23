import SwiftUI
import AppKit
import AIROOMCore

/// Artwork is fetched from a pinned upstream commit by Scripts/fetch-original-assets.sh.
/// The UI remains usable with SF Symbol placeholders before the opt-in download.
enum StudioAsset {
    static func image(_ name: String) -> NSImage? {
        url(name).flatMap(NSImage.init(contentsOf:))
    }
    /// The packaged file, else the SwiftPM resource (for `swift run`).
    static func url(_ name: String) -> URL? {
        let file = (name as NSString).lastPathComponent
        guard !file.contains(".."), !file.isEmpty else { return nil }
        if let bundled = Bundle.main.resourceURL?.appendingPathComponent("Assets", isDirectory: true)
            .appendingPathComponent(file), FileManager.default.fileExists(atPath: bundled.path) {
            return bundled
        }
        return Bundle.module.url(forResource: (file as NSString).deletingPathExtension,
                                 withExtension: (file as NSString).pathExtension)
    }
    static func backdrop(for date: Date) -> String {
        switch Calendar.current.component(.hour, from: date) {
        case 5..<11: return "morning.webp"
        case 11..<17: return "afternoon.webp"
        case 17..<21: return "evening.webp"
        default: return "night.webp"
        }
    }
}

struct StudioPortrait: View {
    @EnvironmentObject private var model: WorkbenchModel
    let agent: MemberID
    /// The editor's framing while a slider is being dragged.
    var preview: (scale: Double, offset: PortraitOffset)? = nil
    /// Draw this picture (e.g. the cut-out) instead of the saved portrait, with the same framing.
    var image: NSImage? = nil
    /// Draw only the picture's shape, filled with this style (for relief echoes).
    var silhouette: AnyShapeStyle? = nil
    /// Sideways shift as a share of the drawn picture's width.
    var shift: CGFloat = 0
    var body: some View {
        let card = model.card(agent)
        let scale = preview?.scale ?? card?.portraitScale ?? 1
        let offset = preview?.offset ?? card?.portraitOffset ?? PortraitOffset(x: 0, y: 0)
        GeometryReader { geo in
            if let art = image ?? model.portraitImage(agent) {
                let drawnWidth = min(geo.size.width, geo.size.height * art.size.width / max(art.size.height, 1)) * scale
                Image(nsImage: art)
                    .renderingMode(silhouette == nil ? .original : .template)
                    .resizable().scaledToFit()
                    .foregroundStyle(silhouette ?? AnyShapeStyle(Color.primary))
                    .frame(width: geo.size.width, height: geo.size.height, alignment: .top)
                    .scaleEffect(scale, anchor: .top)
                    .offset(x: geo.size.width * offset.x + drawnWidth * shift, y: geo.size.height * offset.y)
                    .frame(width: geo.size.width, height: geo.size.height)
                    .clipped()
                    .accessibilityLabel(agent.shortName)
            } else {
                Text(agent.initial)
                    .font(.system(size: 72, weight: .bold, design: .rounded))
                    .foregroundStyle(agent.studioColor)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
    }
}

struct StudioRoomBackdrop: View {
    @Environment(\.colorScheme) private var scheme
    var body: some View {
        TimelineView(.periodic(from: .now, by: 60)) { context in
            ZStack {
                StudioTheme.background(scheme)
                if let artwork = StudioAsset.image(StudioAsset.backdrop(for: context.date)) {
                    GeometryReader { geo in
                        Image(nsImage: artwork)
                            .resizable().scaledToFill()
                            .frame(width: geo.size.width, height: geo.size.height)
                            .clipped()
                    }
                    .opacity(scheme == .dark ? 0.34 : 0.38)
                }
                StudioTheme.background(scheme).opacity(scheme == .dark ? 0.28 : 0.40)
            }
            .accessibilityHidden(true)
        }
    }
}
