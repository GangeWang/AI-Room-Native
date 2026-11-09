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

    /// <summary>Drives the real app-server when the desktop is installed; skips
    /// silently anywhere else. Exercises the full pipeline: account config push,
    /// setModel, setMode, send, interaction answers, turn completion.</summary>
    [Fact]
    public async Task LiveTurnAnswersThroughTheWholePipeline()
    {
        var cjs = FindCjs();
        var auth = ZCode.LoadAuth();
        if (cjs is null || auth is null || ZCode.ResolveRuntime(cjs) is not { } runtime)
        {
            return; // The ZCode desktop (or a usable Node) is not installed here.
        }
        var cwd = Path.Combine(Path.GetTempPath(), "airoom-zcode-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var updates = new List<string>();
            var result = await ZCodeAppServerClient.RunAsync(
                runtime, cjs, auth, "只回覆兩個字：收到", cwd, ExecutionAccess.ReadOnly,
                ZCode.DefaultModelId, "max",
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

    /// <summary>The mode is what lets a member write: an edit-mode turn may
    /// create the file, while the shell command stays refused and the permission
    /// denial ({"decision":"deny"}) is accepted by the strict validator.</summary>
    [Fact]
    public async Task LiveWritingRunsEditButNeverRunCommands()
    {
        var cjs = FindCjs();
        var auth = ZCode.LoadAuth();
        if (cjs is null || auth is null || ZCode.ResolveRuntime(cjs) is not { } runtime)
        {
            return; // The ZCode desktop (or a usable Node) is not installed here.
        }
        var cwd = Path.Combine(Path.GetTempPath(), "airoom-zcode-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var result = await ZCodeAppServerClient.RunAsync(
                runtime, cjs, auth, "建立檔案 ok.txt，內容是 hi。接著用終端機執行 touch cmd.txt。做不到終端機那步就只回覆 DONE", cwd,
                ExecutionAccess.StagedWrite, ZCode.DefaultModelId, "max",
                resumeId: null, delivered: () => { }, onUpdate: _ => { }, timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(result.Text));
            Assert.Equal("hi", File.ReadAllText(Path.Combine(cwd, "ok.txt")).Trim());
            Assert.False(File.Exists(Path.Combine(cwd, "cmd.txt")), "shell commands are refused");
        }
        finally
        {
            try { Directory.Delete(cwd, recursive: true); } catch { }
        }
    }
}
