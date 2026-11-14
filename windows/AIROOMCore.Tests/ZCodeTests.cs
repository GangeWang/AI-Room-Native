using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIROOMCore;
using Xunit;

public class ZCodeTests
{
    static (byte[] Key, string Home) SyntheticHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "airoom-zcode-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var key = ZCode.DeriveKey("win32", home, Environment.UserName);
        return (key, home);
    }

    static string EncryptFor(byte[] key, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        using var aes = new AesGcm(key, 16);
        var cipher = new byte[Encoding.UTF8.GetByteCount(plaintext)];
        var tag = new byte[16];
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(plaintext), cipher, tag);
        string B64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"enc:v1:{B64(nonce)}.{B64(tag)}.{B64(cipher)}";
    }

    static void WriteBuiltin(string home, int revision, string providerId, string[] models)
    {
        var dir = Path.Combine(home, ".zcode", "v2", "runtime", "provider", "windows-x86_64", "3.17.2", "endpoint-abc");
        Directory.CreateDirectory(dir);
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            revision,
            config = new
            {
                providerConfigRules = new
                {
                    providerRules = new object[]
                    {
                        new
                        {
                            providerId,
                            providerName = "Test Plan",
                            config = new
                            {
                                access = new { type = "zhipu-account", accountType = "zai", mode = "start-plan" },
                                api = new { type = "anthropic-messages", baseUrl = "https://example.test/api" },
                                builtinModelIds = models,
                            },
                        },
                    },
                },
                modelConfigRules = new { },
            },
        });
        File.WriteAllText(Path.Combine(dir, "zcode-builtin.json"), payload);
    }

    [Fact]
    public void DeriveKeyMatchesTheCliFallbackRecipe()
    {
        var key = ZCode.DeriveKey("win32", @"C:\Users\t", "tester");
        var expected = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("zcode-credential-fallback:win32:C:\\Users\\t:tester"))).ToLowerInvariant();
        Assert.Equal(expected, Convert.ToHexString(key).ToLowerInvariant());
    }

    [Fact]
    public void DecryptValueRoundTripsAndPassesPlaintext()
    {
        var (key, _) = SyntheticHome();
        var sealedValue = EncryptFor(key, "a-secret-jwt");
        Assert.Equal("a-secret-jwt", ZCode.DecryptValue(sealedValue, key));
        Assert.Equal("plain-token", ZCode.DecryptValue("plain-token", key));
    }

    [Fact]
    public void BasedOnRevisionCombinesRevisionAndPathHash()
    {
        var (key, home) = SyntheticHome();
        WriteBuiltin(home, 43, "account:zai-start-plan", ["GLM-5.3-Flash"]);
        var file = ZCode.NewestBuiltinFile(Path.Combine(home, ".zcode", "v2"));
        Assert.NotNull(file);
        var basedOn = ZCode.BasedOnRevision(file);
        var nonce = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(file)))).ToLowerInvariant();
        Assert.Equal($"zcode-builtin:43:{nonce}", basedOn);
    }

    [Fact]
    public void LoadAuthReadsCredentialsTelemetryAndBuiltinModels()
    {
        var (key, home) = SyntheticHome();
        Directory.CreateDirectory(Path.Combine(home, ".zcode", "v2"));
        var credentials = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["oauth:zai:access_token"] = EncryptFor(key, "oauth-token"),
            ["zcodejwttoken"] = EncryptFor(key, "zcode-jwt"),
            ["oauth:active_provider"] = EncryptFor(key, "zai"),
        });
        File.WriteAllText(Path.Combine(home, ".zcode", "v2", "credentials.json"), credentials);
        File.WriteAllText(Path.Combine(home, ".zcode", "v2", "telemetry-state.json"),
            JsonSerializer.Serialize(new { deviceMid = "mid-123" }));
        WriteBuiltin(home, 43, "account:zai-start-plan", ["GLM-5.3", "GLM-5.3-Flash"]);

        var auth = ZCode.LoadAuth(home);
        Assert.NotNull(auth);
        Assert.Equal("zcode-jwt", auth.ZcodeJwt);
        Assert.Equal("mid-123", auth.DeviceMid);
        Assert.Equal(["GLM-5.3", "GLM-5.3-Flash"], auth.ModelIds);
        Assert.StartsWith("zcode-builtin:43:", auth.BasedOnRevision);
    }

    [Fact]
    public void LoadAuthReturnsNullWithoutCredentials()
    {
        var (_, home) = SyntheticHome();
        Assert.Null(ZCode.LoadAuth(home));
    }

    [Fact]
    public void ModelChoicesLeadWithTheFlashDefault()
    {
        var choices = ZCode.ModelChoices();
        Assert.Contains(choices, c => c.Id == "GLM-5.3-Flash" && c.IsDefault);
        Assert.All(choices, c => Assert.Contains("max", c.Efforts));
    }

    // MARK: Runtime choice — a plain Node (v22+) over the desktop executable (macOS #14)

    [Theory]
    [InlineData("v26.7.0", 26)]
    [InlineData("v22.1.0", 22)]
    [InlineData("  v18.20.1\n", 18)]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NodeMajorReadsTheLeadingNumber(string? output, int? expected) =>
        Assert.Equal(expected, ZCode.NodeMajor(output));

    [Fact]
    public void PickRuntimePrefersAPlainNodeAndFallsBackToTheDesktop()
    {
        var node = @"C:\node\node.exe";
        var desktop = @"C:\ZCode\ZCode.exe";
        static bool NodeAndDesktopExist(string path) =>
            path is @"C:\node\node.exe" or @"C:\ZCode\ZCode.exe";
        // A modern plain Node wins.
        Assert.Equal(new ZCode.Runtime(node, IsElectron: false),
            ZCode.PickRuntime([node], desktop, _ => "v26.7.0", NodeAndDesktopExist));
        // Too old → the desktop executable, as Node.
        Assert.Equal(new ZCode.Runtime(desktop, IsElectron: true),
            ZCode.PickRuntime([node], desktop, _ => "v18.20.1", NodeAndDesktopExist));
        // Broken node → the desktop executable too.
        Assert.Equal(new ZCode.Runtime(desktop, IsElectron: true),
            ZCode.PickRuntime([node], desktop, _ => null, NodeAndDesktopExist));
        // A Node too old and no desktop executable → no runtime at all.
        Assert.Null(ZCode.PickRuntime([node], null, _ => "v18.20.1", NodeAndDesktopExist));
    }

    [Fact]
    public void DesktopExeResolvesFromTheBundledScriptLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "airoom-zcode-desktop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cjs = Path.Combine(root, "resources", "glm", "zcode.cjs");
            Directory.CreateDirectory(Path.GetDirectoryName(cjs)!);
            File.WriteAllText(cjs, "//");
            Assert.Null(ZCode.DesktopExeFor(cjs));
            File.WriteAllText(Path.Combine(root, "ZCode.exe"), "MZ");
            Assert.Equal(Path.Combine(root, "ZCode.exe"), ZCode.DesktopExeFor(cjs));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // MARK: Session mode and the permission answer (macOS #8, #19)

    [Fact]
    public void ReadOnlyRunsPlanAndWritingRunsAskFirst()
    {
        Assert.Equal("plan", ZCode.ModeFor(ExecutionAccess.ReadOnly));
        Assert.Equal("plan", ZCode.ModeFor(ExecutionAccess.ReadFiles));
        // Never "edit": that mode applies edits to any path without asking.
        Assert.Equal("build", ZCode.ModeFor(ExecutionAccess.StagedWrite));
    }

    [Fact]
    public void OnlyFileEditsInsideTheWorkspaceOfAWritingTaskAreAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "airoom-zcode-policy-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "desk");
        Directory.CreateDirectory(Path.Combine(workspace, "notes"));
        try
        {
            bool Allows(string tool, string path, ExecutionAccess access = ExecutionAccess.StagedWrite) =>
                ZCode.Allows(tool, CoreJson.Serialize(new Dictionary<string, object>
                {
                    ["file_path"] = path, ["content"] = "x",
                }), workspace, access);

            Assert.True(Allows("Write", Path.Combine(workspace, "a.txt")));
            Assert.True(Allows("Edit", Path.Combine(workspace, "notes", "new", "b.md")), "folders that don't exist yet are fine");
            Assert.True(Allows("Write", "notes\\c.txt"), "relative paths resolve inside the workspace");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.False(Allows("Write", Path.Combine(home, ".zshrc")), "outside, by absolute path");
            Assert.False(Allows("Write", Path.Combine(workspace, "..", "escape.txt")));
            Assert.False(Allows("Write", "..\\escape.txt"));
            Assert.False(Allows("Write", workspace + "-sibling\\a.txt"), "a folder that only shares the prefix");
            Assert.False(Allows("Write", workspace), "the workspace itself is not a file in it");
            Assert.False(Allows("Write", ""));
            // Read-only tasks, and every other tool.
            Assert.False(Allows("Write", Path.Combine(workspace, "a.txt"), ExecutionAccess.ReadOnly));
            Assert.False(Allows("Write", Path.Combine(workspace, "a.txt"), ExecutionAccess.ReadFiles));
            Assert.False(Allows("Bash", Path.Combine(workspace, "a.txt")));
            Assert.False(ZCode.Allows("Bash", CoreJson.Serialize(new Dictionary<string, object> { ["command"] = "touch x" }),
                workspace, ExecutionAccess.StagedWrite));
            Assert.False(ZCode.Allows("Write", "{}", workspace, ExecutionAccess.StagedWrite));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SymlinksAndHiddenPathsCannotBeWrittenThrough()
    {
        var root = Path.Combine(Path.GetTempPath(), "airoom-zcode-links-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "desk");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        try
        {
            bool Writable(string path) => ZCode.IsWritable(path, workspace);
            bool linked = true, danglingCreated = true;
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), outside);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            {
                linked = false; // creating links needs a privilege this machine may not grant
            }
            if (linked)
            {
                Assert.False(Writable(Path.Combine(workspace, "link", "a.txt")), "a linked folder leads outside");
                Assert.True(Writable(Path.Combine(workspace, "plain.txt")));
            }
            try
            {
                File.CreateSymbolicLink(Path.Combine(workspace, "dangling"), Path.Combine(outside, "not-yet.txt"));
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            {
                danglingCreated = false;
            }
            if (danglingCreated)
            {
                Assert.False(Writable(Path.Combine(workspace, "dangling")), "writing a dangling link creates its target");
            }
            // Config a later turn would trust; hidden paths are refused with or without links.
            Assert.False(Writable(Path.Combine(workspace, ".zcode", "settings.json")));
            Assert.False(Writable(Path.Combine(workspace, ".claude-plugin", "plugin.json")));
            Assert.False(Writable(Path.Combine(workspace, "sub", ".git", "hooks", "pre-commit")));
            Assert.False(Writable(Path.Combine(workspace, ".env")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ThePermissionAnswerDeniesWithAReason()
    {
        // ZCode validates answers strictly: `decision` is required and an
        // empty object is rejected, so the denial must carry both keys.
        var answer = ZCode.PermissionDenial();
        Assert.Equal("deny", answer["decision"]);
        Assert.False(string.IsNullOrWhiteSpace((string)answer["reason"]!));
    }

    [Fact]
    public void ThePermissionAnswerAllowsWorkspaceWritesOnlyInWritingTasks()
    {
        var root = Path.Combine(Path.GetTempPath(), "airoom-zcode-answer-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "desk");
        Directory.CreateDirectory(workspace);
        try
        {
            var input = CoreJson.Serialize(new Dictionary<string, object>
            {
                ["file_path"] = Path.Combine(workspace, "a.txt"),
            });
            var allow = ZCode.PermissionAnswer("Write", input, workspace, ExecutionAccess.StagedWrite);
            Assert.Equal("allow", allow["decision"]);
            Assert.Single(allow);
            var deny = ZCode.PermissionAnswer("Write", input, workspace, ExecutionAccess.ReadOnly);
            Assert.Equal("deny", deny["decision"]);
            Assert.False(string.IsNullOrWhiteSpace((string)deny["reason"]!));
            var bash = ZCode.PermissionAnswer("Bash", "{}", workspace, ExecutionAccess.StagedWrite);
            Assert.Equal("deny", bash["decision"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // MARK: Turn completion from the message list

    static JsonElement Session(params object[] messages) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { messages })).RootElement.Clone();

    static object UserMessage(long created) => new
    {
        info = new { role = "user", time = new { created } },
        parts = new[] { new { type = "text", text = "hi" } },
    };

    static object AssistantText(long created, long? completed, string body) => new
    {
        info = new
        {
            role = "assistant",
            time = completed is { } done ? new { created, completed = done } : (object)new { created },
        },
        parts = new[] { new { type = "text", text = body } },
    };

    static object ExitPlanMessage(long created, long completed, string plan) => new
    {
        info = new { role = "assistant", time = new { created, completed } },
        parts = new object[] { new { type = "tool-ExitPlanMode", state = "output-available", input = new { plan } } },
    };

    [Fact]
    public void PlainTextReplyCompletesTheTurn()
    {
        var session = Session(UserMessage(1), AssistantText(2, 3, "收到"));
        var (done, text) = ZCodeTurn.Result(session);
        Assert.True(done);
        Assert.Equal("收到", text);
    }

    [Fact]
    public void NarrationBeforeToolCallsNeverCompletesTheTurnEarly()
    {
        // GLM narrates (completed), then a tool-call message lands after it:
        // the LAST assistant message decides, so the turn is not done.
        var narration = AssistantText(2, 3, "我先查看檔案");
        var toolStep = new
        {
            info = new { role = "assistant", time = new { created = 4, completed = 5 } },
            parts = new object[] { new { type = "tool-read", state = "output-available", input = new { path = "a.txt" } } },
        };
        var session = Session(UserMessage(1), narration, toolStep);
        var (done, _) = ZCodeTurn.Result(session);
        Assert.False(done);
    }

    [Fact]
    public void PlanArrivingThroughExitPlanModeCompletesTheTurn()
    {
        // Seen live: the plan-mode model hands the whole plan to the
        // ExitPlanMode tool call; the assistant text is empty.
        var final = ExitPlanMessage(6, 7, "{\"objective\":\"把佔位首頁改成最小可用\",\"rounds\":[]}");
        var session = Session(UserMessage(1), final);
        var (done, text) = ZCodeTurn.Result(session);
        Assert.True(done);
        Assert.Contains("objective", text);
        Assert.Contains("objective", ZCodeTurn.FinalText(session));
    }

    [Fact]
    public void AnUncompletedFinalMessageKeepsTheTurnWaiting()
    {
        var session = Session(UserMessage(1), AssistantText(2, null, "還在想"));
        var (done, _) = ZCodeTurn.Result(session);
        Assert.False(done);
    }

    [Fact]
    public void ThePlanOutranksTheNarrationAfterIt()
    {
        // Seen live: GLM hands the plan to ExitPlanMode and then posts a plain-text
        // summary as the final message. The plan is the reply, not the summary.
        var handed = ExitPlanMessage(2, 3, "{\"objective\":\"方案本體\"}");
        var narration = AssistantText(4, 5, "工作區已核實，方案已交。");
        var session = Session(UserMessage(1), handed, narration);
        var (done, text) = ZCodeTurn.Result(session);
        Assert.True(done);
        Assert.Contains("方案本體", text);
        Assert.Contains("方案本體", ZCodeTurn.FinalText(session));
    }
}
