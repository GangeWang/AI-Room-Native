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
}
