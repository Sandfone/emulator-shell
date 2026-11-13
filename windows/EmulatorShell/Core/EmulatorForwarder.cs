using System.Diagnostics;

namespace EmulatorShell;

/// Makes Android Studio, Flutter and the `android` CLI open devices in this app. They all run the SDK's
/// `emulator\emulator.exe`, with no setting for another program, so that file is replaced by a small forwarder
/// (`EmulatorShellForwarder.exe`, built from windows\Forwarder): device launches go to this app, everything else
/// (`-list-avds`, `-version`, headless `-no-window` runs such as Gradle Managed Devices) to the original, kept
/// beside it as `emulator.real.exe`. The forwarder reads this app's path from `emulator-shell.ini` next to it.
static class EmulatorForwarder
{
    public enum ForwarderState { NotInstalled, Installed, ReplacedBySdkUpdate, Foreign }

    /// Set for emulators this app starts itself, so a forwarder that still receives the call runs the real one.
    public const string PassThroughVariable = "EMULATOR_SHELL_PASSTHROUGH";

    /// The forwarder's product name, by which an installed one is recognised.
    private const string ProductName = "Emulator Shell Forwarder";
    private const string ForwarderFile = "EmulatorShellForwarder.exe";

    private static string Folder => Path.Combine(Sdk.Root, "emulator");
    private static string Launcher => Path.Combine(Folder, "emulator.exe");
    private static string Real => Path.Combine(Folder, "emulator.real.exe");
    private static string Config => Path.Combine(Folder, "emulator-shell.ini");

    /// This executable, where the forwarder points.
    public static string AppPath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "EmulatorShell.exe");

    /// The forwarder shipped next to this app.
    private static string Source => Path.Combine(AppContext.BaseDirectory, ForwarderFile);

    public static bool IsAvailable => File.Exists(Source);

    public static ForwarderState State
    {
        get
        {
            if (!File.Exists(Launcher)) return File.Exists(Real) ? ForwarderState.ReplacedBySdkUpdate : ForwarderState.NotInstalled;
            if (IsForwarder(Launcher)) return ForwarderState.Installed;
            // The SDK's own binary.
            return File.Exists(Real) ? ForwarderState.ReplacedBySdkUpdate : ForwarderState.NotInstalled;
        }
    }

    /// The app the installed forwarder opens devices in.
    public static string? InstalledAppPath =>
        IniFile.TryLoad(Config)?["app"];

    private static bool IsForwarder(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductName == ProductName; }
        catch { return false; }
    }

    public static void Install()
    {
        if (!File.Exists(Source)) throw new ShellException($"找不到 {Source}，请用 windows\\build.ps1 构建。");
        switch (State)
        {
            case ForwarderState.NotInstalled:
            case ForwarderState.ReplacedBySdkUpdate:
                // After an SDK update the new binary replaces the old `emulator.real.exe`, so launcher and qemu match.
                if (File.Exists(Real)) File.Delete(Real);
                File.Move(Launcher, Real);
                break;
            case ForwarderState.Installed:
                break;
        }
        File.Copy(Source, Launcher, overwrite: true);
        File.WriteAllText(Config, $"# Emulator Shell forwarder: device launches open in this app; everything else runs emulator.real.exe.\r\n" +
                                  $"# Remove it from Emulator Shell's menu, or move emulator.real.exe back over emulator.exe.\r\n" +
                                  $"app={AppPath}\r\n");
        Log.Info($"installed the emulator forwarder in {Folder}");
    }

    public static void Uninstall()
    {
        if (State != ForwarderState.Installed) return;
        File.Delete(Launcher);
        File.Move(Real, Launcher);
        try { File.Delete(Config); } catch { }
        Log.Info($"restored the SDK's emulator in {Folder}");
    }

    /// The emulator arguments a launcher passed that this app can hand on. It runs the emulator without
    /// a window and with its own gRPC port and GPU mode, so those flags (and Studio's tool-window ones) go.
    public static List<string> Forwardable(IEnumerable<string> arguments)
    {
        string[] dropped = ["-no-window", "-qt-hide-window", "-grpc-use-token", "-grpc-use-jwt"];
        string[] droppedWithValue = ["-grpc", "-gpu", "-idle-grpc-timeout", "-avd"];
        var result = new List<string>();
        var skipNext = false;
        foreach (var argument in arguments)
        {
            if (skipNext) { skipNext = false; continue; }
            if (droppedWithValue.Contains(argument)) { skipNext = true; continue; }
            if (dropped.Contains(argument) || argument.StartsWith('@') || argument == "--") continue;
            result.Add(argument);
        }
        return result;
    }

    // Command-line tool

    /// `emulator-shell <avd>` for the terminal: a .cmd in the per-user folder Windows keeps on the PATH.
    public static string CommandPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "emulator-shell.cmd");

    private const string CommandMarker = "rem Emulator Shell command-line tool";

    public static bool IsCommandInstalled
    {
        get
        {
            try { return File.ReadAllText(CommandPath).Contains($"\"{AppPath}\""); }
            catch { return false; }
        }
    }

    public static void InstallCommand()
    {
        if (File.Exists(CommandPath) && !File.ReadAllText(CommandPath).Contains(CommandMarker))
            throw new ShellException($"{CommandPath} 已经存在，而且不是 Emulator Shell 写的，没有改动它。");
        Directory.CreateDirectory(Path.GetDirectoryName(CommandPath)!);
        File.WriteAllText(CommandPath, $"@echo off\r\n{CommandMarker}, installed by {AppPath}.\r\n\"{AppPath}\" %*\r\n");
    }

    public static void UninstallCommand()
    {
        if (File.Exists(CommandPath) && File.ReadAllText(CommandPath).Contains(CommandMarker)) File.Delete(CommandPath);
    }
}
