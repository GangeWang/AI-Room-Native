using System.ComponentModel;
using AIROOMApp.Controls;
using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AIROOMApp.Views;

/// <summary>首頁: the welcome dashboard — member cards, a preflight checklist and
/// project activity. Ported from the macOS StudioDashboard.</summary>
public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        Render();
        App.Model.PropertyChanged += OnModelPropertyChanged;
    }

    WorkbenchModel Model => App.Model;

    void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null
            or nameof(WorkbenchModel.BusyAgents)
            or nameof(WorkbenchModel.FlowVersion)
            or nameof(WorkbenchModel.AutomationVersion)
            or nameof(WorkbenchModel.CallUsageText))
        {
            _ = DispatcherQueue.TryEnqueue(Render);
        }
    }

    void Render()
    {
        Root.Children.Clear();
        RenderWelcome();
        RenderMemberCards();
        RenderPanels();
        Root.Children.Add(new TextBlock
        {
            Text = "所有工作台紀錄保存在本機。外部 AI CLI 可能需要連接其官方服務。",
            FontSize = 11,
            Opacity = 0.7,
        });
    }

    void RenderWelcome()
    {
        Root.Children.Add(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "我們一起工作的房間",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
                },
                new TextBlock { Text = "歡迎來到 AIROOM。", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                new TextBlock
                {
                    Text = "讓你的 AI 成員一起討論、獨立比較提案，或在你確認後將修改寫入專案。",
                    FontSize = 14,
                    Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(NavButton("打開專案工作區", "projects", accent: true));
        actions.Children.Add(NavButton("前往茶歇室", "lounge"));
        actions.Children.Add(NavButton("管理人物卡", "members"));
        Root.Children.Add(actions);
    }

    Button NavButton(string label, string tag, bool accent = false)
    {
        var button = new Button { Content = label };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => App.Window.NavigateTo(tag);
        return button;
    }

    void RenderMemberCards()
    {
        var cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var agent in Model.Archive.Members.Where(member => !member.CardHidden).Select(member => member.Agent))
        {
            var card = Model.Card(agent);
            var busy = Model.BusyAgents.Contains(agent);
            var panel = new StackPanel { Spacing = 9 };
            var portraitArea = new Border
            {
                Height = 190,
                CornerRadius = new CornerRadius(10),
                Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
                Child = AvatarFor(agent, 150),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            panel.Children.Add(portraitArea);
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(busy ? Microsoft.UI.Colors.Orange : Microsoft.UI.Colors.Green),
                VerticalAlignment = VerticalAlignment.Center,
            });
            head.Children.Add(new TextBlock
            {
                Text = card?.Name ?? agent.RawValue,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(head);
            panel.Children.Add(new TextBlock
            {
                Text = busy ? "正在工作"
                    : card?.Enabled == false ? "尚未啟用"
                    : "準備就緒時可以接收訊息",
                FontSize = 12,
                Opacity = 0.75,
            });
            if (card is { } info && info.Motto.Trim().Length > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"「{info.Motto}」",
                    FontSize = 12,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    Opacity = 0.75,
                    MaxLines = 2,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var profile = new Button { Content = "人物卡", FontSize = 12, Padding = new Thickness(8, 2, 8, 4) };
            profile.Click += (_, _) => App.Window.NavigateTo("members");
            actions.Children.Add(profile);
            var open = new Button
            {
                Content = "前往工位 →",
                FontSize = 12,
                Padding = new Thickness(8, 2, 8, 4),
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            open.IsEnabled = card?.Enabled != false;
            open.Click += (_, _) => App.Window.NavigateTo("workstation");
            actions.Children.Add(open);
            panel.Children.Add(actions);
            cards.Children.Add(new Border
            {
                Child = panel,
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(14),
                Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
                MinWidth = 250,
            });
        }
        Root.Children.Add(cards);
    }

    void RenderPanels()
    {
        var columns = new Grid { ColumnSpacing = 14 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var preflight = Panel(PreflightPanel());
        Grid.SetColumn(preflight, 0);
        columns.Children.Add(preflight);
        var projects = Panel(ProjectPanel());
        Grid.SetColumn(projects, 1);
        columns.Children.Add(projects);
        Root.Children.Add(columns);
    }

    UIElement PreflightPanel()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "開工前檢查", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var agent in Model.MemberIDs)
        {
            var executor = Model.ExecutorOf(agent);
            var status = Model.OfficialCLIStatuses.GetValueOrDefault(executor, OfficialCLIStatus.Checking);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(AvatarFor(agent, 26));
            row.Children.Add(new TextBlock
            {
                Text = Model.MemberName(agent),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Stretch });
            switch (status)
            {
                case OfficialCLIStatus.Authenticated:
                    row.Children.Add(new TextBlock { Text = "✓ 已就緒", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Green), VerticalAlignment = VerticalAlignment.Center });
                    break;
                case OfficialCLIStatus.NotLoggedIn:
                    row.Children.Add(new TextBlock { Text = "需要登入", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange), VerticalAlignment = VerticalAlignment.Center });
                    var login = new Button { Content = "去登入", FontSize = 12, Padding = new Thickness(8, 2, 8, 4) };
                    var loginExecutor = executor;
                    login.Click += (_, _) => Model.OpenOfficialLogin(loginExecutor);
                    row.Children.Add(login);
                    row.Children.Add(Recheck(executor));
                    break;
                case OfficialCLIStatus.MissingExecutable:
                    row.Children.Add(new TextBlock { Text = "缺少程式", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red), VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(NavButton("設定路徑", "settings"));
                    break;
                case OfficialCLIStatus.Checking:
                    row.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true, VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(new TextBlock { Text = "檢查中", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
                    break;
                default:
                    row.Children.Add(new TextBlock { Text = "無法確認", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(Recheck(executor));
                    break;
            }
            panel.Children.Add(row);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "「去登入」會在終端機打開官方程式自己的登入流程；AIROOM 不接觸帳號密碼或 Token。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        return Panel(panel);
    }

    Button Recheck(Executor executor)
    {
        var button = new Button { Content = "⟳", FontSize = 12, Padding = new Thickness(6, 2, 6, 4) };
        button.Click += (_, _) => Model.RefreshCLIStatuses();
        return button;
    }

    UIElement ProjectPanel()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "專案進度", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var flows = Model.Flows();
        if (flows.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "還沒有專案。可以先新增一個資料夾。", FontSize = 13, Opacity = 0.75 });
        }
        foreach (var flow in flows.Take(4))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = flow.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = FolderName(flow.Folder),
                FontSize = 11,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(text, 0);
            row.Children.Add(text);
            var pill = new Border
            {
                Padding = new Thickness(8, 1, 8, 3),
                CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
                Child = new TextBlock { Text = flow.Step.Title(), FontSize = 11 },
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pill, 1);
            row.Children.Add(pill);
            panel.Children.Add(row);
        }
        panel.Children.Add(NavButton("查看專案", "projects"));
        return Panel(panel);
    }

    static string FolderName(string folder)
    {
        var name = folder.TrimEnd('\\', '/');
        var index = name.LastIndexOf('\\');
        return index >= 0 ? name[(index + 1)..] : name;
    }

    StudioAvatar AvatarFor(MemberID agent, double size)
    {
        var card = Model.Card(agent);
        var avatar = new StudioAvatar();
        var (art, crop) = Model.AvatarOrPortrait(card);
        avatar.Set(agent, Model.MemberName(agent), card?.Color, size, art, crop, null, null, false);
        return avatar;
    }

    static Border Panel(UIElement content) => new Border
    {
        Child = content,
        Padding = new Thickness(14),
        CornerRadius = new CornerRadius(12),
        Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
    };
}
