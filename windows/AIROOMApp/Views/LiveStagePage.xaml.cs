using System.ComponentModel;
using AIROOMApp.Controls;
using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIROOMApp.Views;

/// <summary>直播間: a bounded, manual co-host show inside the room it was started
/// from. Members take turns reading the shared transcript; the owner can stop
/// any time, and nothing resumes after the app exits. The TTS/VTube Studio
/// performer layer is a macOS-side extra and is not implemented here.</summary>
public sealed partial class LiveStagePage : Page
{
    string topicDraft = "";
    string audienceDraft = "";
    string audienceName = "";

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
    }

    void Render()
    {
        Root.Children.Clear();
        Root.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "LIVE", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange) },
                new TextBlock { Text = "雙 AI 同台直播", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                new TextBlock
                {
                    Text = "在目前茶歇室話題裡，成員依序輪流發言。逐輪可停止；關閉程式後不會自動續播；錯過的輪次不補。",
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });
        RenderStatus();
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
                Text = "未開播。設定主題後按「開始直播」；需要至少兩位已啟用、在場的成員。",
                FontSize = 13,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            var start = new Button
            {
                Content = "開始直播",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            start.Click += (_, _) => Model.StartLiveShow();
            panel.Children.Add(start);
        }
        Root.Children.Add(Panel(panel));
    }

    void RenderConfig()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "設定", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
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
            Text = "直播使用建立時所在的茶歇室話題；成員輪流唯讀發言，每 12 輪整理一次共識摘要。額度用盡的成員會讓直播暫停。",
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
