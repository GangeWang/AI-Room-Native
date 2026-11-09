using System.Text;
using Microsoft.UI.Xaml;

namespace AIROOMApp;

// A custom entry point (DISABLE_XAML_GENERATED_MAIN) so startup failures are
// written to a log file instead of dying as a silent APPCRASH in combase.
internal static class Program
{
    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIROOMNative", "startup.log");

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:HH:mm:ss.fff} {message}\n", Encoding.UTF8);
        }
        catch
        {
            // logging must never break startup
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        Log($"launch {string.Join(' ', args)}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Log($"unobserved: {e.Exception.Message}");
        try
        {
            Application.Start(callbackParams =>
            {
                Log("creating App");
                _ = new App();
            });
            Log("exited cleanly");
            return 0;
        }
        catch (Exception error)
        {
            Log($"fatal: {error}");
            _ = MessageBoxW(IntPtr.Zero, error.ToString(), "AIROOM 無法啟動", 0);
            return 1;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
