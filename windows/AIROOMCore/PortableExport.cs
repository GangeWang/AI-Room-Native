using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIROOMCore;

/// <summary>
/// Everything needed to move AIROOM to another machine: the saved state plus
/// every attachment (pictures, avatars, files), sealed with a password instead
/// of this machine's DPAPI/Keychain key. CLI sign-ins and API keys are never
/// part of it. The file layout matches the macOS export byte for byte.
/// </summary>
public sealed class PortableExport
{
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public SavedState State { get; set; } = new();
    /// <summary>Attachment bytes by attachment id (UUID string).</summary>
    public Dictionary<string, byte[]> Attachments { get; set; } = [];

    public PortableExport() { }

    public PortableExport(SavedState state, Dictionary<string, byte[]> attachments, DateTimeOffset? createdAt = null)
    {
        Version = 1;
        CreatedAt = createdAt ?? DateTimeOffset.Now;
        State = state;
        Attachments = attachments;
    }
}

public static class PortableExportStateExtensions
{
    /// <summary>Settings that name programs and folders on the machine that made the export:
    /// custom program paths and the default work folder. An import never keeps them: they
    /// decide which executables AIROOM runs (and, for ZCode, which one is handed the
    /// sign-in), so a file from someone else must not be able to set them.</summary>
    public static List<string> MachineSettings(this SavedState state) =>
        ExecutorList.All.Select(executor => state.Settings.ConfiguredPath(executor))
            .Append(state.Settings.WorkspacePath)
            .Select(path => path.Trim())
            .Where(path => path.Length > 0).ToList();

    public static void ClearMachineSettings(this SavedState state)
    {
        foreach (var executor in ExecutorList.All) state.Settings.SetPath("", executor);
        state.Settings.WorkspacePath = "";
    }

    /// <summary>Folders that topics and projects point at, for the owner to look over before importing.</summary>
    public static List<string> ReferencedFolders(this SavedState state)
    {
        var seen = new HashSet<string>();
        return state.Conversations.Select(conversation => conversation.WorkFolder)
            .Concat(state.ProjectFlows is { } flows ? flows.Select(flow => flow.Folder) : [])
            .Select(path => path.Trim())
            .Where(path => path.Length > 0 && seen.Add(path)).ToList();
    }
}

public sealed class PortableExportError : Exception
{
    PortableExportError(string message) : base(message) { }

    public static PortableExportError PasswordTooShort(int minimum) => new($"密碼至少需要 {minimum} 個字。");
    public static PortableExportError NotAnExport => new("這個檔案不是 AIROOM 匯出檔。");
    public static PortableExportError UnsupportedVersion => new("這個匯出檔由更新版的 AIROOM 製作，請先更新 AIROOM。");
    public static PortableExportError WrongPasswordOrDamaged => new("密碼錯誤，或檔案損壞、被改動過。");
    public static PortableExportError TooLarge => new("匯出檔太大，無法開啟。");
}

/// <summary>
/// File layout: "AIROOMX1\n" · salt (16 bytes) · PBKDF2 rounds (UInt32, big-endian) · AES-256-GCM box.
/// The key comes from the password with PBKDF2-HMAC-SHA256; the header is authenticated
/// with the payload, so changing the salt or rounds also fails to open. Identical to
/// the macOS codec, so an export from one platform opens on the other.
/// </summary>
public static class PortableExportCodec
{
    public const string FileExtension = "airoomexport";
    public const int MinimumPasswordLength = 8;
    public const uint DefaultRounds = 600_000;
    const uint MinimumRounds = 10_000;
    const uint MaximumRounds = 10_000_000;
    const int SaltLength = 16;
    public const int MaximumBytes = 1024 * 1024 * 1024;

    static readonly byte[] Magic = "AIROOMX1\n"u8.ToArray();
    static int HeaderLength => Magic.Length + SaltLength + 4;

    /// <summary>Whether the data starts like an export (no password needed).</summary>
    public static bool LooksLikeExport(byte[] data) =>
        data.Length >= Magic.Length && data.AsSpan().StartsWith(Magic);

    /// <summary>Serializes exactly like the macOS encoder: property names lower-camel,
    /// dates as seconds since the 2001 reference date (Apple's default JSON date).</summary>
    static JsonSerializerOptions ExportOptions() => new(JsonSerializerOptions.Default)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new AppleDateConverter(), new MemberIDJsonConverter() },
    };

    public static byte[] Seal(PortableExport export, string password, uint rounds = DefaultRounds)
    {
        if (password.Length < MinimumPasswordLength) throw PortableExportError.PasswordTooShort(MinimumPasswordLength);
        if (rounds < MinimumRounds || rounds > MaximumRounds) throw PortableExportError.UnsupportedVersion;
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(export, ExportOptions());
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var header = new byte[Magic.Length + SaltLength + 4];
        Buffer.BlockCopy(Magic, 0, header, 0, Magic.Length);
        Buffer.BlockCopy(salt, 0, header, Magic.Length, SaltLength);
        var bigEndian = BitConverter.GetBytes(rounds);
        if (BitConverter.IsLittleEndian) Array.Reverse(bigEndian);
        Buffer.BlockCopy(bigEndian, 0, header, Magic.Length + SaltLength, 4);
        return [.. header, .. Encrypt(plaintext, password, salt, rounds, header)];
    }

    public static PortableExport Open(byte[] data, string password)
    {
        if (data.Length > MaximumBytes) throw PortableExportError.TooLarge;
        if (!LooksLikeExport(data) || data.Length <= HeaderLength) throw PortableExportError.NotAnExport;
        var salt = data[Magic.Length..(Magic.Length + SaltLength)];
        var rounds = ((uint)data[Magic.Length + SaltLength] << 24) | ((uint)data[Magic.Length + SaltLength + 1] << 16)
            | ((uint)data[Magic.Length + SaltLength + 2] << 8) | data[Magic.Length + SaltLength + 3];
        if (rounds < MinimumRounds || rounds > MaximumRounds) throw PortableExportError.UnsupportedVersion;
        byte[] plaintext;
        try
        {
            plaintext = Decrypt(data[HeaderLength..], password, salt, rounds, data[..HeaderLength]);
        }
        catch (Exception error) when (error is AuthenticationTagMismatchException or CryptographicException)
        {
            throw PortableExportError.WrongPasswordOrDamaged;
        }
        PortableExport export;
        try
        {
            export = JsonSerializer.Deserialize<PortableExport>(plaintext, ExportOptions())
                ?? throw PortableExportError.WrongPasswordOrDamaged;
        }
        catch (JsonException)
        {
            throw PortableExportError.WrongPasswordOrDamaged;
        }
        if (export.Version != 1) throw PortableExportError.UnsupportedVersion;
        return export;
    }

    static byte[] DeriveKey(string password, byte[] salt, uint rounds)
    {
        // Same characters typed on another machine may arrive in another Unicode
        // form: normalize to NFC, exactly like macOS's canonical mapping.
        var secret = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        using var derive = new Rfc2898DeriveBytes(secret, salt, (int)rounds, HashAlgorithmName.SHA256);
        return derive.GetBytes(32);
    }

    static byte[] Encrypt(byte[] plaintext, string password, byte[] salt, uint rounds, byte[] header)
    {
        using var aes = new AesGcm(DeriveKey(password, salt, rounds), 16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        aes.Encrypt(nonce, plaintext, ciphertext, tag, header);
        // CryptoKit's "combined" layout: nonce ‖ ciphertext ‖ tag.
        var combined = new byte[12 + plaintext.Length + 16];
        Buffer.BlockCopy(nonce, 0, combined, 0, 12);
        Buffer.BlockCopy(ciphertext, 0, combined, 12, plaintext.Length);
        Buffer.BlockCopy(tag, 0, combined, 12 + plaintext.Length, 16);
        return combined;
    }

    static byte[] Decrypt(byte[] combined, string password, byte[] salt, uint rounds, byte[] header)
    {
        if (combined.Length < 12 + 16) throw PortableExportError.WrongPasswordOrDamaged;
        var nonce = combined[..12];
        var body = combined[12..^16];
        var tag = combined[^16..];
        using var aes = new AesGcm(DeriveKey(password, salt, rounds), 16);
        var plaintext = new byte[body.Length];
        aes.Decrypt(nonce, body, tag, plaintext, header);
        return plaintext;
    }
}

/// <summary>Apple's JSON encoder stores dates as seconds since 2001-01-01 (UTC).
/// Reading them keeps a macOS-made export importable here; the Windows export
/// uses the same convention, so macOS can read ours back.</summary>
public sealed class AppleDateConverter : JsonConverter<DateTimeOffset>
{
    static readonly DateTimeOffset Reference = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number
            ? Reference.AddSeconds(reader.GetDouble())
            : DateTimeOffset.Parse(reader.GetString() ?? throw new JsonException());

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((value - Reference).TotalSeconds);
}
