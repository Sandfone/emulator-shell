using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EmulatorShell;

/// Where the Android SDK and its AVDs live, and how to run the SDK's command-line tools.
static class Sdk
{
    /// The folder with a skin's `layout`: the skin folder itself, or for a foldable its `default` folder,
    /// which shows the device unfolded (`closed` holds the folded one).
    public static string? SkinArtwork(string folder) =>
        new[] { folder, Path.Combine(folder, "default") }.FirstOrDefault(f => File.Exists(Path.Combine(f, "layout")));

    public static string Root
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("ANDROID_HOME");
            if (string.IsNullOrEmpty(path)) path = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
            // Some launchers pass the path with an unexpanded `~`.
            if (!string.IsNullOrEmpty(path)) return ExpandHome(path);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
        }
    }

    public static string AvdHome
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("ANDROID_AVD_HOME");
            if (!string.IsNullOrEmpty(path)) return ExpandHome(path);
            var user = Environment.GetEnvironmentVariable("ANDROID_USER_HOME");
            if (!string.IsNullOrEmpty(user)) return Path.Combine(ExpandHome(user), "avd");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd");
        }
    }

    /// Each running emulator writes `pid_<pid>.ini` here (the pid of its qemu process) with its AVD, adb and
    /// gRPC ports.
    public static string RunningEmulatorsDirectory => Path.Combine(Path.GetTempPath(), "avd", "running");

    /// The real emulator launcher. With the forwarder installed (`EmulatorForwarder`) the SDK's `emulator.exe`
    /// is the forwarder and the original sits beside it as `emulator.real.exe`.
    public static string EmulatorExecutable
    {
        get
        {
            var folder = Path.Combine(Root, "emulator");
            var real = Path.Combine(folder, "emulator.real.exe");
            return File.Exists(real) ? real : Path.Combine(folder, "emulator.exe");
        }
    }

    /// The ABI of system images this computer runs: the emulator only accelerates its own architecture.
    public static string HostAbi => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64-v8a" : "x86_64";

    /// A tool from `cmdline-tools/latest`, or from the newest other version installed.
    public static string Tool(string name)
    {
        var tools = Path.Combine(Root, "cmdline-tools");
        var latest = Path.Combine(tools, "latest", "bin", name + ".bat");
        if (File.Exists(latest) || !Directory.Exists(tools)) return latest;
        return Directory.GetDirectories(tools)
            .Select(folder => Path.Combine(folder, "bin", name + ".bat"))
            .Where(File.Exists)
            .OrderByDescending(path => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))), out var v) ? v : new Version())
            .FirstOrDefault() ?? latest;
    }

    /// avdmanager and sdkmanager need Java 17 or newer. Without a JAVA_HOME (or with an old one) they would run
    /// whatever `java` is on the PATH, so Android Studio's bundled runtime is used instead.
    public static IReadOnlyDictionary<string, string> ToolEnvironment => toolEnvironment.Value;

    private static readonly Lazy<Dictionary<string, string>> toolEnvironment = new(() =>
    {
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        env["ANDROID_HOME"] = Root;
        env.TryGetValue("JAVA_HOME", out var current);
        if (string.IsNullOrEmpty(current) || IsOldJava(current))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidates = new[]
            {
                Path.Combine(local, "Programs", "Android Studio", "jbr"),
                Path.Combine(programs, "Android", "Android Studio", "jbr"),
                Path.Combine(local, "Programs", "Android Studio Preview", "jbr"),
            };
            var home = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "bin", "java.exe")));
            if (home != null) env["JAVA_HOME"] = home;
        }
        return env;
    });

    /// Java 8 and 11, which the current command-line tools refuse; read from the runtime's `release` file.
    private static bool IsOldJava(string home)
    {
        try
        {
            var release = File.ReadAllText(Path.Combine(home, "release"));
            return release.Contains("JAVA_VERSION=\"1.") || release.Contains("JAVA_VERSION=\"11");
        }
        catch
        {
            return false;
        }
    }

    /// Runs an SDK tool and returns its output. `onLine` sees each line as it arrives, including lines ended by
    /// a carriage return, which is how sdkmanager redraws its progress bar. Cancelling stops the tool.
    public static async Task<string> Run(string tool, IEnumerable<string> arguments, string? input = null,
                                         Action<string>? onLine = null, CancellationToken cancellation = default)
    {
        var path = Tool(tool);
        if (!File.Exists(path)) throw new ShellException($"找不到 {path}。请在 Android Studio 的 SDK Manager 里安装 Android SDK Command-line Tools。");
        var start = new ProcessStartInfo
        {
            // The .bat runs in cmd.exe, which splits arguments at `;` and `,` unless they are quoted; `/s` makes it
            // strip just the outer quotes, whatever the path contains.
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /s /c \"{QuoteForBatch(path)} {string.Join(" ", arguments.Select(QuoteForBatch))}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment.Clear();
        foreach (var (key, value) in ToolEnvironment) start.Environment[key] = value;

        using var process = new Process { StartInfo = start };
        var collector = new OutputCollector(onLine);
        process.Start();
        var pumps = Task.WhenAll(collector.Pump(process.StandardOutput), collector.Pump(process.StandardError));
        try
        {
            if (input != null) await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        catch (IOException) { }

        using (cancellation.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } }))
        {
            await process.WaitForExitAsync(CancellationToken.None);
            await pumps;
        }
        cancellation.ThrowIfCancellationRequested();
        var text = collector.Text;
        if (process.ExitCode != 0)
        {
            var tail = string.Join("\n", text.Split('\n', '\r').Where(l => l.Trim().Length > 0).TakeLast(3));
            throw new ShellException($"{tool} 失败（{process.ExitCode}）：{tail}");
        }
        return text;
    }

    private static string QuoteForBatch(string argument) =>
        argument.Length > 0 && argument.IndexOfAny([' ', ';', ',', '=', '&', '|', '^', '(', ')', '"']) < 0
            ? argument
            : "\"" + argument.Replace("\"", "\"\"") + "\"";

    private static string ExpandHome(string path) =>
        path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path;
}

/// Gathers a tool's output and hands out complete lines.
sealed class OutputCollector(Action<string>? onLine)
{
    private readonly StringBuilder text = new();
    private readonly object gate = new();

    public async Task Pump(StreamReader reader)
    {
        var buffer = new char[4096];
        var pending = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            lock (gate) text.Append(buffer, 0, count);
            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c is '\n' or '\r')
                {
                    if (pending.Length > 0) onLine?.Invoke(pending.ToString());
                    pending.Clear();
                }
                else
                {
                    pending.Append(c);
                }
            }
        }
        if (pending.Length > 0) onLine?.Invoke(pending.ToString());
    }

    public string Text
    {
        get { lock (gate) return text.ToString(); }
    }
}

/// `key=value` lines such as an AVD's `config.ini`. Edits keep the other lines, their order and line endings.
sealed class IniFile
{
    private readonly List<string> lines;
    private readonly string newline;

    public IniFile(string path)
    {
        var text = File.ReadAllText(path);
        newline = text.Contains("\r\n") ? "\r\n" : "\n";
        lines = text.Replace("\r\n", "\n").Split('\n').ToList();
    }

    public static IniFile? TryLoad(string path)
    {
        try { return new IniFile(path); } catch { return null; }
    }

    public Dictionary<string, string> Values
    {
        get
        {
            var values = new Dictionary<string, string>();
            foreach (var line in lines)
            {
                var index = line.IndexOf('=');
                if (index > 0) values[line[..index].Trim()] = line[(index + 1)..].Trim();
            }
            return values;
        }
    }

    public string? this[string key]
    {
        get => Values.GetValueOrDefault(key);
        set
        {
            var index = lines.FindIndex(l => l.IndexOf('=') is var i && i > 0 && l[..i].Trim() == key);
            if (index >= 0 && value != null) lines[index] = $"{key}={value}";
            else if (index >= 0) lines.RemoveAt(index);
            else if (value != null)
            {
                var end = lines.Count > 0 && lines[^1].Length == 0 ? lines.Count - 1 : lines.Count;
                lines.Insert(end, $"{key}={value}");
            }
        }
    }

    public void Write(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, string.Join(newline, lines));
        File.Move(temporary, path, overwrite: true);
    }
}
