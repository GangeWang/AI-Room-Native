using AIROOMCore;
using Xunit;

public class UpdateCheckTests
{
    static string ObjectJson(string tag = "windows-v0.3.0",
        string page = "https://github.com/GangeWang/AIROOMNative/releases/tag/windows-v0.3.0",
        string? asset = "https://github.com/GangeWang/AIROOMNative/releases/download/windows-v0.3.0/AIROOM-Setup-0.3.0.exe",
        bool prerelease = false)
    {
        var assetEntry = asset is null ? "" : $",{{\"name\":\"setup\",\"browser_download_url\":\"{asset}\"}}";
        return "{"
            + $"\"tag_name\":\"{tag}\","
            + $"\"html_url\":\"{page}\","
            + "\"draft\":false,"
            + $"\"prerelease\":{(prerelease ? "true" : "false")},"
            + "\"published_at\":\"2026-11-05T01:00:00Z\","
            + "\"assets\":["
            + $"{{\"name\":\"notes.txt\",\"browser_download_url\":\"https://github.com/GangeWang/AIROOMNative/releases/download/{tag}/notes.txt\"}}"
            + assetEntry
            + "]}";
    }

    static string ListJson(params string[] objects) => "[" + string.Join(",", objects) + "]";

    static string DmgAsset(string tag, string file) =>
        $"https://github.com/GangeWang/AIROOMNative/releases/download/{tag}/{file}";

    [Fact]
    public void AWindowsTagIsTheOnlyKindTheWindowsAppReads()
    {
        Assert.Equal("0.3.0", UpdateCheck.WindowsVersion("windows-v0.3.0"));
        Assert.Equal("1.0.0", UpdateCheck.WindowsVersion("windows-v1.0.0"));
        Assert.Null(UpdateCheck.WindowsVersion("v0.2.64"));
        Assert.Null(UpdateCheck.WindowsVersion("macos-v0.2.64"));
        Assert.Null(UpdateCheck.WindowsVersion("0.2.64"));
        Assert.Null(UpdateCheck.WindowsVersion("wnext"));
    }

    [Fact]
    public void ReadsAWindowsReleaseWithItsInstaller()
    {
        var info = UpdateCheck.Parse(ObjectJson()) ?? throw new Xunit.Sdk.XunitException("parse failed");
        Assert.Equal("0.3.0", info.Version);
        Assert.Equal("https://github.com/GangeWang/AIROOMNative/releases/tag/windows-v0.3.0", info.Page);
        Assert.EndsWith("AIROOM-Setup-0.3.0.exe", info.Download);
        Assert.NotNull(info.PublishedAt);
    }

    [Fact]
    public void MacTagsAreNeverTakenAsWindowsReleases()
    {
        Assert.Null(UpdateCheck.Parse(ObjectJson(tag: "v0.2.64")));
        Assert.Null(UpdateCheck.Parse(ObjectJson(tag: "macos-v0.2.64")));
    }

    [Fact]
    public void OnlyLinksIntoThisRepositoryAreOffered()
    {
        var info = UpdateCheck.Parse(ObjectJson(
            page: "https://evil.example/releases/x",
            asset: "https://evil.example/AIROOM-Setup.exe")) ?? throw new Xunit.Sdk.XunitException("parse failed");
        Assert.Equal(UpdateCheck.ReleasesPage, info.Page);
        Assert.Null(info.Download);
    }

    [Fact]
    public void PrereleasesDraftsAndOddTagsAreIgnored()
    {
        Assert.Null(UpdateCheck.Parse(ObjectJson(prerelease: true)));
        Assert.Null(UpdateCheck.Parse(ObjectJson(tag: "nightly")));
        Assert.Null(UpdateCheck.Parse("not json"));
    }

    [Fact]
    public void PicksTheNewestWindowsReleaseWithAnInstaller()
    {
        // GitHub lists newest first; here a macOS release is newer and higher-numbered.
        var list = ListJson(
            ObjectJson(tag: "v0.2.65", asset: DmgAsset("v0.2.65", "AIROOMNative-0.2.65.dmg")),
            ObjectJson(tag: "windows-v0.3.0", asset: DmgAsset("windows-v0.3.0", "AIROOM-Setup-0.3.0.exe")),
            ObjectJson(tag: "windows-v0.4.0", asset: null),
            ObjectJson(tag: "windows-v0.5.0", prerelease: true),
            ObjectJson(tag: "windows-v0.2.9", asset: DmgAsset("windows-v0.2.9", "AIROOM-Setup-0.2.9.zip")));
        var newest = UpdateCheck.Newest(list) ?? throw new Xunit.Sdk.XunitException("no release found");
        Assert.Equal("0.3.0", newest.Version);
    }

    [Fact]
    public void AListWithNoWindowsReleaseOffersNothing()
    {
        Assert.Null(UpdateCheck.Newest(ListJson(ObjectJson(tag: "v0.2.64", asset: DmgAsset("v0.2.64", "x.dmg")))));
        Assert.Null(UpdateCheck.Newest("{}"));
        Assert.Null(UpdateCheck.Newest("not json"));
    }

    [Theory]
    [InlineData("0.3.0", "0.2.99", true)]
    [InlineData("0.2.100", "0.2.99", true)]
    [InlineData("0.3", "0.2.99", true)]
    [InlineData("0.2.9", "0.2.58", false)]
    [InlineData("0.2.58", "0.2.58", false)]
    [InlineData("0.2.57", "0.2.58", false)]
    [InlineData("0.2.58", "0.2.58.0", false)]
    [InlineData("abc", "0.2.58", false)]
    public void VersionComparison(string remote, string local, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsNewer(remote, local));

    [Fact]
    public void AutomaticChecksRunAtMostOnceADay()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.True(UpdateCheck.IsDue(null, now));
        Assert.False(UpdateCheck.IsDue(now.AddHours(-1), now));
        Assert.True(UpdateCheck.IsDue(now.AddHours(-21), now));
        Assert.True(UpdateCheck.IsDue(now.AddHours(1), now));
    }

    [Fact]
    public void TheSettingDefaultsToOn()
    {
        Assert.True(new NativeSettings().CheckForUpdates);
        var old = System.Text.Json.JsonSerializer.Deserialize<NativeSettings>(
            """{"claudePath":"","codexPath":""}""", CoreJson.Default);
        Assert.NotNull(old);
        Assert.True(old!.CheckForUpdates);
    }
}
