using AIROOMCore;
using Xunit;

/// <summary>Drives the real GLM and Gemini CLIs through the roundtable pipeline
/// (independent preparation → issue list → a deliberation turn in the meeting
/// engine) when AIROOM_LIVE_CLI=1 and those members can run on this machine.</summary>
public class RoundtableLiveTests
{
    static bool LiveReady() => Environment.GetEnvironmentVariable("AIROOM_LIVE_CLI") == "1";

    /// <summary>A clean, empty working folder for the read-only roundtable calls.</summary>
    static readonly Lazy<string> LiveFolder = new(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "airoom-roundtable-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    });

    static (MemberCard Glm, MemberCard Gemini) Cards() => new(
        new MemberCard(new MemberID("m-liveglm"), Executor.ZCode, name: "GLM"),
        new MemberCard(new MemberID("m-liveagy"), Executor.Antigravity, name: "Gemini"));

    [Fact]
    public async Task LivePrepareFindingsAndDeliberation()
    {
        if (!LiveReady()) return; // Skipped silently unless the live switch is on.
        var runner = new CliProcessRunner();
        var (glm, gemini) = Cards();
        var members = new List<MemberID> { glm.Agent, gemini.Agent };
        var names = new Dictionary<string, string> { [glm.Agent.RawValue] = glm.Name, [gemini.Agent.RawValue] = gemini.Name };
        var topic = new RoundtableTopic("介紹短片分鏡", RoundtableKind.Proposals,
            "我們要做一支 60 秒的產品介紹短片。請提出你的分鏡方案（三段即可）。", null, members, glm.Agent);

        // Independent preparation: two real calls in parallel, each seeing only the material.
        var prepareA = runner.RunAsync(agent: glm.Agent, fallbackWorkspace: LiveFolder.Value,
            prompt: RoundtableFlow.PreparePrompt(topic, glm, "測試主人", [glm, gemini]));
        var prepareB = runner.RunAsync(agent: gemini.Agent, fallbackWorkspace: LiveFolder.Value,
            prompt: RoundtableFlow.PreparePrompt(topic, gemini, "測試主人", [glm, gemini]));
        var replies = await Task.WhenAll(prepareA, prepareB);
        topic.Submissions.Add(new RoundtableSubmission(glm.Agent, replies[0]));
        topic.Submissions.Add(new RoundtableSubmission(gemini.Agent, replies[1]));
        Assert.True(topic.Completed >= 1, $"both calls failed: {replies[0][..Math.Min(80, replies[0].Length)]} / {replies[1][..Math.Min(80, replies[1].Length)]}");

        // The lead compiles a de-duplicated issue list from both versions.
        var findings = await runner.RunAsync(agent: topic.Lead, fallbackWorkspace: LiveFolder.Value,
            prompt: RoundtableFlow.FindingsPrompt(topic, glm, names));
        topic.Findings = findings;
        Assert.True(RoundtableFlow.CountFindings(findings) > 0, $"no findings in: {findings[..Math.Min(120, findings.Length)]}");

        // Deliberation: a meeting turn in the hidden room, with the briefing.
        var room = new TeamRoom
        {
            Title = $"圓桌：{topic.Title}",
            Participants = members,
            Lounge = new LoungeState { Style = LoungeStyle.Meeting },
            RoundtableID = topic.Id,
            Briefing = RoundtableFlow.Briefing(topic, names),
        };
        room.Entries.Add(new RoomEntry
        {
            Speaker = Speaker.User, DisplayName = "你",
            Body = RoundtableFlow.DeliberationOpening(topic),
        });
        LoungeEngine.HumanSpoke(room.Lounge, [], members, room.Entries[0].Id);
        var speaker = LoungeEngine.NextSpeaker(room.Lounge, members);
        Assert.True(speaker is not null);
        var member = speaker == glm.Agent ? glm : gemini;
        var turn = await runner.RunAsync(agent: member.Agent, fallbackWorkspace: LiveFolder.Value,
            prompt: LoungeEngine.TurnPrompt(room, member, [glm, gemini], room.Lounge, "測試主人", InterfaceLanguage.ZhTW));
        var reply = LoungeEngine.ParseReply(turn);
        var outcome = LoungeEngine.Apply(reply, member.Agent, members, room.Lounge, Guid.NewGuid(), names);
        Assert.Equal(LoungeEngine.Outcome.Show, outcome.Kind);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Body));
    }
}
