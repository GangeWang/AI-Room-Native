using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIROOMCore;

public enum Speaker
{
    User,
    Assistant,
    System,
}

/// <summary>A streamed reply is never eligible for another model's context until
/// it has completed successfully. Legacy persisted entries decode as completed.</summary>
public enum RoomDelivery
{
    Completed,
    InProgress,
    Interrupted,
    Failed,
}

public class RoomEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Speaker Speaker { get; set; }
    public MemberID? Agent { get; set; }
    public string DisplayName { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public RoomDelivery Delivery { get; set; } = RoomDelivery.Completed;
    /// <summary>An emoji-only response (body is the emoji).</summary>
    public bool IsReaction { get; set; }
    /// <summary>Meeting minutes written at the end of a meeting.</summary>
    public bool IsMinutes { get; set; }
    /// <summary> Pictures on this entry (image generation is a later phase); the
    /// field is kept so room state round-trips.</summary>
    public List<RoomImage> Images { get; set; } = [];

    /// <summary>Earlier app versions stored an unfinished or failed response as
    /// display text with no status. Do not mistake those placeholders for
    /// trustworthy model output during migration.</summary>
    public static RoomDelivery LegacyDelivery(Speaker speaker, string body)
    {
        var marker = body.Trim().ToLowerInvariant();
        string[] incomplete = ["執行中", "running…", "running...", "結果待核對", "result needs verification"];
        return speaker == Speaker.Assistant && incomplete.Any(marker.StartsWith)
            ? RoomDelivery.Interrupted : RoomDelivery.Completed;
    }
}

public class RoomImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ChatAttachment Attachment { get; set; } = new();
    public string RevisedPrompt { get; set; } = "";
}

public class RoomSession
{
    public string Id { get; set; } = "";
    /// <summary>The newest room entry this member's CLI conversation already contains.</summary>
    public Guid Through { get; set; }
    /// <summary>Fingerprint of the persona and memory that conversation was last given.</summary>
    public string? Profile { get; set; }

    public RoomSession() { }
    public RoomSession(string id, Guid through, string? profile = null)
    {
        Id = id; Through = through; Profile = profile;
    }
}

public class TeamRoom
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "茶歇室";
    public List<RoomEntry> Entries { get; set; } = [];
    public List<MemberID> Participants { get; set; } = [MemberID.Claude, MemberID.Codex];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public LoungeState Lounge { get; set; } = new();
    /// <summary>Compressed context: prompts use this summary plus entries after SummaryThroughEntryID.</summary>
    public string Summary { get; set; } = "";
    public Guid? SummaryThroughEntryID { get; set; }
    /// <summary>Summary of the previous topic, carried into this one when it was opened.</summary>
    public string CarriedSummary { get; set; } = "";
    /// <summary>Set for a roundtable's deliberation room, which is hidden from the lounge.</summary>
    public Guid? RoundtableID { get; set; }
    /// <summary>Material every member should know (roundtable versions, issue list), outside the transcript.</summary>
    public string Briefing { get; set; } = "";
    /// <summary>Set for a project room: members may read (never edit) a copy of this folder.</summary>
    public Guid? ProjectID { get; set; }
    public string ReadFolder { get; set; } = "";
    /// <summary>Each member's own CLI conversation in this room, and the last entry it has seen.</summary>
    public Dictionary<string, RoomSession> Sessions { get; set; } = [];

    /// <summary>Hidden from the lounge: roundtable and project rooms live in their own pages.</summary>
    [JsonIgnore]
    public bool IsLounge => RoundtableID is null && ProjectID is null;
}

/// <summary>An immutable transcript snapshot is sent independently to each official CLI.
/// It is NOT a shared vendor-side session: each model receives the same selected
/// room history as explicitly attributed, untrusted text.</summary>
public sealed record SharedContextSnapshot(IReadOnlyList<RoomEntry> Entries, int OmittedEntries, string Transcript)
{
    public int IncludedCount => Entries.Count;
    public int TotalCharacters => Transcript.Length;
}

public static class RoomPromptBuilder
{
    /// <summary>A hard cap avoids the CLI's 30k character input ceiling and runaway costs.
    /// Most recent *completed* entries take priority; failures, drafts, and system
    /// notifications cannot be mistaken for another agent's genuine reply.</summary>
    public static SharedContextSnapshot Snapshot(TeamRoom room, int maxEntries = 48,
        int maxCharacters = 22_000, int perEntryLimit = 4_000, InterfaceLanguage language = InterfaceLanguage.ZhTW)
    {
        bool Eligible(RoomEntry entry) =>
            (entry.Speaker == Speaker.User || entry.Speaker == Speaker.Assistant)
            && entry.Delivery == RoomDelivery.Completed
            && entry.Body.Trim().Length > 0;
        var limit = Math.Max(0, Math.Min(maxCharacters, 26_000));
        var countLimit = Math.Max(0, maxEntries);
        var entryLimit = Math.Max(0, Math.Min(perEntryLimit, limit));
        var chosen = new List<(RoomEntry Entry, string Formatted)>();
        var used = 0;
        var eligibleList = room.Entries.Where(Eligible).ToList();
        for (var i = eligibleList.Count - 1; i >= 0; i--)
        {
            var entry = eligibleList[i];
            if (chosen.Count >= countLimit) break;
            // Preserve the entire latest user input (bounded to 8k by the
            // workbench) before spending the context budget on older turns.
            var size = entry.Speaker == Speaker.User && chosen.Count == 0
                ? Math.Min(limit, Math.Max(entryLimit, 8_000)) : entryLimit;
            // Pictures themselves aren't sent; members only learn one was posted.
            var pictures = entry.Images.Count == 0 ? "" : (entry.Speaker == Speaker.User
                ? $"\n（附了 {entry.Images.Count} 張參考圖片）"
                : $"\n（畫了 {entry.Images.Count} 張圖片）");
            var body = (size < entry.Body.Length ? entry.Body[..size] : entry.Body) + pictures;
            var kind = entry.Speaker == Speaker.User
                ? (language == InterfaceLanguage.English ? "USER" : "使用者") : "AI";
            var label = entry.Speaker == Speaker.User
                ? $"{kind} [{Truncate(entry.DisplayName, 40)}]"
                : $"{kind} [{entry.Agent?.RawValue ?? "unknown"} / {entry.DisplayName}]";
            var formatted = entry.IsReaction ? $"[{label}]（用表情回應：{body}）" : $"[{label}]\n{body}";
            if (used + formatted.Length + 2 > limit)
            {
                // Preserve the newest messages rather than skipping them for
                // older short messages, which would distort chronology.
                if (chosen.Count == 0 && limit > label.Length + 10)
                {
                    var available = Math.Max(0, limit - label.Length - 4);
                    var shortBody = body.Length > available ? body[^available..] : body;
                    chosen.Add((entry, $"[{label}]\n{shortBody}"));
                }
                break;
            }
            chosen.Add((entry, formatted));
            used += formatted.Length + 2;
        }
        chosen.Reverse();
        var ordered = chosen.Select(item => item.Entry).ToList();
        return new SharedContextSnapshot(ordered, eligibleList.Count - chosen.Count,
            string.Join("\n\n", chosen.Select(item => item.Formatted)));
    }

    static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
