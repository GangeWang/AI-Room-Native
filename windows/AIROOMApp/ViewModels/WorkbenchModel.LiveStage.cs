using AIROOMApp.Services;
using AIROOMCore;

namespace AIROOMApp.ViewModels;

/// <summary>
/// The live stage controller (ported from macOS LiveStageController): plays
/// LiveEvents one at a time — emotion hotkey → TTS with lip sync → next — and
/// drives each co-host's own VTube Studio instance (own port, own token, own
/// model). The live loop awaits <see cref="PerformAsync"/>, so two co-hosts
/// never talk over each other.
/// </summary>
public sealed partial class WorkbenchModel
{
    static readonly System.Diagnostics.Stopwatch StageClock = System.Diagnostics.Stopwatch.StartNew();
    static double StageNow() => StageClock.Elapsed.TotalSeconds;

    /// <summary>Waits before each automatic reconnect: quickly at first, then
    /// every 10 s for about five minutes.</summary>
    public static readonly double[] ReconnectDelays = [1, 2, 5, .. Enumerable.Repeat(10, 27)];

    readonly Dictionary<MemberID, string> stageConnections = [];   // "off/connecting/connected/reconnecting/failed"
    readonly Dictionary<MemberID, string> stageConnectionDetail = [];
    readonly Dictionary<MemberID, List<VTSHotkey>> stageHotkeys = [];
    readonly Dictionary<MemberID, List<VTSModel>> stageModels = [];
    readonly Dictionary<MemberID, VTubeStudioClient> stageClients = [];
    readonly Dictionary<MemberID, StageLink> stageLinks = [];
    readonly Dictionary<MemberID, CancellationTokenSource> stageRetries = [];
    readonly Dictionary<MemberID, CancellationTokenSource> stageRigTasks = [];
    readonly Dictionary<MemberID, LivePoseAnimator> stageAnimators = [];
    readonly Dictionary<MemberID, string> stageActiveExpression = [];
    readonly Dictionary<MemberID, LiveEmotion?> stageLastEmotion = [];
    readonly Dictionary<MemberID, string> stageRigStatus = [];
    readonly Dictionary<MemberID, string> stageRigTrace = [];
    readonly SpeechPlayer stageSpeech = new();
    readonly VTubeStudioLauncher stageLauncher = new();
    volatile bool stageStopped;

    int stageVersion;
    MemberID? stageSpeaking;
    string stageSubtitle = "";
    string? stageLastWarning;
    string? stageLaunchStatus;

    public int StageVersion => stageVersion;
    public MemberID? StageSpeaking => stageSpeaking;
    public string StageSubtitle => stageSubtitle;
    public string? StageLastWarning => stageLastWarning;
    public string? StageLaunchStatus => stageLaunchStatus;

    void BumpStage()
    {
        RunOnDispatcher(() =>
        {
            stageVersion++;
            OnPropertyChanged(nameof(StageVersion));
        });
    }

    public LiveStageSettings StageSettings()
    {
        lock (Archive) return Archive.LiveStage ?? new LiveStageSettings();
    }

    public void UpdateStage(Action<LiveStageSettings> change)
    {
        lock (Archive)
        {
            change(Archive.LiveStage ??= new LiveStageSettings());
        }
        Persist();
        BumpStage();
    }

    public void UpdateStageCharacter(MemberID agent, Action<LiveCharacterStage> change) =>
        UpdateStage(stage => stage.UpdateCharacter(agent, change));

    public (string Kind, string Detail) StageConnection(MemberID agent) =>
        (stageConnections.GetValueOrDefault(agent, "off"), stageConnectionDetail.GetValueOrDefault(agent, ""));

    public List<VTSHotkey> StageHotkeys(MemberID agent) => stageHotkeys.GetValueOrDefault(agent, []);
    public List<VTSModel> StageModels(MemberID agent) => stageModels.GetValueOrDefault(agent, []);
    public string StageRigStatus(MemberID agent) => stageRigStatus.GetValueOrDefault(agent, "");
    public bool StageConnected(MemberID agent) => StageConnection(agent).Kind == "connected";
    public bool StageBusy(MemberID agent) => StageConnection(agent).Kind is "connecting" or "reconnecting";
    public bool StageDrivesRig(MemberID agent) => stageRigTasks.ContainsKey(agent);

    /// <summary>Members whose own VTube Studio avatar the stage drives.</summary>
    public List<MemberID> AvatarMembers() =>
        EnabledMemberIDs.Where(agent => StageSettings().Character(agent).DrivesAvatar).ToList();

    // MARK: settings actions

    public void ConnectVTubeStudio(MemberID agent)
    {
        if (!StorageAvailable) return;
        var character = StageSettings().Character(agent);
        if (stageRetries.Remove(agent, out var pendingRetry)) pendingRetry.Cancel();
        stageLinks[agent] = new StageLink(character.VtsPort, character.VtsToken, character.VtsModelID,
            character.AiRig, character.MouthParameter);
        stageConnections[agent] = "connecting";
        stageConnectionDetail[agent] = $"連線中…若 VTube Studio 跳出「{VTSProtocol.PluginName(agent)}」授權視窗，請按 Allow";
        BumpStage();
        _ = Task.Run(async () =>
        {
            try { await OpenStageAsync(agent, mayAsk: true); }
            catch (Exception error)
            {
                if (stageLinks.ContainsKey(agent))
                {
                    stageConnections[agent] = "failed";
                    stageConnectionDetail[agent] = error.Message;
                    BumpStage();
                }
            }
        });
    }

    public void DisconnectVTubeStudio(MemberID agent)
    {
        stageLinks.Remove(agent);
        StopStageRig(agent);
        if (stageRetries.Remove(agent, out var activeRetry)) activeRetry.Cancel();
        stageActiveExpression.Remove(agent);
        stageHotkeys.Remove(agent);
        stageConnections[agent] = "off";
        stageConnectionDetail[agent] = "";
        if (stageClients.TryGetValue(agent, out var client))
        {
            _ = Task.Run(client.Disconnect);
        }
        BumpStage();
    }

    public void ForgetStageToken(MemberID agent)
    {
        DisconnectVTubeStudio(agent);
        UpdateStageCharacter(agent, character => character.VtsToken = "");
    }

    /// <summary>Switches the member's VTS to `modelID` now (when connected) and keeps it for later connects.</summary>
    public void ChooseStageModel(MemberID agent, VTSModel? model)
    {
        UpdateStageCharacter(agent, character =>
        {
            character.VtsModelID = model?.Id ?? "";
            character.VtsModelName = model?.Name ?? "";
        });
        if (model is { Id.Length: > 0 } && StageConnected(agent) && stageClients.TryGetValue(agent, out var client))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadStageModelIfNeededAsync(agent, model.Id, client, null, CancellationToken.None);
                    await RefreshStageHotkeysAsync(agent);
                }
                catch (Exception error) { StageWarning(error.Message); }
            });
        }
    }

    /// <summary>Turns the agent's rig control on or off (and picks up a new mouth input).</summary>
    public void SetStageRig(MemberID agent, bool on)
    {
        UpdateStageCharacter(agent, character => character.AiRig = on);
        var character = StageSettings().Character(agent);
        if (stageLinks.TryGetValue(agent, out var link))
        {
            stageLinks[agent] = link with { AiRig = on, MouthParameter = character.MouthParameter };
        }
        if (on && StageConnected(agent)) StartStageRig(agent);
        else StopStageRig(agent);
        BumpStage();
    }

    public async Task RefreshStageHotkeysAsync(MemberID agent)
    {
        if (!stageClients.TryGetValue(agent, out var client)) return;
        try { stageHotkeys[agent] = await client.HotkeysAsync(); }
        catch (Exception error) { StageWarning(error.Message); }
        BumpStage();
    }

    public async Task RefreshStageModelsAsync(MemberID agent)
    {
        if (!stageClients.TryGetValue(agent, out var client)) return;
        try { stageModels[agent] = await client.AvailableModelsAsync(); }
        catch (Exception error) { StageWarning(error.Message); }
        BumpStage();
    }

    /// <summary>Fills emotion → hotkey from hotkey names; returns how many were mapped.</summary>
    public int AutoMapStageEmotions(MemberID agent)
    {
        var found = LiveHotkeyMatcher.Match(StageHotkeys(agent));
        if (found.Count == 0) return 0;
        UpdateStageCharacter(agent, character =>
        {
            foreach (var (emotion, id) in found) character.EmotionHotkeys[emotion] = id;
        });
        return found.Count;
    }

    /// <summary>Opens VTube Studio for each avatar member, lowest port first so
    /// ports come out the same every time, then connects them.</summary>
    public async Task LaunchVTubeStudioAsync()
    {
        if (!StorageAvailable || !StageSettings().VtsEnabled) return;
        var members = AvatarMembers();
        if (members.Count == 0) return;
        var stage = StageSettings();
        var (exe, reason) = VTubeStudioLauncher.FindExecutable(Archive.Settings.VtsPath);
        if (exe is null)
        {
            StageWarning(reason ?? "找不到 VTube Studio。");
            return;
        }
        StageWarning(null);
        stageLaunchStatus = "檢查 VTube Studio…";
        BumpStage();
        try
        {
            await stageLauncher.StartAsync([.. members.Select(agent => stage.Character(agent).VtsPort)], exe,
                port =>
                {
                    stageLaunchStatus = $"正在開啟 VTube Studio（連接埠 {port}）…";
                    BumpStage();
                });
            stageLaunchStatus = null;
            foreach (var agent in members.Where(agent => !StageConnected(agent) && !StageBusy(agent)))
            {
                ConnectVTubeStudio(agent);
            }
        }
        catch (Exception error)
        {
            stageLaunchStatus = null;
            StageWarning(error.Message);
        }
        BumpStage();
    }

    void StageWarning(string? message)
    {
        stageLastWarning = message;
        BumpStage();
    }

    // MARK: connections

    sealed record StageLink(int Port, string Token, string ModelID, bool AiRig, string MouthParameter);

    VTubeStudioClient StageClientFor(MemberID agent)
    {
        if (stageClients.TryGetValue(agent, out var existing)) return existing;
        var client = new VTubeStudioClient();
        client.ConnectionLost += () =>
        {
            _ = Task.Run(() => StageConnectionLost(agent));
        };
        stageClients[agent] = client;
        return client;
    }

    /// <summary>Connects, authenticates, puts on the member's model and reads its hotkeys.</summary>
    async Task OpenStageAsync(MemberID agent, bool mayAsk)
    {
        if (!stageLinks.TryGetValue(agent, out var link)) return;
        var client = StageClientFor(agent);
        stageActiveExpression.Remove(agent);
        var result = await client.ConnectAsync(link.Port, link.Token, VTSProtocol.PluginName(agent), mayAsk);
        if (!stageLinks.TryGetValue(agent, out var current)) { client.Disconnect(); return; }
        if (result.Token != current.Token)
        {
            stageLinks[agent] = current with { Token = result.Token };
            UpdateStageCharacter(agent, character => character.VtsToken = result.Token);
        }
        await RefreshStageModelsAsync(agent);
        var wanted = stageLinks.TryGetValue(agent, out var refreshed) ? refreshed.ModelID : "";
        await LoadStageModelIfNeededAsync(agent, wanted, client, result.Model, CancellationToken.None);
        await RefreshStageHotkeysAsync(agent);
        if (stageLinks.TryGetValue(agent, out var settled) && settled.AiRig)
        {
            await CreateStageArmInputsAsync(agent, client);
            StartStageRig(agent);
        }
        BumpStage();
    }

    /// <summary>The arm inputs live in VTS as this member's custom parameters;
    /// the model's parameter settings map them onto its arm rig.</summary>
    async Task CreateStageArmInputsAsync(MemberID agent, VTubeStudioClient client)
    {
        var names = new LivePoseAnimator.ArmInputs(agent);
        string[] explanations = ["AI Room：左上臂", "AI Room：右上臂", "AI Room：左手肘", "AI Room：右手肘"];
        foreach (var (name, text) in names.All.Zip(explanations))
        {
            try { await client.CreateParameterAsync(name, text, -1, 1, 0); }
            catch (Exception error) { StageWarning($"建立手臂參數 {name} 失敗：{error.Message}"); }
        }
    }

    async Task LoadStageModelIfNeededAsync(MemberID agent, string modelID, VTubeStudioClient client,
        VTSModel? known, CancellationToken token)
    {
        var model = known ?? await client.CurrentModelAsync(token);
        if (modelID.Length > 0 && model.Id != modelID)
        {
            var listed = StageModels(agent);
            if (listed.Count == 0 || listed.Any(item => item.Id == modelID))
            {
                stageActiveExpression.Remove(agent);
                // A freshly started VTS refuses while its startup model is still
                // loading (error 154), so retry for about 15 s.
                for (var attempt = 0; ; attempt++)
                {
                    try { await client.LoadModelAsync(modelID, token); break; }
                    catch (VTSErrorException error) when (error.Kind == VTSErrorKind.Api && attempt < 10)
                    {
                        if (attempt == 9)
                        {
                            throw new VTSErrorException(VTSErrorKind.Api,
                                error.Message.Contains("154")
                                    ? "VTube Studio 目前不能換模型，通常是 VTS 視窗裡有對話框或設定視窗開著（例如 Auto-Setup 詢問）。關掉後再按「連線」。"
                                    : error.Message);
                        }
                        await Task.Delay(1500, token);
                    }
                }
                // VTS answers before the new model has finished loading; wait for
                // it so the status and the hotkey list belong to the new model.
                model = await client.CurrentModelAsync(token);
                for (var attempt = 0; attempt < 20 && !(model.Loaded && model.Id == modelID); attempt++)
                {
                    await Task.Delay(500, token);
                    model = await client.CurrentModelAsync(token);
                }
            }
            else
            {
                StageWarning("這個 VTube Studio 裡找不到指定的模型，先沿用目前開著的模型。");
            }
        }
        // CurrentModelRequest can report a model's original name after it was
        // renamed in VTS; the model list carries the current one.
        var shown = StageModels(agent).FirstOrDefault(item => item.Id == model.Id)?.Name ?? model.Name;
        if (stageLinks.ContainsKey(agent))
        {
            stageConnections[agent] = "connected";
            stageConnectionDetail[agent] = model.Loaded ? shown : "";
        }
        BumpStage();
    }

    void StageConnectionLost(MemberID agent)
    {
        if (!stageLinks.ContainsKey(agent) || stageRetries.ContainsKey(agent)) return;
        StopStageRig(agent);
        stageActiveExpression.Remove(agent);
        stageHotkeys.Remove(agent);
        var attempts = new CancellationTokenSource();
        stageRetries[agent] = attempts;
        BumpStage();
        _ = Task.Run(async () =>
        {
            for (var index = 0; index < ReconnectDelays.Length && !attempts.IsCancellationRequested; index++)
            {
                if (!stageLinks.ContainsKey(agent)) return;
                stageConnections[agent] = "reconnecting";
                stageConnectionDetail[agent] = $"VTube Studio 斷線，自動重連中（第 {index + 1} 次）…";
                BumpStage();
                try { await Task.Delay(TimeSpan.FromSeconds(ReconnectDelays[index]), attempts.Token); }
                catch (OperationCanceledException) { return; }
                if (!stageLinks.ContainsKey(agent)) return;
                try
                {
                    await OpenStageAsync(agent, mayAsk: false);
                    if (stageRetries.Remove(agent, out var done)) done.Dispose();
                    return;
                }
                catch (VTSErrorException error) when (error.Kind == VTSErrorKind.NeedsApproval)
                {
                    stageConnections[agent] = "failed";
                    stageConnectionDetail[agent] = error.Message;
                    if (stageRetries.Remove(agent, out var done)) done.Dispose();
                    BumpStage();
                    return;
                }
                catch { /* keep retrying */ }
            }
            if (!stageLinks.ContainsKey(agent)) return;
            stageConnections[agent] = "failed";
            stageConnectionDetail[agent] = "VTube Studio 斷線，自動重連約 5 分鐘仍連不上。開好 VTS 後請按「連線」。";
            if (stageRetries.Remove(agent, out var finished)) finished.Dispose();
            BumpStage();
        }, CancellationToken.None);
    }

    // MARK: rig streaming

    void StartStageRig(MemberID agent)
    {
        if (stageRigTasks.ContainsKey(agent) || !stageClients.TryGetValue(agent, out var client)) return;
        stageAnimators[agent] = new LivePoseAnimator();
        var link = stageRigTasks[agent] = new CancellationTokenSource();
        var arms = new LivePoseAnimator.ArmInputs(agent);
        _ = Task.Run(async () =>
        {
            var tick = 0;
            while (!link.IsCancellationRequested)
            {
                if (!stageAnimators.TryGetValue(agent, out var animator) || !stageLinks.TryGetValue(agent, out var setting)) return;
                client.Inject(animator.Frame(StageNow(), setting.MouthParameter, arms));
                tick += 1;
                if (tick % 6 == 0)
                {
                    // Five times a second: what is sent, and what VTS says about it.
                    var headX = animator.Frame(StageNow()).First(item => item.Item1 == LivePoseAnimator.Input.HeadX).Item2;
                    var error = client.LastUnmatchedError;
                    stageRigStatus[agent] =
                        $"骨架串流：已送出 {client.InjectedFrames} 幀 · FaceAngleX {headX:F1} · 嘴 {animator.Mouth:F2} · {stageRigTrace.GetValueOrDefault(agent, "待機")}"
                        + (error is { Length: > 0 } text ? $" · {text}" : "");
                    BumpStage();
                }
                try { await Task.Delay(33, link.Token); }
                catch (OperationCanceledException) { return; }
            }
        }, CancellationToken.None);
    }

    void StopStageRig(MemberID agent)
    {
        if (stageRigTasks.Remove(agent, out var task)) task.Cancel();
    }

    // MARK: performance

    /// <summary>Stops the current line (used by 停止直播 and shutdown).</summary>
    public void StopStage()
    {
        stageStopped = true;
        stageSpeech.Stop();
    }

    public async Task PerformAsync(List<LiveEvent> events, LiveStageSettings stage, InterfaceLanguage language)
    {
        stageStopped = false;
        StageWarning(null);
        foreach (var evt in events.OrderBy(item => item.Sequence))
        {
            if (stageStopped) break;
            var character = stage.Character(evt.Speaker);
            var client = stage.VtsEnabled && character.DrivesAvatar && StageConnected(evt.Speaker)
                ? stageClients.GetValueOrDefault(evt.Speaker) : null;
            var rig = client is not null && StageDrivesRig(evt.Speaker);
            if (client is not null) await ShowStageEmotionAsync(evt.Emotion, character, client);
            if (rig && (evt.Pose is not null || evt.Emotion != stageLastEmotion.GetValueOrDefault(evt.Speaker)))
            {
                var pose = evt.Pose ?? LivePose.Preset(evt.Emotion);
                if (stageAnimators.GetValueOrDefault(evt.Speaker) is { } posing) posing.Apply(pose, StageNow());
                stageRigTrace[evt.Speaker] = $"第 {evt.Sequence + 1} 段 → head {pose.HeadX switch { null => "-", { } v => ((int)v).ToString() }}";
            }
            else
            {
                stageRigTrace[evt.Speaker] = $"第 {evt.Sequence + 1} 段 · rig={rig} client={client is not null} 連線={StageConnected(evt.Speaker)}";
            }
            stageLastEmotion[evt.Speaker] = evt.Emotion;
            stageSpeaking = evt.Speaker;
            stageSubtitle = evt.Utterance;
            BumpStage();
            if (!stage.SpeechEnabled)
            {
                // Without a voice, still give each pose time to be seen.
                if (rig) await Task.Delay(1200);
                continue;
            }
            var agent = evt.Speaker;
            await stageSpeech.SpeakAsync(evt.Utterance, character, language, value =>
            {
                if (stageAnimators.GetValueOrDefault(agent) is { } animator) animator.Mouth = value;
                else client?.Inject([(character.MouthParameter, value)]);
            });
        }
        // Close the mouth and settle back to rest after the line.
        foreach (var agent in events.Select(item => item.Speaker).Distinct().Where(StageDrivesRig))
        {
            if (stageAnimators.TryGetValue(agent, out var animator))
            {
                animator.Mouth = 0;
                animator.Relax(StageNow() + 0.6, 1.2);
            }
            stageLastEmotion.Remove(agent);
        }
        stageSpeaking = null;
        BumpStage();
    }

    /// <summary>Clears any expression left on by the previous show.</summary>
    public async Task ResetStageAvatarsAsync()
    {
        foreach (var (agent, active) in stageActiveExpression.ToList())
        {
            stageActiveExpression.Remove(agent);
            if (stageClients.TryGetValue(agent, out var client))
            {
                try { await client.TriggerAsync(active); }
                catch { /* the link may already be down */ }
            }
        }
    }

    /// <summary>Tries the voice and avatar with a scripted line (or a custom one).</summary>
    public void RehearseStage(MemberID agent, string name, string script = "")
    {
        if (LiveActive) return;
        var custom = script.Trim();
        if (custom.Length > 2_000) custom = custom[..2_000];
        var language = Archive.Settings.Language;
        var line = custom.Length > 0 ? custom : language == InterfaceLanguage.English
            ? "[happy] Hi everyone, I'm " + name + "! [thinking] [pose head=-14,6,10 gaze=up arms=0.8,0 elbows=0.8,0 in=0.7] This is a voice and expression test. [pose head=16,-2,-6 gaze=left smile=0.6 arms=0,0.8 elbows=0,-0.8 in=0.5] Over here, [surprised] can you see my mouth move? [shy] [pose head=8,-8,-10 gaze=down smile=0.8 arms=-0.6,-0.6 elbows=1,1 in=0.8] Thanks for watching."
            : "[happy] 大家好，我是" + name + "！[thinking] [pose head=-14,6,10 gaze=up arms=0.8,0 elbows=0.8,0 in=0.7] 這是聲音和表情的測試。[pose head=16,-2,-6 gaze=left smile=0.6 arms=0,0.8 elbows=0,-0.8 in=0.5] 往這邊看，[surprised] 有看到我的嘴巴在動嗎？[shy] [pose head=8,-8,-10 gaze=down smile=0.8 arms=-0.6,-0.6 elbows=1,1 in=0.8] 謝謝收看。";
        var stage = StageSettings();
        var events = LiveCueParser.Events(LiveCueParser.Parse(line), agent);
        _ = Task.Run(async () =>
        {
            await PerformAsync(events, stage, language);
            await ResetStageAvatarsAsync();
        });
    }

    async Task ShowStageEmotionAsync(LiveEmotion emotion, LiveCharacterStage character, VTubeStudioClient client)
    {
        var agent = character.Member();
        var target = emotion == LiveEmotion.Neutral ? null : character.EmotionHotkeys.GetValueOrDefault(emotion.ToString().ToLowerInvariant());
        var current = stageActiveExpression.GetValueOrDefault(agent);
        if (target == current) return;
        bool Toggles(string id) => StageHotkeys(agent).FirstOrDefault(hotkey => hotkey.Id == id)?.Toggles == true;
        try
        {
            if (current is { } active && Toggles(active))
            {
                await client.TriggerAsync(active); // switch the old expression off
            }
            stageActiveExpression.Remove(agent);
            if (target is { } wanted)
            {
                await client.TriggerAsync(wanted);
                if (Toggles(wanted)) stageActiveExpression[agent] = wanted;
            }
        }
        catch (Exception error)
        {
            // A dropped link reconnects by itself (see StageConnectionLost).
            StageWarning(error.Message);
        }
    }
}
