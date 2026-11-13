using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;

namespace EmulatorShell;

/// The device's frame with its screen: the live picture while running, dark otherwise.
sealed partial class DevicePreview : Grid
{
    private string? skinFolder;
    /// Width over height of the display, for devices without a skin.
    private double aspect;
    private DeviceThumbnail? screen;
    private WriteableBitmap? screenBitmap;

    public DevicePreview()
    {
        SizeChanged += (_, _) => Build();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    public void Show(Avd avd, DeviceType? deviceType, DeviceThumbnail? thumbnail)
    {
        var folder = thumbnail?.SkinFolder ?? avd.SkinFolder ?? deviceType?.SkinFolder;
        var width = double.TryParse(avd.Config.GetValueOrDefault("hw.lcd.width"), out var w) ? w : 1080;
        var height = double.TryParse(avd.Config.GetValueOrDefault("hw.lcd.height"), out var h) ? h : 2400;
        Set(folder, width / height, thumbnail);
    }

    public void Show(DeviceType? deviceType, bool landscape) =>
        Set(deviceType?.SkinFolder, landscape ? 16.0 / 10.0 : 9.0 / 19.5, null);

    private void Set(string? folder, double aspect, DeviceThumbnail? thumbnail)
    {
        var rebuild = folder != skinFolder || aspect != this.aspect || (thumbnail == null) != (screen == null)
                      || thumbnail?.QuarterTurns != screen?.QuarterTurns || thumbnail?.Width != screen?.Width || thumbnail?.Height != screen?.Height;
        skinFolder = folder;
        this.aspect = aspect;
        screen = thumbnail;
        if (rebuild) Build();
        else if (thumbnail != null) UpdateScreen(thumbnail);
    }

    /// Lays the preview out again for its size and the device's turn.
    private void Build()
    {
        Children.Clear();
        screenBitmap = null;
        double boxWidth = ActualWidth, boxHeight = ActualHeight;
        if (boxWidth <= 0 || boxHeight <= 0) return;
        var turns = screen?.QuarterTurns ?? 0;
        var sideways = turns % 2 == 1;

        if (skinFolder != null && SkinGeometry.Read(skinFolder) is { } skin)
        {
            // Drawn upright and turned as a whole like the device window; the screen, already upright for
            // the viewer, is turned back inside it.
            var shown = sideways ? new Size(skin.Size.Height, skin.Size.Width) : skin.Size;
            var scale = Math.Min(boxWidth / shown.Width, boxHeight / shown.Height);
            var device = new Canvas { Width = skin.Size.Width * scale, Height = skin.Size.Height * scale };
            var display = new Rect(skin.Display.X * scale, skin.Display.Y * scale, skin.Display.Width * scale, skin.Display.Height * scale);
            var screenArea = new Border
            {
                Width = display.Width,
                Height = display.Height,
                CornerRadius = new CornerRadius(skin.CornerRadius * scale),
                Background = new SolidColorBrush(Color.FromArgb(255, 15, 15, 15)),
            };
            Canvas.SetLeft(screenArea, display.X);
            Canvas.SetTop(screenArea, display.Y);
            if (screen != null)
            {
                var image = new Image
                {
                    Stretch = Stretch.Fill,
                    Width = sideways ? display.Height : display.Width,
                    Height = sideways ? display.Width : display.Height,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform { Angle = turns * 90 },
                    Source = Bitmap(screen),
                };
                // Turned about its centre, which is the display's.
                Canvas.SetLeft(image, (display.Width - image.Width) / 2);
                Canvas.SetTop(image, (display.Height - image.Height) / 2);
                screenArea.Child = new Canvas { Children = { image } };
            }
            device.Children.Add(screenArea);
            var frame = new Image
            {
                Source = new BitmapImage(new Uri(skin.FramePath)),
                Stretch = Stretch.Fill,
                Width = skin.FrameRect.Width * scale,
                Height = skin.FrameRect.Height * scale,
            };
            Canvas.SetLeft(frame, skin.FrameRect.X * scale);
            Canvas.SetTop(frame, skin.FrameRect.Y * scale);
            device.Children.Add(frame);
            device.RenderTransformOrigin = new Point(0.5, 0.5);
            device.RenderTransform = new RotateTransform { Angle = -turns * 90 };
            var holder = new Grid
            {
                Width = shown.Width * scale,
                Height = shown.Height * scale,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Translation = new System.Numerics.Vector3(0, 0, 16),
                Shadow = new ThemeShadow(),
            };
            device.HorizontalAlignment = HorizontalAlignment.Center;
            device.VerticalAlignment = VerticalAlignment.Center;
            holder.Children.Add(device);
            Children.Add(holder);
        }
        else
        {
            // No artwork to turn: the box takes the screen's shape as the viewer sees it.
            var shownAspect = sideways ? 1 / aspect : aspect;
            var width = Math.Min(boxWidth, boxHeight * shownAspect);
            var height = width / shownAspect;
            var shortSide = Math.Min(width, height);
            var body = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(shortSide * 0.12),
                Background = new SolidColorBrush(screen == null ? Color.FromArgb(255, 28, 28, 28) : Colors.Black),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (screen != null)
            {
                body.Padding = new Thickness(shortSide * 0.035);
                body.Child = new Border { CornerRadius = new CornerRadius(shortSide * 0.09), Child = new Image { Source = Bitmap(screen), Stretch = Stretch.Fill } };
            }
            else
            {
                body.Child = new TextBlock { Text = "没有外观图", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(178, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            Children.Add(body);
        }
    }

    private WriteableBitmap Bitmap(DeviceThumbnail thumbnail)
    {
        screenBitmap = new WriteableBitmap(thumbnail.Width, thumbnail.Height);
        UpdateScreen(thumbnail);
        return screenBitmap;
    }

    /// RGBA frames into the bitmap's BGRA, opaque.
    private void UpdateScreen(DeviceThumbnail thumbnail)
    {
        if (screenBitmap == null || screenBitmap.PixelWidth != thumbnail.Width || screenBitmap.PixelHeight != thumbnail.Height) return;
        var pixels = thumbnail.Pixels;
        var bgra = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            bgra[i] = pixels[i + 2];
            bgra[i + 1] = pixels[i + 1];
            bgra[i + 2] = pixels[i];
            bgra[i + 3] = 255;
        }
        using (var stream = screenBitmap.PixelBuffer.AsStream()) stream.Write(bgra);
        screenBitmap.Invalidate();
    }
}
