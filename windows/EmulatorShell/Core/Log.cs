namespace EmulatorShell;

static class Log
{
    public static readonly bool IsDebug = Environment.GetEnvironmentVariable("EMULATOR_SHELL_DEBUG") == "1";

    public static void Info(string message) => Write($"[emulator-shell] {message}");

    public static void Debug(string message)
    {
        if (IsDebug) Info(message);
    }

    public static void Error(string message)
    {
        Write($"[emulator-shell] ERROR {message}");
        Keep(message);
    }

    /// Errors also go to a file, `Logs\emulator-shell.log` in the settings folder (beside the forwarder's log): the
    /// app usually runs without anyone reading its output, and a crash should leave behind what caused it. Past
    /// 1 MB it starts over, the previous file kept as `emulator-shell.old.log`.
    private static readonly string Folder = Path.Combine(Settings.Folder, "Logs");
    public static string FilePath { get; } = Path.Combine(Folder, "emulator-shell.log");

    private static readonly Lock FileLock = new();

    private static void Keep(string message)
    {
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1024 * 1024)
                    File.Move(FilePath, Path.Combine(Folder, "emulator-shell.old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// Launchers such as Flutter close the pipe they read this output from once the device is up; writing
    /// then must fail quietly rather than end the app.
    private static void Write(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
            Console.Error.Flush();
        }
        catch { }
        System.Diagnostics.Debug.WriteLine(line);
    }
}

sealed class ShellException(string message) : Exception(message)
{
    public override string ToString() => Message;
}
