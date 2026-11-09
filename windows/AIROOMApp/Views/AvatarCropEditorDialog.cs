using AIROOMApp.Controls;
using AIROOMApp.ViewModels;
using AIROOMCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AIROOMApp.Views;

/// <summary>A local, non-destructive portrait editor, ported from macOS's
/// AvatarCropEditor: drag the big circle to reposition, sliders to fine tune,
/// live previews at the sidebar and card sizes. Only the normalized framing
/// coordinates are written back; the original artwork stays untouched.</summary>
public sealed class AvatarCropEditorDialog
{
    const double PreviewSize = 230;

    readonly WorkbenchModel model;
    readonly MemberCard card;
    readonly MemberID agent;
    readonly AvatarEditorSource source;
    readonly StudioArtImage decoded;

    PortraitFaceAnchor draft;
    PortraitFaceAnchor defaultAnchor;
    readonly double zoomMin;
    readonly double zoomMax;

    StudioAvatar preview = null!;
    readonly List<StudioAvatar> previews = [];
    TextBlock readout = null!;
    Slider zoomSlider = null!;
    Slider xSlider = null!;
    Slider ySlider = null!;
    bool rendering;
    Point? lastDragPosition;
    ContentDialog dialog = null!;

    AvatarCropEditorDialog(WorkbenchModel model, MemberCard card, AvatarEditorSource source, StudioArtImage decoded)
    {
        this.model = model;
        this.card = card;
        this.agent = card.Agent;
        this.source = source;
        this.decoded = decoded;
        (defaultAnchor, zoomMin, zoomMax) = Defaults(source, decoded);
        draft = Fit(source.Saved ?? defaultAnchor);
    }

    /// <summary>Shows the editor; returns the new crop, or null for "back to default".
    /// Null when the artwork can't be decoded.</summary>
    public static async Task<PortraitFaceAnchor?> ShowAsync(WorkbenchModel model, MemberCard card, AvatarEditorSource source)
    {
        if (App.Window is null) return null;
        StudioArtImage decoded;
        try
        {
            decoded = StudioArt.FullImage(source.Image, new SimpleRequester())
                ?? throw new InvalidOperationException("cannot decode");
        }
        catch (Exception error)
        {
            Program.Log($"avatar crop editor: decode failed: {error.Message}");
            return null;
        }
        var editor = new AvatarCropEditorDialog(model, card, source, decoded);
        return await editor.ShowAsync();
    }

    async Task<PortraitFaceAnchor?> ShowAsync()
    {
        dialog = new ContentDialog
        {
            Title = new StackPanel { Spacing = 2, Children =
            {
                new TextBlock { Text = "調整頭像", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = model.MemberName(agent), FontSize = 12, Opacity = 0.6 },
            } },
            Content = BuildContent(),
            PrimaryButtonText = "儲存",
            CloseButtonText = "取消",
            SecondaryButtonText = "恢復預設位置",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = App.Window!.Content.XamlRoot,
        };
        dialog.SecondaryButtonClick += (_, _) => { draft = Fit(defaultAnchor); RenderAll(); };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        return !FitEquals(draft, defaultAnchor) ? draft : null;
    }

    // MARK: Layout

    FrameworkElement BuildContent()
    {
        preview = MakePreview(PreviewSize);
        preview.PointerPressed += OnPreviewPressed;
        preview.PointerMoved += OnPreviewMoved;
        preview.PointerReleased += OnPreviewReleased;
        preview.PointerCanceled += OnPreviewReleased;

        readout = new TextBlock { FontSize = 11, Opacity = 0.6 };
        zoomSlider = Slider("縮放", zoomMin, zoomMax, 0.1, value => Change(zoom: value));
        xSlider = Slider("水平位置（精細調整）", 0, 1, 0.01, value => Change(x: value));
        ySlider = Slider("垂直位置（精細調整）", 0, 1, 0.01, value => Change(y: value));

        var sidePreviews = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var size in new[] { 46.0, 88.0 })
        {
            sidePreviews.Children.Add(MakePreview(size));
        }

        var sliders = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        sliders.Children.Add(sidePreviews);
        sliders.Children.Add(new TextBlock
        {
            Text = "小尺寸與大尺寸即時預覽",
            FontSize = 11,
            Opacity = 0.6,
        });
        sliders.Children.Add(zoomSlider);
        sliders.Children.Add(xSlider);
        sliders.Children.Add(ySlider);
        sliders.Children.Add(readout);

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 30,
            Children =
            {
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        preview,
                        new TextBlock
                        {
                            Text = "拖曳圓形預覽來移動圖片",
                            FontSize = 11,
                            Opacity = 0.6,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                },
                sliders,
            },
        });
        root.Children.Add(new TextBlock
        {
            Text = "只儲存裁切參數，不修改原始立繪。按「儲存」後會同步更新所有房間的頭像。",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
        RenderAll();
        return root;
    }

    StudioAvatar MakePreview(double size)
    {
        var avatar = new StudioAvatar();
        avatar.Set(agent, card.Name, card.Color, size, source.Image, draft, null, null, false);
        previews.Add(avatar);
        return avatar;
    }

    static Slider Slider(string header, double min, double max, double step, Action<double> changed)
    {
        var slider = new Slider
        {
            Header = header,
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            Width = 300,
        };
        slider.ValueChanged += (_, args) => changed(args.NewValue);
        return slider;
    }

    // MARK: Drag

    void OnPreviewPressed(object sender, PointerRoutedEventArgs args)
    {
        preview.CapturePointer(args.Pointer);
        lastDragPosition = null;
        args.Handled = true;
    }

    void OnPreviewMoved(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(preview);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (preview.PointerCaptures is not { Count: > 0 }) return;
        var position = point.Position;
        if (lastDragPosition is { } last)
        {
            // Dragging the art right decreases X — Translated handles the sign.
            draft = Fit(draft.Translated(position.X - last.X, position.Y - last.Y,
                PreviewSize, decoded.Width, decoded.Height));
            RenderAll();
        }
        lastDragPosition = position;
    }

    void OnPreviewReleased(object sender, PointerRoutedEventArgs args)
    {
        preview.ReleasePointerCapture(args.Pointer);
        lastDragPosition = null;
    }

    // MARK: Draft updates

    void Change(double? x = null, double? y = null, double? zoom = null)
    {
        if (rendering) return;
        draft = Fit(new PortraitFaceAnchor(x ?? draft.X, y ?? draft.Y, zoom ?? draft.Zoom));
        RenderAll();
    }

    PortraitFaceAnchor Fit(PortraitFaceAnchor anchor) => anchor.Constrained(decoded.Width, decoded.Height, (zoomMin, zoomMax));

    static bool FitEquals(PortraitFaceAnchor a, PortraitFaceAnchor b) =>
        Math.Abs(a.X - b.X) < 0.002 && Math.Abs(a.Y - b.Y) < 0.002 && Math.Abs(a.Zoom - b.Zoom) < 0.05;

    void RenderAll()
    {
        rendering = true;
        try
        {
            foreach (var avatar in previews)
            {
                avatar.Set(agent, card.Name, card.Color, avatar.Width, source.Image, draft, null, null, false);
            }
            zoomSlider.Value = Math.Clamp(draft.Zoom, zoomMin, zoomMax);
            xSlider.Value = draft.X;
            ySlider.Value = draft.Y;
            readout.Text = $"X {draft.X * 100:F1}%  ·  Y {draft.Y * 100:F1}%";
        }
        finally
        {
            rendering = false;
        }
    }

    /// <summary>macOS's per-kind default anchor and zoom range. The uploaded
    /// avatar's cover zoom depends on the picture's own aspect ratio; the
    /// template art defaults to its bundled face anchor.</summary>
    static (PortraitFaceAnchor Default, double Min, double Max) Defaults(AvatarEditorSource source, StudioArtImage decoded)
    {
        switch (source.Kind)
        {
            case AvatarEditorSourceKind.UploadedAvatar:
                var cover = Math.Max(1.0, (double)decoded.Width / Math.Max(1, decoded.Height));
                return (new PortraitFaceAnchor(0.5, 0.5, cover), cover, Math.Max(cover, 8));
            case AvatarEditorSourceKind.UploadedPortrait:
                return (new PortraitFaceAnchor(0.5, 0.15, 3), 1, 8);
            default:
                return (source.DefaultFace ?? new PortraitFaceAnchor(0.5, 0.15, 3), 2.5, 7);
        }
    }

    sealed class SimpleRequester : IStudioArtRequester
    {
        public void InvalidateRender() { }
    }
}
