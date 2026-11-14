using AIROOMCore;

namespace AIROOMApp.ViewModels;

/// <summary>
/// Scheduled activities, idle member chat and the daily opening — everything
/// the app does on its own clock. Triggers are checked only while the app runs
/// (every 15 s); a trigger missed past the grace period is skipped, never
/// replayed. Ported from the macOS WorkbenchModel's automation section.
/// </summary>
public sealed partial class WorkbenchModel
{
    readonly object automationGate = new();
    System.Timers.Timer? automationTimer;
    int automationVersion;

    /// <summary>Raised when schedules, runs or idle-chat settings change.</summary>
    public int AutomationVersion => automationVersion;

    void BumpAutomation()
    {
        RunOnDispatcher(() =>
        {
            automationVersion++;
            OnPropertyChanged(nameof(AutomationVersion));
        });
    }

    public List<ScheduledActivity> SchedulesSnapshot()
    {
        lock (Archive) return Archive.Schedules is { } schedules ? schedules : [];
    }

    public List<ScheduleRun> ScheduleRunsSnapshot()
    {
        lock (Archive) return Archive.ScheduleRuns is { } runs ? runs : [];
    }

    public AutoChatSettings AutoChatSnapshot()
    {
        lock (Archive) return Archive.AutoChat ?? new AutoChatSettings();
    }

    public DailyOpeningSettings DailyOpeningSnapshot()
    {
        lock (Archive) return Archive.DailyOpening ?? new DailyOpeningSettings();
    }

    public bool SchedulesPaused
    {
        get
        {
            lock (Archive) return Archive.SchedulesPaused ?? false;
        }
    }

    /// <summary>The 15 s tick: quota waits, schedule triggers and idle chat.
    /// Replaces the earlier quota-only timer, which ran the same wait check.</summary>
    void StartAutomationTimer()
    {
        lock (automationGate)
        {
            if (automationTimer is not null) return;
            automationTimer = new System.Timers.Timer(15_000) { AutoReset = true };
            automationTimer.Elapsed += (_, _) => AutomationTick();
            automationTimer.Start();
        }
    }

    // MARK: Schedules

    /// <summary>Adds a schedule, or replaces the one with `id`. Daily fires at hour:minute,
    /// once at `onceAt`, interval every `intervalMinutes` starting that long from now.</summary>
    public void SaveSchedule(Guid? id, string name, string text, ScheduleKind kind, int hour, int minute,
        DateTimeOffset? onceAt, int intervalMinutes, ScheduleAction action, MemberID? target)
    {
        var label = name.Trim();
        var message = text.Trim();
        if (!StorageAvailable || hour is < 0 or > 23 || minute is < 0 or > 59
            || label.Length == 0 || message.Length == 0 || message.Length > 8_000)
        {
            StatusMessage = "請填寫有效的排程名稱、時間與內容（最多 8000 字）。";
            return;
        }
        if (kind == ScheduleKind.Once && (onceAt ?? DateTimeOffset.MinValue) <= DateTimeOffset.Now)
        {
            StatusMessage = "一次性排程要選未來的時間。";
            return;
        }
        try { PrivacyGuard.ValidatePrompt(message); }
        catch (Exception error)
        {
            StatusMessage = error.Message;
            return;
        }
        lock (Archive)
        {
            var index = id is { } existingId
                ? (Archive.Schedules ?? []).FindIndex(item => item.Id == existingId) : -1;
            var item = new ScheduledActivity(
                label, action, message, hour, minute,
                enabled: index >= 0 && Archive.Schedules![index].Enabled,
                roomID: index >= 0 ? Archive.Schedules![index].RoomID : ActiveRoom()?.Id,
                target: target,
                kind: kind,
                onceAt: kind == ScheduleKind.Once ? onceAt : null,
                intervalMinutes: intervalMinutes)
            {
                Id = index >= 0 ? Archive.Schedules![index].Id : Guid.NewGuid(),
            };
            if (kind == ScheduleKind.Interval) item.NextAt = DateTimeOffset.Now.AddMinutes(item.IntervalMinutes);
            if (kind == ScheduleKind.Once) item.Enabled = true;
            if (index >= 0) Archive.Schedules![index] = item;
            else (Archive.Schedules ??= []).Add(item);
        }
        Persist();
        BumpAutomation();
    }

    /// <summary>Records a trigger and moves the schedule on.</summary>
    void FinishSchedule(ScheduledActivity item, ScheduleRun.Outcome outcome, string reason, DateTimeOffset now)
    {
        lock (Archive)
        {
            (Archive.ScheduleRuns ??= []).Add(new ScheduleRun(item.Id, item.Name, outcome, reason)
            {
                At = now,
            });
            if (Archive.ScheduleRuns.Count > 40)
            {
                Archive.ScheduleRuns = Archive.ScheduleRuns.GetRange(Archive.ScheduleRuns.Count - 40, 40);
            }
            if (Archive.Schedules is { } schedules)
            {
                var index = schedules.FindIndex(candidate => candidate.Id == item.Id);
                if (index >= 0) LocalAutomation.Advance(schedules[index], now);
            }
        }
        Persist();
        BumpAutomation();
    }

    public void SetSchedulesPaused(bool paused)
    {
        lock (Archive) Archive.SchedulesPaused = paused;
        Persist();
        BumpAutomation();
    }

    public void SetScheduleEnabled(Guid id, bool enabled)
    {
        lock (Archive)
        {
            var item = (Archive.Schedules ?? []).FirstOrDefault(candidate => candidate.Id == id);
            if (item is null) return;
            item.Enabled = enabled;
            // Switching an interval back on counts from now, so nothing missed
            // while off fires at once.
            if (enabled && item.Kind == ScheduleKind.Interval)
            {
                item.NextAt = DateTimeOffset.Now.AddMinutes(item.IntervalMinutes);
            }
        }
        Persist();
        BumpAutomation();
    }

    public void DeleteSchedule(Guid id)
    {
        lock (Archive) Archive.Schedules?.RemoveAll(item => item.Id == id);
        Persist();
        BumpAutomation();
    }

    public void ConfigureIdleChat(bool enabled, int intervalMinutes, int dailyLimit)
    {
        lock (Archive)
        {
            var rule = Archive.AutoChat ??= new AutoChatSettings();
            rule.Enabled = enabled;
            rule.RoomID = ActiveRoom()?.Id ?? rule.RoomID;
            rule.IntervalMinutes = Math.Min(1440, Math.Max(60, intervalMinutes));
            rule.DailyLimit = Math.Min(6, Math.Max(1, dailyLimit));
        }
        Persist();
        BumpAutomation();
    }

    public void ConfigureDailyOpening(bool enabled)
    {
        lock (Archive)
        {
            var opening = Archive.DailyOpening ??= new DailyOpeningSettings();
            opening.Enabled = enabled;
            opening.RoomID = ActiveRoom()?.Id ?? opening.RoomID;
        }
        Persist();
        BumpAutomation();
    }

    // MARK: The tick

    void AutomationTick()
    {
        if (!StorageAvailable) return;
        FlowQuotaTick();
        var now = DateTimeOffset.Now;
        List<(Guid RoomID, string Text, MemberID? Target)> roomDispatches = [];
        List<(string Title, string Body)> reminders = [];
        lock (Archive)
        {
            if (Archive.Schedules is not { } schedules) return;
            foreach (var item in schedules.ToList())
            {
                var due = LocalAutomation.Evaluate(item, now);
                if (due == LocalAutomation.Due.NotYet) continue;
                if (due == LocalAutomation.Due.Missed)
                {
                    FinishSchedule(item, ScheduleRun.Outcome.Skipped,
                        "約定的時間已錯過（App 關閉或電腦睡眠），這次跳過，不會補發。", now);
                    continue;
                }
                if (SchedulesPaused)
                {
                    FinishSchedule(item, ScheduleRun.Outcome.Skipped, "排程已全部暫停，這次跳過。", now);
                    continue;
                }
                if (item.Action == ScheduleAction.RoomMessage)
                {
                    var room = item.RoomID is { } roomID ? FindRoom(roomID) : null;
                    if (room is null)
                    {
                        FinishSchedule(item, ScheduleRun.Outcome.Failed, "找不到原本的聊天室，沒有送出。", now);
                        continue;
                    }
                    // A busy room keeps the trigger waiting; past the grace period it counts as missed.
                    if (roomRunning) continue;
                    roomDispatches.Add((room.Id, item.Text, item.Target));
                    FinishSchedule(item, ScheduleRun.Outcome.Done, $"已送到「{room.Title}」。", now);
                }
                else
                {
                    reminders.Add(($"排程提醒 · {item.Name}", item.Text));
                    FinishSchedule(item, ScheduleRun.Outcome.Done, "已提醒。", now);
                }
            }
        }
        foreach (var (roomID, text, target) in roomDispatches)
        {
            if (liveActive)
            {
                // During a live show a scheduled message becomes a viewer-like
                // prompt; it does not spawn a competing CLI process.
                lock (Archive)
                {
                    if (LiveConfig().RoomID == roomID && FindRoom(roomID) is { } live)
                    {
                        live.Entries.Add(new RoomEntry
                        {
                            Speaker = Speaker.User, DisplayName = "定時主持", Body = text,
                        });
                        live.UpdatedAt = DateTimeOffset.Now;
                    }
                }
                PersistRooms();
                continue;
            }
            DispatchRoomMessage(roomID, text, target, "定時任務");
        }
        foreach (var (title, body) in reminders)
        {
            StatusMessage = $"{title}：{body}";
            Notify(title, body);
        }
        AutoChatTick(now);
    }

    /// <summary>Occasional idle chat is distinct from a manually started, long-lived
    /// live show. It never resumes missed runs after app exit.</summary>
    void AutoChatTick(DateTimeOffset now)
    {
        AutoChatSettings rule;
        TeamRoom? room;
        lock (Archive)
        {
            rule = Archive.AutoChat ?? new AutoChatSettings();
            room = rule.RoomID is { } roomID ? FindRoom(roomID) : null;
            if (room is null) return;
        }
        var members = PresentMembers(room);
        var enabledMembers = room.Participants
            .Where(agent => members.Any(card => card.Enabled && card.Agent == agent)).ToList();
        var quotaOK = enabledMembers.All(agent =>
        {
            var quota = QuotaOf(agent);
            // unknown is not zero
            if (quota is not { } sample || sample.Status != QuotaStatus.Ready || !sample.IsFresh(now)) return true;
            return !sample.Windows.Any(window => window.Exhausted);
        });
        if (enabledMembers.Count < 2 || roomRunning || liveActive) return;
        if (!LocalAutomation.ShouldAutoChat(rule, now, roomExists: true, quotaAvailable: quotaOK)) return;
        lock (Archive)
        {
            var current = Archive.AutoChat ??= new AutoChatSettings();
            current.LastFiredAt = now;
            var key = LocalAutomation.DayKey(now);
            var cutoff = LocalAutomation.DayKey(now.AddDays(-7));
            current.FiredDays = current.FiredDays
                .Where(entry => string.CompareOrdinal(entry.Key, cutoff) >= 0)
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            if (!current.FiredDays.TryGetValue(key, out var fired)) fired = 0;
            current.FiredDays[key] = fired + 1;
        }
        Persist();
        _ = Task.Run(() => RunMemberChat(room.Id));
    }

    // MARK: The window opened (daily opening)

    /// <summary>The window was opened (or the app came forward): the first time each
    /// day, one member greets in the lounge.</summary>
    public void WindowOpened()
    {
        if (!StorageAvailable) return;
        bool due;
        lock (Archive)
        {
            var opening = Archive.DailyOpening ??= new DailyOpeningSettings();
            due = opening.Opened(DateTimeOffset.Now);
        }
        Persist();
        if (!due || roomRunning || liveActive) return;
        _ = Task.Run(() => RunDailyOpening());
    }

    /// <summary>The configured room, or the most recently used lounge room.</summary>
    TeamRoom? DailyLifeRoom(Guid? preferred)
    {
        lock (Archive)
        {
            var rooms = Archive.Rooms ?? [];
            if (preferred is { } wanted)
            {
                var found = rooms.FirstOrDefault(room => room.Id == wanted && room.IsLounge);
                if (found is not null) return found;
            }
            return rooms.Where(room => room.IsLounge)
                .OrderByDescending(room => room.UpdatedAt).FirstOrDefault();
        }
    }

    async Task RunDailyOpening()
    {
        var room = DailyLifeRoom(DailyOpeningSnapshot().RoomID);
        if (room is null) return;
        var present = PresentMembers(room);
        MemberID? agent;
        lock (Archive) agent = MemberLifeChat.NextGreeter(present.Select(card => card.Agent).ToList(),
            DailyOpeningSnapshot().LastAgent);
        var member = present.FirstOrDefault(card => card.Agent == agent);
        if (member is null) return;
        lock (Archive)
        {
            if (Archive.DailyOpening is { } opening) opening.LastAgent = member.Agent;
        }
        var prompt = MemberLifeChat.OpeningPrompt(member, Archive.Members, room, Archive.Settings.UserName,
            DateTimeOffset.Now);
        await PostLifeLine(room.Id, member, prompt);
        ResumeLoungeIfAsked(room.Id);
    }

    /// <summary>Two lines of small talk between two members, using their routines and relations.</summary>
    async Task RunMemberChat(Guid roomID)
    {
        if (roomRunning) return;
        var room = FindRoom(roomID);
        if (room is null) return;
        var pair = PresentMembers(room).OrderBy(_ => Guid.NewGuid()).Take(2).ToList();
        if (pair.Count < 2) return;
        var (first, second) = (pair[0], pair[1]);
        var opener = MemberLifeChat.ChatPrompt(first, second, Archive.Members, Archive.Settings.UserName);
        if (await PostLifeLine(roomID, first, opener) is not { } line || cancelRoomQueue) return;
        var answer = MemberLifeChat.ChatPrompt(second, first, Archive.Members, Archive.Settings.UserName,
            reply: line);
        await PostLifeLine(roomID, second, answer);
        ResumeLoungeIfAsked(roomID);
    }

    /// <summary>One stateless line in the room. Returns the text, or null when it failed
    /// or was stopped (a failed line is removed; nothing is retried).</summary>
    async Task<string?> PostLifeLine(Guid roomID, MemberCard member, string prompt)
    {
        if (roomRunning || FindRoom(roomID) is null) return null;
        var runID = Guid.NewGuid();
        var entryID = Guid.NewGuid();
        roomRunning = true;
        cancelRoomQueue = false;
        roomRunID = runID;
        lock (Archive)
        {
            if (FindRoom(roomID) is not { } room)
            {
                roomRunning = false;
                roomRunID = null;
                return null;
            }
            room.Entries.Add(new RoomEntry
            {
                Id = entryID, Speaker = Speaker.Assistant, Agent = member.Agent,
                DisplayName = member.Name, Body = "正在想……", Delivery = RoomDelivery.InProgress,
            });
            loungePlaceholder = (roomID, entryID);
        }
        PersistRooms();
        try
        {
            var home = Desk?.PersonalFolder(member.Agent) ?? "";
            var settings = CloneSettingsWithWorkspace(home.Length > 0 ? home : ".");
            var result = await runner.RunAsync(runID, member.Agent, prompt.Length > 30_000 ? prompt[..30_000] : prompt,
                settings, fallbackWorkspace: home, access: ExecutionAccess.ReadOnly);
            var text = result.Trim();
            if (cancelRoomQueue || text.Length == 0)
            {
                if (!cancelRoomQueue)
                {
                    lock (Archive)
                    {
                        if (FindRoom(roomID) is { } current)
                        {
                            current.Entries.RemoveAll(entry => entry.Id == entryID);
                        }
                    }
                    PersistRooms();
                }
                return null;
            }
            FinishRoomReply(roomID, entryID, text.Length > 2_000 ? text[..2_000] : text, RoomDelivery.Completed);
            // This member's continued conversation never sees its own lines; let it start afresh with the full room.
            lock (Archive)
            {
                if (FindRoom(roomID) is { } current) current.Sessions.Remove(member.Agent.RawValue);
            }
            return text;
        }
        catch (Exception error)
        {
            if (!cancelRoomQueue)
            {
                lock (Archive)
                {
                    if (FindRoom(roomID) is { } current)
                    {
                        current.Entries.RemoveAll(entry => entry.Id == entryID);
                    }
                }
                PersistRooms();
            }
            StatusMessage = $"{member.Name} 的日常發言沒有完成：{error.Message}";
            return null;
        }
        finally
        {
            roomRunID = null;
            roomRunning = false;
            loungePlaceholder = null;
            PersistRooms();
        }
    }

    /// <summary>Resume a paused lounge room after a daily line, matching the owner's
    /// 繼續聊 semantics for a specific room.</summary>
    void ResumeLoungeIfAsked(Guid roomID)
    {
        Guid? restart = null;
        lock (Archive)
        {
            if (FindRoom(roomID) is not { } room || room.Lounge.Phase != LoungePhase.Paused) return;
            var lounge = room.Lounge;
            lounge.Phase = lounge.ProposalFrom is not null ? LoungePhase.Closing : LoungePhase.Talking;
            lounge.Calls = 0;
            lounge.Reason = null;
            if (lounge.Style == LoungeStyle.Meeting && lounge.MeetingPending.Count == 0) lounge.Phase = LoungePhase.WaitingUser;
            if (lounge.Phase is LoungePhase.Talking or LoungePhase.Closing) restart = room.Id;
        }
        PersistRooms();
        if (restart is { } id && !roomRunning) StartLounge(id);
    }
}
