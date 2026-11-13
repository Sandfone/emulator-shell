using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace EmulatorShell;

/// How the device manager groups AVDs and device definitions.
enum DeviceCategory { Phone, Fold, Tablet, Wear, Tv, Auto, Desktop }

enum ImageFamily { Phone, Wear, Tv, Auto, Other }

static class CategoryInfo
{
    public static readonly DeviceCategory[] All = Enum.GetValues<DeviceCategory>();

    public static string Title(this DeviceCategory category) => category switch
    {
        DeviceCategory.Phone => "手机",
        DeviceCategory.Fold => "可折叠",
        DeviceCategory.Tablet => "平板",
        DeviceCategory.Wear => "Wear OS",
        DeviceCategory.Tv => "电视",
        DeviceCategory.Auto => "汽车",
        _ => "桌面",
    };

    /// A Segoe Fluent Icons glyph.
    public static string Glyph(this DeviceCategory category) => category switch
    {
        DeviceCategory.Phone or DeviceCategory.Fold => "",
        DeviceCategory.Tablet => "",
        DeviceCategory.Wear => "",
        DeviceCategory.Tv => "",
        DeviceCategory.Auto => "",
        _ => "",
    };

    /// The kind of system image devices in this category run.
    public static ImageFamily Family(this DeviceCategory category) => category switch
    {
        DeviceCategory.Wear => ImageFamily.Wear,
        DeviceCategory.Tv => ImageFamily.Tv,
        DeviceCategory.Auto => ImageFamily.Auto,
        _ => ImageFamily.Phone,
    };

    public static bool IsLandscape(this DeviceCategory category) =>
        category is DeviceCategory.Tablet or DeviceCategory.Tv or DeviceCategory.Auto or DeviceCategory.Desktop;

    public static string Title(this ImageFamily family) => family switch
    {
        ImageFamily.Phone => "手机和平板",
        ImageFamily.Wear => "Wear OS",
        ImageFamily.Tv => "电视",
        ImageFamily.Auto => "汽车",
        _ => "其他",
    };
}

/// A hardware profile from `avdmanager list device`.
sealed record DeviceType(string Id, string Name, string Oem, DeviceCategory Category)
{
    /// The SDK's skin for this device, as an AVD's `skin.path` names it.
    public string? SkinPath
    {
        get
        {
            var folder = Path.Combine(Sdk.Root, "skins", Id);
            return Sdk.SkinArtwork(folder) != null ? folder : null;
        }
    }

    /// Where that skin's `layout` and images are.
    public string? SkinFolder => SkinPath is { } path ? Sdk.SkinArtwork(path) : null;

    public static async Task<List<DeviceType>> Load() => Parse(await Sdk.Run("avdmanager", ["list", "device"]));

    /// Blocks of `id: N or "pixel_9_pro"`, `Name: …`, `OEM : …`, `Tag : …`, separated by dashes.
    public static List<DeviceType> Parse(string output)
    {
        var types = new List<(DeviceType Type, int Order)>();
        var fields = new Dictionary<string, string>();
        void Flush()
        {
            if (fields.TryGetValue("id", out var id) && fields.TryGetValue("Name", out var name)
                && CategoryOf(id, name, fields.GetValueOrDefault("Tag") ?? "") is { } category)
            {
                types.Add((new DeviceType(id, name, fields.GetValueOrDefault("OEM") ?? "", category), types.Count));
            }
            fields.Clear();
        }
        foreach (var raw in output.Split('\n', '\r'))
        {
            var text = raw.Trim();
            if (text.StartsWith("id:"))
            {
                Flush();
                int open = text.IndexOf('"'), close = text.LastIndexOf('"');
                if (open >= 0 && close > open) fields["id"] = text[(open + 1)..close];
            }
            else if (text.StartsWith("---"))
            {
                Flush();
            }
            else if (text.IndexOf(':') is var colon && colon > 0)
            {
                fields[text[..colon].Trim()] = text[(colon + 1)..].Trim();
            }
        }
        Flush();
        // Newest Pixels first, then everything else in avdmanager's order.
        return types.OrderByDescending(t => PixelGeneration(t.Type.Name)).ThenBy(t => t.Order).Select(t => t.Type).ToList();
    }

    private static int PixelGeneration(string name)
    {
        if (!name.StartsWith("Pixel")) return -1;
        var digits = new string(name["Pixel".Length..].TrimStart().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var generation) ? generation : 1;
    }

    /// avdmanager tags only some categories; phones, foldables and tablets are told apart by name.
    /// XR and glasses profiles need images this app cannot show, so they are left out.
    private static DeviceCategory? CategoryOf(string id, string name, string tag)
    {
        tag = tag.ToLowerInvariant();
        var lower = (id + " " + name).ToLowerInvariant();
        if (tag.Contains("xr") || tag.Contains("glasses")) return null;
        if (tag.Contains("wear")) return DeviceCategory.Wear;
        if (tag.Contains("tv")) return DeviceCategory.Tv;
        if (tag.Contains("automotive")) return DeviceCategory.Auto;
        if (tag.Contains("desktop")) return DeviceCategory.Desktop;
        if (new[] { "fold", "flip", "rollable" }.Any(lower.Contains)) return DeviceCategory.Fold;
        if (new[] { "tablet", "freeform", "pixel_c", "nexus 7", "nexus 9", "nexus 10" }.Any(lower.Contains)) return DeviceCategory.Tablet;
        return DeviceCategory.Phone;
    }
}

enum ImageStatus { Installed, Broken, Available }

/// A system image for this computer's architecture, installed or offered by sdkmanager.
sealed record SystemImage(string ApiFolder, string Tag, string Abi)
{
    public ImageStatus Status { get; init; }
    public string Description { get; init; } = "";
    public string? Revision { get; init; }
    public string? ExtensionLevel { get; init; }

    public string Id => Package;
    /// The sdkmanager package path.
    public string Package => $"system-images;{ApiFolder};{Tag};{Abi}";
    /// The value of `image.sysdir.1` in an AVD using this image.
    public string Sysdir => $"system-images\\{ApiFolder}\\{Tag}\\{Abi}\\";
    public string Folder => Path.Combine(Sdk.Root, "system-images", ApiFolder, Tag, Abi);

    /// Whether an AVD's `image.sysdir.1` names this image, whichever slashes it uses.
    public bool Matches(string? sysdir) =>
        sysdir != null && Normalize(sysdir) == Normalize(Sysdir);

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    public string ApiLabel
    {
        get
        {
            var version = ApiFolder.Replace("android-", "");
            // `android-CANARY` is the rolling canary; dated ones are `android-canary-20261007`.
            if (version.Equals("canary", StringComparison.OrdinalIgnoreCase)) return "Canary";
            if (version.StartsWith("canary-"))
            {
                var date = version["canary-".Length..];
                return date.Length == 8 ? $"Canary {date[..4]}-{date[4..6]}-{date[6..]}" : $"Canary {date}";
            }
            if (version.IndexOf("-ext") is var ext and >= 0) return $"API {version[..ext]} · 扩展 {version[(ext + 4)..]}";
            if (version.IndexOf("-beta") is var beta and >= 0) return $"API {version[..beta]} Beta {version[(beta + 5)..]}";
            return $"API {version}";
        }
    }

    public string KindLabel
    {
        get
        {
            var plain = Tag.Replace("_ps16k", "").Replace("-ps16k", "");
            var page = plain == Tag ? "" : " · 16 KB 页";
            var names = new Dictionary<string, string>
            {
                ["google_apis_playstore"] = "Google Play", ["google_apis"] = "Google APIs", ["default"] = "AOSP",
                ["aosp_atd"] = "AOSP ATD", ["google_atd"] = "Google APIs ATD",
                ["google_apis_playstore_tablet"] = "Google Play 平板", ["google_apis_tablet"] = "Google APIs 平板",
                ["android-tv"] = "Android TV", ["google-tv"] = "Google TV",
                ["android-automotive-playstore"] = "Android Automotive · Google Play", ["android-automotive"] = "Android Automotive",
            };
            if (names.TryGetValue(plain, out var name)) return name + page;
            if (plain.Contains("wear") && Regex.Match(Description, @"Wear OS [0-9.]+") is { Success: true } match) return match.Value + page;
            return Description.Length == 0 ? plain : Description;
        }
    }

    public ImageFamily Family =>
        Tag.Contains("wear") ? ImageFamily.Wear
        : Tag.Contains("tv") ? ImageFamily.Tv
        : Tag.Contains("automotive") ? ImageFamily.Auto
        : Tag.Contains("glasses") || Tag.Contains("xr") ? ImageFamily.Other
        : ImageFamily.Phone;

    public bool IsPreview => new[] { "beta", "canary", "preview" }.Any(ApiFolder.ToLowerInvariant().Contains);

    /// Installed images under `system-images/*/*/<host ABI>`. A folder without `source.properties` is an
    /// interrupted install: sdkmanager lists it as version "unknown" and the emulator cannot boot it.
    public static List<SystemImage> ScanInstalled()
    {
        var root = Path.Combine(Sdk.Root, "system-images");
        var images = new List<SystemImage>();
        if (!Directory.Exists(root)) return images;
        foreach (var api in Directory.GetDirectories(root).Select(Path.GetFileName).Where(n => n!.StartsWith("android-")))
        {
            foreach (var tag in Directory.GetDirectories(Path.Combine(root, api!)).Select(Path.GetFileName))
            {
                var folder = Path.Combine(root, api!, tag!, Sdk.HostAbi);
                if (!Directory.Exists(folder)) continue;
                var properties = IniFile.TryLoad(Path.Combine(folder, "source.properties"));
                images.Add(new SystemImage(api!, tag!, Sdk.HostAbi)
                {
                    Status = properties == null ? ImageStatus.Broken : ImageStatus.Installed,
                    Description = properties?["Pkg.Desc"] ?? "",
                    Revision = properties?["Pkg.Revision"],
                    ExtensionLevel = properties?["AndroidVersion.ExtensionLevel"],
                });
            }
        }
        return images;
    }

    /// Rows of the "Available packages:" section of `sdkmanager --list`: path, version and description
    /// in space-padded columns, with `|` between them and `;` in package paths.
    public static List<SystemImage> ParseAvailable(string output)
    {
        var images = new List<SystemImage>();
        var inAvailable = false;
        foreach (var line in output.Split('\n', '\r'))
        {
            if (line.Length == 0) continue;
            if (!line.StartsWith(' '))
            {
                inAvailable = line.StartsWith("Available Packages", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inAvailable || line.Contains("->")) continue;
            var columns = (line.Contains('|') ? line.Split('|') : Regex.Split(line.Trim(), @"\s{2,}"))
                .Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
            if (columns.Length == 0) continue;
            var parts = columns[0].Split(';', '/');
            if (parts.Length != 4 || parts[0] != "system-images" || parts[3] != Sdk.HostAbi) continue;
            images.Add(new SystemImage(parts[1], parts[2], parts[3])
            {
                Status = ImageStatus.Available,
                Description = columns.Length > 2 ? columns[2] : "",
                Revision = columns.Length > 1 ? columns[1] : null,
            });
        }
        return images;
    }

    /// Installed and broken images, plus offered ones not on disk, newest first.
    public static List<SystemImage> Merge(List<SystemImage> installed, List<SystemImage> available)
    {
        var byId = new Dictionary<string, SystemImage>();
        foreach (var image in installed) byId.TryAdd(image.Id, image);
        foreach (var image in available)
        {
            if (byId.TryGetValue(image.Id, out var existing))
            {
                if (existing.Description.Length == 0) byId[image.Id] = existing with { Description = image.Description };
            }
            else
            {
                byId[image.Id] = image;
            }
        }
        return byId.Values
            .OrderByDescending(i => i.ApiFolder, NumericComparer.Instance)
            .ThenBy(i => i.Tag, StringComparer.Ordinal)
            .ToList();
    }
}

/// Compares strings with runs of digits as numbers: `android-9` before `android-10`.
sealed class NumericComparer : IComparer<string>
{
    public static readonly NumericComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string? a, string? b) => StrCmpLogicalW(a ?? "", b ?? "");
}

/// An Android Virtual Device as described by `<avd home>\<id>.ini` and its `config.ini`.
sealed record Avd(string Id, string Folder, IReadOnlyDictionary<string, string> Config)
{
    public string Name => Config.GetValueOrDefault("avd.ini.displayname") ?? Id.Replace('_', ' ');
    public string? DeviceTypeId => Config.GetValueOrDefault("hw.device.name");
    public string Manufacturer => Config.GetValueOrDefault("hw.device.manufacturer") ?? "";
    public string? Sysdir => Config.GetValueOrDefault("image.sysdir.1");
    public bool IsColdBoot => Config.GetValueOrDefault("fastboot.forceColdBoot") == "yes";
    public bool IsLandscape => Config.GetValueOrDefault("hw.initialOrientation") == "landscape";

    public string? Resolution =>
        Config.GetValueOrDefault("hw.lcd.width") is { } width && Config.GetValueOrDefault("hw.lcd.height") is { } height
            ? $"{width} × {height}" + (Config.GetValueOrDefault("hw.lcd.density") is { } dpi ? $" · {dpi} dpi" : "")
            : null;

    /// Where the skin's `layout` and images are.
    public string? SkinFolder =>
        Config.GetValueOrDefault("skin.path") is { } path
            ? Sdk.SkinArtwork(Path.IsPathRooted(path) ? path : Path.Combine(Sdk.Root, path))
            : null;

    /// A foldable skin's artwork for the outer display, shown while the device is closed: `closed` beside
    /// the `default` folder (Pixel Fold models).
    public string? ClosedSkinFolder
    {
        get
        {
            if (SkinFolder is not { } folder || !Path.GetFileName(folder).Equals("default", StringComparison.OrdinalIgnoreCase)) return null;
            var closed = Path.Combine(Path.GetDirectoryName(folder)!, "closed");
            return Sdk.SkinArtwork(closed) == closed ? closed : null;
        }
    }

    public string ConfigPath => Path.Combine(Folder, "config.ini");

    public static List<Avd> LoadAll()
    {
        if (!Directory.Exists(Sdk.AvdHome)) return [];
        return Directory.GetFiles(Sdk.AvdHome, "*.ini").Select(file =>
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var pointer = IniFile.TryLoad(file);
            var folder = pointer?["path"] is { Length: > 0 } path && Directory.Exists(path) ? path : Path.Combine(Sdk.AvdHome, id + ".avd");
            var config = IniFile.TryLoad(Path.Combine(folder, "config.ini"));
            return config == null ? null : new Avd(id, folder, config.Values);
        }).OfType<Avd>().OrderBy(a => a.Name, NumericComparer.Instance).ToList();
    }

    /// The AVD name avdmanager accepts for a display name: letters, digits, `.`, `_` and `-`.
    public static string IdFor(string name) =>
        Regex.Replace(name.Trim(), "[^A-Za-z0-9._-]+", "_").Trim('_');

    public bool Equals(Avd? other) => other != null && other.Id == Id && other.Folder == Folder
        && other.Config.Count == Config.Count && !other.Config.Except(Config).Any();

    public override int GetHashCode() => HashCode.Combine(Id, Folder);
}

/// An emulator process found through the files emulators write while they run, whoever started them.
/// `GrpcToken` is set when it runs with `-grpc-use-token`; `UsesJwt` when it runs with `-grpc-use-jwt` (newer
/// Android Studio), whose calls need a token signed with a key registered in that folder, which this app does
/// not do.
sealed record RunningEmulator(int Pid, string AvdId, int? SerialPort, int? GrpcPort, string? GrpcToken = null, bool UsesJwt = false)
{
    public string? Serial => SerialPort is { } port ? $"emulator-{port}" : null;

    public static Dictionary<string, RunningEmulator> Scan()
    {
        var found = new Dictionary<string, RunningEmulator>();
        var folder = Sdk.RunningEmulatorsDirectory;
        if (!Directory.Exists(folder)) return found;
        foreach (var file in Directory.GetFiles(folder, "pid_*.ini"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!int.TryParse(name["pid_".Length..], out var pid) || !IsEmulator(pid)) continue;
            if (IniFile.TryLoad(file) is not { } info || info["avd.id"] is not { } avdId) continue;
            found[avdId] = new RunningEmulator(pid, avdId,
                int.TryParse(info["port.serial"], out var serial) ? serial : null,
                int.TryParse(info["grpc.port"], out var grpc) ? grpc : null,
                info["grpc.token"], info["grpc.jwks"] != null);
        }
        return found;
    }

    /// The files stay behind when an emulator is killed, and Windows reuses process ids.
    private static bool IsEmulator(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && (process.ProcessName.StartsWith("qemu", StringComparison.OrdinalIgnoreCase)
                                          || process.ProcessName.StartsWith("emulator", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}

static class Sizes
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);

    /// Bytes a folder takes on disk; sparse and compressed files count as what they occupy.
    public static long AllocatedSize(string folder)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var low = GetCompressedFileSizeW(file, out var high);
                if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0) total += new FileInfo(file).Length;
                else total += ((long)high << 32) | low;
            }
        }
        catch { }
        return total;
    }

    /// "6G" → "6 GB", "512M" → "512 MB"; plain numbers are bytes.
    public static string? FormatConfigSize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var units = new Dictionary<char, string> { ['K'] = "KB", ['M'] = "MB", ['G'] = "GB", ['T'] = "TB" };
        if (units.TryGetValue(char.ToUpperInvariant(value[^1]), out var unit)) return $"{value[..^1]} {unit}";
        return long.TryParse(value, out var bytes) ? Format(bytes, binary: true) : value;
    }

    /// `hw.ramSize` and the like in megabytes: "2G" → 2048, "1536M" and "1536" → 1536.
    public static double? Megabytes(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var factor = char.ToUpperInvariant(value[^1]) switch { 'G' => 1024.0, 'M' => 1.0, 'K' => 1 / 1024.0, _ => 0 };
        var number = factor == 0 ? value : value[..^1];
        return double.TryParse(number, out var amount) ? amount * (factor == 0 ? 1 : factor) : null;
    }

    /// Like Finder's sizes: "1.2 GB", "512 KB".
    public static string Format(long bytes, bool binary = false)
    {
        double step = binary ? 1024 : 1000;
        string[] units = ["字节", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= step && unit < units.Length - 1)
        {
            value /= step;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[0]}" : $"{value.ToString(value < 10 ? "0.#" : "0")} {units[unit]}";
    }
}
