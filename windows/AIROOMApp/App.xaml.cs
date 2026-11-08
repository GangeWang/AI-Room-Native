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
        Program.Log("App ctor begin");
        InitializeComponent();
        Program.Log("App ctor end");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Program.Log("OnLaunched begin");
        Model = new WorkbenchModel(DispatcherQueue.GetForCurrentThread());
        Program.Log("model ready");
        AppNotificationManager.Default.NotificationInvoked += (_, _) => BringToFront();
        AppNotificationManager.Default.Register();
        Program.Log("notifications registered");
        Model.NotificationRequested += (title, body) => Services.Notifier.Post(title, body);
        Window = new MainWindow();
        Program.Log("window created");
        Window.Activate();
        Program.Log("window activated");
    }

    public static void BringToFront()
    {
        Window?.Activate();
    }
}
