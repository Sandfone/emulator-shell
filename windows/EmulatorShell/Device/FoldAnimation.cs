using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace EmulatorShell;

/// A foldable folding shut or opening, played from still pictures of both states over the device window's
/// content, whose live view is hidden meanwhile. The window covers the screen's work area while it plays, as the
/// folding half reaches past the device's edges.
///
/// Laid out in the open device's natural orientation, as its skin is drawn: the outer display is on the back
/// of the half left of the hinge (Pixel Fold models), so that half swings over onto the right one, toward
/// the viewer, and past 90° its back, the outer display, comes into view exactly over the right half,
/// which stays put as if lying on a table. The hinge is stiff at first, and magnets pull the halves shut
/// for the last stretch, where they bounce once; opening, they hold for a moment before the hinge gives,
/// and it slows to a stop flat. The whole is held at the device's turn, which folding keeps.
sealed class FoldAnimation
{
    /// Sizes in DIPs, upright, top-left origin.
    public sealed record Geometry(Size Open, Size Closed, double Hinge, Point ClosedOrigin)
    {
        /// Where the closed device lies once folded, in the open device's coordinates.
        public Rect ClosedRect => new(ClosedOrigin.X, ClosedOrigin.Y, Closed.Width, Closed.Height);

        /// From the open device's centre to the closed one's (DIPs, y down), with the device held at `turns`
        /// counterclockwise quarter turns.
        public Point Offset(int turns)
        {
            var closed = ClosedRect;
            double x = closed.X + closed.Width / 2 - Open.Width / 2, y = closed.Y + closed.Height / 2 - Open.Height / 2;
            return (((turns % 4) + 4) % 4) switch
            {
                1 => new Point(y, -x),
                2 => new Point(-x, -y),
                3 => new Point(-y, x),
                _ => new Point(x, y),
            };
        }
    }

    /// A half's frame and display, drawn apart so that the display can light up on its own.
    public sealed record Picture(CanvasRenderTarget Frame, CanvasRenderTarget Screen);

    public static readonly TimeSpan FoldDuration = TimeSpan.FromSeconds(0.6);
    /// A moment ahead: the compositor first takes in the pictures, which would otherwise skip the start.
    private static readonly TimeSpan Preparation = TimeSpan.FromSeconds(1.0 / 15);
    /// How long a display takes to light up with its first live frame.
    private static readonly TimeSpan LightDuration = TimeSpan.FromSeconds(0.2);

    public bool Closing { get; }
    /// When the animation begins, in `DeviceWindow.Now`; whatever goes along with it starts there too.
    public double Start { get; }
    /// Whether the dark display of the state being entered has been given a live frame, and whether that has
    /// finished lighting up.
    public bool HasLiveTarget { get; private set; }
    public bool ShowsLiveTarget { get; private set; }

    /// A half on screen: its display under its frame.
    private sealed record Face(ContainerVisual Visual, SpriteVisual Screen, SpriteVisual Frame);

    private readonly DeviceWindow window;
    private readonly IDisposable hold;
    private readonly Compositor compositor;
    private readonly CompositionGraphicsDevice graphics;
    private readonly ContainerVisual root;
    private readonly Geometry geometry;
    private readonly List<CompositionDrawingSurface> surfaces = [];
    /// The right half, which stays; the left one's front and back, which turn over.
    private readonly Face still, front, back;
    private bool finished;

    /// Folds (`closing`) or unfolds over `window`, with the device held at `turns`; open, the half left of the
    /// hinge is turned `openAngle` degrees toward the viewer (half open). Meanwhile the closed device's centre on
    /// screen (pixels) moves from `closedCenter.From` to `closedCenter.To` and the whole grows by `growth`, which
    /// keeps the window that follows on screen and at its size; both are usually nothing. `completion` runs once
    /// it has finished, with the last picture still showing; `Finish()` then takes it away.
    public FoldAnimation(bool closing, Geometry geometry, Picture open, Picture closed, double openAngle, (Point From, Point To) closedCenter,
                         double growth, int turns, DeviceWindow window, Action completion)
    {
        Closing = closing;
        this.geometry = geometry;
        this.window = window;
        hold = window.Hold(window.Screen.WorkArea);
        Start = DeviceWindow.Now + Preparation.TotalSeconds;

        compositor = ElementCompositionPreview.GetElementVisual(window.Overlay).Compositor;
        graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, DeviceSkin.Device);
        root = compositor.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(window.Overlay, root);

        var size = geometry.Open;
        var hinge = geometry.Hinge;
        var closedRect = geometry.ClosedRect;
        var pivot = new Vector2((float)(closedRect.X + closedRect.Width / 2), (float)(closedRect.Y + closedRect.Height / 2));
        var from = window.FromScreen(closedCenter.From.X, closedCenter.From.Y);
        var to = window.FromScreen(closedCenter.To.X, closedCenter.To.Y);

        // Turned and grown about the closed device's centre, which goes where the window puts it.
        var container = compositor.CreateContainerVisual();
        container.Size = new Vector2((float)size.Width, (float)size.Height);
        container.CenterPoint = new Vector3(pivot, 0);
        container.RotationAngleInDegrees = -90f * turns;
        container.Offset = new Vector3((float)to.X - pivot.X, (float)to.Y - pivot.Y, 0);
        container.Scale = new Vector3((float)growth, (float)growth, 1);
        root.Children.InsertAtTop(container);

        // Some depth, about as a real device seen from a little way off, vanishing at the closed device's centre.
        var stage = compositor.CreateContainerVisual();
        stage.Size = container.Size;
        stage.TransformMatrix = Matrix4x4.CreateTranslation(-pivot.X, -pivot.Y, 0)
                                * new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, -1f / (2.2f * (float)Math.Max(size.Width, size.Height)), 0, 0, 0, 1)
                                * Matrix4x4.CreateTranslation(pivot.X, pivot.Y, 0);
        container.Children.InsertAtTop(stage);

        still = MakeFace(open, new Rect(hinge, 0, size.Width - hinge, size.Height), size);
        stage.Children.InsertAtTop(still.Visual);

        // The left half, which turns about the hinge, its right edge.
        var flap = compositor.CreateContainerVisual();
        flap.Size = new Vector2((float)hinge, (float)size.Height);
        flap.CenterPoint = new Vector3((float)hinge, (float)size.Height / 2, 0);
        flap.RotationAxis = Vector3.UnitY;
        front = MakeFace(open, new Rect(0, 0, hinge, size.Height), size);
        front.Visual.BackfaceVisibility = CompositionBackfaceVisibility.Hidden;
        // The back lands where the closed device lies once the flap has turned over, mirrored about the hinge
        // until then; turned round itself, so that it reads the right way then.
        back = MakeFace(closed, new Rect(0, 0, closedRect.Width, closedRect.Height), geometry.Closed);
        back.Visual.Offset = new Vector3((float)(2 * hinge - closedRect.Right), (float)closedRect.Y, 0);
        back.Visual.CenterPoint = new Vector3(back.Visual.Size / 2, 0);
        back.Visual.RotationAxis = Vector3.UnitY;
        back.Visual.RotationAngleInDegrees = 180;
        back.Visual.BackfaceVisibility = CompositionBackfaceVisibility.Hidden;
        flap.Children.InsertAtTop(front.Visual);
        flap.Children.InsertAtTop(back.Visual);
        stage.Children.InsertAtTop(flap);

        // Composition turns a positive angle about the vertical axis away from the viewer.
        flap.RotationAngleInDegrees = (float)-(closing ? 180 : openAngle);
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        if (growth != 1)
        {
            var growing = compositor.CreateVector3KeyFrameAnimation();
            growing.InsertKeyFrame(0, Vector3.One);
            growing.InsertKeyFrame(1, new Vector3((float)growth, (float)growth, 1), ease);
            Schedule(growing, container, "Scale");
        }
        if (from != to)
        {
            var moving = compositor.CreateVector3KeyFrameAnimation();
            moving.InsertKeyFrame(0, new Vector3((float)from.X - pivot.X, (float)from.Y - pivot.Y, 0));
            moving.InsertKeyFrame(1, new Vector3((float)to.X - pivot.X, (float)to.Y - pivot.Y, 0), ease);
            Schedule(moving, container, "Offset");
        }
        // The motion is sampled finely enough to play back as straight steps between the samples.
        const int steps = 60;
        var linear = compositor.CreateLinearEasingFunction();
        var motion = compositor.CreateScalarKeyFrameAnimation();
        for (var i = 0; i <= steps; i++)
        {
            var progress = (float)i / steps;
            motion.InsertKeyFrame(progress, (float)-(openAngle + Folded(progress, closing) * (180 - openAngle)), linear);
        }
        Schedule(motion, flap, "RotationAngleInDegrees");
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (!finished) completion();
        };
    }

    /// Starts `animation` on `visual` at `Start`, showing its first value until then.
    private void Schedule(KeyFrameAnimation animation, Visual visual, string property)
    {
        animation.Duration = FoldDuration;
        animation.DelayTime = Preparation;
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation(property, animation);
    }

    /// Lights up the dark display of the state being entered (the closed device's outer one, or the open one's
    /// halves) with one of its first live frames (`screen` of a `Picture`), fading it in as a display turning on.
    /// `completion` runs once it is fully lit.
    public void ShowLiveTarget(CanvasRenderTarget screen, Action completion)
    {
        if (finished) return;
        HasLiveTarget = true;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        if (Closing) Light(back, screen, new Rect(0, 0, geometry.Closed.Width, geometry.Closed.Height), geometry.Closed);
        else
        {
            Light(still, screen, new Rect(geometry.Hinge, 0, geometry.Open.Width - geometry.Hinge, geometry.Open.Height), geometry.Open);
            Light(front, screen, new Rect(0, 0, geometry.Hinge, geometry.Open.Height), geometry.Open);
        }
        batch.End();
        batch.Completed += (_, _) =>
        {
            ShowsLiveTarget = true;
            if (!finished) completion();
        };
    }

    /// Fades a lit display in over a face's dark one, under its frame.
    private void Light(Face face, CanvasRenderTarget screen, Rect part, Size size)
    {
        var lit = compositor.CreateSpriteVisual();
        lit.Size = face.Screen.Size;
        lit.Brush = compositor.CreateSurfaceBrush(Surface(screen, part, size));
        face.Visual.Children.InsertAbove(lit, face.Screen);
        var fading = compositor.CreateScalarKeyFrameAnimation();
        fading.InsertKeyFrame(0, 0);
        fading.InsertKeyFrame(1, 1);
        fading.Duration = LightDuration;
        lit.StartAnimation("Opacity", fading);
    }

    /// Takes the pictures away, once the device window shows the new state.
    public void Finish()
    {
        if (finished) return;
        finished = true;
        ElementCompositionPreview.SetElementChildVisual(window.Overlay, null);
        root.Dispose();
        foreach (var surface in surfaces) surface.Dispose();
        graphics.Dispose();
        hold.Dispose();
    }

    /// How far shut (0 open, 1 shut) the device is `progress` of the way through folding or unfolding.
    public static double Folded(double progress, bool closing)
    {
        var p = Math.Clamp(progress, 0, 1);
        if (!closing)
        {
            // The magnets hold for a moment, then the hinge swings open and slows to a stop flat.
            return Math.Pow(1 - Math.Pow(p, 1.3), 3);
        }
        const double caught = 0.82, shut = 0.9;
        if (p < caught)
        {
            // Stiff at first, then steadily on, until the magnets catch it nine tenths shut.
            var x = p / caught;
            return 0.9 * (x + x * x * (3 - 2 * x)) / 2;
        }
        if (p < shut)
        {
            // Pulled shut, faster and faster, from the speed it had.
            double y = (p - caught) / (shut - caught), speed = 0.9 / 2 / caught * (shut - caught);
            return 0.9 + speed * y + (0.1 - speed) * y * y;
        }
        // The halves meet and bounce apart a little once.
        return 1 - 0.025 * Math.Sin(Math.PI * (p - shut) / (1 - shut));
    }

    /// A half: `part` (DIPs of a picture `size` DIPs large) of `picture`'s display under the same part of its frame.
    private Face MakeFace(Picture picture, Rect part, Size size)
    {
        var visual = compositor.CreateContainerVisual();
        visual.Offset = new Vector3((float)part.X, (float)part.Y, 0);
        visual.Size = new Vector2((float)part.Width, (float)part.Height);
        SpriteVisual Layer(CanvasRenderTarget image)
        {
            var sprite = compositor.CreateSpriteVisual();
            sprite.Size = visual.Size;
            sprite.Brush = compositor.CreateSurfaceBrush(Surface(image, part, size));
            visual.Children.InsertAtTop(sprite);
            return sprite;
        }
        var screen = Layer(picture.Screen);
        var frame = Layer(picture.Frame);
        return new Face(visual, screen, frame);
    }

    /// The part `part` (DIPs of a picture `size` DIPs large) of a picture, as a surface of its own.
    private CompositionDrawingSurface Surface(CanvasRenderTarget picture, Rect part, Size size)
    {
        double sx = picture.SizeInPixels.Width / size.Width, sy = picture.SizeInPixels.Height / size.Height;
        var source = new Rect(part.X * sx, part.Y * sy, part.Width * sx, part.Height * sy);
        var pixels = new Windows.Graphics.SizeInt32 { Width = Math.Max(1, (int)Math.Round(source.Width)), Height = Math.Max(1, (int)Math.Round(source.Height)) };
        var surface = graphics.CreateDrawingSurface2(pixels, Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
        using (var ds = CanvasComposition.CreateDrawingSession(surface))
        {
            ds.Clear(Colors.Transparent);
            ds.DrawImage(picture, new Rect(0, 0, pixels.Width, pixels.Height), source);
        }
        surfaces.Add(surface);
        return surface;
    }

    /// The device as it looks: `skin`'s frame, and `screen` (upright for the viewer at its turns, like a frame) in
    /// its display, or a dark display without one; each the device's size, transparent elsewhere. `size` is in
    /// DIPs, drawn at `scale` pixels per DIP.
    public static Picture? MakePicture(DeviceSkin skin, DeviceThumbnail? screen, Size size, double scale)
    {
        try
        {
            var device = DeviceSkin.Device;
            float width = (float)Math.Round(size.Width * scale), height = (float)Math.Round(size.Height * scale);
            double sx = width / skin.Size.Width, sy = height / skin.Size.Height;
            var display = new Rect(skin.Display.X * sx, skin.Display.Y * sy, skin.Display.Width * sx, skin.Display.Height * sy);
            var screenTarget = new CanvasRenderTarget(device, width, height, 96);
            using (var ds = screenTarget.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                using var clip = new CanvasImageBrush(device, skin.ScreenClip);
                clip.Transform = Matrix3x2.CreateScale((float)(display.Width / skin.ScreenClip.SizeInPixels.Width), (float)(display.Height / skin.ScreenClip.SizeInPixels.Height))
                                 * Matrix3x2.CreateTranslation((float)display.X, (float)display.Y);
                using (ds.CreateLayer(clip, display))
                {
                    ds.FillRectangle(display, Colors.Black);
                    if (screen != null)
                    {
                        using var bitmap = CanvasBitmap.CreateFromBytes(device, screen.Pixels, screen.Width, screen.Height,
                                                                        DirectXPixelFormat.R8G8B8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
                        // Frames are upright for the viewer, so inside the upright device they are turned back.
                        var turns = (screen.QuarterTurns % 4 + 4) % 4;
                        var drawn = turns % 2 == 0 ? new Size(display.Width, display.Height) : new Size(display.Height, display.Width);
                        ds.Transform = Matrix3x2.CreateTranslation((float)(-drawn.Width / 2), (float)(-drawn.Height / 2))
                                       * Matrix3x2.CreateRotation((float)(turns * Math.PI / 2))
                                       * Matrix3x2.CreateTranslation((float)(display.X + display.Width / 2), (float)(display.Y + display.Height / 2));
                        ds.DrawImage(bitmap, new Rect(0, 0, drawn.Width, drawn.Height), DeviceSkin.Bounds(bitmap), 1, CanvasImageInterpolation.HighQualityCubic);
                    }
                }
            }
            var frameTarget = new CanvasRenderTarget(device, width, height, 96);
            using (var ds = frameTarget.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                ds.DrawImage(skin.Frame, new Rect(0, 0, width, height), DeviceSkin.Bounds(skin.Frame), 1, CanvasImageInterpolation.HighQualityCubic);
            }
            return new Picture(frameTarget, screenTarget);
        }
        catch (Exception error)
        {
            Log.Error($"cannot draw the fold's pictures: {error.Message}");
            return null;
        }
    }
}
