namespace EmulatorShell;

static class Log
{
    public static readonly bool IsDebug = Environment.GetEnvironmentVariable("EMULATOR_SHELL_DEBUG") == "1";

    public static void Info(string message) => Write($"[emulator-shell] {message}");

    public static void Debug(string message)
    {
        if (IsDebug) Info(message);
    }

    public static void Error(string message) => Write($"[emulator-shell] ERROR {message}");

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
