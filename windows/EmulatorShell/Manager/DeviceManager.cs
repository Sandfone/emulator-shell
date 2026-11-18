using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;

namespace EmulatorShell;

/// What the new/edit dialog edits. Sizes are strings so half-typed values survive.
sealed record AvdForm
{
    /// Null while creating; the AVD's id while editing.
    public string? EditingId { get; init; }
    public string Name { get; set; } = "";
    /// Once the name is typed in, changing the device type no longer renames.
    public bool NameTouched { get; set; }
    public string DeviceTypeId { get; set; } = "pixel_9";
    public string? ImageId { get; set; }
    public string RamMB { get; set; } = "2048";
    public string DataGB { get; set; } = "6";
    public string SdMB { get; set; } = "512";
    public bool HostGpu { get; set; } = true;
    public bool ColdBoot { get; set; }
    public string CameraBack { get; set; } = "virtualscene";
    public string CameraFront { get; set; } = "emulated";
    public bool Landscape { get; set; }

    public bool IsEdit => EditingId != null;
}

enum AvdStatusKind { Stopped, Booting, Stopping, Running, External }

/// What a device window's menu asks of the manager window: to show it, or to show it and turn the SDK forwarder
/// or the command-line tool on or off, which it asks about in its own dialogs.
enum ManagerTask { Show, ToggleForwarder, ToggleCommand }

/// `External`: started by something else (Android Studio, the command line).
sealed record AvdStatus(AvdStatusKind Kind, string? Serial = null)
{
    public bool IsStopped => Kind == AvdStatusKind.Stopped;
}

/// The device manager's state: AVDs, device definitions, system images, and the devices this app runs.
sealed class DeviceManager
{
    public List<Avd> Avds { get; private set; } = [];
    public List<DeviceType> DeviceTypes { get; private set; } = [];
    public List<SystemImage> Images { get; private set; } = [];
    public string? Selection { get; set; }
    /// A short line under the selected device's buttons, e.g. after a wipe.
    public string? Note { get; set; }
    public HashSet<string> Busy { get; } = [];
    public Dictionary<string, long> Sizes { get; private set; } = [];
    public Dictionary<string, long> ImageSizes { get; private set; } = [];
    /// Download progress per image package, 0...1.
    public Dictionary<string, double> Downloads { get; } = [];
    public bool LoadingAvailableImages { get; private set; }
    public DateTime? AvailableImagesUpdated { get; private set; }

    /// Something above changed; the windows show it again.
    public event Action? Changed;
    /// An operation failed; the manager window says so.
    public event Action<string>? Error;
    /// A device session ended or was added.
    public event Action? SessionsChanged;
    /// A device window asked for the manager (Ctrl+Shift+2) or to quit (Ctrl+Q).
    public event Action<ManagerTask>? ManagerRequested;
    public event Action? QuitRequested;

    private readonly Dictionary<string, SessionState> sessionStates = [];
    private Dictionary<string, RunningEmulator> external = [];
    private readonly Dictionary<string, DeviceSession> sessions = [];
    /// Devices whose skins are loading, before they have a session.
    private readonly HashSet<string> starting = [];
    private readonly Dictionary<string, CancellationTokenSource> downloadTasks = [];
    private List<SystemImage> availableImages = [];
    private readonly DispatcherQueue queue;
    private readonly DispatcherQueueTimer timer;

    private static readonly string ListCache = Path.Combine(Settings.Folder, "sdkmanager-list.txt");

    /// Phones and tablets connected over USB or Wi-Fi.
    public PhysicalDeviceStore Phones { get; }

    public DeviceManager()
    {
        queue = DispatcherQueue.GetForCurrentThread();
        Phones = new PhysicalDeviceStore();
        Phones.Error += message => Error?.Invoke(message);
        Phones.Changed += () =>
        {
            FollowPhysicalSelection();
            Notify();
            // A phone's window or recording that ends may be the last thing the app shows.
            SessionsChanged?.Invoke();
        };
        try
        {
            availableImages = SystemImage.ParseAvailable(File.ReadAllText(ListCache));
            AvailableImagesUpdated = File.GetLastWriteTime(ListCache);
        }
        catch { }
        Reload();
        timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (_, _) => RefreshRunning();
        timer.Start();
        _ = LoadDeviceTypes();
    }

    private void Notify() => Changed?.Invoke();

    // Reading

    public void Reload()
    {
        Avds = Avd.LoadAll();
        if (Selection == null || Avds.All(a => a.Id != Selection) && SelectedPhysical == null && !(Selection == PhysicalHelpId && ShowsPhysicalHelp))
            Selection = Phones.Devices.FirstOrDefault()?.Id ?? Avds.FirstOrDefault()?.Id;
        Images = SystemImage.Merge(SystemImage.ScanInstalled(), availableImages);
        RefreshRunning(notify: false);
        MeasureSizes();
        Notify();
    }

    public Avd? SelectedAvd => Avds.FirstOrDefault(a => a.Id == Selection);
    public PhysicalDevice? SelectedPhysical => Phones.Device(Selection);
    /// The selection of the "connect a device" row, shown only while the SDK has no adb: it offers to install it.
    public const string PhysicalHelpId = "device:";
    public bool ShowsPhysicalHelp => !Phones.AdbInstalled;

    /// A physical device's row is keyed by its serial number once that is read; until then by adb's serial. A
    /// selected row whose key changed stays selected.
    private void FollowPhysicalSelection()
    {
        // The help row goes once adb is installed.
        if (Selection == PhysicalHelpId && !ShowsPhysicalHelp) Selection = Phones.Devices.FirstOrDefault()?.Id ?? Avds.FirstOrDefault()?.Id;
        if (Selection is not { } selection || !selection.StartsWith("device:") || selection == PhysicalHelpId || SelectedPhysical != null) return;
        var serial = selection["device:".Length..];
        if (Phones.Devices.FirstOrDefault(d => d.Serials.Contains(serial)) is { } device) Selection = device.Id;
    }

    public void Mirror(PhysicalDevice device) => Phones.Mirror(device.Id, sessions.Count + Phones.Sessions.Count);
    public IReadOnlyList<DeviceSession> RunningSessions => sessions.Values.ToList();

    public AvdStatus Status(Avd avd)
    {
        if (starting.Contains(avd.Id)) return new AvdStatus(AvdStatusKind.Booting);
        return sessionStates.TryGetValue(avd.Id, out var state) ? state switch
        {
            SessionState.Booting => new AvdStatus(AvdStatusKind.Booting),
            SessionState.Running => new AvdStatus(AvdStatusKind.Running, external.GetValueOrDefault(avd.Id)?.Serial),
            SessionState.Stopping => new AvdStatus(AvdStatusKind.Stopping),
            _ => ExternalStatus(avd),
        } : ExternalStatus(avd);
    }

    private AvdStatus ExternalStatus(Avd avd) =>
        external.TryGetValue(avd.Id, out var running) ? new AvdStatus(AvdStatusKind.External, running.Serial) : new AvdStatus(AvdStatusKind.Stopped);

    public DeviceType? DeviceTypeOf(Avd avd) => DeviceTypes.FirstOrDefault(t => t.Id == avd.DeviceTypeId);

    public DeviceCategory CategoryOf(Avd avd)
    {
        if (DeviceTypeOf(avd) is { } type) return type.Category;
        var tag = (avd.Config.GetValueOrDefault("tag.id") ?? "") + (avd.Sysdir ?? "");
        if (tag.Contains("wear")) return DeviceCategory.Wear;
        if (tag.Contains("tv")) return DeviceCategory.Tv;
        if (tag.Contains("automotive")) return DeviceCategory.Auto;
        return avd.IsLandscape ? DeviceCategory.Tablet : DeviceCategory.Phone;
    }

    public SystemImage? ImageOf(Avd avd) => Images.FirstOrDefault(i => i.Matches(avd.Sysdir));

    public DeviceThumbnail? Thumbnail(Avd avd) => sessions.GetValueOrDefault(avd.Id)?.Thumbnail();

    private void RefreshRunning() => RefreshRunning(notify: true);

    private void RefreshRunning(bool notify)
    {
        var found = RunningEmulator.Scan();
        if (found.Count == external.Count && !found.Except(external).Any()) return;
        external = found;
        var pids = found.Values.Select(e => e.Pid).ToHashSet();
        foreach (var gone in owners.Keys.Where(pid => !pids.Contains(pid)).ToList()) owners.Remove(gone);
        if (notify) Notify();
    }

    private void MeasureSizes()
    {
        var folders = Avds.Select(a => (a.Id, a.Folder)).ToList();
        var installed = Images.Where(i => i.Status == ImageStatus.Installed).Select(i => (i.Id, i.Folder)).ToList();
        _ = Task.Run(() =>
        {
            var avdSizes = folders.ToDictionary(f => f.Id, f => EmulatorShell.Sizes.AllocatedSize(f.Folder));
            var imageSizes = installed.ToDictionary(f => f.Id, f => EmulatorShell.Sizes.AllocatedSize(f.Folder));
            queue.TryEnqueue(() =>
            {
                Sizes = avdSizes;
                ImageSizes = imageSizes;
                Notify();
            });
        });
    }

    private async Task LoadDeviceTypes()
    {
        try
        {
            DeviceTypes = await DeviceType.Load();
            Notify();
        }
        catch (Exception error)
        {
            Log.Error($"cannot list device definitions: {error.Message}");
            Error?.Invoke($"无法读取设备类型列表：{error.Message}");
        }
    }

    /// `sdkmanager --list` needs the network and takes a while, so the last result is shown meanwhile.
    public async void RefreshAvailableImages()
    {
        if (LoadingAvailableImages) return;
        LoadingAvailableImages = true;
        Notify();
        try
        {
            var output = await Sdk.Run("sdkmanager", ["--list"]);
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                await File.WriteAllTextAsync(ListCache, output);
            }
            catch { }
            availableImages = SystemImage.ParseAvailable(output);
            AvailableImagesUpdated = DateTime.Now;
            Images = SystemImage.Merge(SystemImage.ScanInstalled(), availableImages);
        }
        catch (Exception error)
        {
            Log.Error($"sdkmanager --list failed: {error.Message}");
        }
        finally
        {
            LoadingAvailableImages = false;
            Notify();
        }
    }

    // Running devices

    public async void Start(Avd avd, bool coldBoot = false, IReadOnlyList<string>? extraArguments = null)
    {
        if (!Status(avd).IsStopped) return;
        var notes = new List<string>();
        if (avd.Config.GetValueOrDefault("hw.keyboard") != "yes")
        {
            try
            {
                avd = EnableKeyboard(avd);
                notes.Add("已开启键盘输入：没有它，按键和文字输入都到不了设备。硬件配置变了，这次会冷启动。");
            }
            catch (Exception error)
            {
                Error?.Invoke($"无法开启“{avd.Name}”的键盘输入：{error.Message}");
                return;
            }
        }
        if (coldBoot) notes.Add("冷启动：忽略快照，从头开机。");
        starting.Add(avd.Id);
        Notify();
        try
        {
            Register(await DeviceSession.Start(avd, FreeGrpcPort(), coldBoot, extraArguments ?? [], sessions.Count));
            Note = notes.Count == 0 ? null : string.Join("\n", notes);
        }
        catch (Exception error)
        {
            Log.Error($"cannot start {avd.Id}: {error}");
            Error?.Invoke($"无法启动“{avd.Name}”：{error.Message}");
        }
        finally
        {
            starting.Remove(avd.Id);
            Notify();
        }
    }

    /// Follows a session's state until it stops.
    private void Register(DeviceSession session)
    {
        var id = session.Avd.Id;
        sessions[id] = session;
        sessionStates[id] = session.State;
        session.StateChanged += state =>
        {
            sessionStates[id] = state;
            if (state == SessionState.Stopped)
            {
                sessions.Remove(id);
                sessionStates.Remove(id);
                RefreshRunning(notify: false);
                MeasureSizes();
                SessionsChanged?.Invoke();
            }
            Notify();
        };
        session.ManagerRequested += task => ManagerRequested?.Invoke(task);
        session.QuitRequested += () => QuitRequested?.Invoke();
        SessionsChanged?.Invoke();
    }

    /// Why a device another program runs cannot be shown in this app; null if it can.
    public string? AttachProblem(Avd avd)
    {
        if (!external.TryGetValue(avd.Id, out var running)) return null;
        if (running.GrpcPort == null) return "它启动时没有开启 gRPC，本 App 连不上。";
        if (running.UsesJwt && running.GrpcToken == null) return "它用 JWT 校验 gRPC 访问（新版 Android Studio 的做法），本 App 还不支持。";
        return null;
    }

    /// Shows a device another program runs in a window of this app, over its gRPC port. Closing the window leaves
    /// the device running.
    public async void Attach(Avd avd)
    {
        if (Status(avd).Kind != AvdStatusKind.External || !external.TryGetValue(avd.Id, out var running)) return;
        // Another Emulator Shell already shows it in a window like this one: a second would only repeat it.
        if (IsInOtherEmulatorShell(avd))
        {
            ShowInOwner(avd);
            return;
        }
        if (AttachProblem(avd) is { } problem)
        {
            Error?.Invoke($"无法在本 App 中显示“{avd.Name}”：{problem}");
            return;
        }
        if (avd.Config.GetValueOrDefault("hw.keyboard") != "yes")
            Note = "这台设备没开键盘输入（hw.keyboard），按键和文字输入到不了它；由本 App 启动时会自动开启。";
        starting.Add(avd.Id);
        Notify();
        try
        {
            Register(await DeviceSession.Attach(avd, running, sessions.Count));
        }
        catch (Exception error)
        {
            Error?.Invoke($"无法在本 App 中显示“{avd.Name}”：{error.Message}");
        }
        finally
        {
            starting.Remove(avd.Id);
            Notify();
        }
    }

    /// The program each external emulator was started from, by emulator pid; null once looked for and none found.
    private readonly Dictionary<int, ProcessOwner?> owners = [];

    /// The program a device another program runs was started from (see `ProcessOwner.Of`).
    public ProcessOwner? Owner(Avd avd)
    {
        if (!external.TryGetValue(avd.Id, out var running)) return null;
        if (!owners.TryGetValue(running.Pid, out var owner)) owners[running.Pid] = owner = ProcessOwner.Of(running.Pid);
        return owner;
    }

    /// Brings forward the program a device another program runs was started from; another Emulator Shell is also
    /// asked to bring that device's window forward.
    public void ShowInOwner(Avd avd) => Owner(avd)?.Activate(avd.Id);

    /// Whether a device another program runs was started, and is shown, by another copy of this app.
    public bool IsInOtherEmulatorShell(Avd avd) => Owner(avd)?.IsEmulatorShell ?? false;

    /// Brings this app's window of a device forward when another Emulator Shell asks.
    public void ListenForShowRequests() =>
        ShowRequests.Listen(avdId => sessions.GetValueOrDefault(avdId)?.BringToFront());

    /// Keys and text reach the guest through the emulator's virtual keyboard, which only exists with
    /// `hw.keyboard=yes`. avdmanager leaves it off (Android Studio turns it on), and gRPC `sendKey` then
    /// succeeds without delivering anything.
    private Avd EnableKeyboard(Avd avd)
    {
        var config = new IniFile(avd.ConfigPath);
        config["hw.keyboard"] = "yes";
        config.Write(avd.ConfigPath);
        Log.Info($"enabled hw.keyboard for {avd.Id}");
        var updated = avd with { Config = config.Values };
        var index = Avds.FindIndex(a => a.Id == avd.Id);
        if (index >= 0) Avds[index] = updated;
        return updated;
    }

    public void Stop(Avd avd)
    {
        if (sessions.TryGetValue(avd.Id, out var session))
        {
            session.Stop();
        }
        else if (external.TryGetValue(avd.Id, out var running))
        {
            // Through its gRPC port, which saves the quick-boot snapshot as closing its window would.
            _ = StopExternal(running);
        }
    }

    private async Task StopExternal(RunningEmulator running)
    {
        if (running.GrpcPort is { } port)
        {
            using var client = new EmulatorClient(port, running.GrpcToken);
            if (await client.ShutDown()) return;
        }
        // Without gRPC: the console's `kill` command, through adb.
        if (running.Serial is { } serial)
        {
            try
            {
                var adb = Path.Combine(Sdk.Root, "platform-tools", "adb.exe");
                using var process = Process.Start(new ProcessStartInfo(adb, ["-s", serial, "emu", "kill"]) { CreateNoWindow = true, UseShellExecute = false });
                if (process != null) await process.WaitForExitAsync();
            }
            catch (Exception error)
            {
                Error?.Invoke($"无法停止 {running.AvdId}：{error.Message}");
            }
        }
    }

    public void ShowWindow(Avd avd) => sessions.GetValueOrDefault(avd.Id)?.BringToFront();

    /// The first even port from 8554 that no emulator uses and nothing else is listening on.
    private int FreeGrpcPort()
    {
        var taken = sessions.Values.Select(s => s.GrpcPort).Concat(external.Values.Select(e => e.GrpcPort).OfType<int>()).ToHashSet();
        var port = 8554;
        while (taken.Contains(port) || !CanBind(port)) port += 2;
        return port;
    }

    private static bool CanBind(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // Creating and editing

    public AvdForm NewForm(string deviceTypeId = "pixel_9")
    {
        var type = DeviceTypes.FirstOrDefault(t => t.Id == deviceTypeId) ?? DeviceTypes.FirstOrDefault();
        return new AvdForm
        {
            DeviceTypeId = type?.Id ?? deviceTypeId,
            Name = UniqueName(type?.Name ?? "Pixel 9"),
            Landscape = type?.Category.IsLandscape() ?? false,
            ImageId = DefaultImage(type?.Category ?? DeviceCategory.Phone)?.Id,
        };
    }

    public AvdForm EditForm(Avd avd) => new()
    {
        EditingId = avd.Id,
        Name = avd.Name,
        NameTouched = true,
        DeviceTypeId = avd.DeviceTypeId ?? "",
        ImageId = ImageOf(avd)?.Id,
        RamMB = EmulatorShell.Sizes.Megabytes(avd.Config.GetValueOrDefault("hw.ramSize"))?.ToString("0") ?? "2048",
        DataGB = (EmulatorShell.Sizes.FormatConfigSize(avd.Config.GetValueOrDefault("disk.dataPartition.size")) ?? "6").Replace(" GB", ""),
        SdMB = (EmulatorShell.Sizes.FormatConfigSize(avd.Config.GetValueOrDefault("sdcard.size")) ?? "512").Replace(" MB", ""),
        HostGpu = !new[] { "swiftshader_indirect", "swiftshader", "swangle", "swangle_indirect", "lavapipe" }.Contains(avd.Config.GetValueOrDefault("hw.gpu.mode") ?? ""),
        ColdBoot = avd.IsColdBoot,
        CameraBack = avd.Config.GetValueOrDefault("hw.camera.back") ?? "virtualscene",
        CameraFront = avd.Config.GetValueOrDefault("hw.camera.front") ?? "emulated",
        Landscape = avd.IsLandscape,
    };

    public string UniqueName(string name)
    {
        var unique = name;
        var number = 2;
        while (Avds.Any(a => a.Id == Avd.IdFor(unique))) unique = $"{name} {number++}";
        return unique;
    }

    /// The newest installed image a device category can run, on this computer's processor.
    public SystemImage? DefaultImage(DeviceCategory category)
    {
        var usable = Images.Where(i => i.Family == category.Family() && i.Status == ImageStatus.Installed && i.RunsHere).ToList();
        return usable.FirstOrDefault(i => !i.IsPreview) ?? usable.FirstOrDefault();
    }

    public string? NameError(AvdForm form)
    {
        var id = Avd.IdFor(form.Name);
        if (form.Name.Trim().Length == 0) return "请输入名称。";
        if (form.IsEdit) return null;
        if (id.Length == 0) return "名称需要包含英文字母或数字。";
        if (Avds.Any(a => a.Id == id)) return $"已有名为“{id}”的模拟器。";
        return null;
    }

    public string? ImageProblem(AvdForm form, DeviceCategory category)
    {
        if (Images.FirstOrDefault(i => i.Id == form.ImageId) is not { } image || image.Family != category.Family())
            return $"还没有安装{category.Title()}可用的系统镜像。";
        if (Downloads.TryGetValue(image.Id, out var progress)) return $"正在下载 {image.ApiLabel}（{(int)(progress * 100)}%）…";
        if (image.Status == ImageStatus.Broken) return "这个镜像目录是空的，安装没有完成。重新下载后才能使用。";
        if (image.Status == ImageStatus.Available) return "这个镜像还没有下载。";
        if (!image.RunsHere) return $"这个镜像是 {image.PlatformLabel} 平台的，这台电脑（{Sdk.HostLabel}）运行不了。请换一个 {Sdk.HostLabel} 平台的镜像。";
        return null;
    }

    /// Creates or saves; null once done, so the dialog can close, or what went wrong.
    public async Task<string?> Submit(AvdForm form)
    {
        Busy.Add("form");
        Notify();
        try
        {
            if (form.EditingId is { } id)
            {
                if (Avds.FirstOrDefault(a => a.Id == id) is not { } avd) return null;
                Apply(form, avd, creating: false);
                Reload();
                Note = "已保存。改动在下次启动时生效。";
            }
            else
            {
                var created = await Create(form);
                Reload();
                Selection = created;
                Note = null;
            }
            return null;
        }
        catch (Exception error)
        {
            return error.Message;
        }
        finally
        {
            Busy.Remove("form");
            Notify();
        }
    }

    private async Task<string> Create(AvdForm form)
    {
        if (Images.FirstOrDefault(i => i.Id == form.ImageId) is not { } image) throw new ShellException("没有选择系统镜像");
        var id = Avd.IdFor(form.Name);
        var sd = int.TryParse(form.SdMB, out var mb) ? mb : 512;
        // avdmanager asks whether to make a custom hardware profile; "no" keeps the device definition's.
        await Sdk.Run("avdmanager", ["create", "avd", "-n", id, "-k", image.Package, "-d", form.DeviceTypeId, "-c", $"{sd}M"], input: "no\n");
        if (Avd.LoadAll().FirstOrDefault(a => a.Id == id) is not { } avd) throw new ShellException($"avdmanager 没有创建 {id}");
        Apply(form, avd, creating: true);
        return id;
    }

    private void Apply(AvdForm form, Avd avd, bool creating)
    {
        var config = new IniFile(avd.ConfigPath);
        config["avd.ini.displayname"] = form.Name.Trim();
        config["hw.ramSize"] = (int.TryParse(form.RamMB, out var ram) ? ram : 2048).ToString();
        config["disk.dataPartition.size"] = $"{(int.TryParse(form.DataGB, out var data) ? data : 6)}G";
        config["hw.gpu.mode"] = form.HostGpu ? "host" : "swiftshader_indirect";
        config["fastboot.forceColdBoot"] = form.ColdBoot ? "yes" : "no";
        config["hw.camera.back"] = form.CameraBack;
        config["hw.camera.front"] = form.CameraFront;
        config["hw.initialOrientation"] = form.Landscape ? "landscape" : "portrait";
        config["hw.keyboard"] = "yes";
        if (Images.FirstOrDefault(i => i.Id == form.ImageId) is { } image)
        {
            // avdmanager writes PlayStore.enabled=no even for Google Play images, which hides the Play Store.
            config["PlayStore.enabled"] = image.Tag.Contains("playstore") ? "true" : "false";
            if (!image.Matches(avd.Sysdir))
            {
                config["image.sysdir.1"] = image.Sysdir;
                config["tag.id"] = image.Tag;
                config["abi.type"] = image.Abi;
            }
            // The emulator goes by `hw.cpu.arch`, and refuses an image for another processor than it names.
            config["hw.cpu.arch"] = image.CpuArch;
        }
        if (creating && (config["skin.path"] == null || config["skin.name"] == null)
            && DeviceTypes.FirstOrDefault(t => t.Id == form.DeviceTypeId)?.SkinPath is { } skin)
        {
            config["skin.name"] = Path.GetFileName(skin);
            config["skin.path"] = skin;
            config["showDeviceFrame"] = "yes";
        }
        config.Write(avd.ConfigPath);
    }

    public async void Duplicate(Avd avd)
    {
        var form = EditForm(avd) with { EditingId = null };
        form.Name = UniqueName($"{avd.Name} 副本");
        if (await Submit(form) is { } error) Error?.Invoke(error);
    }

    // Removing

    /// Deletes the user data and snapshots, as a factory reset; the next boot starts like a new device.
    public void Wipe(Avd avd)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(avd.Folder))
        {
            var name = Path.GetFileName(entry);
            if (!name.StartsWith("userdata-qemu.img") && !name.StartsWith("cache.img") && name != "snapshots") continue;
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch { }
        }
        Note = "数据已擦除，下次启动会像新机一样开机。";
        MeasureSizes();
        Notify();
    }

    public async void Delete(Avd avd)
    {
        Busy.Add(avd.Id);
        Notify();
        try
        {
            await Sdk.Run("avdmanager", ["delete", "avd", "-n", avd.Id]);
            Note = null;
            Reload();
        }
        catch (Exception error)
        {
            Error?.Invoke($"无法删除“{avd.Name}”：{error.Message}");
        }
        finally
        {
            Busy.Remove(avd.Id);
            Notify();
        }
    }

    public void Reveal(Avd avd) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{avd.Folder}\"") { UseShellExecute = false });

    // System images

    public async void Download(SystemImage image)
    {
        if (Downloads.ContainsKey(image.Id)) return;
        Downloads[image.Id] = 0;
        var cancellation = new CancellationTokenSource();
        downloadTasks[image.Id] = cancellation;
        Notify();
        try
        {
            // An interrupted install leaves an empty folder that sdkmanager treats as installed.
            if (image.Status == ImageStatus.Broken && Directory.Exists(image.Folder) && !Directory.EnumerateFileSystemEntries(image.Folder).Any())
                Directory.Delete(image.Folder);
            await Sdk.Run("sdkmanager", ["--install", image.Package], input: string.Concat(Enumerable.Repeat("y\n", 8)), onLine: line =>
            {
                var match = Regex.Match(line, @"([0-9]+)(?=%)");
                if (!match.Success || !double.TryParse(match.Value, out var percent)) return;
                queue.TryEnqueue(() =>
                {
                    if (!Downloads.ContainsKey(image.Id)) return;
                    Downloads[image.Id] = percent / 100;
                    Notify();
                });
            }, cancellation: cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Error?.Invoke($"无法下载 {image.ApiLabel}：{error.Message}");
        }
        Downloads.Remove(image.Id);
        downloadTasks.Remove(image.Id);
        Images = SystemImage.Merge(SystemImage.ScanInstalled(), availableImages);
        MeasureSizes();
        Notify();
    }

    public void CancelDownload(SystemImage image) => downloadTasks.GetValueOrDefault(image.Id)?.Cancel();

    public bool IsInUse(SystemImage image) => Avds.Any(a => image.Matches(a.Sysdir));

    public async void Uninstall(SystemImage image)
    {
        Busy.Add(image.Id);
        Notify();
        try
        {
            await Sdk.Run("sdkmanager", ["--uninstall", image.Package]);
        }
        catch (Exception error)
        {
            Error?.Invoke($"无法删除 {image.ApiLabel}：{error.Message}");
        }
        Busy.Remove(image.Id);
        Images = SystemImage.Merge(SystemImage.ScanInstalled(), availableImages);
        Notify();
    }

    // Quitting

    public bool HasSessions => sessions.Count > 0 || starting.Count > 0 || Phones.HasWork;

    /// Shuts every device down, saving snapshots, and finishes the phones' recordings; the task ends once all
    /// emulators have exited and the files are complete.
    public Task StopAllSessions() => Task.WhenAll(Phones.StopAll(), DeviceSession.StopAll(sessions.Values.ToList()));
}
