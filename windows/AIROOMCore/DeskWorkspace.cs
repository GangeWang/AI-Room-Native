namespace AIROOMCore;

/// <summary>
/// Member-desk working folders, as upstream AIROOM defines them:
/// - the member's **personal folder** is edited in place;
/// - **any other folder** is edited in a per-topic task copy, written back only
///   on request (conflict-checked by WorkspaceStaging).
/// Each round that may write is preceded by a snapshot, so "undo last round"
/// restores the working folder and, if that round was already written back,
/// the original files too.
/// </summary>
public sealed class DeskWorkspace
{
    const string ManifestName = "applied.json";

    public string Root { get; }
    public WorkspaceStaging Staging { get; }

    public DeskWorkspace(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Staging = new WorkspaceStaging(Path.Combine(Root, "copies"));
    }

    public string PersonalFolder(MemberID agent)
    {
        var folder = Path.Combine(Root, "members", agent.Handle, "home");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public bool HasCopy(Guid topic) => Directory.Exists(Staging.WorkingDirectory(topic));

    /// <summary>The folder the CLI works in for this topic (creating the task copy on first use).</summary>
    public string WorkingFolder(Guid topic, MemberID agent, string original)
    {
        if (string.IsNullOrEmpty(original)) return PersonalFolder(agent);
        if (HasCopy(topic)) return Staging.WorkingDirectory(topic);
        return Staging.Prepare(topic, original);
    }

    public List<FileChange> Changes(Guid topic) =>
        HasCopy(topic) ? Staging.Changes(topic) : [];

    // MARK: Rounds and undo

    string RoundDirectory(Guid topic) => Path.Combine(Root, "rounds", topic.ToString("D"));

    public bool CanUndo(Guid topic) => Directory.Exists(RoundDirectory(topic));

    /// <summary>Snapshot before a writing round; replaces the previous round's snapshot.</summary>
    public void BeginRound(Guid topic, MemberID agent, string original)
    {
        var round = RoundDirectory(topic);
        if (Directory.Exists(round)) Directory.Delete(round, recursive: true);
        Directory.CreateDirectory(round);
        if (string.IsNullOrEmpty(original))
        {
            CopyDirectory(PersonalFolder(agent), Path.Combine(round, "work"));
        }
        else
        {
            _ = WorkingFolder(topic, agent, original);
            CopyDirectory(Staging.WorkingDirectory(topic), Path.Combine(round, "work"));
            CopyDirectory(Staging.BaselineDirectory(topic), Path.Combine(round, "baseline"));
        }
    }

    /// <summary>
    /// Writes the copy's changes back to the original folder, then treats the
    /// written state as the new baseline so later rounds diff from here.
    /// </summary>
    public List<FileChange> Apply(Guid topic, string original)
    {
        var folder = Path.GetFullPath(original);
        var changed = Staging.Changes(topic);
        if (changed.Count == 0) return [];
        // Keep the originals of this write-back so undo can restore them.
        var round = RoundDirectory(topic);
        var saved = Path.Combine(round, "applied-originals");
        if (CanUndo(topic))
        {
            if (Directory.Exists(saved)) Directory.Delete(saved, recursive: true);
            Directory.CreateDirectory(saved);
            foreach (var change in changed.Where(change => change.Kind != ChangeKind.Added))
            {
                var source = Path.Combine(folder, change.Path);
                if (!File.Exists(source) && !Directory.Exists(source)) continue;
                var target = Path.Combine(saved, change.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CopyEntry(source, target);
            }
        }
        var applied = Staging.Apply(topic, folder);
        Staging.Rebaseline(topic);
        if (CanUndo(topic))
        {
            File.WriteAllText(Path.Combine(round, ManifestName), CoreJson.Serialize(applied));
        }
        return applied;
    }

    /// <summary>
    /// Restores the state from before the last writing round. If that round was
    /// already written back, the original files are restored as well — but only
    /// when they still hold exactly what AIROOM wrote.
    /// </summary>
    public void UndoRound(Guid topic, MemberID agent, string original)
    {
        var round = RoundDirectory(topic);
        if (!Directory.Exists(round)) throw WorkspaceError.InvalidPhase;
        var manifest = Path.Combine(round, ManifestName);
        if (!string.IsNullOrEmpty(original) && File.Exists(manifest))
        {
            var applied = CoreJson.Deserialize<List<FileChange>>(File.ReadAllText(manifest));
            var folder = Path.GetFullPath(original);
            var written = Staging.WorkingDirectory(topic);
            var conflicts = applied.Where(change =>
            {
                var current = Path.Combine(folder, change.Path);
                if (change.Kind == ChangeKind.Deleted) return File.Exists(current) || Directory.Exists(current);
                var writtenEntry = Path.Combine(written, change.Path);
                if (!File.Exists(current) || !File.Exists(writtenEntry)) return true;
                return !FilesEqual(current, writtenEntry);
            }).Select(change => change.Path).ToList();
            if (conflicts.Count > 0) throw WorkspaceError.Conflict(conflicts);
            var saved = Path.Combine(round, "applied-originals");
            foreach (var change in applied)
            {
                var current = Path.Combine(folder, change.Path);
                if (File.Exists(current)) File.Delete(current);
                else if (Directory.Exists(current)) Directory.Delete(current, recursive: true);
                if (change.Kind != ChangeKind.Added)
                {
                    var savedEntry = Path.Combine(saved, change.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(current)!);
                    CopyEntry(savedEntry, current);
                }
            }
        }
        if (string.IsNullOrEmpty(original))
        {
            Replace(PersonalFolder(agent), Path.Combine(round, "work"));
        }
        else
        {
            Replace(Staging.WorkingDirectory(topic), Path.Combine(round, "work"));
            Replace(Staging.BaselineDirectory(topic), Path.Combine(round, "baseline"));
        }
        Directory.Delete(round, recursive: true);
    }

    /// <summary>Drops the task copy (and any undo snapshot). The original is untouched.</summary>
    public void Discard(Guid topic)
    {
        Staging.Discard(topic);
        var round = RoundDirectory(topic);
        if (Directory.Exists(round)) Directory.Delete(round, recursive: true);
    }

    static void Replace(string folder, string snapshot)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        CopyDirectory(snapshot, folder);
    }

    static void CopyEntry(string source, string destination)
    {
        if (File.Exists(source)) File.Copy(source, destination, overwrite: false);
        else CopyDirectory(source, destination);
    }

    static bool FilesEqual(string a, string b)
    {
        try
        {
            using var left = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var right = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read);
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
        catch
        {
            return false;
        }
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: false);
        }
    }
}
