using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIROOMCore;

/// <summary>The official program a member runs on. Several members may share one.</summary>
public enum Executor
{
    [JsonPropertyName("Claude Code")]
    Claude,
    [JsonPropertyName("Codex")]
    Codex,
    [JsonPropertyName("DeepSeek Harness")]
    DeepSeek,
    [JsonPropertyName("Antigravity")]
    Antigravity,
    [JsonPropertyName("ZCode")]
    ZCode,
}

public static class ExecutorExtensions
{
    /// <summary>The CLI binary name as the vendors ship it.</summary>
    public static string ExecutableName(this Executor executor) => executor switch
    {
        Executor.Claude => "claude",
        Executor.Codex => "codex",
        Executor.DeepSeek => "dsh",
        Executor.Antigravity => "agy",
        Executor.ZCode => "zcode",
        _ => throw new ArgumentOutOfRangeException(nameof(executor)),
    };

    /// <summary>Whether images and files can be sent; the others get text only.
    /// ZCode's turn input is a plain content string for now.</summary>
    public static bool AcceptsFiles(this Executor executor) => executor is Executor.Claude or Executor.Codex;

    /// <summary>"Claude" / "Codex", for labels that name the program.</summary>
    public static string ShortName(this Executor executor) => executor switch
    {
        Executor.Claude => "Claude",
        Executor.Codex => "Codex",
        Executor.DeepSeek => "DeepSeek",
        Executor.Antigravity => "Antigravity",
        Executor.ZCode => "ZCode",
        _ => throw new ArgumentOutOfRangeException(nameof(executor)),
    };

    public static Executor? FromRaw(string? raw) => raw switch
    {
        "Claude Code" => Executor.Claude,
        "Codex" => Executor.Codex,
        "DeepSeek Harness" => Executor.DeepSeek,
        "Antigravity" => Executor.Antigravity,
        _ => null,
    };
}

public static class ExecutorList
{
    public static readonly Executor[] All = [Executor.Claude, Executor.Codex, Executor.DeepSeek, Executor.Antigravity, Executor.ZCode];
}

/// <summary>The exact strings the executors serialize as (kept from the Swift rawValues).</summary>
public static class ExecutorName
{
    public static string Of(Executor executor) => executor switch
    {
        Executor.Claude => "Claude Code",
        Executor.Codex => "Codex",
        Executor.DeepSeek => "DeepSeek Harness",
        Executor.Antigravity => "Antigravity",
        Executor.ZCode => "ZCode",
        _ => throw new ArgumentOutOfRangeException(nameof(executor)),
    };
}

/// <summary>
/// Who a member is, independent of the program they run on. The two built-in
/// members keep the ids they always had ("Claude Code", "Codex"), so state saved
/// before members and executors were separated reads unchanged.
/// Serializes as its raw string, exactly like the Swift version.
/// </summary>
public readonly record struct MemberID : IComparable<MemberID>
{
    public string RawValue { get; }

    [JsonConstructor]
    public MemberID(string rawValue) => RawValue = rawValue ?? "";

    public static readonly MemberID Claude = new(ExecutorName.Of(Executor.Claude));
    public static readonly MemberID Codex = new(ExecutorName.Of(Executor.Codex));
    public static readonly MemberID[] BuiltIn = [Claude, Codex];

    /// <summary>A fresh id for an added member, short enough to use as its handle.</summary>
    public static MemberID Make()
    {
        var hex = Guid.NewGuid().ToString("N")[..6];
        return new MemberID("m-" + hex);
    }

    public readonly string Id => RawValue;
    public readonly bool IsBuiltIn => Array.IndexOf(BuiltIn, this) >= 0;

    /// <summary>The key used in prompts, plans and folder names: "claude" / "codex"
    /// for the built-in members, as before; the id itself for added members.</summary>
    public readonly string Handle => this == Claude ? "claude" : this == Codex ? "codex" : RawValue;

    /// <summary>Words that name this member besides the card's name.</summary>
    public readonly string[] Aliases => this == Claude
        ? [RawValue, Handle, "Claude"]
        : this == Codex ? [RawValue, Handle] : [RawValue];

    /// <summary>What the built-in members always ran on; added members say so on their card.</summary>
    public readonly Executor? BuiltInExecutor => this == Claude ? Executor.Claude
        : this == Codex ? Executor.Codex : null;

    public readonly int CompareTo(MemberID other) => string.CompareOrdinal(RawValue, other.RawValue);
    public override readonly string ToString() => RawValue;
}

public sealed class MemberIDJsonConverter : JsonConverter<MemberID>
{
    public override MemberID Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "");

    public override void Write(Utf8JsonWriter writer, MemberID value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.RawValue);
}

public enum MessageRole
{
    User,
    Assistant,
}

public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MessageRole Role { get; set; }
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public List<ChatAttachment> Attachments { get; set; } = [];

    /// <summary>The model the CLI actually reported for this reply.</summary>
    public string? ModelUsed { get; set; }

    public ChatMessage() { }

    public ChatMessage(MessageRole role, string body, List<ChatAttachment>? attachments = null, Guid? id = null)
    {
        Role = role;
        Body = body;
        Attachments = attachments ?? [];
        if (id is { } value) Id = value;
    }
}

/// <summary>
/// One member-desk topic. Each member has one open topic; "new topic" archives
/// it, and any archived topic can be reopened later.
/// </summary>
public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MemberID Agent { get; set; }
    public string Title { get; set; } = TopicNaming.DefaultTitle;
    public List<ChatMessage> Messages { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public bool Archived { get; set; }

    /// <summary>True once the user typed a title; auto-naming then leaves it alone.</summary>
    public bool TitleLocked { get; set; }

    /// <summary>Empty means the CLI's default model / effort.</summary>
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    public string Draft { get; set; } = "";
    public List<ChatAttachment> DraftAttachments { get; set; } = [];

    /// <summary>Folder this topic works on. Empty = the member's personal folder
    /// (edited in place); otherwise a task copy of this folder is edited.</summary>
    public string WorkFolder { get; set; } = "";

    /// <summary>The CLI conversation this topic continues, and the last reply it already contains.</summary>
    public string? SessionID { get; set; }
    public Guid? SessionThrough { get; set; }

    /// <summary>Things the CLI conversation must be told before the next message (e.g. an undone round).</summary>
    public List<string> SessionNotes { get; set; } = [];

    /// <summary>Fingerprint of the persona and memory that conversation was last given.</summary>
    public string? SessionProfile { get; set; }

    public Conversation() { }

    public Conversation(MemberID agent)
    {
        Agent = agent;
    }

    /// <summary>Continue the CLI conversation only when nothing happened since its last reply.</summary>
    [JsonIgnore]
    public bool CanContinueSession => SessionID is not null && SessionThrough is not null
        && Messages is [.., { } last] && last.Id == SessionThrough;
}

public sealed class RoundtableResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MemberID Agent { get; set; }
    public string Text { get; set; } = "";
    public string? Error { get; set; }

    public RoundtableResult() { }

    public RoundtableResult(MemberID agent, string text = "", string? error = null)
    {
        Agent = agent;
        Text = text;
        Error = error;
    }
}

public enum InterfaceLanguage
{
    [JsonPropertyName("zh-Hant")]
    ZhTW,
    [JsonPropertyName("en")]
    English,
}

public static class PromptBuilder
{
    /// <summary>
    /// MVP uses fresh read-only CLI executions, so explicit prior turns preserve
    /// conversational context. This is not a native CLI session resume and can
    /// consume more tokens.
    /// </summary>
    public static string Build(
        IReadOnlyList<ChatMessage> history, string newQuestion, int maxTurns = 8,
        InterfaceLanguage language = InterfaceLanguage.ZhTW, bool canWrite = false,
        string userName = "", string persona = "", string profile = "")
    {
        var tail = history.Skip(Math.Max(0, history.Count - maxTurns * 2));
        var access = canWrite
            ? (language == InterfaceLanguage.English
               ? "You may read and edit files in the current working folder when the request calls for it. Only change files inside this folder; never touch other paths. Summarise what you changed."
               : "需要時可以讀取並修改目前工作資料夾裡的檔案。只修改這個資料夾內的檔案，絕對不要動其他路徑。完成後簡述改了什麼。")
            : (language == InterfaceLanguage.English
               ? "This is a read-only conversation. Do not modify files or execute changes."
               : "本次是唯讀對話，不要修改檔案或執行變更。");
        var lines = new List<string>
        {
            language == InterfaceLanguage.English
                ? "You are a member of the user's AIROOM team, working with them at your desk (your name and persona follow below). Here is recent conversation history. Reply in English unless the user asks otherwise."
                : "你是使用者在 AIROOM 工位的成員（你的名字與人物設定在下方）。以下是本工作台保存的近期對話。請使用繁體中文回覆，除非使用者另有要求。",
            access,
        };
        var name = userName.Trim();
        if (name.Length > 0)
        {
            lines.Add(language == InterfaceLanguage.English ? $"Address the user as {name}." : $"請稱呼使用者為「{name}」。");
        }
        persona = persona.Trim();
        if (persona.Length > 0)
        {
            lines.Add((language == InterfaceLanguage.English ? "Your persona: " : "你的人物設定：") + persona[..Math.Min(persona.Length, 1_500)]);
        }
        if (profile.Length > 0) lines.Add(profile);
        var userLabel = language == InterfaceLanguage.English ? "User" : "使用者";
        var assistantLabel = language == InterfaceLanguage.English ? "Assistant" : "助理";
        foreach (var message in tail)
        {
            if (message.Body.Length == 0) continue;
            var speaker = message.Role == MessageRole.User ? userLabel : assistantLabel;
            var files = message.Attachments.Count == 0
                ? "" : $" [附件：{string.Join("、", message.Attachments.Select(a => a.FileName))}]";
            lines.Add($"{speaker}: {message.Body}{files}");
        }
        lines.Add($"{userLabel}：{newQuestion}");
        return string.Join("\n\n", lines);
    }
}
