using System.Text.Json;
using AIROOMCore;
using Xunit;

// Ported from macOS LivePoseTests + NativeVTSTests + LiveStageTests.

public class LivePoseTests
{
    [Fact]
    public void PosesClampAndDropUnknownKeys()
    {
        var pose = LivePose.Parse("head=99,5,10 bogus=3 eyes=1.7 smile=abc arms=0.8,0 elbows=0.8,0 in=0.8s");
        Assert.NotNull(pose);
        Assert.Equal(30, pose!.HeadX); // clamped from 99
        Assert.Equal(5, pose.HeadY);
        Assert.Equal(10, pose.HeadZ);
        Assert.Null(pose.Brows); // unknown key and bad numbers never invent values
        Assert.Equal(1, pose.Eyes); // clamped from 1.7
        Assert.Null(pose.Smile);
        Assert.Equal(0.8, pose.ArmL);
        Assert.Equal(0, pose.ArmR);
        Assert.Equal(0.8, pose.ElbowL);
        Assert.Equal(0, pose.ElbowR);
        Assert.Equal(0.8, pose.Duration); // trailing s stripped
    }

    [Fact]
    public void GazeAcceptsNamedDirections()
    {
        Assert.Equal(-0.8, LivePose.Parse("gaze=left")!.GazeX);
        Assert.Equal(0.8, LivePose.Parse("gaze=right")!.GazeX);
        Assert.Equal(0.8, LivePose.Parse("gaze=up")!.GazeY);
        Assert.Equal(-0.8, LivePose.Parse("gaze=down")!.GazeY);
        Assert.Equal(0, LivePose.Parse("gaze=center")!.GazeX);
        Assert.Equal(0, LivePose.Parse("gaze=center")!.GazeY);
    }

    [Fact]
    public void NothingUsableGivesNull()
    {
        Assert.Null(LivePose.Parse("bogus=3 head=abc"));
        Assert.Null(LivePose.Parse(""));
    }

    [Fact]
    public void PresetsCoverEveryEmotionAndNeutralResets()
    {
        foreach (var emotion in Enum.GetValues<LiveEmotion>())
        {
            Assert.False(LivePose.Preset(emotion).IsEmpty);
        }
        var neutral = LivePose.Preset(LiveEmotion.Neutral);
        Assert.Equal(0, neutral.HeadX);
        Assert.Equal(0, neutral.ArmL);
        Assert.Equal(0.35, neutral.Smile);
    }
}

public class LivePoseAnimatorTests
{
    [Fact]
    public void IdleOffFramesAreExactlyThePose()
    {
        var animator = new LivePoseAnimator(idle: false);
        animator.Apply(new LivePose(headX: 10, smile: 0.9, armL: 0.5), 0);
        var frame = animator.Frame(0.5, "MouthOpen", new LivePoseAnimator.ArmInputs(MemberID.Codex)).ToDictionary(item => item.Item1, item => item.Item2);
        Assert.Equal(10, frame["FaceAngleX"]);
        Assert.Equal(0.9, frame["MouthSmile"]);
        Assert.Equal(0.5, frame["AIROOMcodexArmL"]);
        Assert.Equal(1, frame["EyeOpenLeft"]); // no idle: no blink dims the eyes
        Assert.Equal(0, frame["MouthOpen"]); // mouth comes from the voice
    }

    [Fact]
    public void PosesEaseFromTheCurrentValues()
    {
        var animator = new LivePoseAnimator(idle: false);
        animator.Apply(new LivePose(headX: 10), 0);
        animator.Apply(new LivePose(headX: -10), 1); // continues from wherever it is
        var mid = animator.Frame(1.25).ToDictionary(item => item.Item1, item => item.Item2);
        Assert.Equal(0, mid["FaceAngleX"]); // halfway between 10 and -10
    }

    [Fact]
    public void IdleMotionNeverExceedsTheRanges()
    {
        var animator = new LivePoseAnimator(idle: true);
        animator.Apply(LivePose.Preset(LiveEmotion.Excited), 0);
        for (var time = 0.0; time < 30; time += 0.1)
        {
            var frame = animator.Frame(time).ToDictionary(item => item.Item1, item => item.Item2);
            Assert.InRange(frame["FaceAngleX"], -30, 30);
            Assert.InRange(frame["FaceAngleY"], -20, 20);
            Assert.InRange(frame["EyeOpenLeft"], 0, 1);
            Assert.InRange(frame["MouthOpen"], 0, 1);
        }
    }

    [Fact]
    public void RelaxReturnsToRest()
    {
        var animator = new LivePoseAnimator(idle: false);
        animator.Apply(new LivePose(headX: 20, armR: 1), 0);
        animator.Relax(0.2, 0.6);
        var frame = animator.Frame(1.0, arms: new LivePoseAnimator.ArmInputs(MemberID.Claude)).ToDictionary(item => item.Item1, item => item.Item2);
        Assert.Equal(0, frame["FaceAngleX"]);
        Assert.Equal(0, frame["AIROOMclaudeArmR"]);
    }

    [Fact]
    public void ArmInputsArePerMemberAndAlphanumeric()
    {
        Assert.Equal("AIROOMcodexArmL", new LivePoseAnimator.ArmInputs(MemberID.Codex).ArmL);
        Assert.Equal("AIROOMclaudeElbowR", new LivePoseAnimator.ArmInputs(MemberID.Claude).ElbowR);
        var custom = new LivePoseAnimator.ArmInputs(new MemberID("奇怪的 成員!"));
        Assert.All(custom.All, name => Assert.DoesNotContain(name, character => !char.IsAsciiLetterOrDigit(character)));
        Assert.Equal("AIROOMmemberArmL", new LivePoseAnimator.ArmInputs(new MemberID("!@")).ArmL);
    }
}

public class LiveCuePoseParserTests
{
    [Fact]
    public void PoseTagsAttachToTheNextSegmentAndAreNeverSpoken()
    {
        var parsed = LiveCueParser.Parse("[happy] 你好！[pose head=-14,6,10 arms=0.8,0 in=0.7] 這是測試。");
        Assert.DoesNotContain("pose", parsed.CleanText);
        Assert.Equal(2, parsed.Segments.Count);
        Assert.Null(parsed.Segments[0].Pose);
        Assert.NotNull(parsed.Segments[1].Pose);
        Assert.Equal(-14, parsed.Segments[1].Pose!.HeadX);
    }

    [Fact]
    public void InvalidPoseTagsDropEntirely()
    {
        var parsed = LiveCueParser.Parse("[pose bogus=1] 純文字。");
        Assert.Equal("純文字。", parsed.CleanText);
        Assert.All(parsed.Segments, segment => Assert.Null(segment.Pose));
    }

    [Fact]
    public void TwelveSegmentsFitNow()
    {
        var reply = string.Join(" ", Enumerable.Range(1, 12).Select(index => $"[happy] 第{index}段內容"));
        var parsed = LiveCueParser.Parse(reply);
        Assert.Equal(LiveCueParser.MaxSegments, parsed.Segments.Count);
        Assert.Equal(12, LiveCueParser.MaxSegments);
    }
}

public class LiveHotkeyMatcherTests
{
    [Fact]
    public void NamesMatchTheEnglishTagOrTheChineseTitle()
    {
        var hotkeys = new List<VTSHotkey>
        {
            new("h1", "Happy face", "ToggleExpression"),
            new("h2", "害羞 expression", "ToggleExpression"),
            new("h3", "Random effect", "PlayAnimation"),
        };
        var matched = LiveHotkeyMatcher.Match(hotkeys);
        Assert.Equal("h1", matched["happy"]);
        Assert.Equal("h2", matched["shy"]);
        Assert.False(matched.ContainsKey("neutral"));
    }
}

public class LipSyncTests
{
    [Fact]
    public void QuietWindowsGateShutAndLoudOnesFollowThePeak()
    {
        float[] rms = [0.01f, 0.5f, 0.1f];
        var curve = LipSync.MouthCurve(rms);
        Assert.Equal(0, curve[0]); // below the gate
        Assert.Equal(1, curve[1]); // the peak reads fully open
        Assert.Equal(0, LipSync.Mouth(0.03f, 0.5f)); // 0.06 of the peak is under the gate
        Assert.Equal(0, LipSync.Mouth(0.4f, 0));
    }
}

public class VTubeStudioClientTests
{
    /// <summary>A scripted transport: replies from `Reply`, and can start
    /// throwing on receive to simulate a dropped link.</summary>
    sealed class ScriptedTransport : IVTSTransport
    {
        public readonly List<string> Sent = [];
        public Func<string, string?>? Reply;
        public volatile bool FailReceive;
        readonly Queue<string> incoming = new();

        public void Dispose() { }
        public void Close() { }

        public Task SendAsync(string text, CancellationToken token = default)
        {
            Sent.Add(text);
            if (Reply?.Invoke(text) is { } response) lock (incoming) incoming.Enqueue(response);
            return Task.CompletedTask;
        }

        public Task<string> ReceiveAsync(CancellationToken token = default)
        {
            while (true)
            {
                lock (incoming) if (incoming.Count > 0) return Task.FromResult(incoming.Dequeue());
                if (FailReceive) throw new IOException("scripted drop");
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>Echoes every request with the response VTS would give, by message type.</summary>
    static ScriptedTransport Scripted(out VTubeStudioClient client)
    {
        var transport = new ScriptedTransport
        {
            Reply = sent =>
            {
                var element = JsonSerializer.Deserialize<JsonElement>(sent);
                var id = element.GetProperty("requestID").GetString()!;
                return element.GetProperty("messageType").GetString() switch
                {
                    "AuthenticationRequest" => JsonSerializer.Serialize(new { apiName = "", apiVersion = "", requestID = id, messageType = "AuthenticationResponse", data = new { authenticated = true } }),
                    "AuthenticationTokenRequest" => JsonSerializer.Serialize(new { apiName = "", apiVersion = "", requestID = id, messageType = "AuthenticationTokenResponse", data = new { authenticationToken = "issued-token" } }),
                    "CurrentModelRequest" => JsonSerializer.Serialize(new { apiName = "", apiVersion = "", requestID = id, messageType = "CurrentModelResponse", data = new { modelLoaded = true, modelID = "m1", modelName = "Model" } }),
                    "HotkeysInCurrentModelRequest" => JsonSerializer.Serialize(new { apiName = "", apiVersion = "", requestID = id, messageType = "HotkeysInCurrentModelResponse", data = new { availableHotkeys = new[] { new { hotkeyID = "hk", name = "Happy face", type = "ToggleExpression" } } } }),
                    "HotkeyTriggerRequest" => JsonSerializer.Serialize(new { apiName = "", apiVersion = "", requestID = id, messageType = "HotkeyTriggerResponse" }),
                    "ModelLoadRequest" => JsonSerializer.Serialize(new { apiName = "", apiVersion = "", requestID = id, messageType = "ModelLoadResponse" }),
                    "ParameterCreationRequest" => JsonSerializer.Serialize(new { apiName = "", apiVersion = "", requestID = id, messageType = "ParameterCreationResponse" }),
                    _ => null,
                };
            },
        };
        client = new VTubeStudioClient { MakeTransport = _ => transport };
        return transport;
    }

    [Fact]
    public void PluginNamesArePerMemberAndAscii()
    {
        Assert.Equal("AIROOM Native (codex)", VTSProtocol.PluginName(MemberID.Codex));
        Assert.Equal("AIROOM Native (claude)", VTSProtocol.PluginName(MemberID.Claude));
        Assert.Equal("AIROOM Native (abc)", VTSProtocol.PluginName(new MemberID("abc 成員!")));
    }

    [Fact]
    public async Task ConnectReusesAStoredTokenWithoutAsking()
    {
        var transport = Scripted(out var client);
        var (token, model) = await client.ConnectAsync(8001, "stored", "AIROOM Native (codex)");
        Assert.Equal("stored", token);
        Assert.Equal("m1", model.Id);
        Assert.DoesNotContain(transport.Sent, sent => sent.Contains("AuthenticationTokenRequest"));
        client.Disconnect();
    }

    [Fact]
    public async Task ConnectWithNoTokenRequestsOneAndPersistsIt()
    {
        Scripted(out var client);
        var (token, _) = await client.ConnectAsync(8001, "", "AIROOM Native (codex)");
        Assert.Equal("issued-token", token);
        client.Disconnect();
    }

    [Fact]
    public async Task AutomaticReconnectsNeverShowTheApprovalDialog()
    {
        Scripted(out var client);
        var error = await Assert.ThrowsAsync<VTSErrorException>(() =>
            client.ConnectAsync(8001, "", "AIROOM Native (codex)", mayAsk: false));
        Assert.Equal(VTSErrorKind.NeedsApproval, error.Kind);
        client.Disconnect();
    }

    [Fact]
    public async Task InjectClampsValuesByPrefix()
    {
        var transport = Scripted(out var client);
        await client.ConnectAsync(8001, "stored", "AIROOM Native (codex)");
        client.Inject([("FaceAngleX", 100), ("MouthOpen", 2), ("AIROOMcodexArmL", -5), ("EyeLeftX", 0.5)]);
        var spin = 0;
        while (client.InjectedFrames == 0 && spin < 100) { await Task.Delay(20); spin++; }
        Assert.Equal(1, client.InjectedFrames);
        var inject = transport.Sent.First(sent => sent.Contains("InjectParameterDataRequest"));
        using var document = JsonDocument.Parse(inject);
        var values = document.RootElement.GetProperty("data").GetProperty("parameterValues");
        Assert.Equal(30, values.EnumerateArray().First(item => item.GetProperty("id").GetString() == "FaceAngleX").GetProperty("value").GetDouble());
        Assert.Equal(1, values.EnumerateArray().First(item => item.GetProperty("id").GetString() == "MouthOpen").GetProperty("value").GetDouble());
        Assert.Equal(-1, values.EnumerateArray().First(item => item.GetProperty("id").GetString() == "AIROOMcodexArmL").GetProperty("value").GetDouble());
        Assert.Equal(0.5, values.EnumerateArray().First(item => item.GetProperty("id").GetString() == "EyeLeftX").GetProperty("value").GetDouble());
        client.Disconnect();
    }

    [Fact]
    public async Task HotkeysFormTheTriggerWhitelist()
    {
        Scripted(out var client);
        await client.ConnectAsync(8001, "stored", "AIROOM Native (codex)");
        var hotkeys = await client.HotkeysAsync();
        Assert.Single(hotkeys);
        Assert.True(hotkeys[0].Toggles);
        await client.TriggerAsync("hk");
        await Assert.ThrowsAsync<VTSErrorException>(() => client.TriggerAsync("missing"));
        client.Disconnect();
    }

    [Fact]
    public async Task SelfDroppedLinksAnnounceThemselves()
    {
        var transport = Scripted(out var client);
        await client.ConnectAsync(8001, "stored", "AIROOM Native (codex)");
        var lost = new TaskCompletionSource();
        client.ConnectionLost += () => lost.TrySetResult();
        transport.FailReceive = true;
        var finished = await Task.WhenAny(lost.Task, Task.Delay(2000));
        Assert.Equal(lost.Task, finished);
        Assert.False(client.Authenticated);
    }

    [Fact]
    public async Task DisconnectDoesNotCountAsLost()
    {
        Scripted(out var client);
        await client.ConnectAsync(8001, "stored", "AIROOM Native (codex)");
        var lost = false;
        client.ConnectionLost += () => lost = true;
        client.Disconnect();
        await Task.Delay(300);
        Assert.False(lost);
    }
}

public class LiveStageSettingsTests
{
    [Fact]
    public void SettingsRoundTripThroughStateJson()
    {
        var state = new SavedState
        {
            LiveStage = new LiveStageSettings(true, true, [
                new LiveCharacterStage(MemberID.Codex, drivesAvatar: true, vtsPort: 8001, vtsToken: "t"),
                new LiveCharacterStage(MemberID.Claude, drivesAvatar: true, vtsPort: 8002),
            ]),
        };
        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<SavedState>(json)!;
        Assert.NotNull(restored.LiveStage);
        Assert.True(restored.LiveStage!.SpeechEnabled);
        Assert.Equal(2, restored.LiveStage.Characters.Count);
        Assert.Equal(8001, restored.LiveStage.Characters[0].VtsPort);
        Assert.Equal("t", restored.LiveStage.Characters[0].VtsToken);
        Assert.True(restored.LiveStage.Performs);
    }

    [Fact]
    public void PortsDefaultByMemberAndSettingsNormalize()
    {
        Assert.Equal(8001, LiveCharacterStage.DefaultPort(MemberID.Codex));
        Assert.Equal(8002, LiveCharacterStage.DefaultPort(MemberID.Claude));
        Assert.Equal(8003, LiveCharacterStage.DefaultPort(new MemberID("other")));
        var settings = new LiveStageSettings();
        settings.UpdateCharacter(MemberID.Codex, character =>
        {
            character.VtsPort = 7;         // clamped back up
            character.MouthParameter = "壞!"; // non-ASCII falls back to MouthOpen
            character.EmotionHotkeys["not-an-emotion"] = "x"; // dropped
            character.EmotionHotkeys["happy"] = "hk1";        // kept
        });
        var character = settings.Character(MemberID.Codex);
        Assert.Equal(1024, character.VtsPort);
        Assert.Equal("MouthOpen", character.MouthParameter);
        Assert.Equal("hk1", character.EmotionHotkeys["happy"]);
        Assert.False(character.EmotionHotkeys.ContainsKey("not-an-emotion"));
        // An untouched member reads back with drivesAvatar on, like macOS.
        Assert.True(settings.Character(MemberID.Claude).DrivesAvatar);
    }
}
