using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace AIROOMCore;

/// <summary>Normalized face anchor and source-width magnification used by the
/// original AIROOM UI. Values describe the artwork, not a fixed pixel
/// displacement, so small sidebar avatars and larger member cards use the same
/// crop without stretching a portrait into a square.</summary>
public sealed record PortraitFaceAnchor(double X, double Y, double Zoom)
{
    /// <summary>Ensure a crop always fills the full circular viewport and uses
    /// reasonable magnification. Coordinates remain normalized to the ORIGINAL artwork.</summary>
    public PortraitFaceAnchor Constrained(double sourceWidth, double sourceHeight, (double Min, double Max)? zoomRange = null)
    {
        var (min, max) = zoomRange ?? (2.5, 7.0);
        if (!double.IsFinite(sourceWidth) || sourceWidth <= 0 || !double.IsFinite(sourceHeight) || sourceHeight <= 0
            || !double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Zoom)) return this;
        var zoom = Math.Clamp(Zoom, min, max);
        var xMargin = 0.5 / zoom;
        var heightRatio = sourceHeight / sourceWidth;
        var yMargin = 0.5 / (zoom * heightRatio);
        if (!double.IsFinite(yMargin) || yMargin > 0.5) return this;
        return new PortraitFaceAnchor(Math.Clamp(X, xMargin, 1 - xMargin), Math.Clamp(Y, yMargin, 1 - yMargin), zoom);
    }

    /// <summary>The user drags the ART in screen pixels, whereas X/Y encode the
    /// face focus in original-image coordinates. Dragging right decreases X.</summary>
    public PortraitFaceAnchor Translated(double dragX, double dragY, double diameter,
                                         double sourceWidth, double sourceHeight)
    {
        if (!double.IsFinite(diameter) || diameter <= 0 || !double.IsFinite(dragX) || !double.IsFinite(dragY)
            || !double.IsFinite(sourceWidth) || sourceWidth <= 0
            || !double.IsFinite(sourceHeight) || sourceHeight <= 0
            || !double.IsFinite(Zoom) || Zoom <= 0) return this;
        var displayedWidth = diameter * Zoom;
        var displayedHeight = displayedWidth * sourceHeight / sourceWidth;
        if (!double.IsFinite(displayedWidth) || !double.IsFinite(displayedHeight) || displayedHeight <= 0) return this;
        return new PortraitFaceAnchor(X - dragX / displayedWidth, Y - dragY / displayedHeight, Zoom)
            .Constrained(sourceWidth, sourceHeight);
    }
}

/// <summary>Geometry for placing the original, undistorted portrait behind a
/// circular clipping mask. The selected point in the source image lands exactly
/// at the circle's center.</summary>
public sealed record PortraitCropLayout(double ImageWidth, double ImageHeight, double CenterX, double CenterY)
{
    public double OriginX => CenterX - ImageWidth / 2;
    public double OriginY => CenterY - ImageHeight / 2;

    public static PortraitCropLayout? Make(double diameter, double sourceWidth, double sourceHeight, PortraitFaceAnchor anchor)
    {
        if (!double.IsFinite(diameter) || diameter <= 0
            || !double.IsFinite(sourceWidth) || sourceWidth <= 0
            || !double.IsFinite(sourceHeight) || sourceHeight <= 0
            || !double.IsFinite(anchor.X) || anchor.X is < 0 or > 1
            || !double.IsFinite(anchor.Y) || anchor.Y is < 0 or > 1
            || !double.IsFinite(anchor.Zoom) || anchor.Zoom < 1) return null;
        var width = diameter * anchor.Zoom;
        var height = width * sourceHeight / sourceWidth;
        if (!double.IsFinite(height) || height <= 0) return null;
        var centerX = diameter / 2 + (0.5 - anchor.X) * width;
        var centerY = diameter / 2 + (0.5 - anchor.Y) * height;
        return new PortraitCropLayout(width, height, centerX, centerY);
    }
}

/// <summary>Portrait shift from its default framing, as fractions of the frame's width and height.</summary>
public sealed record PortraitOffset(double X, double Y)
{
    public PortraitOffset Clamped =>
        new(double.IsFinite(X) ? Math.Clamp(X, -1, 1) : 0, double.IsFinite(Y) ? Math.Clamp(Y, -1, 1) : 0);
}

public enum MemorySource
{
    Manual,
    Imported,
    Proposed,
}

public sealed class MemoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public MemorySource Source { get; set; } = MemorySource.Manual;

    /// <summary>Where it came from, in words, e.g. "Codex 記憶摘要" or "Claude · 話題「週末計畫」".</summary>
    public string Origin { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Kept on the card but not sent to the member.</summary>
    public bool Off { get; set; }

    public MemoryEntry() { }

    public MemoryEntry(string text, MemorySource source = MemorySource.Manual, string origin = "")
    {
        Text = text.Trim();
        Source = source;
        Origin = origin[..Math.Min(origin.Length, 80)];
    }
}

/// <summary>The four parts of a day a routine describes; local hours, `night` wraps past midnight.</summary>
public enum RoutinePeriod
{
    Morning,
    Day,
    Evening,
    Night,
}

public static class RoutinePeriodExtensions
{
    public static string Title(this RoutinePeriod period) => period switch
    {
        RoutinePeriod.Morning => "早上",
        RoutinePeriod.Day => "白天",
        RoutinePeriod.Evening => "傍晚",
        RoutinePeriod.Night => "深夜",
        _ => throw new ArgumentOutOfRangeException(nameof(period)),
    };

    public static (int From, int To) Hours(this RoutinePeriod period) => period switch
    {
        RoutinePeriod.Morning => (6, 11),
        RoutinePeriod.Day => (11, 17),
        RoutinePeriod.Evening => (17, 22),
        RoutinePeriod.Night => (22, 6),
        _ => throw new ArgumentOutOfRangeException(nameof(period)),
    };

    public static RoutinePeriod At(int hour) => RoutinePeriodExtensions.All
        .FirstOrDefault(period =>
        {
            var (from, to) = period.Hours();
            return from < to ? hour >= from && hour < to : hour >= from || hour < to;
        }, RoutinePeriod.Night);

    public static RoutinePeriod At(DateTimeOffset date) => At(date.LocalDateTime.Hour);

    public static readonly RoutinePeriod[] All = [RoutinePeriod.Morning, RoutinePeriod.Day, RoutinePeriod.Evening, RoutinePeriod.Night];
}

/// <summary>Speaking preference in free chat.</summary>
public enum Talkativeness
{
    Quiet,
    Normal,
    Chatty,
}

public static class TalkativenessExtensions
{
    public static string Title(this Talkativeness value) => value switch
    {
        Talkativeness.Quiet => "安靜",
        Talkativeness.Normal => "一般",
        Talkativeness.Chatty => "健談",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static double Weight(this Talkativeness value) => value switch
    {
        Talkativeness.Quiet => 0.6,
        Talkativeness.Normal => 1,
        Talkativeness.Chatty => 1.6,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

/// <summary>How a member relates to a teammate; part of the persona.</summary>
public sealed class MemberRelation
{
    public MemberID Agent { get; set; }
    public string Text { get; set; } = "";

    public MemberRelation() { }

    public MemberRelation(MemberID agent, string text)
    {
        Agent = agent;
        Text = text;
    }
}

/// <summary>Persisted user-defined agent cards, separate from the vendor's CLI credentials.</summary>
public sealed class MemberCard
{
    public const int UserTitleLimit = 40;
    public const int MottoLimit = 80;
    public const int PersonaLimit = 8_000;
    public static readonly (double Min, double Max) PortraitScaleRange = (0.6, 1.6);

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Who this is. Built-in members keep "Claude Code" / "Codex"; added members get their own.</summary>
    public MemberID Agent { get; set; }

    /// <summary>The program this member runs on.</summary>
    public Executor Executor { get; set; }

    /// <summary>The built-in look this card started from (MemberTemplate.id); null shows the name's initial.</summary>
    public string? Template { get; set; }
    public string Name { get; set; } = "";
    public string Persona { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Optional per-person face framing. Null preserves the pinned original defaults.</summary>
    public PortraitFaceAnchor? AvatarCrop { get; set; }

    /// <summary>Long-term memory sent with the persona (entries that are on).</summary>
    public List<MemoryEntry> Memories { get; set; } = [];

    /// <summary>Suggested by the member from a topic; waiting for the owner to keep or drop.</summary>
    public List<MemoryEntry> MemoryProposals { get; set; } = [];

    /// <summary>What the member is usually doing in each part of the day, by RoutinePeriod.</summary>
    public Dictionary<string, string> Routine { get; set; } = [];
    public List<string> Quirks { get; set; } = [];
    public List<MemberRelation> Relations { get; set; } = [];
    public Talkativeness Talkativeness { get; set; } = Talkativeness.Normal;

    /// <summary>How this member addresses the owner; empty means the owner's own name.</summary>
    public string UserTitle { get; set; } = "";

    /// <summary>One line shown on the team cards.</summary>
    public string Motto { get; set; } = "";

    /// <summary>"#rrggbb"; empty means the built-in color.</summary>
    public string Color { get; set; } = "";

    /// <summary>Used when a topic or room doesn't choose; empty follows the CLI's own default.</summary>
    public string DefaultModel { get; set; } = "";
    public string DefaultEffort { get; set; } = "";

    /// <summary>Uploaded artwork in the encrypted attachment store; null means the built-in art.</summary>
    public ChatAttachment? Portrait { get; set; }
    public double? PortraitScale { get; set; }
    public PortraitOffset? PortraitOffsetValue { get; set; }

    /// <summary>A separate uploaded avatar, and its crop; without one the avatar is cut from the portrait.</summary>
    public ChatAttachment? Avatar { get; set; }
    public PortraitFaceAnchor? AvatarImageCrop { get; set; }

    /// <summary>Presentation only: left off the home page and the sidebar.</summary>
    public bool CardHidden { get; set; }

    [JsonPropertyName("portraitOffset")]
    public PortraitOffset? PortraitOffsetCompat
    {
        get => PortraitOffsetValue;
        set => PortraitOffsetValue = value;
    }

    public MemberCard() { }

    public MemberCard(MemberID agent, Executor? executor = null, string? template = null,
                      string? name = null, string persona = "", bool enabled = true,
                      PortraitFaceAnchor? avatarCrop = null)
    {
        Id = Guid.NewGuid();
        Agent = agent;
        Executor = executor ?? agent.BuiltInExecutor ?? Executor.Claude;
        Template = template ?? MemberTemplate.BuiltIn(agent)?.Id;
        Name = name ?? agent.RawValue;
        Persona = persona;
        Enabled = enabled;
        AvatarCrop = avatarCrop;
    }

    /// <summary>What this member calls the owner.</summary>
    public string Address(string ownerName)
    {
        var title = UserTitle.Trim();
        return title.Length == 0 ? ownerName : title;
    }

    /// <summary>Keeps the card's text and framing within bounds; invalid colors fall back to the default.</summary>
    public MemberCard Normalized()
    {
        var card = new MemberCard
        {
            Id = Id,
            Agent = Agent,
            Executor = Executor,
            Template = Template,
            Name = Name[..Math.Min(Name.Length, 40)],
            Persona = Persona[..Math.Min(Persona.Length, PersonaLimit)],
            Enabled = Enabled,
            AvatarCrop = AvatarCrop,
            Memories = Memories,
            MemoryProposals = MemoryProposals,
            Routine = Routine,
            Quirks = Quirks,
            Relations = Relations,
            Talkativeness = Talkativeness,
            UserTitle = UserTitle.Replace("\n", " ")[..Math.Min(UserTitle.Replace("\n", " ").Length, UserTitleLimit)],
            Motto = Motto.Replace("\n", " ")[..Math.Min(Motto.Replace("\n", " ").Length, MottoLimit)],
            Color = Color,
            DefaultModel = ModelCatalog.Sanitize(DefaultModel),
            DefaultEffort = ModelCatalog.Sanitize(DefaultEffort),
            Portrait = Portrait,
            PortraitScale = PortraitScale,
            PortraitOffsetValue = PortraitOffsetValue?.Clamped,
            Avatar = Avatar,
            AvatarImageCrop = AvatarImageCrop,
            CardHidden = CardHidden,
        };
        if (!System.Text.RegularExpressions.Regex.IsMatch(card.Color, "^#[0-9a-fA-F]{6}$")) card.Color = "";
        if (card.PortraitScale is { } scale)
        {
            card.PortraitScale = double.IsFinite(scale)
                ? Math.Clamp(scale, PortraitScaleRange.Min, PortraitScaleRange.Max) : null;
        }
        return card;
    }

    /// <summary>Memory and life settings as persona lines (see MemberLife.Profile).</summary>
    public string Profile(IReadOnlyList<MemberCard> team, string ownerName) =>
        MemberLife.Profile(this, team, ownerName);

    public static List<MemberCard> Defaults() => MemberID.BuiltIn.Select(agent => new MemberCard(agent)).ToList();

    /// <summary>A new member from a template (or blank), with its own id.</summary>
    public static MemberCard Added(MemberTemplate? template, Executor executor)
    {
        var card = new MemberCard(MemberID.Make(), executor, template?.Id, template?.Name ?? "新成員");
        card.Motto = template?.Motto ?? "";
        card.Color = template?.Color ?? "";
        return card;
    }

    /// <summary>The built-in look, if this card uses one.</summary>
    public MemberTemplate? Look => MemberTemplate.Named(Template);
}

// MARK: Member long-term memory and daily life, after upstream AIROOM:
// memory entries (handwritten / imported / proposed, each can be off) are sent
// with the persona every turn; routine, quirks and relations are the owner's
// character settings — shown as a status line and used for small talk, never
// claimed as real events.

public static class MemberLife
{
    public const int ActiveLimit = 8_000;
    public const int EntryLimit = 2_000;
    public const int EntriesMax = 200;
    public const int QuirksMax = 8;
    public const int QuirkLimit = 40;
    public const int RoutineLimit = 60;
    public const int RelationLimit = 120;

    public static string ActiveText(IReadOnlyList<MemoryEntry> entries) =>
        string.Join("\n", entries.Where(entry => !entry.Off).Select(entry => entry.Text));

    /// <summary>Oversized input is refused rather than cut.</summary>
    public static void Validate(IReadOnlyList<MemoryEntry> entries)
    {
        if (entries.Count > EntriesMax) throw new NativeCLIError($"長期記憶最多 {EntriesMax} 條");
        if (entries.Any(entry => entry.Text.Length > EntryLimit)) throw new NativeCLIError($"每條長期記憶最多 {EntryLimit} 字");
        if (ActiveText(entries).Length > ActiveLimit)
            throw new NativeCLIError($"啟用中的長期記憶合計超過 {ActiveLimit} 字，請精簡或關掉部分條目");
    }

    public static string Clean(string text, int max) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))[..Math.Min(
            string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Length, max)];

    /// <summary>Memory and life settings as persona lines. Empty when the card has none.</summary>
    public static string Profile(MemberCard member, IReadOnlyList<MemberCard> team, string ownerName)
    {
        var parts = new List<string>();
        var title = member.UserTitle.Trim();
        if (title.Length > 0) parts.Add($"【稱呼】稱呼{ownerName}為「{title}」。");
        var memory = member.Memories.Where(entry => !entry.Off).Select(entry => $"- {entry.Text}").ToList();
        if (memory.Count > 0)
        {
            parts.Add($"【長期記憶】{ownerName}希望你記得的事（只用這些，不要編造沒寫的經歷）：\n" + string.Join("\n", memory));
        }
        var life = new List<string>();
        var routine = RoutinePeriodExtensions.All
            .Select(period => member.Routine.TryGetValue(period.ToString().ToLowerInvariant(), out var raw)
                ? Clean(raw, RoutineLimit) is { Length: > 0 } text ? $"{period.Title()}：{text}" : null : null)
            .Where(line => line is not null).Cast<string>().ToList();
        if (routine.Count > 0) life.Add("作息：" + string.Join("；", routine));
        if (member.Quirks.Count > 0) life.Add("小習慣：" + string.Join("、", member.Quirks));
        var relations = member.Relations
            .Where(relation => relation.Agent != member.Agent)
            .Select(relation => (relation, other: team.FirstOrDefault(card => card.Agent == relation.Agent)))
            .Where(pair => pair.other is not null)
            .Select(pair => Clean(pair.relation.Text, RelationLimit) is { Length: > 0 } text
                ? $"- {pair.other!.Name}：{text}" : null)
            .Where(line => line is not null).Cast<string>().ToList();
        if (relations.Count > 0) life.Add("和其他成員的關係：\n" + string.Join("\n", relations));
        if (life.Count > 0)
        {
            parts.Add($"【人物日常設定】以下作息、小習慣和關係是{ownerName}填寫的角色設定，不是真實發生的事；提到時要說明是設定或想像，不可聲稱實際做過。\n" + string.Join("\n", life));
        }
        return string.Join("\n", parts);
    }

    // MARK: Continued CLI conversations

    /// <summary>A short fingerprint of what a member is told about themself, so an
    /// edit made while a CLI conversation continues can be passed on.</summary>
    public static string Fingerprint(string persona, string profile)
    {
        var text = persona.Trim() + "\u001F" + profile;
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Whether a continued conversation needs the current persona and memory
    /// again. Conversations from before fingerprints were kept had none of it.</summary>
    public static bool NeedsRefresh(string? seen, string persona, string profile)
    {
        if (seen is null) return persona.Trim().Length > 0 || profile.Length > 0;
        return seen != Fingerprint(persona, profile);
    }

    public static string RefreshNote(string persona, string profile, string ownerName)
    {
        var personaText = persona.Trim();
        return
            $"（系統說明：{ownerName}更新了你的人物設定或長期記憶。以下是最新版本，取代這段對話裡先前的版本；先前有、這裡沒有的記憶就不要再用。）\n" +
            (personaText.Length == 0 ? "人物設定：（沒有設定）" : $"你的人物設定：{personaText[..Math.Min(personaText.Length, 1_500)]}") + "\n" +
            (profile.Length == 0 ? "長期記憶與日常設定：（沒有）" : profile);
    }
}
