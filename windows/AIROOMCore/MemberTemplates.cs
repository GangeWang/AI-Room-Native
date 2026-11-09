namespace AIROOMCore;

/// <summary>
/// A ready-made look for a member (upstream's 形象模板): name, motto, color and
/// built-in artwork. A template only fills in the card; which program the member
/// runs on is chosen separately and can be changed later.
/// </summary>
public sealed record MemberTemplate(
    string Id,
    string Name,
    string Motto,
    string Color,
    string? Art,
    PortraitFaceAnchor Face,
    string? PlannedProgram,
    Executor SuggestedExecutor)
{
    /// <summary>Framing for an uploaded portrait with no known face position.</summary>
    public static readonly PortraitFaceAnchor InitialFace = new(0.5, 0.15, 3);

    /// <summary>Faces: offAice/src/renderer/members.tsx, BUILTIN_ART (pinned upstream artwork).</summary>
    public static readonly MemberTemplate[] All =
    [
        new("claudia", "Claudia", "翻開書，先把問題想清楚。", "#d9692f",
            "claudia.webp", new PortraitFaceAnchor(0.500, 0.123, 3.6), null, Executor.Claude),
        new("gptia", "GPTia", "交給我，程式碼我來理順。", "#7b6fd6",
            "gptia.webp", new PortraitFaceAnchor(0.487, 0.113, 3.6), null, Executor.Codex),
        new("deepseek", "DS", "收到！馬上開工～", "#3c66d9",
            "ds.webp", new PortraitFaceAnchor(0.533, 0.118, 3.3), null, Executor.DeepSeek),
        new("gemini", "Gemini", "星星還在路上，很快見。", "#a35bdb",
            "gemini.webp", new PortraitFaceAnchor(0.490, 0.128, 3.6), null, Executor.Antigravity),
        new("kimi", "Kimi", "長篇資料交給我，讀完再說。", "#2f8f86",
            "kimi.webp", new PortraitFaceAnchor(0.525, 0.150, 3.4), "Kimi CLI", Executor.Claude),
        new("glm", "GLM", "先列好步驟，再一步步做完。", "#3d8b4f",
            "glm.jpg", new PortraitFaceAnchor(0.540, 0.185, 3.4), null, Executor.ZCode),
    ];

    public static MemberTemplate? Named(string? id) =>
        id is null ? null : Array.Find(All, template => template.Id == id) is { } found && found.Id == id ? found : null;

    /// <summary>The look the two built-in members have always had.</summary>
    public static MemberTemplate? BuiltIn(MemberID member) => member switch
    {
        _ when member == MemberID.Claude => Named("claudia"),
        _ when member == MemberID.Codex => Named("gptia"),
        _ => null,
    };
}
