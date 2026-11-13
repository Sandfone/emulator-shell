using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace EmulatorShell;

/// Follows the guest's log for lines matching a pattern, from when it starts, through `adb logcat`: the
/// emulator's own `streamLogcat` sends nothing. The filtering happens in the guest, so only matching lines
/// cross over. adb gives up while the device is not up yet (or not authorised), so it is run again after a
/// pause whenever it ends, until `Stop()`.
sealed class GuestLogWatch
{
    private readonly Func<string?> serial;
    private readonly string pattern;
    private readonly Action<string> onLine;
    private readonly CancellationTokenSource stopping = new();

    /// `serial` is asked again on each run: the emulator writes it down only once it is up.
    public GuestLogWatch(Func<string?> serial, string pattern, Action<string> onLine)
    {
        this.serial = serial;
        this.pattern = pattern;
        this.onLine = onLine;
        _ = Task.Run(Run);
    }

    public void Stop() => stopping.Cancel();

    private async Task Run()
    {
        var adb = Path.Combine(Sdk.Root, "platform-tools", "adb.exe");
        while (!stopping.IsCancellationRequested)
        {
            if (serial() is { } device && File.Exists(adb))
            {
                try
                {
                    // -T 1: from the newest line on, not what the log already holds.
                    var start = new ProcessStartInfo(adb, ["-s", device, "logcat", "-T", "1", "-v", "brief", "-e", pattern])
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    };
                    using var process = Process.Start(start)!;
                    using var cancel = stopping.Token.Register(() => { try { process.Kill(); } catch { } });
                    _ = process.StandardError.ReadToEndAsync();
                    while (await process.StandardOutput.ReadLineAsync() is { } line)
                    {
                        // -T 1 shows the one newest line too, whatever it is.
                        if (Regex.IsMatch(line, pattern)) onLine(line);
                    }
                }
                catch (Exception error)
                {
                    Log.Debug($"adb logcat: {error.Message}");
                }
            }
            try { await Task.Delay(3000, stopping.Token); } catch (OperationCanceledException) { }
        }
    }
}
