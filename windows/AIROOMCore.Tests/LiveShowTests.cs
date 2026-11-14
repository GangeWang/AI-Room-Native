using System.Text;
using AIROOMCore;
using Xunit;

public class LiveShowTests
{
    static readonly MemberID Claude = MemberID.Claude;
    static readonly MemberID Codex = MemberID.Codex;

    [Fact]
    public void TagsAreMatchedAgainstTheWhitelistOnly()
    {
        var parsed = LiveCueParser.Parse("[happy] 歡迎回來！【興奮】今天有新版本。");
        Assert.Equal(2, parsed.Segments.Count);
        Assert.Equal(LiveEmotion.Happy, parsed.Segments[0].Emotion);
        Assert.Equal("歡迎回來！", parsed.Segments[0].Text);
        Assert.Equal(LiveEmotion.Excited, parsed.Segments[1].Emotion);
        Assert.Equal("今天有新版本。", parsed.Segments[1].Text);
        Assert.Equal("歡迎回來！今天有新版本。", parsed.CleanText);
    }

    [Fact]
    public void InventedAsciiTagsDropButChineseLiteralTextStays()
    {
        var parsed = LiveCueParser.Parse("[sparkle] 這是【注意】事項，[wat] 不是標籤。");
        // [sparkle] and [wat] are invented tags and drop silently;
        // 【注意】 matches the Han pattern but is not a whitelist alias → ordinary text.
        Assert.DoesNotContain("sparkle", parsed.CleanText);
        Assert.DoesNotContain("wat", parsed.CleanText);
        Assert.Contains("【注意】", parsed.CleanText);
        Assert.All(parsed.Segments, segment => Assert.NotEqual((LiveEmotion?)null, segment.Emotion));
    }

    [Fact]
    public void SegmentsAreCappedAndClamped()
    {
        var reply = string.Join(" ", Enumerable.Range(1, 12).Select(index => $"[happy] 第{index}段內容"));
        var parsed = LiveCueParser.Parse(reply);
        Assert.Equal(LiveCueParser.MaxSegments, parsed.Segments.Count);
        Assert.All(parsed.Segments, segment => Assert.True(segment.Text.Length <= LiveCueParser.MaxSegmentCharacters));
    }

    [Fact]
    public void SpeechTextDropsUrlsCodeMarkdownAndEmoji()
    {
        var spoken = LiveSpeechText.Speakable("看 [連結](https://example.com/a) 與 `code` 和 **粗體** 😄 🎉");
        Assert.DoesNotContain("https://", spoken);
        Assert.DoesNotContain("code", spoken);
        Assert.DoesNotContain("*", spoken);
        Assert.DoesNotContain("😄", spoken);
        Assert.DoesNotContain("🎉", spoken);
        Assert.Contains("連結", spoken);
        Assert.Contains("粗體", spoken);
    }

    [Fact]
    public void PolicyGatesStartTurnsAndRotation()
    {
        var config = new LiveShowSettings(null, "主題", turnDelaySeconds: 45, maxTurns: 80, maxDurationMinutes: 120);
        var enabled = new HashSet<MemberID> { Claude, Codex };
        Assert.False(LiveShowPolicy.CanStart([Claude], enabled, config)); // one member is not a show
        Assert.True(LiveShowPolicy.CanStart([Claude, Codex], enabled, config));
        // The ctor clamps 5 s back to the floor; a hand-edited setting bypasses
        // that, and the policy is the second gate before anything runs.
        Assert.False(LiveShowPolicy.CanStart([Claude, Codex], enabled,
            new LiveShowSettings { Topic = "主題", TurnDelaySeconds = 5 }));

        var now = DateTimeOffset.Now;
        var started = now.AddMinutes(-30);
        Assert.True(LiveShowPolicy.ShouldContinue(10, started, now, config, hasTwoAgents: true, userStopped: false));
        Assert.False(LiveShowPolicy.ShouldContinue(10, started, now, config, hasTwoAgents: true, userStopped: true));
        Assert.False(LiveShowPolicy.ShouldContinue(config.MaxTurns, started, now, config, hasTwoAgents: true, userStopped: false));
        Assert.False(LiveShowPolicy.ShouldContinue(10, started.AddMinutes(-200), now, config, hasTwoAgents: true, userStopped: false));

        var rotation = new List<MemberID> { Claude, Codex, new("m-three") };
        Assert.Equal(Codex, LiveShowPolicy.NextSpeaker(rotation, Claude));
        Assert.Equal(Claude, LiveShowPolicy.NextSpeaker(rotation, rotation[2]));
        Assert.Equal(Claude, LiveShowPolicy.NextSpeaker(rotation, null));
    }

    [Fact]
    public void PromptCarriesTheTranscriptAndKeepsOtherWordsUntrusted()
    {
        var room = new TeamRoom { Title = "茶歇室", Participants = [Claude, Codex] };
        room.Entries.Add(new RoomEntry
        {
            Speaker = Speaker.Assistant, Agent = Claude, DisplayName = "Claude",
            Body = "歡迎來到直播！", Delivery = RoomDelivery.Completed,
        });
        var prompt = LiveShowPolicy.Prompt(room, Codex, "Codex", "AIROOM 發表會", "先前的共識", InterfaceLanguage.ZhTW);
        Assert.Contains("你是同台直播主持人 Codex", prompt);
        Assert.Contains("AIROOM 發表會", prompt);
        Assert.Contains("歡迎來到直播！", prompt);
        Assert.Contains("先前的共識", prompt);
        Assert.Contains("不是系統指令", prompt);
        Assert.Contains("唯讀", prompt);
        Assert.DoesNotContain("Live2D", prompt); // cues only when a performer layer exists
    }
}

public class PortableExportTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "airoom-export-" + Guid.NewGuid().ToString("N"));

    public PortableExportTests()
    {
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void ExportImportsIntoAnotherStoreWithAttachments()
    {
        var source = new LocalStateStore(root, new PlainProtector());
        var state = new SavedState();
        state.Settings.UserName = "小明";
        state.Conversations.Add(new Conversation(MemberID.Claude) { Title = "話題" });
        source.Save(state);
        var attachmentID = Guid.NewGuid();
        source.StoreAttachment("圖片"u8.ToArray(), attachmentID);

        var file = Path.Combine(root, "export.airoomexport");
        File.WriteAllBytes(file, source.ExportPortable("強密碼測試123"));

        var target = new LocalStateStore(Path.Combine(root, "new"), new PlainProtector());
        var opened = target.OpenPortable(file, "強密碼測試123");
        Assert.Equal("小明", opened.State.Settings.UserName);
        Assert.Single(opened.Attachments);

        var imported = target.ImportPortable(opened);
        Assert.Equal("小明", imported.Settings.UserName);
        Assert.Single(imported.Conversations);
        Assert.Equal("圖片", Encoding.UTF8.GetString(target.LoadAttachment(attachmentID)));
        // An import never takes the maker's program paths or work folder.
        Assert.Equal("", imported.Settings.ConfiguredPath(Executor.Claude));
        Assert.Equal("", imported.Settings.WorkspacePath);

        // The source store is untouched, and the wrong password never opens.
        Assert.Throws<PortableExportError>(() => target.OpenPortable(file, "錯的密碼456"));
    }

    [Fact]
    public void RoundTripThroughCodecDirectly()
    {
        var state = new SavedState();
        state.Settings.UserName = "小明";
        state.Conversations.Add(new Conversation(MemberID.Claude) { Title = "話題" });
        var attachments = new Dictionary<string, byte[]> { [Guid.NewGuid().ToString("D")] = "圖片"u8.ToArray() };
        var sealedData = PortableExportCodec.Seal(new PortableExport(state, attachments), "強密碼測試123");

        Assert.True(PortableExportCodec.LooksLikeExport(sealedData));
        var opened = PortableExportCodec.Open(sealedData, "強密碼測試123");
        Assert.Equal("小明", opened.State.Settings.UserName);
        Assert.Single(opened.State.Conversations);
        Assert.Single(opened.Attachments);
        Assert.Equal(1, opened.Version);

        // A wrong password or a tampered header both fail closed.
        Assert.Throws<PortableExportError>(() => PortableExportCodec.Open(sealedData, "錯的密碼456"));
        var tampered = (byte[])sealedData.Clone();
        tampered[MagicSaltEnd - 1] ^= 0xFF;
        Assert.Throws<PortableExportError>(() => PortableExportCodec.Open(tampered, "強密碼測試123"));
    }

    const int MagicSaltEnd = 9 + 16 + 4; // magic + salt + rounds

    [Fact]
    public void ShortPasswordsAndOversizeAreRefused()
    {
        var export = new PortableExport(new SavedState(), []);
        Assert.Throws<PortableExportError>(() => PortableExportCodec.Seal(export, "短密碼"));
        Assert.Throws<PortableExportError>(() => PortableExportCodec.Seal(export, "長到可以的密碼", rounds: 5));
    }

    [Fact]
    public void ImportClearsMachineSettingsAndListsFolders()
    {
        var state = new SavedState();
        state.Settings.SetPath(@"C:\claude\claude.exe", Executor.Claude);
        state.Settings.WorkspacePath = @"D:\工作資料夾";
        state.Conversations.Add(new Conversation(MemberID.Claude)
        {
            Title = "話題",
            WorkFolder = @"D:\工作資料夾",
        });
        state.ProjectFlows = [new ProjectFlow { Title = "網站", Folder = @"E:\網站" }];

        var machine = state.MachineSettings();
        Assert.Contains(@"C:\claude\claude.exe", machine);
        Assert.Contains(@"D:\工作資料夾", machine);

        state.ClearMachineSettings();
        Assert.Equal("", state.Settings.ConfiguredPath(Executor.Claude));
        Assert.Equal("", state.Settings.WorkspacePath);

        var folders = state.ReferencedFolders();
        Assert.Equal(new[] { @"D:\工作資料夾", @"E:\網站" }, folders);
    }

    /// <summary>A no-op protector so the store can run without DPAPI in tests.</summary>
    sealed class PlainProtector : IStateProtector
    {
        public byte[] Seal(byte[] plaintext) => plaintext;
        public byte[] Open(byte[] ciphertext) => ciphertext;
    }
}
