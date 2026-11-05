import Foundation

/// A ready-made look for a member (upstream's 形象模板): name, motto, color
/// and built-in artwork. A template only fills in the card; which program the
/// member runs on is chosen separately and can be changed later.
public struct MemberTemplate: Identifiable, Equatable, Sendable {
    public let id: String
    public let name: String
    public let motto: String
    /// "#rrggbb".
    public let color: String
    /// Bundled artwork; nil shows the name's first letter until a portrait is uploaded.
    public let art: String?
    /// Where the face sits in the artwork, as fractions of it.
    public let face: PortraitFaceAnchor
    /// The program this character is meant for when AIROOM doesn't run it yet
    /// (e.g. "DeepSeek"); until then the member runs on Claude Code or Codex.
    public let plannedProgram: String?
    public let suggestedExecutor: Executor

    /// Faces: offAice/src/renderer/members.tsx, BUILTIN_ART (pinned upstream artwork).
    public static let all: [MemberTemplate] = [
        MemberTemplate(id: "claudia", name: "Claudia", motto: "翻開書，先把問題想清楚。", color: "#d9692f",
                       art: "claudia.webp", face: PortraitFaceAnchor(x: 0.500, y: 0.123, zoom: 3.6),
                       plannedProgram: nil, suggestedExecutor: .claude),
        MemberTemplate(id: "gptia", name: "GPTia", motto: "交給我，程式碼我來理順。", color: "#7b6fd6",
                       art: "gptia.webp", face: PortraitFaceAnchor(x: 0.487, y: 0.113, zoom: 3.6),
                       plannedProgram: nil, suggestedExecutor: .codex),
        MemberTemplate(id: "deepseek", name: "DS", motto: "收到！馬上開工～", color: "#3c66d9",
                       art: "ds.webp", face: PortraitFaceAnchor(x: 0.533, y: 0.118, zoom: 3.3),
                       plannedProgram: nil, suggestedExecutor: .deepseek),
        MemberTemplate(id: "gemini", name: "Gemini", motto: "星星還在路上，很快見。", color: "#a35bdb",
                       art: "gemini.webp", face: PortraitFaceAnchor(x: 0.490, y: 0.128, zoom: 3.6),
                       plannedProgram: nil, suggestedExecutor: .antigravity),
        MemberTemplate(id: "kimi", name: "Kimi", motto: "長篇資料交給我，讀完再說。", color: "#2f8f86",
                       art: nil, face: initialFace, plannedProgram: "Kimi CLI", suggestedExecutor: .claude),
        MemberTemplate(id: "glm", name: "GLM", motto: "先列好步驟，再一步步做完。", color: "#3d8b4f",
                       art: nil, face: initialFace, plannedProgram: "ZCode", suggestedExecutor: .claude),
    ]

    /// Framing for an uploaded portrait with no known face position.
    public static let initialFace = PortraitFaceAnchor(x: 0.5, y: 0.15, zoom: 3)

    public static func named(_ id: String?) -> MemberTemplate? {
        guard let id else { return nil }
        return all.first { $0.id == id }
    }

    /// The look the two built-in members have always had.
    public static func builtIn(for member: MemberID) -> MemberTemplate? {
        switch member {
        case .claude: return named("claudia")
        case .codex: return named("gptia")
        default: return nil
        }
    }
}
