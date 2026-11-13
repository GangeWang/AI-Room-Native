using AIROOMCore;
using Xunit;

public class RoundtableTests
{
    static readonly MemberCard Claude = new(MemberID.Claude, name: "克勞蒂亞");
    static readonly MemberCard Codex = new(MemberID.Codex, name: "GPT娘");
    static readonly Dictionary<string, string> Names = new()
    {
        [MemberID.Claude.RawValue] = "克勞蒂亞",
        [MemberID.Codex.RawValue] = "GPT娘",
    };

    static RoundtableTopic Topic()
    {
        var topic = new RoundtableTopic("第一集", RoundtableKind.Proposals, "直播第一集要做什麼？",
            null, [MemberID.Claude, MemberID.Codex], MemberID.Claude);
        topic.Submissions =
        [
            new RoundtableSubmission(MemberID.Claude, "CLAUDE_PLAN"),
            new RoundtableSubmission(MemberID.Codex, "CODEX_PLAN"),
        ];
        return topic;
    }

    [Fact]
    public void PreparationIsIndependent()
    {
        var draft = Topic();
        draft.Submissions = [new RoundtableSubmission(MemberID.Claude, "CLAUDE_PLAN")];
        var prompt = RoundtableFlow.PreparePrompt(draft, Codex, "小明");
        Assert.DoesNotContain("CLAUDE_PLAN", prompt);
        Assert.Contains("直播第一集要做什麼？", prompt);
        Assert.Contains("看不到", prompt);
    }

    [Fact]
    public void PeerReviewShowsOnlyTheOthers()
    {
        var prompt = RoundtableFlow.PeerReviewPrompt(Topic(), Claude, Names);
        Assert.Contains("CODEX_PLAN", prompt);
        Assert.DoesNotContain("CLAUDE_PLAN", prompt);
    }

    [Fact]
    public void FindingsAndBriefingCarryEveryVersion()
    {
        var topic = Topic();
        topic.PeerReviews = [new RoundtableSubmission(MemberID.Codex, "REVIEW_X")];
        var findings = RoundtableFlow.FindingsPrompt(topic, Claude, Names);
        Assert.Contains("CLAUDE_PLAN", findings);
        Assert.Contains("CODEX_PLAN", findings);
        Assert.Contains("REVIEW_X", findings);
        topic.Findings = "- 高：時間太緊（GPT娘）\n- 低：缺少片頭\n說明文字\n2. 第三個問題";
        Assert.Equal(3, topic.FindingCount);
        var brief = RoundtableFlow.Briefing(topic, Names);
        Assert.Contains("【問題清單】", brief);
        var room = new TeamRoom
        {
            Entries = [new RoomEntry { Speaker = Speaker.User, DisplayName = "你", Body = "請審議" }],
            Briefing = brief,
        };
        var prompt = LoungeEngine.TurnPrompt(room, Codex, [Claude, Codex],
            new LoungeState { Style = LoungeStyle.Meeting }, "小明", InterfaceLanguage.ZhTW);
        Assert.Contains("CLAUDE_PLAN", prompt); // deliberation sees the revealed versions
    }

    [Fact]
    public void FailedSubmissionsDoNotCountAndStatesDecode()
    {
        var topic = Topic();
        topic.Submissions[1] = new RoundtableSubmission(MemberID.Codex, error: "timeout");
        Assert.Equal(1, topic.Completed);
        Assert.True(topic.AllPrepared);
        Assert.DoesNotContain("timeout", RoundtableFlow.Briefing(topic, Names));
        var restored = CoreJson.Deserialize<RoundtableTopic>(CoreJson.Serialize(topic))!;
        Assert.Equal(topic.Status, restored.Status);
        Assert.Equal(topic.Completed, restored.Completed);
        var minimal = CoreJson.Deserialize<RoundtableTopic>(
            """{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","title":"舊"}""")!;
        Assert.Equal(RoundtableKind.Proposals, minimal.Kind);
        Assert.Equal(RoundtableStatus.Preparing, minimal.Status);
    }

    [Fact]
    public void LegacyFailedFindingsDecodeIntoTheErrorField()
    {
        var topic = Topic();
        topic.Findings = RoundtableTopic.LegacyFindingsPrefix + "逾時";
        topic.FindingsError = null;
        RoundtableTopic.MigrateLegacyFindings([topic]);
        Assert.Equal("逾時", topic.FindingsError);
        Assert.Equal("", topic.Findings);
        // A topic that already has an error (or a real list) is left alone.
        var clean = Topic();
        clean.Findings = "- 高：時間太緊";
        RoundtableTopic.MigrateLegacyFindings([clean]);
        Assert.Null(clean.FindingsError);
        Assert.Equal("- 高：時間太緊", clean.Findings);
    }

    [Fact]
    public void HandoffCarriesConclusionAndAdoptedVersion()
    {
        var topic = Topic();
        topic.Conclusion = "結論：先做小需求";
        topic.Adopted = MemberID.Codex;
        var text = RoundtableFlow.Handoff(topic, Names);
        Assert.Contains("結論：先做小需求", text);
        Assert.Contains("採用的候選（GPT娘）", text);
        Assert.Contains("CODEX_PLAN", text);
        Assert.False(RoundtableKind.Review.HasCandidates());
        Assert.True(RoundtableKind.Candidates.HasCandidates());
    }

    [Fact]
    public void AttentionRoutesToTheRoundtable()
    {
        var state = new SavedState();
        var topic = Topic();
        topic.Status = RoundtableStatus.Conclusion;
        var room = new TeamRoom { RoundtableID = topic.Id, Lounge = new LoungeState { Phase = LoungePhase.AwaitingHost } };
        state.Rooms = [room];
        state.RoundtableTopics = [topic];
        var items = AttentionBuilder.Items(state, new Dictionary<Executor, string>());
        Assert.Contains(items, item => item.Target.Roundtable == topic.Id && item.Kind == AttentionKind.Acceptance);
        Assert.Contains(items, item => item.Target.Roundtable == topic.Id && item.Kind == AttentionKind.Decision);
    }
}
