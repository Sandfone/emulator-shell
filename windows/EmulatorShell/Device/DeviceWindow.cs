using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace EmulatorShell;

/// The device's borderless, see-through window.
///
/// What the user sees and sizes is the box: the device with its bars, in screen pixels. The window normally
/// reaches `Margin` DIPs further on every side, where the corner resize areas extend past the box as a standard
/// window's do. While the box is dragged larger or animates (turning, folding), the window instead covers all the
/// box can reach (`Hold`), invisible where it shows nothing, so that only the content moves: resizing a window
/// on every frame lags behind its content.
///
/// Clicks on the window's empty parts go to whatever is behind it, as on macOS: the window's region is kept to
/// what it shows (`SetClickRegion`).
sealed partial class DeviceWindow : Window
{
    /// Room around the box for the corner resize areas and the device's shadow, which the window's region would
    /// otherwise cut off square.
    public const double Margin = 16;

    /// The bars and resize handles, above the device; laid out in DIPs from the window's top left.
    public Canvas Root { get; } = new();
    /// Above everything, for the fold animation's visuals.
    public Canvas Overlay { get; } = new() { IsHitTestVisible = false };
    private readonly Grid layers = new();
    public nint Hwnd { get; }

    /// The box, in screen pixels.
    public PixelRect Box { get; private set; }
    /// The window's own rectangle, in screen pixels.
    private PixelRect frame;
    /// Areas the window covers besides the box, while something moves or grows there.
    private readonly List<PixelRect> holds = [];
    private readonly Native.SubclassProc subclass;

    /// The box or the window moved or changed size: lay the content out again.
    public event Action? LayoutNeeded;
    /// Called once a drag that moved the window ends.
    public event Action? Moved;
    /// Called with `true` once a drag first brings the box's top against the top of the screen, and with
    /// `false` when that drag ends.
    public event Action<bool>? ScreenTopReached;
    /// The window moved to a monitor of another scale.
    public event Action? ScaleChanged;
    /// The system or the user asked the window to close (Alt+F4, the taskbar).
    public event Action? CloseRequested;

    public DeviceWindow(string title)
    {
        Title = title;
        Root.Background = null;
        layers.Children.Add(Root);
        layers.Children.Add(Overlay);
        Content = layers;
        SystemBackdrop = new TransparentBackdrop();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }
        // Without a title bar the window still has a thin dialog frame, a line down each side.
        var style = Native.GetWindowLongPtr(Hwnd, Native.GWL_STYLE);
        Native.SetWindowLongPtr(Hwnd, Native.GWL_STYLE, (nint)(style & ~(Native.WS_BORDER | Native.WS_DLGFRAME | Native.WS_THICKFRAME)));
        Native.SetWindowPos(Hwnd, 0, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            CloseRequested?.Invoke();
        };
        // No rounded corners, border or shadow from the window manager: the window is only what it draws.
        var round = Native.DWMWCP_DONOTROUND;
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var border = Native.DWMWA_COLOR_NONE;
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(uint));
        // The window manager composes the whole window as "frame", which shows what is behind it wherever the
        // content is transparent; otherwise those parts are black.
        Native.DwmExtendFrameIntoClientArea(Hwnd, new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 });
        var noTransitions = 1;
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_TRANSITIONS_FORCEDISABLED, ref noTransitions, sizeof(int));

        subclass = WindowProc;
        Native.SetWindowSubclass(Hwnd, subclass, 1, 0);
        Closed += (_, _) => Native.RemoveWindowSubclass(Hwnd, subclass, 1);
    }

    /// DIPs to pixels on the monitor the window is on.
    public double Scale => Native.GetDpiForWindow(Hwnd) is var dpi and > 0 ? dpi / 96.0 : 1;

    public Screen Screen => Screen.ForRect(Box);

    /// The box in the window's content, in DIPs.
    public Rect BoxInContent
    {
        get
        {
            var scale = Scale;
            return new Rect((Box.X - frame.X) / scale, (Box.Y - frame.Y) / scale, Box.Width / scale, Box.Height / scale);
        }
    }

    /// Converts a rectangle in the content (DIPs) to screen pixels.
    public PixelRect ToScreen(Rect rect)
    {
        var scale = Scale;
        return new PixelRect(frame.X + rect.X * scale, frame.Y + rect.Y * scale, rect.Width * scale, rect.Height * scale);
    }

    /// Converts a screen point to the content's DIPs.
    public Point FromScreen(double x, double y) => new((x - frame.X) / Scale, (y - frame.Y) / Scale);

    /// Puts the device's layer (the `DeviceView`) under the bars.
    public void SetDeviceLayer(UIElement element) => layers.Children.Insert(0, element);

    public void SetBox(PixelRect box)
    {
        StopBoxAnimation();
        Box = box.Rounded();
        UpdateFrame();
    }

    /// Covers `area` (screen pixels) as well as the box until the result is disposed; the box may meanwhile
    /// move and change size within it without the window changing.
    public IDisposable Hold(PixelRect area)
    {
        holds.Add(area);
        UpdateFrame();
        return new Release(() =>
        {
            holds.Remove(area);
            UpdateFrame();
        });
    }

    private sealed class Release(Action action) : IDisposable
    {
        private Action? action = action;

        public void Dispose()
        {
            action?.Invoke();
            action = null;
        }
    }

    private void UpdateFrame()
    {
        var wanted = Box.Inflate(Margin * Scale, Margin * Scale);
        foreach (var area in holds) wanted = wanted.Union(area);
        wanted = wanted.Rounded();
        if (wanted == frame)
        {
            LayoutNeeded?.Invoke();
            return;
        }
        frame = wanted;
        Native.SetWindowPos(Hwnd, 0, (int)wanted.X, (int)wanted.Y, (int)wanted.Width, (int)wanted.Height,
                            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        LayoutNeeded?.Invoke();
    }

    /// Keeps clicks to the given rectangles of the content (DIPs); elsewhere they reach the windows behind. The
    /// region also clips what the window shows, so while it holds a larger area for something moving there (a
    /// turn, a fold, a drag) it has none.
    public void SetClickRegion(IEnumerable<Rect> rects)
    {
        if (holds.Count > 0)
        {
            Native.SetWindowRgn(Hwnd, 0, true);
            return;
        }
        var scale = Scale;
        var region = Native.CreateRectRgn(0, 0, 0, 0);
        foreach (var rect in rects)
        {
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var part = Native.CreateRectRgn((int)Math.Floor(rect.X * scale), (int)Math.Floor(rect.Y * scale),
                                            (int)Math.Ceiling(rect.Right * scale), (int)Math.Ceiling(rect.Bottom * scale));
            Native.CombineRgn(region, region, part, Native.RGN_OR);
            Native.DeleteObject(part);
        }
        // The window owns the region from here on.
        Native.SetWindowRgn(Hwnd, region, true);
    }

    public void ShowAndActivate()
    {
        if (Native.IsIconic(Hwnd)) Native.ShowWindow(Hwnd, Native.SW_RESTORE);
        Activate();
        Native.SetForegroundWindow(Hwnd);
    }

    public void Hide() => AppWindow.Hide();

    public void Minimize()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
    }

    public bool IsShown => Native.IsWindowVisible(Hwnd) && !Native.IsIconic(Hwnd);

    // Animated boxes

    private (PixelRect From, PixelRect To, double Start, double Duration, Action<PixelRect>? Step, Action? Completion)? boxAnimation;
    private IDisposable? animationHold;
    private int boxAnimationId;

    /// Seconds on a steady clock, which animations started together share.
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// Moves and resizes the box to `box` over `duration` from `start` (a `Now`), eased like the device's own
    /// composition animations, which it goes along with; `step` runs with each box before the content is laid out
    /// for it, so whatever follows the box can fit it exactly, and `completion` at the end.
    ///
    /// Stepped on each frame the compositor draws, at the point in time the device's animation has reached, so
    /// the two stay together.
    public void SetBox(PixelRect box, TimeSpan duration, double? start = null, Action<PixelRect>? step = null, Action? completion = null)
    {
        StopBoxAnimation();
        box = box.Rounded();
        if (box == Box)
        {
            step?.Invoke(box);
            completion?.Invoke();
            return;
        }
        animationHold = Hold(box.Union(Box).Inflate(Margin * Scale, Margin * Scale));
        boxAnimation = (Box, box, start ?? Now, duration.TotalSeconds, step, completion);
        CompositionTarget.Rendering += StepBoxAnimation;
        // Frames stop while the screen is locked or the window is covered; the animation still ends, on time.
        var id = ++boxAnimationId;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = duration + TimeSpan.FromMilliseconds(100);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (boxAnimationId == id && boxAnimation != null) StepBoxAnimation(null, null);
        };
        timer.Start();
    }

    /// Ends a `SetBox(_:duration:)` animation where it is, without its completion.
    public void StopBoxAnimation()
    {
        if (boxAnimation == null) return;
        CompositionTarget.Rendering -= StepBoxAnimation;
        boxAnimation = null;
        animationHold?.Dispose();
        animationHold = null;
    }

    public bool IsAnimatingBox => boxAnimation != null;

    private void StepBoxAnimation(object? sender, object? e)
    {
        if (boxAnimation is not { } animation)
        {
            StopBoxAnimation();
            return;
        }
        var progress = Math.Clamp((Now - animation.Start) / animation.Duration, 0, 1);
        var eased = EaseInOut(progress);
        // Each edge on its own, rounded: an edge that does not move stays exactly where it is, where rounding the
        // origin and the size apart would nudge it a pixel back and forth.
        double Mix(double a, double b) => Math.Round(a + (b - a) * eased);
        PixelRect from = animation.From, to = animation.To;
        if (progress >= 1)
        {
            Box = to;
        }
        else
        {
            double left = Mix(from.Left, to.Left), top = Mix(from.Top, to.Top);
            Box = new PixelRect(left, top, Mix(from.Right, to.Right) - left, Mix(from.Bottom, to.Bottom) - top);
        }
        animation.Step?.Invoke(Box);
        LayoutNeeded?.Invoke();
        if (progress < 1) return;
        StopBoxAnimation();
        animation.Completion?.Invoke();
    }

    /// The cubic Bézier through (0.42, 0) and (0.58, 1), Core Animation's ease-in-ease-out.
    public static double EaseInOut(double time)
    {
        // Finds the curve's parameter whose x is `time` by bisection (x rises with it), then its y.
        double low = 0, high = 1, s = time;
        for (var i = 0; i < 20; i++)
        {
            s = (low + high) / 2;
            var x = 3 * (1 - s) * (1 - s) * s * 0.42 + 3 * (1 - s) * s * s * 0.58 + s * s * s;
            if (x < time) low = s; else high = s;
        }
        return 3 * s * s - 2 * s * s * s;
    }

    // Dragging

    private bool dragging;
    private bool reachedTop;

    private PixelRect dragFrom;
    private Windows.Graphics.PointInt32 dragAnchor;

    /// Moves the window with the pointer until the button comes up, then brings any part dragged off the screen
    /// back. Called from a press on the device's shell or on a bar.
    ///
    /// The window follows the pointer on every frame rather than through the system's move loop
    /// (WM_NCLBUTTONDOWN on the caption): started from a XAML press, that loop moved the window by amounts
    /// unrelated to the pointer's.
    public void Drag()
    {
        if (holds.Count > 0 || dragging || !Native.IsPrimaryButtonDown) return;
        dragging = true;
        reachedTop = false;
        dragFrom = Box;
        dragAnchor = Native.CursorPosition;
        CompositionTarget.Rendering += StepDrag;
    }

    private void StepDrag(object? sender, object? e)
    {
        var pointer = Native.CursorPosition;
        var box = dragFrom.Offset(pointer.X - dragAnchor.X, pointer.Y - dragAnchor.Y);
        if (box != Box)
        {
            Box = box;
            UpdateFrame();
            if (!reachedTop && AtScreenTop)
            {
                reachedTop = true;
                ScreenTopReached?.Invoke(true);
            }
        }
        if (Native.IsPrimaryButtonDown) return;

        CompositionTarget.Rendering -= StepDrag;
        dragging = false;
        if (reachedTop) ScreenTopReached?.Invoke(false);
        if (Box == dragFrom) return;
        var fitted = Fitted(Box, Screen);
        if (fitted != Box) SetBox(fitted, TimeSpan.FromSeconds(0.2));
        Moved?.Invoke();
    }

    /// Whether the box's top is at or above its screen's work area top, with no other screen above to move on to.
    private bool AtScreenTop
    {
        get
        {
            var screen = Screen;
            if (Box.Top > screen.WorkArea.Top + 1) return false;
            var above = (X: (int)Box.MidX, Y: (int)screen.Frame.Top - 1);
            return !Screen.All.Any(s => s.Handle != screen.Handle && s.Frame.Contains(above.X, above.Y));
        }
    }

    /// The box moved (not resized) so it lies within the screen's work area where it can.
    public static PixelRect Fitted(PixelRect box, Screen screen)
    {
        var visible = screen.WorkArea;
        var x = Math.Min(Math.Max(box.X, visible.Left), Math.Max(visible.Left, visible.Right - box.Width));
        var y = Math.Min(Math.Max(box.Y, visible.Top), Math.Max(visible.Top, visible.Bottom - box.Height));
        return box with { X = x, Y = y };
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, nint id, nint data)
    {
        switch (message)
        {
            case Native.WM_MOVING when holds.Count == 0:
            {
                var rect = Marshal.PtrToStructure<Native.RECT>(lParam);
                frame = PixelRect.From(rect);
                var margin = Margin * Scale;
                Box = frame.Inflate(-margin, -margin).Rounded();
                if (dragging && !reachedTop && AtScreenTop)
                {
                    reachedTop = true;
                    ScreenTopReached?.Invoke(true);
                }
                break;
            }
            case Native.WM_DPICHANGED:
            {
                var result = Native.DefSubclassProc(hwnd, message, wParam, lParam);
                if (Native.GetWindowRect(hwnd, out var rect)) frame = PixelRect.From(rect);
                DispatcherQueue.TryEnqueue(() => ScaleChanged?.Invoke());
                return result;
            }
        }
        return Native.DefSubclassProc(hwnd, message, wParam, lParam);
    }
}

/// A system backdrop that is fully transparent, so the window shows only what its content draws.
sealed partial class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? compositor;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        Native.EnsureSystemDispatcherQueue();
        compositor ??= new Windows.UI.Composition.Compositor();
        connectedTarget.SystemBackdrop = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
