using AIROOMCore;
using Xunit;

public class AutomationTests
{
    static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    static ScheduledActivity Daily(int hour, int minute, HashSet<int>? weekdays = null, string? lastFiredDay = null, bool enabled = true) =>
        new("提醒", ScheduleAction.Reminder, "早安", hour, minute, weekdays, enabled, lastFiredDay: lastFiredDay);

    [Fact]
    public void DailyFiresOnlyInItsMinuteAndWeekday()
    {
        var item = Daily(9, 30);
        Assert.Equal(LocalAutomation.Due.NotYet, LocalAutomation.Evaluate(item, At(2026, 11, 16, 9, 29))); // Monday
        Assert.Equal(LocalAutomation.Due.Fire, LocalAutomation.Evaluate(item, At(2026, 11, 16, 9, 30)));
        Assert.Equal(LocalAutomation.Due.NotYet, LocalAutomation.Evaluate(item, At(2026, 11, 16, 9, 31)));
        Assert.Equal(LocalAutomation.Due.NotYet, LocalAutomation.Evaluate(item, At(2026, 11, 16, 9, 30) - TimeSpan.FromMinutes(1)));

        // Already fired today: a second tick in the same minute does not repeat.
        item.LastFiredDay = "2026-11-16";
        Assert.Equal(LocalAutomation.Due.NotYet, LocalAutomation.Evaluate(item, At(2026, 11, 16, 9, 30)));
    }

    [Fact]
    public void WeekdaysAreIsoAndEmptyMeansEveryDay()
    {
        // 2026-11-14 is a Saturday (ISO 6), 2026-11-16 is a Monday (ISO 1).
        var workdays = Daily(9, 0, [1, 2, 3, 4, 5]);
        Assert.Equal(LocalAutomation.Due.Fire, LocalAutomation.Evaluate(workdays, At(2026, 11, 16, 9, 0)));
        Assert.Equal(LocalAutomation.Due.NotYet, LocalAutomation.Evaluate(workdays, At(2026, 11, 14, 9, 0)));
        var everyDay = Daily(9, 0);
        Assert.Equal(LocalAutomation.Due.Fire, LocalAutomation.Evaluate(everyDay, At(2026, 11, 14, 9, 0)));
    }

    [Fact]
    public void OneTimeFiresWithinGraceThenIsMissed()
    {
        var item = new ScheduledActivity("一次", ScheduleAction.Reminder, "開會", 0, 0,
            kind: ScheduleKind.Once, onceAt: At(2026, 11, 16, 15, 0));
        Assert.Equal(LocalAutomation.Due.NotYet, LocalAutomation.Evaluate(item, At(2026, 11, 16, 14, 59)));
        Assert.Equal(LocalAutomation.Due.Fire, LocalAutomation.Evaluate(item, At(2026, 11, 16, 15, 1)));
        Assert.Equal(LocalAutomation.Due.Fire, LocalAutomation.Evaluate(item, At(2026, 11, 16, 15, 2)));
        // Past the grace period the app was closed or asleep: skipped, never replayed.
        Assert.Equal(LocalAutomation.Due.Missed, LocalAutomation.Evaluate(item, At(2026, 11, 16, 15, 5)));
    }

    [Fact]
    public void IntervalFiresOnItsNextTimeAndAdvancesPastNow()
    {
        var now = At(2026, 11, 16, 9, 0);
        var item = new ScheduledActivity("巡檢", ScheduleAction.Reminder, "檢查", 0, 0,
            kind: ScheduleKind.Interval, intervalMinutes: 30, nextAt: now.AddMinutes(-30));
        Assert.Equal(LocalAutomation.Due.Missed, LocalAutomation.Evaluate(item, now)); // way past grace
        LocalAutomation.Advance(item, now);
        Assert.Equal(now.AddMinutes(30), item.NextAt);
        Assert.Equal(LocalAutomation.Due.NotYet, LocalAutomation.Evaluate(item, now.AddMinutes(29)));
        Assert.Equal(LocalAutomation.Due.Fire, LocalAutomation.Evaluate(item, now.AddMinutes(30)));
        LocalAutomation.Advance(item, now.AddMinutes(30));
        Assert.Equal(now.AddMinutes(60), item.NextAt);
    }

    [Fact]
    public void AdvanceTurnsOneTimeOffAndDailyRemembersTheDay()
    {
        var once = new ScheduledActivity("一次", ScheduleAction.Reminder, "x", 0, 0,
            kind: ScheduleKind.Once, onceAt: At(2026, 11, 16, 15, 0));
        LocalAutomation.Advance(once, At(2026, 11, 16, 15, 1));
        Assert.False(once.Enabled);

        var daily = Daily(9, 30);
        LocalAutomation.Advance(daily, At(2026, 11, 16, 9, 30));
        Assert.Equal("2026-11-16", daily.LastFiredDay);
    }

    [Fact]
    public void NextIsTheEarliestUpcomingDailySlot()
    {
        // Monday 09:00, schedule 22:00 with weekdays Mon–Fri → today 22:00.
        var workdays = Daily(22, 0, [1, 2, 3, 4, 5]);
        Assert.Equal(At(2026, 11, 16, 22, 0), LocalAutomation.Next(workdays, At(2026, 11, 16, 9, 0)));
        // Saturday: next is Monday 22:00.
        Assert.Equal(At(2026, 11, 16, 22, 0), LocalAutomation.Next(workdays, At(2026, 11, 14, 9, 0)));
        // Fired today already: next is tomorrow.
        var fired = Daily(9, 0);
        fired.LastFiredDay = "2026-11-16";
        Assert.Equal(At(2026, 11, 17, 9, 0), LocalAutomation.Next(fired, At(2026, 11, 16, 10, 0)));
    }

    [Fact]
    public void IdleChatNeedsTwoEnabledMembersQuotaAndRespectsCaps()
    {
        var rule = new AutoChatSettings(true, null, 120, 2);
        Assert.False(LocalAutomation.ShouldAutoChat(rule, At(2026, 11, 16, 9, 0), roomExists: false, quotaAvailable: true));
        Assert.True(LocalAutomation.ShouldAutoChat(rule, At(2026, 11, 16, 9, 0), roomExists: true, quotaAvailable: true));
        Assert.False(LocalAutomation.ShouldAutoChat(rule, At(2026, 11, 16, 9, 0), roomExists: true, quotaAvailable: false));

        rule.LastFiredAt = At(2026, 11, 16, 9, 0);
        Assert.False(LocalAutomation.ShouldAutoChat(rule, At(2026, 11, 16, 10, 0), roomExists: true, quotaAvailable: true));
        Assert.True(LocalAutomation.ShouldAutoChat(rule, At(2026, 11, 16, 12, 1), roomExists: true, quotaAvailable: true));

        rule.LastFiredAt = null;
        rule.FiredDays["2026-11-16"] = 2;
        Assert.False(LocalAutomation.ShouldAutoChat(rule, At(2026, 11, 16, 12, 1), roomExists: true, quotaAvailable: true));
    }

    [Fact]
    public void DailyOpeningOnlyAsksOncePerDay()
    {
        var opening = new DailyOpeningSettings { Enabled = true };
        Assert.True(opening.Opened(At(2026, 11, 16, 8, 0)));
        Assert.False(opening.Opened(At(2026, 11, 16, 12, 0))); // same day, relaunch
        Assert.True(opening.Opened(At(2026, 11, 17, 8, 0)));

        var off = new DailyOpeningSettings { Enabled = false };
        Assert.False(off.Opened(At(2026, 11, 17, 8, 0))); // day advances, but the feature is off
    }

    [Fact]
    public void GreeterRotatesInTeamOrder()
    {
        var team = new List<MemberID> { MemberID.Claude, MemberID.Codex, new("m-three") };
        Assert.Equal(MemberID.Claude, MemberLifeChat.NextGreeter(team, null));
        Assert.Equal(MemberID.Codex, MemberLifeChat.NextGreeter(team, MemberID.Claude));
        Assert.Equal(team[2], MemberLifeChat.NextGreeter(team, MemberID.Codex));
        Assert.Equal(MemberID.Claude, MemberLifeChat.NextGreeter(team, team[2])); // wraps around
    }

    [Fact]
    public void IdleChatPromptsStayStatelessAndBounded()
    {
        var team = new List<MemberCard>
        {
            new(MemberID.Claude, Executor.Claude, name: "Claude"),
            new(MemberID.Codex, Executor.Codex, name: "Codex"),
        };
        team[0].Persona = "嚴謹的工程師";
        team[0].Routine["day"] = "白天寫程式";
        team[1].Quirks.Add("喜歡用 emoji");

        var daytime = At(2026, 11, 16, 14, 0); // inside the Day period, so the "day" routine shows
        var opener = MemberLifeChat.ChatPrompt(team[0], team[1], team, "小明", now: daytime);
        Assert.Contains("小明開啟了成員閒聊", opener);
        Assert.Contains("嚴謹的工程師", opener);
        Assert.Contains("白天寫程式", opener);
        Assert.Contains("喜歡用 emoji", opener);
        Assert.Contains("不發起工作任務", opener);
        Assert.DoesNotContain("這是", opener[..opener.IndexOf("你先開口")]); // no reply block in the opener

        var answer = MemberLifeChat.ChatPrompt(team[1], team[0], team, "小明", reply: "今天要部署嗎？", now: daytime);
        Assert.Contains("今天要部署嗎？", answer);
        Assert.Contains("簡短接一句", answer);

        // A very long reply is clamped, not passed whole.
        var longAnswer = MemberLifeChat.ChatPrompt(team[1], team[0], team, "小明", reply: new string('字', 3_000), now: daytime);
        Assert.Contains("字", longAnswer);
        Assert.DoesNotContain(new string('字', 1_500), longAnswer);
    }

    [Fact]
    public void OpeningPromptMentionsYesterdayOnlyFromRealEntries()
    {
        var room = new TeamRoom { Title = "茶歇室" };
        var team = new List<MemberCard> { new(MemberID.Claude, Executor.Claude, name: "Claude") };
        var now = At(2026, 11, 16, 8, 0);
        Assert.Contains("不要編造昨天的經歷", MemberLifeChat.OpeningPrompt(team[0], team, room, "小明", now));

        var yesterday = now.AddDays(-1);
        room.Entries.Add(new RoomEntry
        {
            Speaker = Speaker.User, DisplayName = "你",
            Body = "把登入頁的錯誤提示修好", Delivery = RoomDelivery.Completed,
            CreatedAt = yesterday,
        });
        room.Entries.Add(new RoomEntry
        {
            Speaker = Speaker.Assistant, Agent = MemberID.Claude, DisplayName = "Claude",
            Body = "修好了", Delivery = RoomDelivery.Completed, CreatedAt = yesterday,
        });
        room.Entries.Add(new RoomEntry
        {
            Speaker = Speaker.User, DisplayName = "你",
            Body = "今天早上的訊息不算昨天", Delivery = RoomDelivery.Completed,
            CreatedAt = now,
        });
        var prompt = MemberLifeChat.OpeningPrompt(team[0], team, room, "小明", now);
        Assert.Contains("把登入頁的錯誤提示修好", prompt);
        Assert.Contains("修好了", prompt);
        Assert.DoesNotContain("今天早上的訊息不算昨天", prompt); // today's entries are not "yesterday"
    }
}
