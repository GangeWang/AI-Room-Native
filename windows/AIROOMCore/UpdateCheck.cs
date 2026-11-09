using System.Text.Json;

namespace AIROOMCore;

/// <summary>A published AIROOM Native release.</summary>
public sealed record ReleaseInfo(
    string Version,
    string Page,
    string? Download,
    DateTimeOffset? PublishedAt);

/// <summary>
/// Tells the owner a newer release exists. It only reads GitHub's public
/// release list: nothing about the owner is sent, and nothing is downloaded or
/// installed. Opening the release page is left to the owner.
///
/// The repository also holds the macOS app, so GitHub's single "latest" release
/// may be a macOS one. The Windows app reads the list and takes the newest
/// Windows release: tags "windows-v1.2.3" with a Windows installer attached
/// (docs/RELEASING.md). Windows releases are never marked Latest.
/// </summary>
public static class UpdateCheck
{
    public const string Repository = "GangeWang/AIROOMNative";
    public const string ReleasesAPI = $"https://api.github.com/repos/{Repository}/releases?per_page=30";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";

    /// <summary>Automatic checks happen at most this often.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(20);

    /// <summary>Reads one GitHub release JSON. Links must point into this
    /// repository's releases; anything else is dropped rather than offered.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        JsonElement element;
        try
        {
            element = JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object) return null;
        bool IsTrue(string name) => element.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
        if (IsTrue("draft") || IsTrue("prerelease")) return null;
        if (!element.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) return null;
        if (WindowsVersion(tag.GetString()) is not { } version) return null;
        var page = element.TryGetProperty("html_url", out var htmlUrl) && htmlUrl.ValueKind == JsonValueKind.String
            && htmlUrl.GetString() is { } url && IsOurs(url) ? url : ReleasesPage;
        string? download = null;
        if (element.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            download = assets.EnumerateArray()
                .Where(asset => asset.ValueKind == JsonValueKind.Object
                                && asset.TryGetProperty("browser_download_url", out var link)
                                && link.ValueKind == JsonValueKind.String)
                .Select(asset => asset.GetProperty("browser_download_url").GetString()!)
                .FirstOrDefault(link => IsWindowsAsset(link) && IsOurs(link));
        }
        DateTimeOffset? published = null;
        if (element.TryGetProperty("published_at", out var publishedAt) && publishedAt.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(publishedAt.GetString(), out var parsed))
        {
            published = parsed;
        }
        return new ReleaseInfo(version, page, download, published);
    }

    /// <summary>The newest Windows release in GitHub's release list. One without
    /// a Windows installer is skipped: there would be nothing to install.</summary>
    public static ReleaseInfo? Newest(string json)
    {
        JsonElement list;
        try
        {
            list = JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        if (list.ValueKind != JsonValueKind.Array) return null;
        return list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => Parse(item.GetRawText()))
            .OfType<ReleaseInfo>()
            .Where(release => release.Download is not null)
            .MaxBy(release => VersionParts(release.Version), VersionComparer.Instance);
    }

    /// <summary>"windows-v0.3.0" is a Windows release; "v…"/"macos-v…" belong to
    /// the macOS app and any other prefix belongs to another platform.</summary>
    public static string? WindowsVersion(string? tag)
    {
        const string prefix = "windows-v";
        if (tag is null || !tag.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var version = tag[prefix.Length..];
        return Components(version) is null ? null : version;
    }

    static bool IsWindowsAsset(string url) =>
        url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        || url.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
        || url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>"0.3.0" is newer than "0.2.58"; anything unreadable is never newer.</summary>
    public static bool IsNewer(string remote, string local)
    {
        var a = Components(remote);
        var b = Components(local);
        if (a is null || b is null) return false;
        return VersionComparer.Instance.Compare(a, b) > 0;
    }

    public static bool IsDue(DateTimeOffset? lastChecked, DateTimeOffset? now = null)
    {
        if (lastChecked is null) return true;
        var moment = now ?? DateTimeOffset.Now;
        return moment - lastChecked.Value >= Interval || moment < lastChecked.Value;
    }

    /// <summary>One unauthenticated request to GitHub's public API; no identifying data is attached.</summary>
    public static async Task<ReleaseInfo?> FetchNewestAsync(TimeSpan? timeout = null)
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            UseProxy = false,
        });
        client.Timeout = timeout ?? TimeSpan.FromSeconds(10);
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesAPI);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("AIROOM-Native");
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        return Newest(await response.Content.ReadAsStringAsync());
    }

    static int[]? Components(string version)
    {
        var parts = version.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsDigit)) return null;
            numbers[i] = int.Parse(parts[i]);
        }
        return numbers;
    }

    static int[] VersionParts(string version) => Components(version) ?? [];

    sealed class VersionComparer : System.Collections.Generic.IComparer<int[]>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(int[]? a, int[]? b)
        {
            if (a is null || b is null) return 0;
            for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
            {
                var x = index < a.Length ? a[index] : 0;
                var y = index < b.Length ? b[index] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }
    }

    static bool IsOurs(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "github.com"
        && uri.AbsolutePath.StartsWith($"/{Repository}/releases/", StringComparison.Ordinal);
}
