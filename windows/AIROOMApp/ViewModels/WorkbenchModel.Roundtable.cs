using AIROOMCore;

namespace AIROOMApp.ViewModels;

/// <summary>
/// The 圓桌 (roundtable): topics run through prepare → reveal (peer review +
/// issue list) → deliberation (a hidden meeting room on the lounge engine) →
/// adopt → minutes → confirm. Ported from the macOS WorkbenchModel's topics
/// section. 帶回專案 joins when project rooms are restored.
/// </summary>
public sealed partial class WorkbenchModel
{
    Guid? selectedRoundtableID;
    volatile bool roundtableRunning;
    volatile bool cancelRoundtableQueue;
    readonly HashSet<Guid> roundtableRunIDs = [];
    int roundtableVersion;

    /// <summary>Raised on every roundtable mutation.</summary>
    public int RoundtableVersion => roundtableVersion;
    public bool RoundtableRunning => roundtableRunning;

    public Guid? SelectedRoundtableID
    {
        get => selectedRoundtableID;
        set { selectedRoundtableID = value; BumpRoundtable(); }
    }

    void BumpRoundtable()
    {
        RunOnDispatcher(() =>
        {
            roundtableVersion++;
            OnPropertyChanged(nameof(RoundtableVersion));
        });
    }

    public List<RoundtableTopic> RoundtableTopics()
    {
        lock (Archive) return Archive.RoundtableTopics?.ToList() ?? [];
    }

    public RoundtableTopic? GetRoundtable(Guid id)
    {
        lock (Archive) return FindRoundtable(id);
    }

    RoundtableTopic? FindRoundtable(Guid id) => Archive.RoundtableTopics?.FirstOrDefault(topic => topic.Id == id);

    public void OpenRoundtable(string title, RoundtableKind kind, string material,
        IReadOnlyList<ChatAttachment> attachments, IReadOnlyList<MemberID> members, MemberID lead, bool peerReview)
    {
        if (!StorageAvailable || roundtableRunning) return;
        var text = material.Trim();
        if (text.Length == 0 && attachments.Count == 0) { StatusMessage = "請寫下材料或要求。"; return; }
        List<MemberID> chosen;
        lock (Archive) chosen = EnabledMemberIDs.Where(members.Contains).ToList();
        if (chosen.Count < 2) { StatusMessage = "圓桌至少需要兩位已啟用的成員。"; return; }
        try { PrivacyGuard.ValidatePrompt(text); }
        catch (Exception error) { StatusMessage = error.Message; return; }
        var name = title.Trim();
        var topic = new RoundtableTopic(name.Length == 0 ? TopicNaming.TitleFrom(text) : name, kind, text,
            attachments, chosen, chosen.Contains(lead) ? lead : chosen[0], peerReview);
        lock (Archive)
        {
            Archive.RoundtableTopics ??= [];
            Archive.RoundtableTopics.Insert(0, topic);
        }
        SelectedRoundtableID = topic.Id;
        Persist();
        PrepareRoundtable(topic.Id);
    }

    /// <summary>Independent preparation; contents stay hidden until everyone is done.</summary>
    public void PrepareRoundtable(Guid id)
    {
        List<MemberID> pending;
        List<ChatAttachment> attachments;
        lock (Archive)
        {
            var topic = FindRoundtable(id);
            if (roundtableRunning || topic is null) return;
            pending = topic.Members
                .Where(agent => !(topic.Submission(agent)?.Succeeded ?? false)).ToList();
            if (pending.Count == 0) return;
            topic.Status = RoundtableStatus.Preparing;
            attachments = topic.Attachments;
            roundtableRunning = true;
            cancelRoundtableQueue = false;
        }
        BumpRoundtable();
        Persist();
        _ = Task.Run(async () =>
        {
            try
            {
                var (preparedFiles, inline, scratch) = PrepareAttachments(attachments);
                try
                {
                    string material;
                    RoundtableKind kind;
                    Dictionary<string, MemberCard> cards;
                    lock (Archive)
                    {
                        var topic = FindRoundtable(id)!;
                        kind = topic.Kind;
                        material = inline.Length == 0 ? topic.Material : topic.Material + "\n\n" + inline;
                        cards = Archive.Members.ToDictionary(member => member.Agent.RawValue, member => member);
                    }
                    // Independent work: every member starts at once and none sees another's draft.
                    var calls = pending.Select(agent =>
                    {
                        var member = cards.GetValueOrDefault(agent.RawValue);
                        var prompt = RoundtableFlow.PreparePrompt(
                            new RoundtableTopic { Title = FindRoundtable(id)?.Title ?? "", Kind = kind, Material = material },
                            member, Archive.Settings.UserName, Archive.Members);
                        var callFiles = !ExecutorOf(agent).AcceptsFiles() ? new List<PreparedAttachment>()
                            : ExecutorOf(agent) == Executor.Codex
                                ? preparedFiles.Where(file => file.Attachment.Kind != AttachmentKind.Pdf).ToList()
                                : preparedFiles;
                        return RoundtableCall(id, agent, prompt, callFiles);
                    }).ToList();
                    await foreach (var result in AwaitEach(calls))
                    {
                        if (result is null) continue;
                        lock (Archive)
                        {
                            var current = FindRoundtable(id);
                            if (current is null) continue;
                            current.Submissions.RemoveAll(submission => submission.Agent == result.Agent);
                            current.Submissions.Add(result);
                        }
                        Persist();
                        BumpRoundtable();
                    }
                    lock (Archive)
                    {
                        if (FindRoundtable(id) is { } done && done.AllPrepared)
                        {
                            done.Status = RoundtableStatus.Prepared;
                            Notify("圓桌準備完成", done.Title);
                        }
                    }
                }
                finally { TryDelete(scratch); }
            }
            finally
            {
                roundtableRunning = false;
                Persist();
                BumpRoundtable();
            }
        });
    }

    /// <summary>Reveal: optional peer review, then the lead's issue list, then deliberation.</summary>
    public void RevealRoundtable(Guid id)
    {
        lock (Archive)
        {
            var topic = FindRoundtable(id);
            if (roundtableRunning || topic is null || topic.Status != RoundtableStatus.Prepared
                || topic.Completed < 1) return;
            topic.Status = RoundtableStatus.Reviewing;
            roundtableRunning = true;
            cancelRoundtableQueue = false;
        }
        Persist();
        BumpRoundtable();
        _ = Task.Run(async () =>
        {
            try
            {
                var (names, members, topicCopy) = SnapshotFor(id);
                if (topicCopy.PeerReview)
                {
                    // Each member reviews the others' drafts; the reviews don't depend on one another.
                    var reviewers = topicCopy.Members
                        .Where(agent => topicCopy.Submissions.Any(submission => submission.Agent != agent && submission.Succeeded))
                        .ToList();
                    var reviewCalls = reviewers.Select(agent =>
                    {
                        var member = Card(agent)!;
                        var prompt = RoundtableFlow.PeerReviewPrompt(topicCopy, member, names, WorkPersona(agent));
                        return RoundtableCall(id, agent, prompt, []);
                    }).ToList();
                    await foreach (var review in AwaitEach(reviewCalls))
                    {
                        if (review is null) continue;
                        lock (Archive)
                        {
                            var current = FindRoundtable(id);
                            if (current is null) continue;
                            current.PeerReviews.RemoveAll(item => item.Agent == review.Agent);
                            current.PeerReviews.Add(review);
                        }
                        Persist();
                        BumpRoundtable();
                    }
                }
                if (cancelRoundtableQueue) return;
                await CompileFindings(id);
                StartDeliberation(id);
            }
            finally
            {
                roundtableRunning = false;
                Persist();
                BumpRoundtable();
            }
        });
    }

    async Task CompileFindings(Guid id)
    {
        RoundtableTopic topicCopy;
        MemberCard leadCard;
        lock (Archive)
        {
            var topic = FindRoundtable(id);
            if (topic is null || Card(topic.Lead) is not { } lead) return;
            topicCopy = CloneTopic(topic);
            leadCard = lead;
        }
        var result = await RoundtableCall(id, topicCopy.Lead,
            RoundtableFlow.FindingsPrompt(topicCopy, leadCard, Names(), WorkPersona(topicCopy.Lead)), []);
        lock (Archive)
        {
            var current = FindRoundtable(id);
            if (current is null) return;
            current.Findings = result.Succeeded ? result.Text : "";
            current.FindingsError = result.Succeeded ? null : result.Error ?? "沒有回覆";
            // Keep the deliberation room's briefing in step with the list.
            if (current.RoomID is { } roomID && FindRoom(roomID) is { } room)
            {
                room.Briefing = RoundtableFlow.Briefing(current, Names());
            }
        }
        Persist();
        BumpRoundtable();
    }

    /// <summary>Retry a failed issue list (e.g. after a usage limit resets).</summary>
    public void RecompileFindings(Guid id)
    {
        lock (Archive)
        {
            if (roundtableRunning || roomRunning || FindRoundtable(id) is null) return;
            roundtableRunning = true;
            cancelRoundtableQueue = false;
        }
        BumpRoundtable();
        _ = Task.Run(async () =>
        {
            try { await CompileFindings(id); }
            finally
            {
                roundtableRunning = false;
                Persist();
                BumpRoundtable();
            }
        });
    }

    void StartDeliberation(Guid id)
    {
        Guid roomID;
        lock (Archive)
        {
            var topic = FindRoundtable(id);
            if (topic is null) return;
            var lounge = new LoungeState { Style = LoungeStyle.Meeting, HandRaise = true };
            var room = new TeamRoom
            {
                Title = $"圓桌：{topic.Title}",
                Participants = topic.Members.ToList(),
                Lounge = lounge,
                RoundtableID = topic.Id,
                Briefing = RoundtableFlow.Briefing(topic, Names()),
            };
            Archive.Rooms ??= [];
            Archive.Rooms.Add(room);
            topic.RoomID = room.Id;
            topic.Status = RoundtableStatus.Discussing;
            roomID = room.Id;
        }
        Persist();
        BumpRoundtable();
        DispatchRoomMessage(roomID, RoundtableFlow.DeliberationOpening(FindRoundtable(id)!), null, "你");
    }

    public void SendToDeliberation(Guid id, string text)
    {
        Guid? roomID;
        lock (Archive)
        {
            var topic = FindRoundtable(id);
            if (topic?.RoomID is not { } found || roomRunning) return;
            roomID = found;
        }
        var value = text.Trim();
        if (value.Length == 0 || value.Length > 8_000) return;
        DispatchRoomMessage(roomID.Value, value, null, "你");
    }

    public void AdoptCandidate(Guid id, MemberID? agent)
    {
        lock (Archive)
        {
            var topic = FindRoundtable(id);
            if (topic is null || !topic.Kind.HasCandidates()) return;
            if (agent is { } pick && !(topic.Submission(pick)?.Succeeded ?? false)) return;
            topic.Adopted = agent;
        }
        Persist();
        BumpRoundtable();
    }

    /// <summary>Ends the deliberation: one member writes the minutes, which become the conclusion.</summary>
    public void ConcludeRoundtable(Guid id, MemberID agent)
    {
        lock (Archive)
        {
            var topic = FindRoundtable(id);
            if (topic?.RoomID is not { } roomID) return;
        }
        WriteMinutes(agent);
    }

    public void ConfirmConclusion(Guid id)
    {
        lock (Archive)
        {
            var topic = FindRoundtable(id);
            if (topic is null || topic.Status != RoundtableStatus.Conclusion || topic.Conclusion.Length == 0) return;
            topic.Status = RoundtableStatus.Confirmed;
        }
        Persist();
        BumpRoundtable();
    }

    public void DeleteRoundtable(Guid id)
    {
        lock (Archive)
        {
            var topic = FindRoundtable(id);
            if (roundtableRunning || topic is null) return;
            if (topic.RoomID is { } roomID)
            {
                if (roomRunning) return;
                Archive.Rooms?.RemoveAll(room => room.Id == roomID);
            }
            Archive.RoundtableTopics?.RemoveAll(item => item.Id == id);
            if (selectedRoundtableID == id) selectedRoundtableID = Archive.RoundtableTopics?.FirstOrDefault()?.Id;
        }
        Persist();
        BumpRoundtable();
    }

    public void StopRoundtable()
    {
        cancelRoundtableQueue = true;
        foreach (var runID in roundtableRunIDs.ToList()) runner.Cancel(runID);
        BumpRoundtable();
    }

    async Task<RoundtableSubmission?> RoundtableCall(Guid id, MemberID agent, string prompt,
        IReadOnlyList<PreparedAttachment> files)
    {
        var runID = Guid.NewGuid();
        roundtableRunIDs.Add(runID);
        try
        {
            lock (Archive)
            {
                var topic = FindRoundtable(id);
                if (topic is not null) topic.Calls += 1;
            }
            // A CLI always needs a working directory that exists: anchor each
            // call to the member's own home folder (roundtables are read-only).
            var home = Desk?.PersonalFolder(agent) ?? "";
            var settings = CloneSettingsWithWorkspace(home);
            var result = await runner.RunDetailedAsync(runID, agent, Clamp(prompt, 30_000), settings,
                fallbackWorkspace: home, access: ExecutionAccess.ReadOnly,
                options: new CLIRunOptions("", "", files.ToList(), null), onUpdate: _ => { });
            return new RoundtableSubmission(agent, result.Text);
        }
        catch (Exception error)
        {
            return new RoundtableSubmission(agent, error: cancelRoundtableQueue ? "已停止" : error.Message);
        }
        finally
        {
            roundtableRunIDs.Remove(runID);
        }
    }

    /// <summary>Awaits calls in submission order, yielding each result as it lands.</summary>
    static async IAsyncEnumerable<RoundtableSubmission?> AwaitEach(
        IEnumerable<Task<RoundtableSubmission?>> calls)
    {
        var tasks = calls.ToList();
        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks);
            tasks.Remove(finished);
            yield return await finished;
        }
    }

    (IReadOnlyDictionary<string, string> Names, List<MemberID> Members, RoundtableTopic Topic) SnapshotFor(Guid id)
    {
        lock (Archive)
        {
            var topic = FindRoundtable(id)!;
            return (Names(), topic.Members.ToList(), CloneTopic(topic));
        }
    }

    RoundtableTopic CloneTopic(RoundtableTopic topic) =>
        CoreJson.Deserialize<RoundtableTopic>(CoreJson.Serialize(topic))!;

    Dictionary<string, string> Names() =>
        Archive.Members.ToDictionary(member => member.Agent.RawValue, member => member.Name);

    /// <summary>Decrypts image/PDF attachments to a private temp folder; text files are inlined.</summary>
    (List<PreparedAttachment> Files, string Inline, string? Scratch) PrepareAttachments(IReadOnlyList<ChatAttachment> items)
    {
        if (Persistence is null || items.Count == 0) return ([], "", null);
        var texts = items.Where(item => item.Kind == AttachmentKind.Text)
            .Select(item => (Item: item, Bytes: Persistence.LoadAttachment(item.Id)))
            .Where(pair => pair.Bytes is not null).ToList();
        var binaries = items.Where(item => item.Kind != AttachmentKind.Text).ToList();
        if (binaries.Count == 0) return ([], AttachmentPolicy.Inline(texts.Select(pair => (pair.Item, pair.Bytes!)).ToList()), null);
        var scratch = Path.Combine(Path.GetTempPath(), "airoom-attachments-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(scratch);
        var files = binaries.Select(item =>
        {
            var extension = item.Kind == AttachmentKind.Pdf ? "pdf" : Path.GetExtension(item.FileName).TrimStart('.').ToLowerInvariant();
            var file = Path.Combine(scratch, $"{item.Id:D}.{extension}");
            File.WriteAllBytes(file, Persistence.LoadAttachment(item.Id));
            return new PreparedAttachment(item, file);
        }).ToList();
        return (files, "", scratch);
    }

    static string Clamp(string text, int max) => text.Length <= max ? text : text[..max];
}
