using System.ComponentModel;
using AIROOMApp.Controls;
using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIROOMApp.Views;

/// <summary>The 圓桌 (roundtable): topics on the left; the selected topic's
/// stage — prepare, reveal, deliberation, conclusion — on the right.</summary>
public sealed partial class RoundtablePage : Page
{
    public RoundtablePage()
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
        if (args.PropertyName is null
            or nameof(WorkbenchModel.RoundtableVersion)
            or nameof(WorkbenchModel.RoomsVersion)
            or nameof(WorkbenchModel.FlowVersion))
        {
            _ = DispatcherQueue.TryEnqueue(Render);
        }
    }

    void Render()
    {
        RenderTopicList();
        RenderStage();
    }

    // MARK: Topic list

    void RenderTopicList()
    {
        var topics = Model.RoundtableTopics();
        var selectedID = Model.SelectedRoundtableID ?? topics.FirstOrDefault()?.Id;
        TopicList.ItemsSource = topics.Select(topic => new TopicRow(topic)).ToList();
        var index = topics.FindIndex(topic => topic.Id == selectedID);
        if (TopicList.SelectedIndex != index) TopicList.SelectedIndex = index;
        NewTopicButton.IsEnabled = !Model.RoundtableRunning;
        DeleteTopicButton.IsEnabled = !Model.RoundtableRunning && topics.Count > 0;
    }

    async void OnDeleteTopic(object sender, RoutedEventArgs args)
    {
        var topic = SelectedTopic();
        if (topic is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "刪除這個議題？",
            Content = $"「{topic.Title}」和它的審議紀錄都會移除，無法復原。",
            PrimaryButtonText = "刪除議題",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Model.DeleteRoundtable(topic.Id);
        }
    }

    void OnTopicSelected(object sender, SelectionChangedEventArgs args)
    {
        if (TopicList.SelectedItem is TopicRow row) Model.SelectedRoundtableID = row.Topic.Id;
    }

    // MARK: Stage

    void RenderStage()
    {
        var topic = SelectedTopic();
        Stage.Children.Clear();
        if (topic is null)
        {
            Stage.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 70, 0, 0),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "還沒有議題", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "開一個新議題：評審、比稿或方案比較。", Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center },
                },
            });
            return;
        }
        RenderHeader(topic);
        RenderProgress(topic);
        Stage.Children.Add(MaterialPanel(topic));
        switch (topic.Status)
        {
            case RoundtableStatus.Preparing:
                RenderPreparing(topic);
                break;
            case RoundtableStatus.Prepared:
                RenderPrepared(topic);
                break;
            default:
                RenderRevealed(topic);
                break;
        }
    }

    RoundtableTopic? SelectedTopic()
    {
        var topics = Model.RoundtableTopics();
        var id = Model.SelectedRoundtableID ?? topics.FirstOrDefault()?.Id;
        return topics.FirstOrDefault(topic => topic.Id == id);
    }

    void RenderHeader(RoundtableTopic topic)
    {
        Stage.Children.Add(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = $"ROUNDTABLE · {topic.Kind.Title()}",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
                },
                new TextBlock { Text = topic.Title, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextWrapping = TextWrapping.Wrap },
                StepsRow(topic),
            },
        });
    }

    static StackPanel StepsRow(RoundtableTopic topic)
    {
        var order = new[] { RoundtableStatus.Preparing, RoundtableStatus.Prepared, RoundtableStatus.Reviewing,
            RoundtableStatus.Discussing, RoundtableStatus.Conclusion, RoundtableStatus.Confirmed };
        var at = Array.IndexOf(order, topic.Status);
        string[] labels = ["各自準備", "公開", "審議", "結論", "確認"];
        bool[] reached = [at >= 0, at >= 2, at >= 3, at >= 4, at >= 5];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (var i = 0; i < labels.Length; i++)
        {
            row.Children.Add(new Border
            {
                Padding = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(9),
                Background = reached[i]
                    ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(64, 255, 170, 0))
                    : (Brush)Application.Current.Resources["StudioSurfaceBrush"],
                Child = new TextBlock { Text = labels[i], FontSize = 11 },
            });
            if (i < labels.Length - 1)
            {
                row.Children.Add(new TextBlock { Text = "›", FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
            }
        }
        return row;
    }

    void RenderProgress(RoundtableTopic topic)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var agent in topic.Members)
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            cell.Children.Add(AvatarFor(agent, 22));
            var submission = topic.Submission(agent);
            cell.Children.Add(new TextBlock
            {
                Text = submission is null ? "○" : submission.Succeeded ? "✓" : "⚠",
                FontSize = 13,
                Foreground = new SolidColorBrush(submission is null
                    ? Microsoft.UI.Colors.Gray
                    : submission.Succeeded ? Microsoft.UI.Colors.Green : Microsoft.UI.Colors.Orange),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (agent == topic.Lead)
            {
                cell.Children.Add(new Border
                {
                    Padding = new Thickness(5, 0, 5, 0),
                    CornerRadius = new CornerRadius(8),
                    Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
                    Child = new TextBlock { Text = "匯總", FontSize = 10 },
                });
            }
            row.Children.Add(cell);
        }
        row.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Stretch });
        row.Children.Add(new TextBlock
        {
            Text = $"已交 {topic.Completed}/{topic.Members.Count} · 問題 {topic.FindingCount} 個 · 呼叫 {topic.Calls} 次",
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (Model.RoundtableRunning)
        {
            row.Children.Add(new ProgressRing { Width = 18, Height = 18, IsActive = true });
            var stop = new Button { Content = "停止", Padding = new Thickness(10, 2, 10, 2) };
            stop.Click += (_, _) => Model.StopRoundtable();
            row.Children.Add(stop);
        }
        Stage.Children.Add(row);
    }

    static UIElement Panel(UIElement content) => new Border
    {
        Child = content,
        Padding = new Thickness(14),
        CornerRadius = new CornerRadius(12),
        Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
    };

    UIElement MaterialPanel(RoundtableTopic topic)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new Expander
        {
            Header = "材料／要求",
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    RichText.Block(topic.Material),
                    topic.Attachments.Count == 0
                        ? new TextBlock { Text = "", FontSize = 11 }
                        : new TextBlock
                        {
                            Text = "附件：" + string.Join("、", topic.Attachments.Select(attachment => attachment.FileName)),
                            FontSize = 11, Opacity = 0.7,
                        },
                },
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
        return Panel(stack);
    }

    void RenderPreparing(RoundtableTopic topic)
    {
        Stage.Children.Add(Panel(new TextBlock
        {
            Text = Model.RoundtableRunning
                ? "成員正在各自準備，公開前互相看不到。"
                : "準備被中斷了，可以繼續讓還沒交的成員準備。",
            TextWrapping = TextWrapping.Wrap,
        }));
        if (!Model.RoundtableRunning)
        {
            var go = new Button { Content = "繼續準備", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            go.Click += (_, _) => Model.PrepareRoundtable(topic.Id);
            Stage.Children.Add(go);
        }
    }

    void RenderPrepared(RoundtableTopic topic)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "大家都交了", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock
                {
                    Text = topic.PeerReview
                        ? "公開後每位成員只看別人的版本互評，再由匯總成員整理問題清單。"
                        : "公開後由匯總成員整理問題清單，接著開始審議。",
                    FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
                },
            },
        });
        row.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Stretch });
        var reveal = new Button { Content = "公開", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        reveal.Click += (_, _) => Model.RevealRoundtable(topic.Id);
        reveal.IsEnabled = !Model.RoundtableRunning && topic.Completed > 0;
        row.Children.Add(reveal);
        Stage.Children.Add(Panel(row));
    }

    void RenderRevealed(RoundtableTopic topic)
    {
        var versions = new StackPanel { Spacing = 10 };
        foreach (var submission in topic.Submissions)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(AvatarFor(submission.Agent, 20));
            head.Children.Add(new TextBlock
            {
                Text = Model.MemberName(submission.Agent),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (topic.Adopted == submission.Agent)
            {
                head.Children.Add(new TextBlock
                {
                    Text = "✓ 已採用",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Green),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            versions.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    head,
                    RichText.Block(submission.Succeeded ? submission.Text : "沒有完成：" + (submission.Error ?? "")),
                },
            });
        }
        var versionsPanel = new StackPanel { Spacing = 6 };
        versionsPanel.Children.Add(new Expander { Header = "各成員的版本", Content = versions, HorizontalAlignment = HorizontalAlignment.Stretch, IsExpanded = topic.Members.Count <= 4 });
        if (topic.PeerReviews.Count > 0)
        {
            var reviews = new StackPanel { Spacing = 10 };
            foreach (var review in topic.PeerReviews)
            {
                reviews.Children.Add(new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = Model.MemberName(review.Agent), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        RichText.Block(review.Succeeded ? review.Text : review.Error ?? ""),
                    },
                });
            }
            versionsPanel.Children.Add(new Expander { Header = "互評", Content = reviews, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        var findingsContent = new StackPanel { Spacing = 8 };
        if (topic.FindingsError is { } error)
        {
            var failRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            failRow.Children.Add(new TextBlock
            {
                Text = "⚠ 問題清單沒有整理完成：" + error,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                MaxWidth = 520,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var retry = new Button { Content = "重新整理問題清單", Padding = new Thickness(10, 2, 10, 2) };
            retry.Click += (_, _) => Model.RecompileFindings(topic.Id);
            retry.IsEnabled = !Model.RoundtableRunning && !Model.RoomRunning;
            failRow.Children.Add(retry);
            findingsContent.Children.Add(failRow);
        }
        else
        {
            findingsContent.Children.Add(RichText.Block(topic.Findings.Length == 0 ? "整理中……" : topic.Findings));
        }
        versionsPanel.Children.Add(new Expander
        {
            Header = $"問題清單（{topic.FindingCount} 個）",
            Content = findingsContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsExpanded = true,
        });
        Stage.Children.Add(Panel(versionsPanel));

        if (topic.Status == RoundtableStatus.Reviewing)
        {
            Stage.Children.Add(Panel(new TextBlock
            {
                Text = "正在互評並整理問題清單，完成後會自動開始審議。",
                TextWrapping = TextWrapping.Wrap,
            }));
        }

        // Deliberation: the hidden meeting room, shown in place.
        var room = topic.RoomID is { } roomID ? Model.RoomSnapshot(roomID) : null;
        if (room is not null)
        {
            Stage.Children.Add(DeliberationPanel(topic, room));
        }
        RenderConclusion(topic);
    }

    UIElement DeliberationPanel(RoundtableTopic topic, TeamRoom room)
    {
        var panel = new StackPanel { Spacing = 10 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(new TextBlock
        {
            Text = "審議討論",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Stretch });
        if (topic.Kind.HasCandidates())
        {
            var compare = new Button { Content = "下一步：比較並選擇候選", Padding = new Thickness(10, 2, 10, 2) };
            compare.Click += (_, _) => ShowCompare(topic);
            head.Children.Add(compare);
        }
        if (topic.Status == RoundtableStatus.Discussing && !Model.RoomRunning)
        {
            var minutes = new MenuFlyout();
            foreach (var agent in topic.Members)
            {
                var item = new MenuFlyoutItem { Text = $"請 {Model.MemberName(agent)} 整理" };
                var captured = agent;
                item.Click += (_, _) => Model.ConcludeRoundtable(topic.Id, captured);
                minutes.Items.Add(item);
            }
            var leadButton = new Button { Content = "整理會議紀要", Padding = new Thickness(10, 2, 10, 2) };
            leadButton.Flyout = minutes;
            head.Children.Add(leadButton);
        }
        panel.Children.Add(head);

        foreach (var entry in room.Entries.TakeLast(30))
        {
            if (entry.Speaker == Speaker.System)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"— {entry.Body} —",
                    FontSize = 12,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap,
                });
                continue;
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (entry.Agent is { } agent) row.Children.Add(AvatarFor(agent, 24));
            var bubble = new StackPanel { Spacing = 3 };
            bubble.Children.Add(new TextBlock
            {
                Text = $"{entry.DisplayName} · {entry.CreatedAt.ToLocalTime():HH:mm}"
                    + (entry.Delivery == RoomDelivery.InProgress ? " · 正在想……" : ""),
                FontSize = 11,
                Opacity = 0.65,
            });
            bubble.Children.Add(RichText.Block(entry.IsReaction ? $"{entry.DisplayName} 回應了 {entry.Body}" : entry.Body));
            row.Children.Add(bubble);
            panel.Children.Add(row);
        }

        if (topic.Status == RoundtableStatus.Discussing)
        {
            var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var input = new TextBox
            {
                PlaceholderText = "加入審議……",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 560,
            };
            var send = new Button { Content = "送出", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            send.Click += (_, _) =>
            {
                Model.SendToDeliberation(topic.Id, input.Text);
                input.Text = "";
            };
            input.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    args.Handled = true;
                    Model.SendToDeliberation(topic.Id, input.Text);
                    input.Text = "";
                }
            };
            inputRow.Children.Add(input);
            inputRow.Children.Add(send);
            panel.Children.Add(inputRow);
        }
        return Panel(panel);
    }

    void RenderConclusion(RoundtableTopic topic)
    {
        if (topic.Status is not (RoundtableStatus.Conclusion or RoundtableStatus.Confirmed)
            || topic.Conclusion.Length == 0)
        {
            return;
        }
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "會議紀要／結論",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(RichText.Block(topic.Conclusion));
        if (topic.Status == RoundtableStatus.Conclusion)
        {
            var confirm = new Button { Content = "確認結論", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            confirm.Click += (_, _) => Model.ConfirmConclusion(topic.Id);
            panel.Children.Add(confirm);
        }
        else
        {
            panel.Children.Add(RenderHandoff(topic));
        }
        Stage.Children.Add(Panel(panel));
    }

    /// <summary>帶回專案: the confirmed conclusion becomes material for a project's next plan.</summary>
    UIElement RenderHandoff(RoundtableTopic topic)
    {
        var flows = Model.Flows();
        if (topic.HandoffProjectID is { } handed)
        {
            var title = flows.FirstOrDefault(flow => flow.Id == handed)?.Title;
            return new TextBlock
            {
                Text = title is null ? "✓ 結論已確認，已帶回專案。" : $"✓ 結論已確認，已帶回專案「{title}」。",
                FontSize = 12,
                Opacity = 0.7,
            };
        }
        if (flows.Count == 0)
        {
            return new TextBlock { Text = "✓ 結論已確認。建立專案後可以把結論帶過去當作材料。", FontSize = 12, Opacity = 0.7 };
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = "帶回專案：", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var picker = new ComboBox { MinWidth = 220, PlaceholderText = "選擇專案" };
        foreach (var flow in flows) picker.Items.Add(flow);
        picker.SelectedIndex = flows.Count > 0 ? 0 : -1;
        row.Children.Add(picker);
        var hand = new Button { Content = "帶回專案" };
        hand.Click += (_, _) =>
        {
            if (picker.SelectedItem is ProjectFlow flow) Model.HandoffTopicToFlow(topic.Id, flow.Id);
        };
        row.Children.Add(hand);
        return row;
    }

    async void ShowCompare(RoundtableTopic topic)
    {
        var list = new StackPanel { Spacing = 14 };
        foreach (var submission in topic.Submissions.Where(item => item.Succeeded))
        {
            var cell = new StackPanel { Spacing = 6 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(AvatarFor(submission.Agent, 20));
            head.Children.Add(new TextBlock
            {
                Text = Model.MemberName(submission.Agent) + (topic.Adopted == submission.Agent ? "（已採用）" : ""),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            head.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Stretch });
            var adopt = new Button { Content = "採用這份", Padding = new Thickness(10, 2, 10, 2) };
            adopt.Click += (_, _) => Model.AdoptCandidate(topic.Id, submission.Agent);
            head.Children.Add(adopt);
            cell.Children.Add(head);
            cell.Children.Add(RichText.Block(submission.Text));
            list.Children.Add(new Border
            {
                Child = cell,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(10),
                Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
            });
        }
        list.Children.Add(new TextBlock
        {
            Text = "採用只是標記，不會自動寫任何檔案；結論確認後可帶回專案。",
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "並排比較，選出要採用的版本",
            Content = new ScrollViewer { MaxHeight = 480, Content = list },
            CloseButtonText = "關閉",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
        Render();
    }

    StudioAvatar AvatarFor(MemberID agent, double size)
    {
        var card = Model.Card(agent);
        var avatar = new StudioAvatar();
        var (art, crop) = Model.AvatarOrPortrait(card);
        avatar.Set(agent, Model.MemberName(agent), card?.Color, size, art, crop, null, null, false);
        return avatar;
    }

    // MARK: New topic dialog

    async void OnNewTopic(object sender, RoutedEventArgs args)
    {
        var title = new TextBox { PlaceholderText = "議題名稱（可留空，用材料自動取名）" };
        var kind = new ComboBox { Header = "種類", MinWidth = 200, PlaceholderText = "選一種" };
        foreach (var candidate in new[] { RoundtableKind.Review, RoundtableKind.Candidates, RoundtableKind.Proposals })
        {
            kind.Items.Add(new ComboBoxItem { Content = $"{candidate.Title()} — {candidate.Detail()}", Tag = candidate });
        }
        kind.SelectedIndex = 2;
        var material = new TextBox
        {
            Header = "材料／要求（貼上要討論的內容）",
            AcceptsReturn = true,
            Height = 180,
            TextWrapping = TextWrapping.Wrap,
        };
        var membersPanel = new StackPanel { Spacing = 6 };
        var boxes = new Dictionary<MemberID, CheckBox>();
        foreach (var member in Model.EnabledMemberIDs)
        {
            var box = new CheckBox { IsChecked = true, Content = Model.MemberName(member) };
            boxes[member] = box;
            membersPanel.Children.Add(box);
        }
        var lead = new ComboBox { Header = "匯總成員（整理問題清單）", MinWidth = 200 };
        foreach (var member in Model.EnabledMemberIDs) lead.Items.Add(Model.MemberName(member));
        lead.SelectedIndex = 0;
        var peerReview = new CheckBox { Content = "公開後先互評", IsChecked = true };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(title);
        panel.Children.Add(kind);
        panel.Children.Add(material);
        panel.Children.Add(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "在場成員（至少兩位）", FontSize = 12, Opacity = 0.8 },
                membersPanel,
                lead,
                peerReview,
            },
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "開新議題",
            Content = new ScrollViewer { MaxHeight = 560, Content = panel },
            PrimaryButtonText = "開始各自準備",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        var chosen = boxes.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();
        var leadIndex = Math.Max(0, lead.SelectedIndex);
        var leadAgent = leadIndex < chosen.Count ? chosen[leadIndex] : chosen.FirstOrDefault();
        var pickedKind = (kind.SelectedItem as ComboBoxItem)?.Tag is RoundtableKind parsed ? parsed : RoundtableKind.Proposals;
        Model.OpenRoundtable(title.Text, pickedKind, material.Text, [], chosen, leadAgent, peerReview.IsChecked == true);
    }

    sealed record TopicRow(RoundtableTopic Topic)
    {
        public string StatusTitle => Topic.Status switch
        {
            RoundtableStatus.Preparing => "各自準備中",
            RoundtableStatus.Prepared => "待公開",
            RoundtableStatus.Reviewing => "互評與整理問題",
            RoundtableStatus.Discussing => "審議中",
            RoundtableStatus.Conclusion => "等你確認結論",
            _ => "已確認",
        };

        public override string ToString() => $"{Topic.Title}　{Topic.Kind.Title()} · {StatusTitle}";
    }
}
