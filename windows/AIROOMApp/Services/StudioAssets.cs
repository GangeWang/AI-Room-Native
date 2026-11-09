using System.Security.Cryptography;

namespace AIROOMApp.Services;

/// <summary>Bundled member artwork (fetched by Scripts/fetch-assets.ps1 from
/// the pinned upstream commit), served as decoded bytes for the avatar views.</summary>
public static class StudioAssets
{
    static readonly Dictionary<string, byte[]> Cache = [];

    public static byte[]? Image(string? fileName)
    {
        if (fileName is null) return null;
        if (Cache.TryGetValue(fileName, out var cached)) return cached;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (!File.Exists(path)) return null;
        try
        {
            var data = File.ReadAllBytes(path);
            Cache[fileName] = data;
            return data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A short digest used to key the avatar image cache.</summary>
    public static int Digest(byte[] data)
    {
        var hash = MD5.HashData(data);
        return BitConverter.ToInt32(hash, 0);
    }
}
