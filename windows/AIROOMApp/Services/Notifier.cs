using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AIROOMApp.Services;

/// <summary>Windows toast notifications, the mirror of the macOS Notifier.</summary>
public static class Notifier
{
    public static void Post(string title, string body)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title, new AppNotificationTextProperties { MaxLines = 1 })
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            // Notifications are a nicety; never let them break a run.
        }
    }
}
