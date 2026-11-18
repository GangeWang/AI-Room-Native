using System.Text.RegularExpressions;

namespace AIROOMApp.Services;

/// <summary>
/// Starts VTube Studio so its API ports come up in a fixed order — the Windows
/// counterpart of the macOS native launcher. On Windows VTube Studio is the
/// Steam build: the exe is found under a Steam library's
/// steamapps/common/VTube Studio/, and Steam must be running for it to open.
/// Every instance takes the next free API port from 8001 up, so instances are
/// started one at a time, lowest port first, waiting for each to answer.
/// </summary>
public sealed class VTubeStudioLauncher
{
    public const string ExeRelativePath = "steamapps/common/VTube Studio/VTube Studio.exe";

    /// <summary>A hand-set path (settings) wins; otherwise Steam libraries are
    /// scanned. Returns null with a reason the UI can show.</summary>
    public static (string? Exe, string? Reason) FindExecutable(string configured)
    {
        if (configured.Trim() is { Length: > 0 } setting)
        {
            if (File.Exists(setting)) return (setting, null);
            return (null, "設定的 VTube Studio 路徑不存在。");
        }
        foreach (var library in SteamLibraries())
        {
            var candidate = Path.Combine(library, "steamapps", "common", "VTube Studio", "VTube Studio.exe");
            if (File.Exists(candidate)) return (candidate, null);
        }
        return (null, "找不到 VTube Studio。請先用 Steam 安裝並執行過一次，或在設定欄填入 VTube Studio.exe 的完整路徑。");
    }

    static string[] SteamLibraries()
    {
        var result = new List<string>();
        string? steam;
        try
        {
            steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        }
        catch { steam = null; }
        if (steam is not { Length: > 0 }) steam = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(steam))
        {
            result.Add(steam);
            var config = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(config))
            {
                // Pull the quoted "path" entries out of the VDF by line, without a VDF parser.
                foreach (Match match in Regex.Matches(File.ReadAllText(config), @"""path""\s+\t?""([^""]+)"""))
                {
                    var path = match.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(path)) result.Add(path);
                }
            }
        }
        return [.. result.Distinct()];
    }

    /// <summary>Opens one instance per port that is not answering yet, lowest
    /// port first, waiting for each before the next. Returns the ports it started.</summary>
    public async Task<List<int>> StartAsync(int[] ports, string exe, Action<int>? progress = null,
        int timeoutSeconds = 60, CancellationToken token = default)
    {
        var started = new List<int>();
        foreach (var port in ports.Distinct().Order())
        {
            if (PortIsOpen(port)) continue;
            progress?.Invoke(port);
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                }) ?? throw new InvalidOperationException("VTube Studio 無法啟動（Steam 執行中嗎？）");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("無法開啟 VTube Studio：" + error.Message, error);
            }
            double waited = 0;
            while (!PortIsOpen(port))
            {
                if (waited >= timeoutSeconds)
                {
                    throw new InvalidOperationException(
                        $"VTube Studio 已開啟，但連接埠 {port} 一直沒有回應。請在 VTS 設定開啟「Start API」（兩個實例共用設定，開一次即可），再按一次「開啟 VTube Studio」。");
                }
                await Task.Delay(500, token);
                waited += 0.5;
            }
            started.Add(port);
        }
        return started;
    }

    /// <summary>Whether something accepts TCP connections on 127.0.0.1:port.</summary>
    public static bool PortIsOpen(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(TimeSpan.FromMilliseconds(200)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
