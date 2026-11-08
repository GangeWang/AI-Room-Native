using System.ComponentModel;
using System.Windows.Input;
using AIROOMApp.ViewModels;
using AIROOMApp.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AIROOMApp;

public sealed partial class MainWindow : Window
{
    public ICommand ShowFromTrayCommand { get; }

    public MainWindow()
    {
        InitializeComponent();
        Title = "AIROOM Native";
        AppWindow.Resize(new SizeInt32(1120, 760));
        AppWindow.SetIcon(System.IO.Path.Combine(
            AppContext.BaseDirectory, "Assets", "airoom.ico"));

        // Warm title bar: extend the content into it and let the TitleBar grid be the drag region.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        if (Content is Grid root)
        {
            root.Background = (Brush)Application.Current.Resources["StudioBackgroundBrush"];
        }

        ShowFromTrayCommand = new RelayCommand(_ => BringToFront());
        App.Model.PropertyChanged += OnModelPropertyChanged;
        Closed += (_, _) =>
        {
            Tray.Dispose();
            App.Model.StopAll();
        };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState is WindowActivationState.Deactivated)
            {
                return; // keep running in the tray; schedules come in a later phase
            }
        };
        Navigate("workstation");
    }

    void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(WorkbenchModel.AttentionCount) or null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // NavigationViewItem has no badge in WinUI 3; reflect the count in the label.
                AttentionItem.Content = App.Model.AttentionCount > 0
                    ? $"待你處理（{App.Model.AttentionCount}）" : "待你處理";
            });
        }
    }

    void BringToFront()
    {
        Activate();
        AppWindow.Show();
    }

    void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            Navigate(tag);
        }
    }

    void Navigate(string tag) => ContentFrame.Navigate(tag switch
    {
        "attention" => typeof(AttentionPage),
        "members" => typeof(MembersPage),
        "settings" => typeof(SettingsPage),
        _ => typeof(WorkstationPage),
    });

    void OnStopAllTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        App.Model.StopAll();
    }

    void OnTrayShow(object sender, RoutedEventArgs e) => BringToFront();

    void OnTrayStopAll(object sender, RoutedEventArgs e)
    {
        App.Model.StopAll();
        Services.Notifier.Post("AIROOM", "已中止所有執行中的工作。");
    }

    void OnTrayExit(object sender, RoutedEventArgs e) => Close();
}

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}
