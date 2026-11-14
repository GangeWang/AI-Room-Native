using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIROOMApp.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Render();
        // Only the visible page re-renders: dead pages stop hearing model bumps.
        Loaded += (_, _) => App.Model.PropertyChanged += OnModelPropertyChanged;
        Unloaded += (_, _) => App.Model.PropertyChanged -= OnModelPropertyChanged;
    }

    void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(WorkbenchModel.SettingsVersion) or nameof(WorkbenchModel.OfficialCLIStatuses)
            or nameof(WorkbenchModel.StatusMessage))
        {
            _ = DispatcherQueue.TryEnqueue(Render);
        }
    }

    WorkbenchModel Model => App.Model;

    void Render()
    {
        var settings = Model.Archive.Settings;
        Root.Children.Clear();

        // Update banner
        if (Model.AvailableUpdate is { } release)
        {
            Root.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = true,
                Severity = InfoBarSeverity.Informational,
                Title = $"有新版本：{release.Version}",
                Message = "到 GitHub Releases 下載新版；資料會保留。",
                ActionButton = new HyperlinkButton { Content = "打開 Releases 頁面", NavigateUri = new Uri(release.Page) },
            });
        }
        else if (Model.UpdateStatus is { Length: > 0 } updateStatus)
        {
            Root.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = true,
                Severity = InfoBarSeverity.Informational,
                Title = updateStatus,
            });
        }

        // Official programs
        var programPanel = new StackPanel { Spacing = 8 };
        programPanel.Children.Add(new TextBlock { Text = "成員程式", Style = (Style)Application.Current.Resources["StudioEyebrow"] });
        foreach (var executor in ExecutorList.All)
        {
            var status = Model.OfficialCLIStatuses.GetValueOrDefault(executor);
            var resolved = CommandPlan.ResolveExecutable(executor, settings);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock
            {
                Text = executor.ShortName(),
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            var label = executor == Executor.DeepSeek
                ? "此平台尚未支援"
                : StatusLabel(status) + (resolved is null ? "：找不到執行檔" : "");
            row.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.85,
            });
            if (resolved is not null)
            {
                row.Children.Add(new TextBlock
                {
                    Text = resolved,
                    FontSize = 11,
                    Opacity = 0.55,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 320,
                });
            }
            var spacer = new Microsoft.UI.Xaml.Controls.Border { HorizontalAlignment = HorizontalAlignment.Stretch };
            row.Children.Add(spacer);
            if (executor is Executor.Claude or Executor.Codex)
            {
                row.Children.Add(new Button
                {
                    Content = "登入…",
                    Padding = new Thickness(10, 2, 10, 2),
                });
                ((Button)row.Children[^1]).Click += (_, _) => Model.OpenOfficialLogin(executor);
            }
            programPanel.Children.Add(row);
        }
        programPanel.Children.Add(new TextBlock
        {
            Text = "登入一律在終端機用官方 CLI 自己的流程；AIROOM 不讀取、也不保存任何登入憑證。",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["StudioPanel"],
            Child = programPanel,
        });

        // Paths
        var claudePath = new TextBox { Header = "claude 完整路徑（空白＝自動偵測 App 內附版）", Text = settings.ClaudePath, MinWidth = 460 };
        claudePath.LostFocus += (_, _) => Update(s => s.ClaudePath = claudePath.Text.Trim());
        var codexPath = new TextBox { Header = "codex 完整路徑（空白＝自動偵測；也可用系統的 CODEX_CLI_PATH）", Text = settings.CodexPath, MinWidth = 460 };
        codexPath.LostFocus += (_, _) => Update(s => s.CodexPath = codexPath.Text.Trim());
        var workspace = new TextBox { Header = "預設工作資料夾（工位裡逐話題可另選）", Text = settings.WorkspacePath, MinWidth = 460 };
        workspace.LostFocus += (_, _) => Update(s => s.WorkspacePath = workspace.Text.Trim());

        // Profile
        var userName = new TextBox { Header = "怎麼稱呼你（成員會用這個名字叫你）", Text = settings.UserName, MaxLength = 40, MinWidth = 300 };
        userName.LostFocus += (_, _) => Update(s => s.UserName = userName.Text.Trim());
        var language = new ComboBox { Header = "介面與回覆語言", MinWidth = 200 };
        language.Items.Add("繁體中文");
        language.Items.Add("English");
        language.SelectedIndex = settings.Language == InterfaceLanguage.English ? 1 : 0;
        language.SelectionChanged += (_, _) => Update(s => s.Language = language.SelectedIndex == 1
            ? InterfaceLanguage.English : InterfaceLanguage.ZhTW);
        var updates = new ToggleSwitch { Header = "有新版本時提醒（每天最多查一次 GitHub）", IsOn = settings.CheckForUpdates };
        updates.Toggled += (_, _) => Update(s => s.CheckForUpdates = updates.IsOn);

        Root.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["StudioPanel"],
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "工作台", Style = (Style)Application.Current.Resources["StudioEyebrow"] },
                    claudePath, codexPath, workspace, userName, language, updates,
                },
            },
        });

        // Data
        Root.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["StudioPanel"],
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "資料與隱私", Style = (Style)Application.Current.Resources["StudioEyebrow"] },
                    new TextBlock
                    {
                        Text = $"對話、人物卡與設定以 DPAPI 金鑰加密，只存在這台電腦：{Model.Persistence?.Root ?? "（無法存取）"}",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = $"版本 {WorkbenchModel.CurrentVersion}。呼叫次數：{Model.CallUsageText}",
                        Opacity = 0.7,
                        FontSize = 12,
                    },
                    new TextBlock
                    {
                        Text = "成員背後是真實的官方 CLI，以你的帳號執行。修改檔案的工作在副本進行，由你確認後才寫回。",
                        Opacity = 0.7,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        });

        void Update(Action<NativeSettings> edit)
        {
            Model.UpdateSettings(edit);
            Model.RefreshCLIStatuses();
        }

        // Encrypted backups & restore — macOS BackupsPanel parity.
        var restoreButton = new Button { Content = "恢復選到的備份", IsEnabled = false };
        var backupList = new StackPanel { Spacing = 8 };
        var backupPreview = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        };
        string? selectedBackup = null;
        BackupSummary? selectedSummary = null;

        void RenderBackups()
        {
            backupList.Children.Clear();
            backupPreview.Text = "";
            selectedBackup = null;
            selectedSummary = null;
            var files = Model.BackupFiles();
            if (files.Count == 0)
            {
                backupList.Children.Add(new TextBlock
                {
                    Text = "還沒有備份。可以先備份一份目前的資料。",
                    Opacity = 0.7,
                });
                return;
            }
            foreach (var file in files)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = file.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm"),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
                info.Children.Add(new TextBlock
                {
                    Text = (file.BeforeRestore ? "恢復前自動備份" : "手動備份") + " · " + $"{file.Bytes / 1024.0:F0} KB",
                    FontSize = 11,
                    Opacity = 0.65,
                });
                row.Children.Add(info);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                var previewButton = new Button { Content = "預覽" };
                previewButton.Click += (_, _) =>
                {
                    selectedBackup = file.Url;
                    selectedSummary = Model.PreviewBackup(file.Url);
                    restoreButton.IsEnabled = selectedSummary is not null && Model.CanRestore;
                    backupPreview.Text = selectedSummary is { } summary
                        ? $"話題 {summary.DeskTopics} 個 · 訊息 {summary.DeskMessages} 則 · 成員 {summary.MemberNames.Count} 位（{string.Join("、", summary.MemberNames)}）· 記憶 {summary.Memories} 條 · 擁有者 {summary.OwnerName}"
                        : "這份備份無法在此電腦讀取。";
                };
                buttons.Children.Add(previewButton);
                var deleteButton = new Button { Content = "刪除" };
                deleteButton.Click += (_, _) =>
                {
                    Model.DeleteBackup(file.Url);
                    RenderBackups();
                };
                buttons.Children.Add(deleteButton);
                row.Children.Add(buttons);
                backupList.Children.Add(row);
            }
        }

        async void RestoreAsync(string file)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "恢復備份",
                Content = "目前的資料會先自動備份，再換成這份備份的內容。成員登入不受影響；工作資料夾裡的檔案不會回到過去。確定恢復？",
                PrimaryButtonText = "先備份目前資料，再恢復",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                Model.RestoreFromBackup(file);
                RenderBackups();
            }
        }

        var backupHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        backupHeader.Children.Add(new TextBlock { Text = "加密備份與恢復", Style = (Style)Application.Current.Resources["StudioEyebrow"], VerticalAlignment = VerticalAlignment.Center });
        var refreshButton = new Button { Content = "重新整理" };
        refreshButton.Click += (_, _) => RenderBackups();
        backupHeader.Children.Add(refreshButton);
        var createBackup = new Button { Content = "立即備份", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        createBackup.Click += (_, _) =>
        {
            Model.CreateBackup();
            RenderBackups();
        };
        backupHeader.Children.Add(createBackup);
        restoreButton.Click += (_, _) => { if (selectedBackup is { } file) RestoreAsync(file); };
        backupHeader.Children.Add(restoreButton);

        Root.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["StudioPanel"],
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    backupHeader,
                    new TextBlock
                    {
                        Text = "備份包含話題、人物卡與記憶，用 DPAPI 金鑰加密，目前與這台電腦綁定。附件與圖片留在原處，不在備份檔裡；官方 CLI 的登入憑證不包含在內。",
                        FontSize = 12,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    backupList,
                    backupPreview,
                    new TextBlock
                    {
                        Text = "還原會先自動備份現在的資料再覆蓋；選一份備份並按「預覽」確認內容。",
                        FontSize = 11,
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        });
        RenderBackups();
        restoreButton.IsEnabled = false;
    }

    static string StatusLabel(OfficialCLIStatus status) => status switch
    {
        OfficialCLIStatus.Authenticated => "已登入 ✓",
        OfficialCLIStatus.NotLoggedIn => "尚未登入",
        OfficialCLIStatus.MissingExecutable => "缺少程式",
        OfficialCLIStatus.TimedOut => "檢查逾時",
        OfficialCLIStatus.NotConfirmed => "無法確認",
        _ => "檢查中…",
    };
}
