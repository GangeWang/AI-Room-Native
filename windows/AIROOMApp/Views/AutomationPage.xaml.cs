using System.ComponentModel;
using AIROOMApp.Controls;
using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIROOMApp.Views;

/// <summary>排程與自動對話: scheduled reminders and room messages, the daily
/// opening, and idle member small talk. Missed triggers are never replayed.
/// Ported from the macOS AutomationView.</summary>
public sealed partial class AutomationPage : Page
{
    Guid? editing;
    string titleDraft = "";
    string textDraft = "";
    ScheduleKind kind = ScheduleKind.Daily;
    ScheduleAction action = ScheduleAction.Reminder;
    int hour = 12;
    int minute = 0;
    int intervalMinutes = 60;
    DateTimeOffset onceAt = DateTimeOffset.Now.AddHours(1);
    MemberID? target;

    public AutomationPage()
    {
        InitializeComponent();
        Render();
        App.Model.PropertyChanged += OnModelPropertyChanged;
    }

    WorkbenchModel Model => App.Model;

    void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null
            or nameof(WorkbenchModel.AutomationVersion)
            or nameof(WorkbenchModel.RoomsVersion)
            or nameof(WorkbenchModel.StatusMessage))
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
                new TextBlock { Text = "AUTOMATION", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange) },
                new TextBlock { Text = "排程與自動對話", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                new TextBlock { Text = "只有 App 執行時才檢查；錯過的觸發不補發。", Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
            },
        });
        RenderEditor();
        RenderScheduleList();
        RenderRuns();
        RenderLifeSettings();
    }

    // MARK: Editor + list

    void RenderEditor()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = editing is null ? "建立排程" : "編輯排程",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        var nameBox = new TextBox { PlaceholderText = "排程名稱", Text = titleDraft };
        nameBox.TextChanged += (_, _) => titleDraft = nameBox.Text;
        panel.Children.Add(nameBox);

        var actionPicker = new ComboBox { MinWidth = 240, PlaceholderText = "類型" };
        actionPicker.Items.Add("提醒");
        actionPicker.Items.Add("讓 AI 在聊天室回應");
        actionPicker.SelectedIndex = (int)action;
        actionPicker.SelectionChanged += (_, _) =>
        {
            action = (ScheduleAction)Math.Max(0, actionPicker.SelectedIndex);
            Render();
        };
        panel.Children.Add(StackRow(new TextBlock { Text = "類型", VerticalAlignment = VerticalAlignment.Center }, actionPicker));

        var textBox = new TextBox { PlaceholderText = "內容或提示詞", Text = textDraft, AcceptsReturn = true, Height = 64 };
        textBox.TextChanged += (_, _) => textDraft = textBox.Text;
        panel.Children.Add(textBox);

        var kindPicker = new ComboBox { MinWidth = 240, PlaceholderText = "觸發方式" };
        kindPicker.Items.Add("每天");
        kindPicker.Items.Add("一次");
        kindPicker.Items.Add("固定間隔");
        kindPicker.SelectedIndex = (int)kind;
        kindPicker.SelectionChanged += (_, _) =>
        {
            kind = (ScheduleKind)Math.Max(0, kindPicker.SelectedIndex);
            Render();
        };
        panel.Children.Add(StackRow(new TextBlock { Text = "觸發方式", VerticalAlignment = VerticalAlignment.Center }, kindPicker));

        if (kind == ScheduleKind.Daily)
        {
            var time = new TimePicker
            {
                ClockIdentifier = "24HourClock",
                SelectedTime = new TimeSpan(hour, minute, 0),
            };
            time.TimeChanged += (_, _) =>
            {
                hour = time.SelectedTime.HasValue ? time.SelectedTime.Value.Hours : 12;
                minute = time.SelectedTime.HasValue ? time.SelectedTime.Value.Minutes : 0;
            };
            panel.Children.Add(StackRow(new TextBlock { Text = "每天於", VerticalAlignment = VerticalAlignment.Center }, time));
        }
        else if (kind == ScheduleKind.Once)
        {
            var date = new DatePicker { Date = onceAt.Date };
            var time = new TimePicker { ClockIdentifier = "24HourClock", SelectedTime = onceAt.TimeOfDay };
            date.DateChanged += (_, _) => onceAt = Combine(date.Date, time.SelectedTime);
            time.TimeChanged += (_, _) => onceAt = Combine(date.Date, time.SelectedTime);
            panel.Children.Add(StackRow(new TextBlock { Text = "時間", VerticalAlignment = VerticalAlignment.Center }, date, time));
        }
        else
        {
            var intervalBox = new TextBox { Text = intervalMinutes.ToString(), MinWidth = 120 };
            intervalBox.TextChanged += (_, _) => int.TryParse(intervalBox.Text, out intervalMinutes);
            panel.Children.Add(StackRow(
                new TextBlock { Text = "每 N 分鐘（5–1440）", VerticalAlignment = VerticalAlignment.Center }, intervalBox));
        }

        if (action == ScheduleAction.RoomMessage)
        {
            var targetPicker = new ComboBox { MinWidth = 200, PlaceholderText = "回覆成員" };
            targetPicker.Items.Add("所有在場成員");
            foreach (var member in Model.EnabledMemberIDs) targetPicker.Items.Add(Model.MemberName(member));
            targetPicker.SelectedIndex = target is null ? 0
                : 1 + Model.EnabledMemberIDs.FindIndex(member => member == target);
            targetPicker.SelectionChanged += (_, _) =>
            {
                var index = targetPicker.SelectedIndex;
                target = index is > 0 ? Model.EnabledMemberIDs[index - 1] : null;
            };
            panel.Children.Add(StackRow(new TextBlock { Text = "回覆成員", VerticalAlignment = VerticalAlignment.Center }, targetPicker));
            panel.Children.Add(new TextBlock
            {
                Text = "「讓 AI 在聊天室回應」會送到建立時所在的茶歇室話題，並使用成員額度。",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (editing is not null)
        {
            var cancel = new Button { Content = "取消編輯" };
            cancel.Click += (_, _) => { ResetForm(); Render(); };
            buttons.Children.Add(cancel);
        }
        var save = new Button
        {
            Content = editing is null ? "建立排程" : "儲存修改",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        save.Click += (_, _) =>
        {
            Model.SaveSchedule(editing, titleDraft, textDraft, kind, hour, minute, onceAt,
                intervalMinutes, action, target);
            ResetForm();
            Render();
        };
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Root.Children.Add(Panel(panel));
    }

    void RenderScheduleList()
    {
        var panel = new StackPanel { Spacing = 10 };
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = "現有任務", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var paused = Model.SchedulesPaused;
        var pauseToggle = new ToggleSwitch
        {
            OnContent = "全部暫停",
            OffContent = "排程啟用中",
            IsOn = paused,
        };
        pauseToggle.Toggled += (_, _) => Model.SetSchedulesPaused(pauseToggle.IsOn);
        Grid.SetColumn(pauseToggle, 1);
        head.Children.Add(pauseToggle);
        panel.Children.Add(head);

        var schedules = Model.SchedulesSnapshot();
        if (schedules.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "尚未建立排程", Opacity = 0.75 });
        }
        foreach (var item in schedules)
        {
            var row = new StackPanel { Spacing = 3 };
            var toggle = new CheckBox
            {
                IsChecked = item.Enabled,
                MinWidth = 0,
            };
            toggle.Click += (_, _) => Model.SetScheduleEnabled(item.Id, toggle.IsChecked == true);
            var label = new StackPanel { Spacing = 2 };
            label.Children.Add(new TextBlock { Text = item.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = Describe(item) + " · " + item.Text, FontSize = 12, Opacity = 0.75, MaxLines = 2, TextWrapping = TextWrapping.Wrap });
            if (LocalAutomation.Next(item, DateTimeOffset.Now) is { } next)
            {
                label.Children.Add(new TextBlock { Text = $"下次：{next.ToLocalTime():MM/dd HH:mm}", FontSize = 11, Opacity = 0.65 });
            }
            toggle.Content = label;
            row.Children.Add(toggle);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var editButton = new Button { Content = "編輯" };
            editButton.Click += (_, _) => { LoadForm(item); Render(); };
            buttons.Children.Add(editButton);
            var delete = new Button { Content = "刪除" };
            delete.Click += (_, _) =>
            {
                if (editing == item.Id) ResetForm();
                Model.DeleteSchedule(item.Id);
            };
            buttons.Children.Add(delete);
            row.Children.Add(buttons);
            panel.Children.Add(row);
            panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"] });
        }
        Root.Children.Add(Panel(panel));
    }

    void RenderRuns()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "最近觸發", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var runs = Model.ScheduleRunsSnapshot();
        if (runs.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "暫無觸發記錄。", Opacity = 0.75 });
        }
        foreach (var run in runs.AsEnumerable().Reverse().Take(20))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = run.At.ToLocalTime().ToString("MM/dd HH:mm:ss"),
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                Opacity = 0.7,
            });
            row.Children.Add(new TextBlock
            {
                Text = run.Result switch
                {
                    ScheduleRun.Outcome.Done => "已完成",
                    ScheduleRun.Outcome.Skipped => "已跳過",
                    _ => "失敗",
                },
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(run.Result switch
                {
                    ScheduleRun.Outcome.Done => Microsoft.UI.Colors.Green,
                    ScheduleRun.Outcome.Skipped => Microsoft.UI.Colors.Gray,
                    _ => Microsoft.UI.Colors.Orange,
                }),
            });
            row.Children.Add(new TextBlock { Text = run.Name, FontSize = 12 });
            row.Children.Add(new TextBlock { Text = run.Reason, FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(row);
        }
        Root.Children.Add(Panel(panel));
    }

    void RenderLifeSettings()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "每日開場", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var opening = Model.DailyOpeningSnapshot();
        var openingToggle = new ToggleSwitch
        {
            OnContent = "每天第一次打開 AIROOM 時，由成員輪流在茶歇室問候你",
            OffContent = "已關閉",
            IsOn = opening.Enabled,
        };
        openingToggle.Toggled += (_, _) => Model.ConfigureDailyOpening(openingToggle.IsOn);
        panel.Children.Add(openingToggle);
        panel.Children.Add(new TextBlock
        {
            Text = "從下一天開始生效；會提到昨天房間裡確實說過的事，不會編造。每次一則、一次呼叫。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"] });

        panel.Children.Add(new TextBlock { Text = "成員閒聊", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var chat = Model.AutoChatSnapshot();
        var chatToggle = new ToggleSwitch
        {
            OnContent = "兩位成員依作息和關係設定，在茶歇室輕鬆聊兩句",
            OffContent = "已關閉",
            IsOn = chat.Enabled,
        };
        chatToggle.Toggled += (_, _) => Model.ConfigureIdleChat(chatToggle.IsOn, chat.IntervalMinutes, chat.DailyLimit);
        panel.Children.Add(chatToggle);
        var steps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        var intervalPicker = new ComboBox { MinWidth = 160 };
        foreach (var value in new[] { 60, 120, 180, 240, 360, 720, 1440 }) intervalPicker.Items.Add($"每 {value} 分鐘");
        intervalPicker.SelectedIndex = ClosestInterval(chat.IntervalMinutes);
        intervalPicker.SelectionChanged += (_, _) =>
        {
            if (intervalPicker.SelectedIndex is { } index && index >= 0)
            {
                Model.ConfigureIdleChat(Model.AutoChatSnapshot().Enabled, new[] { 60, 120, 180, 240, 360, 720, 1440 }[index],
                    Model.AutoChatSnapshot().DailyLimit);
            }
        };
        steps.Children.Add(StackRow(new TextBlock { Text = "間隔", VerticalAlignment = VerticalAlignment.Center }, intervalPicker));
        var limitPicker = new ComboBox { MinWidth = 140 };
        for (var value = 1; value <= 6; value++) limitPicker.Items.Add($"每天上限 {value} 次");
        limitPicker.SelectedIndex = Math.Clamp(chat.DailyLimit, 1, 6) - 1;
        limitPicker.SelectionChanged += (_, _) =>
        {
            if (limitPicker.SelectedIndex is { } index && index >= 0)
            {
                Model.ConfigureIdleChat(Model.AutoChatSnapshot().Enabled, Model.AutoChatSnapshot().IntervalMinutes, index + 1);
            }
        };
        steps.Children.Add(StackRow(new TextBlock { Text = "額度", VerticalAlignment = VerticalAlignment.Center }, limitPicker));
        panel.Children.Add(steps);
        panel.Children.Add(new TextBlock
        {
            Text = "閒聊的房間以開啟此功能時所在的茶歇室話題為準。若想讓成員長時間互相接話，那是另一項功能（直播）。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(Panel(panel));
    }

    // MARK: form state

    void LoadForm(ScheduledActivity item)
    {
        editing = item.Id;
        titleDraft = item.Name;
        textDraft = item.Text;
        kind = item.Kind;
        hour = item.Hour;
        minute = item.Minute;
        onceAt = item.OnceAt ?? DateTimeOffset.Now.AddHours(1);
        intervalMinutes = item.IntervalMinutes;
        action = item.Action;
        target = item.Target;
    }

    void ResetForm()
    {
        editing = null;
        titleDraft = "";
        textDraft = "";
        kind = ScheduleKind.Daily;
        action = ScheduleAction.Reminder;
        hour = 12;
        minute = 0;
        onceAt = DateTimeOffset.Now.AddHours(1);
        intervalMinutes = 60;
        target = null;
    }

    string Describe(ScheduledActivity item) => item.Kind switch
    {
        ScheduleKind.Daily => $"每天 {item.Hour:00}:{item.Minute:00}",
        ScheduleKind.Once => "一次 · " + (item.OnceAt is { } at ? at.ToLocalTime().ToString("MM/dd HH:mm") : "—"),
        _ => $"每 {item.IntervalMinutes} 分鐘",
    };

    static DateTimeOffset Combine(DateTimeOffset date, TimeSpan? time)
    {
        var day = date.LocalDateTime.Date;
        var clock = time ?? new TimeSpan(12, 0, 0);
        return new DateTimeOffset(day.Year, day.Month, day.Day, clock.Hours, clock.Minutes, 0, date.Offset);
    }

    static int ClosestInterval(int value)
    {
        var options = new[] { 60, 120, 180, 240, 360, 720, 1440 };
        var best = 0;
        for (var index = 0; index < options.Length; index++)
        {
            if (Math.Abs(options[index] - value) < Math.Abs(options[best] - value)) best = index;
        }
        return best;
    }

    static StackPanel StackRow(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    static UIElement Panel(UIElement content) => new Border
    {
        Child = content,
        Padding = new Thickness(14),
        CornerRadius = new CornerRadius(12),
        Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
    };
}
