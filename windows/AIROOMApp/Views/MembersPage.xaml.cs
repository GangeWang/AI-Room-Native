using System.ComponentModel;
using AIROOMApp.Controls;
using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace AIROOMApp.Views;

public sealed partial class MembersPage : Page
{
    MemberCard? editing;
    readonly string[] colorPalette =
    [
        "#D9692F", "#7B6FD6", "#3C66D9", "#A35BDB", "#2F8F86", "#3D8B4F",
        "#B85C4F", "#C29B3C", "#5D6FB8", "#8A5C7A",
    ];

    public MembersPage()
    {
        InitializeComponent();
        RenderCards();
        App.Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is null or nameof(WorkbenchModel.MemberIDs))
            {
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    RenderCards();
                    if (editing is not null)
                    {
                        editing = App.Model.Card(editing.Agent);
                        RenderEditor();
                    }
                });
            }
        };
    }

    WorkbenchModel Model => App.Model;

    // MARK: Card list

    void RenderCards()
    {
        CardList.Children.Clear();
        foreach (var card in Model.Archive.Members)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = new StudioAvatar();
            avatar.Set(card.Agent, card.Name, card.Color, 44,
                Model.PortraitOrTemplate(card), card.AvatarCrop, card.PortraitOffsetValue?.X,
                card.PortraitOffsetValue?.Y, false);
            Grid.SetColumn(avatar, 0);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = card.Name + (card.CardHidden ? "（已隱藏）" : ""),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            if (card.Motto.Length > 0)
            {
                text.Children.Add(new TextBlock { Text = card.Motto, FontSize = 12, Opacity = 0.7 });
            }
            text.Children.Add(new TextBlock
            {
                Text = $"跑在 {card.Executor.ShortName()}" + (card.Enabled ? "" : " · 已停用"),
                FontSize = 11,
                Opacity = 0.6,
            });
            Grid.SetColumn(text, 1);

            var edit = new Button { Content = "編輯" };
            var captured = card;
            edit.Click += (_, _) =>
            {
                editing = captured;
                RenderEditor();
            };
            Grid.SetColumn(edit, 2);

            row.Children.Add(avatar);
            row.Children.Add(text);
            row.Children.Add(edit);
            CardList.Children.Add(new Border
            {
                Child = row,
                Style = (Style)Application.Current.Resources["StudioPanel"],
                Padding = new Thickness(12),
            });
        }
    }

    // MARK: Editor

    void RenderEditor()
    {
        Editor.Children.Clear();
        if (editing is not { } card)
        {
            EditorHost.IsEnabled = false;
            return;
        }
        EditorHost.IsEnabled = true;
        var memberName = new TextBox { Header = "名字", Text = card.Name, MaxLength = 40 };
        var motto = new TextBox { Header = "一句話介紹", Text = card.Motto, MaxLength = 80 };
        var userTitle = new TextBox { Header = "怎麼稱呼你（寫進人物設定）", Text = card.UserTitle, MaxLength = 40 };

        var persona = new TextBox
        {
            Header = "人物設定（會原封送給模型）",
            Text = card.Persona,
            AcceptsReturn = true,
            Height = 140,
            TextWrapping = TextWrapping.Wrap,
        };

        // Color swatches
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var colorRow = new StackPanel { Spacing = 4 };
        colorRow.Children.Add(new TextBlock { Text = "代表色" });
        colorRow.Children.Add(swatches);
        foreach (var hex in colorPalette)
        {
            var swatch = new Button
            {
                Content = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(StudioColors.ParseHex(hex)),
                },
                Padding = new Thickness(2),
            };
            swatch.Click += (_, _) =>
            {
                Save(card => card.Color = hex);
            };
            swatches.Children.Add(swatch);
        }
        if (card.Color.Length > 0)
        {
            swatches.Children.Add(new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(StudioColors.ParseHex(card.Color)),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["StudioBorderBrush"],
            });
        }

        // Executor + defaults
        var executorBox = new ComboBox { Header = "執行端", MinWidth = 200 };
        foreach (var executor in ExecutorList.All)
        {
            executorBox.Items.Add(executor.ShortName() + (executor is Executor.DeepSeek or Executor.Antigravity
                ? "（此平台尚未支援）" : ""));
        }
        executorBox.SelectedIndex = Array.IndexOf(ExecutorList.All, card.Executor);
        executorBox.SelectionChanged += (_, _) =>
        {
            if (executorBox.SelectedIndex >= 0)
            {
                Save(clone => clone.Executor = ExecutorList.All[executorBox.SelectedIndex]);
            }
        };

        var modelChoices = Model.ModelChoices(card.Agent);
        var defaultModel = new ComboBox { Header = "預設模型", MinWidth = 200 };
        defaultModel.Items.Add("（CLI 自己的預設）");
        foreach (var choice in modelChoices) defaultModel.Items.Add(choice.Title);
        var modelIndex = card.DefaultModel.Length == 0 ? -1
            : modelChoices.FindIndex(choice => choice.Id == card.DefaultModel);
        defaultModel.SelectedIndex = modelIndex >= 0 ? modelIndex + 1 : 0;
        defaultModel.SelectionChanged += (_, _) =>
        {
            Save(clone => clone.DefaultModel = defaultModel.SelectedIndex <= 0
                ? "" : modelChoices[defaultModel.SelectedIndex - 1].Id);
        };

        var defaultEffort = new ComboBox { Header = "預設思考程度", MinWidth = 200 };
        defaultEffort.Items.Add("（CLI 自己的預設）");
        var efforts = card.DefaultModel.Length == 0 ? Array.Empty<string>()
            : modelChoices.Find(choice => choice.Id == card.DefaultModel)?.Efforts.ToArray() ?? [];
        foreach (var effort in efforts) defaultEffort.Items.Add(effort);
        var effortIndex = card.DefaultEffort.Length == 0 ? -1 : Array.IndexOf(efforts, card.DefaultEffort);
        defaultEffort.SelectedIndex = effortIndex >= 0 ? effortIndex + 1 : 0;
        defaultEffort.SelectionChanged += (_, _) =>
        {
            Save(clone => clone.DefaultEffort = defaultEffort.SelectedIndex <= 0
                ? "" : efforts[defaultEffort.SelectedIndex - 1]);
        };

        var enabled = new ToggleSwitch
        {
            Header = "啟用成員",
            IsOn = card.Enabled,
        };
        enabled.Toggled += (_, _) => Save(clone => clone.Enabled = enabled.IsOn);
        var hidden = new ToggleSwitch
        {
            Header = "隱藏人物卡（側欄與首頁不顯示）",
            IsOn = card.CardHidden,
        };
        hidden.Toggled += (_, _) => Save(clone => clone.CardHidden = hidden.IsOn);

        // Memories
        var memoryPanel = new StackPanel { Spacing = 6 };
        memoryPanel.Children.Add(new TextBlock { Text = "長期記憶（每次對話都會送進去）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var memory in card.Memories.ToList())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var toggle = new CheckBox { IsChecked = !memory.Off, Content = memory.Text.Length > 60 ? memory.Text[..60] + "…" : memory.Text };
            var capturedMemory = memory;
            toggle.Checked += (_, _) => Save(clone => clone.Memories.First(m => m.Id == capturedMemory.Id).Off = false);
            toggle.Unchecked += (_, _) => Save(clone => clone.Memories.First(m => m.Id == capturedMemory.Id).Off = true);
            row.Children.Add(toggle);
            var remove = new Button { Content = "刪", Padding = new Thickness(6, 0, 6, 0) };
            remove.Click += (_, _) => Save(clone => clone.Memories.RemoveAll(m => m.Id == capturedMemory.Id));
            row.Children.Add(remove);
            memoryPanel.Children.Add(row);
        }
        var newMemory = new TextBox { PlaceholderText = "新增一條記憶…", MinWidth = 280 };
        var addButton = new Button { Content = "加入" };
        addButton.Click += (_, _) =>
        {
            if (newMemory.Text.Trim().Length > 0)
            {
                Save(clone => clone.Memories.Add(new MemoryEntry(newMemory.Text.Trim())));
            }
        };
        memoryPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { newMemory, addButton } });

        // Portrait
        var portraitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        portraitRow.Children.Add(new TextBlock
        {
            Text = card.Portrait is null ? "立繪：使用模板圖" : "立繪：已上傳",
            VerticalAlignment = VerticalAlignment.Center,
        });
        var upload = new Button { Content = "上傳立繪…" };
        upload.Click += async (_, _) => await UploadPortraitAsync(card);
        portraitRow.Children.Add(upload);

        var saveButton = new Button
        {
            Content = "儲存卡片",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        saveButton.Click += (_, _) => Save(_ => { }, true);

        Editor.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["StudioPanel"],
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = card.Name, Style = (Style)Application.Current.Resources["StudioTitle"] },
                    memberName, motto, userTitle, persona, colorRow,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { executorBox, defaultModel, defaultEffort } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { enabled, hidden } },
                    memoryPanel,
                    portraitRow,
                    saveButton,
                    card.Agent.IsBuiltIn
                        ? (FrameworkElement)new TextBlock { Text = "內建成員不能刪除；可以隱藏或停用。", FontSize = 11, Opacity = 0.6 }
                        : CreateDeleteButton(),
                },
            },
        });

        void Save(Action<MemberCard> mutate, bool silent = false)
        {
            var clone = CoreJson.Deserialize<MemberCard>(CoreJson.Serialize(card))!;
            mutate(clone);
            Model.UpdateMember(clone);
            if (silent) return;
            card = Model.Card(card.Agent) ?? card;
            editing = card;
            RenderCards();
            RenderEditor();
        }
    }

    Button CreateDeleteButton()
    {
        var button = new Button { Content = "刪除這位成員" };
        button.Click += OnDeleteMember;
        return button;
    }

    static nint WindowId(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        return (nint)Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd).Value;
    }

    async System.Threading.Tasks.Task UploadPortraitAsync(MemberCard card)
    {
        if (App.Window is null) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var type in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            picker.FileTypeFilter.Add(type);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowId(App.Window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var attachment = Model.StoreMemberImage(file.Path);
        if (attachment is null) return;
        var clone = CoreJson.Deserialize<MemberCard>(CoreJson.Serialize(card))!;
        clone.Portrait = attachment;
        Model.UpdateMember(clone);
        editing = Model.Card(card.Agent);
        RenderCards();
        RenderEditor();
    }

    async void OnDeleteMember(object sender, RoutedEventArgs e)
    {
        if (editing is not { } card) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "刪除成員",
            Content = $"{card.Name} 的工位話題會一併刪掉；茶歇室、圓桌和專案紀錄會保留並沿用原本的名字。確定刪除？",
            PrimaryButtonText = "刪除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Model.RemoveMember(card.Agent);
            editing = null;
            RenderCards();
            RenderEditor();
        }
    }

    async void OnAddMember(object sender, RoutedEventArgs e)
    {
        var templateBox = new ComboBox { Header = "形象模板", MinWidth = 220, PlaceholderText = "（空白成員）" };
        foreach (var template in MemberTemplate.All)
        {
            templateBox.Items.Add(template.Name);
        }
        var executorBox = new ComboBox { Header = "執行端", MinWidth = 220 };
        foreach (var executor in ExecutorList.All)
        {
            executorBox.Items.Add(executor.ShortName() + (executor is Executor.DeepSeek or Executor.Antigravity
                ? "（此平台尚未支援）" : ""));
        }
        executorBox.SelectedIndex = 0;
        var nameBox = new TextBox { Header = "名字（空白用模板預設）", MinWidth = 220 };
        var panel = new StackPanel { Spacing = 10, Children = { templateBox, executorBox, nameBox } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "新增成員",
            Content = panel,
            PrimaryButtonText = "新增",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var chosenTemplate = templateBox.SelectedIndex >= 0 ? MemberTemplate.All[templateBox.SelectedIndex] : null;
        var chosenExecutor = ExecutorList.All[Math.Max(0, executorBox.SelectedIndex)];
        Model.AddMember(chosenTemplate, chosenExecutor, nameBox.Text);
    }
}
