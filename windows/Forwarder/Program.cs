// Emulator Shell forwarder. Installed in place of the SDK's emulator\emulator.exe, with the original beside it as
// emulator.real.exe and Emulator Shell's path in emulator-shell.ini. Device launches (`-avd X` or `@X`) open in
// Emulator Shell; everything else runs the original emulator. Either way this process waits for the program it
// started and returns its exit code, so a launcher that watches the emulator process (Android Studio does) sees
// it end when the device window closes.
using System.Diagnostics;

var folder = AppContext.BaseDirectory;
var real = Path.Combine(folder, "emulator.real.exe");
var app = ReadApp(Path.Combine(folder, "emulator-shell.ini"));
LogCall(args);

if (Environment.GetEnvironmentVariable("EMULATOR_SHELL_PASSTHROUGH") is { Length: > 0 } || app == null || !File.Exists(app))
    return Run(real, args);

string[] passThrough =
[
    "-no-window", "-list-avds", "-version", "-accel-check", "-snapshot-list", "-check-snapshot-loadable", "-read-only",
];
string? avd = null;
var rest = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    var argument = args[i];
    if (argument == "-avd" && i + 1 < args.Length) avd = args[++i];
    else if (argument.StartsWith('@')) avd = argument[1..];
    else if (passThrough.Contains(argument) || argument.StartsWith("-help")) return Run(real, args);
    else rest.Add(argument);
}
return avd == null ? Run(real, args) : Run(app, [avd, .. rest]);

static int Run(string program, IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo(program) { UseShellExecute = false };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    // Ctrl+C reaches the program too, which shares this console; this process just waits for it to end.
    Console.CancelKeyPress += (_, e) => e.Cancel = true;
    try
    {
        using var process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"emulator-shell forwarder: cannot run {program}: {error.Message}");
        return 1;
    }
}

static string? ReadApp(string config)
{
    try
    {
        return File.ReadAllLines(config).Where(l => l.StartsWith("app=")).Select(l => l[4..].Trim().Trim('"')).FirstOrDefault();
    }
    catch
    {
        return null;
    }
}

static void LogCall(string[] args)
{
    try
    {
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmulatorShell", "Logs");
        Directory.CreateDirectory(logs);
        File.AppendAllText(Path.Combine(logs, "forwarder.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {string.Join(' ', args)}\r\n");
    }
    catch { }
}
