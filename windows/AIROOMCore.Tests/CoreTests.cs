using AIROOMCore;
using Xunit;

public class CoreTests
{
    // MARK: CLI event parsing — the exact JSONL the official CLIs emit.

    static string? Consume(CliEventAccumulator parser, string line) => parser.Consume(line);

    [Fact]
    public void CodexAgentMessageIsTheAnswer()
    {
        var parser = new CliEventAccumulator(Executor.Codex);
        Assert.Null(Consume(parser, """{"type":"thread.started","thread_id":"th_123"}"""));
        Assert.Equal("th_123", parser.SessionID);
        Assert.Null(Consume(parser, """{"type":"item.started","item":{"type":"command_execution","command":"ls"}}"""));
        Assert.Equal("答案", Consume(parser, """{"type":"item.completed","item":{"type":"agent_message","text":"答案"}}"""));
        Assert.Equal("答案", parser.Text);
        Assert.Null(parser.Failure);
    }

    [Fact]
    public void ClaudeInitCarriesTheModelAndSession()
    {
        var parser = new CliEventAccumulator(Executor.Claude);
        Assert.Null(Consume(parser, """{"type":"system","subtype":"init","session_id":"s1","model":"claude-fable"}"""));
        Assert.Equal("s1", parser.SessionID);
        Assert.Equal("claude-fable", parser.Model);
    }

    [Fact]
    public void ClaudePartialDeltasStream()
    {
        var parser = new CliEventAccumulator(Executor.Claude);
        Assert.Equal("你", Consume(parser, """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"你"}}}"""));
        Assert.Equal("你好", Consume(parser, """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"好"}}}"""));
        Assert.Equal("你好", parser.Text);
    }

    [Fact]
    public void NoRawJsonLeaksIntoTheReply()
    {
        var parser = new CliEventAccumulator(Executor.Claude);
        Consume(parser, """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"ok"}}}""");
        Consume(parser, """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read"}]}}""");
        Assert.Equal("ok", parser.Text);
        Consume(parser, """{"type":"result","is_error":false,"result":"完成"}""");
        Assert.Equal("完成", parser.Text);
    }

    [Fact]
    public void ClaudeErrorResultIsNeverAnAnswer()
    {
        var parser = new CliEventAccumulator(Executor.Claude);
        Consume(parser, """{"type":"result","is_error":true,"num_turns":0,"duration_api_ms":0,"subtype":"error"}""");
        Assert.NotNull(parser.Failure);
        Assert.Equal("", parser.Text);
        Assert.True(parser.EndedBeforeModel);
    }

    [Fact]
    public void MalformedLinesAreIgnored()
    {
        var parser = new CliEventAccumulator(Executor.Claude);
        Assert.Null(Consume(parser, "not json"));
        Assert.Null(Consume(parser, "[1,2,3]"));
        Assert.Null(Consume(parser, ""));
    }

    // MARK: PromptBuilder

    [Fact]
    public void PromptBuildsHistoryAndCaps()
    {
        var history = Enumerable.Range(0, 10).Select(index => new ChatMessage(
            index % 2 == 0 ? MessageRole.User : MessageRole.Assistant, $"m{index}")).ToList();
        var prompt = PromptBuilder.Build(history, "問題", maxTurns: 2, persona: "P".PadLeft(1501, 'x'));
        Assert.Contains("m6", prompt);
        Assert.DoesNotContain("m0", prompt);
        Assert.Contains("問題", prompt);
        Assert.Contains("請使用繁體中文回覆", prompt);
        Assert.Equal(1500, prompt.Split("你的人物設定：")[1].Split("\n")[0].Length);
    }

    [Fact]
    public void EnglishPromptAndOwnerName()
    {
        var prompt = PromptBuilder.Build([], "Hi", language: InterfaceLanguage.English, userName: "Gange");
        Assert.Contains("Reply in English", prompt);
        Assert.Contains("Address the user as Gange.", prompt);
    }

    // MARK: CommandPlan — Windows path detection and arguments.

    [Fact]
    public void ReadOnlyArgumentsDisableTools()
    {
        var arguments = CommandPlan.Arguments(Executor.Claude, ExecutionAccess.ReadOnly);
        Assert.Contains("--print", arguments);
        var tools = arguments[arguments.IndexOf("--tools") + 1];
        Assert.Equal("", tools);
        Assert.DoesNotContain("--permission-mode", arguments);
    }

    [Fact]
    public void StagedWriteArgumentsAllowOnlyFileTools()
    {
        var arguments = CommandPlan.Arguments(Executor.Claude, ExecutionAccess.StagedWrite,
            new CLIRunOptions(model: "sonnet", session: new SessionPlan("sess-1", "fallback")));
        var tools = arguments[arguments.IndexOf("--tools") + 1];
        Assert.Equal("Read,Edit,Write,Glob,Grep", tools);
        Assert.Contains("acceptEdits", arguments);
        Assert.Contains("--model", arguments);
        Assert.Equal("sonnet", arguments[arguments.IndexOf("--model") + 1]);
        Assert.Equal("sess-1", arguments[arguments.IndexOf("--resume") + 1]);
    }

    [Fact]
    public void CodexArgumentsAreExplicitlySandboxed()
    {
        var read = CommandPlan.Arguments(Executor.Codex, ExecutionAccess.ReadOnly);
        Assert.Equal("read-only", read[read.IndexOf("--sandbox") + 1]);
        Assert.Contains("--skip-git-repo-check", read);
        Assert.Equal("-", read[^1]);
        var write = CommandPlan.Arguments(Executor.Codex, ExecutionAccess.StagedWrite);
        Assert.Equal("workspace-write", write[write.IndexOf("--sandbox") + 1]);
        Assert.DoesNotContain("--dangerously-bypass", write);
        Assert.DoesNotContain("--full-auto", write);
    }

    [Fact]
    public void CodexResumeGoesThroughTheResumeSubcommand()
    {
        var arguments = CommandPlan.Arguments(Executor.Codex, ExecutionAccess.ReadOnly,
            new CLIRunOptions(session: new SessionPlan("th-9", "fallback")));
        Assert.Equal("resume", arguments[1]);
        Assert.Equal("th-9", arguments[2]);
    }

    [Fact]
    public void SessionPlanRejectsUnsafeIds()
    {
        Assert.Null(new SessionPlan("--evil", "x").ResumeID);
        Assert.Null(new SessionPlan("with space", "x").ResumeID);
        Assert.Null(new SessionPlan("with_underscore", "x").ResumeID);
        Assert.Null(new SessionPlan(new string('a', 81), "x").ResumeID);
        Assert.Equal("th-1", new SessionPlan("th-1", "x").ResumeID);
    }

    [Fact]
    public void BundledClaudePathsPickTheNewestVersionDirectory()
    {
        var home = NewTempDirectory();
        try
        {
            // Store 2026 layout: Claude-Data\<pkg>\claude-code\<version>\<hash>\claude.exe
            var storeParent = Path.Combine(home, "AppData", "Local", "Claude-Data", "claude_pzs8sxrjxfjjc", "claude-code");
            var newHash = Path.Combine(storeParent, "2.1.317", "a33aa5518497");
            Directory.CreateDirectory(newHash);
            File.WriteAllText(Path.Combine(newHash, "claude.exe"), "binary");
            File.WriteAllText(Path.Combine(newHash, ".verified"), "");
            // Classic layout: %APPDATA%\Claude\claude-code\<version>\claude.exe
            var classicParent = Path.Combine(home, "AppData", "Roaming", "Claude", "claude-code");
            Directory.CreateDirectory(Path.Combine(classicParent, "2.1.121"));
            Directory.CreateDirectory(Path.Combine(classicParent, "not-a-version"));
            File.WriteAllText(Path.Combine(classicParent, "2.1.121", "claude.exe"), "binary");
            var paths = CommandPlan.BundledClaudePaths(home: home, appData: Path.Combine(home, "AppData", "Roaming"), localAppData: Path.Combine(home, "AppData", "Local"));
            Assert.Equal(
            [
                Path.Combine(storeParent, "2.1.317", "a33aa5518497", "claude.exe"),
                Path.Combine(storeParent, "2.1.317", "claude.exe"),
                Path.Combine(storeParent, "2.1.317", "claude", "claude.exe"),
                Path.Combine(classicParent, "2.1.121", "claude.exe"),
                Path.Combine(classicParent, "2.1.121", "claude", "claude.exe"),
            ], paths);
        }
        finally { TryDelete(home); }
    }

    [Fact]
    public void BundledCodexPathsPreferTheStagedCopy()
    {
        var local = NewTempDirectory();
        try
        {
            var bin = Path.Combine(local, "OpenAI", "Codex", "bin", "abc123");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "codex.exe"), "binary");
            var paths = CommandPlan.BundledCodexPaths(localAppData: local);
            Assert.Equal(Path.Combine(bin, "codex.exe"), paths[0]);
        }
        finally { TryDelete(local); }
    }

    [Fact]
    public void MissingCommandIsRejected()
    {
        var settings = new NativeSettings { CodexPath = NewTempDirectory() };
        Assert.Null(CommandPlan.ResolveExecutable(Executor.Codex, settings, new Dictionary<string, string>()));
    }

    [Fact]
    public void ConfiguredPathWinsAndExpandsHome()
    {
        var home = NewTempDirectory(); // acts as the user profile for "~"
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(home, "tools")).FullName;
            var exe = Path.Combine(sub, "claude.exe");
            File.WriteAllText(exe, "binary");
            var settings = new NativeSettings { ClaudePath = "~/tools/claude.exe" };
            Assert.Equal(Path.GetFullPath(exe),
                CommandPlan.ResolveExecutable(Executor.Claude, settings, new Dictionary<string, string>(), home));
        }
        finally { TryDelete(home); }
    }

    // MARK: State round trip and encryption.

    [Fact]
    public void StateRoundTrip()
    {
        var state = new SavedState();
        state.Members.Add(new MemberCard(MemberID.Make(), Executor.Claude) { Name = "小護士", Color = "#112233" });
        state.Conversations.Add(new Conversation(MemberID.Claude)
        {
            Title = "話題",
            Messages = [new ChatMessage(MessageRole.User, "hello"), new ChatMessage(MessageRole.Assistant, "hi") { ModelUsed = "fable" }],
            SessionNotes = ["note"],
        });
        state.Calls.Record(CallEvent.Attempt, MemberID.Claude, "2026-11-08");
        var restored = CoreJson.Deserialize<SavedState>(CoreJson.Serialize(state));
        Assert.Equal("小護士", restored.Members.Last().Name);
        Assert.Equal("#112233", restored.Members.Last().Color);
        Assert.Equal(2, restored.Conversations[0].Messages.Count);
        Assert.Equal("fable", restored.Conversations[0].Messages[1].ModelUsed);
        Assert.Equal("note", restored.Conversations[0].SessionNotes[0]);
        Assert.Equal(1, restored.Calls.Usage(MemberID.Claude).Total);
    }

    [Fact]
    public void OldStateSectionsSurviveAsPassthrough()
    {
        const string json = """{"settings":{"language":"zh-Hant"},"rooms":[{"id":"X","title":"茶歇室"}],"projectFlows":[{"id":"Y"}]}""";
        var state = CoreJson.Deserialize<SavedState>(json);
        Assert.Equal(InterfaceLanguage.ZhTW, state.Settings.Language);
        var roundTripped = CoreJson.Serialize(state);
        Assert.Contains("\"rooms\"", roundTripped);
        Assert.Contains("茶歇室", roundTripped);
        Assert.Contains("\"projectFlows\"", roundTripped);
    }

    [Fact]
    public void EncryptedStoreRoundTripAndLegacyJson()
    {
        var root = NewTempDirectory();
        try
        {
            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var store = new LocalStateStore(root, new DpapiStateProtector(key));
            var state = new SavedState();
            state.Members.Add(new MemberCard(MemberID.Make()) { Name = "加密測試" });
            store.Save(state);
            Assert.True(File.Exists(Path.Combine(root, "state.aesgcm")));
            var loaded = new LocalStateStore(root, new DpapiStateProtector(key)).Load();
            Assert.Equal("加密測試", loaded.Members.Last().Name);

            // A different key must fail closed, not return partial data.
            var otherKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            Assert.ThrowsAny<Exception>(() => new LocalStateStore(root, new DpapiStateProtector(otherKey)).Load());

            // Plain legacy JSON still loads and is upgraded to encrypted.
            var legacyRoot = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(legacyRoot, "state.json"),
                    """{"conversations":[{"agent":"Claude Code","title":"舊"}],"members":[]}""");
                var legacyStore = new LocalStateStore(legacyRoot, new DpapiStateProtector(key));
                var migrated = legacyStore.Load();
                Assert.Equal("舊", migrated.Conversations[0].Title);
                Assert.False(File.Exists(Path.Combine(legacyRoot, "state.json")));
                Assert.True(File.Exists(Path.Combine(legacyRoot, "state.aesgcm")));
            }
            finally { TryDelete(legacyRoot); }
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void BackupPreviewRestoreAndDelete()
    {
        var root = NewTempDirectory();
        try
        {
            var store = new LocalStateStore(root, new DpapiStateProtector(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            var state = new SavedState();
            state.Members.Add(new MemberCard(MemberID.Make()) { Name = "備份" });
            state.Conversations.Add(new Conversation(MemberID.Claude));
            store.Save(state);
            var file = store.Backup();
            Assert.Single(store.Backups());

            var emptied = store.Load();
            emptied.Members.Clear();
            store.Save(emptied);
            var summary = store.Preview(file);
            Assert.Equal("備份", summary.MemberNames.Last());
            Assert.Equal(1, summary.DeskTopics);

            store.Restore(file);
            Assert.Equal("備份", store.Load().Members.Last().Name);
            Assert.Equal(2, store.Backups().Count); // the restore left a before-restore backup

            store.DeleteBackup(store.Backups()[0].Url);
            store.DeleteBackup(store.Backups()[0].Url);
            Assert.Empty(store.Backups());
            Assert.Throws<NativeCLIError>(() => store.DeleteBackup(NewTempFile()));
        }
        finally { TryDelete(root); }
    }

    // MARK: Workspace staging.

    [Fact]
    public void WorkspaceChangesAndApply()
    {
        var storage = NewTempDirectory();
        var source = NewTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(source, "a.txt"), "one");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "two");
            var staging = new WorkspaceStaging(storage);
            var id = Guid.NewGuid();
            var work = staging.Prepare(id, source);
            Assert.Empty(staging.Changes(id));
            File.WriteAllText(Path.Combine(work, "a.txt"), "one changed");
            File.WriteAllText(Path.Combine(work, "c.txt"), "new");
            File.Delete(Path.Combine(work, "sub", "b.txt"));
            var changes = staging.Changes(id);
            Assert.Equal(ChangeKind.Modified, changes.Single(c => c.Path == "a.txt").Kind);
            Assert.Equal(ChangeKind.Added, changes.Single(c => c.Path == "c.txt").Kind);
            Assert.Equal(ChangeKind.Deleted, changes.Single(c => c.Path.EndsWith("b.txt")).Kind);
            var applied = staging.Apply(id, source);
            Assert.Equal(3, applied.Count);
            Assert.Equal("one changed", File.ReadAllText(Path.Combine(source, "a.txt")));
            Assert.False(File.Exists(Path.Combine(source, "sub", "b.txt")));
            staging.Rebaseline(id);
            Assert.Empty(staging.Changes(id));
            staging.Discard(id);
            Assert.False(Directory.Exists(staging.StageDirectory(id)));
        }
        finally { TryDelete(storage); TryDelete(source); }
    }

    [Fact]
    public void WorkspaceConflictRejectsAll()
    {
        var storage = NewTempDirectory();
        var source = NewTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(source, "a.txt"), "one");
            var staging = new WorkspaceStaging(storage);
            var id = Guid.NewGuid();
            var work = staging.Prepare(id, source);
            File.WriteAllText(Path.Combine(work, "a.txt"), "from member");
            File.WriteAllText(Path.Combine(source, "a.txt"), "changed elsewhere");
            Assert.Throws<WorkspaceError>(() => staging.Apply(id, source));
            Assert.Equal("changed elsewhere", File.ReadAllText(Path.Combine(source, "a.txt")));
        }
        finally { TryDelete(storage); TryDelete(source); }
    }

    [Fact]
    public void WorkspaceSymlinkDenied()
    {
        var storage = NewTempDirectory();
        var source = NewTempDirectory();
        try
        {
            var target = Path.Combine(source, "real.txt");
            File.WriteAllText(target, "data");
            var link = Path.Combine(source, "link.txt");
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            {
                return; // creating symlinks needs a privilege this environment lacks
            }
            var staging = new WorkspaceStaging(storage);
            Assert.ThrowsAny<Exception>(() => staging.Prepare(Guid.NewGuid(), source));
        }
        finally { TryDelete(storage); TryDelete(source); }
    }

    [Fact]
    public void StagingExcludesSecretsButTracksAllowedFiles()
    {
        var storage = NewTempDirectory();
        var source = NewTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(source, "code.py"), "print('ok')");
            File.WriteAllText(Path.Combine(source, ".env"), "SECRET=1");
            File.WriteAllText(Path.Combine(source, "id_rsa"), "nope");
            var staging = new WorkspaceStaging(storage);
            var work = staging.Prepare(Guid.NewGuid(), source);
            Assert.True(File.Exists(Path.Combine(work, "code.py")));
            Assert.False(File.Exists(Path.Combine(work, ".env")));
            Assert.False(File.Exists(Path.Combine(work, "id_rsa")));
        }
        finally { TryDelete(storage); TryDelete(source); }
    }

    [Fact]
    public void StagingRejectsNewSecretsOnWriteback()
    {
        var storage = NewTempDirectory();
        var source = NewTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(source, "a.txt"), "ok");
            var staging = new WorkspaceStaging(storage);
            var id = Guid.NewGuid();
            var work = staging.Prepare(id, source);
            File.WriteAllText(Path.Combine(work, "leak.txt"), "token: sk-ant-abcdefghijklmnop");
            Assert.ThrowsAny<Exception>(() => staging.Changes(id));
        }
        finally { TryDelete(storage); TryDelete(source); }
    }

    // MARK: Desk workspace rounds and undo.

    [Fact]
    public void DeskRoundApplyAndUndo()
    {
        var root = NewTempDirectory();
        var original = NewTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(original, "doc.txt"), "v1");
            var desk = new DeskWorkspace(root);
            var topic = Guid.NewGuid();
            var agent = MemberID.Claude;
            desk.BeginRound(topic, agent, original);
            var work = desk.WorkingFolder(topic, agent, original);
            File.WriteAllText(Path.Combine(work, "doc.txt"), "v2 member edit");
            Assert.True(desk.CanUndo(topic));

            desk.Apply(topic, original);
            Assert.Equal("v2 member edit", File.ReadAllText(Path.Combine(original, "doc.txt")));

            desk.UndoRound(topic, agent, original);
            Assert.Equal("v1", File.ReadAllText(Path.Combine(original, "doc.txt")));
            Assert.False(desk.CanUndo(topic));
        }
        finally { TryDelete(root); TryDelete(original); }
    }

    [Fact]
    public void DeskUndoRefusesWhenOriginalMovedOn()
    {
        var root = NewTempDirectory();
        var original = NewTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(original, "doc.txt"), "v1");
            var desk = new DeskWorkspace(root);
            var topic = Guid.NewGuid();
            desk.BeginRound(topic, MemberID.Claude, original);
            var work = desk.WorkingFolder(topic, MemberID.Claude, original);
            File.WriteAllText(Path.Combine(work, "doc.txt"), "v2");
            desk.Apply(topic, original);
            File.WriteAllText(Path.Combine(original, "doc.txt"), "user edited after");
            Assert.Throws<WorkspaceError>(() => desk.UndoRound(topic, MemberID.Claude, original));
        }
        finally { TryDelete(root); TryDelete(original); }
    }

    // MARK: PrivacyGuard.

    [Fact]
    public void SensitivePathsAndText()
    {
        foreach (var name in new[] { ".env", "folder/.env.production", ".git/config", ".ssh/id_ed25519", "secrets/x.txt", "config/auth.json", "prod.p12", "state.sqlite" })
        {
            Assert.True(PrivacyGuard.BlockedRelativePath(name), name);
        }
        foreach (var name in new[] { "main.cs", "src/hello.py", "README.md", "public/logo.png" })
        {
            Assert.False(PrivacyGuard.BlockedRelativePath(name), name);
        }

        var secret = "sk-ant-abcdefghijklmnopqrstuvwxyz0123456789";
        Assert.True(PrivacyGuard.ContainsSecrets($"token={secret}"));
        Assert.False(PrivacyGuard.ContainsSecrets("regular conversation"));
        Assert.Throws<NativeCLIError>(() => PrivacyGuard.ValidatePrompt($"My key: {secret}"));
        Assert.DoesNotContain(secret, PrivacyGuard.Redact($"Hello {secret}"));
    }

    [Fact]
    public void EnvironmentIsAllowlisted()
    {
        var source = new Dictionary<string, string>
        {
            ["PATH"] = "C:\\old",
            ["USERPROFILE"] = "C:\\Users\\x",
            ["ANTHROPIC_API_KEY"] = "sk-evil",
            ["NODE_OPTIONS"] = "--inspect",
            ["SYSTEMROOT"] = "C:\\Windows",
        };
        var environment = PrivacyGuard.CliEnvironment(source, "C:\\bin\\claude.exe");
        Assert.False(environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(environment.ContainsKey("NODE_OPTIONS"));
        Assert.Equal("C:\\Users\\x", environment["USERPROFILE"]);
        Assert.StartsWith("C:\\bin", environment["PATH"]);
        Assert.Equal("0", environment["AIROOM_CLAUDE_DEBUG"]);
    }

    // MARK: Attachments and topic naming.

    [Fact]
    public void AttachmentClassification()
    {
        var image = AttachmentPolicy.Classify("pic.PNG", [0x89, 0x50, 0x4E, 0x47]);
        Assert.Equal(AttachmentKind.Image, image.Kind);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(AttachmentKind.Pdf, AttachmentPolicy.Classify("doc.pdf", "%PDF-1.7"u8.ToArray()).Kind);
        var text = AttachmentPolicy.Classify("notes.txt", "hello"u8.ToArray());
        Assert.Equal(AttachmentKind.Text, text.Kind);
        Assert.Throws<AttachmentError>(() => AttachmentPolicy.Classify("program.exe", new byte[10]));
        Assert.Throws<AttachmentError>(() => AttachmentPolicy.Classify("big.txt", new byte[AttachmentPolicy.MaxTextBytes + 1]));
    }

    [Fact]
    public void TextAttachmentsInlineBounded()
    {
        var data = System.Text.Encoding.UTF8.GetBytes(new string('x', 30_000));
        var attachment = AttachmentPolicy.Classify("a.txt", data);
        var inlined = AttachmentPolicy.Inline([(attachment, data)], budget: AttachmentPolicy.InlineTextBudget);
        Assert.Contains("附件結束", inlined);
        Assert.Contains("已截斷", inlined);
    }

    [Fact]
    public void TopicNamingStripsMentionsAndCaps()
    {
        Assert.Equal("新話題", TopicNaming.TitleFrom("   "));
        Assert.Equal("hello world", TopicNaming.TitleFrom("@claude hello world"));
        Assert.Equal(new string('a', 20), TopicNaming.TitleFrom(new string('a', 40)));
        Assert.Equal("第一行", TopicNaming.TitleFrom("第一行\n第二行"));
    }

    // MARK: Member roster.

    [Fact]
    public void RosterAddRemoveAndNameMemory()
    {
        var state = new SavedState();
        var card = MemberCard.Added(MemberTemplate.Named("deepseek"), Executor.Claude);
        state.AddMember(card);
        Assert.Single(state.Members, member => member.Agent == card.Agent);
        Assert.Single(state.Conversations, conversation => conversation.Agent == card.Agent);

        state.FormerMembers.Clear();
        state.RemoveMember(card.Agent);
        Assert.Empty(state.Members.Where(member => member.Agent == card.Agent));
        Assert.Empty(state.Conversations.Where(conversation => conversation.Agent == card.Agent));
        Assert.Equal(card.Name, state.MemberName(card.Agent));

        Assert.Throws<NativeCLIError>(() => state.RemoveMember(MemberID.Claude));
    }

    [Fact]
    public void ExecutorSwitchResetsCliSessions()
    {
        var state = new SavedState();
        var topic = new Conversation(MemberID.Codex) { SessionID = "old", SessionThrough = Guid.NewGuid() };
        state.Conversations.Add(topic);
        state.SetExecutor(Executor.Codex, MemberID.Codex); // same program: untouched
        Assert.Equal("old", topic.SessionID);
        state.SetExecutor(Executor.Claude, MemberID.Codex);
        Assert.Null(topic.SessionID);
        Assert.Null(topic.SessionThrough);
    }

    [Fact]
    public void MemberCardNormalizationClamps()
    {
        var card = new MemberCard(MemberID.Make()) { Name = new string('n', 60), Color = "red", PortraitScale = 99 };
        var normalized = card.Normalized();
        Assert.Equal(40, normalized.Name.Length);
        Assert.Equal("", normalized.Color);
        Assert.Equal(1.6, normalized.PortraitScale);
    }

    // MARK: Call ledger.

    [Fact]
    public void CallLedgerCountsAndPrunes()
    {
        var ledger = new CallLedger();
        var agent = MemberID.Claude;
        ledger.Record(CallEvent.Attempt, agent, "2026-11-08");
        ledger.Record(CallEvent.Delivered, agent, "2026-11-08");
        ledger.Record(CallEvent.Completed, agent, "2026-11-08");
        ledger.Record(CallEvent.NotReached, agent, "2026-11-08");
        var usage = ledger.Usage(agent);
        Assert.Equal(1, usage.Total);
        Assert.Equal(0, usage.Delivered); // notReached took the delivery back
        Assert.Equal(1, usage.Completed);
        Assert.Equal(1, usage.NotSent);
        for (var day = 0; day < 70; day++)
        {
            ledger.Record(CallEvent.Attempt, agent, $"2026-09-{day:00}");
        }
        Assert.True(ledger.Days[agent.RawValue].Count <= 60);
    }

    // MARK: Attention list.

    [Fact]
    public void AttentionIgnoresByVersion()
    {
        var state = new SavedState { Members = [] };
        state.Members.Add(new MemberCard(MemberID.Claude) { Name = "Claudia", Enabled = true });
        var items = AttentionBuilder.Items(state, new Dictionary<Executor, string>
        {
            [Executor.Claude] = "missingExecutable",
        });
        var item = Assert.Single(items);
        Assert.Equal(AttentionKind.Missing, item.Kind);
        Assert.Contains("Claudia", item.Title);

        var dismissed = AttentionBuilder.Dismissing(item, items, new Dictionary<string, string>());
        Assert.Empty(AttentionBuilder.Visible(items, dismissed));

        // A changed reminder comes back even when its id was ignored.
        var changed = new AttentionItem(item.Id, item.Kind, item.Title, "新的說明", item.Target);
        Assert.Single(AttentionBuilder.Visible([changed], dismissed));
    }

    // MARK: Fakes for the runner (the real CLIs are never started in tests).

    [Fact]
    public async Task RunnerReadsExternalCLIJsonl()
    {
        var home = NewTempDirectory();
        try
        {
            var fake = Path.Combine(home, "fake-claude.cmd");
            File.WriteAllLines(fake,
            [
                "@echo off",
                "type \"%~f0\" >nul",
                "echo {\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s-9\",\"model\":\"fable\"}",
                "echo {\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"串流\"}}}",
                "echo {\"type\":\"result\",\"is_error\":false,\"result\":\"最終回答\"}",
                "exit /b 0",
            ]);
            var settings = new NativeSettings { ClaudePath = fake };
            var runner = new CliProcessRunner();
            var updates = new List<string>();
            var result = await runner.RunDetailedAsync(Guid.NewGuid(), MemberID.Claude, "hi", settings,
                home, ExecutionAccess.ReadOnly, new CLIRunOptions(), updates.Add);
            Assert.Equal("最終回答", result.Text);
            Assert.Equal("fable", result.Model);
            Assert.Equal("s-9", result.SessionID);
            Assert.Contains("串流", updates);
            Assert.True(result.Resumed is false);
        }
        finally { TryDelete(home); }
    }

    [Fact]
    public async Task RunnerRejectsSecretsInThePrompt()
    {
        var runner = new CliProcessRunner();
        await Assert.ThrowsAsync<NativeCLIError>(() => runner.RunAsync(
            agent: MemberID.Claude, prompt: "my key is sk-ant-abcdefghijklmnop",
            settings: new NativeSettings(), fallbackWorkspace: NewTempDirectory()));
    }

    [Fact]
    public async Task RunnerRejectsOversizedPrompts()
    {
        var runner = new CliProcessRunner();
        await Assert.ThrowsAsync<NativeCLIError>(() => runner.RunAsync(
            agent: MemberID.Claude, prompt: new string('x', 30_001),
            settings: new NativeSettings(), fallbackWorkspace: NewTempDirectory()));
    }

    [Fact]
    public async Task MissingExecutableThrowsSpecificError()
    {
        var runner = new CliProcessRunner();
        var home = NewTempDirectory();
        try
        {
            var error = await Assert.ThrowsAsync<NativeCLIError>(() => runner.RunAsync(
                agent: MemberID.Claude, prompt: "hi",
                settings: new NativeSettings { ClaudePath = Path.Combine(home, "nothing.exe") },
                fallbackWorkspace: home));
            Assert.Contains("Claude", error.Message);
        }
        finally { TryDelete(home); }
    }

    // MARK: helpers

    internal static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "airoom-test-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string NewTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "airoom-test-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "x");
        return path;
    }

    internal static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { try { File.Delete(path); } catch { /* best effort */ } }
    }
}

public class AntigravityTests
{
    [Fact]
    public void ModelsParsingSkipsProgressLines()
    {
        var output = "Fetching available models...\ngemini-3.8-flash-high\tGemini 3.8 Flash (High)\nclaude-sonnet-4-6\tClaude Sonnet 4.6 (Thinking)\n";
        var models = Antigravity.ParseModels(output);
        Assert.Equal(2, models.Count);
        Assert.Equal("gemini-3.8-flash-high", models[0].Id);
        Assert.Equal("Gemini 3.8 Flash (High)", models[0].Title);
        Assert.Equal(OfficialCLIStatus.Authenticated, Antigravity.Status(output));
        Assert.Equal(OfficialCLIStatus.NotLoggedIn, Antigravity.Status("Fetching...\n"));
    }

    [Fact]
    public void ArgumentsCarrySandboxModelEffortAndConversation()
    {
        var arguments = Antigravity.Arguments(ExecutionAccess.ReadOnly,
            new CLIRunOptions(model: "gemini-3.8-flash-high", effort: "high",
                session: new SessionPlan("conv-1", "fallback")));
        Assert.Contains("--sandbox", arguments);
        Assert.Equal("gemini-3.8-flash-high", arguments[arguments.IndexOf("--model") + 1]);
        Assert.Equal("high", arguments[arguments.IndexOf("--effort") + 1]);
        Assert.Equal("conv-1", arguments[arguments.IndexOf("--conversation") + 1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
        Assert.Equal("plan", arguments[arguments.IndexOf("--mode") + 1]);
    }

    [Fact]
    public void UserMessageIsOneStreamJsonLine()
    {
        var line = System.Text.Encoding.UTF8.GetString(Antigravity.UserMessage("你好"));
        Assert.EndsWith("\n", line);
        Assert.Contains("\"event\":\"user\"", line);
        Assert.Contains("你好", line);
    }

    [Fact]
    public void LostConversationIsDetected()
    {
        Assert.True(Antigravity.LostConversation("conv-1", "conv-2"));
        Assert.False(Antigravity.LostConversation("conv-1", "conv-1"));
        Assert.False(Antigravity.LostConversation(null, "anything"));
    }

    [Fact]
    public void BundledAntigravityPathsCoverWingetAndInstaller()
    {
        var local = CoreTests.NewTempDirectory();
        try
        {
            var wingetPackage = Path.Combine(local, "Microsoft", "WinGet", "Packages", "Google.AntigravityCLI_Microsoft.Winget.Source_8wekyb3d8bbwe");
            Directory.CreateDirectory(wingetPackage);
            File.WriteAllText(Path.Combine(wingetPackage, "agy.exe"), "binary");
            var paths = CommandPlan.BundledAntigravityPaths(localAppData: local, home: Path.Combine(local, "home"));
            Assert.Equal(Path.Combine(wingetPackage, "agy.exe"), paths[0]);
            Assert.Contains(Path.Combine(local, "agy", "bin", "agy.exe"), paths);
        }
        finally { CoreTests.TryDelete(local); }
    }
}
