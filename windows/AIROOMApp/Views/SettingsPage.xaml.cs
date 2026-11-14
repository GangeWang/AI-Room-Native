using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
        RenderPortableExport();
    }

    /// <summary>搬到另一台電腦：密碼匯出。登入憑證與 API 金鑰不包含在內。</summary>
    void RenderPortableExport()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "搬到另一台電腦：密碼匯出",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var exportButton = new Button
        {
            Content = "匯出加密檔…",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = Model.StorageAvailable,
        };
        exportButton.Click += async (_, _) => await ExportAsync();
        buttons.Children.Add(exportButton);
        var importButton = new Button { Content = "從匯出檔匯入…", IsEnabled = Model.CanRestore };
        importButton.Click += async (_, _) => await ImportAsync();
        buttons.Children.Add(importButton);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock
        {
            Text = "匯出檔包含話題、茶歇室、圓桌、專案、人物卡、記憶、附件與頭像，用你設定的密碼加密（PBKDF2 + AES-256-GCM），不依賴這台電腦的金鑰，所以能在另一台電腦匯入。官方 CLI 的登入憑證與 API 金鑰不包含在內，到新電腦要重新登入。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "密碼不會被儲存，也無法找回。忘記密碼，這個匯出檔就打不開。",
            FontSize = 12,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap,
        });
        Root.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["StudioPanel"],
            Child = panel,
        });
    }

    async Task ExportAsync()
    {
        var password = await AskPassword("設定匯出密碼", $"至少 {PortableExportCodec.MinimumPasswordLength} 個字。匯入時要輸入同一組密碼。", confirm: true);
        if (password is null) return;
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = $"airoom-{DateTime.Now:yyyyMMdd-HHmm}",
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("AIROOM 匯出檔", [".airoomexport"]);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            (nint)Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd).Value);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        Model.ExportPortable(file.Path, password);
        Render();
    }

    async Task ImportAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".airoomexport");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            (nint)Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd).Value);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var password = await AskPassword("輸入匯出密碼", file.Name, confirm: false);
        if (password is null) return;
        PortableExport export;
        try
        {
            export = Model.OpenPortable(file.Path, password);
        }
        catch (Exception error)
        {
            Model.StatusMessage = error.Message;
            return;
        }
        var summary = BackupSummary.From(export.State);
        var folders = string.Join("\n", export.State.ReferencedFolders().Take(6));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"匯入 {file.Name}？",
            Content = new StackPanel { Spacing = 6 },
            PrimaryButtonText = "先備份目前資料，再匯入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var content = (StackPanel)dialog.Content;
        content.Children.Add(new TextBlock { Text = $"匯出時間：{export.CreatedAt.ToLocalTime():yyyy/MM/dd HH:mm}", FontSize = 12 });
        content.Children.Add(new TextBlock { Text = $"成員工位：{summary.DeskTopics} 個話題，{summary.DeskMessages} 則訊息", FontSize = 12 });
        content.Children.Add(new TextBlock { Text = $"人物卡：{string.Join("、", summary.MemberNames)}（{summary.Memories} 條記憶）", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = $"附件：{export.Attachments.Count} 個", FontSize = 12 });
        if (folders.Length > 0)
        {
            content.Children.Add(new TextBlock { Text = "用到的資料夾：", FontSize = 12 });
            content.Children.Add(new TextBlock { Text = folders, FontSize = 12, FontFamily = new FontFamily("Consolas"), Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
        }
        content.Children.Add(new TextBlock { Text = "目前的資料會先在這台電腦備份，再換成匯出檔的內容；附件會加入這台電腦。排程與成員閒聊會先暫停。", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Model.ImportPortable(export);
            Render();
        }
    }

    async Task<string?> AskPassword(string title, string message, bool confirm)
    {
        var box = new TextBox { PlaceholderText = "密碼" };
        var again = confirm ? new TextBox { PlaceholderText = "再輸入一次" } : null;
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(new TextBlock { Text = message, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
        form.Children.Add(box);
        if (again is not null) form.Children.Add(again);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = form,
            PrimaryButtonText = confirm ? "匯出" : "解密並預覽",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.IsPrimaryButtonEnabled = false;
        void Check()
        {
            dialog.IsPrimaryButtonEnabled = box.Text.Length >= PortableExportCodec.MinimumPasswordLength
                && (again is null || again.Text == box.Text);
        }
        box.TextChanged += (_, _) => Check();
        if (again is not null) again.TextChanged += (_, _) => Check();
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
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
