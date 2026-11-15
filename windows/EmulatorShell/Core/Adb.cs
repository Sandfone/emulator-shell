using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EmulatorShell;

/// The SDK's adb. Android Studio uses the same one, so both share one adb server: a second adb of another
/// version would restart the server each time either is used, dropping the other's connections.
static partial class Adb
{
    public static string Executable => Path.Combine(Sdk.Root, "platform-tools", "adb.exe");
    public static bool IsInstalled => File.Exists(Executable);

    /// Runs adb and returns what it wrote to standard output; throws with its error output when it fails or
    /// takes longer than `timeout`.
    public static async Task<byte[]> Run(IEnumerable<string> arguments, string? serial = null, TimeSpan? timeout = null,
                                         CancellationToken cancellation = default)
    {
        var list = arguments.ToList();
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (serial != null)
        {
            start.ArgumentList.Add("-s");
            start.ArgumentList.Add(serial);
        }
        foreach (var argument in list) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        cancellation.ThrowIfCancellationRequested();
        process.Start();
        process.StandardInput.Close();
        var output = new MemoryStream();
        var outputPump = process.StandardOutput.BaseStream.CopyToAsync(output, CancellationToken.None);
        var errorPump = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            cancellation.ThrowIfCancellationRequested();
            throw new ShellException($"adb {list.FirstOrDefault()} 超时");
        }
        await outputPump;
        var errors = await errorPump;
        if (process.ExitCode != 0)
        {
            var message = (errors.Trim().Length > 0 ? errors : Encoding.UTF8.GetString(output.ToArray())).Trim();
            throw new ShellException(message.Length > 0 ? message : $"adb {list.FirstOrDefault()} 失败（{process.ExitCode}）");
        }
        return output.ToArray();
    }

    public static async Task<string> Text(IEnumerable<string> arguments, string? serial = null, TimeSpan? timeout = null) =>
        Encoding.UTF8.GetString(await Run(arguments, serial, timeout));

    /// Runs `command` in the device's shell. adb before Android 7 reports no exit status, so the output is
    /// what tells success apart.
    public static Task<string> Shell(string command, string serial, TimeSpan? timeout = null) =>
        Text(["shell", command], serial, timeout);

    /// The device's screen as PNG, at its own resolution.
    public static async Task<byte[]> ScreenshotPng(string serial)
    {
        var data = await Run(["exec-out", "screencap", "-p"], serial, TimeSpan.FromSeconds(20));
        // A PNG starts with 0x89 'P' 'N' 'G'. A device with several displays (a foldable) writes a warning line before
        // it, which is skipped; output without one is an error message.
        var start = data.AsSpan().IndexOf([(byte)0x89, (byte)0x50, (byte)0x4E, (byte)0x47, (byte)0x0D, (byte)0x0A]);
        if (start >= 0 && data.Length > start + 8) return start == 0 ? data : data[start..];
        var message = Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(200, data.Length))).Trim();
        throw new ShellException(message.Length > 0 ? message : "截图失败");
    }

    // Wireless

    /// `adb mdns services`: devices on the network that offer pairing (`_adb-tls-pairing._tcp`) or that are
    /// paired and offer a connection (`_adb-tls-connect._tcp`).
    public sealed record MdnsService(string Name, string Type, string Host, int Port)
    {
        public string Address => $"{Host}:{Port}";
        public bool IsPairing => Type.StartsWith("_adb-tls-pairing");
        public bool IsConnect => Type.StartsWith("_adb-tls-connect");
    }

    public static async Task<List<MdnsService>> MdnsServices()
    {
        var output = await Text(["mdns", "services"], timeout: TimeSpan.FromSeconds(10));
        var services = new List<MdnsService>();
        foreach (var line in output.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            var colon = fields[2].LastIndexOf(':');
            if (colon < 0 || !int.TryParse(fields[2][(colon + 1)..], out var port)) continue;
            services.Add(new MdnsService(fields[0], fields[1], fields[2][..colon], port));
        }
        return services;
    }

    /// Pairs with a device showing a pairing code or scanning this app's QR code (Android 11+).
    public static async Task Pair(string address, string code)
    {
        var output = await Text(["pair", address, code], timeout: TimeSpan.FromSeconds(20));
        if (!output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase)) throw new ShellException(output.Trim());
    }

    public static async Task Connect(string address)
    {
        var output = await Text(["connect", address], timeout: TimeSpan.FromSeconds(15));
        if (!output.Contains("connected to")) throw new ShellException(output.Trim());
    }

    public static Task Disconnect(string address) => Text(["disconnect", address], timeout: TimeSpan.FromSeconds(10));

    /// Switches a USB device to adb over TCP on port 5555 and connects to it there, the way that works on any
    /// Android version; returns the address.
    public static async Task<string> SwitchToWireless(string serial)
    {
        var route = await Shell("ip -f inet addr show wlan0", serial);
        var match = InetAddress().Match(route);
        if (!match.Success) throw new ShellException("这台设备没有连 Wi-Fi");
        await Text(["tcpip", "5555"], serial, TimeSpan.FromSeconds(15));
        var address = $"{match.Groups[1].Value}:5555";
        // adbd restarts on the device; it takes a moment to listen.
        Exception last = new ShellException($"连接 {address} 失败");
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(700);
            try
            {
                await Connect(address);
                return address;
            }
            catch (Exception error)
            {
                last = error;
            }
        }
        throw last;
    }

    [GeneratedRegex(@"inet ([0-9.]+)")]
    private static partial Regex InetAddress();
}

/// One line of `adb devices -l` / `adb track-devices -l`.
sealed partial record AdbDevice(string Serial, string State, IReadOnlyDictionary<string, string> Info)
{
    public bool IsEmulator => Serial.StartsWith("emulator-");
    /// Connected over the network: `ip:port`, or a paired device's mDNS name.
    public bool IsWireless => Serial.Contains(':') || Serial.Contains("._adb-tls-connect.");
    public string? Model => Info.GetValueOrDefault("model")?.Replace('_', ' ');

    public static List<AdbDevice> Parse(string text)
    {
        var devices = new List<AdbDevice>();
        foreach (var line in text.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("List of devices") || line.StartsWith('*')) continue;
            // `-l` pads the serial with spaces rather than a tab; the state may hold spaces ("no permissions
            // (…)"); the details are `key:value` words at the end.
            var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count < 2) continue;
            var serial = words[0];
            words.RemoveAt(0);
            var info = new Dictionary<string, string>();
            while (words.Count > 0 && Detail().IsMatch(words[^1]))
            {
                var word = words[^1];
                var colon = word.IndexOf(':');
                info[word[..colon]] = word[(colon + 1)..];
                words.RemoveAt(words.Count - 1);
            }
            var state = string.Join(" ", words).Split(" (")[0];
            devices.Add(new AdbDevice(serial, state, info));
        }
        return devices;
    }

    public bool Equals(AdbDevice? other) =>
        other != null && other.Serial == Serial && other.State == State && other.Info.Count == Info.Count && !other.Info.Except(Info).Any();

    public override int GetHashCode() => HashCode.Combine(Serial, State);

    [GeneratedRegex(@"^[a-z_]+:[^/\[\]]+$")]
    private static partial Regex Detail();
}

/// Follows `adb track-devices -l`, which reports the whole device list each time it changes, so a device shows
/// up or goes away within a second of being plugged in, with no polling. adb starts its server if none runs;
/// if the server goes away (`adb kill-server`, an SDK update) the tracker starts again.
sealed class AdbDeviceTracker(Action<List<AdbDevice>> onChange)
{
    private readonly object gate = new();
    private Process? process;
    private bool stopped;

    public void Start()
    {
        lock (gate)
        {
            if (stopped || process != null || !Adb.IsInstalled) return;
            var start = new ProcessStartInfo(Adb.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            start.ArgumentList.Add("track-devices");
            start.ArgumentList.Add("-l");
            var started = new Process { StartInfo = start };
            try
            {
                started.Start();
            }
            catch (Exception error)
            {
                Log.Error($"cannot run adb track-devices: {error.Message}");
                return;
            }
            ChildProcesses.Adopt(started);
            started.StandardInput.Close();
            // Its error output is not read: it is only "daemon started" notes, and an unread pipe that fills up
            // would stall it.
            _ = started.StandardError.BaseStream.CopyToAsync(Stream.Null);
            process = started;
            _ = Task.Run(() => Read(started));
        }
    }

    public void Stop()
    {
        Process? running;
        lock (gate)
        {
            stopped = true;
            running = process;
            process = null;
        }
        try { running?.Kill(); } catch { }
    }

    /// Each report is four hex digits giving its length, then that many bytes of `adb devices -l` lines. adb on
    /// Windows writes its output in text mode, which turns each line's "\n" into "\r\n" without changing the length:
    /// the "\r"s are dropped, or every report after the first would be read from the wrong place and the list would
    /// stop following the devices.
    private void Read(Process running)
    {
        var stream = running.StandardOutput.BaseStream;
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        try
        {
            int count;
            while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                foreach (var b in chunk.AsSpan(0, count))
                    if (b != (byte)'\r') buffer.Add(b);
                List<AdbDevice>? last = null;
                while (buffer.Count >= 4)
                {
                    // Out of step with the reports: a new tracker starts again from a whole list.
                    var prefix = Encoding.ASCII.GetString(buffer.GetRange(0, 4).ToArray());
                    if (!int.TryParse(prefix, NumberStyles.AllowHexSpecifier, null, out var length))
                        throw new ShellException($"unexpected output “{prefix}”");
                    if (buffer.Count < 4 + length) break;
                    last = AdbDevice.Parse(Encoding.UTF8.GetString(buffer.GetRange(4, length).ToArray()));
                    buffer.RemoveRange(0, 4 + length);
                }
                if (last != null) onChange(last);
            }
        }
        catch (Exception error)
        {
            if (!stopped) Log.Error($"adb track-devices: {error.Message}");
        }
        try { running.Kill(); } catch { }
        bool restart;
        lock (gate)
        {
            if (process == running) process = null;
            restart = !stopped;
        }
        running.Dispose();
        if (!restart) return;
        onChange([]);
        _ = Task.Delay(2000).ContinueWith(_ => Start());
    }
}

/// What the device manager shows about a physical device, read over adb once it is authorized.
sealed partial record DeviceInfo
{
    public Dictionary<string, string> Properties { get; init; } = [];
    /// The display's size in its natural orientation, in pixels.
    public int Width { get; init; }
    public int Height { get; init; }
    /// Android's logical density (`wm density`) and the panel's real dots per inch, when reported.
    public int? Density { get; init; }
    public double? Xdpi { get; init; }
    public int? BatteryLevel { get; init; }
    public bool Charging { get; init; }
    public long? StorageFree { get; init; }
    public long? StorageTotal { get; init; }
    public long? Memory { get; init; }

    public string Manufacturer => Properties.GetValueOrDefault("ro.product.manufacturer") ?? "";
    public string Model => Properties.GetValueOrDefault("ro.product.model") ?? "";
    /// The name people know the device by ("Pixel 8 Pro", "Galaxy Tab S9"), else the model.
    public string MarketName =>
        new[] { "ro.product.marketname", "ro.vendor.product.marketname", "ro.config.marketing_name", "ro.product.vendor.marketname", "ro.product.odm.marketname" }
            .Select(Properties.GetValueOrDefault).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? Model;
    public string Codename => Properties.GetValueOrDefault("ro.product.device") ?? "";
    public string AndroidVersion => Properties.GetValueOrDefault("ro.build.version.release") ?? "?";
    public int Sdk => int.TryParse(Properties.GetValueOrDefault("ro.build.version.sdk"), out var sdk) ? sdk : 0;
    public string? SerialNumber => Properties.GetValueOrDefault("ro.serialno") is { Length: > 0 } serial ? serial : null;
    public string? SecurityPatch => Properties.GetValueOrDefault("ro.build.version.security_patch");
    public string? BuildId => Properties.GetValueOrDefault("ro.build.display.id") ?? Properties.GetValueOrDefault("ro.build.id");
    public string? Abi => Properties.GetValueOrDefault("ro.product.cpu.abi");
    public bool IsTablet =>
        Properties.GetValueOrDefault("ro.build.characteristics")?.Contains("tablet") == true || Math.Min(Width, Height) >= 1500 && Density < 360;
    public bool IsLandscapeNatural => Width > Height;
    /// MIUI and HyperOS refuse input from adb (touches, keys) until "USB debugging (Security settings)" is turned
    /// on in the developer options; the screen still streams.
    public bool InputBlocked => Properties.GetValueOrDefault("persist.security.adbinput") == "0";

    /// One shell call for everything, as each adb call costs a round trip.
    public static async Task<DeviceInfo> Read(string serial)
    {
        const string script = "getprop; echo @@; wm size; echo @@; wm density; echo @@; dumpsys display | grep -m1 dpi; echo @@; "
                              + "dumpsys battery; echo @@; df -k /data | tail -1; echo @@; head -1 /proc/meminfo";
        var output = await Adb.Shell(script, serial, TimeSpan.FromSeconds(15));
        var parts = output.Split("@@");
        string Part(int index) => index < parts.Length ? parts[index] : "";

        var properties = new Dictionary<string, string>();
        foreach (Match match in Property().Matches(Part(0))) properties[match.Groups[1].Value] = match.Groups[2].Value;
        var size = PhysicalSize().Match(Part(1));
        var battery = Part(4);
        var df = Part(5).Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        long? total = null, free = null;
        if (df.Length >= 4 && long.TryParse(df[1], out var t) && long.TryParse(df[3], out var f))
        {
            total = t * 1024;
            free = f * 1024;
        }
        return new DeviceInfo
        {
            Properties = properties,
            Width = size.Success ? int.Parse(size.Groups[1].Value) : 0,
            Height = size.Success ? int.Parse(size.Groups[2].Value) : 0,
            Density = PhysicalDensity().Match(Part(2)) is { Success: true } density ? int.Parse(density.Groups[1].Value) : null,
            Xdpi = Dpi().Match(Part(3)) is { Success: true } dpi && double.TryParse(dpi.Groups[1].Value, CultureInfo.InvariantCulture, out var x) ? x : null,
            BatteryLevel = Level().Match(battery) is { Success: true } level ? int.Parse(level.Groups[1].Value) : null,
            Charging = Status().Match(battery) is { Success: true } status && status.Groups[1].Value == "2",
            StorageTotal = total,
            StorageFree = free,
            Memory = MemTotal().Match(Part(6)) is { Success: true } memory ? long.Parse(memory.Groups[1].Value) * 1024 : null,
        };
    }

    // [ro.product.model]: [Pixel 8 Pro]
    [GeneratedRegex(@"^\[([^\]]+)\]: \[(.*)\]\s*$", RegexOptions.Multiline)]
    private static partial Regex Property();
    [GeneratedRegex(@"Physical size: ([0-9]+)x([0-9]+)")]
    private static partial Regex PhysicalSize();
    [GeneratedRegex(@"Physical density: ([0-9]+)")]
    private static partial Regex PhysicalDensity();
    [GeneratedRegex(@"([0-9.]+) x [0-9.]+ dpi")]
    private static partial Regex Dpi();
    [GeneratedRegex(@"level: ([0-9]+)")]
    private static partial Regex Level();
    [GeneratedRegex(@"status: ([0-9]+)")]
    private static partial Regex Status();
    [GeneratedRegex(@"MemTotal:\s+([0-9]+)")]
    private static partial Regex MemTotal();

    public bool Equals(DeviceInfo? other) =>
        other != null && other.Width == Width && other.Height == Height && other.Density == Density && other.Xdpi == Xdpi
        && other.BatteryLevel == BatteryLevel && other.Charging == Charging && other.StorageFree == StorageFree
        && other.StorageTotal == StorageTotal && other.Memory == Memory
        && other.Properties.Count == Properties.Count && !other.Properties.Except(Properties).Any();

    public override int GetHashCode() => HashCode.Combine(Width, Height, BatteryLevel, Properties.Count);
}
