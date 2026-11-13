using Android.Emulation.Control;
using Environment = System.Environment;

namespace EmulatorShell;

/// The "More" panel's view of one running device: values read back from the emulator, and the calls
/// that change them.
sealed class DeviceControls
{
    public sealed record Snapshot(string Id, string Name, DateTimeOffset? Created, long Size, bool IsLoaded, bool IsCompatible);
    public sealed record Message(string Text, bool IsError);
    public sealed record Place(string Name, double Latitude, double Longitude);

    public static readonly Place[] Places =
    [
        new("北京", 39.9042, 116.4074), new("上海", 31.2304, 121.4737), new("深圳", 22.5431, 114.0579),
        new("香港", 22.3193, 114.1694), new("东京", 35.6762, 139.6503), new("悉尼", -33.8688, 151.2093),
        new("墨尔本", -37.8136, 144.9631), new("伦敦", 51.5074, -0.1278), new("纽约", 40.7128, -74.0060),
        new("Googleplex", 37.4220, -122.0841),
    ];

    public string DeviceName { get; }
    /// Only devices with a hinge sensor (foldables) accept postures.
    public bool IsFoldable { get; }
    /// The postures the device defines (`hw.sensor.posture_list`); the emulator refuses the others.
    public IReadOnlyList<Posture.Types.PostureValue> Postures { get; }

    public string Latitude { get; set; } = "";
    public string Longitude { get; set; } = "";
    public double BatteryLevel { get; set; } = 100;
    public BatteryState.Types.BatteryCharger Charger { get; set; } = BatteryState.Types.BatteryCharger.Ac;
    public double Brightness { get; set; } = 128;
    public bool UsesMicrophone { get; private set; }
    public string PhoneNumber { get; set; } = "6505551212";
    public string SmsText { get; set; } = "";
    public Posture.Types.PostureValue Posture { get; set; } = Android.Emulation.Control.Posture.Types.PostureValue.PostureOpened;
    public IReadOnlyList<Snapshot> Snapshots { get; private set; } = [];
    public string NewSnapshotName { get; set; } = "";
    public bool Busy { get; private set; }
    public Message? Status { get; set; }
    /// Whether the values above were read from the emulator, so the tiles can show them.
    public bool Loaded { get; private set; }

    private bool clipboardSync;
    public bool ClipboardSync
    {
        get => clipboardSync;
        set
        {
            clipboardSync = value;
            ClipboardSyncChanged?.Invoke(value);
            Notify();
        }
    }

    public event Action<bool>? ClipboardSyncChanged;
    /// Something above changed; the panel shows it again.
    public event Action? Changed;

    private readonly EmulatorClient client;

    public DeviceControls(EmulatorClient client, Avd avd, bool clipboardSync)
    {
        this.client = client;
        DeviceName = avd.Name;
        IsFoldable = avd.Config.GetValueOrDefault("hw.sensor.hinge") == "yes";
        var listed = (avd.Config.GetValueOrDefault("hw.sensor.posture_list") ?? "").Split(',')
            .Select(s => int.TryParse(s.Trim(), out var v) ? (Posture.Types.PostureValue?)v : null)
            .OfType<Posture.Types.PostureValue>()
            .Where(v => v is > Android.Emulation.Control.Posture.Types.PostureValue.PostureUnknown and < Android.Emulation.Control.Posture.Types.PostureValue.PostureMax)
            .ToList();
        Postures = listed.Count > 0 ? listed :
        [
            Android.Emulation.Control.Posture.Types.PostureValue.PostureClosed, Android.Emulation.Control.Posture.Types.PostureValue.PostureHalfOpened,
            Android.Emulation.Control.Posture.Types.PostureValue.PostureOpened, Android.Emulation.Control.Posture.Types.PostureValue.PostureFlipped,
            Android.Emulation.Control.Posture.Types.PostureValue.PostureTent,
        ];
        this.clipboardSync = clipboardSync;
    }

    public void Notify() => Changed?.Invoke();

    // Reading

    /// Reads the current values; each one that cannot be read keeps its last value.
    public async Task Refresh()
    {
        try
        {
            var location = await client.Location();
            Latitude = Format(location.Latitude);
            Longitude = Format(location.Longitude);
        }
        catch { }
        try
        {
            var battery = await client.Battery();
            BatteryLevel = battery.ChargeLevel;
            Charger = battery.Charger;
        }
        catch { }
        try { Brightness = await client.Brightness(); } catch { }
        try { UsesMicrophone = await client.UsesHostMicrophone(); } catch { }
        await RefreshSnapshots();
        Loaded = true;
        Notify();
    }

    public async Task RefreshSnapshots()
    {
        try
        {
            var list = await client.Snapshots();
            Snapshots = list.Select(details => new Snapshot(
                details.SnapshotId,
                details.Details?.HasLogicalName == true && details.Details.LogicalName.Length > 0 ? details.Details.LogicalName : details.SnapshotId,
                details.Details?.CreationTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(details.Details.CreationTime) : null,
                (long)details.Size,
                details.Status == SnapshotDetails.Types.LoadStatus.Loaded,
                details.Status != SnapshotDetails.Types.LoadStatus.Incompatible
            )).OrderByDescending(s => s.Created ?? DateTimeOffset.MinValue).ToList();
            Notify();
        }
        catch { }
    }

    public string LocationSummary
    {
        get
        {
            if (!double.TryParse(Latitude, out var lat) || !double.TryParse(Longitude, out var lon)) return "未读取";
            if (Places.FirstOrDefault(p => Math.Abs(p.Latitude - lat) < 0.001 && Math.Abs(p.Longitude - lon) < 0.001) is { } place) return place.Name;
            return $"{lat:0.00}, {lon:0.00}";
        }
    }

    public string BatterySummary => $"{(int)BatteryLevel}%" + (Charger == BatteryState.Types.BatteryCharger.None ? "" : " · 充电");

    public string BrightnessSummary => $"{(int)Math.Round(Brightness / 255 * 100)}%";

    // Changing

    public async Task SetLocation(Place place)
    {
        Latitude = Format(place.Latitude);
        Longitude = Format(place.Longitude);
        await ApplyLocation();
    }

    public async Task ApplyLocation()
    {
        if (!double.TryParse(Latitude, out var lat) || !double.TryParse(Longitude, out var lon) || lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            Status = new Message("纬度要在 -90 到 90 之间，经度在 -180 到 180 之间。", true);
            Notify();
            return;
        }
        await Run($"位置已设为 {LocationSummary}。", c => c.SetLocation(lat, lon));
    }

    public async Task ApplyBattery()
    {
        int level = (int)BatteryLevel;
        var charger = Charger;
        await Run($"电池已设为 {BatterySummary}。", c => c.SetBattery(level, charger));
    }

    public async Task Phone(PhoneCall.Types.Operation operation)
    {
        var number = PhoneNumber.Trim();
        var done = new Dictionary<PhoneCall.Types.Operation, string>
        {
            [PhoneCall.Types.Operation.InitCall] = $"{number} 正在来电。", [PhoneCall.Types.Operation.AcceptCall] = "对方已接听。",
            [PhoneCall.Types.Operation.DisconnectCall] = "通话已挂断。", [PhoneCall.Types.Operation.PlaceCallOnHold] = "通话已保持。",
            [PhoneCall.Types.Operation.TakeCallOffHold] = "通话已恢复。",
        };
        await Run(done.GetValueOrDefault(operation), c => c.Phone(operation, number));
    }

    public async Task ReceiveSms()
    {
        string number = PhoneNumber.Trim(), text = SmsText;
        if (text.Length == 0) return;
        await Run($"已收到来自 {number} 的短信。", c => c.ReceiveSms(number, text));
        if (Status?.IsError == false)
        {
            SmsText = "";
            Notify();
        }
    }

    /// One touch of finger 1, as when enrolling a fingerprint or unlocking.
    public Task TouchFingerprint() =>
        Run("已触摸指纹传感器。", async c =>
        {
            await c.TouchFingerprint(true, 1);
            await Task.Delay(250);
            await c.TouchFingerprint(false, 1);
        });

    public async Task SetUsesMicrophone(bool enabled)
    {
        UsesMicrophone = enabled;
        await Run(enabled ? "设备现在使用这台电脑的麦克风。" : "已断开这台电脑的麦克风。", c => c.SetUsesHostMicrophone(enabled));
        if (Status?.IsError == true)
        {
            UsesMicrophone = !enabled;
            Notify();
        }
    }

    public Task ApplyBrightness()
    {
        var value = (int)Brightness;
        return Run(null, c => c.SetBrightness(value));
    }

    public Task SetPosture(Posture.Types.PostureValue value)
    {
        Posture = value;
        return Run(null, c => c.SetPosture(value));
    }

    public async Task SaveSnapshot()
    {
        var name = NewSnapshotName.Trim();
        var id = name.Length == 0 ? "snap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") : Avd.IdFor(name);
        if (id.Length == 0)
        {
            Status = new Message("快照名称需要包含英文字母或数字。", true);
            Notify();
            return;
        }
        await Run($"已保存快照“{id}”。", c => c.SaveSnapshot(id));
        NewSnapshotName = "";
        await RefreshSnapshots();
    }

    public async Task LoadSnapshot(Snapshot snapshot)
    {
        await Run($"已恢复到快照“{snapshot.Name}”。", c => c.LoadSnapshot(snapshot.Id));
        await RefreshSnapshots();
    }

    public async Task DeleteSnapshot(Snapshot snapshot)
    {
        await Run($"已删除快照“{snapshot.Name}”。", c => c.DeleteSnapshot(snapshot.Id));
        await RefreshSnapshots();
    }

    private async Task Run(string? success, Func<EmulatorClient, Task> operation)
    {
        Busy = true;
        Notify();
        try
        {
            await operation(client);
            Status = success == null ? null : new Message(success, false);
        }
        catch (Exception error)
        {
            Log.Error($"{DeviceName}: {error.Message}");
            Status = new Message(error is ShellException ? error.Message : $"操作失败：{error.Message}", true);
        }
        finally
        {
            Busy = false;
            Notify();
        }
    }

    private static string Format(double value) => value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);

    public static string PostureName(Posture.Types.PostureValue posture) => posture switch
    {
        Android.Emulation.Control.Posture.Types.PostureValue.PostureClosed => "合上",
        Android.Emulation.Control.Posture.Types.PostureValue.PostureHalfOpened => "半开",
        Android.Emulation.Control.Posture.Types.PostureValue.PostureOpened => "展开",
        Android.Emulation.Control.Posture.Types.PostureValue.PostureFlipped => "翻转",
        Android.Emulation.Control.Posture.Types.PostureValue.PostureTent => "帐篷",
        _ => "未知",
    };
}
