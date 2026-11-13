using System.ComponentModel;
using AIROOMApp.Controls;
using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AIROOMApp.Views;

public sealed partial class WorkstationPage : Page
{
    MemberID? selectedAgent;
    Conversation? selectedTopic;
    bool suppressPickers;

    public WorkstationPage()
    {
        InitializeComponent();
        RenderMembers();
        RenderCallUsage();
        // Only the visible page re-renders: dead pages stop hearing model bumps.
        Loaded += (_, _) => App.Model.PropertyChanged += OnModelPropertyChanged;
        Unloaded += (_, _) => App.Model.PropertyChanged -= OnModelPropertyChanged;
    }

    WorkbenchModel Model => App.Model;

    void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(WorkbenchModel.TopicsVersion) or nameof(WorkbenchModel.MemberIDs))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                RenderMembers();
                RenderTopic();
                RenderCallUsage();
                StatusText();
            });
        }
    }

    void StatusText()
    {
        if (Model.StatusMessage is { Length: > 0 } message)
        {
            Model.StatusMessage = null;
        }
    }

    // MARK: Member list

    void RenderMembers()
    {
        var members = Model.VisibleAgents;
        EmptyState.Visibility = members.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = selectedAgent;
        MemberList.Items.Clear();
        foreach (var agent in members)
        {
            var card = Model.Card(agent);
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var avatar = new StudioAvatar();
            avatar.Set(agent, Model.MemberName(agent), card?.Color, 40,
                Model.AvatarOrPortrait(card).Art, Model.AvatarOrPortrait(card).Crop,
                null, null, Model.BusyAgents.Contains(agent));
            Grid.SetColumn(avatar, 0);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            name.Children.Add(new TextBlock
            {
                Text = Model.MemberName(agent),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["StudioInkBrush"],
            });
            if (Model.BusyAgents.Contains(agent))
            {
                name.Children.Add(new TextBlock
                {
                    Text = "工作中…",
                    FontSize = 11,
                    Opacity = 0.7,
                });
            }
            Grid.SetColumn(name, 1);
            row.Children.Add(avatar);
            row.Children.Add(name);
            MemberList.Items.Add(row);
            if (agent == selected || selected is null && members.Count > 0)
            {
                MemberList.SelectedIndex = MemberList.Items.Count - 1;
            }
        }
        if (MemberList.SelectedIndex < 0 && MemberList.Items.Count > 0)
        {
            MemberList.SelectedIndex = 0;
        }
    }

    void OnMemberSelected(object sender, SelectionChangedEventArgs e)
    {
        var index = MemberList.SelectedIndex;
        if (index < 0 || index >= Model.VisibleAgents.Count) return;
        selectedAgent = Model.VisibleAgents[index];
        selectedTopic = Model.ActiveTopic(selectedAgent.Value);
        // The box mirrors the selected member's draft; without this, text left
        // over from the previous member leaks into the next one's draft on the
        // first keystroke.
        InputBox.Text = Model.LoadDraft(selectedAgent.Value);
        RenderTopic();
    }

    // MARK: Topic header

    void RenderTopic()
    {
        if (selectedAgent is null)
        {
            TopicPicker.ItemsSource = null;
            ChatPanel.Children.Clear();
            return;
        }
        var topics = Model.Topics(selectedAgent.Value);
        suppressPickers = true;
        TopicPicker.ItemsSource = topics.Select(topic =>
            topic.Title + (topic.Archived ? "（已歸檔）" : "")).ToList();
        selectedTopic ??= Model.ActiveTopic(selectedAgent.Value);
        if (selectedTopic is null && topics.Count > 0) selectedTopic = topics[0];
        TopicPicker.SelectedIndex = selectedTopic is null ? -1 : topics.FindIndex(t => t.Id == selectedTopic.Id);
        suppressPickers = false;

        RenderModelEffort();
        RenderChat();
        RenderDraftAttachments();
        RenderDeskBar();
        RenderRunButtons();
    }

    void RenderRunButtons()
    {
        if (selectedAgent is null)
        {
            SendButton.IsEnabled = true;
            SendButton.Content = "送出";
            StopButton.IsEnabled = false;
            return;
        }
        var busy = Model.BusyAgents.Contains(selectedAgent.Value);
        // Pressable while busy: pressing queues the draft to auto-send when
        // the reply lands (clearing the input cancels the queue).
        SendButton.IsEnabled = true;
        SendButton.Content = busy ? "排隊送出" : "送出";
        StopButton.IsEnabled = busy;
    }

    void RenderModelEffort()
    {
        if (selectedAgent is null) return;
        suppressPickers = true;
        var choices = Model.ModelChoices(selectedAgent.Value);
        var models = new List<string> { "預設模型" };
        models.AddRange(choices.Select(choice => choice.Title));
        ModelPicker.ItemsSource = models;
        var topic = selectedTopic;
        var currentModel = topic?.Model ?? "";
        var modelAt = currentModel.Length == 0 ? -1 : choices.FindIndex(choice => choice.Id == currentModel);
        ModelPicker.SelectedIndex = modelAt >= 0 ? modelAt + 1 : 0;

        var efforts = new List<string> { "預設" };
        if (ModelPicker.SelectedIndex > 0)
        {
            efforts.AddRange(choices[ModelPicker.SelectedIndex - 1].Efforts);
        }
        EffortPicker.ItemsSource = efforts;
        var currentEffort = topic?.Effort ?? "";
        var effortAt = currentEffort.Length == 0 ? -1 : efforts.FindIndex(effort => effort == currentEffort);
        EffortPicker.SelectedIndex = effortAt > 0 ? effortAt : 0;
        suppressPickers = false;
    }

    void OnTopicPicked(object sender, SelectionChangedEventArgs e)
    {
        if (suppressPickers || selectedAgent is null) return;
        var topics = Model.Topics(selectedAgent.Value);
        var index = TopicPicker.SelectedIndex;
        if (index < 0 || index >= topics.Count) return;
        selectedTopic = topics[index];
        if (selectedTopic.Archived) Model.OpenTopic(selectedTopic.Id);
        InputBox.Text = Model.LoadDraft(selectedAgent.Value);
        RenderTopic();
    }

    void OnNewTopic(object sender, RoutedEventArgs e)
    {
        if (selectedAgent is null) return;
        Model.NewTopic(selectedAgent.Value);
        selectedTopic = Model.ActiveTopic(selectedAgent.Value);
        InputBox.Text = "";
        RenderTopic();
    }

    void OnRenameTopic(object sender, RoutedEventArgs e)
    {
        if (selectedTopic is null) return;
        RenameAsync(selectedTopic);
    }

    async void OnArchiveTopic(object sender, RoutedEventArgs e)
    {
        if (selectedTopic is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "歸檔這個話題？",
            Content = "歸檔後它會從話題選單消失（紀錄保留），這位成員的下一段對話從新話題開始。",
            PrimaryButtonText = "歸檔",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Model.ArchiveTopic(selectedTopic.Id);
        }
    }

    async void RenameAsync(Conversation topic)
    {
        var box = new TextBox { Text = topic.Title };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "話題標題",
            Content = box,
            PrimaryButtonText = "確定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0)
        {
            Model.RenameTopic(topic.Id, box.Text);
        }
    }

    void OnModelPicked(object sender, SelectionChangedEventArgs e)
    {
        if (suppressPickers || selectedAgent is null) return;
        var choices = Model.ModelChoices(selectedAgent.Value);
        var model = ModelPicker.SelectedIndex switch
        {
            <= 0 => "",
            var at when at <= choices.Count => choices[at - 1].Id,
            _ => "",
        };
        Model.SetTopicModel(selectedAgent.Value, model);
        RenderModelEffort();
    }

    void OnEffortPicked(object sender, SelectionChangedEventArgs e)
    {
        if (suppressPickers || selectedAgent is null) return;
        var effort = EffortPicker.SelectedIndex <= 0 ? "" : EffortPicker.SelectedItem as string ?? "";
        Model.SetTopicEffort(selectedAgent.Value, effort);
    }

    // MARK: Chat rendering

    void RenderChat()
    {
        ChatPanel.Children.Clear();
        if (selectedTopic is null) return;
        foreach (var message in selectedTopic.Messages)
        {
            ChatPanel.Children.Add(Bubble(message));
        }
        if (selectedAgent is { } queuedAgent && Model.QueuedSendText(queuedAgent) is { Length: > 0 } queued)
        {
            // The queued follow-up must be visible where the conversation is,
            // not only in the banner; otherwise a press reads as "didn't send".
            var queuedStack = new StackPanel { Spacing = 4 };
            queuedStack.Children.Add(new TextBlock { Text = "（排隊中）", FontSize = 12, Opacity = 0.8 });
            queuedStack.Children.Add(new TextBlock { Text = queued, TextWrapping = TextWrapping.Wrap });
            ChatPanel.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["StudioBubbleUser"],
                Opacity = 0.55,
                Child = queuedStack,
            });
        }
        ChatScroll.UpdateLayout();
        ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, disableAnimation: true);
    }

    FrameworkElement Bubble(ChatMessage message)
    {
        var isUser = message.Role == MessageRole.User;
        var bubble = new Border
        {
            Style = (Style)Application.Current.Resources[isUser ? "StudioBubbleUser" : "StudioBubbleAgent"],
        };
        var stack = new StackPanel { Spacing = 4 };
        if (!isUser)
        {
            var agent = message.Role == MessageRole.User ? null : selectedAgent;
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(new TextBlock
            {
                Text = agent is null ? "助理" : Model.MemberName(agent.Value),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.85,
            });
            if (message.ModelUsed is { Length: > 0 } model)
            {
                header.Children.Add(new TextBlock { Text = model, FontSize = 11, Opacity = 0.55 });
            }
            stack.Children.Add(header);
        }
        stack.Children.Add(isUser
            ? new TextBlock
            {
                Text = message.Body,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = (Brush)Application.Current.Resources["StudioInkBrush"],
            }
            : RichText.Block(message.Body));
        foreach (var attachment in message.Attachments)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "📎 " + attachment.FileName,
                FontSize = 11,
                Opacity = 0.75,
            });
        }
        bubble.Child = stack;
        return bubble;
    }

    // MARK: Draft attachments

    void RenderDraftAttachments()
    {
        DraftAttachmentsPanel.Children.Clear();
        if (selectedAgent is null) return;
        if (!Model.ChatAttachments.TryGetValue(selectedAgent.Value, out var list)) return;
        foreach (var attachment in list)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            chip.Children.Add(new TextBlock
            {
                Text = attachment.FileName,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var remove = new Button
            {
                Content = "✕",
                FontSize = 10,
                Padding = new Thickness(4, 0, 4, 0),
            };
            var id = attachment.Id;
            remove.Click += (_, _) => Model.RemoveDraftAttachment(id, selectedAgent!.Value);
            chip.Children.Add(remove);
            var border = new Border
            {
                Child = chip,
                Background = (Brush)Application.Current.Resources["StudioSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["StudioBorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3, 4, 3),
            };
            DraftAttachmentsPanel.Children.Add(border);
        }
    }

    void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (selectedAgent is null) return;
        Model.DraftChanged(selectedAgent.Value, InputBox.Text);
    }

    void OnInputKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter
            && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            args.Handled = true;
            OnSend(sender, args);
        }
    }

    void OnSend(object sender, RoutedEventArgs e)
    {
        if (selectedAgent is null) return;
        Model.Submit(selectedAgent.Value);
        // Submit clears the model's draft; the box must follow, or the stale
        // text invites a second press that finds an empty draft and drops it.
        InputBox.Text = Model.LoadDraft(selectedAgent.Value);
        RenderTopic();
    }

    void OnStop(object sender, RoutedEventArgs e)
    {
        if (selectedAgent is null) return;
        Model.Stop(selectedAgent.Value);
    }

    async void OnAttach(object sender, RoutedEventArgs e)
    {
        if (selectedAgent is null || App.Window is null) return;
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        foreach (var type in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf", ".txt", ".md", ".json", ".csv", ".log" })
        {
            picker.FileTypeFilter.Add(type);
        }
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            (nint)Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd).Value);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            Model.AddAttachments(files.Select(file => file.Path), selectedAgent.Value);
            RenderDraftAttachments();
        }
    }

    // MARK: Desk bar

    void RenderDeskBar()
    {
        if (selectedTopic is null)
        {
            DeskBar.Visibility = Visibility.Collapsed;
            return;
        }
        var changes = Model.DeskChanges.GetValueOrDefault(selectedTopic.Id) ?? [];
        var undoable = Model.DeskUndoable.Contains(selectedTopic.Id);
        var folderSet = selectedTopic.WorkFolder.Length > 0;
        DeskBar.Visibility = folderSet || changes.Count > 0 || undoable ? Visibility.Visible : Visibility.Collapsed;
        ApplyDeskButton.IsEnabled = changes.Count > 0;
        UndoDeskButton.IsEnabled = undoable;
        DiscardDeskButton.IsEnabled = changes.Count > 0 || undoable;
        DeskChangesText.Text = folderSet
            ? changes.Count == 0 ? "工作副本沒有變更" : $"工作副本有 {changes.Count} 個檔案變更"
            : "個人資料夾（直接修改）";
    }

    void OnApplyDesk(object sender, RoutedEventArgs e)
    {
        if (selectedAgent is null) return;
        Model.ApplyDeskChanges(selectedAgent.Value);
        StatusText();
    }

    void OnUndoDesk(object sender, RoutedEventArgs e)
    {
        if (selectedAgent is null) return;
        Model.UndoDeskRound(selectedAgent.Value);
        StatusText();
    }

    void OnDiscardDesk(object sender, RoutedEventArgs e)
    {
        if (selectedAgent is null) return;
        Model.DiscardDeskCopy(selectedAgent.Value);
        StatusText();
    }

    void RenderCallUsage()
    {
        CallUsageText.Text = Model.CallUsageText;
    }
}
