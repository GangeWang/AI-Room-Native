using System.ComponentModel;
using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIROOMApp.Views;

/// <summary>用量與額度: subscription limits as the official executables report
/// them — not token estimates, and never failure dressed up as 0% remaining.
/// Ported from macOS's StudioQuotaView.</summary>
public sealed partial class QuotasPage : Page
{
    public QuotasPage()
    {
        InitializeComponent();
        Render();
        Model.RefreshAllQuotas();
        // Only the visible page re-renders: dead pages stop hearing model bumps.
        Loaded += (_, _) => App.Model.PropertyChanged += OnModelPropertyChanged;
        Unloaded += (_, _) => App.Model.PropertyChanged -= OnModelPropertyChanged;
    }

    WorkbenchModel Model => App.Model;

    void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(WorkbenchModel.QuotaVersion) or nameof(WorkbenchModel.CallUsageText))
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
                new TextBlock
                {
                    Text = "USAGE",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
                },
                new TextBlock { Text = "用量與額度", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                new TextBlock
                {
                    Text = "直接查詢官方執行端回報的訂閱限制。不是 Token 推算，也不是 ChatGPT／Claude App 的帳單。",
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });

        foreach (var executor in ExecutorList.All)
        {
            Root.Children.Add(ExecutorPanel(executor));
        }

        Root.Children.Add(new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
            Child = new TextBlock
            {
                Text = "資料只暫存於本次執行期間，超過 15 分鐘標示過期。這項查詢不會傳送聊天室內容，也不讀取 Token；Claude 只有在確認 /usage 為內建命令後才會呼叫。",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            },
        });
    }

    UIElement ExecutorPanel(Executor executor)
    {
        var quota = Model.QuotaFor(executor);
        var memberNames = Model.Archive.Members
            .Where(member => member.Executor == executor).Select(member => member.Name).ToList();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var badge = new Border
        {
            Width = 37,
            Height = 37,
            CornerRadius = new CornerRadius(19),
            Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
            Child = new TextBlock
            {
                Text = executor.ShortName()[..1],
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        header.Children.Add(badge);
        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(new TextBlock { Text = ExecutorName.Of(executor), FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (memberNames.Count > 0)
        {
            heading.Children.Add(new TextBlock
            {
                Text = "成員：" + string.Join("、", memberNames),
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        heading.Children.Add(new TextBlock
        {
            Text = executor switch
            {
                Executor.Codex => "Codex App Server · account/rateLimits/read",
                Executor.DeepSeek => "DeepSeek API 按用量計費，沒有 5 小時或每週額度",
                Executor.Antigravity => "agy 不提供額度查詢；請到 Antigravity App 查看",
                Executor.ZCode => "ZCode 的額度請到 ZCode 桌面版查看",
                _ => "Claude Code · /usage",
            },
            FontSize = 11,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Opacity = 0.65,
        });
        header.Children.Add(heading);
        header.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Stretch });

        var refreshing = Model.IsQuotaRefreshing(executor);
        var refreshable = executor is Executor.Claude or Executor.Codex;
        var refresh = new Button { Content = refreshing ? "查詢中……" : "刷新額度", Padding = new Thickness(10, 4, 10, 4) };
        refresh.Click += (_, _) => Model.RefreshQuota(executor);
        refresh.IsEnabled = refreshable && !refreshing;
        header.Children.Add(refresh);
        if (refreshing) header.Children.Add(new ProgressRing { Width = 18, Height = 18, IsActive = true });

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(header);
        body.Children.Add(new TextBlock
        {
            Text = $"今天：{CallUsageFor(executor, dayOnly: true)}　·　近 60 天：{CallUsageFor(executor, dayOnly: false)}",
            FontSize = 12,
            Opacity = 0.75,
        });

        if (quota is not { } sample)
        {
            body.Children.Add(new TextBlock
            {
                Text = refreshable ? "尚未查詢，按「刷新額度」讀取官方帳號回報。" : "此執行端沒有可查詢的訂閱額度。",
                FontSize = 13,
                Opacity = 0.7,
            });
        }
        else if (!sample.IsFresh())
        {
            body.Children.Add(new TextBlock
            {
                Text = "上次資料已過期，請重新整理；過期不代表額度耗盡。",
                FontSize = 12,
                Opacity = 0.75,
            });
        }
        else if (sample.Status == QuotaStatus.Unavailable)
        {
            body.Children.Add(new TextBlock
            {
                Text = "官方執行端目前未提供可驗證的額度資料；請確認登入狀態。",
                FontSize = 12,
                Opacity = 0.75,
            });
        }
        else
        {
            foreach (var window in sample.Windows)
            {
                var remaining = window.RemainingPercent;
                var row = new StackPanel { Spacing = 6 };
                row.Children.Add(new Grid
                {
                    ColumnSpacing = 8,
                    Children =
                    {
                        SetColumns(new TextBlock { Text = window.Label, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, 0),
                        SetColumns(new TextBlock
                        {
                            Text = $"{remaining:F1}% 剩餘",
                            FontSize = 13,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Foreground = new SolidColorBrush(remaining <= 20 ? Microsoft.UI.Colors.Orange : Microsoft.UI.Colors.Green),
                        }, 1),
                    },
                });
                row.Children.Add(new ProgressBar
                {
                    Maximum = 100,
                    Value = remaining,
                    Foreground = new SolidColorBrush(remaining <= 20 ? Microsoft.UI.Colors.Orange : Microsoft.UI.Colors.Green),
                });
                row.Children.Add(new TextBlock
                {
                    Text = window.ResetsAt is { } resets
                        ? $"{window.UsedPercent:F1}% 已使用 · 重置：{resets.ToLocalTime():MM/dd HH:mm}"
                        : $"{window.UsedPercent:F1}% 已使用 · 未提供重置時間",
                    FontSize = 11,
                    Opacity = 0.65,
                });
                body.Children.Add(row);
            }
            body.Children.Add(new TextBlock
            {
                Text = $"檢查時間：{sample.CheckedAt.ToLocalTime():yyyy/MM/dd HH:mm:ss}",
                FontSize = 11,
                Opacity = 0.6,
            });
        }

        return new Border
        {
            Child = body,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
        };
    }

    string CallUsageFor(Executor executor, bool dayOnly)
    {
        lock (Model.Archive)
        {
            var usage = Model.Archive.Members
                .Where(member => member.Executor == executor)
                .Select(member => Model.Archive.Calls.Usage(member.Agent, dayOnly ? DateTimeOffset.Now.ToString("yyyy-MM-dd") : null))
                .Aggregate(new CallUsage(), (a, b) => a + b)
                .Label(Model.Archive.Settings.Language);
            return usage;
        }
    }

    static T SetColumns<T>(T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        return element;
    }
}
