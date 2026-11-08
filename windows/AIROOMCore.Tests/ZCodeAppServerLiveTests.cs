using AIROOMCore;
using Xunit;

public class ZCodeAppServerLiveTests
{
    static string? FindCjs()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidate = Path.Combine(localAppData, "Programs", "ZCode", "resources", "glm", "zcode.cjs");
        return File.Exists(candidate) ? candidate : null;
    }

    static string? FindNode()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, "node.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Drives the real app-server when the desktop is installed; skips
    /// silently anywhere else. Exercises the full pipeline: account config push,
    /// setModel, send, interaction answers, turn completion.</summary>
    [Fact]
    public async Task LiveTurnAnswersThroughTheWholePipeline()
    {
        var cjs = FindCjs();
        var auth = ZCode.LoadAuth();
        if (cjs is null || auth is null || FindNode() is not { } node)
        {
            return; // The ZCode desktop (or Node) is not installed here.
        }
        var cwd = Path.Combine(Path.GetTempPath(), "airoom-zcode-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var updates = new List<string>();
            var result = await ZCodeAppServerClient.RunAsync(
                node, cjs, auth, "只回覆兩個字：收到", cwd, ZCode.DefaultModelId, "max",
                resumeId: null, delivered: () => { }, onUpdate: updates.Add,
                timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(result.Text));
            Assert.StartsWith("sess_", result.SessionID);
            Assert.NotEmpty(result.SessionID ?? "");
        }
        finally
        {
            try { Directory.Delete(cwd, recursive: true); } catch { }
        }
    }
}
