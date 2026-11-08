using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIROOMCore;

public sealed class NativeSettings
{
    public string ClaudePath { get; set; } = "";
    public string CodexPath { get; set; } = "";

    /// <summary>The dsh launcher; empty finds the DeepSeek Harness install (not shipped on Windows yet).</summary>
    public string DeepseekPath { get; set; } = "";
    public string AntigravityPath { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public InterfaceLanguage Language { get; set; } = InterfaceLanguage.ZhTW;

    /// <summary>How members address the user (設定 → 個人資料). Empty = a neutral default.</summary>
    public string UserName { get; set; } = "";

    /// <summary>Look for a newer release on GitHub (at most once a day).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Name used in prompts for the human owner.</summary>
    [JsonIgnore]
    public string OwnerName
    {
        get
        {
            var name = UserName.Trim();
            return name.Length > 0 ? name : (Language == InterfaceLanguage.English ? "the user" : "使用者");
        }
    }

    public string ConfiguredPath(Executor executor) => executor switch
    {
        Executor.Claude => ClaudePath,
        Executor.Codex => CodexPath,
        Executor.DeepSeek => DeepseekPath,
        Executor.Antigravity => AntigravityPath,
        _ => throw new ArgumentOutOfRangeException(nameof(executor)),
    };

    public void SetPath(string path, Executor executor)
    {
        switch (executor)
        {
            case Executor.Claude: ClaudePath = path; break;
            case Executor.Codex: CodexPath = path; break;
            case Executor.DeepSeek: DeepseekPath = path; break;
            case Executor.Antigravity: AntigravityPath = path; break;
            default: throw new ArgumentOutOfRangeException(nameof(executor));
        }
    }
}

public enum ExecutionAccess
{
    /// <summary>No tools.</summary>
    ReadOnly,
    /// <summary>May read the (copied) working folder.</summary>
    ReadFiles,
    /// <summary>May edit it.</summary>
    StagedWrite,
}

public sealed class NativeCLIError : Exception
{
    public NativeCLIError(string message) : base(message) { }

    public static NativeCLIError ExecutableMissing(string name) => new(name switch
    {
        "claude" => "找不到 Claude Code。請先在 Claude 桌面 App 開啟 Code 分頁安裝內附執行端，或在設定指定 claude 的完整執行檔路徑。",
        "dsh" => "找不到 DeepSeek Harness（此平台尚未支援）。或在設定指定 dsh 的完整路徑。",
        _ => $"找不到可執行的 {name}。可使用 ChatGPT(Codex) App 內附的 codex，或在設定指定完整執行檔路徑。",
    });

    public static NativeCLIError InvalidWorkspace(string directory) => new($"工作資料夾不存在：{directory}");
    public static NativeCLIError InvalidPrompt => new("請輸入內容（最多 30,000 字）。");
    public static NativeCLIError ProcessFailed(string message) => new(message);
    public static NativeCLIError NoResponse(string name) => new($"{name} 未輸出可辨識的回覆，沒有自動重送請求。");

    public NativeCLIError With(string suffix) => new(Message + suffix);
}

/// <summary>
/// The earlier conversation is gone, and the model was not reached: safe to start over.
/// </summary>
public sealed class SessionUnavailable : Exception
{
    public SessionUnavailable() : base("先前的 CLI 對話已無法延續") { }
}

/// <summary>
/// Resolves the official CLI to execute and builds its argument list.
/// Windows mirrors of the macOS CommandPlan: prefer the vendor's own desktop-app
/// copies, never scan arbitrary user directories or start a GUI application.
/// </summary>
public static class CommandPlan
{
    public const int MaxPromptCharacters = 30_000;

    /// <summary>Home-relative directories searched after the process PATH.</summary>
    public static readonly string[] FallbackHomeDirectories =
    [
        ".local/bin", ".npm-global/bin",
    ];

    /// <summary>
    /// The Claude desktop app installs an independent, versioned Claude Code
    /// executable after its Code tab has been used. Re-resolve on each
    /// invocation: the desktop updater can replace or remove a version
    /// directory. Three layouts are probed (newest observed first):
    /// - Store 2026:  %LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc\claude-code\<ver>\<hash>\claude.exe
    /// - Store older: %LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude-code\…
    /// - Direct exe:  %APPDATA%\Claude\claude-code\<ver>\claude.exe
    /// Hash subfolders marked `.verified` are preferred, as on macOS.
    /// </summary>
    public static List<string> BundledClaudePaths(string? home = null, string? appData = null, string? localAppData = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var parents = new[]
        {
            Path.Combine(localAppData, "Claude-Data", "claude_pzs8sxrjxfjjc", "claude-code"),
            Path.Combine(localAppData, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude", "claude-code"),
            Path.Combine(appData, "Claude", "claude-code"),
        };
        var results = new List<string>();
        foreach (var parent in parents)
        {
            if (!Directory.Exists(parent)) continue;
            foreach (var version in VersionedDirectories(parent))
            {
                var versionDir = Path.Combine(parent, version);
                // Newest desktop builds add a content-hash level, with `.verified`
                // marking the copy the updater checked.
                var hashes = SafeDirectories(versionDir)
                    .Where(hash => hash.All(char.IsLetterOrDigit))
                    .OrderByDescending(hash => File.Exists(Path.Combine(versionDir, hash, ".verified")))
                    .ToList();
                foreach (var hash in hashes)
                {
                    results.Add(Path.Combine(versionDir, hash, "claude.exe"));
                }
                results.Add(Path.Combine(versionDir, "claude.exe"));
                results.Add(Path.Combine(versionDir, "claude", "claude.exe"));
            }
        }
        return results;
    }

    /// <summary>
    /// The ChatGPT (Codex) desktop app stages a user-readable copy of its bundled
    /// codex.exe under %LOCALAPPDATA%\OpenAI\Codex\bin (the WindowsApps original
    /// is ACL-protected and cannot be spawned directly). The app also honours the
    /// CODEX_CLI_PATH override; the standalone CLI lives in Programs\OpenAI\Codex.
    /// </summary>
    public static List<string> BundledCodexPaths(string? localAppData = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var results = new List<string>();
        var staged = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        if (Directory.Exists(staged))
        {
            foreach (var hash in Directory.GetDirectories(staged).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(Path.Combine(hash, "codex.exe"));
            }
        }
        results.Add(Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe"));
        return results;
    }

    /// <summary>
    /// The Antigravity CLI installs via winget (Google.AntigravityCLI) or the
    /// official installer. winget puts the exe in its package folder; the
    /// installer uses %LOCALAPPDATA%\agy\bin.
    /// </summary>
    public static List<string> BundledAntigravityPaths(string? localAppData = null, string? home = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var results = new List<string>();
        var wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPackages))
        {
            foreach (var package in SafeDirectories(wingetPackages))
            {
                if (!package.StartsWith("Google.AntigravityCLI", StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(Path.Combine(wingetPackages, package, "agy.exe"));
            }
        }
        results.Add(Path.Combine(localAppData, "agy", "bin", "agy.exe"));
        results.Add(Path.Combine(home, ".local", "bin", "agy.exe"));
        return results;
    }

    /// <summary>Directory names that parse as version numbers, newest first ("2.1.227" &gt; "2.1.121").</summary>
    public static List<string> VersionedDirectories(string parent)
    {
        var entries = new List<(string Name, int[] Parts)>();
        foreach (var name in SafeDirectories(parent))
        {
            var parts = ParseVersion(name);
            if (parts is not null) entries.Add((name, parts));
        }
        entries.Sort((a, b) =>
        {
            for (var i = 0; i < Math.Max(a.Parts.Length, b.Parts.Length); i++)
            {
                var left = i < a.Parts.Length ? a.Parts[i] : 0;
                var right = i < b.Parts.Length ? b.Parts[i] : 0;
                if (left != right) return right.CompareTo(left);
            }
            return string.CompareOrdinal(b.Name, a.Name);
        });
        return entries.Select(e => e.Name).ToList();
    }

    private static int[]? ParseVersion(string name)
    {
        var pieces = name.Split('.');
        if (pieces.Length is < 2 or > 4) return null;
        var numbers = new int[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (pieces[i].Length == 0 || !pieces[i].All(char.IsDigit)) return null;
            numbers[i] = int.Parse(pieces[i]);
        }
        return numbers;
    }

    private static List<string> SafeDirectories(string parent)
    {
        try
        {
            return Directory.EnumerateDirectories(parent).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Finds the executable for a member's program: an explicitly configured path
    /// first, then the vendor's bundled copies, then PATH and known home folders.
    /// Returns null when nothing executable is found.
    /// </summary>
    public static string? ResolveExecutable(
        Executor executor, NativeSettings settings,
        IReadOnlyDictionary<string, string>? environment = null, string? home = null)
    {
        environment ??= Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value as string ?? "", StringComparer.OrdinalIgnoreCase);
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] candidates;
        var configured = settings.ConfiguredPath(executor).Trim();
        if (configured.Length > 0)
        {
            var expanded = ExpandHome(configured, home);
            candidates = [expanded];
        }
        else
        {
            var bundled = executor switch
            {
                Executor.Codex => BundledCodexPaths(),
                Executor.Claude => BundledClaudePaths(home: home),
                Executor.Antigravity => BundledAntigravityPaths(home: home),
                _ => [],
            };
            var override_ = executor == Executor.Codex
                ? environment.GetValueOrDefault("CODEX_CLI_PATH")?.Trim() : null;
            if (!string.IsNullOrEmpty(override_)) bundled.Insert(0, ExpandHome(override_, home));
            var pathDirs = (environment.GetValueOrDefault("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var dirs = pathDirs
                .Concat(FallbackHomeDirectories.Select(d => Path.Combine(home, d)))
                .ToList();
            var names = executor.ExecutableName() switch
            {
                "claude" => new[] { "claude.exe", "claude.cmd", "claude" },
                "codex" => new[] { "codex.exe", "codex.cmd", "codex" },
                var other => new[] { other + ".exe", other },
            };
            candidates = [.. bundled, .. dirs.SelectMany(d => names.Select(n => Path.Combine(d, n)))];
        }

        foreach (var candidate in candidates)
        {
            if (IsExecutableFile(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    /// <summary>A path is usable when it exists, is a file, and is a .exe/.cmd (or extensionless). </summary>
    public static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var extension = Path.GetExtension(path);
            return extension is ".exe" or ".cmd" or ".bat" or "";
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string ExpandHome(string path, string home)
    {
        if (path == "~") return home;
        if (path.StartsWith("~/") || path.StartsWith("~\\"))
        {
            return Path.Combine(home, path[2..]);
        }
        // %APPDATA%-style environment references are accepted in configured paths.
        if (path.Contains('%'))
        {
            return Environment.ExpandEnvironmentVariables(path);
        }
        return path;
    }

    /// <summary>
    /// The CLI arguments for one headless run. The prompt always arrives on stdin.
    /// Codex uses its documented exec mode: explicitly read-only unless the task
    /// copy may write. Claude print mode disables tools for read-only runs and
    /// limits writing runs to file tools inside the working directory.
    /// </summary>
    public static List<string> Arguments(Executor executor, ExecutionAccess access = ExecutionAccess.ReadOnly,
                                         CLIRunOptions? options = null)
    {
        options ??= new CLIRunOptions();
        switch (executor)
        {
            case Executor.Antigravity:
                return Antigravity.Arguments(access, options);
            case Executor.Codex:
            {
                var sandbox = access == ExecutionAccess.StagedWrite ? "workspace-write" : "read-only";
                var head = options.Session?.ResumeID is { Length: > 0 } resumeID
                    ? new List<string> { "exec", "resume", resumeID }
                    : new List<string> { "exec" };
                // Explicitly read-only. No --full-auto / --dangerously-bypass flags.
                head.AddRange(["--json", "--sandbox", sandbox, "--skip-git-repo-check", "-"]);
                return head;
            }
            case Executor.Claude:
            {
                // Print mode via stdin; empty tools list disables tool execution for MVP.
                // Writing runs get file tools only — no Bash or network — and edits are
                // auto-accepted inside the working directory; anything outside is denied.
                var claude = new List<string>
                {
                    "--print", "--output-format", "stream-json", "--include-partial-messages", "--verbose",
                };
                claude.AddRange(access switch
                {
                    ExecutionAccess.ReadOnly => ["--tools", ""],
                    ExecutionAccess.ReadFiles => ["--tools", "Read,Glob,Grep"],
                    ExecutionAccess.StagedWrite => ["--tools", "Read,Edit,Write,Glob,Grep", "--permission-mode", "acceptEdits"],
                    _ => throw new ArgumentOutOfRangeException(nameof(access)),
                });
                if (options.Model.Length > 0) claude.AddRange(["--model", options.Model]);
                if (options.Effort.Length > 0) claude.AddRange(["--effort", options.Effort]);
                if (options.Session?.ResumeID is { Length: > 0 } resumeID) claude.AddRange(["--resume", resumeID]);
                // Images and PDFs travel as content blocks in one stream-json user message.
                if (options.Attachments.Count > 0) claude.AddRange(["--input-format", "stream-json"]);
                return claude;
            }
            default:
                throw new NotSupportedException($"{executor} is not available on this platform yet.");
        }
    }

    /// <summary>The working directory for a run: the configured workspace, else a fallback.</summary>
    public static string WorkingDirectory(NativeSettings settings, string fallback)
    {
        var value = settings.WorkspacePath.Trim();
        var path = value.Length == 0 ? fallback : ExpandHome(value, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!Directory.Exists(path)) throw NativeCLIError.InvalidWorkspace(path);
        return Path.GetFullPath(path);
    }
}

public static class ModelCatalog
{
    public static readonly string[] ClaudeEfforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Fallback only, used when the CLI cannot be asked for its live catalog.</summary>
    public static readonly ModelChoice[] ClaudeFallback =
    [
        new("fable", "Fable", ClaudeEfforts),
        new("opus", "Opus", ClaudeEfforts),
        new("sonnet", "Sonnet", ClaudeEfforts),
        new("haiku", "Haiku", ["low", "medium", "high"]),
    ];

    /// <summary>
    /// Parses the `models` array of Claude Code's SDK `initialize` response.
    /// The `default` entry is folded into the first model it resolves to.
    /// </summary>
    public static List<ModelChoice> ClaudeModelsFrom(JsonElement models)
    {
        var choices = new List<ModelChoice>();
        if (models.ValueKind != JsonValueKind.Array) return choices;
        string? resolvedDefault = null;
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.String
                && valueProp.GetString() == "default"
                && item.TryGetProperty("resolvedModel", out var resolved) && resolved.ValueKind == JsonValueKind.String)
            {
                resolvedDefault = resolved.GetString();
            }
        }
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("value", out var valueProp) || valueProp.ValueKind != JsonValueKind.String) continue;
            var value = valueProp.GetString() ?? "";
            if (value == "default" || Sanitize(value).Length == 0) continue;
            var efforts = item.TryGetProperty("supportsEffort", out var supports) && supports.ValueKind == JsonValueKind.True
                && item.TryGetProperty("supportedEffortLevels", out var levels) && levels.ValueKind == JsonValueKind.Array
                ? levels.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : [];
            // Only the first entry resolving to the default carries the flag.
            var isDefault = resolvedDefault is not null
                && item.TryGetProperty("resolvedModel", out var rm) && rm.ValueKind == JsonValueKind.String
                && rm.GetString() == resolvedDefault
                && choices.All(c => !c.IsDefault);
            var title = item.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
                ? dn.GetString() ?? value : value;
            choices.Add(new ModelChoice(value, title, efforts, isDefault));
        }
        return choices;
    }

    static readonly System.Text.RegularExpressions.Regex SafeCharacters =
        new("^[A-Za-z0-9\\-_.,:\\[\\]]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Model and effort strings become CLI arguments / JSON-RPC values.</summary>
    public static string Sanitize(string value)
    {
        var trimmed = value.Trim();
        // A leading "-" would be parsed by the CLI as another flag.
        if (trimmed.Length > 80 || trimmed.StartsWith('-') || !SafeCharacters.IsMatch(trimmed)) return "";
        return trimmed;
    }
}

/// <summary>A selectable model; an empty id means "the CLI's own default".</summary>
public sealed record ModelChoice(string Id, string Title, IReadOnlyList<string> Efforts, bool IsDefault = false);
