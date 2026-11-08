using System.Runtime.InteropServices.WindowsRuntime;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AIROOMApp.Controls;

/// <summary>The shared accent colors, kept beside the theme values.</summary>
public static class StudioColors
{
    public static readonly Windows.UI.Color Amber = Windows.UI.Color.FromArgb(255, 0xDB, 0x96, 0x4A);
    public static readonly Windows.UI.Color Orange = Windows.UI.Color.FromArgb(255, 0xD9, 0x69, 0x2F);
    public static readonly Windows.UI.Color Violet = Windows.UI.Color.FromArgb(255, 0x7A, 0x6E, 0xD6);
    public static readonly Windows.UI.Color Moss = Windows.UI.Color.FromArgb(255, 0x3D, 0x8A, 0x5C);

    public static Windows.UI.Color ParseHex(string? hex)
    {
        if (hex is null || hex.Length != 7 || hex[0] != '#') return default;
        try
        {
            var value = Convert.ToUInt32(hex[1..], 16);
            return Windows.UI.Color.FromArgb(255,
                (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
        }
        catch (FormatException)
        {
            return default;
        }
    }

    public static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) =>
        Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
}

/// <summary>
/// Face crop shared by the sidebar, member cards and chat bubbles. Original
/// artwork is a tall portrait; the crop keeps its aspect ratio and aligns the
/// measured face anchor (PortraitCropLayout), exactly like the macOS version.
///
/// WinUI 3 renders the cropped circle in pixels rather than with XAML parts:
/// a RenderTransform or a clip on the image silently draws nothing here, and
/// ImageBrush lost its Viewbox — so the decoder scales the artwork once, the
/// face window is copied out and given an anti-aliased circular mask in the
/// same buffer, and XAML shows a plain Image of the finished portrait.
/// </summary>
public sealed class StudioAvatar : Grid
{
    /// <summary>Finished circular portraits, keyed by artwork digest and framing.</summary>
    static readonly Dictionary<string, ImageSource> Rendered = [];
    static readonly HashSet<string> Rendering = [];
    static readonly Dictionary<string, List<StudioAvatar>> Waiting = [];

    double diameter;
    MemberID agent = MemberID.Claude;
    byte[]? portraitData;
    string? colorHex;
    string name = "";
    PortraitFaceAnchor? anchor;
    double? offsetX;
    double? offsetY;
    bool running;
    bool built;

    public void Set(MemberID member, string memberName, string? color, double size,
                    byte[]? portrait, PortraitFaceAnchor? faceAnchor,
                    double? scaleOffsetX, double? scaleOffsetY, bool isRunning)
    {
        agent = member;
        name = memberName;
        colorHex = color;
        diameter = size;
        portraitData = portrait;
        anchor = faceAnchor;
        offsetX = scaleOffsetX;
        offsetY = scaleOffsetY;
        running = isRunning;
        built = false;
        Render();
    }

    void Render()
    {
        if (built) return;
        built = true;
        var size = diameter;
        Children.Clear();
        Width = size;
        Height = size;

        var color = StudioColors.ParseHex(colorHex);
        if (color == default)
        {
            color = agent == MemberID.Codex ? StudioColors.Violet : StudioColors.Orange;
        }

        Children.Add(new Border
        {
            Background = new SolidColorBrush(StudioColors.WithAlpha(color, 38)),
            CornerRadius = new CornerRadius(size / 2),
        });

        var face = portraitData is null ? null : RenderedPortrait(portraitData, this);
        if (face is not null)
        {
            Children.Add(new Image
            {
                Source = face,
                Width = size,
                Height = size,
                Stretch = Stretch.Fill,
            });
        }
        else
        {
            // The artwork has not rendered yet (or there is none); show the initial.
            AddInitial(size, color);
        }

        // Ring: the member's color at ~34% opacity.
        Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(StudioColors.WithAlpha(color, 87)),
            BorderThickness = new Thickness(1.4),
            CornerRadius = new CornerRadius(size / 2),
        });

        // Running dot: amber while working, moss when idle.
        Children.Add(new Ellipse
        {
            Width = size * 0.23,
            Height = size * 0.23,
            Fill = new SolidColorBrush(running ? StudioColors.Amber : StudioColors.Moss),
            StrokeThickness = 2,
            Stroke = (Brush)Application.Current.Resources["StudioBackgroundBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        });
    }

    void AddInitial(double size, Windows.UI.Color color)
    {
        var initial = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
        Children.Add(new TextBlock
        {
            Text = initial,
            FontSize = size * 0.46,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    /// <summary>The finished portrait for this artwork and framing, or null while
    /// it renders. Bundled portraits and uploads are reused across the app, so
    /// the cache stays small. Requesters registered while rendering are repainted
    /// on completion — the member list is rebuilt while a decode is in flight,
    /// and only the requesters at decode start would otherwise repaint.</summary>
    static ImageSource? RenderedPortrait(byte[] data, StudioAvatar requester)
    {
        var digest = Services.StudioAssets.Digest(data);
        var anchor = requester.anchor ?? MemberTemplate.InitialFace;
        var key = $"{digest}:{requester.diameter:0.##}:{anchor.X:0.####}:{anchor.Y:0.####}:{anchor.Zoom:0.##}"
            + $":{requester.offsetX ?? 0:0.####}:{requester.offsetY ?? 0:0.####}";
        if (Rendered.TryGetValue(key, out var ready)) return ready;
        if (!Rendering.Add(key)) return null;
        if (Waiting.TryGetValue(key, out var waiting)) waiting.Add(requester);
        else Waiting[key] = [requester];
        StartDecode(data, key, requester.diameter, anchor, requester.offsetX ?? 0, requester.offsetY ?? 0);
        return null;
    }

    static async void StartDecode(byte[] data, string key, double diameter,
                                  PortraitFaceAnchor anchor, double offsetX, double offsetY)
    {
        var size = (int)Math.Round(diameter);
        byte[]? face = null;
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var width = (int)decoder.OrientedPixelWidth;
            var height = (int)decoder.OrientedPixelHeight;
            if (size > 0)
            {
                face = await RenderFacePixels(decoder, width, height, size, anchor, offsetX, offsetY);
            }
        }
        catch (Exception error)
        {
            AIROOMApp.Program.Log($"decode FAIL 0x{error.HResult:X8} {error.GetType().Name} {error.Message}");
        }
        FinishDecode(key, face, size);
    }

    /// <summary>The pixels are in: publish the portrait on the UI thread and
    /// repaint every avatar that asked while the decode was running.</summary>
    static void FinishDecode(string key, byte[]? face, int size)
    {
        var queue = Waiting.TryGetValue(key, out var waiting) && waiting.Count > 0
            ? waiting[0].DispatcherQueue : null;
        if (queue is null)
        {
            Rendering.Remove(key);
            return;
        }
        queue.TryEnqueue(() =>
        {
            try
            {
                if (face is { Length: > 0 })
                {
                    var bitmap = new WriteableBitmap(size, size);
                    using var buffer = bitmap.PixelBuffer.AsStream();
                    buffer.Write(face, 0, face.Length);
                    bitmap.Invalidate();
                    Rendered[key] = bitmap;
                }
            }
            catch (Exception error)
            {
                AIROOMApp.Program.Log($"decode FAIL 0x{error.HResult:X8} {error.GetType().Name} {error.Message}");
            }
            finally
            {
                if (Waiting.Remove(key, out var list))
                {
                    foreach (var avatar in list)
                    {
                        avatar.InvalidateRender();
                    }
                }
                Rendering.Remove(key);
            }
        });
    }

    /// <summary>The size×size BGRA face crop with an anti-aliased circular mask.
    /// The decoder scales the full artwork once (high quality), then the face
    /// window is copied and masked per pixel.</summary>
    static async Task<byte[]?> RenderFacePixels(BitmapDecoder decoder, int width, int height,
                                                int size, PortraitFaceAnchor anchor,
                                                double offsetX, double offsetY)
    {
        var layout = PortraitCropLayout.Make(size, width, height, anchor.Constrained(width, height));
        if (layout is null) return null;

        var scaledWidth = (int)Math.Ceiling(layout.ImageWidth);
        var scaledHeight = (int)Math.Ceiling(layout.ImageHeight);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)scaledWidth,
            ScaledHeight = (uint)scaledHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var source = provider.DetachPixelData();
        if (!HasVisibleAlpha(source))
        {
            // The codec reported fully transparent pixels for opaque artwork;
            // force them opaque instead of rendering nothing.
            provider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            source = provider.DetachPixelData();
        }

        var face = new byte[size * size * 4];
        var fromX = (int)Math.Round(-(layout.OriginX + offsetX * size));
        var fromY = (int)Math.Round(-(layout.OriginY + offsetY * size));
        var radius = size / 2.0;
        for (var y = 0; y < size; y++)
        {
            var sourceY = Math.Clamp(fromY + y, 0, scaledHeight - 1);
            var dy = y - size / 2 + 0.5;
            for (var x = 0; x < size; x++)
            {
                var sourceX = Math.Clamp(fromX + x, 0, scaledWidth - 1);
                var dx = x - size / 2 + 0.5;
                // Premultiplied output: every channel fades by the circle's
                // coverage. Zeroing alpha alone leaves invalid pixels (RGB
                // above alpha), which the compositor draws as opaque.
                var coverage = Math.Clamp(radius - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
                var read = (sourceY * scaledWidth + sourceX) * 4;
                var write = (y * size + x) * 4;
                face[write] = (byte)(source[read] * coverage);
                face[write + 1] = (byte)(source[read + 1] * coverage);
                face[write + 2] = (byte)(source[read + 2] * coverage);
                face[write + 3] = (byte)(source[read + 3] * coverage);
            }
        }
        return face;
    }

    /// <summary>Whether any of the sampled BGRA pixels is not fully transparent.</summary>
    static bool HasVisibleAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4 * 97)
        {
            if (pixels[i] > 0) return true;
        }
        return false;
    }

    /// <summary>Decoded pixels arrived: rebuild with the finished portrait.</summary>
    internal void InvalidateRender()
    {
        built = false;
        Render();
    }
}
