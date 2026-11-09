using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using AIROOMApp.Helpers;
using AIROOMCore;

namespace AIROOMApp.ViewModels;

/// <summary>
/// The app's central state, ported from the macOS WorkbenchModel (the MVP
/// parts: member desk, members, attention, settings, tray-level actions).
/// Rooms, roundtables, projects and the live stage join in later phases.
/// </summary>
public sealed class WorkbenchModel : ObservableObject
{
    readonly CliProcessRunner runner = new();
    readonly Microsoft.UI.Dispatching.DispatcherQueue dispatcher;

    Dictionary<MemberID, Guid> activeRuns = [];
    Dictionary<Guid, CancellationTokenSource> runCancellations = [];
    Dictionary<MemberID, string> queuedSends = [];
    List<AttentionItem> allAttentionItems = [];
    DateTimeOffset? lastUpdateCheckAt;
    string? updateStatus;
    ReleaseInfo? availableUpdate;
    bool stateLoaded;
    bool updateChecking;

    public WorkbenchModel(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        this.dispatcher = dispatcher;
        try
        {
            Persistence = new LocalStateStore();
            Archive = Persistence.Load();
        }
        catch (Exception error)
        {
            Persistence = null;
            Archive = new SavedState();
            StorageAvailable = false;
            StatusMessage = $"無法讀取加密資料：{error.Message}";
        }
        Desk = StorageAvailable ? DeskWorkspaceOrNull() : null;
        runner.Observer = (agent, @event) =>
        {
            var day = DateTimeOffset.Now.ToString("yyyy-MM-dd");
            lock (Archive)
            {
                Archive.Calls.Record(@event, agent, day);
            }
            if (@event is CallEvent.Completed or CallEvent.NotSent or CallEvent.Unknown)
            {
                RunOnDispatcher(() => { OnPropertyChanged(nameof(CallUsageText)); });
            }
        };
        ApplyMemberSettings();
        BusyAgents.CollectionChanged += (_, _) => RunOnDispatcher(() =>
        {
            OnPropertyChanged(nameof(AnyBusyAgent));
            // The workstation re-renders member rows and the send/stop buttons
            // off this, exactly like a topic change.
            OnPropertyChanged(nameof(TopicsVersion));
        });
        RefreshCLIStatuses();
        _ = CheckForUpdateAsync();
    }

    DeskWorkspace? DeskWorkspaceOrNull()
    {
        try
        {
            return new DeskWorkspace(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIROOMNative", "desk"));
        }
        catch
        {
            return null;
        }
    }

    public LocalStateStore? Persistence { get; }
    public DeskWorkspace? Desk { get; }
    public WorkspaceStaging? Staging => Desk?.Staging;

    public SavedState Archive { get; private set; }

    public bool StorageAvailable { get; } = true;

    public ObservableCollection<MemberID> BusyAgents { get; } = [];
    public ObservableCollection<Guid> DeskUndoable { get; } = [];
    public Dictionary<Guid, List<FileChange>> DeskChanges { get; } = [];
    public Dictionary<MemberID, string> ChatDrafts { get; } = [];
    public Dictionary<MemberID, List<ChatAttachment>> ChatAttachments { get; } = [];
    public Dictionary<Executor, List<ModelChoice>> ModelCatalogs { get; } = [];
    public HashSet<Executor> ModelCatalogLoading { get; } = [];
    public Dictionary<Executor, OfficialCLIStatus> OfficialCLIStatuses { get; private set; } = [];

    string? statusMessage;
    public string? StatusMessage
    {
        get => statusMessage;
        set => SetProperty(ref statusMessage, value);
    }

    public string CallUsageText
    {
        get
        {
            lock (Archive)
            {
                // Every real member counts — the executor defaults were the
                // only ones summed before, which read as zero for teams of
                // added members.
                return Archive.Members
                    .Select(member => Archive.Calls.Usage(member.Agent))
                    .Aggregate(new CallUsage(), (a, b) => a + b)
                    .Label(Archive.Settings.Language);
            }
        }
    }

    public bool AnyBusyAgent => BusyAgents.Count > 0;

    public ReleaseInfo? AvailableUpdate { get => availableUpdate; private set => SetProperty(ref availableUpdate, value); }
    public bool UpdateChecking { get => updateChecking; private set => SetProperty(ref updateChecking, value); }
    public string? UpdateStatus { get => updateStatus; private set => SetProperty(ref updateStatus, value); }

    // MARK: Members

    public MemberCard? Card(MemberID agent) =>
        Archive.Members.FirstOrDefault(member => member.Agent == agent);

    public List<MemberID> MemberIDs => Archive.Members.Select(member => member.Agent).ToList();

    public List<MemberID> EnabledMemberIDs =>
        Archive.Members.Where(member => member.Enabled).Select(member => member.Agent).ToList();

    public List<MemberID> VisibleAgents =>
        Archive.Members.Where(member => !member.CardHidden).Select(member => member.Agent).ToList();

    public Executor ExecutorOf(MemberID agent) => Card(agent)?.Executor ?? agent.BuiltInExecutor ?? Executor.Claude;

    public string MemberName(MemberID agent) => Archive.MemberName(agent);

    void ApplyMemberSettings()
    {
        foreach (var member in Archive.Members)
        {
            runner.SetDefaults(member.Agent, member.Executor, member.DefaultModel, member.DefaultEffort);
        }
    }

    public void UpdateMember(MemberCard member)
    {
        var index = Archive.Members.FindIndex(existing => existing.Id == member.Id);
        if (index < 0) return;
        Archive.Members[index] = member.Normalized();
        ApplyMemberSettings();
        Persist();
        OnPropertyChanged(nameof(MemberIDs));
    }

    public void ResetMember(MemberID agent)
    {
        var index = Archive.Members.FindIndex(member => member.Agent == agent);
        if (index < 0) return;
        var old = Archive.Members[index];
        var fresh = agent.IsBuiltIn
            ? new MemberCard(agent, old.Executor, enabled: old.Enabled) { Id = old.Id }
            : MemberCard.Added(old.Look, old.Executor);
        fresh.Id = old.Id;
        fresh.Agent = agent;
        fresh.Enabled = old.Enabled;
        Archive.Members[index] = fresh;
        ApplyMemberSettings();
        Persist();
        OnPropertyChanged(nameof(MemberIDs));
    }

    public MemberID? AddMember(MemberTemplate? template, Executor executor, string name)
    {
        var card = MemberCard.Added(template, executor);
        var trimmed = name.Trim();
        if (trimmed.Length > 0) card.Name = trimmed;
        Archive.AddMember(card);
        ApplyMemberSettings();
        Persist();
        OnPropertyChanged(nameof(MemberIDs));
        return card.Agent;
    }

    public void RemoveMember(MemberID agent)
    {
        if (BusyAgents.Contains(agent))
        {
            StatusMessage = "這位成員正在工作，請先停止。";
            return;
        }
        try
        {
            Archive.RemoveMember(agent);
            ChatDrafts.Remove(agent);
            ChatAttachments.Remove(agent);
            Persist();
            OnPropertyChanged(nameof(MemberIDs));
        }
        catch (Exception error)
        {
            StatusMessage = error.Message;
        }
    }

    public void SetExecutor(Executor executor, MemberID agent)
    {
        if (BusyAgents.Contains(agent))
        {
            StatusMessage = "這位成員正在工作，請先停止。";
            return;
        }
        Archive.SetExecutor(executor, agent);
        ApplyMemberSettings();
        Persist();
    }

    /// <summary>Copies a picked picture into the encrypted store for a member's portrait or avatar.</summary>
    public ChatAttachment? StoreMemberImage(string path)
    {
        if (Persistence is null) return null;
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length > 8 * 1024 * 1024)
            {
                StatusMessage = "圖片需在 8 MB 以內。";
                return null;
            }
            var mediaType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => null,
            };
            if (mediaType is null)
            {
                StatusMessage = "只支援 PNG、JPG 或 WebP 圖片。";
                return null;
            }
            var attachment = new ChatAttachment(Path.GetFileName(path), AttachmentKind.Image, mediaType, data.Length);
            Persistence.StoreAttachment(data, attachment.Id);
            return attachment;
        }
        catch (Exception error)
        {
            StatusMessage = error.Message;
            return null;
        }
    }

    public byte[]? MemberImageData(ChatAttachment? attachment)
    {
        if (attachment is null || Persistence is null) return null;
        try
        {
            return Persistence.LoadAttachment(attachment.Id);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The uploaded portrait, else the template's bundled artwork.</summary>
    public byte[]? PortraitOrTemplate(MemberCard? card)
    {
        var uploaded = MemberImageData(card?.Portrait);
        if (uploaded is not null) return uploaded;
        var art = card?.Look?.Art;
        var bytes = Services.StudioAssets.Image(art);
        if (art is not null && bytes is null) Program.Log($"portrait[{card?.Agent}] art {art} MISSING");
        return bytes;
    }

    /// <summary>The art a member's circular avatar is cut from: the uploaded
    /// avatar when one exists, else the portrait/template. Its crop follows.</summary>
    public (byte[]? Art, PortraitFaceAnchor? Crop) AvatarOrPortrait(MemberCard? card)
    {
        var avatar = MemberImageData(card?.Avatar);
        if (avatar is { Length: > 0 }) return (avatar, card?.AvatarImageCrop);
        return (PortraitOrTemplate(card), card?.AvatarCrop);
    }

    /// <summary>Which picture the avatar crop editor edits, following macOS's
    /// avatarSource: uploaded avatar, uploaded portrait, then template art.</summary>
    public AvatarEditorSource? AvatarSource(MemberID agent)
    {
        var card = Card(agent);
        if (card is null) return null;
        var avatar = MemberImageData(card.Avatar);
        if (avatar is { Length: > 0 })
        {
            return new AvatarEditorSource(avatar, card.AvatarImageCrop, AvatarEditorSourceKind.UploadedAvatar);
        }
        var portrait = PortraitOrTemplate(card);
        if (portrait is not { Length: > 0 }) return null;
        if (MemberImageData(card.Portrait) is not null)
        {
            return new AvatarEditorSource(portrait, card.AvatarCrop, AvatarEditorSourceKind.UploadedPortrait);
        }
        return new AvatarEditorSource(portrait, card.AvatarCrop, AvatarEditorSourceKind.TemplateArt,
            card.Look?.Face ?? MemberTemplate.InitialFace);
    }

    // MARK: Encrypted backups

    public bool CanRestore => StorageAvailable && BusyAgents.Count == 0;

    public string? CreateBackup()
    {
        if (Persistence is null || !StorageAvailable) return null;
        try
        {
            return Persistence.Backup();
        }
        catch (Exception error)
        {
            StatusMessage = $"備份失敗：{error.Message}";
            return null;
        }
    }

    public List<BackupFile> BackupFiles() => Persistence?.Backups() ?? [];

    /// <summary>Reads a backup without changing anything; null means unreadable here.</summary>
    public BackupSummary? PreviewBackup(string file)
    {
        try
        {
            return Persistence?.Preview(file);
        }
        catch (Exception error)
        {
            StatusMessage = $"無法讀取備份：{error.Message}";
            return null;
        }
    }

    public void DeleteBackup(string file)
    {
        try
        {
            Persistence?.DeleteBackup(file);
            StatusMessage = "備份已刪除。";
        }
        catch (Exception error)
        {
            StatusMessage = $"刪除失敗：{error.Message}";
        }
    }

    /// <summary>Replaces the in-memory archive with the restored one; the store
    /// has already written it to disk (with a before-restore backup). Members
    /// must be idle: runs hold session ids and drafts that the snapshot owns.</summary>
    public void RestoreFromBackup(string file)
    {
        if (Persistence is null || !CanRestore)
        {
            StatusMessage = "成員工作中時不能還原備份。";
            return;
        }
        try
        {
            Archive = Persistence.Restore(file);
            ApplyMemberSettings();
            ChatDrafts.Clear();
            ChatAttachments.Clear();
            activeRuns.Clear();
            runCancellations.Clear();
            BusyAgents.Clear();
            queuedSends.Clear();
            DeskUndoable.Clear();
            DeskChanges.Clear();
            Persist();
            OnPropertyChanged(nameof(MemberIDs));
            OnPropertyChanged(nameof(TopicsVersion));
            OnPropertyChanged(nameof(CallUsageText));
            OnPropertyChanged(nameof(AttachmentsVersion));
            StatusMessage = "已恢復備份（恢復前的資料已自動存一份備份）。";
        }
        catch (Exception error)
        {
            StatusMessage = $"恢復失敗：{error.Message}";
        }
    }

    /// <summary>The persona line: what the member is told about themself and the owner.</summary>
    public string MemberProfile(MemberID agent)
    {
        var card = Card(agent);
        if (card is null) return "";
        return card.Profile(Archive.Members, Archive.Settings.OwnerName);
    }

    public string WorkPersona(MemberID agent)
    {
        var card = Card(agent);
        var persona = card?.Persona.Trim() ?? "";
        var profile = MemberProfile(agent);
        if (persona.Length == 0 && profile.Length == 0) return "";
        var lines = new List<string> { "（以下人物設定只影響你的語氣和觀點，不改變這一步的工作規則與輸出格式。）" };
        if (persona.Length > 0) lines.Add("你的人物設定：" + persona[..Math.Min(persona.Length, 800)]);
        if (profile.Length > 0) lines.Add(profile);
        return string.Join("\n", lines);
    }

    // MARK: Desk topics

    public Conversation? ActiveTopic(MemberID agent) =>
        Archive.Conversations.FirstOrDefault(conversation => conversation.Agent == agent && !conversation.Archived);

    public List<Conversation> Topics(MemberID agent) =>
        Archive.Conversations.Where(conversation => conversation.Agent == agent && !conversation.Archived)
            .Concat(Archive.Conversations.Where(conversation => conversation.Agent == agent && conversation.Archived))
            .ToList();

    public void NewTopic(MemberID agent)
    {
        var index = Archive.Conversations.FindIndex(conversation => conversation.Agent == agent && !conversation.Archived);
        if (index < 0)
        {
            // No open topic yet: "new topic" simply opens the first one.
            Archive.Conversations.Add(new Conversation(agent));
            Persist();
            OnPropertyChanged(nameof(TopicsVersion));
            return;
        }
        var topic = Archive.Conversations[index];
        topic.Archived = true;
        topic.Draft = "";
        topic.DraftAttachments = [];
        if (!topic.TitleLocked && topic.Messages.Count > 0)
        {
            topic.Title = TopicNaming.TitleFrom(string.Join(" ",
                topic.Messages.Where(message => message.Role == MessageRole.User).Select(message => message.Body)));
        }
        Archive.Conversations.Add(new Conversation(agent));
        Persist();
        OnPropertyChanged(nameof(TopicsVersion));
    }

    public void OpenTopic(Guid id)
    {
        var index = Archive.Conversations.FindIndex(conversation => conversation.Id == id);
        if (index < 0) return;
        var topic = Archive.Conversations[index];
        var openIndex = Archive.Conversations.FindIndex(
            conversation => conversation.Agent == topic.Agent && !conversation.Archived);
        if (openIndex >= 0) Archive.Conversations[openIndex].Archived = true;
        topic.Archived = false;
        Persist();
        OnPropertyChanged(nameof(TopicsVersion));
    }

    public void RenameTopic(Guid id, string title)
    {
        var index = Archive.Conversations.FindIndex(conversation => conversation.Id == id);
        if (index < 0) return;
        Archive.Conversations[index].Title = title.Trim();
        Archive.Conversations[index].TitleLocked = true;
        Persist();
        OnPropertyChanged(nameof(TopicsVersion));
    }

    public void SetTopicModel(MemberID agent, string model)
    {
        var topic = ActiveTopic(agent);
        if (topic is null) return;
        topic.Model = ModelCatalog.Sanitize(model);
        Persist();
    }

    public void SetTopicEffort(MemberID agent, string effort)
    {
        var topic = ActiveTopic(agent);
        if (topic is null) return;
        topic.Effort = ModelCatalog.Sanitize(effort);
        Persist();
    }

    /// <summary>UI binding signal for the topic list.</summary>
    public int TopicsVersion { get; private set; }

    public List<ModelChoice> ModelChoices(MemberID agent)
    {
        var catalog = ModelCatalogs.GetValueOrDefault(ExecutorOf(agent));
        if (catalog is { Count: > 0 }) return catalog;
        return ExecutorOf(agent) switch
        {
            Executor.Claude => [.. ModelCatalog.ClaudeFallback],
            Executor.ZCode => ZCode.ModelChoices(),
            _ => [],
        };
    }

    public async void RefreshModelCatalogs()
    {
        foreach (var executor in ExecutorList.All)
        {
            if (executor != Executor.Claude || ModelCatalogLoading.Contains(executor)) continue;
            ModelCatalogLoading.Add(executor);
            try
            {
                var choices = await Task.Run(() => runner.ClaudeModelsAsync(Archive.Settings, 10_000));
                ModelCatalogs[executor] = choices;
            }
            catch
            {
                ModelCatalogs[executor] = [.. ModelCatalog.ClaudeFallback];
            }
            finally
            {
                ModelCatalogLoading.Remove(executor);
                OnPropertyChanged(nameof(ModelChoices));
            }
        }
    }

    // MARK: Drafts and attachments

    public void DraftChanged(MemberID agent, string text)
    {
        ChatDrafts[agent] = text;
        var topic = ActiveTopic(agent);
        if (topic is not null)
        {
            topic.Draft = text;
            Persist();
        }
    }

    public string LoadDraft(MemberID agent) =>
        ChatDrafts.TryGetValue(agent, out var draft) ? draft
            : ActiveTopic(agent)?.Draft is { } saved ? saved : "";

    public void AddAttachments(IEnumerable<string> paths, MemberID agent)
    {
        if (Persistence is null) return;
        var list = ChatAttachments.TryGetValue(agent, out var existing) ? existing : ChatAttachments[agent] = [];
        foreach (var path in paths)
        {
            if (list.Count >= AttachmentPolicy.MaxPerMessage)
            {
                StatusMessage = AttachmentError.TooMany.Message;
                break;
            }
            try
            {
                var data = File.ReadAllBytes(path);
                var attachment = AttachmentPolicy.Classify(Path.GetFileName(path), data);
                Persistence.StoreAttachment(data, attachment.Id);
                list.Add(attachment);
            }
            catch (Exception error) when (error is AttachmentError or NativeCLIError)
            {
                StatusMessage = error.Message;
            }
            catch (Exception error)
            {
                StatusMessage = error.Message;
            }
        }
        OnPropertyChanged(nameof(AttachmentsVersion));
    }

    public void RemoveDraftAttachment(Guid id, MemberID agent)
    {
        if (ChatAttachments.TryGetValue(agent, out var list))
        {
            list.RemoveAll(attachment => attachment.Id == id);
            OnPropertyChanged(nameof(AttachmentsVersion));
        }
    }

    public int AttachmentsVersion { get; private set; }

    public byte[]? AttachmentData(Guid id) => Persistence?.LoadAttachment(id);

    // MARK: Submitting a desk message

    public void Submit(MemberID agent) => Submit(agent, null);

    /// <summary>Text to append to the room. queuedText carries a message that
    /// was queued while the member was busy; it bypasses the input box, which
    /// was emptied the moment the user queued it.</summary>
    public void Submit(MemberID agent, string? queuedText)
    {
        if (!StorageAvailable || Persistence is null || Desk is null) return;
        if (BusyAgents.Contains(agent))
        {
            // Queuing beats silence: the member replies, then this text goes
            // out on its own. The draft freezes into the queue now, so the
            // input box frees up for the next line right away.
            var text = queuedText ?? (ChatDrafts.GetValueOrDefault(agent) ?? "").Trim();
            if (text.Length == 0)
            {
                StatusMessage = "這位成員工作中——先輸入訊息再按「排隊送出」。";
                return;
            }
            queuedSends[agent] = text;
            if (queuedText is null) ChatDrafts[agent] = "";
            StatusMessage = "已排隊：這則訊息會在回覆後自動送出。";
            OnPropertyChanged(nameof(TopicsVersion));
            return;
        }
        var index = Archive.Conversations.FindIndex(conversation => conversation.Agent == agent && !conversation.Archived);
        if (index < 0)
        {
            // Built-in members start with no desk topic at all; the first
            // message opens one instead of silently dropping the text.
            Archive.Conversations.Add(new Conversation(agent));
            index = Archive.Conversations.Count - 1;
        }
        var prompt = queuedText ?? (ChatDrafts.GetValueOrDefault(agent) ?? "").Trim();
        var attachments = ChatAttachments.GetValueOrDefault(agent) ?? [];
        if (prompt.Length == 0 && attachments.Count == 0) return;
        if (prompt.Length > CommandPlan.MaxPromptCharacters)
        {
            StatusMessage = "輸入文字不可超過 30,000 字。";
            return;
        }
        var topic = Archive.Conversations[index];
        var history = topic.Messages.Where(message => message.Body.Length > 0).ToList();

        // Continuing the CLI conversation: earlier images are already in it.
        var resumeID = topic.CanContinueSession ? topic.SessionID : null;
        var recentImages = resumeID is not null
            ? []
            : history.Where(message => message.Role == MessageRole.User).TakeLast(2)
                .SelectMany(message => message.Attachments)
                .Where(attachment => attachment.Kind != AttachmentKind.Text);
        var files = recentImages.Concat(attachments.Where(attachment => attachment.Kind != AttachmentKind.Text))
            .DistinctBy(attachment => attachment.Id)
            .TakeLast(AttachmentPolicy.MaxPerMessage)
            .ToList();
        if (ExecutorOf(agent) == Executor.Codex) files.RemoveAll(attachment => attachment.Kind == AttachmentKind.Pdf);
        if (!ExecutorOf(agent).AcceptsFiles()) files.Clear();

        var textAttachments = attachments.Where(attachment => attachment.Kind == AttachmentKind.Text)
            .Select(attachment => (attachment, Persistence.LoadAttachment(attachment.Id))).ToList();
        var question = string.Join("\n\n", new[]
        {
            prompt.Length == 0 ? "請看附件。" : prompt,
            AttachmentPolicy.Inline(textAttachments),
        }.Where(part => part.Length > 0));

        var persona = Card(agent)?.Persona ?? "";
        var profile = MemberProfile(agent);
        var fullPrompt = PromptBuilder.Build(history, question,
            language: Archive.Settings.Language, canWrite: true,
            userName: Archive.Settings.UserName, persona: persona, profile: profile);

        // Rounds may edit the topic's folder: snapshot first so the round can be undone.
        string folder;
        try
        {
            Desk.BeginRound(topic.Id, agent, topic.WorkFolder);
            folder = Desk.WorkingFolder(topic.Id, agent, topic.WorkFolder);
            if (!DeskUndoable.Contains(topic.Id)) DeskUndoable.Add(topic.Id);
        }
        catch (Exception error)
        {
            StatusMessage = $"無法準備工作資料夾: {error.Message}";
            return;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "airoom-attachments-" + Guid.NewGuid().ToString("D"));
        var prepared = new List<PreparedAttachment>();
        try
        {
            if (files.Count > 0) Directory.CreateDirectory(scratch);
            foreach (var item in files)
            {
                var extension = item.Kind == AttachmentKind.Pdf ? "pdf" : Path.GetExtension(item.FileName).TrimStart('.').ToLowerInvariant();
                var file = Path.Combine(scratch, $"{item.Id:D}.{extension}");
                File.WriteAllBytes(file, Persistence.LoadAttachment(item.Id));
                prepared.Add(new PreparedAttachment(item, file));
            }
        }
        catch (Exception error)
        {
            TryDelete(scratch);
            StatusMessage = error.Message;
            return;
        }

        var refresh = MemberLife.NeedsRefresh(topic.SessionProfile, persona, profile)
            ? new List<string> { MemberLife.RefreshNote(persona, profile, Archive.Settings.OwnerName) }
            : new List<string>();
        var delta = string.Join("\n\n", topic.SessionNotes.Concat(refresh).Append(question));
        var fingerprint = MemberLife.Fingerprint(persona, profile);
        var options = new CLIRunOptions(topic.Model, topic.Effort, prepared,
            new SessionPlan(resumeID, fullPrompt));
        var sentPrompt = resumeID is not null ? delta : fullPrompt;
        var replyID = Guid.NewGuid();
        var runID = Guid.NewGuid();
        if (topic.Messages.Count == 0 && !topic.TitleLocked)
        {
            topic.Title = prompt.Length == 0
                ? (attachments.FirstOrDefault()?.FileName ?? TopicNaming.DefaultTitle)[..Math.Min(
                    attachments.FirstOrDefault()?.FileName.Length ?? 0, 20)]
                : TopicNaming.TitleFrom(prompt);
        }
        topic.Messages.Add(new ChatMessage(MessageRole.User, prompt, attachments));
        topic.Messages.Add(new ChatMessage(MessageRole.Assistant, "執行中……若中斷，請自行確認是否已完成。") { Id = replyID });
        topic.UpdatedAt = DateTimeOffset.Now;
        ChatDrafts[agent] = "";
        ChatAttachments[agent] = [];
        BusyAgents.Add(agent);
        activeRuns[agent] = runID;
        var cancellations = new CancellationTokenSource();
        runCancellations[runID] = cancellations;
        Persist();
        OnPropertyChanged(nameof(TopicsVersion));
        OnPropertyChanged(nameof(AttachmentsVersion));

        var settings = CloneSettingsWithWorkspace(folder);
        var topicID = topic.Id;
        var succeeded = false;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await runner.RunDetailedAsync(runID, agent, sentPrompt, settings,
                    fallbackWorkspace: folder, access: ExecutionAccess.StagedWrite, options: options,
                    onUpdate: text => RunOnDispatcher(() => UpdateReply(agent, replyID, text)),
                    cancellationToken: cancellations.Token);
                succeeded = true;
                RunOnDispatcher(() =>
                {
                    UpdateReply(agent, replyID, result.Text, force: true);
                    SetReplyModel(agent, replyID, result.Model ?? (options.Model.Length == 0 ? null : options.Model));
                    UpdateTopicSession(topicID, result.SessionID, replyID, fingerprint);
                    Notify($"{MemberName(agent)} 回覆了", Truncate(result.Text, 200));
                });
            }
            catch (Exception error)
            {
                RunOnDispatcher(() =>
                {
                    UpdateReply(agent, replyID,
                        $"執行失敗／結果尚待確認: {error.Message}", force: true);
                    UpdateTopicSession(topicID, null, null);
                    Notify($"{MemberName(agent)} 的回覆未完成", "結果待核對，沒有自動重送。");
                });
            }
            finally
            {
                TryDelete(scratch);
                RunOnDispatcher(() =>
                {
                    RefreshDesk(topicID);
                    activeRuns.Remove(agent);
                    runCancellations.Remove(runID);
                    BusyAgents.Remove(agent);
                    Persist();
                    // A send pressed while this run was working goes out now.
                    // Only on success: after a failure the queued text returns
                    // to the draft box for the user to reword or drop.
                    if (succeeded && queuedSends.Remove(agent, out var queuedText)
                        && !string.IsNullOrWhiteSpace(queuedText))
                    {
                        Submit(agent, queuedText);
                    }
                    else if (!succeeded && queuedSends.Remove(agent, out var dropped)
                        && !string.IsNullOrWhiteSpace(dropped))
                    {
                        ChatDrafts[agent] = dropped;
                    }
                });
            }
        });
    }

    NativeSettings CloneSettingsWithWorkspace(string folder)
    {
        var settings = CoreJson.Deserialize<NativeSettings>(CoreJson.Serialize(Archive.Settings))!;
        settings.WorkspacePath = folder;
        return settings;
    }

    void UpdateReply(MemberID agent, Guid replyID, string text, bool force = false)
    {
        var index = Archive.Conversations.FindIndex(conversation => conversation.Agent == agent && !conversation.Archived);
        if (index < 0) return;
        var messages = Archive.Conversations[index].Messages;
        var messageIndex = messages.FindIndex(message => message.Id == replyID);
        if (messageIndex < 0) return;
        if (!force && messages[messageIndex].Body.StartsWith("執行失敗")) return;
        messages[messageIndex].Body = text;
        messages[messageIndex].ModelUsed = null;
        OnPropertyChanged(nameof(TopicsVersion));
    }

    void SetReplyModel(MemberID agent, Guid replyID, string? model)
    {
        var index = Archive.Conversations.FindIndex(conversation => conversation.Agent == agent && !conversation.Archived);
        if (index < 0) return;
        var messages = Archive.Conversations[index].Messages;
        var messageIndex = messages.FindIndex(message => message.Id == replyID);
        if (messageIndex >= 0)
        {
            messages[messageIndex].ModelUsed = model;
            OnPropertyChanged(nameof(TopicsVersion));
        }
    }

    void UpdateTopicSession(Guid topicID, string? sessionID, Guid? through, string? profile = null)
    {
        var index = Archive.Conversations.FindIndex(conversation => conversation.Id == topicID);
        if (index < 0) return;
        var topic = Archive.Conversations[index];
        topic.SessionID = sessionID;
        topic.SessionThrough = sessionID is null ? null : through;
        topic.SessionProfile = sessionID is null ? null : profile;
        if (sessionID is null) topic.SessionNotes = [];
        Persist();
    }

    // MARK: Desk folders

    public void RefreshDesk(Guid topicID)
    {
        if (Desk is null) return;
        DeskChanges[topicID] = Desk.Changes(topicID);
        var undoable = Desk.CanUndo(topicID);
        if (undoable && !DeskUndoable.Contains(topicID)) DeskUndoable.Add(topicID);
        else if (!undoable) DeskUndoable.Remove(topicID);
        OnPropertyChanged(nameof(DeskChanges));
    }

    /// <summary>null = personal folder. A topic with unapplied copy changes must apply or discard first.</summary>
    public void SetTopicFolder(MemberID agent, string? path)
    {
        if (BusyAgents.Contains(agent) || Desk is null) return;
        var index = Archive.Conversations.FindIndex(conversation => conversation.Agent == agent && !conversation.Archived);
        if (index < 0) return;
        var topic = Archive.Conversations[index];
        if ((DeskChanges.GetValueOrDefault(topic.Id) ?? []).Count > 0)
        {
            StatusMessage = "工作副本還有未套用的變更，請先「應用到原資料夾」或「丟棄工作副本」。";
            return;
        }
        try { Desk.Discard(topic.Id); }
        catch (Exception error) { StatusMessage = error.Message; return; }
        topic.WorkFolder = path is null ? "" : Path.GetFullPath(path);
        topic.SessionID = null; // a different folder starts a new CLI conversation
        RefreshDesk(topic.Id);
        Persist();
    }

    public void ApplyDeskChanges(MemberID agent)
    {
        if (BusyAgents.Contains(agent) || Desk is null) return;
        var topic = ActiveTopic(agent);
        if (topic is null || topic.WorkFolder.Length == 0) return;
        try
        {
            var applied = Desk.Apply(topic.Id, topic.WorkFolder);
            StatusMessage = $"已應用到原資料夾: {applied.Count} 個檔案";
        }
        catch (Exception error)
        {
            StatusMessage = $"拒絕寫入，請檢查差異及衝突: {error.Message}";
        }
        RefreshDesk(topic.Id);
    }

    public void UndoDeskRound(MemberID agent)
    {
        if (BusyAgents.Contains(agent) || Desk is null) return;
        var index = Archive.Conversations.FindIndex(conversation => conversation.Agent == agent && !conversation.Archived);
        if (index < 0) return;
        var topic = Archive.Conversations[index];
        try
        {
            Desk.UndoRound(topic.Id, agent, topic.WorkFolder);
            var lastIndex = topic.Messages.FindLastIndex(message => message.Role == MessageRole.Assistant);
            if (lastIndex >= 0)
            {
                topic.Messages[lastIndex].Body += "\n\n— 已撤銷這一輪的檔案變更 —";
            }
            // The CLI conversation still remembers those edits; tell it before the next message.
            topic.SessionNotes.Add("（系統說明：你上一輪對檔案做的修改已被使用者撤銷，資料夾已恢復到那一輪之前的樣子。）");
            StatusMessage = "已恢復到這一輪開始前的樣子。";
            Persist();
            OnPropertyChanged(nameof(TopicsVersion));
        }
        catch (Exception error)
        {
            StatusMessage = $"無法撤銷: {error.Message}";
        }
        RefreshDesk(topic.Id);
    }

    public void DiscardDeskCopy(MemberID agent)
    {
        if (BusyAgents.Contains(agent) || Desk is null) return;
        var topic = ActiveTopic(agent);
        if (topic is null) return;
        try { Desk.Discard(topic.Id); }
        catch (Exception error) { StatusMessage = error.Message; }
        RefreshDesk(topic.Id);
    }

    // MARK: Stopping

    /// <summary>Stops the member's running CLI process. A stopped round keeps its
    /// placeholder reply: the result is unconfirmed, and nothing is resent.</summary>
    public void Stop(MemberID agent)
    {
        if (queuedSends.Remove(agent)) OnPropertyChanged(nameof(TopicsVersion));
        if (!activeRuns.TryGetValue(agent, out var runID)) return;
        if (runCancellations.TryGetValue(runID, out var cancellation)) cancellation.Cancel();
        runner.Cancel(runID);
    }

    /// <summary>The text queued to auto-send when this member's run ends;"" when none.</summary>
    public string QueuedSendText(MemberID agent) => queuedSends.GetValueOrDefault(agent) ?? "";

    /// <summary>Stops every running room: desk members now; rooms and the live
    /// stage join in later phases.</summary>
    public void StopAll()
    {
        foreach (var agent in BusyAgents.ToList()) Stop(agent);
        runner.CancelAll();
        StatusMessage = "已中止所有執行中的工作。";
    }

    // MARK: CLI statuses and quotas

    public void RefreshCLIStatuses()
    {
        var statuses = new Dictionary<Executor, OfficialCLIStatus>();
        foreach (var executor in ExecutorList.All)
        {
            statuses[executor] = executor is Executor.DeepSeek
                ? OfficialCLIStatus.NotConfirmed
                : OfficialCLIProbe.Check(executor, Archive.Settings);
            Program.Log($"probe {executor.ShortName()}: {statuses[executor]}");
        }
        OfficialCLIStatuses = statuses;
        OnPropertyChanged(nameof(OfficialCLIStatuses));
        UpdateAttentionItems();
    }

    /// <summary>Launches the official login flow in a visible terminal window;
    /// AIROOM never sees what the user types.</summary>
    public void OpenOfficialLogin(Executor executor)
    {
        var executable = CommandPlan.ResolveExecutable(executor, Archive.Settings);
        if (executable is null)
        {
            StatusMessage = NativeCLIError.ExecutableMissing(executor.ExecutableName()).Message;
            return;
        }
        try
        {
            var (fileName, arguments) = LoginScript.Command(executor, executable);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            StatusMessage = error.Message;
        }
    }

    // MARK: Attention (待你處理)

    List<AttentionItem> AttentionItems =>
        AttentionBuilder.Items(Archive, OfficialCLIStatuses.ToDictionary(
            pair => pair.Key, pair => pair.Value switch
            {
                OfficialCLIStatus.NotLoggedIn => "notLoggedIn",
                OfficialCLIStatus.MissingExecutable => "missingExecutable",
                _ => "",
            }));

    public List<AttentionItem> VisibleAttention() => AttentionBuilder.Visible(AttentionItems, Archive.AttentionDismissals);

    public void IgnoreAttention(AttentionItem item)
    {
        Archive.AttentionDismissals = AttentionBuilder.Dismissing(item, AttentionItems, Archive.AttentionDismissals);
        Persist();
        UpdateAttentionItems();
    }

    public void ShowIgnoredAttention()
    {
        Archive.AttentionDismissals = [];
        Persist();
        UpdateAttentionItems();
    }

    void UpdateAttentionItems()
    {
        allAttentionItems = AttentionItems;
        OnPropertyChanged(nameof(AttentionCount));
    }

    public int AttentionCount => AttentionBuilder.Visible(AttentionItems, Archive.AttentionDismissals).Count;

    // MARK: Updates

    public async Task CheckForUpdateAsync(bool manual = false)
    {
        if (manual) UpdateChecking = true;
        try
        {
            if (!manual && !UpdateCheck.IsDue(lastUpdateCheckAt)) return;
            if (!Archive.Settings.CheckForUpdates) return;
            lastUpdateCheckAt = DateTimeOffset.Now;
            var release = await UpdateCheck.FetchNewestAsync();
            if (release is not null && UpdateCheck.IsNewer(release.Version, CurrentVersion))
            {
                RunOnDispatcher(() => AvailableUpdate = release);
            }
            else if (manual)
            {
                RunOnDispatcher(() => UpdateStatus = $"已是最新版本（{CurrentVersion}）。");
            }
        }
        catch (Exception error)
        {
            if (manual) RunOnDispatcher(() => UpdateStatus = $"檢查更新失敗：{error.Message}");
        }
        finally
        {
            if (manual) RunOnDispatcher(() => UpdateChecking = false);
        }
    }

    public static string CurrentVersion
    {
        get
        {
            var version = typeof(WorkbenchModel).Assembly.GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    // MARK: Settings

    public void UpdateSettings(Action<NativeSettings> edit)
    {
        edit(Archive.Settings);
        ApplyMemberSettings();
        Persist();
        OnPropertyChanged(nameof(SettingsVersion));
    }

    public int SettingsVersion { get; private set; }

    // MARK: Persistence

    /// <summary>Coalesces bursts of writes; the state file is small but the
    /// attachments make an immediate save on every keystroke pointless.</summary>
    System.Timers.Timer? persistTimer;

    public void Persist()
    {
        if (Persistence is null || !StorageAvailable) return;
        if (persistTimer is null)
        {
            persistTimer = new System.Timers.Timer(400) { AutoReset = false };
            persistTimer.Elapsed += (_, _) =>
            {
                try
                {
                    lock (Archive)
                    {
                        Persistence.Save(Archive);
                    }
                }
                catch (Exception error)
                {
                    RunOnDispatcher(() => StatusMessage = $"儲存失敗：{error.Message}");
                }
            };
        }
        persistTimer.Stop();
        persistTimer.Start();
    }

    // MARK: helpers

    void RunOnDispatcher(Action action)
    {
        if (dispatcher.HasThreadAccess)
        {
            action();
            return;
        }
        _ = dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                // One failing callback must not leave the run bookkeeping or
                // the rendering half-done, invisibly.
                Program.Log($"dispatcher callback failed: {error}");
            }
        });
    }

    public event Action<string, string>? NotificationRequested;

    void Notify(string title, string body) => NotificationRequested?.Invoke(title, body);

    static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "……";

    static void TryDelete(string? directory)
    {
        if (directory is null) return;
        try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>What picture the avatar crop editor works on, with the crop that
/// was saved for it. The dialog computes the default anchor from the decoded
/// size, following macOS's avatarSource rules per kind.</summary>
public enum AvatarEditorSourceKind
{
    UploadedAvatar,
    UploadedPortrait,
    TemplateArt,
}

public sealed record AvatarEditorSource(
    byte[] Image,
    PortraitFaceAnchor? Saved,
    AvatarEditorSourceKind Kind,
    PortraitFaceAnchor? DefaultFace = null);
