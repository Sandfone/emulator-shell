using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace EmulatorShell;

/// Renders the device: the live display under the skin's frame, both composited by the Visual layer with real
/// alpha so every edge is antialiased against whatever is behind the window.
sealed partial class DeviceView : Grid
{
    /// A touch at a device pixel went down / moved (`true`) or up.
    public event Action<Point, bool>? Touch;
    /// A side button went down (`true`) or up; the key is a W3C key name.
    public event Action<string, bool>? SideButtonKey;
    /// The pointer went down on the screen, before that press becomes a touch.
    public event Action? ScreenPressed;
    /// The pointer went down on the frame away from the screen and buttons: the window should be dragged.
    public event Action? DragRequested;
    /// The window moved to a monitor of another scale; the visuals have been drawn again for it.
    public event Action? RasterScaleChanged;
    /// The rasterization scale the visuals were last drawn at.
    private double drawnScale;

    private DeviceSkin skin;
    /// `DeviceSkin.CornerInsets`, of the skin shown.
    private double[] skinCornerInsets;
    /// Size of the upright device in whole DIPs, so a quarter turn about its centre keeps pixels on the grid.
    public Size DeviceSize { get; private set; }
    /// DIPs per skin pixel the visuals are drawn for.
    public double DrawnScale { get; private set; }
    /// DIPs per skin pixel shown; differs from `DrawnScale` only while the window is being resized.
    public double LiveScale { get; private set; }
    /// DIPs per skin pixel, per axis (they differ by the rounding of `DeviceSize`).
    private double scaleX, scaleY;
    /// Counterclockwise quarter turns the device is drawn at.
    public int QuarterTurns { get; private set; }
    /// Quarter turns of the frame last shown, which the emulator already turned upright.
    private int contentTurns;

    private readonly Compositor compositor;
    private readonly CompositionGraphicsDevice graphics;
    /// Holds everything that turns with the device; its own coordinates are the upright device's.
    private readonly ContainerVisual deviceVisual;
    private readonly SpriteVisual silhouetteVisual, screenVisual, frameVisual, flashVisual, highlightVisual;
    private readonly CompositionSurfaceBrush contentBrush, clipBrush, frameBrush, silhouetteBrush;
    /// Holds the display and frame; clipped to the right of the hinge while a foldable is half open, when
    /// `flapVisual` shows the half left of it, turned toward the viewer about the hinge.
    private readonly ContainerVisual bodyVisual;
    /// Gives the turned half its depth, about as the fold animation's (`FoldAnimation`).
    private readonly ContainerVisual flapStage, flapVisual;
    /// The flap's own display and frame, showing what the body's do through the same brushes.
    private readonly SpriteVisual flapScreen, flapFrame;
    /// How far the half left of the hinge is turned toward the viewer, in degrees, and where the hinge is, in skin
    /// pixels from the left.
    public double FoldAngle { get; private set; }
    private double hinge;
    private readonly DropShadow shadow;
    private CompositionDrawingSurface? screenSurface, clipSurface, frameSurface, silhouetteSurface;
    private CanvasBitmap? frameBitmap;
    private bool isTouching;
    private uint touchPointer;
    private SideButton? pressedButton, hoveredButton;
    private ToolTip? buttonTip;

    /// The device's turn as last set, and while a turn animates where it started, to work out where it is.
    private float shownAngle;
    private (float From, float To, double Start)? turnAnimation;

    public DeviceView(DeviceSkin skin, double scale)
    {
        this.skin = skin;
        skinCornerInsets = skin.CornerInsets;
        DrawnScale = LiveScale = scale;
        DeviceSize = new Size(Math.Round(skin.Size.Width * scale), Math.Round(skin.Size.Height * scale));
        scaleX = DeviceSize.Width / skin.Size.Width;
        scaleY = DeviceSize.Height / skin.Size.Height;
        // Hit-testable everywhere, like the frame it shows.
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        AllowFocusOnInteraction = true;

        compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, DeviceSkin.Device);
        graphics.RenderingDeviceReplaced += (_, _) => DispatcherQueue.TryEnqueue(RebuildVisuals);

        deviceVisual = compositor.CreateContainerVisual();
        contentBrush = compositor.CreateSurfaceBrush();
        contentBrush.Stretch = CompositionStretch.Fill;
        clipBrush = compositor.CreateSurfaceBrush();
        clipBrush.Stretch = CompositionStretch.Fill;
        frameBrush = compositor.CreateSurfaceBrush();
        frameBrush.Stretch = CompositionStretch.Fill;

        var screenMask = compositor.CreateMaskBrush();
        screenMask.Source = contentBrush;
        screenMask.Mask = clipBrush;
        screenVisual = compositor.CreateSpriteVisual();
        screenVisual.Brush = screenMask;

        var flashMask = compositor.CreateMaskBrush();
        flashMask.Source = compositor.CreateColorBrush(Colors.White);
        flashMask.Mask = clipBrush;
        flashVisual = compositor.CreateSpriteVisual();
        flashVisual.Brush = flashMask;
        flashVisual.Opacity = 0;
        flashVisual.RelativeSizeAdjustment = Vector2.One;
        screenVisual.Children.InsertAtTop(flashVisual);

        frameVisual = compositor.CreateSpriteVisual();
        frameVisual.Brush = frameBrush;

        // The window's shadow, as macOS gives a window: cast by everything the device shows (frame and screen,
        // drawn underneath both), so the frame's hole casts none onto the screen.
        silhouetteBrush = compositor.CreateSurfaceBrush();
        silhouetteBrush.Stretch = CompositionStretch.Fill;
        silhouetteVisual = compositor.CreateSpriteVisual();
        silhouetteVisual.Brush = silhouetteBrush;
        shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 10;
        shadow.Opacity = 0.35f;
        shadow.Color = Colors.Black;
        shadow.SourcePolicy = CompositionDropShadowSourcePolicy.InheritFromVisualContent;
        silhouetteVisual.Shadow = shadow;

        highlightVisual = compositor.CreateSpriteVisual();
        highlightVisual.Brush = compositor.CreateColorBrush(Color.FromArgb(89, 0, 102, 214));
        highlightVisual.IsVisible = false;

        // The screen goes below the frame, whose transparent hole shows it.
        bodyVisual = compositor.CreateContainerVisual();
        bodyVisual.Children.InsertAtTop(silhouetteVisual);
        bodyVisual.Children.InsertAtTop(screenVisual);
        bodyVisual.Children.InsertAtTop(frameVisual);
        bodyVisual.Children.InsertAtTop(highlightVisual);
        deviceVisual.Children.InsertAtTop(bodyVisual);

        flapScreen = compositor.CreateSpriteVisual();
        flapScreen.Brush = screenMask;
        flapFrame = compositor.CreateSpriteVisual();
        flapFrame.Brush = frameBrush;
        flapVisual = compositor.CreateContainerVisual();
        flapVisual.RotationAxis = Vector3.UnitY;
        flapVisual.Children.InsertAtTop(flapScreen);
        flapVisual.Children.InsertAtTop(flapFrame);
        flapStage = compositor.CreateContainerVisual();
        flapStage.IsVisible = false;
        flapStage.Children.InsertAtTop(flapVisual);
        deviceVisual.Children.InsertAtTop(flapStage);
        ElementCompositionPreview.SetElementChildVisual(this, deviceVisual);

        Loaded += (_, _) =>
        {
            RebuildVisuals();
            XamlRoot.Changed += (_, _) =>
            {
                if (XamlRoot.RasterizationScale == drawnScale) return;
                RebuildVisuals();
                RasterScaleChanged?.Invoke();
            };
        };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        PointerExited += (_, _) => { if (pressedButton == null) Highlight(null); };
        ApplyTransform();
    }

    public DeviceSkin Skin => skin;

    /// Device pixels per DIP on the monitor the window is on.
    private double RasterScale => XamlRoot?.RasterizationScale ?? 1;

    /// The view's size with the device at `turns` quarter turns and `scale` DIPs per skin pixel
    /// (by default the one shown).
    public Size SizeFor(int turns, DeviceSkin? skin = null, double? scale = null)
    {
        var s = scale ?? LiveScale;
        skin ??= this.skin;
        var size = new Size(Math.Round(skin.Size.Width * s), Math.Round(skin.Size.Height * s));
        return turns % 2 == 0 ? size : new Size(size.Height, size.Width);
    }

    /// How far in from the corner of this view, per axis, the device's body starts at the shown size, for the
    /// corner `clockwise` steps round from the top left.
    public double CornerInset(int clockwise) =>
        // A counterclockwise quarter turn brings the skin's next corner clockwise to each corner of the view.
        skinCornerInsets[(clockwise + QuarterTurns) % 4] * LiveScale;

    /// The skin's size in skin pixels with the device at `turns` quarter turns.
    public Size SkinSizeFor(int turns) => turns % 2 == 0 ? skin.Size : new Size(skin.Size.Height, skin.Size.Width);

    /// While the window is resized: stretches what is already drawn, which keeps up with the pointer.
    public void SetLiveScale(double scale)
    {
        LiveScale = scale;
        StopTurnAnimation();
        ApplyTransform();
    }

    /// Once resizing ends: draws the skin again at the new size so it is sharp.
    public void CommitScale()
    {
        if (LiveScale == DrawnScale) return;
        DrawnScale = LiveScale;
        DeviceSize = SizeFor(0, scale: DrawnScale);
        scaleX = DeviceSize.Width / skin.Size.Width;
        scaleY = DeviceSize.Height / skin.Size.Height;
        ApplyTransform();
        RebuildVisuals();
    }

    /// Draws another skin at the same scale, e.g. a foldable's outer display once it is closed; the window is
    /// resized separately.
    public void SetSkin(DeviceSkin skin)
    {
        this.skin = skin;
        skinCornerInsets = skin.CornerInsets;
        StopTurnAnimation();
        DeviceSize = SizeFor(0, scale: DrawnScale);
        scaleX = DeviceSize.Width / skin.Size.Width;
        scaleY = DeviceSize.Height / skin.Size.Height;
        Highlight(null);
        // Another skin's hinge is not this one's.
        FoldAngle = 0;
        flapVisual.StopAnimation("RotationAngleInDegrees");
        ShowsFold(false);
        ApplyTransform();
        RebuildVisuals();
        // The last frame is of the other display; dark until the first of this one's.
        ClearScreen();
    }

    /// How long the half left of the hinge takes to turn between open and half open.
    public static readonly TimeSpan FoldDuration = TimeSpan.FromSeconds(0.45);

    /// Where the flap is turned to now, while it animates too.
    private (double From, double To, double Start)? foldAnimation;

    private double CurrentFoldAngle
    {
        get
        {
            if (foldAnimation is not { } turn) return flapStage.IsVisible ? FoldAngle : 0;
            var progress = Math.Clamp((DeviceWindow.Now - turn.Start) / FoldDuration.TotalSeconds, 0, 1);
            return turn.From + (turn.To - turn.From) * DeviceWindow.EaseInOut(progress);
        }
    }

    /// Turns the half left of `hinge` (in skin pixels from the left) by `angle` degrees toward the viewer, as a
    /// foldable half open; 0 lays it flat again.
    public void SetFold(double angle, double hinge, bool animated)
    {
        var from = CurrentFoldAngle;
        FoldAngle = angle;
        this.hinge = hinge;
        flapVisual.StopAnimation("RotationAngleInDegrees");
        foldAnimation = null;
        LayoutFold();
        if (!animated || from == angle)
        {
            if (angle == 0) ShowsFold(false);
            return;
        }
        // Composition turns a positive angle about the vertical axis away from the viewer.
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));
        var turning = compositor.CreateScalarKeyFrameAnimation();
        turning.InsertKeyFrame(0, (float)-from);
        turning.InsertKeyFrame(1, (float)-angle, easing);
        turning.Duration = FoldDuration;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        flapVisual.StartAnimation("RotationAngleInDegrees", turning);
        batch.End();
        foldAnimation = (from, angle, DeviceWindow.Now);
        // Laid flat, the body shows the whole device again once the half has come down.
        batch.Completed += (_, _) =>
        {
            foldAnimation = null;
            if (FoldAngle == 0) ShowsFold(false);
        };
    }

    /// The hinge's distance from the device's left edge, in DIPs.
    private double HingeX => Math.Round(hinge * scaleX);

    /// The distance the turned half is seen from, as in the fold animation.
    private double Perspective => 2.2 * Math.Max(DeviceSize.Width, DeviceSize.Height);

    /// Splits the device at the hinge, the half left of it turned by `FoldAngle`.
    private void LayoutFold()
    {
        float width = (float)DeviceSize.Width, height = (float)DeviceSize.Height, x = (float)HingeX;
        if ((FoldAngle == 0 && foldAnimation == null) || x <= 0 || x >= width)
        {
            ShowsFold(false);
            return;
        }
        ShowsFold(true);
        bodyVisual.Clip = compositor.CreateInsetClip(x, 0, 0, 0);
        flapVisual.Size = new Vector2(width, height);
        flapVisual.Clip = compositor.CreateInsetClip(0, 0, width - x, 0);
        flapVisual.CenterPoint = new Vector3(x, height / 2, 0);
        if (foldAnimation == null) flapVisual.RotationAngleInDegrees = (float)-FoldAngle;
        // The depth is seen from in front of the right half, as in the fold animation.
        var center = new Vector3((x + width) / 2, height / 2, 0);
        flapStage.Size = flapVisual.Size;
        flapStage.TransformMatrix = Matrix4x4.CreateTranslation(-center)
                                    * new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, -1f / (float)Perspective, 0, 0, 0, 1)
                                    * Matrix4x4.CreateTranslation(center);
        flapScreen.Offset = screenVisual.Offset;
        flapScreen.Size = screenVisual.Size;
        flapFrame.Size = frameVisual.Size;
    }

    private void ShowsFold(bool shown)
    {
        if (shown == flapStage.IsVisible) return;
        flapStage.IsVisible = shown;
        if (!shown) bodyVisual.Clip = null;
    }

    /// Display rect in upright device DIPs (top-left origin).
    private Rect DisplayRect => new(skin.Display.X * scaleX, skin.Display.Y * scaleY, skin.Display.Width * scaleX, skin.Display.Height * scaleY);

    /// Where the screen sits: `DisplayRect` grown to whole device pixels. If the screen only half-covered the
    /// pixel under the frame's antialiased hole edge, the two partial coverages would leave a faint see-through
    /// line; grown, the screen is opaque there and the frame edge blends against it.
    private Rect ScreenRect
    {
        get
        {
            var backing = RasterScale;
            var rect = DisplayRect;
            double left = Math.Floor(rect.X * backing) / backing, top = Math.Floor(rect.Y * backing) / backing;
            double right = Math.Ceiling(rect.Right * backing) / backing, bottom = Math.Ceiling(rect.Bottom * backing) / backing;
            return new Rect(left, top, right - left, bottom - top);
        }
    }

    /// Pixel size of the upright display on screen.
    public Size DisplayPixelSize
    {
        get
        {
            var screen = ScreenRect;
            return new Size(Math.Round(screen.Width * RasterScale), Math.Round(screen.Height * RasterScale));
        }
    }

    /// While set, frames are not shown: the view keeps its last one, e.g. until a fold animation takes over.
    public bool FreezesFrames { get; set; }

    /// Draws a frame (RGBA, upright for the viewer at `rotation` quarter turns) into the screen.
    public void Show(byte[] pixels, int width, int height, int rotation)
    {
        if (FreezesFrames || screenSurface == null) return;
        try
        {
            if (frameBitmap == null || frameBitmap.SizeInPixels.Width != width || frameBitmap.SizeInPixels.Height != height)
            {
                frameBitmap?.Dispose();
                frameBitmap = CanvasBitmap.CreateFromBytes(DeviceSkin.Device, pixels, width, height,
                                                           DirectXPixelFormat.R8G8B8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
            }
            else
            {
                frameBitmap.SetPixelBytes(pixels);
            }
            contentTurns = rotation;
            DrawScreen();
        }
        catch (Exception error) when (DeviceSkin.Device.IsDeviceLost(error.HResult))
        {
            DeviceSkin.Device.RaiseDeviceLost();
        }
    }

    private void DrawScreen()
    {
        if (screenSurface == null) return;
        var size = screenSurface.SizeInt32;
        using var ds = CanvasComposition.CreateDrawingSession(screenSurface);
        ds.Clear(Colors.Black);
        if (frameBitmap == null) return;
        // Frames are upright for the viewer, so inside the turned device they are turned back.
        var turns = (contentTurns % 4 + 4) % 4;
        double w = size.Width, h = size.Height;
        var drawn = turns % 2 == 0 ? new Size(w, h) : new Size(h, w);
        ds.Transform = Matrix3x2.CreateTranslation((float)(-drawn.Width / 2), (float)(-drawn.Height / 2))
                       * Matrix3x2.CreateRotation((float)(turns * Math.PI / 2))
                       * Matrix3x2.CreateTranslation((float)(w / 2), (float)(h / 2));
        ds.DrawImage(frameBitmap, new Rect(0, 0, drawn.Width, drawn.Height), DeviceSkin.Bounds(frameBitmap), 1, CanvasImageInterpolation.Linear);
    }

    private void ClearScreen()
    {
        frameBitmap?.Dispose();
        frameBitmap = null;
        DrawScreen();
    }

    /// How long a turn takes; the window resizes along with it.
    public static readonly TimeSpan TurnDuration = TimeSpan.FromSeconds(0.3);

    /// Turns the device to `turns` counterclockwise quarter turns; the window is resized separately.
    public void SetQuarterTurns(int turns, bool animated) => SetQuarterTurns(turns, DrawnScale, animated);

    /// Turns the device to `turns` and draws it at `newScale` (it may fit the screen at another size turned),
    /// animating both from how it looks now, so that the turn is never cut short by the new size.
    public void SetQuarterTurns(int turns, double newScale, bool animated)
    {
        if (turns == QuarterTurns && newScale == DrawnScale) return;
        var fromAngle = CurrentAngle;
        var fromStretch = 1.0;
        if (newScale != DrawnScale)
        {
            // Drawn anew at the new size: the old look is the new visual stretched back by the ratio.
            fromStretch = DrawnScale / newScale;
            LiveScale = newScale;
            CommitScale();
        }
        QuarterTurns = turns;
        StopTurnAnimation();
        ApplyTransform();
        if (animated)
        {
            var target = TargetAngle;
            // The shorter way round, as Core Animation turns between transforms.
            while (fromAngle - target > 180) fromAngle -= 360;
            while (target - fromAngle > 180) fromAngle += 360;
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));
            var rotation = compositor.CreateScalarKeyFrameAnimation();
            rotation.InsertKeyFrame(0, fromAngle);
            rotation.InsertKeyFrame(1, target, easing);
            rotation.Duration = TurnDuration;
            deviceVisual.StartAnimation("RotationAngleInDegrees", rotation);
            if (fromStretch != 1)
            {
                var stretch = compositor.CreateVector3KeyFrameAnimation();
                stretch.InsertKeyFrame(0, new Vector3((float)fromStretch, (float)fromStretch, 1));
                stretch.InsertKeyFrame(1, Vector3.One, easing);
                stretch.Duration = TurnDuration;
                deviceVisual.StartAnimation("Scale", stretch);
            }
            turnAnimation = (fromAngle, target, Environment.TickCount64 / 1000.0);
        }
        UpdateShapes();
    }

    /// Composition turns clockwise for positive angles, the device counterclockwise for positive turns.
    private float TargetAngle => -90f * QuarterTurns;

    private float CurrentAngle
    {
        get
        {
            if (turnAnimation is not { } turn) return shownAngle;
            var progress = Math.Clamp((Environment.TickCount64 / 1000.0 - turn.Start) / TurnDuration.TotalSeconds, 0, 1);
            return (float)(turn.From + (turn.To - turn.From) * DeviceWindow.EaseInOut(progress));
        }
    }

    private void StopTurnAnimation()
    {
        if (turnAnimation == null) return;
        deviceVisual.StopAnimation("RotationAngleInDegrees");
        deviceVisual.StopAnimation("Scale");
        turnAnimation = null;
    }

    /// The device's turn, and while resizing the stretch from the drawn size to the shown one.
    private void ApplyTransform()
    {
        var stretch = (float)(LiveScale / DrawnScale);
        shownAngle = TargetAngle;
        deviceVisual.Size = new Vector2((float)DeviceSize.Width, (float)DeviceSize.Height);
        deviceVisual.CenterPoint = new Vector3((float)DeviceSize.Width / 2, (float)DeviceSize.Height / 2, 0);
        deviceVisual.RotationAngleInDegrees = shownAngle;
        deviceVisual.Scale = new Vector3(stretch, stretch, 1);
        // The shadow falls downwards on screen whichever way the device is held.
        var down = Vector2.Transform(new Vector2(0, 3), Matrix3x2.CreateRotation((float)(QuarterTurns * Math.PI / 2)));
        shadow.Offset = new Vector3(down, 0);
        PlaceDeviceVisual();
    }

    /// Where the device is shown, in this view's coordinates (the window's content): its rectangle as turned.
    private Rect placement;

    public void Place(Rect frame)
    {
        placement = frame;
        PlaceDeviceVisual();
    }

    /// The upright device's top left, so that turned about its centre it fills `placement`.
    private void PlaceDeviceVisual() =>
        deviceVisual.Offset = new Vector3((float)(placement.X + placement.Width / 2 - DeviceSize.Width / 2),
                                          (float)(placement.Y + placement.Height / 2 - DeviceSize.Height / 2), 0);

    /// Briefly whitens the screen, as a screenshot confirmation.
    public void Flash()
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0.85f);
        animation.InsertKeyFrame(1, 0);
        animation.Duration = TimeSpan.FromSeconds(0.3);
        flashVisual.StartAnimation("Opacity", animation);
    }

    /// Sizes the visuals for the upright device and draws the frame, the clip and the screen at the pixel size
    /// they are shown at.
    private void RebuildVisuals()
    {
        if (XamlRoot == null) return;
        var backing = RasterScale;
        drawnScale = backing;
        var screen = ScreenRect;
        frameVisual.Size = new Vector2((float)DeviceSize.Width, (float)DeviceSize.Height);
        screenVisual.Offset = new Vector3((float)screen.X, (float)screen.Y, 0);
        screenVisual.Size = new Vector2((float)screen.Width, (float)screen.Height);

        var framePixels = new Windows.Graphics.SizeInt32 { Width = (int)Math.Round(DeviceSize.Width * backing), Height = (int)Math.Round(DeviceSize.Height * backing) };
        frameSurface = Resize(frameSurface, framePixels);
        // Drawn once with high-quality filtering instead of letting the compositor minify the artwork ~3×.
        using (var ds = CanvasComposition.CreateDrawingSession(frameSurface))
        {
            ds.Clear(Colors.Transparent);
            ds.DrawImage(skin.Frame, new Rect(0, 0, framePixels.Width, framePixels.Height), DeviceSkin.Bounds(skin.Frame), 1, CanvasImageInterpolation.HighQualityCubic);
        }
        frameBrush.Surface = frameSurface;

        // At a quarter of the size: the shadow's blur hides the detail.
        var silhouettePixels = new Windows.Graphics.SizeInt32 { Width = framePixels.Width / 4, Height = framePixels.Height / 4 };
        silhouetteSurface = Resize(silhouetteSurface, silhouettePixels);
        using (var ds = CanvasComposition.CreateDrawingSession(silhouetteSurface))
        {
            ds.Clear(Colors.Transparent);
            double sx = silhouettePixels.Width / skin.Size.Width, sy = silhouettePixels.Height / skin.Size.Height;
            ds.DrawImage(skin.ScreenClip, new Rect(skin.Display.X * sx, skin.Display.Y * sy, skin.Display.Width * sx, skin.Display.Height * sy),
                         DeviceSkin.Bounds(skin.ScreenClip), 1, CanvasImageInterpolation.Linear);
            ds.DrawImage(skin.Frame, new Rect(0, 0, silhouettePixels.Width, silhouettePixels.Height), DeviceSkin.Bounds(skin.Frame), 1, CanvasImageInterpolation.Linear);
        }
        silhouetteBrush.Surface = silhouetteSurface;
        silhouetteVisual.Size = frameVisual.Size;
        bodyVisual.Size = frameVisual.Size;

        var screenPixels = new Windows.Graphics.SizeInt32 { Width = (int)Math.Round(screen.Width * backing), Height = (int)Math.Round(screen.Height * backing) };
        clipSurface = Resize(clipSurface, screenPixels);
        using (var ds = CanvasComposition.CreateDrawingSession(clipSurface))
        {
            ds.Clear(Colors.Transparent);
            ds.DrawImage(skin.ScreenClip, new Rect(0, 0, screenPixels.Width, screenPixels.Height), DeviceSkin.Bounds(skin.ScreenClip), 1, CanvasImageInterpolation.HighQualityCubic);
        }
        clipBrush.Surface = clipSurface;
        screenSurface = Resize(screenSurface, screenPixels);
        contentBrush.Surface = screenSurface;
        DrawScreen();
        LayoutFold();
        UpdateShapes();
    }

    private CompositionDrawingSurface Resize(CompositionDrawingSurface? surface, Windows.Graphics.SizeInt32 size)
    {
        size.Width = Math.Max(1, size.Width);
        size.Height = Math.Max(1, size.Height);
        if (surface == null)
            return graphics.CreateDrawingSurface2(size, Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
        if (surface.SizeInt32.Width != size.Width || surface.SizeInt32.Height != size.Height) surface.Resize(size);
        return surface;
    }

    // Side buttons

    /// A side button's hit area in upright device DIPs.
    private Rect LocalRect(SideButton button) =>
        new(button.Rect.X * scaleX, button.Rect.Y * scaleY, button.Rect.Width * scaleX, button.Rect.Height * scaleY);

    private SideButton? SideButtonAt(Point local) => skin.SideButtons.FirstOrDefault(b => LocalRect(b).Contains(local));

    private void Highlight(SideButton? button)
    {
        if (button != null)
        {
            var rect = LocalRect(button);
            highlightVisual.Offset = new Vector3((float)rect.X, (float)rect.Y, 0);
            highlightVisual.Size = new Vector2((float)rect.Width, (float)rect.Height);
            var clip = compositor.CreateRectangleClip(0, 0, (float)rect.Width, (float)rect.Height);
            clip.TopLeftRadius = clip.TopRightRadius = clip.BottomLeftRadius = clip.BottomRightRadius = new Vector2(3);
            highlightVisual.Clip = clip;
        }
        highlightVisual.IsVisible = button != null;
        if (button == hoveredButton) return;
        hoveredButton = button;
        // A tooltip names the button under the pointer, as the macOS window's tooltip rectangles do.
        if (button == null)
        {
            if (buttonTip != null) buttonTip.IsOpen = false;
            ToolTipService.SetToolTip(this, null);
            buttonTip = null;
        }
        else
        {
            buttonTip = new ToolTip { Content = button.Label };
            ToolTipService.SetToolTip(this, buttonTip);
        }
    }

    private void UpdateShapes() => Highlight(pressedButton ?? hoveredButton);

    // Input

    /// A point in this view in upright device DIPs.
    private Point LocalPoint(Point point)
    {
        var center = new Vector2((float)(placement.X + placement.Width / 2), (float)(placement.Y + placement.Height / 2));
        var stretch = LiveScale / DrawnScale;
        var turned = Vector2.Transform(new Vector2((float)point.X, (float)point.Y) - center,
                                       Matrix3x2.CreateRotation((float)(QuarterTurns * Math.PI / 2)));
        var local = new Point(turned.X / stretch + DeviceSize.Width / 2, turned.Y / stretch + DeviceSize.Height / 2);
        return flapStage.IsVisible && local.X < HingeX ? OnFlap(local) : local;
    }

    /// Where on the half left of the hinge, turned toward the viewer and seen in perspective, a point of the
    /// upright device lies: the projection undone.
    private Point OnFlap(Point seen)
    {
        double angle = CurrentFoldAngle * Math.PI / 180, d = Perspective, x = HingeX;
        double cx = (x + DeviceSize.Width) / 2, cy = DeviceSize.Height / 2;
        // A point `dx` left of the hinge (negative) comes `-dx sin` toward the viewer and lands `dx cos` from it,
        // then is projected from `d` in front of the centre.
        var dx = d * (x - seen.X) / ((seen.X - cx) * Math.Sin(angle) - d * Math.Cos(angle));
        var depth = d / (d + dx * Math.Sin(angle));
        return new Point(x + dx, cy + (seen.Y - cy) / depth);
    }

    /// Device pixel under the pointer, clamped to the display so a drag that leaves the screen still ends cleanly.
    /// Touch coordinates are the panel's own, so they do not change when the device is turned.
    private Point DevicePoint(Point local)
    {
        var display = DisplayRect;
        var x = (Math.Clamp(local.X, display.X, display.Right) - display.X) / scaleX;
        var y = (Math.Clamp(local.Y, display.Y, display.Bottom) - display.Y) / scaleY;
        return new Point(Math.Min(Math.Floor(x), skin.Display.Width - 1), Math.Min(Math.Floor(y), skin.Display.Height - 1));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        Focus(FocusState.Pointer);
        if (!point.Properties.IsLeftButtonPressed) return;
        var local = LocalPoint(point.Position);
        if (DisplayRect.Contains(local))
        {
            ScreenPressed?.Invoke();
            isTouching = true;
            touchPointer = e.Pointer.PointerId;
            CapturePointer(e.Pointer);
            Touch?.Invoke(DevicePoint(local), true);
        }
        else if (SideButtonAt(local) is { } button)
        {
            pressedButton = button;
            CapturePointer(e.Pointer);
            Highlight(button);
            SideButtonKey?.Invoke(button.Key, true);
        }
        else if (local.X >= 0 && local.Y >= 0 && local.X < DeviceSize.Width && local.Y < DeviceSize.Height)
        {
            DragRequested?.Invoke();
        }
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var local = LocalPoint(e.GetCurrentPoint(this).Position);
        if (isTouching && e.Pointer.PointerId == touchPointer)
        {
            Touch?.Invoke(DevicePoint(local), true);
        }
        else if (pressedButton == null)
        {
            Highlight(SideButtonAt(local));
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var local = LocalPoint(e.GetCurrentPoint(this).Position);
        EndPress(local);
        ReleasePointerCapture(e.Pointer);
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (isTouching || pressedButton != null) EndPress(LocalPoint(e.GetCurrentPoint(this).Position));
    }

    private void EndPress(Point local)
    {
        if (pressedButton is { } button)
        {
            pressedButton = null;
            SideButtonKey?.Invoke(button.Key, false);
            Highlight(SideButtonAt(local));
        }
        if (!isTouching) return;
        isTouching = false;
        Touch?.Invoke(DevicePoint(local), false);
    }

}
