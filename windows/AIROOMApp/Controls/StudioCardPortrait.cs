using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AIROOMApp.Controls;

/// <summary>The full-body portrait on a member card, like macOS StudioPortrait:
/// the artwork fits the frame from the top, then the card's portrait scale and
/// shift are applied and the frame clips the rest. The crop lives in layout
/// (explicit size plus margin) — a RenderTransform under a clip renders
/// nothing here.</summary>
public sealed class StudioCardPortrait : Grid, IStudioArtRequester
{
    MemberCard? card;
    byte[]? data;
    double frameWidth;

    public StudioCardPortrait()
    {
        SizeChanged += (_, args) =>
        {
            frameWidth = args.NewSize.Width;
            InvalidateRender();
        };
    }

    public void Set(MemberCard member, byte[]? portrait)
    {
        card = member;
        data = portrait;
        InvalidateRender();
    }

    public void InvalidateRender()
    {
        Children.Clear();
        var frameHeight = Height is > 0 ? Height : 190;
        if (frameWidth <= 0) return;
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, frameWidth, frameHeight) };

        var color = card is null ? default : StudioColors.ParseHex(card.Color);
        if (color == default)
        {
            color = card is { } member && member.Agent == MemberID.Codex
                ? StudioColors.Violet : StudioColors.Orange;
        }

        var art = data is null ? null : StudioArt.FullImage(data, this);
        if (art is { } image)
        {
            var scale = card?.PortraitScale ?? 1;
            var offset = card?.PortraitOffsetValue?.Clamped ?? new PortraitOffset(0, 0);
            var fit = Math.Min(frameWidth / image.Width, frameHeight / image.Height);
            Children.Add(new Image
            {
                Source = image.Source,
                Width = image.Width * fit * scale,
                Height = image.Height * fit * scale,
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(frameWidth * offset.X, frameHeight * offset.Y, 0, 0),
            });
        }
        else
        {
            var name = card?.Name ?? "";
            Children.Add(new TextBlock
            {
                Text = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?",
                FontSize = 72,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }
}
