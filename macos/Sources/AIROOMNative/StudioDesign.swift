import SwiftUI
import AppKit
import AIROOMCore

// Native reconstruction of AIROOM's warm paper / lamplight design system.
// The original Electron stylesheet is not loaded; all controls are SwiftUI.
//
// One palette, one spacing scale, one set of radii and type roles. Views should
// reach for these tokens instead of literal numbers so every page reads as one app.
enum StudioTheme {
    static let amber = Color(red: 0.90, green: 0.62, blue: 0.30)
    static let orange = Color(red: 0.88, green: 0.43, blue: 0.19)
    static let violet = Color(red: 0.49, green: 0.44, blue: 0.86)
    static let moss = Color(red: 0.27, green: 0.58, blue: 0.39)
    static let warning = Color(red: 0.71, green: 0.47, blue: 0.16)
    static let sky = Color(red: 0.27, green: 0.52, blue: 0.86)
    static let teal = Color(red: 0.18, green: 0.58, blue: 0.60)
    static let rose = Color(red: 0.86, green: 0.36, blue: 0.50)
    static let plum = Color(red: 0.62, green: 0.38, blue: 0.74)
    static let slate = Color(red: 0.47, green: 0.46, blue: 0.50)
    /// The lamplight accent: used for primary buttons, the brand mark and selection.
    static let accentGradient = LinearGradient(colors: [Color(red: 0.93, green: 0.58, blue: 0.27), orange],
                                               startPoint: .topLeading, endPoint: .bottomTrailing)

    static func background(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color(red: 0.095, green: 0.082, blue: 0.074) : Color(red: 0.962, green: 0.938, blue: 0.902)
    }
    static func sidebar(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color(red: 0.122, green: 0.104, blue: 0.092) : Color(red: 0.935, green: 0.898, blue: 0.848)
    }
    static func surface(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color(red: 0.145, green: 0.126, blue: 0.112) : Color(red: 1.0, green: 0.994, blue: 0.978)
    }
    /// A surface one step above `surface`: inputs, chips and nested rows.
    static func elevated(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color(red: 0.18, green: 0.158, blue: 0.14) : Color(red: 0.985, green: 0.968, blue: 0.94)
    }
    static func secondary(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color(red: 0.165, green: 0.142, blue: 0.125) : Color(red: 0.975, green: 0.952, blue: 0.92)
    }
    static func border(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color(red: 0.255, green: 0.222, blue: 0.195) : Color(red: 0.878, green: 0.838, blue: 0.784)
    }
    /// Hairline inside a panel (dividers, row separators).
    static func hairline(_ scheme: ColorScheme) -> Color {
        border(scheme).opacity(scheme == .dark ? 0.75 : 0.85)
    }
    static func ink(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color(red: 0.95, green: 0.92, blue: 0.88) : Color(red: 0.20, green: 0.16, blue: 0.13)
    }
    static func subtleFill(_ scheme: ColorScheme) -> Color {
        ink(scheme).opacity(scheme == .dark ? 0.06 : 0.045)
    }
    static func shadow(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? .black.opacity(0.28) : Color(red: 0.42, green: 0.30, blue: 0.18).opacity(0.075)
    }
}

/// Page padding and corner radii.
enum StudioMetrics {
    /// Outer padding of every scrolling page.
    static let page: CGFloat = 28
    static let radiusControl: CGFloat = 10
    static let radiusCard: CGFloat = 14
    static let radiusPanel: CGFloat = 16
}

/// Type roles. Rounded display faces for headings, the system face for reading.
enum StudioType {
    static let eyebrow = Font.system(size: 11, weight: .bold, design: .rounded)
    static let pageTitle = Font.system(size: 28, weight: .bold, design: .rounded)
    static let heroTitle = Font.system(size: 34, weight: .bold, design: .rounded)
    static let sectionTitle = Font.system(size: 15, weight: .semibold, design: .rounded)
}

enum StudioI18n {
    static func t(_ zh: String, _ en: String, _ language: InterfaceLanguage) -> String {
        language == .english ? en : zh
    }
}

/// Every member's card, for views that only know a MemberID; kept current by WorkbenchModel.
enum MemberDirectory {
    nonisolated(unsafe) static var cards: [MemberID: MemberCard] = [:]
    /// Names of removed members, by id, for their old lines.
    nonisolated(unsafe) static var formerNames: [String: String] = [:]
}

extension Color {
    /// "#rrggbb"; nil for anything else.
    init?(hex: String) {
        guard hex.range(of: #"^#[0-9a-fA-F]{6}$"#, options: .regularExpression) != nil,
              let value = UInt32(hex.dropFirst(), radix: 16) else { return nil }
        self.init(red: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }

    var hexString: String? {
        guard let rgb = NSColor(self).usingColorSpace(.sRGB) else { return nil }
        let part = { (v: CGFloat) in Int((min(max(v, 0), 1) * 255).rounded()) }
        return String(format: "#%02x%02x%02x", part(rgb.redComponent), part(rgb.greenComponent), part(rgb.blueComponent))
    }
}

extension MemberID {
    private var card: MemberCard? { MemberDirectory.cards[self] }
    var studioColor: Color { card.flatMap { Color(hex: $0.color) } ?? builtInColor }
    /// The template's color, else the two classic studio colors.
    var builtInColor: Color {
        if let hex = card?.look?.color ?? MemberTemplate.builtIn(for: self)?.color, let color = Color(hex: hex) { return color }
        return self == .codex ? StudioTheme.violet : StudioTheme.orange
    }
    /// The card's name; a removed member's last name; else the id.
    var shortName: String { card?.name ?? MemberDirectory.formerNames[rawValue] ?? rawValue }
    /// The program this member runs on.
    var executor: Executor { card?.executor ?? builtInExecutor ?? .claude }
    /// The built-in artwork file, if the card uses a template that has one.
    var templateArt: String? { (card?.look ?? (card == nil ? MemberTemplate.builtIn(for: self) : nil))?.art }
    /// Shown when there is no artwork: the name's first letter.
    var initial: String { shortName.first.map { String($0).uppercased() } ?? "?" }
}

extension Executor {
    /// "Claude" / "Codex", for labels that name the program.
    var shortName: String {
        switch self {
        case .claude: return "Claude"
        case .codex: return "Codex"
        case .deepseek: return "DeepSeek"
        case .antigravity: return "Antigravity"
        case .zcode: return "ZCode"
        }
    }
}

/// A small status capsule: colored dot, short text.
struct StudioStatusBadge: View {
    var text: String
    var tint: Color
    var icon: String? = nil
    var body: some View {
        HStack(spacing: 5) {
            if let icon { Image(systemName: icon).font(.system(size: 10, weight: .bold)) }
            else { Circle().fill(tint).frame(width: 6, height: 6) }
            Text(text).font(.caption.weight(.semibold)).lineLimit(1)
        }
        .foregroundStyle(tint)
        .padding(.horizontal, 9).padding(.vertical, 4)
        .background(tint.opacity(0.12), in: Capsule())
        .overlay(Capsule().strokeBorder(tint.opacity(0.25), lineWidth: 1))
    }
}

/// The card every page is built from: warm surface, hairline border, a soft lift.
struct StudioPanel<Content: View>: View {
    @Environment(\.colorScheme) private var scheme
    var padding: CGFloat = 18
    @ViewBuilder var content: Content
    var body: some View {
        content
            .padding(padding)
            // Panels span their column so stacked panels line up on both edges.
            .frame(maxWidth: .infinity, alignment: .leading)
            .studioCard(radius: StudioMetrics.radiusPanel)
    }
}

private struct StudioCardModifier: ViewModifier {
    @Environment(\.colorScheme) private var scheme
    var radius: CGFloat
    var fill: Color?
    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
        content
            .background {
                shape.fill(fill ?? StudioTheme.surface(scheme))
                    // A faint lamplight wash from the top edge gives the panel some depth.
                    .overlay(shape.fill(LinearGradient(
                        colors: [Color.white.opacity(scheme == .dark ? 0.025 : 0.5), .clear],
                        startPoint: .top, endPoint: .center)))
                    .shadow(color: StudioTheme.shadow(scheme), radius: scheme == .dark ? 6 : 10, y: scheme == .dark ? 2 : 4)
            }
            .overlay(shape.strokeBorder(StudioTheme.border(scheme), lineWidth: 1))
    }
}

extension View {
    /// Surface + border + shadow, the shared look of panels and cards.
    func studioCard(radius: CGFloat = StudioMetrics.radiusCard, fill: Color? = nil) -> some View {
        modifier(StudioCardModifier(radius: radius, fill: fill))
    }
}

/// An inset well for nested boxes inside a panel (lists, previews, file rows).
/// Resolves against the current color scheme, so it can be used anywhere a ShapeStyle goes.
struct StudioWell: ShapeStyle {
    func resolve(in environment: EnvironmentValues) -> Color {
        environment.colorScheme == .dark
            ? Color(red: 0.115, green: 0.099, blue: 0.088)
            : Color(red: 0.958, green: 0.932, blue: 0.895)
    }
}

/// Filled, warm call-to-action button. Honors controlSize, isEnabled and destructive role.
struct StudioPrimaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled
    @Environment(\.controlSize) private var size
    func makeBody(configuration: Configuration) -> some View {
        let small = size == .small || size == .mini
        let shape = RoundedRectangle(cornerRadius: small ? 7 : 9, style: .continuous)
        configuration.label
            .font(small ? .caption.weight(.semibold) : .callout.weight(.semibold))
            .labelStyle(StudioButtonLabelStyle())
            .foregroundStyle(.white)
            .padding(.horizontal, small ? 9 : 13).padding(.vertical, small ? 3.5 : 6)
            .background {
                if configuration.role == .destructive {
                    shape.fill(Color.red.opacity(0.85))
                } else {
                    shape.fill(StudioTheme.accentGradient)
                }
            }
            .overlay(shape.strokeBorder(Color.white.opacity(0.16), lineWidth: 1))
            .shadow(color: StudioTheme.orange.opacity(enabled ? 0.22 : 0), radius: 4, y: 1.5)
            .brightness(configuration.isPressed ? -0.07 : 0)
            .opacity(enabled ? 1 : 0.42)
            .contentShape(shape)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}

/// Quiet bordered button on the warm surface; the app-wide default.
struct StudioSecondaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled
    @Environment(\.controlSize) private var size
    @Environment(\.colorScheme) private var scheme
    func makeBody(configuration: Configuration) -> some View {
        let small = size == .small || size == .mini
        let shape = RoundedRectangle(cornerRadius: small ? 7 : 9, style: .continuous)
        configuration.label
            .font(small ? .caption.weight(.medium) : .callout.weight(.medium))
            .labelStyle(StudioButtonLabelStyle())
            .foregroundStyle(configuration.role == .destructive ? Color.red : StudioTheme.ink(scheme))
            .padding(.horizontal, small ? 9 : 12).padding(.vertical, small ? 3.5 : 6)
            .background(shape.fill(StudioTheme.elevated(scheme)))
            .overlay(shape.fill(configuration.isPressed ? StudioTheme.subtleFill(scheme) : .clear))
            .overlay(shape.strokeBorder(StudioTheme.border(scheme), lineWidth: 1))
            .opacity(enabled ? 1 : 0.45)
            .contentShape(shape)
    }
}

private struct StudioButtonLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 5) {
            configuration.icon.imageScale(.small)
            configuration.title
        }
    }
}

extension ButtonStyle where Self == StudioPrimaryButtonStyle {
    static var studioPrimary: StudioPrimaryButtonStyle { StudioPrimaryButtonStyle() }
}
extension ButtonStyle where Self == StudioSecondaryButtonStyle {
    static var studioSecondary: StudioSecondaryButtonStyle { StudioSecondaryButtonStyle() }
}

struct StudioBrandMark: View {
    var size: CGFloat = 36
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
                .fill(LinearGradient(colors: [Color(red: 0.29, green: 0.22, blue: 0.18), Color(red: 0.17, green: 0.13, blue: 0.11)],
                                     startPoint: .top, endPoint: .bottom))
            RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
                .strokeBorder(Color.white.opacity(0.12), lineWidth: 1)
            // A soft lamp glow behind the three members.
            Circle().fill(StudioTheme.amber.opacity(0.35)).blur(radius: size * 0.16)
                .frame(width: size * 0.62, height: size * 0.62)
            Circle().fill(StudioTheme.orange)
                .frame(width: size * 0.27, height: size * 0.27)
                .offset(x: -size * 0.19, y: size * 0.14)
            Circle().fill(StudioTheme.violet)
                .frame(width: size * 0.27, height: size * 0.27)
                .offset(x: size * 0.19, y: size * 0.14)
            Circle().fill(Color(red: 0.30, green: 0.50, blue: 0.88))
                .frame(width: size * 0.27, height: size * 0.27)
                .offset(y: -size * 0.18)
        }
        .frame(width: size, height: size)
        .shadow(color: .black.opacity(0.18), radius: 3, y: 1.5)
        .accessibilityLabel("AIROOM")
    }
}

/// Face crop shared by the sidebar, member card, chat bubbles and room participants.
/// Original artwork is a tall portrait. The face is NOT at the image centre;
/// preserve its aspect ratio and align the original artwork's measured face anchor.
struct StudioFaceArtwork: View {
    @EnvironmentObject private var model: WorkbenchModel
    let agent: MemberID
    let size: CGFloat
    /// Supplied by the editor for live preview; otherwise read the saved card.
    var previewAnchor: PortraitFaceAnchor? = nil

    var body: some View {
        GeometryReader { geometry in
            if let source = model.avatarSource(agent),
               let crop = PortraitCropLayout.make(
                   diameter: Double(geometry.size.width),
                   sourceWidth: Double(source.image.size.width),
                   sourceHeight: Double(source.image.size.height),
                   anchor: source.fitted(previewAnchor ?? source.anchor)
               ) {
                Image(nsImage: source.image)
                    .resizable()
                    .interpolation(.high)
                    .frame(width: CGFloat(crop.imageWidth), height: CGFloat(crop.imageHeight))
                    .position(x: CGFloat(crop.centerX), y: CGFloat(crop.centerY))
            } else {
                Text(agent.initial)
                    .font(.system(size: geometry.size.width * 0.46, weight: .bold, design: .rounded))
                    .foregroundStyle(agent.studioColor)
                    .frame(width: geometry.size.width, height: geometry.size.height)
            }
        }
        .frame(width: size, height: size)
        .clipShape(Circle())
        // The portrait is drawn several times larger than the circle and only clipped:
        // without this, its invisible overflow still took clicks, and a desk header's
        // avatar covered the sidebar's chat buttons.
        .contentShape(Circle())
        .accessibilityHidden(true)
    }
}

/// What a member's avatar is cut from: an uploaded avatar, else the (uploaded or built-in) portrait.
struct AvatarSource {
    let image: NSImage
    /// The saved crop, or the default for this picture.
    let anchor: PortraitFaceAnchor
    let defaultAnchor: PortraitFaceAnchor
    let zoomRange: ClosedRange<Double>
    /// Whether the crop belongs to an uploaded avatar (avatarImageCrop) or the portrait (avatarCrop).
    let isUploadedAvatar: Bool

    func fitted(_ anchor: PortraitFaceAnchor) -> PortraitFaceAnchor {
        anchor.constrained(sourceWidth: Double(image.size.width), sourceHeight: Double(image.size.height), zoomRange: zoomRange)
    }
}

extension WorkbenchModel {
    func card(_ agent: MemberID) -> MemberCard? { archive.members.first { $0.agent == agent } }

    /// Every member, in card order.
    var memberIDs: [MemberID] { archive.members.map(\.agent) }

    /// Members who can be asked to take part (their card is switched on).
    var enabledMemberIDs: [MemberID] { archive.members.filter(\.enabled).map(\.agent) }

    /// Members whose card shows on the home page and in the sidebar.
    var visibleAgents: [MemberID] { archive.members.filter { !$0.cardHidden }.map(\.agent) }

    func executor(of agent: MemberID) -> Executor { card(agent)?.executor ?? agent.builtInExecutor ?? .claude }

    /// The uploaded portrait, else the built-in art.
    func portraitImage(_ agent: MemberID) -> NSImage? {
        memberImage(card(agent)?.portrait) ?? agent.templateArt.flatMap(StudioAsset.image)
    }

    func avatarSource(_ agent: MemberID) -> AvatarSource? {
        let card = card(agent)
        if let image = memberImage(card?.avatar), image.size.width > 0, image.size.height > 0 {
            // An uploaded avatar always covers the circle.
            let cover = max(1, Double(image.size.width / image.size.height))
            let fallback = PortraitFaceAnchor(x: 0.5, y: 0.5, zoom: cover)
            return AvatarSource(image: image, anchor: card?.avatarImageCrop ?? fallback, defaultAnchor: fallback,
                                zoomRange: cover...max(cover, 8), isUploadedAvatar: true)
        }
        if let image = memberImage(card?.portrait) {
            let fallback = PortraitFaceAnchor(x: 0.5, y: 0.15, zoom: 3)
            return AvatarSource(image: image, anchor: card?.avatarCrop ?? fallback, defaultAnchor: fallback,
                                zoomRange: 1...8, isUploadedAvatar: false)
        }
        guard let image = agent.templateArt.flatMap(StudioAsset.image) else { return nil }
        let face = card?.look?.face ?? MemberTemplate.initialFace
        return AvatarSource(image: image, anchor: card?.avatarCrop ?? face, defaultAnchor: face,
                            zoomRange: 2.5...7, isUploadedAvatar: false)
    }
}

struct StudioAvatar: View {
    let agent: MemberID
    var size: CGFloat = 40
    var running = false
    var body: some View {
        ZStack(alignment: .bottomTrailing) {
            Circle().fill(agent.studioColor.opacity(0.15))
                .overlay(Circle().strokeBorder(agent.studioColor.opacity(0.34), lineWidth: 1.4))
                .overlay {
                    StudioFaceArtwork(agent: agent, size: size)
                }
            Circle()
                .fill(running ? StudioTheme.amber : StudioTheme.moss)
                .frame(width: size * 0.23, height: size * 0.23)
                .overlay(Circle().strokeBorder(.background, lineWidth: 2))
        }
        .frame(width: size, height: size)
        .accessibilityLabel(agent.shortName)
    }
}

struct StudioPageHeading: View {
    @Environment(\.colorScheme) private var scheme
    var eyebrow: String
    var title: String
    var subtitle: String
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            StudioEyebrow(text: eyebrow)
            Text(title)
                .font(StudioType.pageTitle)
                .foregroundStyle(StudioTheme.ink(scheme))
            Text(subtitle)
                .font(.callout)
                .foregroundStyle(.secondary)
                .lineSpacing(2)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.bottom, 4)
    }
}

/// Small spaced capitals above a title, led by a short lamplight rule.
struct StudioEyebrow: View {
    var text: String
    var body: some View {
        HStack(spacing: 8) {
            Capsule().fill(StudioTheme.accentGradient).frame(width: 16, height: 3)
            Text(text.uppercased())
                .font(StudioType.eyebrow)
                .tracking(1.8)
                .foregroundStyle(StudioTheme.warning)
        }
    }
}

/// A filled, glossy app-icon style tile: each place in the app gets its own hue,
/// like the rows of macOS System Settings.
struct StudioIconTile: View {
    var symbol: String
    var tint: Color
    var size: CGFloat = 26
    var body: some View {
        let shape = RoundedRectangle(cornerRadius: size * 0.27, style: .continuous)
        Image(systemName: symbol)
            .font(.system(size: size * 0.48, weight: .semibold))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background {
                shape.fill(LinearGradient(colors: [tint.opacity(0.88), tint], startPoint: .top, endPoint: .bottom))
                    .overlay(shape.fill(LinearGradient(colors: [.white.opacity(0.28), .clear],
                                                       startPoint: .top, endPoint: .center)))
            }
            .overlay(shape.strokeBorder(.black.opacity(0.08), lineWidth: 0.5))
            .shadow(color: tint.opacity(0.35), radius: 2.5, y: 1)
            .accessibilityHidden(true)
    }
}

/// Title row of a panel: a tinted icon tile and a rounded heading.
struct StudioSectionTitle: View {
    @Environment(\.colorScheme) private var scheme
    var title: String
    var icon: String
    var tint: Color = StudioTheme.orange
    init(_ title: String, icon: String, tint: Color = StudioTheme.orange) {
        self.title = title; self.icon = icon; self.tint = tint
    }
    var body: some View {
        HStack(spacing: 9) {
            Image(systemName: icon)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(tint)
                .frame(width: 26, height: 26)
                .background(tint.opacity(0.13), in: RoundedRectangle(cornerRadius: 7, style: .continuous))
            Text(title).font(StudioType.sectionTitle).foregroundStyle(StudioTheme.ink(scheme))
        }
        .accessibilityElement(children: .combine)
    }
}

struct StudioNotice: View {
    @Environment(\.colorScheme) private var scheme
    var icon: String = "shield.lefthalf.filled"
    var message: String
    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: icon)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(StudioTheme.warning)
                .frame(width: 16)
                .padding(.top, 1)
            Text(message).font(.caption).foregroundStyle(.secondary)
                .lineSpacing(1.5)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.vertical, 11).padding(.horizontal, 12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(StudioTheme.amber.opacity(scheme == .dark ? 0.08 : 0.1),
                    in: RoundedRectangle(cornerRadius: StudioMetrics.radiusControl, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: StudioMetrics.radiusControl, style: .continuous)
            .strokeBorder(StudioTheme.amber.opacity(0.22), lineWidth: 1))
    }
}

struct StudioPhasePill: View {
    let phase: ProjectPhase
    let language: InterfaceLanguage
    private var color: Color {
        switch phase {
        case .draft: return StudioTheme.warning
        case .executing: return StudioTheme.violet
        case .reviewing: return StudioTheme.orange
        case .accepted: return StudioTheme.moss
        case .rejected, .failed: return .red
        }
    }
    var body: some View {
        HStack(spacing: 5) {
            Circle().fill(color).frame(width: 5, height: 5)
            Text(LocalizedUI.phase(phase, language: language))
        }
        .font(.caption.weight(.semibold))
        .padding(.horizontal, 9).padding(.vertical, 4)
        .background(color.opacity(0.12), in: Capsule())
        .overlay(Capsule().strokeBorder(color.opacity(0.22), lineWidth: 1))
        .foregroundStyle(color)
    }
}

struct StudioEmptyState: View {
    var icon: String
    var title: String
    var detail: String
    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: icon)
                .font(.system(size: 30, weight: .medium))
                .foregroundStyle(StudioTheme.accentGradient)
                .frame(width: 68, height: 68)
                .background(StudioTheme.amber.opacity(0.12), in: Circle())
                .overlay(Circle().strokeBorder(StudioTheme.amber.opacity(0.25), lineWidth: 1))
            Text(title).font(.system(.title3, design: .rounded).weight(.semibold))
            Text(detail).font(.callout).foregroundStyle(.secondary).multilineTextAlignment(.center)
        }
        .padding(30)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Renders the light Markdown models write (headings, **bold**, *italic*,
/// `code`, lists) without turning links into anything clickable.
struct RichText: View {
    let source: String
    var font: Font = .system(size: 14)
    init(_ source: String, font: Font = .system(size: 14)) { self.source = source; self.font = font }

    var body: some View {
        Text(Self.render(source)).font(font)
    }

    static func render(_ text: String) -> AttributedString {
        var result = AttributedString()
        let lines = text.components(separatedBy: "\n")
        for (index, raw) in lines.enumerated() {
            var line = raw
            var heading = false
            let hashes = line.prefix(while: { $0 == "#" }).count
            if (1...6).contains(hashes), line.dropFirst(hashes).first == " " {
                line = String(line.dropFirst(hashes + 1))
                heading = true
            }
            var piece = (try? AttributedString(markdown: line, options: .init(
                allowsExtendedAttributes: false, interpretedSyntax: .inlineOnlyPreservingWhitespace,
                failurePolicy: .returnPartiallyParsedIfPossible))) ?? AttributedString(line)
            // Links stay plain text: model output must never become a click target.
            for run in piece.runs where run.link != nil { piece[run.range].link = nil }
            if heading { piece.inlinePresentationIntent = .stronglyEmphasized }
            result += piece
            if index < lines.count - 1 { result += AttributedString("\n") }
        }
        return result
    }
}
