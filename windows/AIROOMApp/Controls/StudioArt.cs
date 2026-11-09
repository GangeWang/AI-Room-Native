using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AIROOMApp.Controls;

/// <summary>A decoded artwork and its pixel size, for views that place the
/// full picture themselves.</summary>
public sealed record StudioArtImage(ImageSource Source, int Width, int Height);

public interface IStudioArtRequester
{
    void InvalidateRender();
}

/// <summary>Full artwork decode cache shared by the card views. The same
/// worker-thread rules as the avatar apply: the WinRT imaging types are
/// thread-agnostic, the WriteableBitmap is built on the UI thread, and
/// requesters that asked while a decode was running are repainted when it
/// lands.</summary>
public static class StudioArt
{
    const int MaxHeight = 800;

    sealed class Entry
    {
        public WriteableBitmap? Bitmap;
        public int Width;
        public int Height;
        public List<IStudioArtRequester> Waiting { get; } = [];
    }

    static readonly Dictionary<int, Entry> Cache = [];

    public static StudioArtImage? FullImage(byte[] data, IStudioArtRequester requester)
    {
        var digest = Services.StudioAssets.Digest(data);
        if (Cache.TryGetValue(digest, out var entry))
        {
            return entry.Bitmap is { } bitmap ? new StudioArtImage(bitmap, entry.Width, entry.Height) : null;
        }
        entry = new Entry();
        entry.Waiting.Add(requester);
        Cache[digest] = entry;
        StartDecode(data, digest, entry);
        return null;
    }

    static async void StartDecode(byte[] data, int digest, Entry entry)
    {
        (int Width, int Height, byte[] Pixels)? decoded = null;
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var width = (int)decoder.OrientedPixelWidth;
            var height = (int)decoder.OrientedPixelHeight;
            var scale = Math.Min(1.0, MaxHeight / (double)height);
            var scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
            var scaledHeight = Math.Max(1, (int)Math.Round(height * scale));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)scaledWidth,
                ScaledHeight = (uint)scaledHeight,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var provider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            var pixels = provider.DetachPixelData();
            if (!HasVisibleAlpha(pixels))
            {
                // The codec reported fully transparent pixels for opaque
                // artwork; force them opaque instead of rendering nothing.
                provider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                pixels = provider.DetachPixelData();
            }
            decoded = (scaledWidth, scaledHeight, pixels);
        }
        catch (Exception error)
        {
            AIROOMApp.Program.Log($"art FAIL 0x{error.HResult:X8} {error.GetType().Name} {error.Message}");
        }
        FinishDecode(digest, entry, decoded);
    }

    static void FinishDecode(int digest, Entry entry, (int Width, int Height, byte[] Pixels)? decoded)
    {
        var queue = entry.Waiting.Count > 0
            ? ((FrameworkElement)entry.Waiting[0]).DispatcherQueue : null;
        if (queue is null)
        {
            Cache.Remove(digest);
            return;
        }
        queue.TryEnqueue(() =>
        {
            try
            {
                if (decoded is { } art)
                {
                    var bitmap = new WriteableBitmap(art.Width, art.Height);
                    using var buffer = bitmap.PixelBuffer.AsStream();
                    buffer.Write(art.Pixels, 0, art.Pixels.Length);
                    bitmap.Invalidate();
                    entry.Bitmap = bitmap;
                    entry.Width = art.Width;
                    entry.Height = art.Height;
                }
            }
            catch (Exception error)
            {
                AIROOMApp.Program.Log($"art FAIL 0x{error.HResult:X8} {error.GetType().Name} {error.Message}");
            }
            finally
            {
                var waiting = entry.Waiting.ToList();
                entry.Waiting.Clear();
                foreach (var requester in waiting)
                {
                    requester.InvalidateRender();
                }
                if (entry.Bitmap is null) Cache.Remove(digest);
            }
        });
    }

    /// <summary>Whether any of the sampled BGRA pixels is not fully transparent.</summary>
    internal static bool HasVisibleAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4 * 97)
        {
            if (pixels[i] > 0) return true;
        }
        return false;
    }
}
