using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace EmulatorShell;

enum PhysicalState
{
    /// Authorized and usable.
    Online,
    /// Plugged in, waiting for "Allow USB debugging" on the device.
    Unauthorized,
    /// adb is still setting up the connection.
    Connecting,
    /// A paired Wi-Fi device that is not connected, or a USB device adb reports offline.
    Offline,
    /// Windows may not open the USB device (adb's "no permissions").
    NoPermissions,
}

/// A phone or tablet connected over USB or Wi-Fi, as the device manager lists it.
sealed record PhysicalDevice(
    /// "device:" and the device's serial number (`ro.serialno`), or adb's serial until that is known.
    string Id,
    /// The adb serial to use: the USB one when the device is connected both ways; null while offline.
    string? Serial,
    /// Every adb serial this device is connected by.
    IReadOnlyList<string> Serials,
    PhysicalState State,
    bool IsWireless,
    string Name,
    DeviceInfo? Info,
    /// For a paired Wi-Fi device: where it was last reached, and when.
    string? Address = null,
    DateTime? LastSeen = null,
    DateTime? PairedAt = null)
{
    public bool IsOnline => State == PhysicalState.Online && Serial != null;

    public bool Equals(PhysicalDevice? other) =>
        other != null && other.Id == Id && other.Serial == Serial && other.Serials.SequenceEqual(Serials) && other.State == State
        && other.IsWireless == IsWireless && other.Name == Name && Equals(other.Info, Info) && other.Address == Address
        && other.LastSeen == LastSeen && other.PairedAt == PairedAt;

    public override int GetHashCode() => HashCode.Combine(Id, State, Name);
}

/// "截图已存到桌面" and the like, shown for a few seconds; `Path` is the file it is about.
sealed record PhysicalToast(string Text, string? Path = null, bool IsError = false)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// Physical devices: following adb's device list, reading each device's details, and the streams, mirror
/// windows and recordings that use scrcpy-server. Lives on the UI thread; `Changed` says something shown changed.
sealed partial class PhysicalDeviceStore
{
    public List<PhysicalDevice> Devices { get; private set; } = [];
    public bool AdbInstalled { get; private set; } = Adb.IsInstalled;
    /// Installing platform-tools, 0…1.
    public double? InstallProgress { get; private set; }
    /// When each device's recording started, by device id.
    public Dictionary<string, DateTime> Recordings { get; } = [];
    /// The latest picture of each device's screen, for the detail pane's preview.
    public Dictionary<string, VideoFrame> Thumbnails { get; } = [];
    /// Devices whose screen is turned off while mirroring.
    public HashSet<string> ScreensOff { get; } = [];
    /// Devices with a stream whose sound reaches this PC.
    public HashSet<string> AudioDevices { get; } = [];
    /// A device being switched to Wi-Fi, reconnected or the like, by id.
    public HashSet<string> Busy { get; } = [];
    /// Devices whose stream reported that they refuse input from adb.
    public HashSet<string> InputRefused { get; } = [];
    private PhysicalToast? toast;
    public PhysicalToast? Toast
    {
        get => toast;
        set
        {
            toast = value;
            Changed?.Invoke();
        }
    }

    public event Action? Changed;
    /// A device's preview picture changed.
    public event Action<string>? ThumbnailChanged;
    /// Shown as the manager's alert.
    public event Action<string>? Error;

    public IReadOnlyDictionary<string, DeviceSession> Sessions => sessions;
    private readonly Dictionary<string, DeviceSession> sessions = [];
    private readonly Dictionary<string, ScrcpyStream> streams = [];
    private readonly Dictionary<string, RecorderBox> recorders = [];
    /// Times a stream has failed in a row, so a broken one is not restarted forever.
    private readonly Dictionary<string, int> failures = [];
    private readonly HashSet<string> opening = [];
    private List<AdbDevice> adbDevices = [];
    private readonly Dictionary<string, DeviceInfo> infos = [];
    private readonly HashSet<string> fetching = [];
    private AdbDeviceTracker? tracker;
    private readonly DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
    private DispatcherQueueTimer? clipboardTimer;
    private uint clipboardSequence;

    private const string PairedKey = "pairedDevices";
    /// `EMULATOR_SHELL_MIRROR_EMULATORS=1` lists emulators here too (scrcpy works with them), for trying the
    /// physical-device features without a phone.
    private static readonly bool ListsEmulators = Environment.GetEnvironmentVariable("EMULATOR_SHELL_MIRROR_EMULATORS") == "1";

    // Device list

    /// Starts following adb's device list, once something shows it (the manager, the notification area's menu):
    /// a copy of the app opened for one emulator has no use for it.
    public void StartTracking()
    {
        AdbInstalled = Adb.IsInstalled;
        if (!AdbInstalled || tracker != null) return;
        tracker = new AdbDeviceTracker(devices => queue.TryEnqueue(() => AdbChanged(devices)));
        tracker.Start();
        Rebuild();
    }

    public void StopTracking()
    {
        tracker?.Stop();
        tracker = null;
    }

    private void AdbChanged(List<AdbDevice> list)
    {
        Log.Debug($"adb devices: {string.Join(", ", list.Select(d => $"{d.Serial} {d.State}"))}");
        adbDevices = list.Where(d => !d.IsEmulator || ListsEmulators).ToList();
        var online = adbDevices.Where(d => d.State == "device").Select(d => d.Serial).ToHashSet();
        foreach (var serial in infos.Keys.Where(k => !online.Contains(k)).ToList()) infos.Remove(serial);
        foreach (var device in adbDevices.Where(d => d.State == "device" && !infos.ContainsKey(d.Serial))) FetchInfo(device.Serial);
        Rebuild();
    }

    private async void FetchInfo(string serial)
    {
        if (!fetching.Add(serial)) return;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var info = await DeviceInfo.Read(serial);
                    if (!adbDevices.Any(d => d.Serial == serial && d.State == "device")) return;
                    infos[serial] = info;
                    Rebuild();
                    return;
                }
                catch (Exception error)
                {
                    Log.Error($"cannot read {serial}'s details (attempt {attempt + 1}): {error.Message}");
                    await Task.Delay(1000);
                }
            }
        }
        finally
        {
            fetching.Remove(serial);
        }
    }

    /// Reads a device's details again, e.g. its battery while its detail pane is shown.
    public async Task RefreshInfo(string id)
    {
        if (Device(id)?.Serial is not { } serial) return;
        try
        {
            infos[serial] = await DeviceInfo.Read(serial);
            Rebuild();
        }
        catch (Exception error)
        {
            Log.Debug($"cannot refresh {serial}: {error.Message}");
        }
    }

    public PhysicalDevice? Device(string? id) => Devices.FirstOrDefault(d => d.Id == id);

    /// One row per device: a device connected over both USB and Wi-Fi is the same serial number twice.
    private void Rebuild()
    {
        // Only a Wi-Fi connection joins a USB one of the same serial number: some cheap devices share one
        // ("0123456789ABCDEF"), and two cables are two devices.
        var groups = new Dictionary<string, List<AdbDevice>>();
        var order = new List<string>();
        var cabled = new Dictionary<string, string>();
        foreach (var device in adbDevices.OrderBy(d => d.IsWireless))
        {
            var serialNumber = infos.GetValueOrDefault(device.Serial)?.SerialNumber;
            string key;
            if (!device.IsWireless)
            {
                key = device.Serial;
                if (serialNumber != null) cabled.TryAdd(serialNumber, key);
            }
            else
            {
                key = (serialNumber != null ? cabled.GetValueOrDefault(serialNumber) : null) ?? serialNumber ?? device.Serial;
            }
            if (!groups.ContainsKey(key))
            {
                order.Add(key);
                groups[key] = [];
            }
            groups[key].Add(device);
        }
        var paired = PairedDevices;
        var pairedChanged = false;
        var list = new List<PhysicalDevice>();
        foreach (var key in order)
        {
            var members = groups[key];
            var usable = members.Where(m => m.State == "device").ToList();
            var primary = usable.FirstOrDefault(m => !m.IsWireless) ?? usable.FirstOrDefault() ?? members[0];
            var info = infos.GetValueOrDefault(primary.Serial);
            var state = primary.State switch
            {
                "device" => info == null ? PhysicalState.Connecting : PhysicalState.Online,
                "unauthorized" => PhysicalState.Unauthorized,
                "authorizing" or "connecting" => PhysicalState.Connecting,
                "no permissions" => PhysicalState.NoPermissions,
                _ => PhysicalState.Offline,
            };
            var name = info?.MarketName ?? (state == PhysicalState.Unauthorized ? "未授权的设备" : primary.Model ?? primary.Serial);
            string? address = null;
            DateTime? pairedAt = null;
            if (usable.FirstOrDefault(m => m.IsWireless) is { } wireless && info?.SerialNumber is { } wirelessSerial)
            {
                // Remembered so the device stays listed while it is away, and can be reached again.
                var entry = paired[wirelessSerial] as JsonObject ?? new JsonObject();
                entry["name"] = name;
                if (wireless.Serial.Contains(':')) entry["address"] = wireless.Serial;
                entry["lastSeen"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                entry["pairedAt"] ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                paired[wirelessSerial] = entry;
                pairedChanged = true;
            }
            if (info?.SerialNumber is { } known && paired[known] is JsonObject remembered)
            {
                address = remembered["address"]?.GetValue<string>();
                pairedAt = Time(remembered["pairedAt"]);
            }
            list.Add(new PhysicalDevice("device:" + key, primary.State == "device" ? primary.Serial : null, members.Select(m => m.Serial).ToList(),
                                        state, primary.IsWireless, name, info, address, null, pairedAt));
        }
        var present = list.Select(d => d.Info?.SerialNumber).Where(s => s != null).Concat(order).ToHashSet();
        foreach (var (serialNumber, node) in paired)
        {
            if (present.Contains(serialNumber) || node is not JsonObject entry) continue;
            list.Add(new PhysicalDevice("device:" + serialNumber, null, [], PhysicalState.Offline, true,
                                        entry["name"]?.GetValue<string>() ?? serialNumber, null, entry["address"]?.GetValue<string>(),
                                        Time(entry["lastSeen"]), Time(entry["pairedAt"])));
        }
        if (pairedChanged) PairedDevices = paired;
        static int Rank(PhysicalDevice device) => device.State switch
        {
            PhysicalState.Online => 0,
            PhysicalState.Offline => 2,
            _ => 1,
        };
        list = [.. list.OrderBy(Rank).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
        var changed = !list.SequenceEqual(Devices);
        Devices = list;
        ResumeStreams();
        if (changed) Changed?.Invoke();
    }

    private static DateTime? Time(JsonNode? node) =>
        node?.GetValue<long>() is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime : null;

    private static JsonObject PairedDevices
    {
        get => Settings.Get(PairedKey)?.DeepClone() as JsonObject ?? new JsonObject();
        set => Settings.Set(PairedKey, value);
    }

    // Options

    public ScrcpyStream.Options OptionsFor(string id)
    {
        var options = new ScrcpyStream.Options();
        if (Settings.Get($"mirror.{id}") is JsonObject saved)
        {
            options = options with
            {
                MaxSize = saved["maxSize"]?.GetValue<int>() ?? options.MaxSize,
                BitRate = saved["bitRate"]?.GetValue<int>() ?? options.BitRate,
                Audio = saved["audio"]?.GetValue<bool>() ?? options.Audio,
                StayAwake = saved["stayAwake"]?.GetValue<bool>() ?? options.StayAwake,
                ShowTouches = saved["showTouches"]?.GetValue<bool>() ?? options.ShowTouches,
                ClipboardSync = saved["clipboardSync"]?.GetValue<bool>() ?? options.ClipboardSync,
            };
        }
        // Sound needs Android 11; asking an older device only opens a socket it reports disabled on.
        if ((Device(id)?.Info?.Sdk ?? 0) < 30) options = options with { Audio = false };
        return options;
    }

    /// Saves the options and reconnects a running stream with them.
    public void SetOptions(ScrcpyStream.Options options, string id)
    {
        Settings.Set($"mirror.{id}", new JsonObject
        {
            ["maxSize"] = options.MaxSize, ["bitRate"] = options.BitRate, ["audio"] = options.Audio,
            ["stayAwake"] = options.StayAwake, ["showTouches"] = options.ShowTouches, ["clipboardSync"] = options.ClipboardSync,
        });
        UpdateClipboardTimer();
        if (!streams.TryGetValue(id, out var stream)) return;
        var effective = OptionsFor(id);
        if (effective != stream.Settings)
        {
            ScreensOff.Remove(id);
            stream.Restart(effective);
        }
        Changed?.Invoke();
    }

    // Streams

    /// The device's stream, started if it was not running.
    private ScrcpyStream? StreamFor(string id)
    {
        if (streams.TryGetValue(id, out var running)) return running;
        if (Device(id)?.Serial is not { } serial) return null;
        var stream = new ScrcpyStream(serial, OptionsFor(id));
        stream.StateChanged += state => StreamStateChanged(id, state);
        stream.VideoSizeChanged += size => sessions.GetValueOrDefault(id)?.PhysicalVideoSizeChanged(size);
        stream.AudioAvailableChanged += available =>
        {
            if (available) AudioDevices.Add(id); else AudioDevices.Remove(id);
            sessions.GetValueOrDefault(id)?.PhysicalAudioChanged(available);
        };
        stream.Clipboard += text => DeviceCopied(text, id);
        stream.InputRefused += () =>
        {
            if (!InputRefused.Add(id)) return;
            sessions.GetValueOrDefault(id)?.PhysicalInputRefused();
            Changed?.Invoke();
        };
        streams[id] = stream;
        stream.Start();
        UpdateClipboardTimer();
        return stream;
    }

    private void StreamStateChanged(string id, ScrcpyStream.State state)
    {
        sessions.GetValueOrDefault(id)?.PhysicalStreamChanged(state);
        switch (state.Phase)
        {
            case ScrcpyStream.Phase.Streaming:
                failures.Remove(id);
                break;
            case ScrcpyStream.Phase.Ended:
                ScreensOff.Remove(id);
                AudioDevices.Remove(id);
                if (recorders.ContainsKey(id)) _ = StopRecording(id, "设备断开了");
                if (!sessions.ContainsKey(id))
                {
                    ReleaseStream(id);
                    return;
                }
                // The window stays and waits for the device to come back; a stream that fails while the device is
                // still there is tried again a couple of times.
                var failed = failures.GetValueOrDefault(id) + 1;
                failures[id] = failed;
                if (state.Message != null && failed <= 2 && Device(id)?.IsOnline == true)
                {
                    _ = Task.Delay(TimeSpan.FromSeconds(failed)).ContinueWith(_ => queue.TryEnqueue(() =>
                    {
                        if (sessions.ContainsKey(id) && streams.GetValueOrDefault(id) is { CurrentState.Phase: ScrcpyStream.Phase.Ended } stream
                            && Device(id)?.IsOnline == true)
                            stream.Start();
                    }));
                }
                break;
        }
    }

    /// A window waiting for its device starts streaming again once the device is back (USB re-plugged, Wi-Fi
    /// reached again), on whichever connection it came back by.
    private void ResumeStreams()
    {
        foreach (var (id, session) in sessions.ToList())
        {
            if (Device(id) is not { Serial: { } serial } || !streams.TryGetValue(id, out var stream)
                || stream.CurrentState.Phase != ScrcpyStream.Phase.Ended || failures.GetValueOrDefault(id) > 2 && stream.Serial == serial)
                continue;
            if (stream.Serial != serial)
            {
                stream.Stop();
                streams.Remove(id);
                if (StreamFor(id) is { } fresh) session.PhysicalStreamReplaced(fresh);
            }
            else
            {
                failures.Remove(id);
                stream.Start();
            }
        }
    }

    private void ReleaseStream(string id)
    {
        if (sessions.ContainsKey(id) || recorders.ContainsKey(id) || !streams.Remove(id, out var stream)) return;
        stream.Stop();
        ScreensOff.Remove(id);
        AudioDevices.Remove(id);
        failures.Remove(id);
        UpdateClipboardTimer();
    }

    // Mirroring

    public async void Mirror(string id, int cascade)
    {
        if (sessions.TryGetValue(id, out var shown))
        {
            shown.BringToFront();
            return;
        }
        if (Device(id) is not { IsOnline: true, Info: { } info } device || !opening.Add(id)) return;
        var stream = StreamFor(id);
        if (stream == null)
        {
            opening.Remove(id);
            return;
        }
        try
        {
            var session = await DeviceSession.Mirror(device, info, stream, this, cascade);
            sessions[id] = session;
            session.StateChanged += state =>
            {
                if (state != SessionState.Stopped) return;
                sessions.Remove(id);
                ReleaseStream(id);
                Changed?.Invoke();
            };
            session.SetRecording(Recordings.TryGetValue(id, out var since) ? since : null);
            Changed?.Invoke();
        }
        catch (Exception error)
        {
            Log.Error($"cannot mirror {id}: {error}");
            Error?.Invoke($"无法投屏“{device.Name}”：{error.Message}");
            ReleaseStream(id);
        }
        finally
        {
            opening.Remove(id);
        }
    }

    public bool IsMirroring(string id) => sessions.ContainsKey(id);

    public void SetScreenOff(bool off, string id)
    {
        if (!streams.TryGetValue(id, out var stream) || !stream.IsStreaming) return;
        stream.SetDisplayPower(!off);
        if (off) ScreensOff.Add(id); else ScreensOff.Remove(id);
        Changed?.Invoke();
    }

    // Screenshots

    /// Saves the screen to the Desktop at the display's resolution; without `completion`, says so in a toast.
    public async void Screenshot(string id, Action<string?, string?>? completion = null)
    {
        if (Device(id) is not { Serial: { } serial } device) return;
        var path = ScreenRecorder.DesktopPath(device.Name, "png");
        var frame = streams.TryGetValue(id, out var stream) && stream.IsStreaming ? stream.LastFrame() : null;
        try
        {
            // `screencap` gives the display's own resolution; the stream's frame, which may be scaled down, only if
            // that fails.
            byte[] png;
            try
            {
                png = await Adb.ScreenshotPng(serial);
            }
            catch when (frame != null)
            {
                png = await Pictures.EncodePng(frame);
            }
            // An app that forbids screenshots (FLAG_SECURE) comes through black.
            if (await Pictures.IsBlack(png)) throw new ShellException("画面是全黑的：当前应用可能不允许截屏，或者屏幕关着");
            await File.WriteAllBytesAsync(path, png);
            Log.Info($"screenshot saved to {path}");
            if (completion != null) completion(path, null); else Toast = new PhysicalToast("截图已存到桌面", path);
        }
        catch (Exception error)
        {
            Log.Error($"screenshot failed: {error.Message}");
            if (completion != null) completion(null, error.Message); else Toast = new PhysicalToast($"截图失败：{error.Message}", IsError: true);
        }
    }

    // Recording

    public bool IsRecording(string id) => Recordings.ContainsKey(id);

    public void ToggleRecording(string id)
    {
        if (Recordings.ContainsKey(id)) _ = StopRecording(id, null);
        else StartRecording(id);
    }

    /// Records the stream, starting one (without a window) if none runs. The file is made with the first frame,
    /// whose size it keeps.
    private void StartRecording(string id)
    {
        if (Device(id) is not { } device || StreamFor(id) is not { } stream) return;
        var box = new RecorderBox(ScreenRecorder.DesktopPath(device.Name, "mp4"), stream.Settings.Audio);
        recorders[id] = box;
        Recordings[id] = DateTime.Now;
        sessions.GetValueOrDefault(id)?.SetRecording(Recordings[id]);
        const string key = "recorder";
        stream.AddFrameSink(key, box.Append);
        stream.AddAudioSink(key, box.AppendAudio);
        // A frame comes only when the screen changes; the one shown now starts the file at once.
        if (stream.IsStreaming && stream.LastFrame() is { } frame) box.Append(new VideoFrame(frame.Pixels, frame.Width, frame.Height, HostClock.Now));
        Changed?.Invoke();
    }

    private async Task StopRecording(string id, string? reason)
    {
        if (!recorders.Remove(id, out var box)) return;
        streams.GetValueOrDefault(id)?.RemoveSinks("recorder");
        Recordings.Remove(id);
        sessions.GetValueOrDefault(id)?.SetRecording(null);
        ReleaseStream(id);
        Changed?.Invoke();
        var saved = await box.Finish();
        var text = saved ? (reason != null ? $"{reason}，录屏已存到桌面" : "录屏已存到桌面") : "录屏没有内容，没有保存";
        if (sessions.TryGetValue(id, out var session)) session.ShowToast(text, saved ? box.Path : null);
        else Toast = new PhysicalToast(text, saved ? box.Path : null, !saved);
    }

    // Preview

    /// Keeps the device's preview current while its detail pane is shown: the stream's frames if one runs,
    /// otherwise a `screencap` every couple of seconds. Ends when `cancellation` is cancelled.
    public async Task FollowPreview(string id, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (streams.TryGetValue(id, out var stream) && stream.IsStreaming && stream.LastFrame() is { } frame)
                {
                    Thumbnails[id] = await Task.Run(() => Pictures.Shrunk(frame, 640));
                    ThumbnailChanged?.Invoke(id);
                    await Task.Delay(1000, cancellation);
                }
                else if (Device(id)?.Serial is { } serial)
                {
                    try
                    {
                        var png = await Adb.ScreenshotPng(serial);
                        if (await Pictures.DecodePng(png, 640) is { } picture && !cancellation.IsCancellationRequested)
                        {
                            Thumbnails[id] = picture;
                            ThumbnailChanged?.Invoke(id);
                        }
                    }
                    catch (Exception error)
                    {
                        Log.Debug($"preview of {serial}: {error.Message}");
                    }
                    await Task.Delay(2000, cancellation);
                }
                else
                {
                    await Task.Delay(2000, cancellation);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // Clipboard

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    /// The text last put on a device's clipboard from here, which the device reports back as copied; it is not a copy
    /// to bring to Windows (typing Chinese goes through the device's clipboard, and must not replace the PC's).
    private string? sentToDevice;

    /// Text this app is about to put on a device's clipboard.
    public void NoteSentToDevice(string text) => sentToDevice = text;

    /// The device copied something: the Windows clipboard gets it.
    private async void DeviceCopied(string text, string id)
    {
        if (!OptionsFor(id).ClipboardSync || text.Length == 0 || text == sentToDevice) return;
        sentToDevice = text;
        // Another app may hold the clipboard open for a moment.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                clipboardSequence = GetClipboardSequenceNumber();
                return;
            }
            catch (Exception error)
            {
                if (attempt == 4) Log.Error($"cannot set the clipboard: 0x{error.HResult:X8} {error.Message}");
                await Task.Delay(100);
            }
        }
    }

    /// While a stream with clipboard sync runs, what is copied on Windows goes to the device.
    private void UpdateClipboardTimer()
    {
        var wanted = streams.Keys.Any(id => OptionsFor(id).ClipboardSync);
        if (wanted && clipboardTimer == null)
        {
            clipboardSequence = GetClipboardSequenceNumber();
            clipboardTimer = queue.CreateTimer();
            clipboardTimer.Interval = TimeSpan.FromSeconds(0.5);
            clipboardTimer.Tick += (_, _) => PollClipboard();
            clipboardTimer.Start();
        }
        else if (!wanted && clipboardTimer != null)
        {
            clipboardTimer.Stop();
            clipboardTimer = null;
        }
    }

    private bool readingClipboard;

    private async void PollClipboard()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == clipboardSequence || readingClipboard) return;
        clipboardSequence = sequence;
        readingClipboard = true;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            if (text == sentToDevice) return;
            sentToDevice = text;
            foreach (var (id, stream) in streams)
            {
                if (stream.IsStreaming && OptionsFor(id).ClipboardSync) stream.SetClipboard(text, paste: false);
            }
        }
        catch (Exception error)
        {
            // Another app may hold the clipboard open for a moment.
            Log.Debug($"cannot read the clipboard: {error.Message}");
        }
        finally
        {
            readingClipboard = false;
        }
    }

    // Connections

    private async void Run(string id, string what, Func<PhysicalDevice, Task> body)
    {
        if (Device(id) is not { } device || !Busy.Add(id)) return;
        Changed?.Invoke();
        try
        {
            await body(device);
        }
        catch (Exception error)
        {
            Error?.Invoke($"{what}没有完成：{error.Message}");
        }
        finally
        {
            Busy.Remove(id);
            Changed?.Invoke();
        }
    }

    /// `adb tcpip` and `adb connect`: the device can then be unplugged.
    public void SwitchToWireless(string id) => Run(id, "切换为无线连接", async device =>
    {
        if (device.Serial is not { } serial) return;
        var address = await Adb.SwitchToWireless(serial);
        Toast = new PhysicalToast($"已通过 Wi-Fi 连接（{address}），可以拔掉数据线了");
    });

    public void Reconnect(string id) => Run(id, "重新连接", async device =>
    {
        if (device.Address is { } address && (device.Serial == null || device.IsWireless)) await Adb.Connect(address);
        else if ((device.Serial ?? device.Serials.FirstOrDefault()) is { } serial) await Adb.Text(["reconnect"], serial, TimeSpan.FromSeconds(10));
    });

    public void Disconnect(string id) => Run(id, "断开连接", async device =>
    {
        foreach (var serial in device.Serials.Where(s => s.Contains(':'))) await Adb.Disconnect(serial);
    });

    /// Forgets a paired Wi-Fi device: it is disconnected and no longer listed while away.
    public void Forget(string id)
    {
        if (Device(id) is not { } device) return;
        var paired = PairedDevices;
        if ((device.Info?.SerialNumber ?? (device.Serial == null ? id["device:".Length..] : null)) is { } serialNumber) paired.Remove(serialNumber);
        PairedDevices = paired;
        if (device.Serials.Any(s => s.Contains(':'))) Disconnect(id);
        Rebuild();
        Changed?.Invoke();
    }

    public void Reboot(string id) => Run(id, "重启设备", async device =>
    {
        if (device.Serial is { } serial) await Adb.Text(["reboot"], serial, TimeSpan.FromSeconds(15));
    });

    /// Installs APKs and copies other files to the device's Download folder.
    public async void Send(IReadOnlyList<string> paths, string id)
    {
        if (Device(id)?.Serial is not { } serial) return;
        int installed = 0, copied = 0;
        var problems = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (Path.GetExtension(path).Equals(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    await Adb.Text(["install", "-r", path], serial, TimeSpan.FromSeconds(300));
                    installed++;
                }
                else
                {
                    await Adb.Text(["push", path, "/sdcard/Download/"], serial, TimeSpan.FromSeconds(600));
                    copied++;
                }
            }
            catch (Exception error)
            {
                problems.Add($"{Path.GetFileName(path)}：{error.Message}");
            }
        }
        var parts = new List<string>();
        if (installed > 0) parts.Add($"已安装 {installed} 个应用");
        if (copied > 0) parts.Add($"{copied} 个文件已存到“下载”文件夹");
        var text = parts.Count == 0 ? "没有完成" : string.Join("，", parts);
        if (sessions.TryGetValue(id, out var session)) session.ShowToast(text, null);
        else Toast = new PhysicalToast(text);
        if (problems.Count > 0) Error?.Invoke(string.Join("\n", problems));
    }

    /// `sdkmanager --install platform-tools`, for an SDK without adb.
    public async void InstallPlatformTools()
    {
        if (InstallProgress != null) return;
        InstallProgress = 0;
        Changed?.Invoke();
        try
        {
            await Sdk.Run("sdkmanager", ["--install", "platform-tools"], string.Concat(Enumerable.Repeat("y\n", 4)), line =>
            {
                if (Percent().Match(line) is { Success: true } match && double.TryParse(match.Groups[1].Value, out var percent))
                    queue.TryEnqueue(() =>
                    {
                        InstallProgress = percent / 100;
                        Changed?.Invoke();
                    });
            });
        }
        catch (Exception error)
        {
            Error?.Invoke($"无法安装 Platform-Tools：{error.Message}");
        }
        InstallProgress = null;
        StartTracking();
        Changed?.Invoke();
    }

    [GeneratedRegex(@"([0-9]+)%")]
    private static partial Regex Percent();

    /// Opens the folder screenshots and recordings go to.
    public static void RevealDesktop() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)}\"") { UseShellExecute = false });

    public static void Reveal(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });

    // Quitting

    public bool HasWork => sessions.Count > 0 || recorders.Count > 0;

    /// Finishes every recording and closes the streams; the task ends once the files are complete.
    public async Task StopAll()
    {
        foreach (var session in sessions.Values.ToList()) session.Close();
        foreach (var id in recorders.Keys.ToList()) await StopRecording(id, null);
        foreach (var stream in streams.Values.ToList()) stream.Stop();
        streams.Clear();
        StopTracking();
    }
}

/// A recording whose file is made once the first frame gives its size; fed from the stream's threads.
sealed class RecorderBox(string path, bool withAudio)
{
    public string Path { get; } = path;
    private readonly object gate = new();
    private ScreenRecorder? recorder;
    private bool failed;

    public void Append(VideoFrame frame)
    {
        ScreenRecorder? target;
        lock (gate)
        {
            if (recorder == null && !failed)
            {
                try
                {
                    recorder = new ScreenRecorder(Path, frame.Width, frame.Height, withAudio);
                }
                catch (Exception error)
                {
                    failed = true;
                    Log.Error($"cannot start recording: {error.Message}");
                }
            }
            target = recorder;
        }
        target?.Append(frame);
    }

    public void AppendAudio(byte[] samples, long time)
    {
        ScreenRecorder? target;
        lock (gate) target = recorder;
        target?.AppendAudio(samples, time);
    }

    public async Task<bool> Finish()
    {
        ScreenRecorder? target;
        lock (gate)
        {
            failed = true;
            target = recorder;
        }
        return target != null && await target.Finish(HostClock.Now);
    }
}
