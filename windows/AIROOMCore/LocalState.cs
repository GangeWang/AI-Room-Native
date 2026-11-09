using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIROOMCore;

/// <summary>
/// The complete persisted document. Room, roundtable, project, schedule and
/// live-stage sections belong to later phases; they are carried through
/// unchanged (passthrough JSON) so a future version can add them without
/// losing what a previous build wrote.
/// </summary>
public sealed class SavedState
{
    public NativeSettings Settings { get; set; } = new();
    public List<Conversation> Conversations { get; set; } = [];
    public List<MemberCard> Members { get; set; } = MemberCard.Defaults();
    public CallLedger Calls { get; set; } = new();

    /// <summary>Needs-attention items the owner ignored, by id, with the version ignored.</summary>
    public Dictionary<string, string> AttentionDismissals { get; set; } = [];

    /// <summary>Names of removed members, by id, so their old lines still say who wrote them.</summary>
    public Dictionary<string, string> FormerMembers { get; set; } = [];

    // Sections owned by later phases; carried through untouched.
    [JsonPropertyName("rooms")]
    public JsonElement? Rooms { get; set; }
    [JsonPropertyName("projects")]
    public JsonElement? Projects { get; set; }
    [JsonPropertyName("roundtables")]
    public JsonElement? Roundtables { get; set; }
    [JsonPropertyName("schedules")]
    public JsonElement? Schedules { get; set; }
    [JsonPropertyName("autoChat")]
    public JsonElement? AutoChat { get; set; }
    [JsonPropertyName("teamWorkflows")]
    public JsonElement? TeamWorkflows { get; set; }
    [JsonPropertyName("liveShow")]
    public JsonElement? LiveShow { get; set; }
    [JsonPropertyName("liveStage")]
    public JsonElement? LiveStage { get; set; }
    [JsonPropertyName("roundtableTopics")]
    public JsonElement? RoundtableTopics { get; set; }
    [JsonPropertyName("projectFlows")]
    public JsonElement? ProjectFlows { get; set; }
    [JsonPropertyName("dailyOpening")]
    public JsonElement? DailyOpening { get; set; }
    [JsonPropertyName("schedulesPaused")]
    public bool? SchedulesPaused { get; set; }
    [JsonPropertyName("scheduleRuns")]
    public JsonElement? ScheduleRuns { get; set; }
}

/// <summary>An abstract seam for tests. Production Windows always uses DPAPI + AES-GCM.</summary>
public interface IStateProtector
{
    byte[] Seal(byte[] plaintext);
    byte[] Open(byte[] ciphertext);
}

public sealed class StateProtectionError : Exception
{
    public StateProtectionError(string message) : base(message) { }
}

/// <summary>
/// AES-256-GCM with a random 32-byte key protected by DPAPI (CurrentUser).
/// This is the Windows mirror of the macOS Keychain protector: the key is
/// bound to this user on this machine and never leaves it.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpapiStateProtector : IStateProtector
{
    public const int KeySize = 32;
    const int NonceSize = 12;
    const int TagSize = 16;

    readonly byte[] key;

    public DpapiStateProtector(byte[]? key = null)
    {
        this.key = key ?? LoadOrCreateKey();
    }

    static byte[] LoadOrCreateKey()
    {
        var keyFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIROOMNative", "key.bin");
        if (File.Exists(keyFile))
        {
            try
            {
                var protectedKey = File.ReadAllBytes(keyFile);
                var key = System.Security.Cryptography.ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
                if (key.Length == KeySize) return key;
            }
            catch
            {
                // Fall through: an unreadable key file (different user, damaged)
                // gets replaced; the state file then fails to open and the user
                // starts fresh rather than silently mixing keys.
            }
        }
        var fresh = RandomNumberGenerator.GetBytes(KeySize);
        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        File.WriteAllBytes(keyFile, System.Security.Cryptography.ProtectedData.Protect(fresh, null, DataProtectionScope.CurrentUser));
        return fresh;
    }

    public byte[] Seal(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        // CryptoKit's "combined" layout: nonce ‖ ciphertext ‖ tag.
        var combined = new byte[NonceSize + plaintext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize, plaintext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + plaintext.Length, TagSize);
        return combined;
    }

    public byte[] Open(byte[] ciphertext)
    {
        if (ciphertext.Length < NonceSize + TagSize) throw new StateProtectionError("Encrypted state cannot be decoded or authenticated.");
        var nonce = ciphertext.AsSpan(0, NonceSize).ToArray();
        var tag = ciphertext.AsSpan(ciphertext.Length - TagSize, TagSize).ToArray();
        var body = ciphertext.AsSpan(NonceSize, ciphertext.Length - NonceSize - TagSize).ToArray();
        var plaintext = new byte[body.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, body, tag, plaintext);
        }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException)
        {
            throw new StateProtectionError("Encrypted state cannot be decoded or authenticated.");
        }
        return plaintext;
    }
}

public sealed record BackupFile(string Url, DateTimeOffset CreatedAt, long Bytes, bool BeforeRestore)
{
    public string Id => Url;
}

/// <summary>What a backup holds, to look at before restoring it.</summary>
public sealed record BackupSummary(
    int DeskTopics,
    int DeskMessages,
    List<string> MemberNames,
    int Memories,
    string OwnerName)
{
    public static BackupSummary From(SavedState state) => new(
        state.Conversations.Count,
        state.Conversations.Sum(conversation => conversation.Messages.Count),
        state.Members.Select(member => member.Name).ToList(),
        state.Members.Sum(member => member.Memories.Count),
        state.Settings.UserName);
}

/// <summary>Encrypts, stores and backs up the local state document.</summary>
public sealed class LocalStateStore
{
    // AIROOM1\n — same magic as the macOS version; the files themselves are
    // platform-specific (different key stores) and never interchanged.
    static readonly byte[] Magic = "AIROOM1\n"u8.ToArray();

    readonly string protectedState;
    readonly string legacyState;
    readonly IStateProtector? protector;

    public string Root { get; }
    public string AttachmentDirectory => Path.Combine(Root, "attachments");
    public string BackupDirectory => Path.Combine(Root, "backups");

    public LocalStateStore(string? root = null, IStateProtector? customProtector = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIROOMNative");
        protector = customProtector ?? new DpapiStateProtector();
        protectedState = Path.Combine(Root, "state.aesgcm");
        legacyState = Path.Combine(Root, "state.json");
        Directory.CreateDirectory(Root);
    }

    SavedState Decode(byte[] data)
    {
        if (data.AsSpan().StartsWith(Magic))
        {
            if (protector is null) throw new StateProtectionError("Encryption is required.");
            return CoreJson.Deserialize<SavedState>(
                System.Text.Encoding.UTF8.GetString(protector.Open(data[Magic.Length..])));
        }
        if (protector is not null) throw new StateProtectionError("Encrypted state cannot be decoded or authenticated.");
        return CoreJson.Deserialize<SavedState>(System.Text.Encoding.UTF8.GetString(data));
    }

    byte[] Encode(SavedState state)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes(CoreJson.Serialize(state));
        return protector is null ? plaintext : [.. Magic, .. protector.Seal(plaintext)];
    }

    static void ProtectedWrite(byte[] data, string file)
    {
        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
        stream.Write(data);
    }

    public SavedState Load()
    {
        if (File.Exists(protectedState)) return Decode(File.ReadAllBytes(protectedState));
        if (File.Exists(legacyState))
        {
            var value = CoreJson.Deserialize<SavedState>(File.ReadAllText(legacyState));
            if (protector is not null)
            {
                // Fail closed: never erase legacy data before verifying an encrypted write.
                Save(value);
                _ = Decode(File.ReadAllBytes(protectedState));
                File.Delete(legacyState);
            }
            return value;
        }
        return new SavedState();
    }

    public void Save(SavedState state) => ProtectedWrite(Encode(state), protector is null ? legacyState : protectedState);

    // MARK: Attachments — sealed like the state file, one file per attachment.

    public void StoreAttachment(byte[] data, Guid id)
    {
        Directory.CreateDirectory(AttachmentDirectory);
        var sealedData = protector is null ? data : [.. Magic, .. protector.Seal(data)];
        ProtectedWrite(sealedData, Path.Combine(AttachmentDirectory, $"{id.ToString("D")}.aesgcm"));
    }

    public byte[] LoadAttachment(Guid id)
    {
        var data = File.ReadAllBytes(Path.Combine(AttachmentDirectory, $"{id.ToString("D")}.aesgcm"));
        if (!data.AsSpan().StartsWith(Magic))
        {
            if (protector is not null) throw new StateProtectionError("Encrypted state cannot be decoded or authenticated.");
            return data;
        }
        if (protector is null) throw new StateProtectionError("Encryption is required.");
        return protector.Open(data[Magic.Length..]);
    }

    // MARK: Backups — conversations and metadata, never official CLI credentials.

    public string Backup(bool beforeRestore = false)
    {
        Directory.CreateDirectory(BackupDirectory);
        var date = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH-mm-ss.fffZ").Replace(":", "-");
        var name = $"AIROOM-backup-{date}{(beforeRestore ? "-before-restore" : "")}";
        var file = Path.Combine(BackupDirectory, name + (protector is null ? ".json" : ".airoombackup"));
        ProtectedWrite(Encode(Load()), file);
        return file;
    }

    /// <summary>Backups in the backup folder, newest first.</summary>
    public List<BackupFile> Backups()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        return Directory.EnumerateFiles(BackupDirectory)
            .Where(file =>
            {
                var extension = Path.GetExtension(file);
                return extension is ".airoombackup" or ".json"
                    && Path.GetFileName(file).StartsWith("AIROOM-backup-", StringComparison.Ordinal);
            })
            .Select(file => new FileInfo(file))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => new BackupFile(
                info.FullName, new DateTimeOffset(info.LastWriteTimeUtc), info.Length,
                Path.GetFileNameWithoutExtension(info.FullName).EndsWith("-before-restore", StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// Opens a backup without changing anything. Decrypting it is the integrity
    /// check: a damaged or altered file, or one sealed with another machine's
    /// key, fails here.
    /// </summary>
    public BackupSummary Preview(string file) => BackupSummary.From(DecodeBackup(file));

    public SavedState Restore(string file)
    {
        var restored = DecodeBackup(file);
        _ = Backup(beforeRestore: true);
        Save(restored);
        return restored;
    }

    /// <summary>Only a plain file directly inside the backup folder can be deleted.</summary>
    public void DeleteBackup(string file)
    {
        var full = Path.GetFullPath(file);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(BackupDirectory), StringComparison.OrdinalIgnoreCase)
            || !Backups().Any(backup => backup.Url == full))
        {
            throw new NativeCLIError($"不允許符號連結、特殊檔案或不安全路徑：{Path.GetFileName(file)}");
        }
        File.Delete(full);
    }

    SavedState DecodeBackup(string file)
    {
        var data = File.ReadAllBytes(file);
        if (data.Length > 50 * 1024 * 1024) throw new NativeCLIError($"專案超出安全複製限制：{Path.GetFileName(file)}");
        return Decode(data);
    }
}
