using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Android.Emulation.Control;
using Environment = System.Environment;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using PostureValue = Android.Emulation.Control.Posture.Types.PostureValue;

namespace EmulatorShell;

/// A device-window command, from the keyboard, the window's context menu or the chrome.
abstract record DeviceCommand
{
    public sealed record Key(string Name) : DeviceCommand;
    public sealed record Do(DeviceAction Action) : DeviceCommand;

    /// The context menu's device commands: title, command, shortcut; groups are separated.
    public static readonly (string Title, DeviceCommand Command, string Shortcut)[][] MenuGroups =
    [
        [("返回", new Key("GoBack"), "Ctrl+B"), ("主屏幕", new Key("GoHome"), "Ctrl+Shift+H"), ("最近任务", new Key("AppSwitch"), "Ctrl+Shift+O")],
        [("截图", new Do(DeviceAction.Screenshot), "Ctrl+S"), ("拷贝截图", new Do(DeviceAction.CopyScreenshot), "Ctrl+Shift+S"),
         ("开始或停止录屏", new Do(DeviceAction.Record), "Ctrl+Shift+R"),
         ("向左旋转", new Do(DeviceAction.RotateLeft), "Ctrl+←"),
         ("向右旋转", new Do(DeviceAction.RotateRight), "Ctrl+→"), ("折叠 / 展开", new Do(DeviceAction.ToggleFold), "Ctrl+Shift+F")],
        [("音量 −", new Key("AudioVolumeDown"), "Ctrl+Alt+↓"), ("音量 +", new Key("AudioVolumeUp"), "Ctrl+Alt+↑"), ("电源", new Key("Power"), "Ctrl+L")],
    ];
}

/// A window size the context menu and the keyboard offer.
enum DeviceZoom
{
    /// One device pixel per screen pixel.
    ActualPixels,
    /// The display as large as the real device's, judging by the AVD's density and the monitor's size.
    Physical,
    /// As large as fits the screen.
    Fit,
    Larger,
    Smaller,
}

enum SessionState { Booting, Running, Stopping, Stopped }

/// A running device's latest frame as the device manager's preview shows it: RGBA pixels upright for the viewer,
/// the counterclockwise quarter turns the device is held at, and the skin shown instead of the AVD's own (a
/// foldable's closed one).
sealed record DeviceThumbnail(byte[] Pixels, int Width, int Height, int QuarterTurns, string? SkinFolder);

/// One running device: the emulator process, its gRPC connection and the window that shows it.
sealed partial class DeviceSession
{
    public Avd Avd { get; private set; }
    public int GrpcPort { get; }
    private SessionState state = SessionState.Booting;
    public SessionState State
    {
        get => state;
        private set
        {
            state = value;
            StateChanged?.Invoke(value);
        }
    }
    public event Action<SessionState>? StateChanged;
    /// Ctrl+Shift+2 or the menu, which open the device manager, or the menu's forwarder and command-line items.
    public event Action<ManagerTask>? ManagerRequested;
    /// Ctrl+Q in a device window, which quits the app.
    public event Action? QuitRequested;
    public DeviceWindow? Window { get; private set; }

    private EmulatorProcess? emulator;
    /// Set for a device another program started, which this window only shows: closing it leaves the emulator
    /// running, and only stopping it from the manager shuts it down.
    public RunningEmulator? Attached { get; private set; }
    /// Tells an attached session that its emulator has exited.
    private System.Diagnostics.Process? exitWatch;
    private EmulatorClient? client;
    private DeviceView? view;
    private DeviceShell? shell;
    private DeviceChrome? chrome;
    /// The skin shown: a foldable's `closedSkin` while it is closed, otherwise `openSkin`.
    private DeviceSkin skin;
    private readonly DeviceSkin openSkin;
    private readonly DeviceSkin? closedSkin;
    /// Whether a foldable with `closedSkin` is closed, which shows that skin.
    private bool isClosed;
    /// Whether an open foldable is half open, which turns the half left of its hinge toward the viewer by
    /// `HalfOpenAngle` degrees, about as Xcode's iPhone Duo shows its "Partially Open".
    private bool halfOpen;
    private const double HalfOpenAngle = 30;
    private double OpenAngle => halfOpen ? HalfOpenAngle : 0;
    /// For a foldable, until its first frame: the turn it is held at, which the emulator's own (on its side
    /// while open) may differ from. Then, until frames report that turn or `holdingSince` is a few seconds
    /// old, other turns they report are not followed.
    private int? heldTurns;
    private DateTime? holdingSince;
    /// A fold or unfold playing, with the live view hidden.
    private FoldAnimation? foldAnimation;
    /// After folding: where the closed device was put, and where the open one was, so that unfolding a device
    /// that has not been moved meanwhile puts it back exactly, without rounding creeping in.
    private (Point Closed, Point Open)? unfoldsTo;
    /// From a posture change until its animation has ended (it starts once its pictures are drawn).
    private bool folding;
    /// Tells a fold whose pictures are being drawn whether another has come since.
    private int foldId;
    private bool liveTargetDrawing;
    /// Ends a fold animation that finished before the display entered lit up with a live frame.
    private Action? foldEndWaiting;
    /// While folding: when the posture changed, and when Android turned the display entered on, with what it
    /// showed then (see `LiveFrameSettled`).
    private DateTime? foldStartedAt;
    private (DateTime At, byte[] Signature)? screenOn;
    /// Watches the guest's log for the display entered turning on.
    private GuestLogWatch? screenOnWatch;
    /// Pixels per DIP of the pictures in the animation playing.
    private double pictureScale = 2;
    /// DIPs per skin pixel the user chose. The device is shown smaller only while it does not fit the
    /// screen, e.g. turned on its side; it returns to this size when it fits again.
    private double preferredScale = 1;
    /// During a corner drag: the dragged corner, where it started, the opposite corner (which stays put), the
    /// scale the drag started at, and the area the window covers meanwhile.
    private (ResizeHandle.Corner Corner, Point Start, Point Anchor, double Scale, IDisposable Hold)? resizing;
    /// Side of the square box the screen stream was last requested at, in pixels.
    private int streamSide;
    private ClipboardSync? clipboard;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? streamCancellation, postureCancellation;
    /// Device input, sent strictly in order: separate tasks may run out of order and swap a key's down and up.
    private readonly Channel<Func<EmulatorClient, Task>> input = Channel.CreateUnbounded<Func<EmulatorClient, Task>>();
    /// Counterclockwise quarter turns the emulator reports the device is held at.
    private int deviceTurns;
    private readonly DispatcherQueue queue;
    private IDisposable? turnHold;

    // The latest frame: written on the streaming thread, shown on this one, kept for thumbnails and folds.
    private readonly object frameLock = new();
    private byte[] pendingPixels = [];
    private (int Width, int Height, int Rotation, bool Folded, bool Blank) pendingFrame;
    private bool frameScheduled;
    private byte[]? lastPixels;
    private (int Width, int Height) lastSize;
    /// The quarter turns the last frame was reported at.
    private int lastRotation;

    private DeviceSession(Avd avd, int grpcPort, DeviceSkin openSkin, DeviceSkin? closedSkin)
    {
        Avd = avd;
        GrpcPort = grpcPort;
        this.openSkin = openSkin;
        skin = openSkin;
        this.closedSkin = closedSkin;
        queue = DispatcherQueue.GetForCurrentThread();
    }

    /// `cascade` offsets the window so several devices do not open on top of each other.
    /// `extraArguments` go to the emulator as they are, e.g. the ones Android Studio or Flutter passed.
    public static async Task<DeviceSession> Start(Avd avd, int grpcPort, bool coldBoot, IReadOnlyList<string> extraArguments, int cascade)
    {
        var openSkin = await DeviceSkin.Load(avd);
        var session = new DeviceSession(avd, grpcPort, openSkin, await LoadClosedSkin(avd, openSkin));
        session.ShowWindow(cascade);
        session.StartEmulator(coldBoot, extraArguments);
        return session;
    }

    /// Shows a device another program (Android Studio, the command line, another Emulator Shell) started, over
    /// the gRPC port it announced. Its sound stays with that program.
    public static async Task<DeviceSession> Attach(Avd avd, RunningEmulator running, int cascade)
    {
        if (running.GrpcPort is not { } port) throw new ShellException("模拟器没有开启 gRPC 端口");
        var openSkin = await DeviceSkin.Load(avd);
        var session = new DeviceSession(avd, port, openSkin, await LoadClosedSkin(avd, openSkin)) { Attached = running };
        session.ShowWindow(cascade);
        session.AttachEmulator(running);
        return session;
    }

    /// The outer display's pixels are denser than the inner one's, so its skin draws the same device taller;
    /// scaled to the open skin's, the closed device is the open one's right half. Cut down to the device first,
    /// so that it fits the screen wherever the open one does.
    private static async Task<DeviceSkin?> LoadClosedSkin(Avd avd, DeviceSkin openSkin)
    {
        if (avd.ClosedSkinFolder is not { } folder) return null;
        try
        {
            var closed = (await DeviceSkin.Load(folder)).CroppedToBody();
            return closed.Scaled(openSkin.Body.Height / closed.Body.Height);
        }
        catch (Exception error)
        {
            Log.Error($"{avd.Id}: closed skin unusable, staying with the open one: {error.Message}");
            return null;
        }
    }

    public string? Serial => RunningEmulator.Scan().GetValueOrDefault(Avd.Id)?.Serial;

    public void BringToFront() => Window?.ShowAndActivate();

    /// Shuts the emulator down like closing its window: the quick-boot snapshot is saved first. One another
    /// program started is asked the same over its gRPC port; whoever started it sees it exit, as when its own
    /// window closes, and the exit ends this session.
    public void Stop()
    {
        // A phone keeps running; its window only lets go of it.
        if (physical != null)
        {
            TearDown();
            return;
        }
        if (State is not (SessionState.Booting or SessionState.Running)) return;
        State = SessionState.Stopping;
        Window?.Hide();
        CloseStreams();
        _ = Task.Run(async () =>
        {
            await ShutDownEmulator();
            // Saving a snapshot has taken over 30 s; only much later is the emulator considered stuck.
            await Task.Run(() => emulator?.WaitOrTerminate());
        });
    }

    /// Closing the window: shuts down a device this app started, only lets go of one it is just showing.
    public void Close()
    {
        if (Attached != null || physical != null) TearDown(); else Stop();
    }

    /// For quitting: every device window goes away at once and the emulators save their snapshots in
    /// parallel, without holding up the UI thread; the task ends once they have all exited. Devices another
    /// program started are only let go.
    public static async Task StopAll(IReadOnlyList<DeviceSession> sessions)
    {
        foreach (var session in sessions.Where(s => s.Attached != null)) session.TearDown();
        var stopping = sessions.Where(s => s.Attached == null && s.State is SessionState.Booting or SessionState.Running).ToList();
        foreach (var session in stopping)
        {
            session.State = SessionState.Stopping;
            session.Window?.Hide();
            session.CloseStreams();
        }
        await Task.WhenAll(stopping.Select(s => s.ShutDownEmulator()));
        // Saving a snapshot has taken over 30 s; only much later is an emulator considered stuck.
        await Task.Run(() => Parallel.ForEach(sessions, s => s.emulator?.WaitOrTerminate()));
    }

    /// Asks the emulator to shut down over gRPC; one this app started that does not answer (still starting) is
    /// ended outright.
    private async Task ShutDownEmulator()
    {
        if (client == null) return;
        if (Attached == null && !(emulator?.IsRunning ?? false)) return;
        if (!await client.ShutDown()) emulator?.Terminate();
    }

    /// Ends the screen, posture and clipboard streams, so nothing is drawn or synced while the emulator shuts
    /// down.
    private void CloseStreams()
    {
        streamCancellation?.Cancel();
        streamCancellation = null;
        postureCancellation?.Cancel();
        postureCancellation = null;
        screenOnWatch?.Stop();
        screenOnWatch = null;
        clipboard?.Stop();
    }

    public void Run(DeviceCommand command)
    {
        switch (command)
        {
            case DeviceCommand.Key key when physical != null:
                if (AndroidKey.Code(key.Name) is { } code) TapKey(code);
                break;
            case DeviceCommand.Key key: Enqueue(c => c.SendKey(key.Name)); break;
            case DeviceCommand.Do action: Perform(action.Action); break;
        }
    }

    /// The latest frame, for the device manager's preview, with the skin and turn it is shown at.
    public DeviceThumbnail? Thumbnail()
    {
        if (lastPixels == null) return null;
        return new DeviceThumbnail((byte[])lastPixels.Clone(), lastSize.Width, lastSize.Height, lastRotation, isClosed ? Avd.ClosedSkinFolder : null);
    }

    // Window

    private string PlacementKey => $"deviceWindow.{Avd.Id}";

    private void ShowWindow(int cascade)
    {
        var saved = Settings.Get(PlacementKey) as JsonObject;
        double? savedX = saved?["x"]?.GetValue<double>(), savedTop = saved?["top"]?.GetValue<double>();
        var screen = savedX is { } sx && savedTop is { } st && Screen.All.FirstOrDefault(s => s.Frame.Contains(sx, st)) is { } found
            ? found : Screen.Primary;
        var visible = screen.WorkArea;
        var longSide = Math.Max(skin.Size.Width, skin.Size.Height);
        preferredScale = saved?["scale"]?.GetValue<double>() ?? Math.Min(FitScale(0, visible, screen.Scale), 900 / longSide);
        // Opened the way it was last held, which is how a quick boot brings the device back.
        var turns = ((saved?["turns"]?.GetValue<int>() ?? 0) % 4 + 4) % 4;
        var scale = ShownScale(turns, screen);

        var view = new DeviceView(skin, scale);
        view.SetQuarterTurns(turns, false);
        if (closedSkin != null) heldTurns = turns;
        deviceTurns = turns;
        var window = new DeviceWindow(Avd.Name);
        var shell = new DeviceShell(window, view);
        var size = BoxPixels(view.SizeFor(turns), screen.Scale);
        PixelRect frame;
        if (savedX is { } x && savedTop is { } top)
        {
            frame = new PixelRect(x, top, size.Width, size.Height);
        }
        else
        {
            var offset = cascade % 6 * 32 * screen.Scale;
            frame = new PixelRect(Math.Round(visible.MidX - size.Width / 2 + offset), Math.Round(visible.MidY - size.Height / 2 + offset), size.Width, size.Height);
        }
        window.SetBox(DeviceWindow.Fitted(frame, screen));

        var chrome = new DeviceChrome(window, Avd.Name, Subtitle(skin), closedSkin != null, physical: physical != null);
        chrome.Key += SendKey;
        chrome.Action += Perform;
        chrome.MenuProvider = MakeMenu;
        chrome.Posture += posture => Enqueue(async c =>
        {
            try { await c.SetPosture(posture); } catch (Exception error) { Log.Error($"setPosture failed: {error.Message}"); }
        });
        shell.Chrome = chrome;
        shell.ResizeBegan += ResizeBegan;
        shell.ResizeChanged += ResizeChanged;
        shell.ResizeEnded += ResizeEnded;
        window.Moved += WindowMoved;
        window.ScreenTopReached += reached => chrome.HoldTitleBar(reached);
        window.ScaleChanged += ScaleChanged;
        window.CloseRequested += Close;

        view.Touch += (point, pressed) =>
        {
            if (physical != null)
            {
                PhysicalTouch(point, pressed);
                return;
            }
            var touch = TouchPoint(point);
            Log.Debug($"touch {(pressed ? "down" : "up")} at {(int)touch.X},{(int)touch.Y}");
            Enqueue(c => c.SendTouch((int)touch.X, (int)touch.Y, pressed));
        };
        view.SideButtonKey += SendKey;
        view.ScreenPressed += chrome.Dismiss;
        view.DragRequested += window.Drag;
        view.RasterScaleChanged += CommitSize;
        view.KeyDown += OnKeyDown;
        view.CharacterReceived += OnCharacterReceived;
        view.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            var menu = MakeMenu();
            if (e.TryGetPosition(view, out var point)) menu.ShowAt(view, point);
            else menu.ShowAt(view);
        };
        window.Root.PreviewKeyDown += OnShortcut;
        view.PreviewKeyDown += OnShortcut;

        Window = window;
        this.shell = shell;
        this.chrome = chrome;
        this.view = view;
        window.ShowAndActivate();
        view.Focus(FocusState.Programmatic);
        shell.Layout();
    }

    /// "API 36 · 1080 × 2424", from the system image folder name and the display size.
    private string Subtitle(DeviceSkin skin)
    {
        var resolution = $"{(int)skin.Resolution.Width} × {(int)skin.Resolution.Height}";
        if (physical?.Info is { } info) return $"Android {info.AndroidVersion} · {resolution}";
        var folder = Avd.Sysdir?.Split('/', '\\').FirstOrDefault(p => p.StartsWith("android-"));
        return folder == null ? resolution : $"API {folder["android-".Length..]} · {resolution}";
    }

    /// The window's menu, which the title bar's menu button and a right click both open: what macOS keeps in its
    /// menu bar (the app's, Device and Window menus), the device's commands and sizes a level down.
    private MenuFlyout MakeMenu()
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, string? shortcut, Action click, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, KeyboardAcceleratorTextOverride = shortcut ?? "", IsEnabled = enabled };
            item.Click += (_, _) => click();
            return item;
        }

        menu.Items.Add(Item("设备管理器", "Ctrl+Shift+2", () => ManagerRequested?.Invoke(ManagerTask.Show)));
        menu.Items.Add(new MenuFlyoutSeparator());

        var running = State == SessionState.Running;
        var device = new MenuFlyoutSubItem { Text = "设备" };
        foreach (var group in DeviceCommand.MenuGroups)
        {
            if (device.Items.Count > 0) device.Items.Add(new MenuFlyoutSeparator());
            foreach (var (title, command, shortcut) in group.Where(g => IsAvailable(g.Command)))
                device.Items.Add(Item(title, shortcut, () => Run(command), running));
        }
        menu.Items.Add(device);
        var sizes = new MenuFlyoutSubItem { Text = "窗口大小" };
        foreach (var (title, zoom, shortcut) in ZoomItems) sizes.Items.Add(Item(title, shortcut, () => Zoom(zoom), CanZoom(zoom)));
        menu.Items.Add(sizes);
        menu.Items.Add(new MenuFlyoutSeparator());

        var forwarder = new ToggleMenuFlyoutItem
        {
            Text = "让 Android Studio 等工具使用 Emulator Shell",
            IsChecked = EmulatorForwarder.State == EmulatorForwarder.ForwarderState.Installed,
            IsEnabled = EmulatorForwarder.IsAvailable,
        };
        forwarder.Click += (_, _) => ManagerRequested?.Invoke(ManagerTask.ToggleForwarder);
        menu.Items.Add(forwarder);
        menu.Items.Add(Item(EmulatorForwarder.IsCommandInstalled ? "移除命令行工具 emulator-shell" : "安装命令行工具 emulator-shell", null,
                            () => ManagerRequested?.Invoke(ManagerTask.ToggleCommand)));
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(Item("最小化", "Ctrl+M", () => Window?.Minimize()));
        menu.Items.Add(Item("关闭", "Ctrl+W", Close));
        menu.Items.Add(Item("退出 Emulator Shell", "Ctrl+Q", () => QuitRequested?.Invoke()));
        return menu;
    }

    public static readonly (string Title, DeviceZoom Zoom, string Shortcut)[] ZoomItems =
    [
        ("像素一比一", DeviceZoom.ActualPixels, "Ctrl+1"), ("物理尺寸", DeviceZoom.Physical, "Ctrl+2"), ("适合屏幕", DeviceZoom.Fit, "Ctrl+3"),
        ("放大", DeviceZoom.Larger, "Ctrl++"), ("缩小", DeviceZoom.Smaller, "Ctrl+−"),
    ];

    // Keyboard

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// The shortcuts the macOS menus carry, with Ctrl for ⌘.
    private void OnShortcut(object sender, KeyRoutedEventArgs e)
    {
        bool control = IsDown(VirtualKey.Control), shift = IsDown(VirtualKey.Shift), alt = IsDown(VirtualKey.Menu);
        if (!control) return;
        var key = e.Key;
        DeviceCommand? command = (key, shift, alt) switch
        {
            (VirtualKey.B, false, false) => new DeviceCommand.Key("GoBack"),
            (VirtualKey.H, true, false) => new DeviceCommand.Key("GoHome"),
            (VirtualKey.O, true, false) => new DeviceCommand.Key("AppSwitch"),
            (VirtualKey.S, false, false) => new DeviceCommand.Do(DeviceAction.Screenshot),
            (VirtualKey.S, true, false) => new DeviceCommand.Do(DeviceAction.CopyScreenshot),
            (VirtualKey.R, true, false) => new DeviceCommand.Do(DeviceAction.Record),
            (VirtualKey.Left, false, false) => new DeviceCommand.Do(DeviceAction.RotateLeft),
            (VirtualKey.Right, false, false) => new DeviceCommand.Do(DeviceAction.RotateRight),
            (VirtualKey.F, true, false) => new DeviceCommand.Do(DeviceAction.ToggleFold),
            (VirtualKey.Down, false, true) => new DeviceCommand.Key("AudioVolumeDown"),
            (VirtualKey.Up, false, true) => new DeviceCommand.Key("AudioVolumeUp"),
            (VirtualKey.L, false, false) => new DeviceCommand.Key("Power"),
            _ => null,
        };
        if (command != null)
        {
            e.Handled = true;
            if (State == SessionState.Running && IsAvailable(command)) Run(command);
            return;
        }
        DeviceZoom? zoom = (key, shift, alt) switch
        {
            (VirtualKey.Number1 or VirtualKey.NumberPad1, false, false) => DeviceZoom.ActualPixels,
            (VirtualKey.Number2 or VirtualKey.NumberPad2, false, false) => DeviceZoom.Physical,
            (VirtualKey.Number3 or VirtualKey.NumberPad3, false, false) => DeviceZoom.Fit,
            ((VirtualKey)187 or VirtualKey.Add, _, false) => DeviceZoom.Larger,
            ((VirtualKey)189 or VirtualKey.Subtract, false, false) => DeviceZoom.Smaller,
            _ => null,
        };
        if (zoom is { } z)
        {
            e.Handled = true;
            if (CanZoom(z)) Zoom(z);
            return;
        }
        switch (key, shift, alt)
        {
            case (VirtualKey.W, false, false):
                e.Handled = true;
                Close();
                break;
            case (VirtualKey.M, false, false):
                e.Handled = true;
                Window?.Minimize();
                break;
            case (VirtualKey.Number2, true, false):
                e.Handled = true;
                ManagerRequested?.Invoke(ManagerTask.Show);
                break;
            case (VirtualKey.Q, false, false):
                e.Handled = true;
                QuitRequested?.Invoke();
                break;
            case (VirtualKey.V, false, false) when physical != null:
                e.Handled = true;
                PastePhysical();
                break;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (KeyMapping.Special(e.Key, IsDown(VirtualKey.Control), IsDown(VirtualKey.Menu)) is not { } key) return;
        e.Handled = true;
        if (physical != null) PhysicalKey(key);
        else Enqueue(c => c.SendKey(key));
    }

    private char? highSurrogate;

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        var character = e.Character;
        // Ctrl+letter arrives as a control character, Enter and Tab as theirs: those went as keys or shortcuts.
        if (character < ' ' || character == '\x7f' || (IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu))) return;
        if (char.IsHighSurrogate(character))
        {
            highSurrogate = character;
            return;
        }
        var text = highSurrogate is { } high && char.IsLowSurrogate(character) ? new string([high, character]) : character.ToString();
        highSurrogate = null;
        e.Handled = true;
        var (key, typed) = KeyMapping.Character(text);
        if (physical != null)
        {
            if (key != null) PhysicalKey(key);
            else if (typed != null) TypeOnDevice(typed);
            return;
        }
        if (key != null) Enqueue(c => c.SendKey(key));
        else if (typed != null) Enqueue(c => c.SendText(typed));
    }

    // Size

    /// The box in pixels around a device of `device` DIPs, at `scale` pixels per DIP.
    private Size BoxPixels(Size device, double scale)
    {
        var box = DeviceShell.BoxSize(device, SideRoom);
        return new Size(Math.Round(box.Width * scale), Math.Round(box.Height * scale));
    }

    /// Room either side of the device: a foldable's posture rail.
    private double SideRoom => closedSkin != null ? DeviceShell.FoldableSideRoom : 0;

    private Screen CurrentScreen => Window?.Screen ?? Screen.Primary;

    /// Shows the device at one of the menu's sizes.
    public void Zoom(DeviceZoom zoom)
    {
        if (Window is not { } window || view == null || folding) return;
        var screen = CurrentScreen;
        double target;
        switch (zoom)
        {
            case DeviceZoom.ActualPixels:
                target = PixelScale(screen);
                break;
            case DeviceZoom.Physical:
                if (PhysicalScale(screen) is not { } physical)
                {
                    chrome?.ShowResizeReadout("不知道这台显示器的尺寸");
                    chrome?.ShowResizeReadout(null);
                    return;
                }
                target = physical;
                break;
            case DeviceZoom.Fit:
                target = FitScale(deviceTurns, screen.WorkArea, screen.Scale);
                break;
            default:
                // In steps of 10 % of one-to-one pixels: 50 %, 60 %, 70 % …
                var pixel = PixelScale(screen);
                var percent = view.DrawnScale / pixel * 10;
                var step = zoom == DeviceZoom.Larger ? Math.Floor(percent + 0.05) + 1 : Math.Ceiling(percent - 0.05) - 1;
                target = Math.Max(step, 1) / 10 * pixel;
                break;
        }
        var scale = Math.Min(Math.Max(target, MinScale), FitScale(deviceTurns, screen.WorkArea, screen.Scale));
        preferredScale = scale;
        var box = BoxPixels(view.SizeFor(deviceTurns, scale: scale), screen.Scale);
        var old = window.Box;
        chrome?.ShowResizeReadout(Readout(scale, screen));
        chrome?.ShowResizeReadout(null);
        // Over 0.3 s, the top kept in place: the device is stretched to each step's box, as during a corner drag,
        // and drawn anew at the end. The window covers both boxes meanwhile, so only its content moves.
        const double rooms = DeviceShell.TopRoom + DeviceShell.BottomRoom;
        var skinHeight = view.SkinSizeFor(deviceTurns).Height;
        window.SetBox(DeviceWindow.Fitted(new PixelRect(Math.Round(old.MidX - box.Width / 2), old.Top, box.Width, box.Height), screen),
                      ZoomDuration,
                      step: step => view.SetLiveScale((step.Height / window.Scale - rooms) / skinHeight),
                      completion: () =>
                      {
                          view.SetLiveScale(scale);
                          CommitSize();
                      });
    }

    private static readonly TimeSpan ZoomDuration = TimeSpan.FromSeconds(0.3);

    /// Whether `zoom` would change the window, for the menu.
    public bool CanZoom(DeviceZoom zoom)
    {
        if (view == null) return false;
        var screen = CurrentScreen;
        return zoom switch
        {
            DeviceZoom.Physical => PhysicalScale(screen) != null,
            DeviceZoom.Larger => view.DrawnScale < FitScale(deviceTurns, screen.WorkArea, screen.Scale) - 0.0001,
            DeviceZoom.Smaller => view.DrawnScale > MinScale + 0.0001,
            _ => true,
        };
    }

    /// A corner drag: the device keeps its proportions and the opposite corner stays where it is.
    private void ResizeBegan(ResizeHandle.Corner corner)
    {
        if (Window is not { } window || view == null) return;
        bool left = corner is ResizeHandle.Corner.TopLeft or ResizeHandle.Corner.BottomLeft;
        bool top = corner is ResizeHandle.Corner.TopLeft or ResizeHandle.Corner.TopRight;
        var box = window.Box;
        var anchor = new Point(left ? box.Right : box.Left, top ? box.Bottom : box.Top);
        // The window covers all the box can grow into meanwhile: from the fixed corner to the screen's edges.
        var visible = CurrentScreen.WorkArea;
        var reach = new PixelRect(left ? visible.Left : anchor.X, top ? visible.Top : anchor.Y,
                                  left ? anchor.X - visible.Left : visible.Right - anchor.X, top ? anchor.Y - visible.Top : visible.Bottom - anchor.Y);
        resizing = (corner, new Point(left ? box.Left : box.Right, top ? box.Top : box.Bottom), anchor, view.LiveScale, window.Hold(reach));
    }

    private void ResizeChanged(Point point)
    {
        if (resizing is not { } drag || Window is not { } window || view == null) return;
        var screen = CurrentScreen;
        var visible = screen.WorkArea;
        bool left = drag.Corner is ResizeHandle.Corner.TopLeft or ResizeHandle.Corner.BottomLeft;
        bool top = drag.Corner is ResizeHandle.Corner.TopLeft or ResizeHandle.Corner.TopRight;
        var skinSize = view.SkinSizeFor(deviceTurns);
        // How far the corner moved outwards, per axis, in DIPs. The device grows by the part of that along its own
        // diagonal, so it changes smoothly from the size it had, whichever way the pointer goes.
        var dx = (point.X - drag.Start.X) * (left ? -1 : 1) / screen.Scale;
        var dy = (point.Y - drag.Start.Y) * (top ? -1 : 1) / screen.Scale;
        var diagonal = skinSize.Width * skinSize.Width + skinSize.Height * skinSize.Height;
        var scale = drag.Scale + (dx * skinSize.Width + dy * skinSize.Height) / diagonal;
        // As large as fits between the fixed corner and the screen's edges.
        var room = new PixelRect(0, 0, left ? drag.Anchor.X - visible.Left : visible.Right - drag.Anchor.X,
                                 top ? drag.Anchor.Y - visible.Top : visible.Bottom - drag.Anchor.Y);
        var largest = FitScale(deviceTurns, room, screen.Scale);
        scale = Snapped(Math.Min(Math.Max(scale, MinScale), Math.Max(largest, MinScale)), largest, screen);
        view.SetLiveScale(scale);
        var box = BoxPixels(view.SizeFor(deviceTurns, scale: scale), screen.Scale);
        var frame = new PixelRect(left ? drag.Anchor.X - box.Width : drag.Anchor.X, top ? drag.Anchor.Y - box.Height : drag.Anchor.Y, box.Width, box.Height);
        if (frame != window.Box) window.SetBox(frame);
        chrome?.ShowResizeReadout(Readout(scale, screen));
    }

    private void ResizeEnded()
    {
        if (resizing is not { } drag || view == null) return;
        resizing = null;
        drag.Hold.Dispose();
        chrome?.ShowResizeReadout(null);
        preferredScale = view.LiveScale;
        CommitSize();
    }

    /// After a drag, maybe onto another screen: the preferred size if it fits there, else as large as fits.
    private void WindowMoved()
    {
        if (Window is not { } window || view == null) return;
        var screen = CurrentScreen;
        var scale = ShownScale(deviceTurns, screen);
        if (Math.Abs(scale - view.DrawnScale) > 0.0001)
        {
            view.SetLiveScale(scale);
            var box = BoxPixels(view.SizeFor(deviceTurns, scale: scale), screen.Scale);
            var old = window.Box;
            window.SetBox(DeviceWindow.Fitted(new PixelRect(old.Left, old.Top, box.Width, box.Height), screen));
        }
        CommitSize();
    }

    /// Moved to a monitor of another scale: the same DIP size there, which is another pixel size.
    private void ScaleChanged()
    {
        if (Window is not { } window || view == null) return;
        var screen = CurrentScreen;
        var box = BoxPixels(view.SizeFor(deviceTurns, scale: ShownScale(deviceTurns, screen)), window.Scale);
        var old = window.Box;
        if (!window.IsAnimatingBox && resizing == null) window.SetBox(new PixelRect(old.Left, old.Top, box.Width, box.Height));
        WindowMoved();
    }

    /// The square the screen is streamed in, in pixels: a square because the emulator fits the upright picture
    /// into it, so one stream serves both orientations. For a foldable it fits either display at the chosen
    /// size, so folding never asks for a new stream: asking while the emulator switches displays has crashed it.
    private int WantedStreamSide
    {
        get
        {
            if (view == null) return 0;
            var shown = Math.Max(view.DisplayPixelSize.Width, view.DisplayPixelSize.Height);
            if (closedSkin == null) return (int)shown;
            // At the chosen size, which a fold does not change though either display may be shown smaller to fit.
            var pixelsPerSkinPixel = Math.Max(view.DrawnScale, preferredScale) * (Window?.Scale ?? 1);
            var displays = new[] { openSkin, closedSkin }.Select(s => Math.Max(s.Display.Width, s.Display.Height) * pixelsPerSkinPixel);
            // Whole pixels, with one to spare for the display grown to the pixel grid.
            return (int)Math.Max(shown, Math.Ceiling(displays.Max()) + 1);
        }
    }

    /// Once the size has settled: sharp visuals at the new size, and frames streamed at its pixel size.
    private void CommitSize()
    {
        if (view == null) return;
        view.CommitScale();
        shell?.Layout();
        SavePlacement();
        var side = WantedStreamSide;
        if (side != streamSide && streamCancellation != null && client != null)
        {
            Log.Debug($"display is now {side} px; requesting frames at that size");
            StartStream(client);
        }
    }

    /// Snaps to the one-to-one, physical and fit sizes within a few DIPs of the pointer.
    private double Snapped(double scale, double largest, Screen screen)
    {
        var height = view?.SkinSizeFor(deviceTurns).Height ?? 0;
        var candidates = new[] { PixelScale(screen), PhysicalScale(screen), largest }.OfType<double>();
        foreach (var candidate in candidates)
        {
            if (Math.Abs(candidate - scale) * height < 6 && candidate <= largest && candidate >= MinScale) return candidate;
        }
        return scale;
    }

    /// "52% · 666 × 1486": the size relative to one-to-one pixels, and the preset's name or the display's
    /// size in screen pixels.
    private string Readout(double scale, Screen screen)
    {
        var percent = $"{(int)Math.Round(scale / PixelScale(screen) * 100)}%";
        bool Close(double? other) => other is { } o && Math.Abs(o - scale) < 0.0005;
        if (Close(PixelScale(screen))) return percent + " · 像素一比一";
        if (Close(PhysicalScale(screen))) return percent + " · 物理尺寸";
        if (Close(FitScale(deviceTurns, screen.WorkArea, screen.Scale))) return percent + " · 适合屏幕";
        int width = (int)Math.Round(skin.Display.Width * scale * screen.Scale), height = (int)Math.Round(skin.Display.Height * scale * screen.Scale);
        return percent + $" · {width} × {height}";
    }

    /// The scale to show with the device at `turns`: the preferred one, unless it does not fit `screen`.
    private double ShownScale(int turns, Screen screen) =>
        Math.Max(Math.Min(preferredScale, FitScale(turns, screen.WorkArea, screen.Scale)), MinScale);

    /// The largest scale at which the window, with the device at `turns`, fits in `area` (pixels at `scale`).
    private double FitScale(int turns, PixelRect area, double scale)
    {
        var size = turns % 2 == 0 ? skin.Size : new Size(skin.Size.Height, skin.Size.Width);
        const double rooms = DeviceShell.TopRoom + DeviceShell.BottomRoom;
        return Math.Min((area.Height / scale - rooms) / size.Height, (area.Width / scale - 2 * SideRoom) / size.Width);
    }

    /// The device's long side is never shorter than 360 DIPs.
    private double MinScale => 360 / Math.Max(skin.Size.Width, skin.Size.Height);

    /// Device pixels per skin pixel: skins may draw the display at another size than the device's. `hw.lcd.*`
    /// describes the open display, so a foldable's closed skin is measured by the open one.
    private double DevicePixelsPerSkinPixel =>
        double.TryParse(Avd.Config.GetValueOrDefault("hw.lcd.width"), out var width) && openSkin.Display.Width > 0
            ? width / openSkin.Display.Width : 1;

    private double PixelScale(Screen screen) => DevicePixelsPerSkinPixel / screen.Scale;

    /// From the AVD's density (dots per inch) and the monitor's size in millimetres; null if either is unknown.
    private double? PhysicalScale(Screen screen)
    {
        if (!double.TryParse(Avd.Config.GetValueOrDefault("hw.lcd.density"), out var density) || density <= 0) return null;
        if (screen.PhysicalSize is not { } millimetres) return null;
        var dipsPerInch = screen.Frame.Width / screen.Scale / (millimetres.Width / 25.4);
        return DevicePixelsPerSkinPixel / density * dipsPerInch;
    }

    /// Remembers the chosen size, the device's turn and where the box's top-left corner is, for the next launch.
    private void SavePlacement()
    {
        if (Window is not { } window) return;
        Settings.Set(PlacementKey, new JsonObject
        {
            ["scale"] = preferredScale,
            ["x"] = window.Box.Left,
            ["top"] = window.Box.Top,
            ["turns"] = deviceTurns,
        });
    }

    // Emulator

    /// Opens the gRPC connection and the queue device input goes through.
    private EmulatorClient MakeClient(string? token = null)
    {
        var client = new EmulatorClient(GrpcPort, token);
        this.client = client;
        _ = Task.Run(async () =>
        {
            await foreach (var send in input.Reader.ReadAllAsync())
            {
                await send(client);
            }
        });
        return client;
    }

    private void AttachEmulator(RunningEmulator running)
    {
        var client = MakeClient(running.GrpcToken);
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(running.Pid);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => queue.TryEnqueue(EmulatorExited);
            exitWatch = process;
            if (process.HasExited) queue.TryEnqueue(EmulatorExited);
        }
        catch (ArgumentException)
        {
            // Gone before the watch started.
            queue.TryEnqueue(EmulatorExited);
        }
        Log.Info($"attached to {Avd.Id} (pid {running.Pid}) on gRPC port {GrpcPort}");
        StartClipboardAndControls(client);
        WaitForBoot(client);
    }

    private void StartEmulator(bool coldBoot, IReadOnlyList<string> extraArguments)
    {
        var client = MakeClient();

        // Without a window the emulator's "auto" GPU mode falls back to software rendering, so the host GPU
        // is used unless the AVD asks for software rendering.
        string[] software = ["swiftshader_indirect", "swiftshader", "swangle", "swangle_indirect", "lavapipe"];
        var gpu = software.Contains(Avd.Config.GetValueOrDefault("hw.gpu.mode") ?? "") ? "swiftshader_indirect" : "host";
        var emulator = new EmulatorProcess(Avd.Id, GrpcPort, gpu, (coldBoot ? ["-no-snapshot-load"] : Array.Empty<string>()).Concat(extraArguments).ToList());
        emulator.Exited += () => queue.TryEnqueue(EmulatorExited);
        var logs = Path.Combine(Settings.Folder, "Logs");
        Directory.CreateDirectory(logs);
        var logFile = Path.Combine(logs, $"emulator-{Avd.Id}.log");
        emulator.Start(logFile);
        this.emulator = emulator;
        Log.Info($"launched emulator for {Avd.Id} on gRPC port {GrpcPort}, log: {logFile}");
        StartClipboardAndControls(client);
        WaitForBoot(client);
    }

    private void StartClipboardAndControls(EmulatorClient client)
    {
        var clipboard = new ClipboardSync(client, queue);
        this.clipboard = clipboard;
        var controls = new DeviceControls(client, Avd, clipboard.IsEnabled);
        controls.ClipboardSyncChanged += on => clipboard.IsEnabled = on;
        if (chrome != null) chrome.Controls = controls;
    }

    /// Starts the streams once the gRPC endpoint answers, and marks the device running once it has booted.
    private void WaitForBoot(EmulatorClient client)
    {
        var cancellation = lifetime.Token;
        _ = Boot();

        async Task Boot()
        {
            try
            {
                await client.WaitUntilReachable(TimeSpan.FromSeconds(120), booted => Log.Info($"gRPC endpoint reachable, guest booted: {booted}"), cancellation);
                StartStream(client);
                FollowPosture(client);
                while (!await client.IsBooted(cancellation)) await Task.Delay(1000, cancellation);
                Log.Info($"{Avd.Id} booted");
                if (State != SessionState.Booting) return;
                State = SessionState.Running;
                chrome?.SetReady(true);
                clipboard?.Start();
                RunTestScript();
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested) Fail(error);
            }
        }
    }

    /// See DeviceSession.Debug.cs.
    partial void RunTestScript();

    private void EmulatorExited()
    {
        if (State == SessionState.Stopped) return;
        if (State != SessionState.Stopping) Log.Info($"emulator for {Avd.Id} exited");
        TearDown();
    }

    private void TearDown()
    {
        if (State == SessionState.Stopped) return;
        exitWatch?.Dispose();
        exitWatch = null;
        TearDownPhysical();
        lifetime.Cancel();
        streamCancellation?.Cancel();
        postureCancellation?.Cancel();
        screenOnWatch?.Stop();
        input.Writer.TryComplete();
        clipboard?.Stop();
        client?.Dispose();
        chrome?.Close();
        foldAnimation?.Finish();
        if (Window is { } window)
        {
            window.Hide();
            window.Close();
        }
        lastPixels = null;
        State = SessionState.Stopped;
    }

    /// (Re)opens the screen stream at the display's current pixel size; a new stream starts with the current frame.
    private void StartStream(EmulatorClient client)
    {
        var side = WantedStreamSide;
        streamSide = side;
        streamCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        streamCancellation = cancellation;
        var counter = new FrameCounter();
        _ = Task.Run(async () =>
        {
            try
            {
                Log.Info($"streaming {side}×{side} frames");
                await client.StreamScreen(side, side, frame => Receive(frame, counter), cancellation.Token);
            }
            catch (Exception error)
            {
                if (cancellation.IsCancellationRequested) return;
                queue.TryEnqueue(() =>
                {
                    if (State is SessionState.Booting or SessionState.Running) Fail(error);
                });
            }
        });
    }

    /// On the streaming thread: keeps the newest frame and has the UI thread show it, dropping any it has not
    /// got to yet.
    private void Receive(RawFrame frame, FrameCounter counter)
    {
        counter.NoteShape(frame);
        var length = frame.Width * frame.Height * 4;
        var blank = IsBlank(frame.Pixels.Span, frame.Width, frame.Height);
        bool dropped;
        lock (frameLock)
        {
            if (pendingPixels.Length != length) pendingPixels = new byte[length];
            frame.Pixels.Span[..length].CopyTo(pendingPixels);
            pendingFrame = (frame.Width, frame.Height, frame.Rotation, frame.Folded, blank);
            dropped = frameScheduled;
            frameScheduled = true;
        }
        counter.Tick(frame, shown: !dropped);
        if (!dropped) queue.TryEnqueue(DispatcherQueuePriority.High, ShowPendingFrame);
    }

    private void ShowPendingFrame()
    {
        byte[] pixels;
        (int Width, int Height, int Rotation, bool Folded, bool Blank) info;
        lock (frameLock)
        {
            frameScheduled = false;
            info = pendingFrame;
            pixels = pendingPixels;
            // The next frame is written into another buffer; this one is kept as the last frame.
            pendingPixels = lastPixels?.Length == pixels.Length ? lastPixels : new byte[pixels.Length];
        }
        if (view == null || State == SessionState.Stopped) return;
        // A foldable's frames of the other state, which keep coming for a moment after its posture changes,
        // do not belong in the skin it now shows.
        if (closedSkin != null && info.Folded != isClosed) return;
        lastPixels = pixels;
        lastSize = (info.Width, info.Height);
        lastRotation = info.Rotation;
        view.Show(pixels, info.Width, info.Height, info.Rotation);
        ShowLiveFold(info.Folded);
        HoldTurn(info.Rotation);
        DeviceTurned(info.Rotation);
    }

    /// Whether a frame is all but black, as the first ones of a display the emulator has just switched to are.
    private static bool IsBlank(ReadOnlySpan<byte> pixels, int width, int height)
    {
        // An 8 × 8 grid of samples is plenty: the status bar alone has bright pixels.
        for (var y = height / 16; y < height; y += Math.Max(1, height / 8))
        {
            for (var x = width / 16; x < width; x += Math.Max(1, width / 8))
            {
                var index = (y * width + x) * 4;
                if (pixels[index] > 12 || pixels[index + 1] > 12 || pixels[index + 2] > 12) return false;
            }
        }
        return true;
    }

    private void Enqueue(Func<EmulatorClient, Task> send) => input.Writer.TryWrite(send);

    private void SendKey(string key, bool pressed)
    {
        Log.Debug($"key {key} {(pressed ? "down" : "up")}");
        if (physical != null)
        {
            if (AndroidKey.Code(key) is { } code) physical.Stream.InjectKey(code, pressed ? ScrcpyStream.KeyAction.Down : ScrcpyStream.KeyAction.Up);
            return;
        }
        Enqueue(c => c.SendKey(key, pressed ? KeyboardEvent.Types.KeyEventType.Keydown : KeyboardEvent.Types.KeyEventType.Keyup));
    }

    private void Perform(DeviceAction action)
    {
        switch (action)
        {
            case DeviceAction.Close: Close(); break;
            case DeviceAction.Minimize: Window?.Minimize(); break;
            case DeviceAction.Screenshot when physical != null: PhysicalScreenshot(); break;
            case DeviceAction.Screenshot: TakeScreenshot(); break;
            case DeviceAction.CopyScreenshot when physical != null: PhysicalCopyScreenshot(); break;
            case DeviceAction.CopyScreenshot: CopyScreenshot(); break;
            case DeviceAction.Record when physical != null: physical.Store.ToggleRecording(physical.Id); break;
            case DeviceAction.RotateLeft: Rotate(1); break;
            case DeviceAction.RotateRight: Rotate(-1); break;
            case DeviceAction.ToggleFold: ToggleFold(); break;
        }
    }

    /// Whether `command` applies to this device: folding only to a foldable with a closed skin; a phone turns when
    /// it is turned, and recording is for phones for now.
    public bool IsAvailable(DeviceCommand command) => command switch
    {
        DeviceCommand.Do { Action: DeviceAction.ToggleFold } => closedSkin != null,
        DeviceCommand.Do { Action: DeviceAction.RotateLeft or DeviceAction.RotateRight } => physical == null,
        DeviceCommand.Do { Action: DeviceAction.Record } => physical != null,
        _ => true,
    };

    /// Folds an open foldable shut or opens a closed one; the posture notification then plays the animation.
    private void ToggleFold()
    {
        if (closedSkin == null || folding) return;
        var posture = isClosed ? PostureValue.PostureOpened : PostureValue.PostureClosed;
        Enqueue(async c =>
        {
            try { await c.SetPosture(posture); } catch (Exception error) { Log.Error($"setPosture failed: {error.Message}"); }
        });
    }

    // Rotation

    /// Asks the emulator to turn the device; the window follows once frames report the new orientation.
    private void Rotate(int turns) => TurnTo(deviceTurns + turns);

    /// Asks the emulator to hold the device at `turns` counterclockwise quarter turns.
    /// `reopensIfSilent` reopens the screen stream when no frame reports the turn soon; not while folding,
    /// when the emulator is switching displays.
    private void TurnTo(int turns, bool reopensIfSilent = true)
    {
        var target = (turns % 4 + 4) % 4;
        float[] degrees = [0, 90, 180, -90];
        Log.Debug($"rotate to {degrees[target]}°");
        Enqueue(c => c.SetRotation(degrees[target]));
        // A screen that does not redraw (a portrait-only app) sends no frame to report the new orientation.
        if (!reopensIfSilent) return;
        _ = Task.Delay(400).ContinueWith(_ => queue.TryEnqueue(() =>
        {
            if (deviceTurns == target || client == null || State != SessionState.Running) return;
            Log.Debug("no frame at the new orientation yet; reopening the screen stream");
            StartStream(client);
        }));
    }

    /// Turns the window with the device. A device that no longer fits the screen is shown smaller until it
    /// fits again at the preferred size.
    private void DeviceTurned(int turns)
    {
        // While folding the animation shows the device; the live view takes the turn once it is done.
        if (turns == deviceTurns || folding || heldTurns != null || view == null || Window is not { } window) return;
        deviceTurns = turns;
        var scale = ShownScale(turns, CurrentScreen);
        // The turning device sweeps a circle about its centre, which the window covers until the turn ends.
        var center = window.ToScreen(shell!.DeviceFrame);
        var radius = Math.Sqrt(Math.Pow(view.SizeFor(0).Width, 2) + Math.Pow(view.SizeFor(0).Height, 2)) / 2 * Math.Max(1, scale / view.DrawnScale)
                     * window.Scale + 16;
        turnHold?.Dispose();
        turnHold = window.Hold(new PixelRect(center.MidX - radius, center.MidY - radius, 2 * radius, 2 * radius));
        // Turned, it may be shown at another size to fit; the turn animates that too.
        view.SetQuarterTurns(turns, scale, animated: true);
        ReshapeWindow(animate: true);
    }

    /// Fits the window around the device as it is now held and drawn, keeping its centre where it was.
    private void ReshapeWindow(bool animate)
    {
        if (Window is not { } window || view == null) return;
        var screen = CurrentScreen;
        var scale = ShownScale(deviceTurns, screen);
        if (scale != view.DrawnScale)
        {
            view.SetLiveScale(scale);
            view.CommitScale();
        }
        var box = BoxPixels(view.SizeFor(deviceTurns), screen.Scale);
        var old = window.Box;
        var fitted = DeviceWindow.Fitted(new PixelRect(Math.Round(old.MidX - box.Width / 2), Math.Round(old.MidY - box.Height / 2), box.Width, box.Height), screen);
        if (animate)
        {
            // Along with the device's turn, ending with it.
            window.SetBox(fitted, DeviceView.TurnDuration, completion: () =>
            {
                turnHold?.Dispose();
                turnHold = null;
                CommitSize();
            });
            if (!window.IsAnimatingBox)
            {
                // Same box: the turn alone animates; the window is let go when it ends.
                _ = Task.Delay(DeviceView.TurnDuration).ContinueWith(_ => queue.TryEnqueue(() =>
                {
                    turnHold?.Dispose();
                    turnHold = null;
                    shell?.Layout();
                }));
            }
        }
        else
        {
            window.SetBox(fitted);
            CommitSize();
        }
    }

    // Folding

    /// The emulator reads touch coordinates in the open display's pixels and scales them to the display the guest
    /// shows, so a closed foldable's points on its outer display are scaled up to that size first.
    private Point TouchPoint(Point point)
    {
        if (skin.Display.Width == openSkin.Display.Width && skin.Display.Height == openSkin.Display.Height) return point;
        return new Point(Math.Floor(point.X * openSkin.Display.Width / skin.Display.Width),
                         Math.Floor(point.Y * openSkin.Display.Height / skin.Display.Height));
    }

    /// The emulator holds a book-style foldable on its side while open, unlike the closed one: after a cold boot it
    /// reports a turn of 3. Folding keeps the turn, as a real device stays in the hand, so a foldable is held
    /// as its window was last (upright the first time) from its first frame on, and the open and closed
    /// device stay the same way up. Turns the frames report meanwhile are not followed, so the window does not
    /// turn there and back.
    private void HoldTurn(int reported)
    {
        if (heldTurns is not { } turns) return;
        if (holdingSince is not { } since)
        {
            holdingSince = DateTime.UtcNow;
            if (reported != turns)
            {
                Log.Debug($"foldable reported turn {reported}; holding it at {turns}");
                TurnTo(turns, reopensIfSilent: false);
            }
            else
            {
                heldTurns = null;
            }
            return;
        }
        if (reported == turns || (DateTime.UtcNow - since).TotalSeconds > 5) heldTurns = null;
    }

    /// Follows the posture of a foldable, whether set from the "More" panel, the rail, the console or a snapshot.
    private void FollowPosture(EmulatorClient client)
    {
        if (Avd.Config.GetValueOrDefault("hw.sensor.hinge") != "yes") return;
        postureCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        postureCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await client.StreamPosture(posture => queue.TryEnqueue(() => PostureChanged(posture)), cancellation.Token);
            }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested) Log.Error($"posture notifications ended: {error.Message}");
            }
        });
        // Android keeps a display it switches to off until the windows on it have drawn, and logs when it turns it
        // on; the emulator streams what it draws meanwhile, which a real device never shows.
        screenOnWatch?.Stop();
        var avdId = Avd.Id;
        screenOnWatch = new GuestLogWatch(() => RunningEmulator.Scan().GetValueOrDefault(avdId)?.Serial, "Unblocked screen on",
                                          _ => queue.TryEnqueue(ScreenTurnedOn));
    }

    /// While folding, notes that Android has turned the display entered on, and what it showed then.
    private void ScreenTurnedOn()
    {
        if (!folding || screenOn != null || lastPixels == null) return;
        Log.Debug("display turned on");
        screenOn = (DateTime.UtcNow, Signature(lastPixels, lastSize.Width, lastSize.Height));
        _ = Task.Delay(SettleTime).ContinueWith(_ => queue.TryEnqueue(() => ShowLiveFold(isClosed)));
    }

    /// How long after Android turns a display on its windows may still be redrawing at the new size.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(0.5);
    /// How long after a fold to light up the display entered when the log has not said it turned on (adb may not
    /// be authorised, for one).
    private static readonly TimeSpan UnannouncedTime = TimeSpan.FromSeconds(1.2);

    /// Whether a frame of the display entered shows what Android lit it up with, not one of the frames it draws
    /// while it switches displays: the old layout stretched, then the wallpaper alone while the windows redraw.
    /// Android turns the display on when they report drawn, which is often a moment before their new layout comes
    /// through; so the frame must also differ from the one shown then, or come a while after.
    private bool LiveFrameSettled(byte[] pixels, int width, int height)
    {
        if (foldStartedAt is not { } started) return true;
        if (screenOn is not { } on) return DateTime.UtcNow - started > UnannouncedTime;
        return DateTime.UtcNow - on.At >= SettleTime || Differs(Signature(pixels, width, height), on.Signature);
    }

    /// A 32 × 32 grid of a frame's colours, to tell one picture from another.
    private static byte[] Signature(byte[] pixels, int width, int height)
    {
        var samples = new byte[32 * 32 * 3];
        for (var j = 0; j < 32; j++)
        {
            for (var i = 0; i < 32; i++)
            {
                var pixel = (height * (2 * j + 1) / 64 * width + width * (2 * i + 1) / 64) * 4;
                Array.Copy(pixels, pixel, samples, (j * 32 + i) * 3, 3);
            }
        }
        return samples;
    }

    /// Whether two signatures show different pictures: a few percent of the grid changed, as the icons coming onto
    /// a bare wallpaper do, not just the clock.
    private static bool Differs(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return true;
        var changed = 0;
        for (var k = 0; k < a.Length; k += 3)
        {
            if (Math.Abs(a[k] - b[k]) > 24 || Math.Abs(a[k + 1] - b[k + 1]) > 24 || Math.Abs(a[k + 2] - b[k + 2]) > 24) changed++;
        }
        return changed > a.Length / 3 / 50;
    }

    /// The open skin's hinge, in skin pixels from its left edge.
    private double Hinge => openSkin.Display.X + openSkin.Display.Width / 2;

    /// A closed foldable shows its outer display, so the window switches to that skin, animated like a book
    /// closing. The device stays held as it is (see `HoldTurn`), so the two states are the same way up. Open and
    /// half open show the same display, the half left of the hinge turned toward the viewer when half open.
    private void PostureChanged(PostureValue posture)
    {
        Log.Debug($"posture {posture}");
        if (chrome?.Controls is { } controls)
        {
            controls.Posture = posture;
            controls.Notify();
        }
        chrome?.ShowPosture(posture);
        if (closedSkin == null || view == null || Window is not { } window) return;
        var closed = posture == PostureValue.PostureClosed;
        if (!closed) halfOpen = posture == PostureValue.PostureHalfOpened;
        if (closed == isClosed)
        {
            // Only the half left of the hinge turns. While folding the animation's end takes it on.
            if (!closed && !folding) view.SetFold(OpenAngle, Hinge, animated: State == SessionState.Running);
            return;
        }
        var angle = closed ? view.FoldAngle : OpenAngle;
        isClosed = closed;
        var entering = closed ? closedSkin : openSkin;
        skin = entering;
        // Played for a change one sees happen, not for the posture a quick boot comes back in.
        if (State != SessionState.Running || !window.IsShown || lastPixels == null || shell == null)
        {
            view.SetSkin(entering);
            if (!closed) view.SetFold(OpenAngle, Hinge, animated: false);
            ReshapeWindow(animate: false);
            return;
        }
        var screen = CurrentScreen;
        var dpi = window.Scale;
        var deviceCenter = window.ToScreen(shell.DeviceFrame);
        var center = new Point(deviceCenter.MidX, deviceCenter.MidY);
        var drawn = view.DrawnScale;
        // The closed device lies exactly over the open one's right half: their outlines share the top and the outer
        // edge.
        var geometry = new FoldAnimation.Geometry(view.SizeFor(0, openSkin, drawn), view.SizeFor(0, closedSkin, drawn), Hinge * drawn,
                                                  new Point((openSkin.Body.Right - closedSkin.Body.Right) * drawn, (openSkin.Body.Top - closedSkin.Body.Top) * drawn));
        // The state entered is shown as large as it fits, which may differ from the state left; the animation
        // grows or shrinks to it while it folds.
        var growth = ShownScale(deviceTurns, screen) / drawn;
        // The closed device folds where the left half lands and unfolds where it is; it moves if the window
        // for the state entered, which follows the device, would not fit the screen there. In screen pixels.
        var offset = geometry.Offset(deviceTurns);
        offset = new Point(offset.X * dpi, offset.Y * dpi);
        var finalSize = view.SizeFor(deviceTurns, entering, drawn * growth);
        var closedFrom = closed ? new Point(center.X + offset.X, center.Y + offset.Y) : center;
        Point closedTo, finalCenter;
        if (closed)
        {
            finalCenter = FittedCenter(finalSize, closedFrom, screen);
            closedTo = finalCenter;
        }
        else
        {
            // Unfolded at the new size, the open device's centre is that much further from the closed one's.
            var grown = new Point(offset.X * growth, offset.Y * growth);
            Point? unmoved = unfoldsTo is { } back && Math.Abs(back.Closed.X - center.X) < 2 && Math.Abs(back.Closed.Y - center.Y) < 2 ? back.Open : null;
            finalCenter = FittedCenter(finalSize, unmoved ?? new Point(center.X - grown.X, center.Y - grown.Y), screen);
            closedTo = new Point(finalCenter.X + grown.X, finalCenter.Y + grown.Y);
        }
        // The window ends on whole pixels; the animation ends exactly where the live view then shows the device,
        // which is centred across the box, `BottomRoom` above its bottom.
        var finalBox = BoxFrame(finalSize, finalCenter, screen);
        var landed = new Point(finalBox.MidX, finalBox.Bottom - (DeviceShell.BottomRoom + finalSize.Height / 2) * dpi);
        var closedEnd = closed ? landed : new Point(landed.X + (closedTo.X - finalCenter.X), landed.Y + (closedTo.Y - finalCenter.Y));

        // The pictures take a while to draw, so they are drawn on another thread; the window's resizing and the
        // fold start together once they are ready. Meanwhile the live view keeps its last frame.
        view.FreezesFrames = true;
        folding = true;
        foldStartedAt = DateTime.UtcNow;
        screenOn = null;
        _ = Task.Delay(UnannouncedTime).ContinueWith(_ => queue.TryEnqueue(() => ShowLiveFold(isClosed)));
        var id = ++foldId;
        var turns = deviceTurns;
        var scale = dpi * Math.Max(1, growth);
        pictureScale = scale;
        var leaving = new DeviceThumbnail((byte[])lastPixels.Clone(), lastSize.Width, lastSize.Height, lastRotation, null);
        var open = openSkin;
        _ = Task.Run(() =>
        {
            // The display entered is dark, as a real one is until it turns on, until its first live frame lights it
            // up: a frame from the last time it was on would show what the device showed then.
            var openPicture = FoldAnimation.MakePicture(open, closed ? leaving : null, geometry.Open, scale);
            var closedPicture = FoldAnimation.MakePicture(closedSkin, closed ? null : leaving, geometry.Closed, scale);
            queue.TryEnqueue(() =>
            {
                if (foldId != id || this.view is not { } view || Window is not { } window) return;
                view.SetSkin(entering);
                view.FreezesFrames = false;
                if (openPicture == null || closedPicture == null)
                {
                    folding = false;
                    ReshapeWindow(animate: false);
                    return;
                }
                view.Visibility = Visibility.Collapsed;
                foldAnimation?.Finish();
                FoldAnimation? animation = null;
                animation = new FoldAnimation(closed, geometry, openPicture, closedPicture, angle, (closedFrom, closedEnd), growth, turns, window, () =>
                {
                    if (animation == null || foldAnimation != animation) return;
                    void End()
                    {
                        if (foldAnimation != animation) return;
                        foldEndWaiting = null;
                        foldAnimation = null;
                        folding = false;
                        foldStartedAt = null;
                        screenOn = null;
                        FoldEnded(turns, landed, closed ? 0 : angle);
                        unfoldsTo = closed && DeviceCenterOnScreen is { } now ? (now, center) : null;
                        animation.Finish();
                    }
                    // The display entered lights up in the animation's last picture, which stays until it has (or a
                    // little while, for a device that goes to sleep as it folds), so that the live view takes over
                    // from a lit display.
                    if (animation.ShowsLiveTarget)
                    {
                        End();
                    }
                    else
                    {
                        foldEndWaiting = End;
                        _ = Task.Delay(1500).ContinueWith(_ => queue.TryEnqueue(() =>
                        {
                            if (!animation.HasLiveTarget) End();
                        }));
                    }
                });
                foldAnimation = animation;
                // The window, and with it the bars, takes the new state's size along with the fold.
                window.SetBox(finalBox, FoldAnimation.FoldDuration, animation.Start);
            });
        });
    }

    /// Shows the live device again where and as the animation left it: held at the turn the emulator was asked
    /// for, which frames confirm shortly if they have not yet, and open at `angle`, then as the posture is now,
    /// which may have changed meanwhile.
    private void FoldEnded(int turns, Point center, double angle)
    {
        if (view == null) return;
        Window?.StopBoxAnimation();
        if (turns != deviceTurns)
        {
            deviceTurns = turns;
            view.SetQuarterTurns(turns, animated: false);
        }
        PlaceDevice(center);
        if (!isClosed)
        {
            view.SetFold(angle, Hinge, animated: false);
            if (OpenAngle != angle) view.SetFold(OpenAngle, Hinge, animated: true);
        }
        view.Visibility = Visibility.Visible;
        if (lastRotation != deviceTurns) DeviceTurned(lastRotation);
    }

    /// Where a device of `device` DIPs centred at `center` (screen pixels) ends up once its window is kept on `screen`.
    private Point FittedCenter(Size device, Point center, Screen screen)
    {
        var box = BoxPixels(device, screen.Scale);
        var frame = new PixelRect(center.X - box.Width / 2, center.Y + device.Height * screen.Scale / 2 + DeviceShell.BottomRoom * screen.Scale - box.Height,
                                  box.Width, box.Height);
        var fitted = DeviceWindow.Fitted(frame, screen);
        return new Point(center.X + fitted.X - frame.X, center.Y + fitted.Y - frame.Y);
    }

    /// While folding, and after until the animation ends, the first settled live frame of the state being entered
    /// lights up its dark display in the animation; drawn elsewhere, like the pictures, so the window's resizing is
    /// not held up.
    private void ShowLiveFold(bool folded)
    {
        if (foldAnimation is not { } animation || animation.HasLiveTarget || liveTargetDrawing || animation.Closing != folded
            || lastPixels == null || view == null || IsBlank(lastPixels, lastSize.Width, lastSize.Height)
            || !LiveFrameSettled(lastPixels, lastSize.Width, lastSize.Height)) return;
        liveTargetDrawing = true;
        var target = folded ? closedSkin ?? openSkin : openSkin;
        var size = view.SizeFor(0, target, view.DrawnScale);
        var scale = pictureScale;
        var screen = new DeviceThumbnail((byte[])lastPixels.Clone(), lastSize.Width, lastSize.Height, lastRotation, null);
        _ = Task.Run(() =>
        {
            var picture = FoldAnimation.MakePicture(target, screen, size, scale);
            queue.TryEnqueue(() =>
            {
                liveTargetDrawing = false;
                if (picture != null && foldAnimation == animation) animation.ShowLiveTarget(picture.Screen, () => foldEndWaiting?.Invoke());
            });
        });
    }

    /// The device's centre on screen, in pixels.
    private Point? DeviceCenterOnScreen =>
        Window is { } window && shell != null ? window.ToScreen(shell.DeviceFrame) is var frame ? new Point(frame.MidX, frame.MidY) : null : null;

    /// Moves and sizes the window so the device's centre is at `center` (screen pixels), keeping it on screen.
    private void PlaceDevice(Point center)
    {
        if (Window is not { } window || view == null) return;
        var screen = CurrentScreen;
        var scale = ShownScale(deviceTurns, screen);
        if (scale != view.DrawnScale)
        {
            view.SetLiveScale(scale);
            view.CommitScale();
        }
        window.SetBox(BoxFrame(view.SizeFor(deviceTurns), center, screen));
        CommitSize();
    }

    /// The box for a device of `device` DIPs centred at `center` (screen pixels), kept on `screen`.
    private PixelRect BoxFrame(Size device, Point center, Screen screen)
    {
        var box = BoxPixels(device, screen.Scale);
        // The device sits `BottomRoom` above the box's bottom edge, centred across it.
        var bottom = center.Y + (device.Height / 2 + DeviceShell.BottomRoom) * screen.Scale;
        var frame = new PixelRect(Math.Round(center.X - box.Width / 2), Math.Round(bottom - box.Height), box.Width, box.Height);
        return DeviceWindow.Fitted(frame, screen);
    }

    // Screenshot

    private void TakeScreenshot()
    {
        if (client == null) return;
        view?.Flash();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(desktop, $"{Avd.Id}_{DateTime.Now:yyyy-MM-dd_HH.mm.ss}.png");
        _ = Task.Run(async () =>
        {
            try
            {
                await File.WriteAllBytesAsync(path, await client.ScreenshotPng());
                Log.Info($"screenshot saved to {path}");
                queue.TryEnqueue(() => chrome?.ShowToast("截图已存到桌面", () =>
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })));
            }
            catch (Exception error)
            {
                Log.Error($"screenshot failed: {error.Message}");
            }
        });
    }

    /// Puts the emulator's screen on the Windows clipboard.
    private async void CopyScreenshot()
    {
        if (client == null) return;
        view?.Flash();
        try
        {
            await PhysicalDeviceStore.CopyToClipboard(await client.ScreenshotPng());
            Log.Info("screenshot copied to the clipboard");
            chrome?.ShowToast("截图已拷贝到剪贴板", () => { });
        }
        catch (Exception error)
        {
            Log.Error($"screenshot copy failed: {error.Message}");
        }
    }

    private void Fail(Exception error)
    {
        Log.Error($"{Avd.Id}: {error}");
        Dialogs.Alert(Window?.Hwnd ?? 0, $"“{Avd.Name}”出错了", error.Message);
        Close();
    }
}

/// Logs the frame rate every few seconds so streaming throughput is visible in the terminal.
sealed class FrameCounter
{
    private readonly object gate = new();
    private int frames;
    /// Frames the emulator produced but never sent, judging by gaps in `seq`.
    private int skipped;
    /// Frames received while the previous one was still waiting to be shown.
    private int dropped;
    private uint? lastSeq;
    private DateTime windowStart = DateTime.UtcNow;
    private (int Width, int Height, int Rotation)? lastShape;

    /// Logs each change of the frames' size or reported rotation.
    public void NoteShape(RawFrame frame)
    {
        if (!Log.IsDebug) return;
        lock (gate)
        {
            var shape = (frame.Width, frame.Height, frame.Rotation);
            if (lastShape == shape) return;
            lastShape = shape;
            Log.Debug($"frames now {frame.Width}x{frame.Height}, rotation {frame.Rotation}");
        }
    }

    public void Tick(RawFrame frame, bool shown)
    {
        if (!Log.IsDebug) return;
        lock (gate)
        {
            frames++;
            if (lastSeq is { } last && frame.Seq > last + 1) skipped += (int)(frame.Seq - last - 1);
            lastSeq = frame.Seq;
            if (!shown) dropped++;
            var elapsed = (DateTime.UtcNow - windowStart).TotalSeconds;
            if (elapsed < 5) return;
            var latency = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - frame.TimestampUs / 1000.0;
            Log.Debug($"{frames / elapsed:0.0} fps at {frame.Width}x{frame.Height}, {skipped} skipped by emulator, {dropped} dropped, latency {latency:0} ms");
            frames = skipped = dropped = 0;
            windowStart = DateTime.UtcNow;
        }
    }
}
