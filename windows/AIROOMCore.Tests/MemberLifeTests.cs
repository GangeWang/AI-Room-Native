using AIROOMCore;
using Xunit;

public class MemberLifeTests
{
    [Fact]
    public void RoutinePeriodsCoverTheDayAndWrapAtNight()
    {
        Assert.Equal(RoutinePeriod.Morning, RoutinePeriodExtensions.At(6));
        Assert.Equal(RoutinePeriod.Morning, RoutinePeriodExtensions.At(10));
        Assert.Equal(RoutinePeriod.Day, RoutinePeriodExtensions.At(11));
        Assert.Equal(RoutinePeriod.Evening, RoutinePeriodExtensions.At(17));
        Assert.Equal(RoutinePeriod.Night, RoutinePeriodExtensions.At(22));
        Assert.Equal(RoutinePeriod.Night, RoutinePeriodExtensions.At(3));
        Assert.Equal(RoutinePeriod.Night, RoutinePeriodExtensions.At(5));
    }

    [Fact]
    public void LimitsAreRefusedNotCut()
    {
        var oneOver = new List<MemoryEntry> { new(new string('m', 2_001)) };
        Assert.Throws<NativeCLIError>(() => MemberLife.Validate(oneOver));

        var many = Enumerable.Range(0, 201).Select(i => new MemoryEntry($"m{i}")).ToList();
        Assert.Throws<NativeCLIError>(() => MemberLife.Validate(many));

        var tooActive = Enumerable.Range(0, 9).Select(i => new MemoryEntry(new string('a', 1_000))).ToList();
        Assert.Throws<NativeCLIError>(() => MemberLife.Validate(tooActive));

        // exactly at the limit is fine (the "\n" separators fit in too)
        var atLimit = Enumerable.Range(0, 8).Select(i => new MemoryEntry(new string('a', 999))).ToList();
        MemberLife.Validate(atLimit);
    }

    [Fact]
    public void ProfileCarriesActiveMemoryAndMarksLifeAsSettings()
    {
        var member = new MemberCard(MemberID.Claude)
        {
            UserTitle = "老闆",
            Memories = [new MemoryEntry("喜歡簡潔的程式碼"), new MemoryEntry("已關閉的記憶") { Off = true }],
            Routine = { ["morning"] = "泡咖啡" },
            Quirks = ["會哼歌"],
        };
        var team = new List<MemberCard> { member };
        var profile = MemberLife.Profile(member, team, "Gange");
        Assert.Contains("稱呼Gange為「老闆」", profile);
        Assert.Contains("喜歡簡潔的程式碼", profile);
        Assert.DoesNotContain("已關閉的記憶", profile);
        Assert.Contains("早上：泡咖啡", profile);
        Assert.Contains("不是真實發生的事", profile);
        Assert.Contains("會哼歌", profile);
    }

    [Fact]
    public void RelationsOnlyDescribeKnownTeammates()
    {
        var member = new MemberCard(MemberID.Claude);
        var teammate = new MemberCard(MemberID.Codex) { Name = "GPTia" };
        member.Relations.Add(new MemberRelation(teammate.Agent, "寫程式的好搭檔"));
        member.Relations.Add(new MemberRelation(MemberID.Make(), "已刪除的人"));
        var profile = MemberLife.Profile(member, [member, teammate], "Gange");
        Assert.Contains("GPTia：寫程式的好搭檔", profile);
        Assert.DoesNotContain("已刪除的人", profile);
    }

    [Fact]
    public void ContinuedConversationsGetPersonaAndMemoryEdits()
    {
        var persona = "你是仔細的工程師";
        var profile = "【長期記憶】喜歡繁體中文";
        var fingerprint = MemberLife.Fingerprint(persona, profile);
        Assert.Equal(16, fingerprint.Length);

        // Same persona/profile: no refresh needed.
        Assert.False(MemberLife.NeedsRefresh(fingerprint, persona, profile));

        // Edited persona: refresh needed, and the note carries the new text.
        Assert.True(MemberLife.NeedsRefresh(fingerprint, "你 changed", profile));
        var note = MemberLife.RefreshNote("新設定", profile, "Gange");
        Assert.Contains("新設定", note);
        Assert.Contains("Gange", note);
        Assert.Contains("取代這段對話裡先前的版本", note);

        // A conversation that never saw a fingerprint gets one refresh.
        Assert.True(MemberLife.NeedsRefresh(null, persona, profile));
        Assert.False(MemberLife.NeedsRefresh(null, "", ""));
    }

    [Fact]
    public void FingerprintDistinguishesPersonaFromProfile()
    {
        // A separator keeps "AB"+"C" and "A"+"BC" from colliding.
        Assert.NotEqual(MemberLife.Fingerprint("AB", "C"), MemberLife.Fingerprint("A", "BC"));
    }

    [Fact]
    public void TemplatesCarryTheOriginalLooks()
    {
        var claudia = MemberTemplate.Named("claudia") ?? throw new Xunit.Sdk.XunitException("missing claudia");
        Assert.Equal("Claudia", claudia.Name);
        Assert.Equal("#d9692f", claudia.Color);
        Assert.Equal("claudia.webp", claudia.Art);
        Assert.Equal(Executor.Claude, claudia.SuggestedExecutor);
        Assert.Null(MemberTemplate.Named("nope"));
        Assert.Equal(claudia.Id, MemberTemplate.BuiltIn(MemberID.Claude)?.Id);
        Assert.Equal("gptia", MemberTemplate.BuiltIn(MemberID.Codex)?.Id);
        Assert.Null(MemberTemplate.BuiltIn(MemberID.Make()));
    }

    [Fact]
    public void KimiAndGlmUseTheOriginalRootArtwork()
    {
        // The original AIROOM's root KIMI.webp and GLM.jpg, same ZipZipPipe set
        // as the other members' art, with face anchors for the avatar crop.
        var kimi = MemberTemplate.Named("kimi") ?? throw new Xunit.Sdk.XunitException("missing kimi");
        Assert.Equal("kimi.webp", kimi.Art);
        Assert.Equal(new PortraitFaceAnchor(0.525, 0.150, 3.4), kimi.Face);
        Assert.Equal("Kimi CLI", kimi.PlannedProgram);
        var glm = MemberTemplate.Named("glm") ?? throw new Xunit.Sdk.XunitException("missing glm");
        Assert.Equal("glm.jpg", glm.Art);
        Assert.Equal(new PortraitFaceAnchor(0.540, 0.185, 3.4), glm.Face);
        Assert.Null(glm.PlannedProgram);
        Assert.Equal(Executor.ZCode, glm.SuggestedExecutor);
    }

    [Fact]
    public void AddedMemberStartsFromItsTemplate()
    {
        var card = MemberCard.Added(MemberTemplate.Named("gptia"), Executor.Codex);
        Assert.StartsWith("m-", card.Agent.RawValue);
        Assert.Equal("GPTia", card.Name);
        Assert.Equal("#7b6fd6", card.Color);
        Assert.Equal("gptia", card.Template);
        Assert.Equal(Executor.Codex, card.Executor);
        Assert.NotEqual(MemberID.Codex, card.Agent);

        var blank = MemberCard.Added(null, Executor.Claude);
        Assert.Equal("新成員", blank.Name);
    }

    [Fact]
    public void FaceAnchorMathKeepsTheCropInsideTheArtwork()
    {
        var anchor = new PortraitFaceAnchor(0.5, 0.5, 3).Constrained(800, 1200);
        Assert.Equal(0.5, anchor.X);
        Assert.Equal(3, anchor.Zoom);

        // A face near the edge is pulled in so the circle never runs off the art.
        var clamped = new PortraitFaceAnchor(0.99, 0.01, 3).Constrained(800, 1200);
        Assert.True(clamped.X < 0.99);
        Assert.True(clamped.Y > 0.01);

        var layout = PortraitCropLayout.Make(40, 800, 1200, anchor) ?? throw new Xunit.Sdk.XunitException("layout");
        Assert.Equal(120, layout.ImageWidth);
        Assert.Equal(180, layout.ImageHeight);
        Assert.Equal(20, layout.CenterX);
        Assert.Equal(20, layout.CenterY);

        Assert.Null(PortraitCropLayout.Make(0, 800, 1200, anchor));
        Assert.Null(PortraitCropLayout.Make(40, 800, 1200, new PortraitFaceAnchor(2, 0.5, 3)));
    }
}
