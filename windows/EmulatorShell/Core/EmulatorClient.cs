using Android.Emulation.Control;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Environment = System.Environment;

namespace EmulatorShell;

/// One display frame as delivered by the emulator: RGBA8888, top row first.
readonly record struct RawFrame(
    int Width,
    int Height,
    /// Emulator-side sequence number; gaps are frames the emulator skipped because this app was behind.
    uint Seq,
    ulong TimestampUs,
    /// Quarter turns the device is held at (`Rotation.SkinRotation`); the pixels are already upright for the viewer.
    int Rotation,
    /// Whether a foldable is folded and this is its outer display.
    bool Folded,
    /// Only valid for the duration of the callback it is passed to.
    ReadOnlyMemory<byte> Pixels);

/// Thin wrapper over the emulator's gRPC services: `EmulatorController` and `SnapshotService`.
///
/// Frames ride inside the gRPC messages. The emulator's shared-memory transport (`ImageTransport.MMAP`), which
/// the macOS build uses, sends no frames at all from the Windows emulator, whatever the handle.
sealed class EmulatorClient : IDisposable
{
    private const int MaxFrameBytes = 64 * 1024 * 1024;

    private readonly GrpcChannel channel;
    private readonly EmulatorController.EmulatorControllerClient controller;
    private readonly SnapshotService.SnapshotServiceClient snapshotService;

    /// `token` is the bearer token an emulator started with `-grpc-use-token` (as Android Studio does) wants on
    /// every call; it writes it into its `pid_*.ini` as `grpc.token`.
    public EmulatorClient(int port, string? token = null)
    {
        channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions
        {
            MaxReceiveMessageSize = MaxFrameBytes,
            MaxSendMessageSize = MaxFrameBytes,
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            },
        });
        var invoker = token == null ? channel.CreateCallInvoker()
            : channel.Intercept(metadata =>
            {
                metadata.Add("authorization", $"Bearer {token}");
                return metadata;
            });
        controller = new EmulatorController.EmulatorControllerClient(invoker);
        snapshotService = new SnapshotService.SnapshotServiceClient(invoker);
    }

    public void Dispose() => channel.Dispose();

    /// Polls `getStatus` until the gRPC endpoint answers.
    public async Task WaitUntilReachable(TimeSpan timeout, Action<bool> onStatus, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var status = await controller.getStatusAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(2), cancellationToken: cancellation);
                onStatus(status.Booted);
                return;
            }
            catch (RpcException) { }
            await Task.Delay(500, cancellation);
        }
        throw new ShellException($"模拟器的 gRPC 端口在 {timeout.TotalSeconds:0} 秒内没有响应");
    }

    /// Streams display frames scaled to fit `width`×`height` pixels until cancelled.
    public async Task StreamScreen(int width, int height, Action<RawFrame> onFrame, CancellationToken cancellation)
    {
        var format = new ImageFormat { Format = ImageFormat.Types.ImgFormat.Rgba8888, Width = (uint)width, Height = (uint)height };
        using var call = controller.streamScreenshot(format, cancellationToken: cancellation);
        while (await call.ResponseStream.MoveNext(cancellation))
        {
            var image = call.ResponseStream.Current;
            int w = (int)image.Format.Width, h = (int)image.Format.Height;
            if (w <= 0 || h <= 0 || image.Image_.Length < w * h * 4) continue;
            onFrame(new RawFrame(w, h, image.Seq, image.TimestampUs, (int)(image.Format.Rotation?.Rotation_ ?? 0),
                                 image.Format.FoldedDisplay != null, image.Image_.Memory));
        }
    }

    public Task SendTouch(int x, int y, bool pressed) =>
        Send(() => controller.sendTouchAsync(new TouchEvent
        {
            Touches = { new Touch { X = x, Y = y, Identifier = 0, Pressure = pressed ? 1 : 0 } },
        }).ResponseAsync);

    /// `key` is a W3C `KeyboardEvent.key` value, e.g. "a", "Enter", "GoBack", "GoHome", "Power".
    /// Sending `keydown` and `keyup` separately lets Android see real long presses (power menu, volume repeat).
    public Task SendKey(string key, KeyboardEvent.Types.KeyEventType type = KeyboardEvent.Types.KeyEventType.Keypress) =>
        Send(() => controller.sendKeyAsync(new KeyboardEvent { EventType = type, Key = key }).ResponseAsync);

    public Task SendText(string text) =>
        Send(() => controller.sendKeyAsync(new KeyboardEvent { Text = text }).ResponseAsync);

    /// Turns the virtual device about the axis through the screen; the emulator reports the resulting
    /// orientation in each frame's `format.rotation`.
    public Task SetRotation(float degrees) =>
        Send(() => controller.setPhysicalModelAsync(new PhysicalModelValue
        {
            Target = PhysicalModelValue.Types.PhysicalType.Rotation,
            Value = new ParameterValue { Data = { 0, 0, degrees } },
            // The default SMOOTH glide leaves frames sent at its start reporting the old orientation.
            Interpolation = PhysicalModelValue.Types.Interpolation.Step,
        }).ResponseAsync);

    /// Full-resolution PNG of the display, upright as the device is currently held.
    public async Task<byte[]> ScreenshotPng() =>
        (await controller.getScreenshotAsync(new ImageFormat { Format = ImageFormat.Types.ImgFormat.Png })).Image_.ToByteArray();

    public async Task<bool> IsBooted(CancellationToken cancellation = default)
    {
        try { return (await controller.getStatusAsync(new Empty(), cancellationToken: cancellation)).Booted; }
        catch (RpcException) { return false; }
    }

    /// Shuts the emulator down like closing its window: it saves the quick-boot snapshot, then exits.
    public async Task<bool> ShutDown()
    {
        try
        {
            await controller.setVmStateAsync(new VmRunState { State = VmRunState.Types.RunState.Shutdown },
                                              deadline: DateTime.UtcNow.AddSeconds(5));
            return true;
        }
        catch (RpcException error)
        {
            Log.Error($"shutdown request failed: {error.Status}");
            return false;
        }
    }

    public Task SetClipboard(string text) =>
        Send(() => controller.setClipboardAsync(new ClipData { Text = text }).ResponseAsync);

    /// Delivers the guest clipboard now and on every change made inside the guest, until cancelled.
    public async Task StreamClipboard(Action<string> onText, CancellationToken cancellation)
    {
        using var call = controller.streamClipboard(new Empty(), cancellationToken: cancellation);
        while (await call.ResponseStream.MoveNext(cancellation)) onText(call.ResponseStream.Current.Text);
    }

    /// Delivers the foldable posture now and on every change, whoever made it, until cancelled.
    public async Task StreamPosture(Action<Posture.Types.PostureValue> onPosture, CancellationToken cancellation)
    {
        using var call = controller.streamNotification(new Empty(), cancellationToken: cancellation);
        while (await call.ResponseStream.MoveNext(cancellation))
        {
            if (call.ResponseStream.Current.Posture is { } posture) onPosture(posture.Value);
        }
    }

    // Device state, for the "More" panel. These throw so the panel can say what went wrong.

    public async Task<(double Latitude, double Longitude)> Location()
    {
        var gps = await controller.getGpsAsync(new Empty());
        return (gps.Latitude, gps.Longitude);
    }

    public async Task SetLocation(double latitude, double longitude) =>
        await controller.setGpsAsync(new GpsState { Latitude = latitude, Longitude = longitude, Satellites = 12, PassiveUpdate = true });

    public async Task<BatteryState> Battery() => await controller.getBatteryAsync(new Empty());

    public async Task SetBattery(int level, BatteryState.Types.BatteryCharger charger) =>
        await controller.setBatteryAsync(new BatteryState
        {
            HasBattery = true,
            IsPresent = true,
            ChargeLevel = level,
            Charger = charger,
            Health = BatteryState.Types.BatteryHealth.Good,
            Status = charger == BatteryState.Types.BatteryCharger.None ? BatteryState.Types.BatteryStatus.Discharging
                : level >= 100 ? BatteryState.Types.BatteryStatus.Full : BatteryState.Types.BatteryStatus.Charging,
        });

    /// Simulates the network side of a call: an incoming call from `number`, hanging up, holding.
    public async Task Phone(PhoneCall.Types.Operation operation, string number) =>
        Check(await controller.sendPhoneAsync(new PhoneCall { Operation = operation, Number = number }));

    public async Task ReceiveSms(string number, string text) =>
        Check(await controller.sendSmsAsync(new SmsMessage { SrcAddress = number, Text = text }));

    private static void Check(PhoneResponse response)
    {
        var reasons = new Dictionary<PhoneResponse.Types.Response, string>
        {
            [PhoneResponse.Types.Response.BadNumber] = "号码格式不对",
            [PhoneResponse.Types.Response.InvalidAction] = "当前没有可操作的通话",
            [PhoneResponse.Types.Response.ActionFailed] = "模拟器内部出错",
            [PhoneResponse.Types.Response.RadioOff] = "设备的无线电已关闭（飞行模式？）",
        };
        if (response.Response != PhoneResponse.Types.Response.Ok)
            throw new ShellException(reasons.GetValueOrDefault(response.Response) ?? $"电话操作失败（{response.Response}）");
    }

    public async Task TouchFingerprint(bool touching, int finger) =>
        await controller.sendFingerprintAsync(new Fingerprint { IsTouching = touching, TouchId = finger });

    /// Whether the guest hears this computer's microphone.
    public async Task<bool> UsesHostMicrophone() => (await controller.getMicrophoneStateAsync(new Empty())).RealAudioEnabled;

    public async Task SetUsesHostMicrophone(bool enabled) =>
        await controller.setMicrophoneStateAsync(new MicrophoneState { RealAudioEnabled = enabled });

    /// Screen backlight, 0...255.
    public async Task<int> Brightness() =>
        (int)(await controller.getBrightnessAsync(new BrightnessValue { Target = BrightnessValue.Types.LightType.Lcd })).Value;

    public async Task SetBrightness(int value) =>
        await controller.setBrightnessAsync(new BrightnessValue { Target = BrightnessValue.Types.LightType.Lcd, Value = (uint)Math.Clamp(value, 0, 255) });

    public async Task SetPosture(Posture.Types.PostureValue value) =>
        await controller.setPostureAsync(new Posture { Value = value });

    public async Task<IReadOnlyList<SnapshotDetails>> Snapshots() =>
        (await snapshotService.ListSnapshotsAsync(new SnapshotFilter { StatusFilter = SnapshotFilter.Types.LoadStatus.All })).Snapshots;

    public async Task SaveSnapshot(string id) => Check(await snapshotService.SaveSnapshotAsync(new SnapshotPackage { SnapshotId = id }), "保存");

    public async Task LoadSnapshot(string id) => Check(await snapshotService.LoadSnapshotAsync(new SnapshotPackage { SnapshotId = id }), "加载");

    public async Task DeleteSnapshot(string id) => Check(await snapshotService.DeleteSnapshotAsync(new SnapshotPackage { SnapshotId = id }), "删除");

    private static void Check(SnapshotPackage result, string action)
    {
        if (result.Success) return;
        var reason = result.Err.ToStringUtf8();
        throw new ShellException($"快照{action}失败" + (reason.Length == 0 ? "" : $"：{reason}"));
    }

    private static async Task Send(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception error)
        {
            Log.Error($"gRPC input call failed: {error.Message}");
        }
    }
}
