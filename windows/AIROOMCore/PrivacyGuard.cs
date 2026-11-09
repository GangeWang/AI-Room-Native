using System.Text;
using System.Text.RegularExpressions;

namespace AIROOMCore;

/// <summary>
/// A defense-in-depth guard, NOT a replacement for sandboxing or vendor CLI
/// privacy controls.
/// </summary>
public static partial class PrivacyGuard
{
    static readonly string[] BlockedDirectories =
    [
        ".git", ".ssh", ".gnupg", ".aws", ".azure", ".config/gcloud", ".kube", ".docker",
        ".npm", ".claude", ".codex", ".cursor", ".vscode", "secrets", "private", "credentials",
        "node_modules", ".env", ".venv", "venv", "__pycache__", ".idea",
    ];

    static readonly string[] BlockedNames =
    [
        ".env", ".env.local", ".env.production", ".env.development", ".npmrc", ".netrc",
        ".pypirc", "id_rsa", "id_ed25519", "id_ecdsa", "known_hosts", "authorized_keys",
        "credentials.json", "auth.json", "secrets.json", "service-account.json", "service_account.json",
        "terraform.tfstate", "terraform.tfstate.backup", "keychain", "login.keychain-db",
    ];

    static readonly string[] BlockedExtensions =
    [
        "pem", "p12", "pfx", "key", "jks", "keystore", "mobileprovision", "tfstate", "db", "sqlite", "sqlite3", "kdbx", "asc", "gpg",
    ];

    [GeneratedRegex(@"(?i)\b(?:sk-(?:ant-|proj-)?[a-z0-9_-]{16,}|gh[opurs]_[a-z0-9_]{20,}|github_pat_[a-z0-9_]{20,}|xox[baprs]-[a-z0-9-]{15,}|AKIA[0-9A-Z]{16})\b")]
    private static partial Regex SecretPattern1();

    [GeneratedRegex(@"(?i)\b(?:Bearer|Basic)\s+[a-z0-9._~+/=-]{12,}")]
    private static partial Regex SecretPattern2();

    [GeneratedRegex(@"(?i)\b(?:API_KEY|ACCESS_TOKEN|REFRESH_TOKEN|CLIENT_SECRET|PASSWORD|AUTH_TOKEN)\s*[:=]\s*['""]?\S{8,}")]
    private static partial Regex SecretPattern3();

    [GeneratedRegex(@"-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----")]
    private static partial Regex SecretPattern4();

    static readonly Regex[] SecretPatterns = [SecretPattern1(), SecretPattern2(), SecretPattern3(), SecretPattern4()];

    static readonly HashSet<string> ScannableText = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "json", "yaml", "yml", "toml", "ini", "xml", "swift", "ts", "tsx", "js", "jsx", "py", "rb", "rs", "go", "c", "h", "cpp", "hpp", "java", "kt", "sh", "zsh", "env", "sql", "css", "html", "gradle", "properties", "conf",
    };

    public static bool BlockedRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').ToLowerInvariant();
        var components = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0) return false;
        for (var index = 0; index < components.Length; index++)
        {
            var part = components[index];
            if (BlockedNames.Contains(part) || BlockedDirectories.Contains(part)) return true;
            if (part.StartsWith(".env.") || part.EndsWith(".secret") || part.EndsWith(".secrets")) return true;
            if (BlockedExtensions.Contains(Path.GetExtension(part).TrimStart('.'))) return true;
            if (index > 0 && index + 1 < components.Length && part == "config"
                && components[index - 1] == ".config" && components[index + 1] == "gcloud") return true;
        }
        return false;
    }

    /// <summary>
    /// Scan text inputs before putting them in a vendor CLI work copy. Unknown
    /// large blobs are not considered safe just because the filename looks innocent.
    /// </summary>
    public static bool UnsafeContent(string filePath, long size)
    {
        if (size > 1_048_576) return ScannableText.Contains(Path.GetExtension(filePath).TrimStart('.'));
        try
        {
            var data = File.ReadAllBytes(filePath);
            if (data.Contains((byte)0)) return false;
            var text = Encoding.UTF8.GetString(data);
            return ContainsSecrets(text);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool ContainsSecrets(string text) => Array.Exists(SecretPatterns, p => p.IsMatch(text));

    public static void ValidatePrompt(string text)
    {
        if (ContainsSecrets(text))
        {
            throw new NativeCLIError("偵測到疑似 API Key、Token 或私鑰。請先移除機密資訊再送出。 / Possible credential detected in prompt.");
        }
    }

    public static string Redact(string text)
    {
        var value = text;
        foreach (var pattern in SecretPatterns)
        {
            value = pattern.Replace(value, "[REDACTED]");
        }
        return value;
    }

    // Environment variables the CLIs may legitimately need on Windows. Deliberately
    // narrow: no inherited API keys, cookies, cloud credentials, SSH agents, NODE_OPTIONS
    // or host-app session tokens reach agent processes.
    static readonly string[] AllowedEnvironment = new[]
    {
        "APPDATA", "COMPUTERNAME", "COMSPEC", "HOMEDRIVE", "HOMEPATH", "LANG", "LC_ALL", "LC_CTYPE",
        "LOCALAPPDATA", "LOGNAME", "NO_COLOR", "NUMBER_OF_PROCESSORS", "OS", "PATH", "PROCESSOR_ARCHITECTURE",
        "PROGRAMDATA", "PROGRAMFILES", "SYSTEMROOT", "TEMP", "TMP", "USERNAME", "USERPROFILE", "WINDIR",
    };

    /// <summary>An explicit allowlist for the environment a CLI process inherits.</summary>
    public static Dictionary<string, string> CliEnvironment(
        IReadOnlyDictionary<string, string> source, string executablePath, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in AllowedEnvironment)
        {
            if (source.TryGetValue(key, out var value) && value.Length > 0)
            {
                environment[key] = value;
            }
        }
        environment.TryAdd("USERPROFILE", home);
        // PATH is rebuilt: the executable's own folder first, so a vendor CLI
        // finds its sibling runtimes, then the system defaults.
        var systemRoot = source.GetValueOrDefault("SYSTEMROOT") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        environment["PATH"] = string.Join(";",
            Path.GetDirectoryName(executablePath) ?? "",
            Path.Combine(systemRoot, "System32"),
            systemRoot,
            Path.Combine(systemRoot, "System32", "Wbem"),
            Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0"));
        environment["AIROOM_CLAUDE_DEBUG"] = "0"; // Native app never enables upstream debug capture.
        return environment;
    }
}
