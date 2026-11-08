using AIROOMApp.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;

namespace AIROOMApp;

public partial class App : Application
{
    public static WorkbenchModel Model { get; private set; } = null!;
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Model = new WorkbenchModel(DispatcherQueue.GetForCurrentThread());
        AppNotificationManager.Default.NotificationInvoked += (_, _) => BringToFront();
        AppNotificationManager.Default.Register();
        Model.NotificationRequested += (title, body) => Services.Notifier.Post(title, body);
        Window = new MainWindow();
        Window.Activate();
    }

    public static void BringToFront()
    {
        Window?.Activate();
    }
}
