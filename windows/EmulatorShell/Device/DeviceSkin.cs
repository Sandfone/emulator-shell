using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace EmulatorShell;

/// An emulator skin folder (`layout` + frame image + screen mask), read in the device's natural orientation.
sealed class DeviceSkin
{
    /// Frame artwork with the mask's bezel corners and camera cutout drawn in, at the skin's full size;
    /// transparent over the visible screen.
    public required CanvasBitmap Frame { get; init; }
    /// Alpha mask for the display that hides its square corners where they poke out of the frame.
    public required CanvasBitmap ScreenClip { get; init; }
    /// Size of the whole skin in skin pixels.
    public required Size Size { get; init; }
    /// Display rectangle in skin pixels, top-left origin. Its size equals the device resolution, unless the
    /// skin has been `Scaled`.
    public required Rect Display { get; init; }
    /// The device's outline in skin pixels, top-left origin: where the frame artwork is drawn.
    public required Rect Body { get; init; }
    /// The display's size in device pixels.
    public required Size Resolution { get; init; }
    /// Hardware buttons drawn on the frame's edge, clickable like the real ones.
    public required IReadOnlyList<SideButton> SideButtons { get; init; }
    /// How far in from each corner of the skin the device's body starts, measured along the diagonal (in skin
    /// pixels per axis): where a rounded phone's visible corner is. Clockwise from the top left.
    public required double[] CornerInsets { get; init; }

    public static CanvasDevice Device => CanvasDevice.GetSharedDevice();

    /// The skin cut down to the device's outline. Some layouts leave room around it (a Pixel Fold's closed one
    /// 59 pixels below), which would count when fitting the device on the screen.
    public DeviceSkin CroppedToBody()
    {
        if (Body == new Rect(0, 0, Size.Width, Size.Height)) return this;
        var body = Body;
        var frame = Render(new Size(body.Width, body.Height), ds => ds.DrawImage(Frame, (float)-body.X, (float)-body.Y));
        Rect Shift(Rect rect) => new(rect.X - body.X, rect.Y - body.Y, rect.Width, rect.Height);
        var size = new Size(body.Width, body.Height);
        return new DeviceSkin
        {
            Frame = frame,
            ScreenClip = ScreenClip,
            Size = size,
            Display = Shift(Display),
            Body = new Rect(0, 0, body.Width, body.Height),
            Resolution = Resolution,
            SideButtons = SideButtons.Select(b => b with { Rect = Shift(b.Rect) }).ToList(),
            CornerInsets = MeasureCornerInsets(frame.GetPixelBytes(), (int)frame.SizeInPixels.Width, (int)frame.SizeInPixels.Height, size),
        };
    }

    /// The skin drawn `factor` times as large in skin pixels; the artwork is resampled when drawn. A foldable's
    /// closed skin has denser pixels than its open one, and is scaled to match it so that the device is as
    /// tall either way.
    public DeviceSkin Scaled(double factor)
    {
        Rect Scale(Rect rect) => new(rect.X * factor, rect.Y * factor, rect.Width * factor, rect.Height * factor);
        return new DeviceSkin
        {
            Frame = Frame,
            ScreenClip = ScreenClip,
            Size = new Size(Size.Width * factor, Size.Height * factor),
            Display = Scale(Display),
            Body = Scale(Body),
            Resolution = Resolution,
            SideButtons = SideButtons.Select(b => b with { Rect = Scale(b.Rect) }).ToList(),
            CornerInsets = CornerInsets.Select(inset => inset * factor).ToArray(),
        };
    }

    /// The AVD's skin, or a plain rounded bezel when it has none: many device definitions ship no artwork.
    public static Task<DeviceSkin> Load(Avd avd)
    {
        if (avd.SkinFolder is { } folder) return Load(folder);
        if (!double.TryParse(avd.Config.GetValueOrDefault("hw.lcd.width"), out var width)
            || !double.TryParse(avd.Config.GetValueOrDefault("hw.lcd.height"), out var height))
            throw new ShellException($"{avd.Id} 既没有外观图，也没有 hw.lcd.width/height");
        return Task.Run(() => Frameless(new Size(width, height)));
    }

    public static async Task<DeviceSkin> Load(string folder)
    {
        var layout = LayoutNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "layout")));
        // Tablets and other landscape-first devices only define a landscape layout, which is then their
        // natural orientation: the one the emulator calls unrotated.
        var displayNode = layout["parts"]?["device"]?["display"];
        var mainLayout = layout["layouts"]?["portrait"] ?? layout["layouts"]?["landscape"];
        var devicePart = mainLayout?.Children.FirstOrDefault(c => c.Value["name"]?.Text == "device").Value;
        var portraitPart = layout["parts"]?["portrait"];
        if (displayNode?.Number("width") is not { } displayWidth || displayNode.Number("height") is not { } displayHeight
            || mainLayout?.Number("width") is not { } skinWidth || mainLayout.Number("height") is not { } skinHeight
            || devicePart?.Number("x") is not { } displayX || devicePart.Number("y") is not { } displayY
            || portraitPart?["background"]?["image"]?.Text is not { } frameName)
            throw new ShellException($"不支持的外观布局：{folder}");

        var display = new Rect(displayX, displayY, displayWidth, displayHeight);
        var skinSize = new Size(skinWidth, skinHeight);
        var background = await LoadImage(Path.Combine(folder, frameName));
        var maskName = portraitPart["foreground"]?["mask"]?.Text;
        var mask = maskName == null ? null : await LoadImage(Path.Combine(folder, maskName));
        var cornerRadius = displayNode.Number("corner_radius") ?? 0;

        return await Task.Run(() =>
        {
            // Like the emulator, the image keeps its own pixel size; stretching it to the layout size shifts the
            // frame's edge away from the mask's near the far corners.
            var frameRect = FrameRect(mainLayout, background.SizeInPixels.Width, background.SizeInPixels.Height);
            var frameOnly = Render(skinSize, ds => ds.DrawImage(background, frameRect, Bounds(background), 1, CanvasImageInterpolation.NearestNeighbor));
            // The mask draws the bezel's corners and the camera cutout over the display, partly repeating the
            // frame's edge. Drawn into the frame here, both are resampled together; as separate visuals each is
            // scaled to a slightly different rectangle and the edge they share no longer lines up.
            var frame = mask == null ? frameOnly : Render(skinSize, ds =>
            {
                ds.DrawImage(mask, display, Bounds(mask), 1, CanvasImageInterpolation.HighQualityCubic);
                ds.DrawImage(frameOnly);
            });

            var screenClip = mask != null
                ? MakeScreenClip(mask, display.Width, display.Height)
                : Render(new Size(display.Width, display.Height), ds =>
                    ds.FillRoundedRectangle(new Rect(0, 0, display.Width, display.Height), (float)cornerRadius, (float)cornerRadius, Colors.White));

            var framePixels = frameOnly.GetPixelBytes();
            return new DeviceSkin
            {
                Frame = frame,
                ScreenClip = screenClip,
                Size = skinSize,
                Display = display,
                Body = OpaqueBounds(framePixels, (int)frameOnly.SizeInPixels.Width, (int)frameOnly.SizeInPixels.Height),
                Resolution = new Size(display.Width, display.Height),
                SideButtons = FindSideButtons(framePixels, (int)frameOnly.SizeInPixels.Width, (int)frameOnly.SizeInPixels.Height, display),
                CornerInsets = MeasureCornerInsets(frame.GetPixelBytes(), (int)frame.SizeInPixels.Width, (int)frame.SizeInPixels.Height, skinSize),
            };
        });
    }

    /// Where the background image goes in skin pixels: at the layout's background part, at the image's
    /// own size. Some skins' images differ from the layout by a few pixels (Pixel 10 Pro: 1410 × 2968
    /// against a 1408 × 2965 layout), and Wear OS ones are smaller and offset.
    public static Rect FrameRect(LayoutNode layout, double imageWidth, double imageHeight)
    {
        var part = layout.Children.FirstOrDefault(c => c.Value["name"]?.Text == "portrait").Value;
        return new Rect(part?.Number("x") ?? 0, part?.Number("y") ?? 0, imageWidth, imageHeight);
    }

    /// A dark rounded bezel around a display of the given pixel size, for devices without skin artwork.
    public static DeviceSkin Frameless(Size size)
    {
        var shortSide = Math.Min(size.Width, size.Height);
        var bezel = Math.Round(shortSide * 0.035);
        var outerRadius = (float)(shortSide * 0.09);
        var innerRadius = outerRadius - (float)bezel;
        var skinSize = new Size(size.Width + 2 * bezel, size.Height + 2 * bezel);
        var display = new Rect(bezel, bezel, size.Width, size.Height);

        var frame = Render(skinSize, ds =>
        {
            var bounds = new Rect(0, 0, skinSize.Width, skinSize.Height);
            using var outer = CanvasGeometry.CreateRoundedRectangle(ds, bounds, outerRadius, outerRadius);
            using var inner = CanvasGeometry.CreateRoundedRectangle(ds, display, innerRadius, innerRadius);
            using var body = outer.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            ds.FillGeometry(body, Color.FromArgb(255, 28, 28, 28));
            ds.DrawRoundedRectangle(new Rect(1.5, 1.5, skinSize.Width - 3, skinSize.Height - 3), outerRadius - 1.5f, outerRadius - 1.5f,
                                    Color.FromArgb(255, 82, 82, 82), 3);
        });
        var screenClip = Render(size, ds => ds.FillRoundedRectangle(new Rect(0, 0, size.Width, size.Height), innerRadius, innerRadius, Colors.White));
        return new DeviceSkin
        {
            Frame = frame,
            ScreenClip = screenClip,
            Size = skinSize,
            Display = display,
            Body = new Rect(0, 0, skinSize.Width, skinSize.Height),
            Resolution = size,
            SideButtons = [],
            CornerInsets = MeasureCornerInsets(frame.GetPixelBytes(), (int)frame.SizeInPixels.Width, (int)frame.SizeInPixels.Height, skinSize),
        };
    }

    public static CanvasRenderTarget Render(Size size, Action<CanvasDrawingSession> draw)
    {
        var target = new CanvasRenderTarget(Device, (float)Math.Ceiling(size.Width), (float)Math.Ceiling(size.Height), 96);
        using var ds = target.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
        draw(ds);
        return target;
    }

    public static Rect Bounds(CanvasBitmap bitmap) => new(0, 0, bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height);

    private static async Task<CanvasBitmap> LoadImage(string path)
    {
        try
        {
            return await CanvasBitmap.LoadAsync(Device, path, 96);
        }
        catch (Exception error)
        {
            throw new ShellException($"无法解码 {path}（WebP 外观图需要 Windows 的 WebP 图像扩展）：{error.Message}");
        }
    }

    /// Skins carry no button metadata, so the buttons are found as bumps on the frame's right edge:
    /// the longer one is the volume rocker (top half up, bottom half down), the shorter one power.
    private static List<SideButton> FindSideButtons(byte[] pixels, int width, int height, Rect display)
    {
        int RightEdge(int y)
        {
            var x = width - 1;
            while (x > 0 && pixels[(y * width + x) * 4 + 3] < 128) x--;
            return x;
        }
        var edges = Enumerable.Range(0, height).Select(RightEdge).ToArray();
        // The median over the display's rows: any single row may be one of the buttons.
        int top = Math.Clamp((int)display.Y, 0, height - 1), bottom = Math.Clamp((int)(display.Y + display.Height), top + 1, height);
        var body = edges[top..bottom].OrderBy(e => e).ElementAt((bottom - top) / 2);
        var bumps = new List<(int Start, int End)>();
        int? start = null;
        for (var y = 0; y < height; y++)
        {
            var outside = edges[y] > body + 2;
            if (outside && start == null) start = y;
            if (!outside && start is { } first)
            {
                bumps.Add((first, y));
                start = null;
            }
        }
        Log.Debug($"side buttons: frame edge at x {body}, bumps at rows {string.Join(", ", bumps)}");
        if (bumps.Count is < 1 or > 2) return [];

        // A hit area reaching into the bezel, since the drawn bumps are only a few pixels wide.
        Rect RectFor(int from, int to) => new(body - 24, from, width - body + 24, to - from);
        var sorted = bumps.OrderBy(b => b.End - b.Start).ToList();
        var buttons = new List<SideButton> { new("Power", "电源", RectFor(sorted[0].Start, sorted[0].End)) };
        if (sorted.Count == 2)
        {
            var rocker = sorted[1];
            var middle = rocker.Start + (rocker.End - rocker.Start) / 2;
            buttons.Add(new SideButton("AudioVolumeUp", "音量 +", RectFor(rocker.Start, middle)));
            buttons.Add(new SideButton("AudioVolumeDown", "音量 −", RectFor(middle, rocker.End)));
        }
        return buttons;
    }

    /// Where the screen shows: the mask's opening, reaching a little under the bezel around it.
    ///
    /// The display rectangle's square corners stick out past the rounded frame on some skins. The stock
    /// emulator hides them by shaping its window; here they are flood-filled from each corner through the
    /// mask's transparent pixels and left out. The screen must not run under the whole bezel either: it
    /// would show through the bezel's soft outer edge.
    private static CanvasBitmap MakeScreenClip(CanvasBitmap mask, double displayWidth, double displayHeight)
    {
        int width = (int)displayWidth, height = (int)displayHeight;
        using var scaled = Render(new Size(width, height), ds =>
            ds.DrawImage(mask, new Rect(0, 0, width, height), Bounds(mask), 1, CanvasImageInterpolation.HighQualityCubic));
        var pixels = scaled.GetPixelBytes();
        byte Alpha(int index) => pixels[index * 4 + 3];

        var outside = new bool[width * height];
        var stack = new Stack<int>(new[] { 0, width - 1, (height - 1) * width, height * width - 1 }.Where(i => Alpha(i) < 128));
        var filled = 0;
        while (stack.Count > 0)
        {
            var index = stack.Pop();
            if (outside[index] || Alpha(index) >= 128) continue;
            outside[index] = true;
            filled++;
            int x = index % width, y = index / width;
            if (x > 0) stack.Push(index - 1);
            if (x < width - 1) stack.Push(index + 1);
            if (y > 0) stack.Push(index - width);
            if (y < height - 1) stack.Push(index + width);
        }
        // A fill this large means the mask is open to the screen area; keep the corners rather than lose the screen.
        if (filled > width * height / 100)
        {
            Log.Error($"corner flood reached {filled} px; skipping corner clipping");
            Array.Clear(outside);
        }

        // The opening, grown by a few skin pixels so it runs under the bezel's soft inner edge at any scale
        // while staying far inside the bezel's outer edge.
        const int reach = 4;
        var opening = new byte[width * height];
        for (var i = 0; i < opening.Length; i++) opening[i] = (byte)(!outside[i] && Alpha(i) < 128 ? 1 : 0);
        var grown = Dilate(opening, width, height, reach);

        var clip = new byte[width * height * 4];
        for (var i = 0; i < grown.Length; i++)
        {
            if (grown[i] != 0 && !outside[i]) clip[i * 4] = clip[i * 4 + 1] = clip[i * 4 + 2] = clip[i * 4 + 3] = 255;
        }
        return CanvasBitmap.CreateFromBytes(Device, clip, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
    }

    /// A square max filter of side `2 * reach + 1`, done along rows then columns.
    private static byte[] Dilate(byte[] source, int width, int height, int reach)
    {
        var rows = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            var count = 0;
            var row = y * width;
            for (var x = 0; x < reach && x < width; x++) count += source[row + x];
            for (var x = 0; x < width; x++)
            {
                if (x + reach < width) count += source[row + x + reach];
                if (x - reach - 1 >= 0) count -= source[row + x - reach - 1];
                rows[row + x] = (byte)(count > 0 ? 1 : 0);
            }
        }
        var result = new byte[source.Length];
        for (var x = 0; x < width; x++)
        {
            var count = 0;
            for (var y = 0; y < reach && y < height; y++) count += rows[y * width + x];
            for (var y = 0; y < height; y++)
            {
                if (y + reach < height) count += rows[(y + reach) * width + x];
                if (y - reach - 1 >= 0) count -= rows[(y - reach - 1) * width + x];
                result[y * width + x] = (byte)(count > 0 ? 1 : 0);
            }
        }
        return result;
    }

    /// Where the image is more than half opaque, top-left origin; all of it if nowhere is.
    private static Rect OpaqueBounds(byte[] pixels, int width, int height)
    {
        int left = width, right = -1, top = height, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] <= 128) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        return right < 0 ? new Rect(0, 0, width, height) : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    private static double[] MeasureCornerInsets(byte[] pixels, int width, int height, Size size)
    {
        int right = width - 1, bottom = height - 1, limit = Math.Min(width, height) / 2;
        (int X, int Y, int Dx, int Dy)[] corners = [(0, 0, 1, 1), (right, 0, -1, 1), (right, bottom, -1, -1), (0, bottom, 1, -1)];
        return corners.Select(c =>
        {
            var steps = 0;
            while (steps < limit && pixels[((c.Y + steps * c.Dy) * width + c.X + steps * c.Dx) * 4 + 3] <= 128) steps++;
            return steps >= limit ? 0 : steps * size.Width / width;
        }).ToArray();
    }
}

/// `Key`: W3C key name sent while the button is held. `Rect`: hit area in skin pixels, top-left origin.
sealed record SideButton(string Key, string Label, Rect Rect);

/// Where a skin's frame image and display are, read from its `layout` alone: enough for small previews.
sealed record SkinGeometry(string FramePath, Rect FrameRect, Size Size, Rect Display, double CornerRadius)
{
    private static readonly Dictionary<string, SkinGeometry?> Cache = new();

    public static SkinGeometry? Read(string folder)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(folder, out var cached)) return cached;
        }
        SkinGeometry? geometry = null;
        try
        {
            var layout = LayoutNode.Parse(File.ReadAllText(Path.Combine(folder, "layout")));
            var displayNode = layout["parts"]?["device"]?["display"];
            var main = layout["layouts"]?["portrait"] ?? layout["layouts"]?["landscape"];
            var device = main?.Children.FirstOrDefault(c => c.Value["name"]?.Text == "device").Value;
            var frameName = layout["parts"]?["portrait"]?["background"]?["image"]?.Text;
            if (displayNode != null && main != null && device != null && frameName != null
                && main.Number("width") is { } width && main.Number("height") is { } height
                && ImageHeader.Size(Path.Combine(folder, frameName)) is { } imageSize)
            {
                geometry = new SkinGeometry(
                    Path.Combine(folder, frameName),
                    DeviceSkin.FrameRect(main, imageSize.Width, imageSize.Height),
                    new Size(width, height),
                    new Rect(device.Number("x") ?? 0, device.Number("y") ?? 0, displayNode.Number("width") ?? 0, displayNode.Number("height") ?? 0),
                    displayNode.Number("corner_radius") ?? 0);
            }
        }
        catch { }
        lock (Cache) Cache[folder] = geometry;
        return geometry;
    }
}

/// The pixel size of a PNG or WebP image from its header, without decoding it.
static class ImageHeader
{
    public static (int Width, int Height)? Size(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[30];
            if (stream.Read(header, 0, header.Length) < header.Length) return null;
            if (header[0] == 0x89 && header[1] == 'P' && header[2] == 'N' && header[3] == 'G')
                return (BigEndian(header, 16), BigEndian(header, 20));
            if (header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' && header[8] == 'W' && header[9] == 'E')
            {
                var chunk = System.Text.Encoding.ASCII.GetString(header, 12, 4);
                return chunk switch
                {
                    "VP8 " => (BitConverter.ToUInt16(header, 26) & 0x3FFF, BitConverter.ToUInt16(header, 28) & 0x3FFF),
                    "VP8L" => (1 + (header[21] | (header[22] & 0x3F) << 8), 1 + ((header[22] >> 6) | header[23] << 2 | (header[24] & 0x0F) << 10)),
                    "VP8X" => (1 + (header[24] | header[25] << 8 | header[26] << 16), 1 + (header[27] | header[28] << 8 | header[29] << 16)),
                    _ => null,
                };
            }
        }
        catch { }
        return null;
    }

    private static int BigEndian(byte[] bytes, int offset) =>
        bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
}

/// The emulator skin `layout` format: whitespace-separated `key value` pairs and `key { ... }` blocks.
sealed class LayoutNode
{
    public string? Text { get; init; }
    public List<KeyValuePair<string, LayoutNode>> Children { get; } = [];

    public LayoutNode? this[string key] => Children.FirstOrDefault(c => c.Key == key).Value;

    public double? Number(string key) =>
        double.TryParse(this[key]?.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    public static LayoutNode Parse(string source)
    {
        var tokens = new Queue<string>(source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var root = ParseBlock(tokens);
        if (tokens.Count > 0) throw new ShellException("layout 里有多余的 '}'");
        return root;
    }

    private static LayoutNode ParseBlock(Queue<string> tokens)
    {
        var node = new LayoutNode();
        while (tokens.TryPeek(out var key) && key != "}")
        {
            tokens.Dequeue();
            if (!tokens.TryDequeue(out var next)) throw new ShellException($"layout 在 '{key}' 之后结束");
            if (next == "{")
            {
                var child = ParseBlock(tokens);
                if (!tokens.TryDequeue(out var close) || close != "}") throw new ShellException($"layout 里的 '{key}' 没有闭合");
                node.Children.Add(new(key, child));
            }
            else
            {
                node.Children.Add(new(key, new LayoutNode { Text = next }));
            }
        }
        return node;
    }
}
