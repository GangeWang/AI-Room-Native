using System.Text.Json;
using System.Text.Json.Nodes;
using AIROOMCore;
using Xunit;

public class LoungeTests
{
    static readonly List<MemberID> Both = [MemberID.Claude, MemberID.Codex];
    static readonly List<MemberCard> Cards =
        [new(MemberID.Claude, name: "克勞蒂亞"), new(MemberID.Codex, name: "GPT娘")];

    static LoungeReply Reply(LoungeAction action, string body = "", string? emoji = null) =>
        new(action, body, emoji, null, [], Unstructured: false);

    sealed class Fixed : Random
    {
        readonly double value;
        public Fixed(double value) => this.value = value;
        public override double NextDouble() => value;
    }

    [Fact]
    public void MentionsMatchDisplayAndShortNamesInOrder()
    {
        Assert.Equal([MemberID.Codex, MemberID.Claude], LoungeEngine.Mentions("@GPT娘 你先說，然後 @claude", Cards));
        Assert.Equal([], LoungeEngine.Mentions("大家好", Cards));
    }

    [Fact]
    public void ChatNeverRepeatsSpeakerAndHonoursMentions()
    {
        var state = new LoungeState();
        LoungeEngine.HumanSpoke(state, [MemberID.Codex], Both, Guid.NewGuid());
        Assert.Equal(MemberID.Codex, LoungeEngine.NextSpeaker(state, Both, random: new Fixed(0)));
        LoungeEngine.Apply(Reply(LoungeAction.Speak, "嗨"), MemberID.Codex, Both, state, Guid.NewGuid());
        foreach (var draw in new[] { 0.0, 0.5, 0.99 })
        {
            Assert.Equal(MemberID.Claude, LoungeEngine.NextSpeaker(state, Both, random: new Fixed(draw)));
        }
    }

    [Fact]
    public void ChatEndsWhenEveryonePasses()
    {
        var state = new LoungeState();
        LoungeEngine.HumanSpoke(state, [], Both, Guid.NewGuid());
        Assert.Equal(LoungeEngine.Outcome.Silent,
            LoungeEngine.Apply(Reply(LoungeAction.Pass), MemberID.Claude, Both, state, Guid.NewGuid()).Kind);
        var second = LoungeEngine.Apply(Reply(LoungeAction.Pass), MemberID.Codex, Both, state, Guid.NewGuid());
        Assert.Equal(LoungeEngine.Outcome.Ended, second.Kind);
        Assert.Equal(LoungePhase.Idle, state.Phase);
    }

    [Fact]
    public void ProposalNeedsEnoughTalkThenEveryoneAccepting()
    {
        var state = new LoungeState();
        LoungeEngine.HumanSpoke(state, [], Both, Guid.NewGuid());
        LoungeEngine.Apply(Reply(LoungeAction.Propose, "先這樣吧"), MemberID.Claude, Both, state, Guid.NewGuid());
        Assert.Equal(LoungePhase.Talking, state.Phase); // too early: treated as a remark
        foreach (var speaker in new[] { MemberID.Codex, MemberID.Claude, MemberID.Codex })
        {
            LoungeEngine.Apply(Reply(LoungeAction.Speak, "嗯"), speaker, Both, state, Guid.NewGuid());
        }
        LoungeEngine.Apply(Reply(LoungeAction.Propose, "今天聊得差不多了"), MemberID.Claude, Both, state, Guid.NewGuid());
        Assert.Equal(LoungePhase.Closing, state.Phase);
        Assert.Equal(MemberID.Codex, LoungeEngine.NextSpeaker(state, Both));
        // The proposal author is already on board: the other member's accept ends it.
        var acceptance = LoungeEngine.Apply(Reply(LoungeAction.Accept), MemberID.Codex, Both, state, Guid.NewGuid());
        Assert.Equal(LoungeEngine.Outcome.Ended, acceptance.Kind);
        Assert.Equal(LoungePhase.Idle, state.Phase);
    }

    [Fact]
    public void RejectReopensTheChat()
    {
        var state = new LoungeState { Phase = LoungePhase.Closing, ProposalFrom = MemberID.Claude, Calls = 6 };
        var rejection = LoungeEngine.Apply(Reply(LoungeAction.Reject, "等等，還有一點"), MemberID.Codex, Both, state, Guid.NewGuid());
        Assert.Equal(LoungeEngine.Outcome.Show, rejection.Kind);
        Assert.Equal("等等，還有一點", rejection.Body);
        Assert.Equal(LoungePhase.Talking, state.Phase);
        Assert.Null(state.ProposalFrom);
    }

    [Fact]
    public void AskUserLeavesAPendingQuestionUntilAnswered()
    {
        var state = new LoungeState();
        LoungeEngine.HumanSpoke(state, [], Both, Guid.NewGuid());
        LoungeEngine.Apply(Reply(LoungeAction.AskUser, "你週末想去哪？"), MemberID.Claude, Both, state, Guid.NewGuid());
        Assert.Equal(["你週末想去哪？"], state.PendingQuestions.Select(question => question.Text));
        Assert.Equal(LoungePhase.Talking, state.Phase); // others keep chatting
        var id = state.PendingQuestions[0].Id;
        LoungeEngine.HumanSpoke(state, [MemberID.Claude], Both, Guid.NewGuid(), answering: id);
        Assert.Empty(state.PendingQuestions);
    }

    [Fact]
    public void MeetingGoesRoundTheTableThenHands()
    {
        var state = new LoungeState { Style = LoungeStyle.Meeting };
        var start = Guid.NewGuid();
        LoungeEngine.HumanSpoke(state, [], Both, start);
        Assert.Equal(start, state.MeetingStartEntryID);
        Assert.Equal(MemberID.Claude, LoungeEngine.NextSpeaker(state, Both));
        // A meeting reply can't be silent.
        var silent = LoungeEngine.Apply(Reply(LoungeAction.Pass), MemberID.Claude, Both, state, Guid.NewGuid());
        Assert.Equal(LoungeEngine.Outcome.Show, silent.Kind);
        Assert.Equal("（沒有補充）", silent.Body);
        Assert.Equal(MemberID.Codex, LoungeEngine.NextSpeaker(state, Both));
        LoungeEngine.Apply(Reply(LoungeAction.Speak, "同意"), MemberID.Codex, Both, state, Guid.NewGuid());
        Assert.Null(LoungeEngine.NextSpeaker(state, Both));
        var votes = new Dictionary<string, HandVote>
        {
            ["Claude Code"] = new(1, "小補充"),
            ["Codex"] = new(3, "有風險"),
        };
        Assert.Equal(MemberID.Codex, LoungeEngine.ChooseHand(votes, state, Both));
        state.Supplemented = [MemberID.Codex];
        Assert.Equal(MemberID.Claude, LoungeEngine.ChooseHand(votes, state, Both));
    }

    [Fact]
    public void MeetingMentionOnlyAsksThoseNamed()
    {
        var state = new LoungeState { Style = LoungeStyle.Meeting };
        LoungeEngine.HumanSpoke(state, [MemberID.Codex], Both, Guid.NewGuid());
        Assert.Equal([MemberID.Codex], state.MeetingPending);
        Assert.True(state.MentionedOnly);
    }

    [Fact]
    public void MentionedMemberIsToldToAnswerInWords()
    {
        var state = new LoungeState();
        LoungeEngine.HumanSpoke(state, [MemberID.Codex], Both, Guid.NewGuid());
        var room = new TeamRoom
        {
            Entries = [new RoomEntry { Speaker = Speaker.User, DisplayName = "你", Body = "@GPT娘 你還好嗎" }],
        };
        Assert.Contains("你剛被點名", LoungeEngine.TurnPrompt(room, Cards[1], Cards, state, "使用者", InterfaceLanguage.ZhTW));
        Assert.DoesNotContain("你剛被點名", LoungeEngine.TurnPrompt(room, Cards[0], Cards, state, "使用者", InterfaceLanguage.ZhTW));
    }

    [Fact]
    public void ParseReplyHandlesJsonFencesAndProse()
    {
        var json = LoungeEngine.ParseReply("""{"action":"speak","body":"哈哈","emoji":"😄","questions":[{"to":"user","text":"你呢？"}]}""");
        Assert.Equal(LoungeAction.Speak, json.Action);
        Assert.Equal("😄", json.Emoji);
        Assert.Equal(["你呢？"], json.Questions);
        var fenced = LoungeEngine.ParseReply("```json\n{\"action\":\"pass\",\"body\":\"\"}\n```");
        Assert.Equal(LoungeAction.Pass, fenced.Action);
        var prose = LoungeEngine.ParseReply("我覺得可以。");
        Assert.True(prose.Unstructured);
        Assert.Equal("我覺得可以。", prose.Body);
        Assert.Null(LoungeEngine.ParseReply("""{"action":"react","body":"","emoji":"💣"}""").Emoji);
        Assert.Equal(LoungeAction.Pass, LoungeEngine.ParseReply("""{"action":"speak","body":"  "}""").Action);
        Assert.Equal(3, LoungeEngine.ParseHand("""{"want":7,"why":"x"}""")?.Want);
        Assert.Null(LoungeEngine.ParseHand("我想說話"));
    }

    [Fact]
    public void PromptsUseSummaryAndOnlyNewerMessages()
    {
        var old = new RoomEntry { Speaker = Speaker.User, DisplayName = "你", Body = "很久以前的話" };
        var recent = new RoomEntry { Speaker = Speaker.User, DisplayName = "你", Body = "最新的話" };
        var room = new TeamRoom { Entries = [old, recent], Summary = "之前聊過旅行", SummaryThroughEntryID = old.Id };
        var (summary, transcript) = LoungeEngine.Context(room, InterfaceLanguage.ZhTW);
        Assert.Equal("之前聊過旅行", summary);
        Assert.DoesNotContain("很久以前的話", transcript);
        Assert.Contains("最新的話", transcript);
    }

    [Fact]
    public void MarkdownExportAndLegacyRoomDecoding()
    {
        var room = new TeamRoom
        {
            Title = "週末",
            Entries =
            [
                new RoomEntry { Speaker = Speaker.User, DisplayName = "你", Body = "去哪玩？" },
                new RoomEntry { Speaker = Speaker.Assistant, Agent = MemberID.Claude, DisplayName = "克勞蒂亞", Body = "👍", IsReaction = true },
                new RoomEntry { Speaker = Speaker.Assistant, Agent = MemberID.Codex, DisplayName = "GPT娘", Body = "結論：去海邊", IsMinutes = true },
            ],
        };
        var markdown = LoungeEngine.Markdown(room);
        Assert.StartsWith("# 週末", markdown);
        Assert.Contains("克勞蒂亞 回應了 👍", markdown);
        Assert.Contains("GPT娘**（會議紀要）", markdown);

        // A room saved by an older build (no lounge/summary keys) still loads
        // with its defaults, and old delivery-less assistant text stays suspect.
        var node = JsonNode.Parse(CoreJson.Serialize(room))!.AsObject();
        foreach (var key in new[] { "lounge", "summary", "summaryThroughEntryID", "carriedSummary", "entries" })
        {
            node.Remove(key);
        }
        var legacy = CoreJson.Deserialize<TeamRoom>(node.ToJsonString())!;
        Assert.Equal(LoungeStyle.Chat, legacy.Lounge.Style);
        Assert.Equal(LoungePhase.Idle, legacy.Lounge.Phase);
        var stale = CoreJson.Deserialize<TeamRoom>(CoreJson.Serialize(room))!;
        Assert.Equal(3, stale.Entries.Count);
    }

    /// <summary>Seen live from DeepSeek: quotes inside the body weren't escaped, so the raw JSON was shown.</summary>
    [Fact]
    public void UnescapedQuotesInsideTheBodyStillParse()
    {
        var raw = """{"action":"speak","body":"最精彩的是 please work 之後隔兩分鐘那筆 revert "please work"，等於神明已讀不回，只好自己把祈禱收回來 😄","emoji":"😄"}""";
        var reply = LoungeEngine.ParseReply(raw);
        Assert.False(reply.Unstructured);
        Assert.Equal(LoungeAction.Speak, reply.Action);
        Assert.Equal("""最精彩的是 please work 之後隔兩分鐘那筆 revert "please work"，等於神明已讀不回，只好自己把祈禱收回來 😄""", reply.Body);
        Assert.Equal("😄", reply.Emoji);
    }

    [Fact]
    public void RepairKeepsValidJsonAndFixesCommonSlips()
    {
        // Raw line breaks inside a string, and quotes followed by ordinary text.
        var broken = "{\"action\":\"speak\",\"body\":\"第一行\n他說 \"好\" 就走了\",\"next\":\"Claude\"}";
        var reply = LoungeEngine.ParseReply(broken);
        Assert.Equal("第一行\n他說 \"好\" 就走了", reply.Body);
        Assert.Equal("Claude", reply.Next);
        // Valid JSON is left exactly as it was.
        var valid = """{"action":"speak","body":"引號 \"已跳脫\" 沒問題","questions":["要嗎？"]}""";
        Assert.Equal(valid, LoungeEngine.RepairedJson(valid));
        Assert.Equal(["要嗎？"], LoungeEngine.ParseReply(valid).Questions);
        // Prose that isn't JSON at all is still shown as is.
        Assert.True(LoungeEngine.ParseReply("只是普通的一句話").Unstructured);
    }

    [Fact]
    public void EveryJsonPromptAsksForCornerQuotes()
    {
        var member = new MemberCard(MemberID.Claude, name: "Claude");
        var room = new TeamRoom();
        room.Entries.Add(new RoomEntry { Speaker = Speaker.User, DisplayName = "我", Body = "嗨" });
        Assert.Contains(LoungeEngine.QuoteRule,
            LoungeEngine.TurnPrompt(room, member, [member], new LoungeState(), "我", InterfaceLanguage.ZhTW));
        Assert.Contains(LoungeEngine.QuoteRule,
            LoungeEngine.TurnDelta(room, member, new LoungeState(), room.Entries[0].Id, "我", InterfaceLanguage.ZhTW));
        Assert.Contains(LoungeEngine.QuoteRule,
            LoungeEngine.HandPrompt(room, member, new LoungeState { Style = LoungeStyle.Meeting }, InterfaceLanguage.ZhTW));
        Assert.Contains(LoungeEngine.QuoteRuleEnglish,
            LoungeEngine.TurnPrompt(room, member, [member], new LoungeState(), "me", InterfaceLanguage.English));
    }
}

public class SharedRoomContextTests
{
    static TeamRoom SampleRoom() => new()
    {
        Title = "Architecture",
        Entries =
        [
            new RoomEntry { Speaker = Speaker.User, DisplayName = "Me", Body = "Design a system" },
            new RoomEntry { Speaker = Speaker.Assistant, Agent = MemberID.Claude, DisplayName = "Claudia", Body = "Use message queues" },
            new RoomEntry { Speaker = Speaker.Assistant, Agent = MemberID.Codex, DisplayName = "GPTIA", Body = "Use a durable event log" },
            new RoomEntry { Speaker = Speaker.User, DisplayName = "Me", Body = "Can we combine them?" },
        ],
    };

    [Fact]
    public void AllCompletedAgentsShareOneOrderedTranscript()
    {
        var room = SampleRoom();
        var snapshot = RoomPromptBuilder.Snapshot(room, language: InterfaceLanguage.English);
        Assert.Equal(4, snapshot.IncludedCount);
        var transcript = snapshot.Transcript;
        var ordered = new[] { "Design a system", "Use message queues", "Use a durable event log", "Can we combine them?" };
        var offsets = ordered.Select(item => transcript.IndexOf(item, StringComparison.Ordinal)).ToList();
        Assert.All(offsets, offset => Assert.True(offset >= 0, "missing an ordered line"));
        Assert.Equal(offsets.OrderBy(offset => offset), offsets);
        Assert.Equal(1, transcript.Split("Can we combine them?").Length - 1);
    }

    [Fact]
    public void InProgressAndFailedRepliesNeverPoisonSharedContext()
    {
        var room = SampleRoom();
        room.Entries.Add(new RoomEntry
        {
            Speaker = Speaker.Assistant, Agent = MemberID.Claude, DisplayName = "Claudia",
            Body = "partial output 123", Delivery = RoomDelivery.InProgress,
        });
        room.Entries.Add(new RoomEntry
        {
            Speaker = Speaker.Assistant, Agent = MemberID.Codex, DisplayName = "GPTIA",
            Body = "API request failed", Delivery = RoomDelivery.Failed,
        });
        room.Entries.Add(new RoomEntry
        {
            Speaker = Speaker.Assistant, Agent = MemberID.Codex, DisplayName = "GPTIA",
            Body = "cancelled", Delivery = RoomDelivery.Interrupted,
        });
        var snapshot = RoomPromptBuilder.Snapshot(room);
        foreach (var excluded in new[] { "partial output 123", "API request failed", "cancelled" })
        {
            Assert.DoesNotContain(excluded, snapshot.Transcript);
        }
        Assert.Equal(4, snapshot.IncludedCount);
    }

    [Fact]
    public void TranscriptBoundsPreserveRecentContinuity()
    {
        var room = new TeamRoom();
        for (var i = 0; i < 50; i++)
        {
            room.Entries.Add(new RoomEntry { Speaker = Speaker.User, DisplayName = "User", Body = $"turn-{i} " + new string('z', 100) });
        }
        var snapshot = RoomPromptBuilder.Snapshot(room, maxEntries: 10, maxCharacters: 650, perEntryLimit: 160);
        Assert.True(snapshot.Transcript.Length <= 650);
        Assert.Contains("turn-49", snapshot.Transcript);
        Assert.DoesNotContain("turn-0 ", snapshot.Transcript);
        Assert.True(snapshot.OmittedEntries > 0);
    }

    [Fact]
    public void LegacyDeliveryMarksPlaceholdersAsUnconfirmed()
    {
        Assert.Equal(RoomDelivery.Interrupted, RoomEntry.LegacyDelivery(Speaker.Assistant, "執行中……"));
        Assert.Equal(RoomDelivery.Interrupted, RoomEntry.LegacyDelivery(Speaker.Assistant, "Running… half"));
        Assert.Equal(RoomDelivery.Completed, RoomEntry.LegacyDelivery(Speaker.Assistant, "一個真正的回覆"));
        Assert.Equal(RoomDelivery.Completed, RoomEntry.LegacyDelivery(Speaker.User, "執行中……"));
    }
}
