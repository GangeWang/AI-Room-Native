using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIROOMCore;

// MARK: Live stage — the performer layer behind the text show. Ported from
// macOS LiveStage.swift + LivePoseAnimator.swift: pose keyframes an agent
// writes inline, the persisted per-character stage settings (voice, VTube
// Studio port/token/model), hotkey auto-matching and the lip-sync envelope.

/// <summary>A pose keyframe an agent writes inline, e.g.
/// `[pose head=-12,6,8 gaze=left eyes=0.9 brows=0.7 smile=0.8 in=0.6]`.
/// Every field is optional (null keeps the current value) and clamped; the
/// values are VTube Studio tracking inputs, so the model's own parameter
/// mapping decides what actually moves.</summary>
public sealed record LivePose
{
    public double? HeadX { get; init; }
    public double? HeadY { get; init; }
    public double? HeadZ { get; init; }
    public double? GazeX { get; init; }
    public double? GazeY { get; init; }
    public double? Eyes { get; init; }
    public double? Brows { get; init; }
    public double? Smile { get; init; }
    /// <summary>-1…1 per side: upper arm (shoulder) and forearm (elbow), sent
    /// as the member's own VTS custom inputs.</summary>
    public double? ArmL { get; init; }
    public double? ArmR { get; init; }
    public double? ElbowL { get; init; }
    public double? ElbowR { get; init; }
    /// <summary>Seconds to move into this pose.</summary>
    public double Duration { get; init; } = DefaultDuration;

    public const double DefaultDuration = 0.5;

    public LivePose() { }

    public LivePose(double? headX = null, double? headY = null, double? headZ = null, double? gazeX = null,
        double? gazeY = null, double? eyes = null, double? brows = null, double? smile = null,
        double? armL = null, double? armR = null, double? elbowL = null, double? elbowR = null,
        double duration = DefaultDuration)
    {
        HeadX = Clamp(headX, -30, 30); HeadY = Clamp(headY, -20, 20); HeadZ = Clamp(headZ, -30, 30);
        GazeX = Clamp(gazeX, -1, 1); GazeY = Clamp(gazeY, -1, 1);
        Eyes = Clamp(eyes, 0, 1); Brows = Clamp(brows, 0, 1); Smile = Clamp(smile, 0, 1);
        ArmL = Clamp(armL, -1, 1); ArmR = Clamp(armR, -1, 1);
        ElbowL = Clamp(elbowL, -1, 1); ElbowR = Clamp(elbowR, -1, 1);
        Duration = Clamp(duration, 0.1, 4) ?? DefaultDuration;
    }

    static double? Clamp(double? value, double low, double high) =>
        value is { } number && !double.IsNaN(number) && !double.IsInfinity(number)
            ? Math.Min(high, Math.Max(low, number)) : null;

    public bool IsEmpty =>
        HeadX is null && HeadY is null && HeadZ is null && GazeX is null && GazeY is null
        && Eyes is null && Brows is null && Smile is null
        && ArmL is null && ArmR is null && ElbowL is null && ElbowR is null;

    /// <summary>Parses the inside of a `[pose …]` tag. Unknown keys and bad
    /// numbers are dropped; a tag with nothing usable gives null.</summary>
    public static LivePose? Parse(string arguments)
    {
        double? headX = null, headY = null, headZ = null, gazeX = null, gazeY = null;
        double? eyes = null, brows = null, smile = null, armL = null, armR = null, elbowL = null, elbowR = null;
        var duration = DefaultDuration;
        double?[] Numbers(string text) => text.Split(',').Select(part =>
            double.TryParse(part.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? (double?)number : null).ToArray();
        foreach (var pair in arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(12))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            var key = parts[0].ToLowerInvariant();
            var value = parts[1];
            switch (key)
            {
                case "head":
                    var head = Numbers(value);
                    if (head.Length > 0) headX = head[0];
                    if (head.Length > 1) headY = head[1];
                    if (head.Length > 2) headZ = head[2];
                    break;
                case "gaze":
                    switch (value.ToLowerInvariant())
                    {
                        case "left": gazeX = -0.8; gazeY = 0; break;
                        case "right": gazeX = 0.8; gazeY = 0; break;
                        case "up": gazeX = 0; gazeY = 0.8; break;
                        case "down": gazeX = 0; gazeY = -0.8; break;
                        case "center" or "camera" or "viewer": gazeX = 0; gazeY = 0; break;
                        default:
                            var gaze = Numbers(value);
                            if (gaze.Length > 0) gazeX = gaze[0];
                            if (gaze.Length > 1) gazeY = gaze[1];
                            break;
                    }
                    break;
                case "eyes": eyes = ParseOne(value); break;
                case "brows": brows = ParseOne(value); break;
                case "smile": smile = ParseOne(value); break;
                case "arms":
                    var arms = Numbers(value);
                    if (arms.Length > 0) armL = arms[0];
                    if (arms.Length > 1) armR = arms[1];
                    break;
                case "elbows":
                    var elbows = Numbers(value);
                    if (elbows.Length > 0) elbowL = elbows[0];
                    if (elbows.Length > 1) elbowR = elbows[1];
                    break;
                case "arml": armL = ParseOne(value); break;
                case "armr": armR = ParseOne(value); break;
                case "elbowl": elbowL = ParseOne(value); break;
                case "elbowr": elbowR = ParseOne(value); break;
                case "in":
                    duration = double.TryParse(value.TrimEnd('s'), System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                        ? seconds : duration;
                    break;
            }
        }
        var pose = new LivePose(headX, headY, headZ, gazeX, gazeY, eyes, brows, smile,
            armL, armR, elbowL, elbowR, duration);
        return pose.IsEmpty ? null : pose;
    }

    static double? ParseOne(string value) =>
        double.TryParse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number : null;

    /// <summary>What the rig does for a bare emotion tag with no pose of its own.</summary>
    public static LivePose Preset(LiveEmotion emotion) => emotion switch
    {
        LiveEmotion.Neutral => new(0, 0, 0, 0, 0, 1, 0.5, 0.35, 0, 0, 0, 0, 0.6),
        LiveEmotion.Happy => new(headY: 3, headZ: 4, eyes: 1, brows: 0.65, smile: 0.9, armL: 0, armR: 0, elbowL: 0, elbowR: 0, duration: 0.5),
        LiveEmotion.Excited => new(headY: 6, headZ: -5, eyes: 1, brows: 0.85, smile: 1, armL: 0.7, armR: 0.7, elbowL: 0.6, elbowR: 0.6, duration: 0.3),
        LiveEmotion.Thinking => new(-8, 4, 9, 0.5, 0.6, 0.8, 0.6, 0.3, 0, 0.2, 0, 1, 0.8),
        LiveEmotion.Surprised => new(headY: 5, eyes: 1, brows: 1, smile: 0.4, armL: 0.3, armR: 0.3, elbowL: 0.9, elbowR: 0.9, duration: 0.2),
        LiveEmotion.Sad => new(headY: -8, headZ: -4, gazeY: -0.5, eyes: 0.7, brows: 0.2, smile: 0.1, armL: 0, armR: 0, elbowL: 0, elbowR: 0, duration: 1.0),
        LiveEmotion.Angry => new(headY: -4, eyes: 0.85, brows: 0, smile: 0.05, armL: 0, armR: 0, elbowL: 0, elbowR: 0, duration: 0.4),
        _ => new(10, -6, -8, -0.6, -0.4, 0.75, 0.55, 0.7, 0, 0, 1, 1, 0.7),
    };
}

/// <summary>Turns pose keyframes into a steady stream of VTube Studio tracking
/// inputs, so an agent can drive a model's rig the way a face tracker would:
/// smooth easing between keyframes plus an idle layer (blinking, breathing, a
/// slight sway). Mouth openness comes from the voice. Everything is a pure
/// function of time, so it is deterministic and testable.</summary>
public sealed class LivePoseAnimator
{
    /// <summary>VTube Studio default tracking inputs this animator writes.</summary>
    public static class Input
    {
        public const string HeadX = "FaceAngleX", HeadY = "FaceAngleY", HeadZ = "FaceAngleZ";
        public const string EyeOpenLeft = "EyeOpenLeft", EyeOpenRight = "EyeOpenRight";
        public const string EyeLeftX = "EyeLeftX", EyeLeftY = "EyeLeftY";
        public const string EyeRightX = "EyeRightX", EyeRightY = "EyeRightY";
        public const string Brows = "Brows", Smile = "MouthSmile";
    }

    /// <summary>The member's own VTS custom inputs for the arms (VTS requires
    /// unique 4–32 character alphanumeric names; each member gets its own set).</summary>
    public sealed record ArmInputs
    {
        public readonly string ArmL, ArmR, ElbowL, ElbowR;
        public ArmInputs(MemberID member)
        {
            var tag = new string(member.Handle.Where(character => char.IsAsciiLetterOrDigit(character)).Take(14).ToArray());
            var name = "AIROOM" + (tag.Length == 0 ? "member" : tag);
            ArmL = name + "ArmL"; ArmR = name + "ArmR"; ElbowL = name + "ElbowL"; ElbowR = name + "ElbowR";
        }
        public string[] All => [ArmL, ArmR, ElbowL, ElbowR];
    }

    struct Values
    {
        public double HeadX, HeadY, HeadZ, GazeX, GazeY, Eyes, Brows, Smile, ArmL, ArmR, ElbowL, ElbowR;

        public Values() { Eyes = 1; Brows = 0.5; Smile = 0.35; }

        public Values(double headX, double headY, double headZ, double gazeX, double gazeY,
            double eyes, double brows, double smile, double armL, double armR, double elbowL, double elbowR)
        {
            HeadX = headX; HeadY = headY; HeadZ = headZ; GazeX = gazeX; GazeY = gazeY;
            Eyes = eyes; Brows = brows; Smile = smile; ArmL = armL; ArmR = armR; ElbowL = elbowL; ElbowR = elbowR;
        }

        public Values Applying(LivePose pose)
        {
            var next = this;
            if (pose.HeadX is { } v) next.HeadX = v;
            if (pose.HeadY is { } v2) next.HeadY = v2;
            if (pose.HeadZ is { } v3) next.HeadZ = v3;
            if (pose.GazeX is { } v4) next.GazeX = v4;
            if (pose.GazeY is { } v5) next.GazeY = v5;
            if (pose.Eyes is { } v6) next.Eyes = v6;
            if (pose.Brows is { } v7) next.Brows = v7;
            if (pose.Smile is { } v8) next.Smile = v8;
            if (pose.ArmL is { } v9) next.ArmL = v9;
            if (pose.ArmR is { } v10) next.ArmR = v10;
            if (pose.ElbowL is { } v11) next.ElbowL = v11;
            if (pose.ElbowR is { } v12) next.ElbowR = v12;
            return next;
        }

        public static Values Blend(Values a, Values b, double t)
        {
            double Mix(double x, double y) => x + (y - x) * t;
            return new Values(Mix(a.HeadX, b.HeadX), Mix(a.HeadY, b.HeadY), Mix(a.HeadZ, b.HeadZ),
                Mix(a.GazeX, b.GazeX), Mix(a.GazeY, b.GazeY), Mix(a.Eyes, b.Eyes),
                Mix(a.Brows, b.Brows), Mix(a.Smile, b.Smile), Mix(a.ArmL, b.ArmL),
                Mix(a.ArmR, b.ArmR), Mix(a.ElbowL, b.ElbowL), Mix(a.ElbowR, b.ElbowR));
        }
    }

    Values from = new(), to = new();
    double start;
    double duration = 0.001;

    /// <summary>0…1, set from the voice's loudness.</summary>
    public double Mouth;
    /// <summary>Idle motion on/off (tests turn it off to check exact values).</summary>
    public bool Idle;

    public LivePoseAnimator(bool idle = true)
    {
        Idle = idle;
    }

    /// <summary>Starts moving from wherever the rig is now toward `pose`.</summary>
    public void Apply(LivePose pose, double time)
    {
        var current = ValuesAt(time);
        from = current;
        to = current.Applying(pose);
        start = time;
        duration = Math.Max(0.001, pose.Duration);
    }

    /// <summary>Eases back to the neutral resting pose.</summary>
    public void Relax(double time, double seconds = 1.0)
    {
        from = ValuesAt(time);
        to = new Values();
        start = time;
        duration = Math.Max(0.001, seconds);
    }

    Values ValuesAt(double time)
    {
        var raw = Math.Min(1, Math.Max(0, (time - start) / duration));
        var eased = raw * raw * (3 - 2 * raw); // smoothstep
        return Values.Blend(from, to, eased);
    }

    /// <summary>Eyelid factor for a natural blink every ~3–5 s (0 = shut).</summary>
    static double Blink(double time)
    {
        const double period = 4.2, length = 0.16;
        // Vary the gap a little so blinks do not tick like a metronome.
        var shifted = time + 0.9 * Math.Sin(time * 0.37);
        var phase = shifted % period;
        if (phase < 0 || phase >= length) return 1;
        return Math.Abs(phase / length * 2 - 1);
    }

    /// <summary>The tracking inputs to send to VTube Studio at `time`.</summary>
    public (string, double)[] Frame(double time, string mouthParameter = "MouthOpen", ArmInputs? arms = null)
    {
        var v = ValuesAt(time);
        var eyes = v.Eyes;
        if (Idle)
        {
            v.HeadY += 1.2 * Math.Sin(time * 2 * Math.PI / 4.5);              // breathing
            v.HeadX += 1.5 * Math.Sin(time * 0.53) + 0.8 * Math.Sin(time * 1.31); // sway
            v.HeadZ += 1.0 * Math.Sin(time * 0.41 + 1.7);
            eyes *= Blink(time);
            // Arms drift a little with the breath so they never look pinned.
            var breath = 0.04 * Math.Sin(time * 2 * Math.PI / 4.5);
            v.ArmL += breath; v.ArmR += breath;
        }
        var frame = new List<(string, double)>
        {
            (Input.HeadX, Math.Clamp(v.HeadX, -30, 30)), (Input.HeadY, Math.Clamp(v.HeadY, -20, 20)),
            (Input.HeadZ, Math.Clamp(v.HeadZ, -30, 30)),
            (Input.EyeOpenLeft, eyes), (Input.EyeOpenRight, eyes),
            (Input.EyeLeftX, v.GazeX), (Input.EyeRightX, v.GazeX),
            (Input.EyeLeftY, v.GazeY), (Input.EyeRightY, v.GazeY),
            (Input.Brows, v.Brows), (Input.Smile, v.Smile),
            (mouthParameter, Math.Clamp(Mouth, 0, 1)),
        };
        if (arms is not null)
        {
            frame.Add((arms.ArmL, Math.Clamp(v.ArmL, -1, 1)));
            frame.Add((arms.ArmR, Math.Clamp(v.ArmR, -1, 1)));
            frame.Add((arms.ElbowL, Math.Clamp(v.ElbowL, -1, 1)));
            frame.Add((arms.ElbowR, Math.Clamp(v.ElbowR, -1, 1)));
        }
        return [.. frame];
    }
}

/// <summary>Maps each emotion to the first hotkey whose name contains the
/// emotion's English tag or Chinese title ("開心 happy", "Happy face", "害羞"…).</summary>
public static class LiveHotkeyMatcher
{
    public static Dictionary<string, string> Match(IReadOnlyList<VTSHotkey> hotkeys)
    {
        var result = new Dictionary<string, string>();
        foreach (var emotion in Enum.GetValues<LiveEmotion>())
        {
            if (emotion == LiveEmotion.Neutral) continue;
            var hit = hotkeys.FirstOrDefault(hotkey =>
            {
                var name = hotkey.Name.ToLowerInvariant();
                return name.Contains(emotion.ToString().ToLowerInvariant()) || name.Contains(emotion.ZhTitle());
            });
            if (hit is not null) result[emotion.ToString().ToLowerInvariant()] = hit.Id;
        }
        return result;
    }
}

/// <summary>Lip sync envelope: per-window RMS loudness into 0…1 mouth
/// openness, normalised to the utterance's own peak.</summary>
public static class LipSync
{
    public static double[] MouthCurve(float[] rms, float gate = 0.08f)
    {
        var peak = rms.Length == 0 ? 0 : rms.Max();
        return rms.Select(level => Mouth(level, peak, gate)).ToArray();
    }

    /// <summary>One window's openness against the loudest window heard so far
    /// (used while audio is still streaming in and the final peak is unknown).</summary>
    public static double Mouth(float level, float peak, float gate = 0.08f)
    {
        if (peak <= 0) return 0;
        var relative = level / peak;
        return relative < gate ? 0 : Math.Min(1, Math.Pow(relative, 0.6));
    }
}

// MARK: Persisted stage configuration (inside the encrypted state file)

public sealed class LiveCharacterStage
{
    [JsonPropertyName("agent")] public string Agent { get; set; } = "";
    /// <summary>Voice identifier for the platform TTS; empty means the system
    /// default for the UI language.</summary>
    [JsonPropertyName("voiceIdentifier")] public string VoiceIdentifier { get; set; } = "";
    /// <summary>macOS AVSpeechUtterance rate (0.3–0.65); Windows maps it onto its
    /// own rate scale so the field keeps one meaning across platforms.</summary>
    [JsonPropertyName("speechRate")] public double SpeechRate { get; set; } = 0.5;
    [JsonPropertyName("pitch")] public double Pitch { get; set; } = 1.0;
    /// <summary>Whether this co-host has its own VTube Studio instance to drive.</summary>
    [JsonPropertyName("drivesAvatar")] public bool DrivesAvatar { get; set; }
    /// <summary>Each co-host has a separate VTS instance; each new instance takes
    /// the next free port from 8001 up, in the order they are started.</summary>
    [JsonPropertyName("vtsPort")] public int VtsPort { get; set; } = 1024;
    /// <summary>Plugin token issued by that VTS instance after the user clicks
    /// Allow. It only grants control of local VTS; stored with the encrypted state.</summary>
    [JsonPropertyName("vtsToken")] public string VtsToken { get; set; } = "";
    /// <summary>The VTS model this co-host wears. Instances share their settings,
    /// so both start on the same last-used model; this one is loaded on connect.</summary>
    [JsonPropertyName("vtsModelID")] public string VtsModelID { get; set; } = "";
    [JsonPropertyName("vtsModelName")] public string VtsModelName { get; set; } = "";
    /// <summary>The agent drives the rig (head, eyes, brows, smile) through VTS
    /// tracking inputs while connected, with idle motion between lines.</summary>
    [JsonPropertyName("aiRig")] public bool AiRig { get; set; } = true;
    [JsonPropertyName("mouthParameter")] public string MouthParameter { get; set; } = "MouthOpen";
    /// <summary>LiveEmotion raw value → VTube Studio hotkeyID. Only IDs reported
    /// by the currently loaded model are ever triggered.</summary>
    [JsonPropertyName("emotionHotkeys")] public Dictionary<string, string> EmotionHotkeys { get; set; } = [];

    public LiveCharacterStage() { }

    public LiveCharacterStage(MemberID agent, string voiceIdentifier = "", double speechRate = 0.5,
        double pitch = 1.0, bool drivesAvatar = false, int? vtsPort = null, string vtsToken = "",
        string vtsModelID = "", string vtsModelName = "", bool aiRig = true,
        string mouthParameter = "MouthOpen", Dictionary<string, string>? emotionHotkeys = null)
    {
        Agent = agent.RawValue;
        VoiceIdentifier = voiceIdentifier.Length > 200 ? voiceIdentifier[..200] : voiceIdentifier;
        SpeechRate = Math.Min(0.65, Math.Max(0.3, speechRate));
        Pitch = Math.Min(2.0, Math.Max(0.5, pitch));
        DrivesAvatar = drivesAvatar;
        VtsPort = Math.Min(65_535, Math.Max(1_024, vtsPort ?? DefaultPort(agent)));
        VtsToken = vtsToken.Length > 512 ? vtsToken[..512] : vtsToken;
        VtsModelID = vtsModelID.Length > 128 ? vtsModelID[..128] : vtsModelID;
        VtsModelName = vtsModelName.Length > 200 ? vtsModelName[..200] : vtsModelName;
        AiRig = aiRig;
        var mouth = new string(mouthParameter.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_').ToArray());
        MouthParameter = mouth.Length == 0 ? "MouthOpen" : mouth[..Math.Min(mouth.Length, 32)];
        EmotionHotkeys = (emotionHotkeys ?? [])
            .Where(pair => Enum.TryParse<LiveEmotion>(pair.Key, ignoreCase: true, out _) && pair.Value.Length <= 128)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    public MemberID Member() => new(Agent);

    /// <summary>GPT's model went live first, so its VTS instance usually owns
    /// 8001. Each instance takes the next port; added members start at 8003.</summary>
    public static int DefaultPort(MemberID agent) =>
        agent == MemberID.Codex ? 8001 : agent == MemberID.Claude ? 8002 : 8003;

    /// <summary>Re-normalises after any mutation so clamps always hold.</summary>
    public static LiveCharacterStage Normalize(LiveCharacterStage edited) => new(
        edited.Member(), edited.VoiceIdentifier, edited.SpeechRate, edited.Pitch, edited.DrivesAvatar,
        edited.VtsPort, edited.VtsToken, edited.VtsModelID, edited.VtsModelName, edited.AiRig,
        edited.MouthParameter, edited.EmotionHotkeys);
}

public sealed class LiveStageSettings
{
    [JsonPropertyName("speechEnabled")] public bool SpeechEnabled { get; set; }
    [JsonPropertyName("vtsEnabled")] public bool VtsEnabled { get; set; }
    [JsonPropertyName("characters")] public List<LiveCharacterStage> Characters { get; set; } = [];

    [JsonIgnore] public bool Performs => SpeechEnabled || VtsEnabled;

    public LiveStageSettings() { }

    public LiveStageSettings(bool speechEnabled, bool vtsEnabled, List<LiveCharacterStage> characters)
    {
        SpeechEnabled = speechEnabled;
        VtsEnabled = vtsEnabled;
        var unique = new List<LiveCharacterStage>();
        foreach (var item in characters.Where(item => !unique.Any(existing => existing.Agent == item.Agent)))
        {
            unique.Add(item);
        }
        Characters = unique;
    }

    public LiveCharacterStage Character(MemberID agent) =>
        Characters.FirstOrDefault(existing => existing.Agent == agent.RawValue)
            ?? new LiveCharacterStage(agent, drivesAvatar: true);

    public void UpdateCharacter(MemberID agent, Action<LiveCharacterStage> change)
    {
        if (Characters.All(existing => existing.Agent != agent.RawValue))
        {
            Characters.Add(new LiveCharacterStage(agent, drivesAvatar: true));
        }
        var index = Characters.FindIndex(existing => existing.Agent == agent.RawValue);
        if (index < 0) return;
        change(Characters[index]);
        Characters[index] = LiveCharacterStage.Normalize(Characters[index]);
    }
}
