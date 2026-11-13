using Windows.Foundation;

namespace EmulatorShell;

/// Lays out the device window's content: the device, with room above it for the title bar and below it for the
/// button pill, and for a foldable on either side for the posture rail (on the right; the left is the same
/// width, so the device stays centred). That box is everything the window shows, so its size is the window's
/// size as the user sees it; the room stays reserved while the title bar is hidden so the device does not jump
/// when it appears. Corner
/// resize areas sit on the device's corners, reaching `ResizeHandle.Outside` past them.
sealed class DeviceShell
{
    /// Space above the device (title bar and gap) and below it (gap and pill), in DIPs.
    public const double TopRoom = DeviceChrome.BarHeight + DeviceChrome.Gap;
    public const double BottomRoom = DeviceChrome.Gap + DeviceChrome.BarHeight;
    /// Space on either side of a foldable for the posture rail and its gap.
    public const double FoldableSideRoom = DeviceChrome.Gap + DeviceChrome.BarHeight;

    public DeviceView DeviceView { get; }
    private readonly DeviceWindow window;
    private readonly List<ResizeHandle> handles = [];
    private DeviceChrome? chrome;

    /// A corner drag began, the box's corner should now be at a screen point, or it ended.
    public event Action<ResizeHandle.Corner>? ResizeBegan;
    public event Action<Point>? ResizeChanged;
    public event Action? ResizeEnded;

    public DeviceShell(DeviceWindow window, DeviceView deviceView)
    {
        this.window = window;
        DeviceView = deviceView;
        window.SetDeviceLayer(deviceView);
        foreach (var corner in Enum.GetValues<ResizeHandle.Corner>())
        {
            var handle = new ResizeHandle(corner, () => window.Box);
            handle.Began += c => ResizeBegan?.Invoke(c);
            handle.Changed += p => ResizeChanged?.Invoke(p);
            handle.Ended += () => ResizeEnded?.Invoke();
            handles.Add(handle);
        }
        window.LayoutNeeded += Layout;
    }

    /// Set once the chrome has added its elements, so the handles can go above them.
    public DeviceChrome? Chrome
    {
        get => chrome;
        set
        {
            chrome = value;
            foreach (var handle in handles)
            {
                window.Root.Children.Remove(handle);
                window.Root.Children.Add(handle);
            }
            if (value != null) value.ShapeChanged += UpdateClickRegion;
            Layout();
        }
    }

    /// The box's size around a device of `device` DIPs: as wide as the device and `sideRoom` either side (a
    /// foldable's `FoldableSideRoom`, else 0), its bars fitting what room there is.
    public static Size BoxSize(Size device, double sideRoom) => new(device.Width + 2 * sideRoom, device.Height + TopRoom + BottomRoom);

    /// The device's frame in the window's content.
    public Rect DeviceFrame
    {
        get
        {
            var box = window.BoxInContent;
            var size = DeviceView.SizeFor(DeviceView.QuarterTurns);
            return new Rect(Math.Round(box.X + box.Width / 2 - size.Width / 2), Math.Round(box.Bottom - BottomRoom - size.Height), size.Width, size.Height);
        }
    }

    public void Layout()
    {
        var device = DeviceFrame;
        DeviceView.Place(device);
        chrome?.Layout(device, window.BoxInContent);
        foreach (var handle in handles) handle.Place(device, DeviceView.CornerInset(handle.ClockwiseIndex));
        UpdateClickRegion();
    }

    /// Clicks reach the window on the device and its corner areas and on the bars; elsewhere, in the room above
    /// the device while the title bar is hidden and beside the pill, they go to the windows behind it.
    private void UpdateClickRegion()
    {
        var device = DeviceFrame;
        var rects = new List<Rect> { new(device.X - ResizeHandle.Outside, device.Y - ResizeHandle.Outside,
                                         device.Width + 2 * ResizeHandle.Outside, device.Height + 2 * ResizeHandle.Outside) };
        if (chrome != null) rects.AddRange(chrome.Shapes);
        window.SetClickRegion(rects);
    }
}
