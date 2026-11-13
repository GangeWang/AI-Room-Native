using System.ComponentModel;
using AIROOMApp.Controls;
using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIROOMApp.Views;

/// <summary>項目會議室: projects on the left; the selected project's six steps —
/// 討論 → 定負責人 → 方案 → 執行 → 驗收 → 完成 — on the right.
/// Ported from the macOS ProjectRoomView.</summary>
public sealed partial class ProjectsPage : Page
{
    int tab;
    string noteDraft = "";
    string opinionDraft = "";
    string changeDraft = "";
    string roomDraft = "";
    int purpose;
    readonly Dictionary<MemberID, string> questionDrafts = [];
    readonly Dictionary<MemberID, string> answers = [];
    readonly Dictionary<Guid, Dictionary<string, FlowVerdict>> verdicts = [];
    TextBox? noteBox;
    TextBox? opinionBox;
    TextBox? changeBox;

    public ProjectsPage()
    {
        InitializeComponent();
        Render();
        App.Model.PropertyChanged += OnModelPropertyChanged;
    }

    WorkbenchModel Model => App.Model;

    void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null
            or nameof(WorkbenchModel.FlowVersion)
            or nameof(WorkbenchModel.RoomsVersion)
            or nameof(WorkbenchModel.RoundtableVersion))
        {
            _ = DispatcherQueue.TryEnqueue(Render);
        }
    }

    void Render()
    {
        RenderProjectList();
        RenderStage();
    }

    // MARK: Project list

    void RenderProjectList()
    {
        var flows = Model.Flows();
        var selectedID = Model.SelectedFlowID ?? flows.FirstOrDefault()?.Id;
        FlowList.ItemsSource = flows.Select(flow => new FlowRow(flow)).ToList();
        var index = flows.FindIndex(flow => flow.Id == selectedID);
        if (FlowList.SelectedIndex != index) FlowList.SelectedIndex = index;
        NewFlowButton.IsEnabled = !Model.FlowRunning;
        DeleteFlowButton.IsEnabled = !Model.FlowRunning && flows.Count > 0;
    }

    sealed record FlowRow(ProjectFlow Flow)
    {
        string FolderName()
        {
            var name = Flow.Folder.TrimEnd('\\', '/');
            var index = name.LastIndexOf('\\');
            return index >= 0 ? name[(index + 1)..] : name;
        }

        public override string ToString() => $"{Flow.Title}　{Flow.Step.Title()} · {FolderName()}";
    }

    ProjectFlow? SelectedFlow()
    {
        var flows = Model.Flows();
        var selectedID = Model.SelectedFlowID ?? flows.FirstOrDefault()?.Id;
        return flows.FirstOrDefault(flow => flow.Id == selectedID) ?? flows.FirstOrDefault();
    }

    void OnFlowSelected(object sender, SelectionChangedEventArgs args)
    {
        if (FlowList.SelectedItem is FlowRow row)
        {
            Model.SelectedFlowID = row.Flow.Id;
            RenderStage();
        }
    }

    async void OnDeleteFlow(object sender, RoutedEventArgs args)
    {
        var flow = SelectedFlow();
        if (flow is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "刪除這個專案的紀錄和副本？",
            Content = $"「{flow.Title}」的討論紀錄和工作副本都會移除；原始資料夾不受影響。",
            PrimaryButtonText = "刪除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Model.DeleteFlow(flow.Id);
        }
    }

    async void OnNewFlow(object sender, RoutedEventArgs args)
    {
        var folderBox = new TextBox { PlaceholderText = "C:\\路徑\\專案資料夾", IsReadOnly = true };
        var browse = new Button { Content = "選取…" };
        var folderPath = "";
        browse.Click += async (_, _) => folderPath = await PickFolder() ?? folderPath;
        var nameBox = new TextBox { PlaceholderText = "專案名稱（可留空，用資料夾名稱）" };
        var memberPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var boxes = new List<(CheckBox Box, MemberID Agent)>();
        foreach (var member in Model.EnabledMemberIDs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var avatar = new StudioAvatar();
            var (art, crop) = Model.AvatarOrPortrait(Model.Card(member));
            avatar.Set(member, Model.MemberName(member), Model.Card(member)?.Color, 22, art, crop, null, null, false);
            row.Children.Add(avatar);
            row.Children.Add(new TextBlock
            {
                Text = Model.MemberName(member),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var box = new CheckBox { Content = row, IsChecked = Model.EnabledMemberIDs.Contains(member) };
            boxes.Add((box, member));
            memberPanel.Children.Add(box);
        }
        var levelPicker = new ComboBox { MinWidth = 220 };
        foreach (ReviewLevel level in Enum.GetValues<ReviewLevel>()) levelPicker.Items.Add(level.Title());
        levelPicker.SelectedIndex = (int)ReviewLevel.Standard;
        var detail = new TextBlock { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        levelPicker.SelectionChanged += (_, _) =>
            detail.Text = ((ReviewLevel)levelPicker.SelectedIndex).Detail();
        detail.Text = ReviewLevel.Standard.Detail();
        var hint = new TextBlock
        {
            Text = "成員可以只讀查看這個資料夾；所有修改都在副本進行，你驗收通過才寫回。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };
        var form = new StackPanel { Spacing = 10 };
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folderRow.Children.Add(folderBox);
        folderRow.Children.Add(browse);
        form.Children.Add(new TextBlock { Text = "專案資料夾" });
        form.Children.Add(folderRow);
        form.Children.Add(nameBox);
        form.Children.Add(new TextBlock { Text = "參加成員（至少兩位）" });
        form.Children.Add(memberPanel);
        form.Children.Add(new TextBlock { Text = "複核程度" });
        form.Children.Add(levelPicker);
        form.Children.Add(detail);
        form.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "請大家一起來：新增專案",
            Content = form,
            PrimaryButtonText = "進入會議室",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.IsPrimaryButtonEnabled = false;
        folderBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
            folderBox.Text.Trim().Length > 0 && boxes.Count(entry => entry.Box.IsChecked == true) >= 2;
        foreach (var (box, _) in boxes)
        {
            box.Checked += (_, _) => dialog.IsPrimaryButtonEnabled =
                folderBox.Text.Trim().Length > 0 && boxes.Count(entry => entry.Box.IsChecked == true) >= 2;
            box.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled =
                folderBox.Text.Trim().Length > 0 && boxes.Count(entry => entry.Box.IsChecked == true) >= 2;
        }
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Model.CreateFlow(nameBox.Text, folderPath.Trim().Length > 0 ? folderPath : folderBox.Text,
                boxes.Where(entry => entry.Box.IsChecked == true).Select(entry => entry.Agent).ToList(),
                (ReviewLevel)levelPicker.SelectedIndex);
        }
    }

    async Task<string?> PickFolder()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            (nint)Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd).Value);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    // MARK: Detail

    void RenderStage()
    {
        Stage.Children.Clear();
        var flow = SelectedFlow();
        if (flow is null)
        {
            Stage.Children.Add(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "請大家一起來", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock { Text = "新增一個專案資料夾、選至少兩位成員，就能進入會議室。", Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                },
            });
            return;
        }
        RenderHeader(flow);
        RenderNextStep(flow);
        Stage.Children.Add(TabBar());
        switch (tab)
        {
            case 1: RenderBoard(flow); break;
            case 2: RenderReports(flow); break;
            default: RenderRoom(flow); break;
        }
    }

    UIElement TabBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var names = new[] { "會議室", "看板", "成員匯報" };
        for (var index = 0; index < names.Length; index++)
        {
            var captured = index;
            var button = new Button
            {
                Content = names[index],
                Padding = new Thickness(14, 4, 14, 6),
                FontWeight = index == tab ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Background = (Brush)Application.Current.Resources[index == tab ? "StudioAmberBrush" : "StudioSurfaceBrush"],
            };
            button.Click += (_, _) => { tab = captured; RenderStage(); };
            bar.Children.Add(button);
        }
        return bar;
    }

    void RenderHeader(ProjectFlow flow)
    {
        var panel = new StackPanel { Spacing = 8 };
        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(new TextBlock { Text = flow.Title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        heading.Children.Add(new TextBlock { Text = flow.Folder, FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(heading, 0);
        titleRow.Children.Add(heading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(new TextBlock
        {
            Text = $"第 {flow.Segment} 段 · 呼叫 {flow.Calls} 次",
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (flow.Step == FlowStep.Executing)
        {
            if (flow.Paused)
            {
                var resume = new Button { Content = "繼續", IsEnabled = !Model.FlowRunning };
                resume.Click += (_, _) => Model.ResumeFlow(flow.Id);
                actions.Children.Add(resume);
            }
            else if (Model.FlowRunning)
            {
                var pause = new Button { Content = "暫停" };
                pause.Click += (_, _) => Model.PauseFlow(flow.Id);
                actions.Children.Add(pause);
            }
        }
        if (Model.FlowRunning)
        {
            var stop = new Button { Content = "中止" };
            stop.Click += (_, _) => Model.StopFlow();
            actions.Children.Add(stop);
        }
        Grid.SetColumn(actions, 1);
        titleRow.Children.Add(actions);
        panel.Children.Add(titleRow);

        var steps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var all = Enum.GetValues<FlowStep>();
        var reached = Array.IndexOf(all, flow.Step);
        for (var index = 0; index < all.Length; index++)
        {
            var here = index == reached;
            var done = index < reached;
            steps.Children.Add(new Border
            {
                Padding = new Thickness(8, 2, 8, 4),
                CornerRadius = new CornerRadius(9),
                Background = (Brush)Application.Current.Resources[here ? "StudioAmberBrush" : "StudioSurfaceBrush"],
                Opacity = here ? 0.9 : done ? 0.6 : 0.35,
                Child = new TextBlock
                {
                    Text = $"{index + 1} {all[index].Title()}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(here ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Gray),
                },
            });
            if (index < all.Length - 1)
            {
                steps.Children.Add(new TextBlock { Text = "›", FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });
            }
        }
        panel.Children.Add(steps);

        if (Model.FlowRunning && Model.FlowActivity is { } activity)
        {
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new ProgressRing { Width = 16, Height = 16, IsActive = true },
                    new TextBlock { Text = activity, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }
        Stage.Children.Add(Panel(panel));
    }

    // MARK: 下一步

    void RenderNextStep(ProjectFlow flow)
    {
        var panel = new StackPanel { Spacing = 10 };
        switch (flow.Step)
        {
            case FlowStep.Discussion or FlowStep.Leader:
                panel.Children.Add(new TextBlock
                {
                    Text = "下一步：說清楚要做什麼，然後選負責人起草方案",
                    FontSize = 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "在「會議室」分頁聊需求；成員可以只讀查看專案檔案核實現狀，不會改檔案。",
                    FontSize = 12,
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap,
                });
                var leads = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var member in flow.Members)
                {
                    var button = new Button { Content = $"請 {Model.MemberName(member)} 當負責人，起草方案" };
                    var captured = member;
                    button.Click += (_, _) => Model.ChooseFlowLead(flow.Id, captured);
                    button.IsEnabled = !Busy;
                    leads.Children.Add(button);
                }
                panel.Children.Add(leads);
                break;
            case FlowStep.Plan:
                RenderPlanCard(flow, panel);
                break;
            case FlowStep.Executing:
                RenderExecutingCard(flow, panel);
                break;
            case FlowStep.Acceptance:
                RenderAcceptanceCard(flow, panel);
                break;
            case FlowStep.Completed:
                panel.Children.Add(new TextBlock { Text = "整段工作總結", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                panel.Children.Add(RichText.Block(flow.CompletionReport));
                var again = new Button { Content = "開始新的一段工作", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                again.Click += (_, _) => Model.StartNewSegment(flow.Id);
                again.IsEnabled = !Busy;
                panel.Children.Add(again);
                break;
        }
        Stage.Children.Add(Panel(panel));
    }

    void RenderPlanCard(ProjectFlow flow, StackPanel panel)
    {
        if (flow.Plan is not { } plan) return;
        panel.Children.Add(new TextBlock
        {
            Text = $"方案（第 {plan.Version} 版，負責人：{(flow.Lead is { } lead ? Model.MemberName(lead) : "")}）",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(RichText.Block($"**目標**：{plan.Objective}\n**範圍**：{plan.Scope}"));
        if (plan.Deliverables.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "交付：" + string.Join("；", plan.Deliverables), TextWrapping = TextWrapping.Wrap });
        }
        if (plan.Acceptance.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "怎樣算完成：" + string.Join("；", plan.Acceptance), TextWrapping = TextWrapping.Wrap });
        }
        foreach (var round in plan.Rounds)
        {
            var block = new StackPanel { Spacing = 3 };
            block.Children.Add(new TextBlock
            {
                Text = $"{round.Title}：{round.Goal}",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            foreach (var task in plan.TasksIn(round))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(AvatarFor(task.Member, 18));
                row.Children.Add(new TextBlock { Text = task.Title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                row.Children.Add(new TextBlock { Text = task.Goal, FontSize = 11, Opacity = 0.7, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                if (task.Files.Count > 0)
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = string.Join("、", task.Files),
                        FontSize = 10,
                        FontFamily = new FontFamily("Consolas"),
                        Opacity = 0.6,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                }
                block.Children.Add(row);
            }
            panel.Children.Add(block);
        }
        panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"] });
        var levelPicker = new ComboBox { MinWidth = 420 };
        foreach (ReviewLevel level in Enum.GetValues<ReviewLevel>()) levelPicker.Items.Add(level.Title());
        levelPicker.SelectedIndex = (int)flow.ReviewLevel;
        panel.Children.Add(StackRow(new TextBlock { Text = "複核程度", VerticalAlignment = VerticalAlignment.Center }, levelPicker));
        var detail = new TextBlock { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        levelPicker.SelectionChanged += (_, _) => detail.Text = ((ReviewLevel)levelPicker.SelectedIndex).Detail();
        detail.Text = flow.ReviewLevel.Detail();
        panel.Children.Add(detail);
        panel.Children.Add(ParallelControl(flow));

        noteBox = DraftBox(noteDraft, "給第一輪的附言（可留空）", value => noteDraft = value);
        var confirm = new Button
        {
            Content = "確認方案，開工",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = !Busy,
        };
        confirm.Click += (_, _) =>
        {
            Model.ConfirmPlan(flow.Id, (ReviewLevel)levelPicker.SelectedIndex, noteDraft);
            noteDraft = "";
            if (noteBox is not null) noteBox.Text = "";
        };
        panel.Children.Add(StackRow(noteBox, confirm));

        changeBox = DraftBox(changeDraft, "要改一下：寫下要負責人改什麼", value => changeDraft = value);
        var change = new Button { Content = "要改一下", IsEnabled = !Busy && changeDraft.Trim().Length > 0 };
        change.Click += (_, _) =>
        {
            Model.RequestPlanChange(flow.Id, changeDraft);
            changeDraft = "";
            if (changeBox is not null) changeBox.Text = "";
        };
        panel.Children.Add(StackRow(changeBox, change));
    }

    void RenderExecutingCard(ProjectFlow flow, StackPanel panel)
    {
        if (flow.ActiveRound is not { } round) return;
        var tasks = flow.Plan?.TasksIn(round) ?? [];
        panel.Children.Add(new TextBlock
        {
            Text = $"第 {flow.CurrentRound + 1} 輪：{round.Title}",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock { Text = round.Goal, FontSize = 13, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        if (round.Status == FlowRoundStatus.Pending && !Model.FlowRunning)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "下一步：開始這一輪前，確認按原計劃開始還是要求修改方案。附言會交給這一輪的成員，並在檢查時核對。",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            });
            noteBox = DraftBox(noteDraft, "附言（可留空）", value => noteDraft = value);
            var start = new Button
            {
                Content = "按原計劃開始",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            start.Click += (_, _) =>
            {
                Model.StartRound(flow.Id, noteDraft);
                noteDraft = "";
                if (noteBox is not null) noteBox.Text = "";
            };
            panel.Children.Add(StackRow(noteBox, start));
            changeBox = DraftBox(changeDraft, "要改一下：寫下要負責人改什麼", value => changeDraft = value);
            var change = new Button { Content = "要求修改方案", IsEnabled = changeDraft.Trim().Length > 0 };
            change.Click += (_, _) =>
            {
                Model.RequestPlanChange(flow.Id, changeDraft);
                changeDraft = "";
                if (changeBox is not null) changeBox.Text = "";
            };
            panel.Children.Add(StackRow(changeBox, change));
            return;
        }
        var countRow = new Grid { ColumnSpacing = 8 };
        countRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        countRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var count = new TextBlock
        {
            Text = $"已完成 {tasks.Count(task => task.Status == FlowTaskStatus.Done)}/{tasks.Count} 項任務",
            FontSize = 12,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 0);
        countRow.Children.Add(count);
        var parallel = ParallelControl(flow);
        Grid.SetColumn(parallel, 1);
        countRow.Children.Add(parallel);
        panel.Children.Add(countRow);
        foreach (var wait in flow.QuotaWaits) panel.Children.Add(QuotaBanner(flow, wait));
        foreach (var task in tasks.Where(task => task.Status == FlowTaskStatus.Disputed))
        {
            if (Model.LeadRulingFor(task.Id) is { } ruling)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"「{task.Title}」的複核異議由負責人 {Model.MemberName(ruling)} 裁定中……",
                    FontSize = 12,
                    Opacity = 0.8,
                });
            }
            else
            {
                panel.Children.Add(DisputeCard(flow, task));
            }
        }
        if (tasks.FirstOrDefault(task => task.Status == FlowTaskStatus.Failed) is { } failed)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"「{failed.Title}」沒有完成：{failed.Error ?? ""}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                TextWrapping = TextWrapping.Wrap,
            });
            var again = new Button { Content = "繼續（重新派這一輪未完成的任務）", IsEnabled = !Model.FlowRunning };
            again.Click += (_, _) => Model.ResumeFlow(flow.Id);
            panel.Children.Add(again);
        }
        else if (flow.Paused && !Model.FlowRunning)
        {
            panel.Children.Add(new TextBlock { Text = "已暫停。按「繼續」接著派任務。", FontSize = 12, Opacity = 0.75 });
        }
    }

    void RenderAcceptanceCard(ProjectFlow flow, StackPanel panel)
    {
        if (flow.ActiveRound is not { } round) return;
        panel.Children.Add(new TextBlock
        {
            Text = $"下一步：查看第 {flow.CurrentRound + 1} 輪成品並驗收",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(RichText.Block(round.Report));
        var changes = Model.FlowRoundChanges(flow.Id);
        panel.Children.Add(new TextBlock
        {
            Text = "改動的檔案（點一下看前後對比）：",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (var change in changes)
        {
            var captured = change;
            var button = new Button
            {
                Content = $"{captured.Kind.ToRaw()} {captured.Path}",
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Padding = new Thickness(6, 2, 6, 4),
            };
            button.Click += (_, _) => ShowPreview(flow.Id, captured.Path);
            panel.Children.Add(button);
        }
        if (changes.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "這一輪沒有檔案改動。", FontSize = 12, Opacity = 0.7 });
        }
        panel.Children.Add(new TextBlock
        {
            Text = "通過後才會寫進你的專案資料夾；寫入前會檢查衝突。",
            FontSize = 11,
            Opacity = 0.7,
        });
        var accept = new Button
        {
            Content = "通過",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = !Busy,
        };
        accept.Click += (_, _) => Model.AcceptRound(flow.Id);
        opinionBox = DraftBox(opinionDraft, "駁回意見：哪裡要改", value => opinionDraft = value);
        var reject = new Button { Content = "駁回", IsEnabled = !Busy && opinionDraft.Trim().Length > 0 };
        reject.Click += (_, _) =>
        {
            Model.ReturnRound(flow.Id, opinionDraft);
            opinionDraft = "";
            if (opinionBox is not null) opinionBox.Text = "";
        };
        panel.Children.Add(StackRow(accept, opinionBox, reject));
    }

    async void ShowPreview(Guid flowID, string path)
    {
        var pair = Model.FlowPreview(flowID, path);
        var before = new TextBox
        {
            Text = pair?.Before ?? "無法讀取",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            Height = 360,
        };
        var after = new TextBox
        {
            Text = pair?.After ?? "無法讀取",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            Height = 360,
        };
        var columns = new Grid { ColumnSpacing = 12 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(before, 0);
        Grid.SetColumn(after, 1);
        columns.Children.Add(before);
        columns.Children.Add(after);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"變更審查: {path}",
            Content = new ScrollViewer { Content = columns, MaxHeight = 400 },
            CloseButtonText = "關閉",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    // MARK: 異議裁定 and 額度等待

    UIElement DisputeCard(ProjectFlow flow, FlowTask task)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"「{task.Title}」：{Model.MemberName(task.Member)} 對複核意見有異議，等你裁定",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"支持作者：這條不用改，並返還一次退回次數（每項任務最多 {ProjectFlowEngine.MaxWaivers} 次，已用 {task.Waivers} 次）。維持複核：作者必須修正，不能再提異議。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        var choices = verdicts.GetValueOrDefault(task.Id, []);
        foreach (var dispute in task.Disputes)
        {
            var finding = task.OpenFindings.FirstOrDefault(candidate => candidate.Id == dispute.FindingID);
            if (finding is null) continue;
            var cell = new StackPanel { Spacing = 4 };
            cell.Children.Add(new TextBlock
            {
                Text = $"{(task.Reviewer is { } reviewer ? Model.MemberName(reviewer) : "複核者")}：" +
                       (finding.Location.Length > 0 ? finding.Location + "：" : "") + finding.Request,
                TextWrapping = TextWrapping.Wrap,
            });
            cell.Children.Add(new TextBlock
            {
                Text = "作者異議：" + dispute.Reason,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            });
            if (dispute.LeadNote is { } note)
            {
                cell.Children.Add(new TextBlock
                {
                    Text = note,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            var picker = new ComboBox { MinWidth = 220 };
            picker.Items.Add("支持作者");
            picker.Items.Add("維持複核");
            picker.SelectedIndex = choices.TryGetValue(dispute.FindingID, out var verdict)
                ? verdict == FlowVerdict.Author ? 0 : 1 : -1;
            picker.SelectionChanged += (_, _) =>
            {
                var updated = new Dictionary<string, FlowVerdict>(choices);
                if (picker.SelectedIndex == 0) updated[dispute.FindingID] = FlowVerdict.Author;
                else if (picker.SelectedIndex == 1) updated[dispute.FindingID] = FlowVerdict.Review;
                else updated.Remove(dispute.FindingID);
                verdicts[task.Id] = updated;
            };
            cell.Children.Add(picker);
            panel.Children.Add(new Border
            {
                Child = cell,
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
            });
        }
        if (task.Summary.Length > 0)
        {
            panel.Children.Add(new Expander
            {
                Header = "作者這次的說明",
                Content = RichText.Block(task.Summary),
            });
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var sideAll = new Button { Content = "支持全部異議" };
        sideAll.Click += (_, _) => Model.RuleDisputes(flow.Id, task.Id,
            task.Disputes.ToDictionary(dispute => dispute.FindingID, _ => FlowVerdict.Author));
        var upholdAll = new Button { Content = "維持全部複核意見" };
        upholdAll.Click += (_, _) => Model.RuleDisputes(flow.Id, task.Id,
            task.Disputes.ToDictionary(dispute => dispute.FindingID, _ => FlowVerdict.Review));
        var submit = new Button
        {
            Content = "提交逐條裁定",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = task.Disputes.All(dispute => choices.ContainsKey(dispute.FindingID)),
        };
        submit.Click += (_, _) => Model.RuleDisputes(flow.Id, task.Id, verdicts.GetValueOrDefault(task.Id, []));
        buttons.Children.Add(sideAll);
        buttons.Children.Add(upholdAll);
        buttons.Children.Add(submit);
        panel.Children.Add(buttons);
        return new Border
        {
            Child = panel,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["StudioAmberBrush"],
            Opacity = 0.14,
        };
    }

    UIElement QuotaBanner(ProjectFlow flow, FlowQuotaWait wait)
    {
        var name = Model.MemberName(wait.Agent);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = wait.Ready ? $"{name} 的額度應該恢復了" : $"{name} 的額度用完了",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"停在：{wait.Stage}。" + (wait.ResetAt is { } reset
                ? $"預計 {reset.ToLocalTime():MM/dd HH:mm} 恢復，到時會通知你。"
                : "官方還沒給恢復時間，可以重新查詢額度。"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        if (wait.Message.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = wait.Message, FontSize = 11, Opacity = 0.65, MaxLines = 2, TextWrapping = TextWrapping.Wrap });
        }
        panel.Children.Add(new TextBlock { Text = "不會自動重送。其他成員的任務照常進行。", FontSize = 11, Opacity = 0.7 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var resume = new Button
        {
            Content = "接著做",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = !Model.FlowRunning,
        };
        resume.Click += (_, _) => Model.ResumeFlow(flow.Id);
        buttons.Children.Add(resume);
        var round = flow.ActiveRound;
        var plan = flow.Plan;
        var hasWork = plan is not null && round is not null && plan.Tasks.Any(task =>
            task.RoundKey == round.Key && task.Member == wait.Agent
            && task.Status is FlowTaskStatus.Pending or FlowTaskStatus.Failed or FlowTaskStatus.Fixing);
        foreach (var other in flow.Members.Where(member => member != wait.Agent))
        {
            var captured = other;
            var hand = new Button { Content = $"交給 {Model.MemberName(other)} 接手", IsEnabled = hasWork };
            hand.Click += (_, _) => Model.HandOverQuotaWork(flow.Id, wait.Agent, captured);
            buttons.Children.Add(hand);
        }
        var executor = Model.ExecutorOf(wait.Agent);
        var check = new Button
        {
            Content = "重新查詢額度",
            IsEnabled = executor is Executor.Claude or Executor.Codex && !Model.IsQuotaRefreshing(executor),
        };
        check.Click += (_, _) => Model.RefreshQuota(executor);
        buttons.Children.Add(check);
        panel.Children.Add(buttons);
        return new Border
        {
            Child = panel,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
        };
    }

    // MARK: 會議室

    void RenderRoom(ProjectFlow flow)
    {
        var room = flow.RoomID is { } roomID ? Model.RoomSnapshot(roomID) : null;
        var panel = new StackPanel { Spacing = 10 };
        if (room is not null)
        {
            foreach (var entry in room.Entries.Where(item => item.Speaker != Speaker.System))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (entry.Agent is { } agent) row.Children.Add(AvatarFor(agent, 24));
                else
                {
                    row.Children.Add(new TextBlock { Text = "🙂", FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
                }
                var body = new StackPanel { Spacing = 3 };
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                head.Children.Add(new TextBlock { Text = entry.DisplayName, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                if (entry.Delivery == RoomDelivery.InProgress)
                {
                    head.Children.Add(new ProgressRing { Width = 12, Height = 12, IsActive = true, VerticalAlignment = VerticalAlignment.Center });
                }
                body.Children.Add(head);
                body.Children.Add(RichText.Block(entry.Body));
                row.Children.Add(body);
                panel.Children.Add(row);
            }
            if (room.Entries.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "在這裡說清楚要做什麼、有什麼要求。每位成員會依序回答一次，然後等你。",
                    FontSize = 12,
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }
        var working = flow.Step is FlowStep.Executing or FlowStep.Acceptance;
        MemberID? mentioned = null;
        if (room is not null && room.Participants.Count > 0)
        {
            var found = LoungeEngine.Mentions(roomDraft, Model.Archive.Members
                .Where(member => flow.Members.Contains(member.Agent)).ToList()).FirstOrDefault();
            if (found != default) mentioned = found;
        }
        if (working && mentioned is { } who)
        {
            var purposePicker = new ComboBox { MinWidth = 320 };
            purposePicker.Items.Add("只是問問");
            purposePicker.Items.Add("安排新任務");
            purposePicker.Items.Add("補充到他目前的任務");
            purposePicker.SelectedIndex = purpose;
            purposePicker.SelectionChanged += (_, _) => purpose = purposePicker.SelectedIndex;
            panel.Children.Add(StackRow(new TextBlock { Text = $"點名 {Model.MemberName(who)}：", VerticalAlignment = VerticalAlignment.Center }, purposePicker));
            panel.Children.Add(new TextBlock
            {
                Text = purpose == 1 ? $"追加一項任務到本輪，交給 {Model.MemberName(who)}。"
                    : purpose == 2 ? $"把補充要求送到 {Model.MemberName(who)} 正在做的任務。"
                    : $"{Model.MemberName(who)} 只回答，不改任務。",
                FontSize = 11,
                Opacity = 0.7,
            });
        }
        var input = new TextBox
        {
            PlaceholderText = "在會議室發言（用 @名字 點名）",
            Text = roomDraft,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        input.TextChanged += (_, _) => { roomDraft = input.Text; };
        input.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; SendRoom(flow, input); }
        };
        var sendRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sendRow.Children.Add(input);
        if (Model.RoomRunning)
        {
            var stop = new Button { Content = "停止" };
            stop.Click += (_, _) => Model.StopRoom();
            sendRow.Children.Add(stop);
        }
        var send = new Button { Content = "送出", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        send.Click += (_, _) => SendRoom(flow, input);
        sendRow.Children.Add(send);
        panel.Children.Add(sendRow);
        Stage.Children.Add(Panel(panel));
    }

    void SendRoom(ProjectFlow flow, TextBox input)
    {
        var text = input.Text.Trim();
        if (text.Length == 0) return;
        var working = flow.Step is FlowStep.Executing or FlowStep.Acceptance;
        var mentioned = LoungeEngine.Mentions(text, Model.Archive.Members
            .Where(member => flow.Members.Contains(member.Agent)).ToList()).FirstOrDefault();
        if (working && mentioned is { } member && purpose != 0)
        {
            var body = text.Replace($"@{Model.MemberName(member)}", "").Trim();
            if (purpose == 1) Model.AddFlowTask(flow.Id, member, body);
            else Model.SupplementFlowTask(flow.Id, member, body);
        }
        else
        {
            Model.SayInProject(flow.Id, text);
        }
        input.Text = "";
        purpose = 0;
        roomDraft = "";
    }

    // MARK: 看板

    void RenderBoard(ProjectFlow flow)
    {
        var tasks = flow.Plan?.Tasks ?? [];
        var columns = new Grid { ColumnSpacing = 12 };
        for (var index = 0; index < 3; index++) columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var names = new[] { "待開始", "進行中", "已完成" };
        for (var column = 0; column < 3; column++)
        {
            var list = new StackPanel { Spacing = 8 };
            list.Children.Add(new TextBlock { Text = names[column], FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            foreach (var task in tasks.Where(task => task.Status.Column() == column))
            {
                var cell = new StackPanel { Spacing = 4 };
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                head.Children.Add(AvatarFor(task.Member, 18));
                head.Children.Add(new TextBlock { Text = task.Title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                cell.Children.Add(head);
                cell.Children.Add(new TextBlock
                {
                    Text = StatusTitle(task),
                    FontSize = 11,
                    Opacity = 0.75,
                    Foreground = new SolidColorBrush(task.Status == FlowTaskStatus.Failed ? Microsoft.UI.Colors.Orange : Microsoft.UI.Colors.Gray),
                    TextWrapping = TextWrapping.Wrap,
                });
                if (task.Files.Count > 0)
                {
                    cell.Children.Add(new TextBlock
                    {
                        Text = string.Join("、", task.Files),
                        FontSize = 10,
                        FontFamily = new FontFamily("Consolas"),
                        Opacity = 0.6,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                }
                if (task.Findings.Length > 0)
                {
                    cell.Children.Add(new TextBlock { Text = task.Findings, FontSize = 10, Opacity = 0.7, MaxLines = 3, TextWrapping = TextWrapping.Wrap });
                }
                if (task.Waivers > 0)
                {
                    cell.Children.Add(new TextBlock { Text = $"異議返還已用 {task.Waivers}/{ProjectFlowEngine.MaxWaivers} 次", FontSize = 10, Opacity = 0.7 });
                }
                list.Children.Add(new Border
                {
                    Child = cell,
                    Padding = new Thickness(8),
                    CornerRadius = new CornerRadius(8),
                    Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
                });
            }
            Grid.SetColumn(list, column);
            columns.Children.Add(list);
        }
        Stage.Children.Add(Panel(columns));
    }

    string StatusTitle(FlowTask task) => task.Status switch
    {
        FlowTaskStatus.Pending => "待開始",
        FlowTaskStatus.Running => "執行中",
        FlowTaskStatus.Reviewing => "複核中" + (task.Reviewer is { } reviewer ? $"（{Model.MemberName(reviewer)}）" : ""),
        FlowTaskStatus.Fixing => $"依複核修正（第 {task.Returns} 次退回）",
        FlowTaskStatus.Done => "完成" + (task.Changes.Count > 0 ? $" · 改了 {task.Changes.Count} 個檔案" : ""),
        FlowTaskStatus.Failed => "沒有完成：" + (task.Error ?? ""),
        _ => Model.LeadRulingFor(task.Id) is not null
            ? "對複核有異議，負責人裁定中"
            : "對複核有異議，等你裁定",
    };

    // MARK: 成員匯報

    void RenderReports(ProjectFlow flow)
    {
        var stack = new StackPanel { Spacing = 12 };
        foreach (var member in flow.Members)
        {
            var panel = new StackPanel { Spacing = 8 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(AvatarFor(member, 26));
            head.Children.Add(new TextBlock { Text = Model.MemberName(member), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(head);
            var mine = flow.Plan?.Tasks.Where(task => task.Member == member).ToList() ?? [];
            if (mine.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "還沒有分到任務。", FontSize = 12, Opacity = 0.7 });
            }
            foreach (var task in mine)
            {
                panel.Children.Add(new Expander
                {
                    Header = new TextBlock { Text = $"{task.Title} · {task.Status.ToRaw()}", FontSize = 12 },
                    Content = RichText.Block(task.Summary.Length == 0 ? "（還沒有匯報）" : task.Summary),
                });
            }
            var input = new TextBox
            {
                PlaceholderText = "問 TA 做了什麼（只讀回答，不改檔案）",
                Text = questionDrafts.GetValueOrDefault(member, ""),
            };
            input.TextChanged += (_, _) => questionDrafts[member] = input.Text;
            var ask = new Button { Content = "提問", IsEnabled = !Model.FlowRunning && questionDrafts.GetValueOrDefault(member, "").Trim().Length > 0 };
            ask.Click += (_, _) =>
            {
                var question = input.Text;
                input.Text = "";
                questionDrafts[member] = "";
                Model.AskFlowMember(flow.Id, member, question, reply =>
                {
                    RunOnUI(() =>
                    {
                        answers[member] = reply;
                        RenderStage();
                    });
                });
            };
            panel.Children.Add(StackRow(input, ask));
            if (answers.TryGetValue(member, out var answer))
            {
                panel.Children.Add(RichText.Block(answer));
            }
            stack.Children.Add(Panel(panel));
        }
        Stage.Children.Add(stack);
    }

    // MARK: shared helpers

    bool Busy => Model.FlowRunning || Model.RoomRunning;

    void RunOnUI(Action action) => DispatcherQueue.TryEnqueue(() => action());

    TextBox DraftBox(string text, string placeholder, Action<string> onChanged)
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = text, MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.TextChanged += (_, _) => onChanged(box.Text);
        return box;
    }

    StackPanel ParallelControl(ProjectFlow flow)
    {
        var picker = new ComboBox { MinWidth = 90 };
        picker.Items.Add("1");
        picker.Items.Add("2");
        picker.Items.Add("3");
        picker.SelectedIndex = Math.Clamp(flow.MaxParallel, 1, 3) - 1;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex is { } value && value >= 0) Model.SetFlowParallel(flow.Id, value + 1);
        };
        return StackRow(
            new TextBlock { Text = "同時進行的任務", FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            picker);
    }

    static StackPanel StackRow(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    StudioAvatar AvatarFor(MemberID agent, double size)
    {
        var card = Model.Card(agent);
        var avatar = new StudioAvatar();
        var (art, crop) = Model.AvatarOrPortrait(card);
        avatar.Set(agent, Model.MemberName(agent), card?.Color, size, art, crop, null, null, false);
        return avatar;
    }

    static UIElement Panel(UIElement content) => new Border
    {
        Child = content,
        Padding = new Thickness(14),
        CornerRadius = new CornerRadius(12),
        Background = (Brush)Application.Current.Resources["StudioSurfaceBrush"],
    };
}
