using System.Runtime.InteropServices.WindowsRuntime;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
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
/// </summary>
public sealed class StudioAvatar : Grid
{
    static readonly Dictionary<int, BitmapImage> ImageCache = [];

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

        var bitmap = portraitData is null ? null : GetOrDecode(portraitData);
        if (bitmap is not null)
        {
            bitmap.ImageOpened += (_, _) =>
            {
                built = false;
                _ = DispatcherQueue.TryEnqueue(Render);
            };
            var sourceWidth = (double)bitmap.PixelWidth;
            var sourceHeight = (double)bitmap.PixelHeight;
            var layout = sourceWidth > 0
                ? PortraitCropLayout.Make(size, sourceWidth, sourceHeight,
                    (anchor ?? MemberTemplate.InitialFace).Constrained(sourceWidth, sourceHeight))
                : null;
            if (layout is not null)
            {
                Children.Add(new Image
                {
                    Source = bitmap,
                    Width = layout.ImageWidth,
                    Height = layout.ImageHeight,
                    Stretch = Stretch.Fill,
                    RenderTransform = new TranslateTransform
                    {
                        X = layout.OriginX + (offsetX ?? 0) * size,
                        Y = layout.OriginY + (offsetY ?? 0) * size,
                    },
                });
            }
            else
            {
                // The bitmap has not opened yet; show the initial until it does.
                AddInitial(size, color);
            }
        }
        else
        {
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

        Clip = new RectangleGeometry { Rect = new Rect(0, 0, size, size) };
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

    /// <summary>Decodes once per content digest; bundled portraits and uploads
    /// are reused across the app, so the cache stays small.</summary>
    static BitmapImage? GetOrDecode(byte[] data)
    {
        var digest = Services.StudioAssets.Digest(data);
        if (ImageCache.TryGetValue(digest, out var cached)) return cached;
        var bitmap = new BitmapImage();
        ImageCache[digest] = bitmap;
        _ = DecodeAsync(data, bitmap);
        return bitmap;
    }

    static async Task DecodeAsync(byte[] data, BitmapImage bitmap)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }
        catch
        {
            // A broken image leaves the initial letter showing.
        }
    }
}
