using System.ComponentModel;
using AIROOMApp.Controls;
using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIROOMApp.Views;

/// <summary>The 茶歇室 (lounge): all present members share one transcript and take
/// turns on their own. Ported from macOS's RoomView, on the lounge engine.</summary>
public sealed partial class LoungePage : Page
{
    public LoungePage()
    {
        InitializeComponent();
        Render();
        // Only the visible page re-renders: dead pages stop hearing model bumps.
        Loaded += (_, _) => App.Model.PropertyChanged += OnModelPropertyChanged;
        Unloaded += (_, _) => App.Model.PropertyChanged -= OnModelPropertyChanged;
    }

    WorkbenchModel Model => App.Model;

    void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(WorkbenchModel.RoomsVersion) or nameof(WorkbenchModel.RoomDraft))
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                Render();
                SyncDraft();
            });
        }
    }

    void Render()
    {
        if (App.Model is null) return;
        RenderRoomList();
        RenderHeader();
        RenderChat();
        RenderComposer();
    }

    void SyncDraft()
    {
        var caret = InputBox.SelectionStart;
        if (InputBox.Text != Model.RoomDraft)
        {
            InputBox.Text = Model.RoomDraft;
            InputBox.SelectionStart = Math.Min(caret, Math.Max(0, InputBox.Text.Length));
        }
        SendButton.IsEnabled = Model.RoomDraft.Trim().Length > 0
            && (!Model.RoomRunning || Model.ActiveRoom()?.Lounge.Style == LoungeStyle.Chat);
    }

    // MARK: Room list

    void RenderRoomList()
    {
        var rooms = Model.LoungeRooms();
        var activeID = Model.ActiveRoom()?.Id;
        RoomList.ItemsSource = rooms.Select(room => new RoomRow(room, room.Id == activeID)).ToList();
        if (activeID is null && rooms.Count > 0) Model.SelectRoom(rooms[0].Id);
    }

    void OnRoomSelected(object sender, SelectionChangedEventArgs args)
    {
        if (RoomList.SelectedItem is RoomRow row) Model.SelectRoom(row.Room.Id);
    }

    void OnNewTopic(object sender, RoutedEventArgs args) => Model.CreateRoom(carryOver: true);
    void OnNewTopicFresh(object sender, RoutedEventArgs args) => Model.CreateRoom(carryOver: false);

    // MARK: Header: mode, status, participants

    void RenderHeader()
    {
        Header.Children.Clear();
        var room = Model.ActiveRoom();
        if (room is null)
        {
            Header.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "共用上下文對話室", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock
                    {
                        Text = "同一段歷史供所有成員閱讀；完成的回覆會接續傳給下一位。先按「新話題」開一間。",
                        Opacity = 0.7, FontSize = 12,
                    },
                },
            });
            return;
        }
        var lounge = room.Lounge;

        Header.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = "共用上下文對話室", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock
                {
                    Text = "同一段歷史供所有成員閱讀；完成的回覆會接續傳給下一位。",
                    Opacity = 0.7, FontSize = 12,
                },
            },
        });

        // Mode bar
        var modeBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var mode = new ToggleSwitch
        {
            OnContent = "開會",
            OffContent = "閒聊",
            IsOn = lounge.Style == LoungeStyle.Meeting,
            Margin = new Thickness(0, -4, 0, -4),
        };
        mode.Toggled += (_, _) => Model.SetLoungeStyle(mode.IsOn ? LoungeStyle.Meeting : LoungeStyle.Chat);
        mode.IsEnabled = !Model.RoomRunning;
        modeBar.Children.Add(mode);
        if (lounge.Style == LoungeStyle.Meeting)
        {
            var handRaise = new CheckBox { Content = "舉手模式", IsChecked = lounge.HandRaise };
            handRaise.Checked += (_, _) => Model.SetLoungeOption(handRaise: true);
            handRaise.Unchecked += (_, _) => Model.SetLoungeOption(handRaise: false);
            handRaise.IsEnabled = !Model.RoomRunning;
            modeBar.Children.Add(handRaise);
            var hostPick = new CheckBox { Content = "舉手後由我點名", IsChecked = lounge.HostPick, IsEnabled = lounge.HandRaise && !Model.RoomRunning };
            hostPick.Checked += (_, _) => Model.SetLoungeOption(hostPick: true);
            hostPick.Unchecked += (_, _) => Model.SetLoungeOption(hostPick: false);
            modeBar.Children.Add(hostPick);
        }
        modeBar.Children.Add(new TextBlock
        {
            Text = lounge.Style == LoungeStyle.Chat
                ? "成員自由接話，聊到自然告一段落就停；你隨時可以插話。"
                : "每人依序回答一次，然後等你；結束時整理會議紀要。",
            Opacity = 0.7,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Header.Children.Add(modeBar);

        // Status + controls
        var hasMeeting = lounge.Style == LoungeStyle.Meeting && lounge.MeetingStartEntryID is not null;
        if (Model.LoungeActivity is { } activity || lounge.Phase != LoungePhase.Idle || lounge.Reason is not null || hasMeeting)
        {
            var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            status.Children.Add(new TextBlock
            {
                Text = Model.LoungeActivity ?? PhaseLabel(lounge.Phase),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (lounge.Reason is { } reason)
            {
                status.Children.Add(new TextBlock
                {
                    Text = reason,
                    Opacity = 0.75,
                    FontSize = 12,
                    MaxLines = 2,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            if (lounge.Phase == LoungePhase.Paused && !Model.RoomRunning)
            {
                var continueButton = new Button { Content = "繼續聊", Padding = new Thickness(10, 2, 10, 2) };
                continueButton.Click += (_, _) => Model.ContinueLounge();
                status.Children.Add(continueButton);
            }
            if (hasMeeting && !Model.RoomRunning)
            {
                var minutesMenu = new MenuFlyout();
                foreach (var member in Model.PresentMembers(room))
                {
                    var item = new MenuFlyoutItem { Text = $"請 {member.Name} 整理" };
                    var agent = member.Agent;
                    item.Click += (_, _) => Model.WriteMinutes(agent);
                    minutesMenu.Items.Add(item);
                }
                var minutesButton = new Button { Content = "整理會議紀要", Padding = new Thickness(10, 2, 10, 2) };
                minutesButton.Flyout = minutesMenu;
                status.Children.Add(minutesButton);
            }
            if (Model.RoomRunning)
            {
                var progress = new ProgressRing { Width = 18, Height = 18, IsActive = true };
                status.Children.Add(progress);
            }
            Header.Children.Add(status);
        }

        // Participants + shared-context counts
        var participantsBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        participantsBar.Children.Add(new TextBlock
        {
            Text = "在場",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        foreach (var member in Model.EnabledMemberIDs)
        {
            var card = Model.Card(member);
            var inRoom = room.Participants.Contains(member);
            var box = new CheckBox { IsChecked = inRoom, IsEnabled = !Model.RoomRunning };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var avatar = new StudioAvatar();
            var (art, crop) = Model.AvatarOrPortrait(card);
            avatar.Set(member, card?.Name ?? member.RawValue, card?.Color, 26, art, crop, null, null, false);
            row.Children.Add(avatar);
            row.Children.Add(new TextBlock
            {
                Text = member == MemberID.Claude ? "Claude" : member == MemberID.Codex ? "Codex" : Model.MemberName(member),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
            box.Content = row;
            var included = inRoom;
            box.Checked += (_, _) => { if (!included) Model.SetRoomParticipant(member, true); };
            box.Unchecked += (_, _) => { if (included) Model.SetRoomParticipant(member, false); };
            participantsBar.Children.Add(box);
        }
        participantsBar.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Stretch });
        var snapshot = RoomPromptBuilder.Snapshot(room);
        participantsBar.Children.Add(new TextBlock
        {
            Text = $"{snapshot.IncludedCount} / {room.Entries.Count} 則共用紀錄"
                + (snapshot.OmittedEntries > 0 ? $" · 較早 {snapshot.OmittedEntries} 則未傳送" : ""),
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var toolsBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var compressButton = new Button { Content = "壓縮上下文", Padding = new Thickness(10, 2, 10, 2) };
        compressButton.Click += (_, _) => Model.CompressRoom();
        compressButton.IsEnabled = !Model.RoomRunning && room.Entries.Count > 0;
        toolsBar.Children.Add(compressButton);
        var exportButton = new Button { Content = "匯出（Markdown）", Padding = new Thickness(10, 2, 10, 2) };
        exportButton.Click += OnExport;
        toolsBar.Children.Add(exportButton);
        var clearButton = new Button { Content = "清空當前話題", Padding = new Thickness(10, 2, 10, 2) };
        clearButton.Click += OnClear;
        clearButton.IsEnabled = !Model.RoomRunning && room.Entries.Count > 0;
        toolsBar.Children.Add(clearButton);
        participantsBar.Children.Add(toolsBar);
        Header.Children.Add(participantsBar);
    }

    static string PhaseLabel(LoungePhase phase) => phase switch
    {
        LoungePhase.Talking => "討論中",
        LoungePhase.Raising => "大家正在舉手",
        LoungePhase.AwaitingHost => "等你點名",
        LoungePhase.Closing => "有人提議先聊到這裡",
        LoungePhase.Summarizing => "正在整理會議紀要",
        LoungePhase.WaitingUser => "等你說話",
        LoungePhase.Paused => "已暫停",
        _ => "本次討論已結束",
    };

    // MARK: Transcript

    void RenderChat()
    {
        var previousCount = ChatPanel.Children.Count;
        ChatPanel.Children.Clear();
        var entries = Model.RoomEntries();
        var room = Model.ActiveRoom();
        if (room is null) return;

        if (entries.Count == 0)
        {
            ChatPanel.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 60, 0, 0),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "歡迎來到茶歇室", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "選擇在場成員，再提出第一個話題。", Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center },
                },
            });
            return;
        }

        foreach (var (entry, reactions) in GroupedEntries(entries))
        {
            if (entry.Speaker == Speaker.System)
            {
                ChatPanel.Children.Add(new TextBlock
                {
                    Text = $"— {entry.Body} —",
                    Opacity = 0.65,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                });
                continue;
            }
            if (entry.IsReaction)
            {
                var reactionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(43, 0, 0, 0) };
                if (entry.Agent is { } agent) reactionRow.Children.Add(AvatarFor(agent, 20));
                reactionRow.Children.Add(new TextBlock
                {
                    Text = $"{entry.DisplayName} 回應了 {entry.Body}",
                    Opacity = 0.7,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                ChatPanel.Children.Add(reactionRow);
                continue;
            }
            ChatPanel.Children.Add(EntryBubble(entry, reactions));
        }
        if (ChatPanel.Children.Count != previousCount)
        {
            ChatScroll.UpdateLayout();
            ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, disableAnimation: true);
        }
    }

    UIElement EntryBubble(RoomEntry entry, List<RoomEntry> reactions)
    {
        var card = entry.Agent is { } agent ? Model.Card(agent) : null;
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerRow.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (entry.IsMinutes)
        {
            headerRow.Children.Add(new TextBlock
            {
                Text = "會議紀要",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        if (entry.Delivery == RoomDelivery.InProgress)
        {
            headerRow.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true, VerticalAlignment = VerticalAlignment.Center });
        }
        else if (entry.Delivery is RoomDelivery.Failed or RoomDelivery.Interrupted)
        {
            headerRow.Children.Add(new TextBlock
            {
                Text = "⚠ 本次回覆未完成，不會進入其他成員的上下文",
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        headerRow.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Stretch });
        headerRow.Children.Add(new TextBlock
        {
            Text = entry.CreatedAt.ToLocalTime().ToString("HH:mm"),
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var bodyStack = new StackPanel { Spacing = 7 };
        bodyStack.Children.Add(headerRow);
        bodyStack.Children.Add(RichText.Block(entry.Body));
        if (reactions.Count > 0)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var reaction in reactions)
            {
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                chip.Children.Add(new TextBlock { Text = reaction.Body, VerticalAlignment = VerticalAlignment.Center });
                if (reaction.Agent is { } reactor) chip.Children.Add(AvatarFor(reactor, 16));
                chips.Children.Add(new Border
                {
                    Child = chip,
                    Padding = new Thickness(7, 3, 7, 3),
                    CornerRadius = new CornerRadius(10),
                    Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
                });
            }
            bodyStack.Children.Add(chips);
        }

        var bubbleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (entry.Agent is { } speaker)
        {
            bubbleRow.Children.Add(AvatarFor(speaker, 33));
        }
        else
        {
            bubbleRow.Children.Add(new FontIcon { Glyph = "", FontSize = 26 });
        }
        bubbleRow.Children.Add(new Border
        {
            Child = bodyStack,
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(13),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
        });
        return bubbleRow;
    }

    StudioAvatar AvatarFor(MemberID agent, double size)
    {
        var card = Model.Card(agent);
        var avatar = new StudioAvatar();
        var (art, crop) = Model.AvatarOrPortrait(card);
        avatar.Set(agent, Model.MemberName(agent), card?.Color, size, art, crop, null, null, false);
        return avatar;
    }

    static List<(RoomEntry Entry, List<RoomEntry> Reactions)> GroupedEntries(List<RoomEntry> entries)
    {
        var result = new List<(RoomEntry, List<RoomEntry>)>();
        foreach (var entry in entries)
        {
            if (entry.IsReaction && result.Count > 0)
            {
                var lastIndex = result.FindLastIndex(item => item.Item1.Speaker != Speaker.System);
                if (lastIndex >= 0)
                {
                    result[lastIndex].Item2.Add(entry);
                    continue;
                }
            }
            result.Add((entry, []));
        }
        return result;
    }

    // MARK: Questions, hands, composer

    void RenderComposer()
    {
        var room = Model.ActiveRoom();
        QuestionPanel.Children.Clear();
        HandsPanel.Children.Clear();

        // Responders picker
        TargetPicker.Items.Clear();
        TargetPicker.Items.Add("所有在場成員");
        foreach (var member in room?.Participants ?? [])
        {
            TargetPicker.Items.Add(member == MemberID.Claude ? "Claude" : member == MemberID.Codex ? "Codex" : Model.MemberName(member));
        }
        var targetIndex = Model.RoomTarget is null ? 0 : (room?.Participants.ToList().FindIndex(id => id == Model.RoomTarget) ?? -1) + 1;
        if (TargetPicker.SelectedIndex != targetIndex) TargetPicker.SelectedIndex = Math.Max(0, targetIndex);
        TargetPicker.IsEnabled = !Model.RoomRunning;

        // Pending questions
        if (room is not null)
        {
            foreach (var question in room.Lounge.PendingQuestions.TakeLast(4))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(9, 5, 9, 5) };
                row.Children.Add(AvatarFor(question.From, 20));
                row.Children.Add(new TextBlock
                {
                    Text = $"問你：{question.Text}",
                    FontSize = 12,
                    MaxLines = 2,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 560,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var answerButton = new Button
                {
                    Content = Model.AnsweringQuestionID == question.Id ? "回答中" : "回答",
                    Padding = new Thickness(10, 2, 10, 2),
                };
                answerButton.Click += (_, _) => Model.AnswerQuestion(question);
                row.Children.Add(answerButton);
                var skipButton = new Button { Content = "略過", Padding = new Thickness(10, 2, 10, 2) };
                skipButton.Click += (_, _) => Model.DismissQuestion(question.Id);
                row.Children.Add(skipButton);
                QuestionPanel.Children.Add(new Border
                {
                    Child = row,
                    CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(26, 255, 170, 0)),
                });
            }

            // Hand raising
            var lounge = room.Lounge;
            if (lounge.Phase is LoungePhase.Raising or LoungePhase.AwaitingHost && lounge.Hands.Count > 0)
            {
                foreach (var member in Model.EnabledMemberIDs)
                {
                    if (lounge.Hands.GetValueOrDefault(member.RawValue) is not { } vote) continue;
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(9, 5, 9, 5) };
                    row.Children.Add(AvatarFor(member, 20));
                    row.Children.Add(new TextBlock
                    {
                        Text = vote.Want > 0 ? string.Concat(Enumerable.Repeat("✋", vote.Want)) : "沒有補充",
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = vote.Why,
                        FontSize = 12,
                        Opacity = 0.7,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 420,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    if (lounge.Phase == LoungePhase.AwaitingHost && vote.Want > 0 && !lounge.Supplemented.Contains(member))
                    {
                        var pickButton = new Button { Content = "請他補充", Padding = new Thickness(10, 2, 10, 2) };
                        pickButton.Click += (_, _) => Model.PickHand(member);
                        row.Children.Add(pickButton);
                    }
                    HandsPanel.Children.Add(row);
                }
                if (lounge.Phase == LoungePhase.AwaitingHost)
                {
                    var nobodyButton = new Button { Content = "不用了，等我說話", Padding = new Thickness(10, 2, 10, 2) };
                    nobodyButton.Click += (_, _) => Model.PickHand(null);
                    HandsPanel.Children.Add(nobodyButton);
                }
            }
        }

        StopButton.IsEnabled = Model.RoomRunning;
        StopButton.Visibility = Model.RoomRunning ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Content = Model.AnsweringQuestionID is not null ? "回答" : "送出";
        SyncDraft();
        StatusLine.Text = Model.RoomRunning
            ? "成員發言中……"
            : "用 @名字 指定誰先接話。共用紀錄會傳給所選 AI 供應商。";
    }

    void OnTargetPicked(object sender, SelectionChangedEventArgs args)
    {
        var participants = Model.ActiveRoom()?.Participants;
        if (TargetPicker.SelectedIndex is int index && index > 0
            && participants is { Count: > 0 } && index <= participants.Count)
        {
            Model.SetRoomTarget(participants[index - 1]);
        }
        else
        {
            Model.SetRoomTarget(null);
        }
    }

    void OnInputChanged(object sender, TextChangedEventArgs args)
    {
        Model.RoomDraft = InputBox.Text; // the model re-notifies; SyncDraft keeps the caret
        SendButton.IsEnabled = Model.RoomDraft.Trim().Length > 0
            && (!Model.RoomRunning || Model.ActiveRoom()?.Lounge.Style == LoungeStyle.Chat);
    }

    void OnInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter
            && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            args.Handled = true;
            Model.SendToRoom();
        }
    }

    void OnSend(object sender, RoutedEventArgs args) => Model.SendToRoom();

    void OnStop(object sender, RoutedEventArgs args) => Model.StopRoom();

    async void OnExport(object sender, RoutedEventArgs args)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = $"{Model.ActiveRoom()?.Title ?? "AIROOM"}.md",
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("Markdown", [".md"]);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            (nint)Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd).Value);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            await Windows.Storage.FileIO.WriteTextAsync(file, Model.RoomMarkdown());
        }
        catch (Exception error)
        {
            Model.StatusMessage = error.Message;
        }
    }

    async void OnClear(object sender, RoutedEventArgs args)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空這個話題？",
            Content = "這個話題的所有訊息都會移除，無法復原。",
            PrimaryButtonText = "清空當前話題",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) Model.ClearRoom();
    }

    sealed record RoomRow(TeamRoom Room, bool Active)
    {
        public override string ToString() => Active ? $"● {Room.Title}" : Room.Title;
    }
}
