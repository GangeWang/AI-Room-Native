using AIROOMCore;

namespace AIROOMApp.ViewModels;

/// <summary>
/// The 茶歇室 (lounge) engine, ported from the macOS WorkbenchModel's room
/// section: one member at a time until the chat winds down, the meeting round
/// (and hand raising) ends, the human stops it, or a reply cannot be confirmed.
/// </summary>
public sealed partial class WorkbenchModel
{
    Guid? selectedRoomID;
    Guid? roomRunID;
    volatile bool roomRunning;
    volatile bool cancelRoomQueue;
    (Guid Room, Guid Entry)? loungePlaceholder;
    string? loungeActivity;
    Guid? answeringQuestionID;
    string roomDraft = "";
    MemberID? roomTarget;
    int roomsVersion;

    /// <summary>Raised on every room mutation; pages re-read the room state.</summary>
    public int RoomsVersion => roomsVersion;
    public Guid? AnsweringQuestionID => answeringQuestionID;
    public MemberID? RoomTarget => roomTarget;
    public string RoomDraft
    {
        get => roomDraft;
        set { roomDraft = value; OnPropertyChanged(nameof(RoomDraft)); }
    }

    /// <summary>The lounge topics, newest first.</summary>
    public List<TeamRoom> LoungeRooms()
    {
        lock (Archive) return Archive.Rooms?.Where(room => room.IsLounge).ToList() ?? [];
    }

    public TeamRoom? ActiveRoom()
    {
        lock (Archive)
        {
            return selectedRoomID is { } id ? Archive.Rooms?.FirstOrDefault(room => room.Id == id) : null;
        }
    }

    /// <summary>Entries of the active room, read as one consistent snapshot.</summary>
    public List<RoomEntry> RoomEntries()
    {
        lock (Archive) return ActiveRoom()?.Entries.ToList() ?? [];
    }

    public bool RoomRunning => roomRunning;
    public string? LoungeActivity => loungeActivity;

    void BumpRooms()
    {
        RunOnDispatcher(() =>
        {
            roomsVersion++;
            OnPropertyChanged(nameof(RoomsVersion));
        });
    }

    void PersistRooms()
    {
        Persist();
        BumpRooms();
    }

    /// <summary>New topic. By default the previous topic's summary is carried over
    /// so members need not start from scratch.</summary>
    public void CreateRoom(bool carryOver = true)
    {
        if (!StorageAvailable || roomRunning) return;
        TeamRoom? previous;
        lock (Archive)
        {
            previous = ActiveRoom();
            var count = (Archive.Rooms?.Where(room => room.IsLounge).Count() ?? 0) + 1;
            var room = new TeamRoom
            {
                Title = $"討論室 {count}",
                Participants = previous?.Participants ?? Archive.Members.Where(m => m.Enabled).Select(m => m.Agent).ToList(),
                Lounge = new LoungeState
                {
                    Style = previous?.Lounge.Style ?? LoungeStyle.Chat,
                    HandRaise = previous?.Lounge.HandRaise ?? true,
                    HostPick = previous?.Lounge.HostPick ?? false,
                },
            };
            Archive.Rooms ??= [];
            Archive.Rooms.Insert(0, room);
            selectedRoomID = room.Id;
            roomDraft = "";
            roomTarget = null;
            answeringQuestionID = null;
        }
        PersistRooms();
        if (!carryOver || previous is null
            || !previous.Entries.Any(entry => entry.Speaker != Speaker.System && entry.Delivery == RoomDelivery.Completed))
        {
            return;
        }
        var lastCompleted = previous.Entries.LastOrDefault(entry => entry.Delivery == RoomDelivery.Completed);
        if (previous.SummaryThroughEntryID == lastCompleted?.Id && previous.Summary.Length > 0)
        {
            SetCarriedSummary(previous.Id, previous.Summary);
        }
        else
        {
            SummarizeRoom(previous.Id, carryInto: selectedRoomID);
        }
    }

    void SetCarriedSummary(Guid roomID, string summary)
    {
        lock (Archive)
        {
            var room = FindRoom(roomID);
            if (room is null) return;
            room.CarriedSummary = summary.Length > 5_000 ? summary[..5_000] : summary;
            room.Entries.Add(new RoomEntry
            {
                Speaker = Speaker.System, DisplayName = "AIROOM",
                Body = "已帶入上一個話題的摘要，成員不用從頭了解。",
            });
        }
        PersistRooms();
    }

    public void SelectRoom(Guid id)
    {
        if (roomRunning) return;
        lock (Archive)
        {
            if (Archive.Rooms?.Any(room => room.Id == id) != true) return;
            selectedRoomID = id;
            roomDraft = "";
            roomTarget = null;
            answeringQuestionID = null;
        }
        BumpRooms();
    }

    /// <summary>The member rows in seat order: enabled cards among the participants.</summary>
    public List<MemberCard> PresentMembers(TeamRoom room)
    {
        lock (Archive)
        {
            return Archive.Members
                .Where(member => member.Enabled && room.Participants.Contains(member.Agent))
                .OrderBy(member => room.Participants.IndexOf(member.Agent))
                .ToList();
        }
    }

    public void SendToRoom()
    {
        var room = ActiveRoom();
        if (!StorageAvailable || room is null) return;
        // A chat accepts interjections at any time; a meeting round must finish first.
        if (roomRunning && room.Lounge.Style != LoungeStyle.Chat)
        {
            StatusMessage = "會議正在進行，請等這一輪結束或按停止。";
            return;
        }
        var question = RoomDraft.Trim();
        if (question.Length == 0 || question.Length > 8_000)
        {
            StatusMessage = "共用對話室每則訊息需介於 1 至 8,000 字。";
            return;
        }
        try { PrivacyGuard.ValidatePrompt(question); }
        catch (Exception error) { StatusMessage = error.Message; return; }
        RoomDraft = "";
        var answering = answeringQuestionID;
        answeringQuestionID = null;
        DispatchRoomMessage(room.Id, question, roomTarget, "你", answering);
    }

    void DispatchRoomMessage(Guid roomID, string question, MemberID? target, string displayName, Guid? answering = null)
    {
        List<MemberID> mentions;
        List<MemberID> present;
        Guid entryID;
        lock (Archive)
        {
            var room = FindRoom(roomID);
            if (room is null) return;
            var members = PresentMembers(room);
            if (members.Count == 0) { StatusMessage = "請先啟用至少一位成員"; return; }
            entryID = Guid.NewGuid();
            room.Entries.Add(new RoomEntry { Speaker = Speaker.User, DisplayName = displayName, Body = question });
            room.UpdatedAt = DateTimeOffset.Now;
            mentions = target is { } picked ? [picked] : LoungeEngine.Mentions(question, members);
            present = members.Select(member => member.Agent).ToList();
            LoungeEngine.HumanSpoke(room.Lounge, mentions, present, entryID, answering);
        }
        PersistRooms();
        // A running chat picks the new message up on its next turn.
        if (!roomRunning) StartLounge(roomID);
    }

    void StartLounge(Guid roomID)
    {
        roomRunning = true;
        cancelRoomQueue = false;
        BumpRooms();
        _ = Task.Run(() => RunLounge(roomID));
    }

    async Task RunLounge(Guid roomID)
    {
        try
        {
            while (!cancelRoomQueue)
            {
                TeamRoom? room;
                List<MemberCard> members;
                LoungeState state;
                lock (Archive)
                {
                    room = FindRoom(roomID);
                    if (room is null) break;
                    members = PresentMembers(room);
                    state = room.Lounge;
                    if (state.Phase is not (LoungePhase.Talking or LoungePhase.Closing)) break;
                    if (state.Style == LoungeStyle.Chat && state.Phase == LoungePhase.Talking
                        && state.Calls >= LoungeEngine.ChatBudget)
                    {
                        room.Lounge.Phase = LoungePhase.Paused;
                        room.Lounge.Reason = "這段聊天已經聊了一陣子，先停下來等你。說句話或按「繼續聊」就會接著聊。";
                        break;
                    }
                }
                var present = members.Select(member => member.Agent).ToList();
                var preferences = members.ToDictionary(member => member.Agent, member => member.Talkativeness);
                var speaker = LoungeEngine.NextSpeaker(state, present, preferences);
                if (speaker is not { } who)
                {
                    if (state.Style == LoungeStyle.Meeting && state.Phase == LoungePhase.Talking)
                    {
                        if (await RaiseHands(roomID)) continue;
                        break;
                    }
                    lock (Archive) { if (FindRoom(roomID) is { } settled) settled.Lounge.Phase = LoungePhase.Idle; }
                    break;
                }
                var member = members.First(card => card.Agent == who);
                var runID = Guid.NewGuid();
                var entryID = Guid.NewGuid();
                roomRunID = runID;
                // Everything up to here is what this member's CLI conversation will have seen.
                Guid? seenThrough;
                string prompt, sentPrompt;
                string? resumeID, fingerprint;
                lock (Archive)
                {
                    var current = FindRoom(roomID)!;
                    seenThrough = current.Entries.LastOrDefault()?.Id;
                    current.Entries.Add(new RoomEntry
                    {
                        Id = entryID, Speaker = Speaker.Assistant, Agent = who,
                        DisplayName = member.Name, Body = "正在想……", Delivery = RoomDelivery.InProgress,
                    });
                    loungePlaceholder = (roomID, entryID);
                    prompt = LoungeEngine.TurnPrompt(current, member, members, state,
                        Archive.Settings.UserName, Archive.Settings.Language);
                    var session = current.Sessions.GetValueOrDefault(who.RawValue);
                    resumeID = session is not null && current.Entries.Any(entry => entry.Id == session.Through)
                        ? session.Id : null;
                    var profile = member.Profile(members, Archive.Settings.UserName);
                    fingerprint = MemberLife.Fingerprint(member.Persona, profile);
                    sentPrompt = resumeID is not null
                        ? LoungeEngine.TurnDelta(current, member, state, session.Through,
                            Archive.Settings.UserName, Archive.Settings.Language)
                        : prompt;
                }
                PersistRooms();
                string text;
                try
                {
                    var roomFolder = WorkspaceFolderForRoom(roomID, who);
                    var settings = CloneSettingsWithWorkspace(roomFolder);
                    var result = await runner.RunDetailedAsync(runID, who, sentPrompt, settings,
                        fallbackWorkspace: roomFolder,
                        access: RoomReadFolder(roomID).Length == 0 ? ExecutionAccess.ReadOnly : ExecutionAccess.ReadFiles,
                        options: new CLIRunOptions("", "", [], new SessionPlan(resumeID, prompt)),
                        onUpdate: _ => { });
                    text = result.Text;
                    lock (Archive)
                    {
                        if (FindRoom(roomID) is { } finished && result.SessionID is { } sessionID && seenThrough is { } through)
                        {
                            finished.Sessions[who.RawValue] = new RoomSession(sessionID, through, fingerprint);
                        }
                    }
                    if (cancelRoomQueue)
                    {
                        FinishRoomReply(roomID, entryID, "已停止；這則回覆沒有轉交。", RoomDelivery.Interrupted);
                        break;
                    }
                }
                catch (Exception error)
                {
                    lock (Archive)
                    {
                        if (FindRoom(roomID) is { } failed)
                        {
                            failed.Sessions.Remove(who.RawValue); // unconfirmed: start fresh next time
                        }
                    }
                    if (cancelRoomQueue) break; // Stop already settled the bubble.
                    FinishRoomReply(roomID, entryID, $"結果待核對: {error.Message}", RoomDelivery.Failed);
                    lock (Archive)
                    {
                        if (FindRoom(roomID) is { } pausedRoom)
                        {
                            pausedRoom.Lounge.Phase = LoungePhase.Paused;
                            pausedRoom.Lounge.Reason = $"{member.Name} 這次沒有完成回覆，已暫停，沒有自動重送。檢查連線或額度後可按「繼續」。";
                        }
                    }
                    PersistRooms();
                    break;
                }
                LoungeReply reply;
                int questionsBefore;
                lock (Archive)
                {
                    var current = FindRoom(roomID);
                    if (current is null) break;
                    reply = LoungeEngine.ParseReply(text);
                    questionsBefore = current.Lounge.PendingQuestions.Count;
                    var outcome = LoungeEngine.Apply(reply, who, present, current.Lounge, entryID,
                        MemberNames());
                    switch (outcome.Kind)
                    {
                        case LoungeEngine.Outcome.Show:
                            FinishRoomReply(roomID, entryID, outcome.Body, RoomDelivery.Completed);
                            break;
                        case LoungeEngine.Outcome.Reaction:
                            FinishRoomReply(roomID, entryID, outcome.Body, RoomDelivery.Completed);
                            var entryIndex = current.Entries.FindIndex(entry => entry.Id == entryID);
                            if (entryIndex >= 0) current.Entries[entryIndex].IsReaction = true;
                            break;
                        case LoungeEngine.Outcome.Silent:
                            current.Entries.RemoveAll(entry => entry.Id == entryID);
                            break;
                        case LoungeEngine.Outcome.Ended:
                            if (reply.Body.Length == 0) current.Entries.RemoveAll(entry => entry.Id == entryID);
                            else FinishRoomReply(roomID, entryID, reply.Body, RoomDelivery.Completed);
                            current.Entries.Add(new RoomEntry
                            {
                                Speaker = Speaker.System, DisplayName = "AIROOM", Body = outcome.Body,
                            });
                            break;
                    }
                    var askedNow = current.Lounge.PendingQuestions.Count > questionsBefore;
                    if (askedNow) RunOnDispatcher(() => Notify($"{member.Name} 問你", reply.Body));
                }
                PersistRooms();
                PersistRooms();
            }
            NotifyIfWaiting(roomID);
            if (cancelRoomQueue)
            {
                lock (Archive)
                {
                    if (FindRoom(roomID) is { } stopped
                        && stopped.Lounge.Phase is LoungePhase.Talking or LoungePhase.Closing or LoungePhase.Raising)
                    {
                        stopped.Lounge.Phase = LoungePhase.Paused;
                        stopped.Lounge.Reason = "你已停止討論。";
                    }
                }
                PersistRooms();
            }
        }
        finally
        {
            roomRunID = null;
            roomRunning = false;
            loungePlaceholder = null;
            PersistRooms();
        }
    }

    /// <summary>The room's own read folder (project rooms) or an empty string.</summary>
    string RoomReadFolder(Guid roomID)
    {
        lock (Archive) return FindRoom(roomID)?.ReadFolder ?? "";
    }

    string WorkspaceFolderForRoom(Guid roomID, MemberID speaker)
    {
        var readFolder = RoomReadFolder(roomID);
        if (readFolder.Length > 0) return readFolder;
        // Lounge turns are read-only and touch no files, but a CLI still needs
        // a working directory that exists: use the member's own home folder.
        return Desk?.PersonalFolder(speaker) ?? "";
    }

    Dictionary<string, string> MemberNames()
    {
        lock (Archive) return Archive.Members.ToDictionary(member => member.Agent.RawValue, member => member.Name);
    }

    TeamRoom? FindRoom(Guid id)
    {
        return Archive.Rooms?.FirstOrDefault(room => room.Id == id);
    }

    /// <summary>Meeting hand raising. Returns true when someone was chosen to add something.</summary>
    async Task<bool> RaiseHands(Guid roomID)
    {
        LoungeState stateBefore;
        List<MemberCard> members;
        lock (Archive)
        {
            var room = FindRoom(roomID);
            if (room is null) return false;
            stateBefore = room.Lounge;
            members = PresentMembers(room);
            var candidates = members.Where(member => !stateBefore.Supplemented.Contains(member.Agent)).ToList();
            if (!stateBefore.HandRaise || stateBefore.MentionedOnly || candidates.Count == 0)
            {
                room.Lounge.Phase = LoungePhase.WaitingUser;
                PersistRooms();
                return false;
            }
            room.Lounge.Phase = LoungePhase.Raising;
            room.Lounge.Hands = [];
            PersistRooms();
        }
        var votes = new Dictionary<string, HandVote>();
        foreach (var member in members.Where(card => !stateBefore.Supplemented.Contains(card.Agent)))
        {
            if (cancelRoomQueue) return false;
            string? text = null;
            Guid runID;
            lock (Archive)
            {
                var room = FindRoom(roomID);
                if (room is null) return false;
                runID = Guid.NewGuid();
                roomRunID = runID;
            }
            try
            {
                var prompt = LoungeEngine.HandPrompt(FindRoom(roomID)!, member, FindRoom(roomID)!.Lounge,
                    Archive.Settings.Language);
                text = await runner.RunAsync(runID, member.Agent, prompt, Archive.Settings);
            }
            catch { /* A failed or unreadable bid counts as "no hand", never as a guess. */ }
            votes[member.Agent.RawValue] = text is null ? new HandVote(0, "沒有回覆") : LoungeEngine.ParseHand(text) ?? new HandVote(0, "沒有回覆");
            lock (Archive)
            {
                if (FindRoom(roomID) is { } room) room.Lounge.Hands = new Dictionary<string, HandVote>(votes);
            }
            BumpRooms();
        }
        bool talking;
        lock (Archive)
        {
            var room = FindRoom(roomID);
            if (room is null || cancelRoomQueue) return false;
            var lounge = room.Lounge;
            var raised = votes.Any(pair => pair.Value.Want > 0);
            if (!raised)
            {
                lounge.Phase = LoungePhase.WaitingUser;
                lounge.Reason = "這一輪大家都沒有要補充的，等你說話。";
            }
            else if (lounge.HostPick)
            {
                lounge.Phase = LoungePhase.AwaitingHost;
                lounge.Reason = "有人舉手了，請點名誰補充。";
            }
            else if (LoungeEngine.ChooseHand(votes, lounge, members.Select(card => card.Agent).ToList()) is { } chosen)
            {
                lounge.Supplemented.Add(chosen);
                lounge.MeetingPending = [chosen];
                lounge.Phase = LoungePhase.Talking;
            }
            else
            {
                lounge.Phase = LoungePhase.WaitingUser;
            }
            talking = lounge.Phase == LoungePhase.Talking;
        }
        PersistRooms();
        return talking;
    }

    // MARK: Lounge controls

    public void SetLoungeStyle(LoungeStyle style)
    {
        lock (Archive)
        {
            var room = ActiveRoom();
            if (roomRunning || room is null || room.Lounge.Style == style) return;
            var lounge = new LoungeState { Style = style, HandRaise = room.Lounge.HandRaise, HostPick = room.Lounge.HostPick };
            lounge.Questions = room.Lounge.Questions;
            room.Lounge = lounge;
            room.Sessions = []; // the rules changed: members start new CLI conversations
        }
        PersistRooms();
    }

    public void SetLoungeOption(bool? handRaise = null, bool? hostPick = null)
    {
        lock (Archive)
        {
            var room = ActiveRoom();
            if (room is null) return;
            if (handRaise is { } raise) room.Lounge.HandRaise = raise;
            if (hostPick is { } pick) room.Lounge.HostPick = pick;
        }
        PersistRooms();
    }

    /// <summary>Resume a paused chat or meeting without a new human message.</summary>
    public void ContinueLounge()
    {
        lock (Archive)
        {
            var room = ActiveRoom();
            if (roomRunning || room is null || room.Lounge.Phase != LoungePhase.Paused) return;
            var lounge = room.Lounge;
            lounge.Phase = lounge.ProposalFrom is not null ? LoungePhase.Closing : LoungePhase.Talking;
            lounge.Calls = 0;
            lounge.Reason = null;
            if (lounge.Style == LoungeStyle.Meeting && lounge.MeetingPending.Count == 0) lounge.Phase = LoungePhase.WaitingUser;
        }
        PersistRooms();
        if (ActiveRoom()?.Lounge.Phase is LoungePhase.Talking or LoungePhase.Closing)
        {
            StartLounge(ActiveRoom()!.Id);
        }
    }

    /// <summary>Host picks who adds something after hands went up (null = nobody).</summary>
    public void PickHand(MemberID? agent)
    {
        bool start = false;
        Guid roomID = Guid.Empty;
        lock (Archive)
        {
            var room = ActiveRoom();
            if (roomRunning || room is null || room.Lounge.Phase != LoungePhase.AwaitingHost) return;
            roomID = room.Id;
            if (agent is { } pick)
            {
                room.Lounge.Supplemented.Add(pick);
                room.Lounge.MeetingPending = [pick];
                room.Lounge.Phase = LoungePhase.Talking;
                room.Lounge.Reason = null;
                start = true;
            }
            else
            {
                room.Lounge.Phase = LoungePhase.WaitingUser;
                room.Lounge.Reason = null;
            }
        }
        PersistRooms();
        if (start) StartLounge(roomID);
    }

    /// <summary>Which member the next message is directed at (null = all present).</summary>
    public void SetRoomTarget(MemberID? agent)
    {
        roomTarget = agent;
        OnPropertyChanged(nameof(RoomTarget));
    }

    public void AnswerQuestion(LoungeQuestion question)
    {
        answeringQuestionID = question.Id;
        string name;
        lock (Archive) name = Archive.Members.FirstOrDefault(card => card.Agent == question.From)?.Name ?? question.From.RawValue;
        if (!RoomDraft.StartsWith("@")) RoomDraft = $"@{name} " + RoomDraft;
    }

    public void DismissQuestion(Guid id)
    {
        lock (Archive)
        {
            var room = ActiveRoom();
            var question = room?.Lounge.Questions.Find(item => item.Id == id);
            if (question is null) return;
            question.Answered = true;
            if (answeringQuestionID == id) answeringQuestionID = null;
        }
        PersistRooms();
    }

    /// <summary>Ends the meeting: one member writes conclusions, disagreements and next steps.</summary>
    public void WriteMinutes(MemberID agent)
    {
        if (!StorageAvailable || roomRunning) return;
        Guid roomID, entryID, runID;
        string prompt;
        lock (Archive)
        {
            var room = ActiveRoom();
            if (room is null) return;
            var member = PresentMembers(room).FirstOrDefault(card => card.Agent == agent);
            if (member is null) return;
            roomID = room.Id;
            var attendees = PresentMembers(room);
            prompt = LoungeEngine.MinutesPrompt(room, room.Lounge, attendees,
                Archive.Settings.UserName, Archive.Settings.Language);
            room.Lounge.Phase = LoungePhase.Summarizing;
            room.Lounge.Reason = null;
            entryID = Guid.NewGuid();
            runID = Guid.NewGuid();
            room.Entries.Add(new RoomEntry
            {
                Id = entryID, Speaker = Speaker.Assistant, Agent = agent,
                DisplayName = member.Name, Body = "正在整理會議紀要……",
                Delivery = RoomDelivery.InProgress, IsMinutes = true,
            });
            roomRunning = true;
            cancelRoomQueue = false;
            roomRunID = runID;
        }
        PersistRooms();
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await runner.RunAsync(runID, agent, prompt, Archive.Settings,
                    onUpdate: output => RunOnDispatcher(() => UpdateRoomEntryText(roomID, entryID, output)));
                FinishRoomReply(roomID, entryID, text, RoomDelivery.Completed);
                lock (Archive)
                {
                    if (FindRoom(roomID) is { } room)
                    {
                        room.Lounge.Phase = LoungePhase.Idle;
                        room.Lounge.MeetingStartEntryID = null;
                        room.Lounge.Reason = "會議紀要已整理，會議結束。";
                    }
                }
                Notify("會議紀要已整理", text.Length > 200 ? text[..200] : text);
            }
            catch (Exception error)
            {
                FinishRoomReply(roomID, entryID, $"結果待核對: {error.Message}",
                    cancelRoomQueue ? RoomDelivery.Interrupted : RoomDelivery.Failed);
                lock (Archive)
                {
                    if (FindRoom(roomID) is { } room) room.Lounge.Phase = LoungePhase.WaitingUser;
                }
            }
            finally
            {
                roomRunID = null;
                roomRunning = false;
                PersistRooms();
            }
        });
    }

    /// <summary>Compress context: a member summarises the topic; later prompts use
    /// the summary plus newer messages. The original messages are kept.</summary>
    public void CompressRoom()
    {
        var room = ActiveRoom();
        if (room is null) return;
        SummarizeRoom(room.Id, carryInto: null);
    }

    void SummarizeRoom(Guid roomID, Guid? carryInto)
    {
        if (!StorageAvailable || roomRunning) return;
        string prompt;
        Guid last, runID;
        lock (Archive)
        {
            var room = FindRoom(roomID);
            if (room is null) return;
            var member = Archive.Members.FirstOrDefault(card => card.Enabled && room.Participants.Contains(card.Agent))
                ?? Archive.Members.FirstOrDefault(card => card.Enabled);
            var lastCompleted = room.Entries.LastOrDefault(entry => entry.Delivery == RoomDelivery.Completed && entry.Speaker != Speaker.System);
            if (member is null || lastCompleted is null) return;
            prompt = LoungeEngine.SummaryPrompt(room, Archive.Settings.Language);
            last = lastCompleted.Id;
            runID = Guid.NewGuid();
            roomRunning = true;
            cancelRoomQueue = false;
            roomRunID = runID;
            loungeActivity = carryInto is null ? "正在壓縮上下文……" : "正在整理上一個話題的摘要……";
        }
        PersistRooms();
        _ = Task.Run(async () =>
        {
            try
            {
                MemberID summarizer;
                lock (Archive)
                {
                    var room = FindRoom(roomID);
                    summarizer = room?.Participants.FirstOrDefault(agent =>
                            Archive.Members.Any(card => card.Agent == agent && card.Enabled))
                        ?? Archive.Members.First(card => card.Enabled).Agent;
                }
                var summary = await runner.RunAsync(runID, summarizer, prompt, Archive.Settings);
                if (!cancelRoomQueue)
                {
                    lock (Archive)
                    {
                        var room = FindRoom(roomID);
                        if (room is not null)
                        {
                            room.Summary = summary.Length > 5_000 ? summary[..5_000] : summary;
                            room.SummaryThroughEntryID = last;
                        }
                    }
                    if (carryInto is { } target) SetCarriedSummary(target, summary);
                    else
                    {
                        lock (Archive)
                        {
                            FindRoom(roomID)?.Entries.Add(new RoomEntry
                            {
                                Speaker = Speaker.System, DisplayName = "AIROOM",
                                Body = "已壓縮上下文：之後成員會從摘要繼續，原本的訊息都保留。",
                            });
                        }
                    }
                }
            }
            catch (Exception error)
            {
                StatusMessage = $"摘要沒有完成: {error.Message}";
            }
            finally
            {
                roomRunID = null;
                roomRunning = false;
                loungeActivity = null;
                PersistRooms();
            }
        });
    }

    public void ClearRoom()
    {
        lock (Archive)
        {
            var room = ActiveRoom();
            if (roomRunning || room is null) return;
            var lounge = new LoungeState { Style = room.Lounge.Style, HandRaise = room.Lounge.HandRaise, HostPick = room.Lounge.HostPick };
            room.Entries = [];
            room.Lounge = lounge;
            room.Summary = "";
            room.SummaryThroughEntryID = null;
            room.CarriedSummary = "";
            room.Sessions = [];
            room.UpdatedAt = DateTimeOffset.Now;
            answeringQuestionID = null;
        }
        PersistRooms();
    }

    public string RoomMarkdown()
    {
        var room = ActiveRoom();
        return room is null ? "" : LoungeEngine.Markdown(room);
    }

    public void StopRoom()
    {
        cancelRoomQueue = true;
        if (roomRunID is { } id) runner.Cancel(id);
        // Don't leave a bubble spinning while the CLI winds down.
        if (loungePlaceholder is { } pending)
        {
            FinishRoomReply(pending.Room, pending.Entry, "已停止，這則回覆沒有轉交。", RoomDelivery.Interrupted);
            loungePlaceholder = null;
            PersistRooms();
        }
    }

    public void SetRoomParticipant(MemberID agent, bool included)
    {
        lock (Archive)
        {
            var room = ActiveRoom();
            if (roomRunning || room is null) return;
            room.Participants.RemoveAll(id => id == agent);
            if (included) room.Participants.Add(agent);
            if (!included && roomTarget == agent) roomTarget = null;
        }
        PersistRooms();
    }

    void UpdateRoomEntryText(Guid roomID, Guid entryID, string text)
    {
        lock (Archive)
        {
            var room = FindRoom(roomID);
            var index = room?.Entries.FindIndex(entry => entry.Id == entryID) ?? -1;
            if (room is null || index < 0 || room.Entries[index].Delivery != RoomDelivery.InProgress) return;
            var old = room.Entries[index].Body;
            if (old == "執行中……" || text.Length >= old.Length)
            {
                room.Entries[index].Body = text;
                room.UpdatedAt = DateTimeOffset.Now;
            }
        }
        BumpRooms();
    }

    void FinishRoomReply(Guid roomID, Guid entryID, string text, RoomDelivery delivery)
    {
        lock (Archive)
        {
            var room = FindRoom(roomID);
            var index = room?.Entries.FindIndex(entry => entry.Id == entryID) ?? -1;
            if (room is null || index < 0) return;
            room.Entries[index].Body = text;
            room.Entries[index].Delivery = delivery;
            room.UpdatedAt = DateTimeOffset.Now;
        }
        BumpRooms();
    }

    void NotifyIfWaiting(Guid roomID)
    {
        string title, body;
        lock (Archive)
        {
            var room = FindRoom(roomID);
            if (room is null || room.Lounge.Phase
                is not (LoungePhase.WaitingUser or LoungePhase.AwaitingHost or LoungePhase.Paused)) return;
            title = "茶歇室在等你";
            body = room.Lounge.Reason ?? room.Title;
        }
        Notify(title, body);
    }
}
