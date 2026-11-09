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
        if (editing is null && Model.Archive.Members.Count > 0)
        {
            editing = Model.Archive.Members[0];
            RenderEditor();
        }
        foreach (var card in Model.Archive.Members)
        {
            CardList.Children.Add(BuildMemberCard(card));
        }
        CardList.Children.Add(BuildAddCard());
    }

    FrameworkElement BuildMemberCard(MemberCard card)
    {
        var selected = editing?.Agent == card.Agent;
        var color = MemberColor(card);

        var stack = new StackPanel { Spacing = 8 };
        var portrait = new StudioCardPortrait { Height = 190 };
        portrait.Set(card, Model.PortraitOrTemplate(card));
        stack.Children.Add(portrait);
        stack.Children.Add(new TextBlock
        {
            Text = card.Name + (card.CardHidden ? "（已隱藏）" : ""),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        stack.Children.Add(new TextBlock
        {
            Text = card.Executor.ShortName() + (card.CardHidden ? " · 人物卡已隱藏" : card.Enabled ? "" : " · 已停用"),
            FontSize = 11,
            Opacity = 0.6,
        });
        if (card.Motto.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"「{card.Motto}」",
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
            });
        }

        var border = new Border
        {
            Child = stack,
            Style = (Style)Application.Current.Resources["StudioPanel"],
            Width = 200,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected
                ? new SolidColorBrush(color)
                : (Brush)Application.Current.Resources["StudioBorderBrush"],
        };
        var captured = card;
        border.Tapped += (_, _) =>
        {
            editing = captured;
            RenderCards();
        };
        return border;
    }

    FrameworkElement BuildAddCard()
    {
        var stack = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new FontIcon { Glyph = "\uE8FA", FontSize = 34, Opacity = 0.7 });
        stack.Children.Add(new TextBlock
        {
            Text = "新增成員",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.7,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "可套用 DS、Gemini、Kimi、GLM 等形象",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 170,
        });
        var border = new Border
        {
            Child = stack,
            Style = (Style)Application.Current.Resources["StudioPanel"],
            Width = 200,
            MinHeight = 250,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["StudioBorderBrush"],
            Opacity = 0.85,
        };
        border.Tapped += (_, _) => OnAddMember(border, new RoutedEventArgs());
        return border;
    }

    Windows.UI.Color MemberColor(MemberCard card)
    {
        var color = StudioColors.ParseHex(card.Color);
        if (color == default)
        {
            color = card.Agent == MemberID.Codex ? StudioColors.Violet : StudioColors.Orange;
        }
        return color;
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
            executorBox.Items.Add(executor.ShortName() + (executor is Executor.DeepSeek
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

        // Portrait & avatar — macOS MemberArtworkEditor parity: upload/replace
        // both pictures, adjust the avatar crop, portrait framing sliders.
        var liveCard = CoreJson.Deserialize<MemberCard>(CoreJson.Serialize(card))!;
        var portraitPreview = new StudioCardPortrait { Height = 190, Width = 150 };
        portraitPreview.Set(liveCard, Model.PortraitOrTemplate(liveCard));
        DispatcherTimer? framingCommit = null;

        void ApplyFraming(double? scale, double? x, double? y)
        {
            if (scale is { } s) liveCard.PortraitScale = s;
            if (x is not null || y is not null)
            {
                var current = liveCard.PortraitOffsetValue?.Clamped ?? new PortraitOffset(0, 0);
                liveCard.PortraitOffsetValue = new PortraitOffset(
                    x is { } hx ? hx : current.X,
                    y is { } hy ? hy : current.Y);
            }
            portraitPreview.Set(liveCard, Model.PortraitOrTemplate(liveCard));
            if (framingCommit is null)
            {
                framingCommit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                framingCommit.Tick += (_, _) =>
                {
                    framingCommit.Stop();
                    var committed = CoreJson.Deserialize<MemberCard>(CoreJson.Serialize(card))!;
                    committed.PortraitScale = liveCard.PortraitScale;
                    committed.PortraitOffsetValue = liveCard.PortraitOffsetValue;
                    Model.UpdateMember(committed);
                    card = Model.Card(card.Agent) ?? card;
                };
            }
            framingCommit.Stop();
            framingCommit.Start();
        }

        Slider FramingSlider(string header, double min, double max, double initial, Action<double> apply)
        {
            var slider = new Slider { Header = header, Minimum = min, Maximum = max, StepFrequency = 0.01, Value = initial };
            slider.ValueChanged += (_, args) => apply(args.NewValue);
            return slider;
        }

        async void AdjustAvatarAsync()
        {
            var editorSource = Model.AvatarSource(card.Agent);
            if (editorSource is null) return;
            var newCrop = await AvatarCropEditorDialog.ShowAsync(Model, card, editorSource);
            var clone = CoreJson.Deserialize<MemberCard>(CoreJson.Serialize(card))!;
            if (editorSource.Kind == AvatarEditorSourceKind.UploadedAvatar) clone.AvatarImageCrop = newCrop;
            else clone.AvatarCrop = newCrop;
            Model.UpdateMember(clone);
            card = Model.Card(card.Agent) ?? card;
            RenderCards();
            RenderEditor();
        }

        async void PickImageAsync(bool asAvatar)
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
            if (asAvatar)
            {
                clone.Avatar = attachment;
                clone.AvatarImageCrop = null; // a new picture starts with fresh framing
            }
            else
            {
                clone.Portrait = attachment;
            }
            Model.UpdateMember(clone);
            card = Model.Card(card.Agent) ?? card;
            RenderCards();
            RenderEditor();
        }

        Button UploadPortraitButton(MemberCard forCard)
        {
            var button = new Button { Content = forCard.Portrait is null ? "上傳立繪…" : "更換立繪…" };
            button.Click += (_, _) => PickImageAsync(asAvatar: false);
            return button;
        }

        Button ResetPortraitButton(MemberCard forCard, Action refresh)
        {
            var button = new Button { Content = "用回原版立繪" };
            button.Click += (_, _) =>
            {
                var clone = CoreJson.Deserialize<MemberCard>(CoreJson.Serialize(forCard))!;
                clone.Portrait = null;
                clone.PortraitScale = null;
                clone.PortraitOffsetValue = null;
                clone.AvatarCrop = null;
                Model.UpdateMember(clone);
                refresh();
            };
            return button;
        }

        Button UploadAvatarButton(MemberCard forCard)
        {
            var button = new Button { Content = forCard.Avatar is null ? "上傳單獨頭像…" : "更換頭像…" };
            button.Click += (_, _) => PickImageAsync(asAvatar: true);
            return button;
        }

        Button RemoveAvatarButton(MemberCard forCard, Action refresh)
        {
            var button = new Button { Content = "移除頭像" };
            button.Click += (_, _) =>
            {
                var clone = CoreJson.Deserialize<MemberCard>(CoreJson.Serialize(forCard))!;
                clone.Avatar = null;
                clone.AvatarImageCrop = null;
                Model.UpdateMember(clone);
                refresh();
            };
            return button;
        }

        Button AdjustAvatarButton(MemberCard forCard)
        {
            var button = new Button { Content = "調整頭像…", IsEnabled = Model.AvatarSource(forCard.Agent) is not null };
            button.Click += (_, _) => AdjustAvatarAsync();
            return button;
        }

        Button ResetFramingButton(MemberCard forCard, Action refresh)
        {
            var button = new Button { Content = "立繪位置恢復預設" };
            button.Click += (_, _) =>
            {
                var clone = CoreJson.Deserialize<MemberCard>(CoreJson.Serialize(forCard))!;
                clone.PortraitScale = null;
                clone.PortraitOffsetValue = null;
                Model.UpdateMember(clone);
                refresh();
            };
            return button;
        }

        var artButtons = new StackPanel { Spacing = 6 };
        artButtons.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                UploadPortraitButton(card),
            },
        });
        if (card.Portrait is not null)
        {
            artButtons.Children.Add(ResetPortraitButton(card, () => { card = Model.Card(card.Agent) ?? card; RenderCards(); RenderEditor(); }));
        }
        var avatarRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var (avatarArt, avatarCropNow) = Model.AvatarOrPortrait(card);
        var avatarPreview = new StudioAvatar();
        avatarPreview.Set(card.Agent, card.Name, card.Color, 44, avatarArt, avatarCropNow, null, null, false);
        avatarRow.Children.Add(avatarPreview);
        avatarRow.Children.Add(UploadAvatarButton(card));
        if (card.Avatar is not null)
        {
            avatarRow.Children.Add(RemoveAvatarButton(card, () => { card = Model.Card(card.Agent) ?? card; RenderCards(); RenderEditor(); }));
        }
        avatarRow.Children.Add(AdjustAvatarButton(card));
        artButtons.Children.Add(avatarRow);

        var artPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        artPanel.Children.Add(portraitPreview);
        var artSliders = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        artSliders.Children.Add(FramingSlider("立繪大小", MemberCard.PortraitScaleRange.Item1, MemberCard.PortraitScaleRange.Item2,
            liveCard.PortraitScale ?? 1, value => ApplyFraming(value, null, null)));
        artSliders.Children.Add(FramingSlider("左右位置", -0.5, 0.5,
            liveCard.PortraitOffsetValue?.Clamped.X ?? 0, value => ApplyFraming(null, value, null)));
        artSliders.Children.Add(FramingSlider("上下位置", -0.5, 0.5,
            liveCard.PortraitOffsetValue?.Clamped.Y ?? 0, value => ApplyFraming(null, null, value)));
        if (card.PortraitScale is not null || card.PortraitOffsetValue is not null)
        {
            artSliders.Children.Add(ResetFramingButton(card, () => { card = Model.Card(card.Agent) ?? card; RenderCards(); RenderEditor(); }));
        }
        artPanel.Children.Add(new StackPanel { Spacing = 10, Children = { artButtons, artSliders } });

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
                    EditorHeader(card),
                    memberName, motto, userTitle, persona, colorRow,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { executorBox, defaultModel, defaultEffort } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { enabled, hidden } },
                    memoryPanel,
                    artPanel,
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

    /// <summary>The editor header: the member's 42px avatar beside the titles,
    /// like macOS's 編輯人物卡 panel.</summary>
    FrameworkElement EditorHeader(MemberCard card)
    {
        var avatar = new StudioAvatar();
        var (headerArt, headerCrop) = Model.AvatarOrPortrait(card);
        avatar.Set(card.Agent, card.Name, card.Color, 42, headerArt, headerCrop, null, null, false);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        titles.Children.Add(new TextBlock
        {
            Text = "編輯人物卡",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 16,
        });
        titles.Children.Add(new TextBlock { Text = card.Executor.ShortName(), FontSize = 11, Opacity = 0.6 });
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { avatar, titles } };
    }

    static nint WindowId(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        return (nint)Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd).Value;
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
            executorBox.Items.Add(executor.ShortName() + (executor is Executor.DeepSeek
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
