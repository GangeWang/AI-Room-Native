using System.ComponentModel;
using AIROOMApp.Controls;
using AIROOMApp.Services;
using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIROOMApp.Views;

/// <summary>直播間: a bounded, manual co-host show inside the room it was started
/// from, plus the 直播舞台 setup that turns replies into voice and drives each
/// member's VTube Studio avatar (ported from the macOS LiveStageView).</summary>
public sealed partial class LiveStagePage : Page
{
    string topicDraft = "";
    string audienceDraft = "";
    string audienceName = "";
    readonly Dictionary<MemberID, string> scriptDrafts = [];
    readonly Dictionary<MemberID, TextBlock> stageStatusLines = [];
    readonly Dictionary<MemberID, TextBlock> rigStatusLines = [];
    readonly Dictionary<MemberID, ContentPresenter> speakingBadges = [];
    SpeechPlayer.VoiceOption[]? voices;
    TextBlock? stageSubtitleText;

    public LiveStagePage()
    {
        InitializeComponent();
        topicDraft = Model.LiveConfig().Topic;
        Render();
        App.Model.PropertyChanged += OnModelPropertyChanged;
    }

    WorkbenchModel Model => App.Model;

    void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null
            or nameof(WorkbenchModel.AutomationVersion)
            or nameof(WorkbenchModel.RoomsVersion))
        {
            _ = DispatcherQueue.TryEnqueue(Render);
        }
        if (args.PropertyName is null or nameof(WorkbenchModel.StageVersion))
        {
            _ = DispatcherQueue.TryEnqueue(UpdateStageLines);
        }
    }

    void Render()
    {
        Root.Children.Clear();
        stageStatusLines.Clear();
        rigStatusLines.Clear();
        speakingBadges.Clear();
        Root.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "LIVE", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange) },
                new TextBlock { Text = "AI 直播", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                new TextBlock
                {
                    Text = "在目前茶歇室話題裡，成員依序輪流發言。逐輪可停止；關閉程式後不會自動續播；錯過的輪次不補。",
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });
        RenderStatus();
        RenderStageSetup();
        RenderConfig();
        RenderAudience();
        RenderTranscript();
    }

    void RenderStatus()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (Model.LiveActive)
        {
            panel.Children.Add(new ProgressRing { Width = 18, Height = 18, IsActive = true, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new TextBlock
            {
                Text = $"直播中 · 第 {Model.LiveTurns} 輪",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var stop = new Button { Content = "停止直播" };
            stop.Click += (_, _) => Model.StopLiveShow();
            panel.Children.Add(stop);
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "未開播。設定主題後按「開始 AI 直播」；一位成員可獨自開台，多位則輪流接話。",
                FontSize = 13,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            var start = new Button
            {
                Content = "開始 AI 直播",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            start.Click += (_, _) => Model.StartLiveShow();
            panel.Children.Add(start);
        }
        Root.Children.Add(Panel(panel));
    }

    // MARK: 直播舞台（TTS + VTube Studio）

    void RenderStageSetup()
    {
        var stage = Model.StageSettings();
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "直播舞台：語音與 VTube Studio", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        var speech = new ToggleSwitch { Header = "直播時用語音念出發言（Windows 內建 TTS）", IsOn = stage.SpeechEnabled };
        speech.Toggled += (_, _) => Model.UpdateStage(item => item.SpeechEnabled = speech.IsOn);
        panel.Children.Add(speech);
        panel.Children.Add(new TextBlock
        {
            Text = "想要更自然的中文聲音：系統設定 → 時間與語言 → 語音 → 新增語音，下載高品質語音後回到這裡選擇。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        var vts = new ToggleSwitch { Header = "連接 VTube Studio（每位成員一個 Live2D 形象）", IsOn = stage.VtsEnabled };
        vts.Toggled += (_, _) => Model.UpdateStage(item => item.VtsEnabled = vts.IsOn);
        panel.Children.Add(vts);
        var launchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var launch = new Button { Content = "開啟 VTube Studio", IsEnabled = stage.VtsEnabled && Model.AvatarMembers().Count > 0 };
        launch.Click += (_, _) => _ = Model.LaunchVTubeStudioAsync();
        launchRow.Children.Add(launch);
        if (Model.StageLaunchStatus is { } launchStatus)
        {
            launchRow.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
            launchRow.Children.Add(new TextBlock { Text = launchStatus, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        }
        panel.Children.Add(launchRow);
        if (Model.StageLastWarning is { } warning)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "⚠ " + warning,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Windows 上的 VTube Studio 是 Steam 版：第一次請先用 Steam 安裝並啟動過一次；「開啟 VTube Studio」會一個一個開（連接埠由小到大），開好自動連線。第一次使用要在 VTS 設定開啟「Start API」。VTS.exe 不在 Steam 資料夾時，可在「設定 → 工作台」填完整路徑。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "AI 只能從固定的 8 種情緒中選擇；App 只觸發你在下方對應、且目前模型確實存在的熱鍵。VTS 授權 Token 只存在本機加密資料中。聲音從系統預設輸出播放，OBS 擷取桌面音訊即可。",
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(Panel(panel));

        foreach (var agent in Model.EnabledMemberIDs)
        {
            Root.Children.Add(MemberStagePanel(agent, stage));
        }
    }

    UIElement MemberStagePanel(MemberID agent, LiveStageSettings stage)
    {
        var character = stage.Character(agent);
        var panel = new StackPanel { Spacing = 8 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(AvatarFor(agent, 30));
        head.Children.Add(new TextBlock
        {
            Text = Model.MemberName(agent),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var speaking = new ContentPresenter { Visibility = Model.StageSpeaking == agent ? Visibility.Visible : Visibility.Collapsed };
        speaking.Content = new TextBlock { Text = "發言中", Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        speakingBadges[agent] = speaking;
        head.Children.Add(speaking);
        var spacer = new Microsoft.UI.Xaml.Controls.Border { HorizontalAlignment = HorizontalAlignment.Stretch };
        head.Children.Add(spacer);
        var rehearse = new Button { Content = "試演", IsEnabled = !Model.LiveActive && stage.Performs };
        rehearse.Click += (_, _) => Model.RehearseStage(agent, Model.MemberName(agent), ScriptDraft(agent));
        head.Children.Add(rehearse);
        panel.Children.Add(head);

        var script = new TextBox
        {
            PlaceholderText = "自訂試演台詞（可留空；可用 [happy]、[pose head=10 arms=1,0] 等標籤）",
            Text = ScriptDraft(agent),
            AcceptsReturn = false,
        };
        script.TextChanged += (_, _) => scriptDrafts[agent] = script.Text;
        panel.Children.Add(script);

        panel.Children.Add(new ToggleSwitch
        {
            Header = "這位有自己的 VTube Studio 形象",
            IsOn = character.DrivesAvatar,
        });

        if (character.DrivesAvatar)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var port = new TextBox
            {
                Header = "VTS 連接埠",
                Text = character.VtsPort.ToString(),
                Width = 120,
            };
            port.LostFocus += (_, _) =>
            {
                if (int.TryParse(port.Text, out var value) && value != character.VtsPort)
                {
                    Model.UpdateStageCharacter(agent, item => item.VtsPort = value);
                }
            };
            row.Children.Add(port);
            var connect = new Button
            {
                Content = Model.StageConnected(agent) ? "重新連線" : "連線",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                IsEnabled = stage.VtsEnabled && !Model.StageBusy(agent),
            };
            connect.Click += (_, _) => Model.ConnectVTubeStudio(agent);
            row.Children.Add(connect);
            if (Model.StageConnected(agent) || Model.StageBusy(agent))
            {
                var hotkeys = new Button { Content = "重新讀取熱鍵" };
                hotkeys.Click += (_, _) => _ = Model.RefreshStageHotkeysAsync(agent);
                row.Children.Add(hotkeys);
                var disconnect = new Button { Content = "中斷" };
                disconnect.Click += (_, _) => Model.DisconnectVTubeStudio(agent);
                row.Children.Add(disconnect);
            }
            if (character.VtsToken.Length > 0)
            {
                var forget = new Button { Content = "撤銷授權" };
                forget.Click += (_, _) => Model.ForgetStageToken(agent);
                row.Children.Add(forget);
            }
            panel.Children.Add(row);

            var status = new TextBlock { FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, Text = ConnectionText(agent) };
            stageStatusLines[agent] = status;
            panel.Children.Add(status);

            var modelOptions = new List<(string Value, string Label)>
            {
                ("", "沿用 VTS 目前的模型"),
            };
            var models = Model.StageModels(agent);
            if (character.VtsModelID.Length > 0 && models.All(item => item.Id != character.VtsModelID))
            {
                modelOptions.Add((character.VtsModelID, (character.VtsModelName.Length == 0 ? character.VtsModelID[..8] : character.VtsModelName) + "（離線）"));
            }
            foreach (var item in models) modelOptions.Add((item.Id, item.Name));
            panel.Children.Add(Picker("模型", modelOptions, character.VtsModelID,
                value => Model.ChooseStageModel(agent, models.FirstOrDefault(item => item.Id == value)), 340));
            panel.Children.Add(new TextBlock
            {
                Text = "兩個 VTS 共用設定，開啟時會是同一個模型；連線後自動換成這裡選的。",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            panel.Children.Add(new ToggleSwitch
            {
                Header = "讓 AI 接管骨架（AI 用 [pose …] 標籤控制頭、眼、眉、笑、手；沒說話時自動眨眼呼吸）",
                IsOn = character.AiRig,
            });
            if (character.AiRig && Model.StageRigStatus(agent).Length > 0)
            {
                var rigStatus = new TextBlock
                {
                    FontSize = 11,
                    Opacity = 0.6,
                    FontFamily = new FontFamily("Consolas"),
                    Text = Model.StageRigStatus(agent),
                    TextWrapping = TextWrapping.Wrap,
                };
                rigStatusLines[agent] = rigStatus;
                panel.Children.Add(rigStatus);
            }

            var mouth = new TextBox { Header = "嘴型參數（VTS 預設輸入為 MouthOpen）", Text = character.MouthParameter, Width = 220 };
            mouth.LostFocus += (_, _) => Model.UpdateStageCharacter(agent, item => item.MouthParameter = mouth.Text.Trim());
            panel.Children.Add(mouth);

            panel.Children.Add(EmotionGrid(agent, character));
        }

        panel.Children.Add(VoicePanel(agent, character));

        return Panel(panel);
    }

    UIElement VoicePanel(MemberID agent, LiveCharacterStage character)
    {
        var panel = new StackPanel { Spacing = 6 };
        voices ??= SpeechPlayer.Voices();
        var options = new List<(string Value, string Label)> { ("", "系統預設") };
        foreach (var voice in voices) options.Add((voice.Id, $"{voice.Name} · {voice.Language}"));
        panel.Children.Add(Picker("聲音", options, character.VoiceIdentifier,
            value => Model.UpdateStageCharacter(agent, item => item.VoiceIdentifier = value), 340));
        var sliders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        sliders.Children.Add(RateSlider("語速", character.SpeechRate, 0.3, 0.65, value => Model.UpdateStageCharacter(agent, item => item.SpeechRate = value)));
        sliders.Children.Add(RateSlider("音高", character.Pitch, 0.5, 2.0, value => Model.UpdateStageCharacter(agent, item => item.Pitch = value)));
        panel.Children.Add(sliders);
        return panel;
    }

    UIElement RateSlider(string label, double value, double min, double max, Action<double> apply)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var readout = new TextBlock { Text = value.ToString("F2"), VerticalAlignment = VerticalAlignment.Center, Width = 34 };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = 0.01, Value = value, Width = 160 };
        slider.ValueChanged += (_, _) => readout.Text = slider.Value.ToString("F2");
        // Apply once on release so dragging does not fight a full re-render.
        slider.PointerCaptureLost += (_, _) => apply(Math.Round(slider.Value, 2));
        row.Children.Add(slider);
        row.Children.Add(readout);
        return row;
    }

    UIElement EmotionGrid(MemberID agent, LiveCharacterStage character)
    {
        var panel = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(new TextBlock { Text = "情緒 → 熱鍵", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var auto = new Button
        {
            Content = "依名稱自動對應",
            IsEnabled = Model.StageHotkeys(agent).Count > 0,
        };
        auto.Click += (_, _) => Model.AutoMapStageEmotions(agent);
        head.Children.Add(auto);
        panel.Children.Add(head);
        var hotkeys = Model.StageHotkeys(agent);
        foreach (var emotion in Enum.GetValues<LiveEmotion>())
        {
            if (emotion == LiveEmotion.Neutral) continue;
            var key = emotion.ToString().ToLowerInvariant();
            var options = new List<(string Value, string Label)> { ("", "不觸發") };
            if (character.EmotionHotkeys.TryGetValue(key, out var mapped) && hotkeys.All(item => item.Id != mapped))
            {
                options.Add((mapped, "（離線）" + mapped[..Math.Min(mapped.Length, 8)]));
            }
            foreach (var hotkey in hotkeys) options.Add((hotkey.Id, $"{hotkey.Name} · {hotkey.Type}"));
            panel.Children.Add(Picker(emotion.ZhTitle(), options, character.EmotionHotkeys.GetValueOrDefault(key, ""),
                value =>
                {
                    Model.UpdateStageCharacter(agent, item =>
                    {
                        if (value.Length == 0) item.EmotionHotkeys.Remove(key);
                        else item.EmotionHotkeys[key] = value;
                    });
                }, 300));
        }
        return panel;
    }

    string ConnectionText(MemberID agent)
    {
        var (kind, detail) = Model.StageConnection(agent);
        return kind switch
        {
            "connecting" => "⏳ " + detail,
            "reconnecting" => "↻ " + detail,
            "connected" => $"● 已連線 · 模型：{(detail.Length == 0 ? "（未載入）" : detail)} · {Model.StageHotkeys(agent).Count} 個熱鍵",
            "failed" => "✖ " + detail,
            _ => "○ 未連線",
        };
    }

    void UpdateStageLines()
    {
        foreach (var (agent, line) in stageStatusLines)
        {
            line.Text = ConnectionText(agent);
        }
        foreach (var (agent, line) in rigStatusLines)
        {
            line.Text = Model.StageRigStatus(agent);
        }
        foreach (var (agent, badge) in speakingBadges)
        {
            badge.Visibility = Model.StageSpeaking == agent ? Visibility.Visible : Visibility.Collapsed;
        }
        if (stageSubtitleText is not null)
        {
            var subtitle = Model.StageSubtitle;
            stageSubtitleText.Text = subtitle.Length == 0 ? "（尚未有人發言）" : subtitle;
        }
    }

    // MARK: the text show

    void RenderConfig()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "直播設定", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var topic = new TextBox
        {
            PlaceholderText = "直播主題",
            Text = topicDraft,
            IsEnabled = !Model.LiveActive,
        };
        topic.TextChanged += (_, _) => topicDraft = topic.Text;
        panel.Children.Add(topic);
        var config = Model.LiveConfig();

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        options.Children.Add(OptionPicker("輪間隔", new[] { 15, 30, 45, 60, 90, 120 },
            config.TurnDelaySeconds, value => Model.UpdateLiveConfig(live => live.TurnDelaySeconds = value),
            value => $"{value} 秒", !Model.LiveActive));
        options.Children.Add(OptionPicker("輪數上限", new[] { 2, 10, 20, 40, 80, 120 },
            config.MaxTurns, value => Model.UpdateLiveConfig(live => live.MaxTurns = value),
            value => $"{value} 輪", !Model.LiveActive));
        options.Children.Add(OptionPicker("時長上限", new[] { 10, 30, 60, 120, 180, 240 },
            config.MaxDurationMinutes, value => Model.UpdateLiveConfig(live => live.MaxDurationMinutes = value),
            value => $"{value} 分鐘", !Model.LiveActive));
        panel.Children.Add(options);
        panel.Children.Add(new TextBlock
        {
            Text = "直播使用建立時所在的茶歇室話題；成員輪流唯讀發言，每 12 輪整理一次共識摘要。額度用盡的成員會讓直播暫停。勾選語音或 VTube Studio 後，發言會念出並驅動形象，念完才輪到下一位。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(Panel(panel));
    }

    StackPanel OptionPicker(string label, int[] values, int current, Action<int> apply, Func<int, string> render, bool enabled)
    {
        var picker = new ComboBox { MinWidth = 130, IsEnabled = enabled };
        var best = 0;
        for (var index = 0; index < values.Length; index++)
        {
            picker.Items.Add(render(values[index]));
            if (Math.Abs(values[index] - current) < Math.Abs(values[best] - current)) best = index;
        }
        picker.SelectedIndex = best;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex is { } index && index >= 0) apply(values[index]);
        };
        return StackRow(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, picker);
    }

    void RenderAudience()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "觀眾留言", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var name = new TextBox { PlaceholderText = "暱稱（可留空）", Text = audienceName, MaxWidth = 220 };
        name.TextChanged += (_, _) => audienceName = name.Text;
        var input = new TextBox
        {
            PlaceholderText = "留言會成為下一輪發言可見的對話資料（1–500 字）",
            Text = audienceDraft,
            IsEnabled = Model.LiveActive,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        input.TextChanged += (_, _) => audienceDraft = input.Text;
        var send = new Button
        {
            Content = "送出留言",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = Model.LiveActive && audienceDraft.Trim().Length > 0,
        };
        send.Click += (_, _) =>
        {
            Model.EnqueueLiveAudienceMessage(audienceDraft, audienceName);
            audienceDraft = "";
            input.Text = "";
        };
        panel.Children.Add(StackRow(name, input, send));
        panel.Children.Add(new TextBlock
        {
            Text = "進行中的回覆不會被打斷；下一位發言者會看到新的留言。",
            FontSize = 12,
            Opacity = 0.75,
        });
        Root.Children.Add(Panel(panel));
    }

    void RenderTranscript()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "直播紀錄", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var roomID = Model.LiveConfig().RoomID;
        var room = roomID is { } id ? Model.RoomSnapshot(id) : null;
        if (room is null)
        {
            panel.Children.Add(new TextBlock { Text = "還沒有直播紀錄。", FontSize = 12, Opacity = 0.75 });
        }
        else
        {
            foreach (var entry in room.Entries.Where(item => item.Speaker != Speaker.System).TakeLast(30))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (entry.Agent is { } agent) row.Children.Add(AvatarFor(agent, 22));
                var body = new StackPanel { Spacing = 2 };
                body.Children.Add(new TextBlock { Text = entry.DisplayName, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                body.Children.Add(RichText.Block(entry.Body));
                row.Children.Add(body);
                panel.Children.Add(row);
            }
        }
        Root.Children.Add(Panel(panel));

        var subtitlePanel = new StackPanel { Spacing = 4 };
        subtitlePanel.Children.Add(new TextBlock { Text = "字幕（現在正在念的這句）", FontSize = 12, Opacity = 0.7 });
        stageSubtitleText = new TextBlock { Text = "（尚未有人發言）", FontSize = 14, TextWrapping = TextWrapping.Wrap };
        subtitlePanel.Children.Add(stageSubtitleText);
        Root.Children.Add(Panel(subtitlePanel));
    }

    string ScriptDraft(MemberID agent) => scriptDrafts.GetValueOrDefault(agent, "");

    ComboBox Picker(string label, IReadOnlyList<(string Value, string Label)> options, string current,
        Action<string> apply, double width = 260)
    {
        var picker = new ComboBox { Header = label, MinWidth = width, MaxWidth = 420 };
        var index = 0;
        for (var position = 0; position < options.Count; position++)
        {
            picker.Items.Add(options[position].Label);
            if (options[position].Value == current) index = position;
        }
        picker.SelectedIndex = index;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex is { } selected && selected >= 0) apply(options[selected].Value);
        };
        return picker;
    }

    static StackPanel StackRow(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    StudioAvatar AvatarFor(MemberID agent, double size)
    {
        var card = Model.Card(agent);
        var avatar = new StudioAvatar();
        var (art, crop) = Model.AvatarOrPortrait(card);
        avatar.Set(agent, Model.MemberName(agent), card?.Color, size, art, crop, null, null, false);
        return avatar;
    }

    static UIElement Panel(UIElement content) => new Border
    {
        Child = content,
        Padding = new Thickness(14),
        CornerRadius = new CornerRadius(12),
        Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
    };
}
