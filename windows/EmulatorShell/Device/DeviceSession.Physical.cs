using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace EmulatorShell;

/// What a physical device's window needs of its stream and of the store that owns it.
sealed class PhysicalLink(string id, ScrcpyStream stream, PhysicalDeviceStore store, DeviceInfo info, bool isWireless)
{
    public string Id { get; } = id;
    public ScrcpyStream Stream { get; set; } = stream;
    public PhysicalDeviceStore Store { get; } = store;
    public DeviceInfo Info { get; } = info;
    public bool IsWireless { get; } = isWireless;
    public PhysicalControls? Controls { get; set; }
    /// The device refuses input from adb (see `DeviceInfo.InputBlocked`), and when that was last said.
    public bool InputBlocked { get; set; } = info.InputBlocked;
    public DateTime LastInputWarning { get; set; } = DateTime.MinValue;
    /// A finger is down on the screen; with Alt held as it went down, a pinch.
    public bool Touching { get; set; }
    public bool Pinching { get; set; }
    /// Whether the device was asked which way round it is held since the video last changed size.
    public bool RotationChecked { get; set; }
}

/// A physical device mirrored through scrcpy, in the same kind of window as an emulator: a plain bezel around a
/// display of the size adb reports, which the window sizes use, the physical size from the panel's real dpi.
sealed partial class DeviceSession
{
    private PhysicalLink? physical;
    public bool IsPhysical => physical != null;
    private DeviceAudio? audio;
    // A physical device's latest frame: written on the stream's thread, shown on this one.
    private readonly object physicalFrameLock = new();
    private byte[] physicalPending = [];
    private byte[]? physicalShown;
    private (int Width, int Height) physicalPendingSize, physicalShownSize;
    private bool physicalScheduled;
    /// The counterclockwise quarter turns the device is held at, as its frames and adb say.
    private int physicalTurns;

    public static async Task<DeviceSession> Mirror(PhysicalDevice device, DeviceInfo info, ScrcpyStream stream, PhysicalDeviceStore store, int cascade)
    {
        var density = info.Xdpi is { } xdpi ? (int)Math.Round(xdpi) : info.Density ?? 420;
        var avd = new Avd(device.Id, Path.GetTempPath(), new Dictionary<string, string>
        {
            ["avd.ini.displayname"] = device.Name,
            ["hw.lcd.width"] = info.Width.ToString(),
            ["hw.lcd.height"] = info.Height.ToString(),
            ["hw.lcd.density"] = density.ToString(),
        });
        var skin = await DeviceSkin.Load(avd);
        var session = new DeviceSession(avd, 0, skin, null)
        {
            physical = new PhysicalLink(device.Id, stream, store, info, device.IsWireless),
        };
        session.ShowWindow(cascade);
        session.SetUpPhysicalWindow();
        session.ConnectPhysical(stream);
        return session;
    }

    /// The window's parts that do not change with the stream: the "More" panel, scrolling and dropped files.
    private void SetUpPhysicalWindow()
    {
        if (physical is not { } link || view == null || chrome == null) return;
        var controls = new PhysicalControls(link.Id, Avd.Name, link.Info, link.IsWireless, link.Store, () => Window?.Hwnd ?? 0);
        link.Controls = controls;
        chrome.PanelProvider = () => new PhysicalMorePanel(controls);
        view.Scroll += (point, dx, dy) =>
        {
            if (VideoPoint(point) is { } video) physical?.Stream.InjectScroll(video, dx, dy);
        };
        view.FilesDropped = paths =>
        {
            link.Store.Send(paths, link.Id);
            ShowToast(paths.Any(p => p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) ? "正在安装…" : "正在传到手机…", null);
        };
    }

    /// Shows the stream's frames and plays its sound.
    private void ConnectPhysical(ScrcpyStream stream)
    {
        if (physical == null) return;
        physical.Stream = stream;
        stream.AddFrameSink(this, ReceivePhysical);
        var player = audio ??= new DeviceAudio();
        stream.AddAudioSink(this, (samples, _) => player.Play(samples));
        PhysicalStreamChanged(stream.CurrentState);
    }

    /// The store started a new stream for this window (the device came back over another connection).
    public void PhysicalStreamReplaced(ScrcpyStream stream)
    {
        physical?.Stream.RemoveSinks(this);
        ConnectPhysical(stream);
    }

    public void PhysicalStreamChanged(ScrcpyStream.State streamState)
    {
        switch (streamState.Phase)
        {
            case ScrcpyStream.Phase.Connecting:
                if (physicalShown != null) chrome?.ShowOverlay("正在重新连接…");
                break;
            case ScrcpyStream.Phase.Streaming:
                chrome?.ShowOverlay(null);
                chrome?.SetReady(true);
                if (State == SessionState.Booting)
                {
                    State = SessionState.Running;
                    RunTestScript();
                }
                break;
            case ScrcpyStream.Phase.Ended:
                if (State == SessionState.Stopped || physical is not { } link) return;
                const string reconnects = "重新插上数据线，或回到同一个 Wi-Fi，会自动继续。";
                chrome?.ShowOverlay(streamState.Message == null || link.Store.Device(link.Id)?.IsOnline == false
                                    ? $"设备已断开\n{reconnects}" : $"投屏中断了\n{streamState.Message}");
                break;
        }
    }

    public void PhysicalVideoSizeChanged(Size size)
    {
        Log.Debug($"video is now {(int)size.Width} × {(int)size.Height}");
        if (physical != null) physical.RotationChecked = false;
    }

    public void PhysicalAudioChanged(bool available)
    {
        if (physical?.Controls is { } controls) controls.AudioAvailable = available;
    }

    public void SetRecording(DateTime? start) => chrome?.SetRecording(start);

    /// The device refused an input event: the window says what to turn on, from now on at each touch.
    public void PhysicalInputRefused()
    {
        if (physical is not { } link) return;
        link.InputBlocked = true;
        link.LastInputWarning = DateTime.MinValue;
        WarnIfInputBlocked();
    }

    public void ShowToast(string text, string? path) =>
        chrome?.ShowToast(text, () =>
        {
            if (path != null) PhysicalDeviceStore.Reveal(path);
        });

    /// On the stream's thread: keeps the newest frame and has the UI thread show it, dropping any it has not got to.
    private void ReceivePhysical(VideoFrame frame)
    {
        var length = frame.Width * frame.Height * 4;
        bool scheduled;
        lock (physicalFrameLock)
        {
            if (physicalPending.Length != length) physicalPending = new byte[length];
            Buffer.BlockCopy(frame.Pixels, 0, physicalPending, 0, length);
            physicalPendingSize = (frame.Width, frame.Height);
            scheduled = physicalScheduled;
            physicalScheduled = true;
        }
        if (!scheduled) queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, ShowPhysicalFrame);
    }

    /// A decoded frame, upright as the device shows it. Its shape tells whether the device is held the way its
    /// display is long or across; which way round it is held, adb tells.
    private void ShowPhysicalFrame()
    {
        byte[] pixels;
        (int Width, int Height) size;
        lock (physicalFrameLock)
        {
            physicalScheduled = false;
            pixels = physicalPending;
            size = physicalPendingSize;
            // The next frame is written into another buffer; this one is kept for turning it again.
            physicalPending = physicalShown?.Length == pixels.Length ? physicalShown : new byte[pixels.Length];
        }
        if (view == null || State == SessionState.Stopped || physical == null) return;
        physicalShown = pixels;
        physicalShownSize = size;
        var natural = openSkin.Display;
        var across = size.Width != size.Height && (size.Width > size.Height) != (natural.Width > natural.Height);
        var turns = physicalTurns;
        if (across != (turns % 2 == 1))
        {
            turns = across ? 1 : 0;
            RefinePhysicalRotation(across);
        }
        else if (!physical.RotationChecked)
        {
            // A window opened the way it was last held, or a new session: which way round is not known yet.
            RefinePhysicalRotation(across);
        }
        physicalTurns = turns;
        view.Show(pixels, size.Width, size.Height, turns, bgra: true);
        DeviceTurned(turns);
    }

    /// Asks the device which way round it is held (90° or 270°, upright or upside down), which the frames' shape
    /// does not say.
    private async void RefinePhysicalRotation(bool across)
    {
        if (physical is not { } link) return;
        link.RotationChecked = true;
        try
        {
            // The main display's viewport (newer Android), or the touchscreen's orientation (older).
            const string command = "dumpsys input | grep -E 'displayId=0,|SurfaceOrientation' | grep -m1 -o -E 'orientation=[0-3]|SurfaceOrientation: [0-3]'";
            var output = await Adb.Shell(command, link.Stream.Serial);
            if (Regex.Match(output, @"(?:SurfaceOrientation: |orientation=)([0-3])") is not { Success: true } match) return;
            var turns = int.Parse(match.Groups[1].Value);
            if (turns % 2 == 1 != across || physicalTurns % 2 != turns % 2 || physicalTurns == turns || State == SessionState.Stopped) return;
            Log.Debug($"device reports rotation {turns}");
            physicalTurns = turns;
            if (physicalShown != null) view?.Show(physicalShown, physicalShownSize.Width, physicalShownSize.Height, turns, bgra: true);
            DeviceTurned(turns);
        }
        catch (Exception error)
        {
            Log.Debug($"cannot read the device's rotation: {error.Message}");
        }
    }

    private void PhysicalTouch(Point point, bool pressed)
    {
        if (physical is not { } link || VideoPoint(point) is not { } video) return;
        var stream = link.Stream;
        var action = !pressed ? ScrcpyStream.TouchAction.Up : link.Touching ? ScrcpyStream.TouchAction.Move : ScrcpyStream.TouchAction.Down;
        if (action != ScrcpyStream.TouchAction.Move) Log.Debug($"touch {action} at {(int)video.X},{(int)video.Y} of {stream.VideoSize}");
        if (action == ScrcpyStream.TouchAction.Down)
        {
            WarnIfInputBlocked();
            // Alt-drag pinches: a second finger mirrors the first about the screen's centre.
            link.Pinching = IsDown(VirtualKey.Menu);
        }
        link.Touching = pressed;
        stream.InjectTouch(action, video);
        if (link.Pinching)
        {
            var size = stream.VideoSize;
            stream.InjectTouch(action, new Point(size.Width - 1 - video.X, size.Height - 1 - video.Y), ScrcpyStream.VirtualFingerPointer);
        }
    }

    /// A point on the display in its natural orientation's pixels (as the view reports touches), in the pixels of
    /// the video as the device now shows it.
    private Point? VideoPoint(Point point)
    {
        if (physical?.Stream.VideoSize is not { Width: > 0 } video) return null;
        double w = openSkin.Display.Width, h = openSkin.Display.Height;
        var turns = (physicalTurns % 4 + 4) % 4;
        var (logical, size) = turns switch
        {
            1 => (new Point(point.Y, w - point.X), new Size(h, w)),
            2 => (new Point(w - point.X, h - point.Y), new Size(w, h)),
            3 => (new Point(h - point.Y, point.X), new Size(h, w)),
            _ => (point, new Size(w, h)),
        };
        return new Point(Math.Clamp(logical.X * video.Width / size.Width, 0, video.Width - 1),
                         Math.Clamp(logical.Y * video.Height / size.Height, 0, video.Height - 1));
    }

    /// On a Xiaomi device whose developer options block input from adb, says what to turn on; it is read again
    /// each time, so the warning stops once the setting is on.
    private async void WarnIfInputBlocked()
    {
        if (physical is not { InputBlocked: true } link || DateTime.Now - link.LastInputWarning < TimeSpan.FromSeconds(8)) return;
        link.LastInputWarning = DateTime.Now;
        try
        {
            var value = await Adb.Shell("getprop persist.security.adbinput", link.Stream.Serial);
            if (value.Trim() == "1") link.InputBlocked = false;
            else chrome?.ShowToast("这台手机不接受电脑的操作：在开发者选项里打开“USB 调试（安全设置）”", () => { });
        }
        catch (Exception error)
        {
            Log.Debug($"cannot read persist.security.adbinput: {error.Message}");
        }
    }

    /// A key that types nothing (arrows, Enter, Backspace…) as the emulator names it.
    private void PhysicalKey(string key)
    {
        WarnIfInputBlocked();
        if (AndroidKey.Code(key) is { } code) TapKey(code); else TypeOnDevice(key);
    }

    /// Text the device's keyboard can type goes as keys; anything else (Chinese, emoji) is pasted.
    private void TypeOnDevice(string text)
    {
        WarnIfInputBlocked();
        if (physical?.Stream is not { } stream) return;
        if (text.All(char.IsAscii))
        {
            stream.InjectText(text);
            return;
        }
        physical!.Store.NoteSentToDevice(text);
        stream.SetClipboard(text, paste: true);
    }

    private void TapKey(int code)
    {
        physical?.Stream.InjectKey(code, ScrcpyStream.KeyAction.Down);
        physical?.Stream.InjectKey(code, ScrcpyStream.KeyAction.Up);
    }

    /// Ctrl+V: what is on the Windows clipboard is pasted on the device.
    private async void PastePhysical()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            physical?.Store.NoteSentToDevice(text);
            physical?.Stream.SetClipboard(text, paste: true);
        }
        catch (Exception error)
        {
            Log.Debug($"cannot read the clipboard: {error.Message}");
        }
    }

    private void PhysicalScreenshot()
    {
        if (physical is not { } link) return;
        view?.Flash();
        link.Store.Screenshot(link.Id, (path, error) =>
            ShowToast(path != null ? "截图已存到桌面" : $"截图失败：{error}", path));
    }

    private void PhysicalCopyScreenshot()
    {
        if (physical is not { } link) return;
        view?.Flash();
        link.Store.CopyScreenshot(link.Id, error => ShowToast(error == null ? "截图已拷贝到剪贴板" : $"截图失败：{error}", null));
    }

    private void TearDownPhysical()
    {
        physical?.Stream.RemoveSinks(this);
        audio?.Dispose();
        audio = null;
        physicalShown = null;
    }
}
