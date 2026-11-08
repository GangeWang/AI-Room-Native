using System.Text;

namespace AIROOMCore;

public sealed class WorkspaceError : Exception
{
    WorkspaceError(string message) : base(message) { }

    public static WorkspaceError UnsafeFile(string name) =>
        new($"不允許符號連結、特殊檔案或不安全路徑：{name}");
    public static WorkspaceError ExceedsLimit(string name) =>
        new($"專案超出安全複製限制：{name}");
    public static WorkspaceError Missing(string name) =>
        new($"找不到工作區：{name}");
    public static WorkspaceError Conflict(IReadOnlyList<string> files) =>
        new($"原資料夾已有異動，未寫入：{string.Join(", ", files)}");
    public static WorkspaceError InvalidPhase => new("目前狀態不允許此操作");
}

public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
}

public sealed record FileChange(string Path, ChangeKind Kind);

/// <summary>
/// A copy-on-write *workflow* layer, not an OS sandbox. Vendor CLIs can access
/// other paths with the user's privileges. Never use it as a security boundary
/// for untrusted prompts.
/// </summary>
public sealed class WorkspaceStaging
{
    const int DefaultMaximumFiles = 2000;
    const long DefaultMaximumBytes = 256 * 1024 * 1024;

    public string Storage { get; }
    public int MaximumFiles { get; }
    public long MaximumBytes { get; }

    public WorkspaceStaging(string storage, int maximumFiles = DefaultMaximumFiles, long maximumBytes = DefaultMaximumBytes)
    {
        Storage = Path.GetFullPath(storage);
        MaximumFiles = maximumFiles;
        MaximumBytes = maximumBytes;
        Directory.CreateDirectory(Storage);
    }

    public string StageDirectory(Guid id) => Path.Combine(Storage, id.ToString("D"));
    public string WorkingDirectory(Guid id) => Path.Combine(StageDirectory(id), "work");
    public string BaselineDirectory(Guid id) => Path.Combine(StageDirectory(id), "baseline");

    /// <summary>
    /// Rejects links before copying, so copying cannot dereference a link
    /// outside the project. Returns relativePath → absolute path.
    /// </summary>
    Dictionary<string, string> Inventory(string directory, bool ignoreSensitive = false)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        var root = Path.GetFullPath(directory);
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            foreach (var entry in SafeEntries(next))
            {
                var full = Path.GetFullPath(entry);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !string.Equals(full, root, StringComparison.Ordinal))
                {
                    throw WorkspaceError.UnsafeFile(full);
                }
                var relative = full[(root.Length + 1)..];
                // Sensitive files are never copied into a CLI-accessible work directory.
                // A newly created sensitive file in a working copy is a hard error, not
                // silently omitted from the review or accepted on writeback.
                if (PrivacyGuard.BlockedRelativePath(relative))
                {
                    if (ignoreSensitive) continue;
                    throw new NativeCLIError($"禁止存取機密檔案：{relative} / Sensitive path blocked");
                }
                var info = new FileInfo(entry);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw WorkspaceError.UnsafeFile(relative);
                if (info.Attributes.HasFlag(FileAttributes.Directory))
                {
                    queue.Enqueue(full);
                    continue;
                }
                if (PrivacyGuard.UnsafeContent(full, info.Length))
                {
                    if (ignoreSensitive) continue;
                    throw new NativeCLIError($"禁止存取機密檔案：{relative} / Sensitive path blocked");
                }
                total += info.Length;
                files[relative] = full;
                if (files.Count > MaximumFiles || total > MaximumBytes) throw WorkspaceError.ExceedsLimit(relative);
            }
        }
        return files;
    }

    static IEnumerable<string> SafeEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception)
        {
            return [];
        }
    }

    static void RequireDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists) throw WorkspaceError.Missing(directory);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw WorkspaceError.UnsafeFile(directory);
    }

    /// <summary>Creates a stage with a verified baseline and an editable work copy.</summary>
    public string Prepare(Guid id, string source)
    {
        var folder = Path.GetFullPath(source);
        if (SameOrNested(folder, Storage) || SameOrNested(Storage, folder))
        {
            throw WorkspaceError.UnsafeFile("專案與工作副本存放位置不能互相包含");
        }
        RequireDirectory(folder);
        var approved = Inventory(folder, ignoreSensitive: true);
        var stage = StageDirectory(id);
        if (Directory.Exists(stage)) throw WorkspaceError.UnsafeFile("已有同一工作區，拒絕覆寫");
        var baseline = BaselineDirectory(id);
        var work = WorkingDirectory(id);
        try
        {
            Directory.CreateDirectory(baseline);
            Directory.CreateDirectory(work);
            // Never copy the source root blindly: it would bring .env, credentials,
            // cloud profiles and hidden VCS internals along as well.
            foreach (var relative in approved.Keys.Order())
            {
                var sourceFile = approved[relative];
                foreach (var destinationRoot in new[] { baseline, work })
                {
                    var destination = Path.Combine(destinationRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(sourceFile, destination, overwrite: false);
                }
            }
        }
        catch
        {
            try { Directory.Delete(stage, recursive: true); } catch { /* best effort */ }
            throw;
        }
        return work;
    }

    static bool SameOrNested(string inner, string outer) =>
        inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || string.Equals(inner, outer, StringComparison.OrdinalIgnoreCase);

    static bool EqualFiles(string a, string b)
    {
        using var left = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var right = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        if (left.Length != right.Length) return false;
        Span<byte> leftBuffer = stackalloc byte[65536];
        Span<byte> rightBuffer = stackalloc byte[65536];
        while (true)
        {
            var leftRead = left.Read(leftBuffer);
            var rightRead = right.Read(rightBuffer);
            if (leftRead != rightRead) return false;
            if (leftRead == 0) return true;
            if (!leftBuffer[..leftRead].SequenceEqual(rightBuffer[..rightRead])) return false;
        }
    }

    /// <summary>Added / modified / deleted paths between the baseline and the work copy.</summary>
    public List<FileChange> Changes(Guid id)
    {
        var baseline = BaselineDirectory(id);
        var work = WorkingDirectory(id);
        RequireDirectory(baseline);
        RequireDirectory(work);
        var before = Inventory(baseline);
        var after = Inventory(work);
        var changes = new List<FileChange>();
        foreach (var path in before.Keys.Union(after.Keys).Order())
        {
            if (!before.TryGetValue(path, out var oldFile)) changes.Add(new FileChange(path, ChangeKind.Added));
            else if (!after.TryGetValue(path, out var newFile)) changes.Add(new FileChange(path, ChangeKind.Deleted));
            else if (!EqualFiles(oldFile, newFile)) changes.Add(new FileChange(path, ChangeKind.Modified));
        }
        return changes;
    }

    /// <summary>Human-readable preview only. Binary files are identified but not emitted.</summary>
    public (string Before, string After) Preview(Guid id, string path, int limit = 12_000)
    {
        var changes = Changes(id);
        if (PrivacyGuard.BlockedRelativePath(path) || !changes.Any(change => change.Path == path))
        {
            throw WorkspaceError.UnsafeFile(path);
        }
        string Read(string root)
        {
            var url = Path.Combine(root, path);
            // A missing side means the file was added (no "before") or deleted (no "after").
            if (!File.Exists(url)) return "（這一側沒有這個檔案：新增或已刪除）";
            try
            {
                var data = File.ReadAllBytes(url);
                if (data.Length > limit || data.Contains((byte)0)) return "（二進位或內容過長，不顯示）";
                return PrivacyGuard.Redact(Encoding.UTF8.GetString(data));
            }
            catch
            {
                return "（二進位或內容過長，不顯示）";
            }
        }
        return (Read(BaselineDirectory(id)), Read(WorkingDirectory(id)));
    }

    /// <summary>
    /// Refuses every changed path if the original differs from the preserved
    /// baseline. Applies a verified, user-approved changeset. On error, restores
    /// pre-apply files.
    /// </summary>
    public List<FileChange> Apply(Guid id, string original)
    {
        var folder = Path.GetFullPath(original);
        RequireDirectory(folder);
        if (SameOrNested(folder, Storage) || SameOrNested(Storage, folder))
        {
            throw WorkspaceError.UnsafeFile("來源與工作副本路徑互相包含");
        }
        var changed = Changes(id);
        var initial = Inventory(BaselineDirectory(id));
        var current = Inventory(folder, ignoreSensitive: true);
        var modified = changed.Select(change => change.Path).ToList();
        var conflicts = modified.Where(path =>
        {
            // A directory at an expected file path must never be replaced/deleted.
            var target = Path.Combine(folder, path);
            if (Directory.Exists(target)) return true;
            switch ((initial.ContainsKey(path), current.ContainsKey(path)))
            {
                case (false, false): return false;
                case (true, true): return !EqualFiles(initial[path], current[path]);
                default: return true;
            }
        }).ToList();
        if (conflicts.Count > 0) throw WorkspaceError.Conflict(conflicts);

        // Capture each changed file before writing, to permit rollback on I/O errors.
        var rollback = Path.Combine(StageDirectory(id), "rollback");
        if (Directory.Exists(rollback)) throw WorkspaceError.UnsafeFile("回復暫存目錄已存在");
        Directory.CreateDirectory(rollback);
        var originalExists = current.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var path in modified.Where(originalExists.Contains))
        {
            var destination = Path.Combine(rollback, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(folder, path), destination, overwrite: false);
        }
        var applied = new List<string>();
        try
        {
            foreach (var change in changed)
            {
                var destination = Path.Combine(folder, change.Path);
                applied.Add(change.Path);
                if (File.Exists(destination)) File.Delete(destination);
                else if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                if (change.Kind != ChangeKind.Deleted)
                {
                    var source = Path.Combine(WorkingDirectory(id), change.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: false);
                }
            }
        }
        catch
        {
            foreach (var path in applied.AsEnumerable().Reverse())
            {
                var destination = Path.Combine(folder, path);
                try
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    else if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                    if (originalExists.Contains(path))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(Path.Combine(rollback, path), destination, overwrite: false);
                    }
                }
                catch { /* best-effort rollback */ }
            }
            throw;
        }
        try { Directory.Delete(rollback, recursive: true); } catch { /* best effort */ }
        return changed;
    }

    /// <summary>After a write-back, the written state becomes the new baseline.</summary>
    public void Rebaseline(Guid id)
    {
        var baseline = BaselineDirectory(id);
        var work = WorkingDirectory(id);
        RequireDirectory(work);
        _ = Inventory(work); // refuse links / sensitive files before copying
        var fresh = Path.Combine(StageDirectory(id), "baseline-next");
        if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
        CopyDirectory(work, fresh);
        if (Directory.Exists(baseline)) Directory.Delete(baseline, recursive: true);
        Directory.Move(fresh, baseline);
    }

    public void Discard(Guid id)
    {
        var directory = StageDirectory(id);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Storage, StringComparison.OrdinalIgnoreCase))
        {
            throw WorkspaceError.UnsafeFile(directory);
        }
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            File.Copy(file, Path.Combine(destination, relative), overwrite: false);
        }
    }
}
