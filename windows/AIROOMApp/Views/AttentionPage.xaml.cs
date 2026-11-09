using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIROOMApp.Views;

public sealed partial class AttentionPage : Page
{
    public AttentionPage()
    {
        InitializeComponent();
        Render();
    }

    void Render()
    {
        var items = App.Model.VisibleAttention();
        ItemList.Children.Clear();
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowIgnoredButton.Visibility = App.Model.Archive.AttentionDismissals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in items)
        {
            var kindLabel = item.Kind switch
            {
                AttentionKind.Login => "登入",
                AttentionKind.Missing => "缺少程式",
                AttentionKind.Decision => "需要決定",
                AttentionKind.Question => "問題",
                AttentionKind.Acceptance => "驗收",
                AttentionKind.UnknownResult => "待核對",
                _ => "",
            };
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Border
                    {
                        Background = (Brush)Application.Current.Resources["StudioAmberBrush"],
                        Opacity = 0.9,
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(8, 1, 8, 2),
                        Child = new TextBlock
                        {
                            Text = kindLabel,
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                        },
                    },
                    new TextBlock { Text = item.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                },
            });
            text.Children.Add(new TextBlock
            {
                Text = item.Detail,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(text, 0);
            var ignore = new Button { Content = "忽略" };
            var captured = item;
            ignore.Click += (_, _) =>
            {
                App.Model.IgnoreAttention(captured);
                Render();
            };
            Grid.SetColumn(ignore, 1);
            row.Children.Add(text);
            row.Children.Add(ignore);
            ItemList.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["StudioPanel"],
                Padding = new Thickness(14, 10, 10, 10),
                Child = row,
            });
        }
    }

    void OnShowIgnored(object sender, RoutedEventArgs e)
    {
        App.Model.ShowIgnoredAttention();
        Render();
    }
}
