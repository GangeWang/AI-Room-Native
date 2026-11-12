using System.Text.Json;
using AIROOMCore;
using Xunit;

public class QuotaTests
{
    static JsonElement Json(string source) => JsonDocument.Parse(source).RootElement.Clone();

    [Fact]
    public void CodexQuotaBucketsAndRemaining()
    {
        var sample = Json("""
            {"rateLimitsByLimitId":{"standard":{"limitName":"Codex","primary":{"usedPercent":32.4,"windowDurationMins":300,"resetsAt":1800000000},"secondary":{"usedPercent":67.5,"windowDurationMins":10080}}}}
            """);
        var windows = QuotaProjection.Codex(sample);
        Assert.Equal(2, windows.Count);
        Assert.Equal("5 小時", windows[0].Label);
        Assert.Equal(67.6, windows[0].RemainingPercent, precision: 3);
        Assert.NotNull(windows[0].ResetsAt);
        Assert.Equal(32.5, windows[1].RemainingPercent, precision: 3);
    }

    [Fact]
    public void QuotaRejectsMalformedValuesAndNeverAssumesZero()
    {
        var sample = Json("""
            {"rateLimits":{"primary":{"usedPercent":120},"secondary":{"usedPercent":"0"}}}
            """);
        Assert.Empty(QuotaProjection.Codex(sample));
        var quota = new AccountQuota(Executor.Codex, DateTimeOffset.Now, []);
        Assert.Equal(QuotaStatus.Unavailable, quota.Status);
        Assert.False(quota.IsFresh(quota.CheckedAt.AddMinutes(16)));
    }

    [Fact]
    public void CodexProtocolExtractsRateLimitResponse()
    {
        var wire = new QuotaWire(Executor.Codex);
        var line = """{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":80,"windowDurationMins":300}}}}""";
        Assert.Empty(wire.Receive(line));
        Assert.True(wire.Finished);
        Assert.Equal(20, wire.Windows[0].RemainingPercent);
    }

    [Fact]
    public void ClaudeUsageRequiresBuiltinVerification()
    {
        var wire = new QuotaWire(Executor.Claude, "check");
        var invalid = """{"type":"control_response","response":{"request_id":"check","subtype":"success","response":{"commands":[{"name":"usage","builtin":false}]}}}""";
        Assert.Empty(wire.Receive(invalid));
        Assert.True(wire.Failed);
        Assert.False(wire.SentQuery);
    }

    [Fact]
    public void ClaudeUsageNativeReport()
    {
        var wire = new QuotaWire(Executor.Claude, "check");
        var ready = """{"type":"control_response","response":{"request_id":"check","subtype":"success","response":{"commands":[{"name":"usage","builtin":true}]}}}""";
        var commands = wire.Receive(ready);
        Assert.Single(commands);
        Assert.Contains("/usage", commands[0]);
        Assert.True(wire.SentQuery);
        wire.Receive("""{"type":"assistant","usage_report":{"rate_limits":{"limits":[{"kind":"session","percent":30,"resets_at":"2030-01-01T00:00:00Z"},{"kind":"weekly_all","percent":55}]}}}""");
        wire.Receive("""{"type":"result","subtype":"success","is_error":false,"local_command":"usage","num_turns":0}""");
        Assert.True(wire.Finished);
        Assert.Equal(2, wire.Windows.Count);
        Assert.Equal(70, wire.Windows[0].RemainingPercent);
        Assert.Equal(45, wire.Windows[1].RemainingPercent);
    }

    [Fact]
    public void ClaudeRejectsToolRequestsAndNonUsageResult()
    {
        var wire = new QuotaWire(Executor.Claude, "check");
        var denied = wire.Receive("""{"type":"control_request","request_id":"extra","request":{"subtype":"doSomething"}}""");
        Assert.True(wire.Failed);
        Assert.Contains("\"subtype\":\"error\"", denied[0]);
        var second = new QuotaWire(Executor.Claude, "check");
        second.Receive("""{"type":"control_response","response":{"request_id":"check","subtype":"success","response":{"commands":[{"name":"usage","builtin":true}]}}}""");
        second.Receive("""{"type":"result","subtype":"success","local_command":"other","num_turns":0}""");
        Assert.False(second.Finished);
        Assert.True(second.Failed);
    }
}

public class QuotaProcessTests
{
    /// <summary>A Node script that plays the Codex app-server: answers
    /// initialize, then the rate-limits read, then exits.</summary>
    static string WriteCodexFake(string folder)
    {
        var path = Path.Combine(folder, "fake-codex.js");
        File.WriteAllLines(path, JsLines(
            "let buffer = \"\";",
            "process.stdin.setEncoding(\"utf8\");",
            "process.stdin.on(\"data\", chunk => {",
            "  buffer += chunk;",
            "  let index;",
            "  while ((index = buffer.indexOf(\"\\n\")) >= 0) {",
            "    const line = buffer.slice(0, index);",
            "    buffer = buffer.slice(index + 1);",
            "    if (line.includes('\"initialize\"')) {",
            "      process.stdout.write('{\"id\":1,\"result\":{}}\\n');",
            "    } else if (line.includes(\"account/rateLimits/read\")) {",
            "      process.stdout.write('{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":42,\"windowDurationMins\":300,\"resetsAt\":1800000000}}}}\\n');",
            "      process.exit(0);",
            "    }",
            "  }",
            "});"));
        return path;
    }

    /// <summary>A Node script that plays Claude: verifies the usage command, then
    /// answers the /usage query with a native report and a success result.</summary>
    static string WriteClaudeFake(string folder)
    {
        var path = Path.Combine(folder, "fake-claude.js");
        File.WriteAllLines(path, JsLines(
            "let buffer = \"\";",
            "let ready = false;",
            "process.stdin.setEncoding(\"utf8\");",
            "process.stdin.on(\"data\", chunk => {",
            "  buffer += chunk;",
            "  let index;",
            "  while ((index = buffer.indexOf(\"\\n\")) >= 0) {",
            "    const line = buffer.slice(0, index);",
            "    buffer = buffer.slice(index + 1);",
            "    if (!ready && line.includes('\"subtype\":\"initialize\"')) {",
            "      ready = true;",
            "      const echo = JSON.parse(line);",
            "      const rid = echo && echo.request_id ? echo.request_id : '';",
            "      process.stdout.write(JSON.stringify({type:'control_response', response:{request_id: rid, subtype:'success', response:{commands:[{name:'usage', builtin:true}]}}}) + '\\n');",
            "    } else if (ready && line.includes(\"/usage\")) {",
            "      process.stdout.write('{\"type\":\"assistant\",\"usage_report\":{\"rate_limits\":{\"limits\":[{\"kind\":\"session\",\"percent\":13}]}}}\\n');",
            "      process.stdout.write('{\"type\":\"result\",\"subtype\":\"success\",\"local_command\":\"usage\",\"num_turns\":0}\\n');",
            "      process.exit(0);",
            "    }",
            "  }",
            "});"));
        return path;
    }

    static string[] JsLines(params string[] lines) => lines;
    [Fact]
    public async Task CodexQuotaProcessQueriesOfficialMethod()
    {
        var folder = Path.Combine(Path.GetTempPath(), "airoom-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var fake = WriteCodexFake(folder);
            var settings = new NativeSettings { CodexPath = fake };
            var result = await new AccountQuotaReader().QueryAsync(Executor.Codex, settings, timeoutMilliseconds: 8_000);
            Assert.Equal(QuotaStatus.Ready, result.Status);
            Assert.Equal(58, result.Windows[0].RemainingPercent);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ClaudeQuotaProcessVerifiesUsageCommand()
    {
        var folder = Path.Combine(Path.GetTempPath(), "airoom-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var fake = WriteClaudeFake(folder);
            var settings = new NativeSettings { ClaudePath = fake };
            var result = await new AccountQuotaReader().QueryAsync(Executor.Claude, settings, timeoutMilliseconds: 8_000);
            Assert.Equal(QuotaStatus.Ready, result.Status);
            Assert.Equal(87, result.Windows[0].RemainingPercent);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task QuotaProbeTimesOutWithoutGuessingRemaining()
    {
        var folder = Path.Combine(Path.GetTempPath(), "airoom-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var fake = Path.Combine(folder, "fake-silent.cmd");
            File.WriteAllLines(fake, ["@echo off", ":loop", "set \"line=\"", "set /p \"line=\"", "if not defined line exit /b 0", "goto loop"]);
            var settings = new NativeSettings { CodexPath = fake };
            var result = await new AccountQuotaReader().QueryAsync(Executor.Codex, settings, timeoutMilliseconds: 2_000);
            Assert.Equal(QuotaStatus.Unavailable, result.Status);
            Assert.Empty(result.Windows);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }
}
