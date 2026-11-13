using System.Diagnostics;
using System.Text;

namespace EmulatorShell;

/// Launches `emulator -avd <name> -no-window -gpu <mode> -grpc <port>` and stops it on request.
///
/// On Windows `emulator.exe` is a launcher that runs the emulator itself as a child process
/// (`qemu-system-x86_64-headless.exe`) and exits with it. The emulator saves its quick-boot snapshot when it
/// is shut down over gRPC (`EmulatorClient.ShutDown`); a console Ctrl+C reaches the launcher too, whose exit
/// takes the emulator down mid-save, so that is not used.
sealed class EmulatorProcess(string avdName, int grpcPort, string gpu = "host", IReadOnlyList<string>? extraArguments = null)
{
    public string AvdName { get; } = avdName;
    public int GrpcPort { get; } = grpcPort;
    private Process? process;
    private StreamWriter? log;

    /// How long an emulator may take to save its quick-boot snapshot and exit before it is considered stuck.
    /// Saving has taken over 30 s on macOS.
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(90);

    /// Raised on an arbitrary thread when the emulator exits, however that happens.
    public event Action? Exited;

    public void Start(string logFile)
    {
        var emulator = Sdk.EmulatorExecutable;
        if (!File.Exists(emulator)) throw new ShellException($"找不到模拟器程序 {emulator}");
        log = new StreamWriter(logFile, append: false, Encoding.UTF8) { AutoFlush = true };

        var start = new ProcessStartInfo(emulator)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in new[] { "-avd", AvdName, "-no-window", "-gpu", gpu, "-grpc", GrpcPort.ToString() }
                     .Concat(extraArguments ?? []))
        {
            start.ArgumentList.Add(argument);
        }
        // Tells the forwarder, should this still reach it, to run the real emulator instead of this app.
        start.Environment[EmulatorForwarder.PassThroughVariable] = "1";
        // Asked to shut down, the emulator kills itself after 20 s by default, even in the middle of saving the
        // snapshot, which is then lost.
        start.Environment["ANDROID_EMULATOR_WAIT_TIME_BEFORE_KILL"] = ((int)ShutdownTimeout.TotalSeconds).ToString();

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Exited += (_, _) => Exited?.Invoke();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        this.process = process;
    }

    private void Append(string? line)
    {
        if (line == null) return;
        try
        {
            lock (this) log?.WriteLine(line);
        }
        catch { }
    }

    public bool IsRunning
    {
        get
        {
            try { return process is { HasExited: false }; }
            catch { return false; }
        }
    }

    /// Ends the launcher and the emulator under it at once, without saving a snapshot.
    public void Terminate()
    {
        if (!IsRunning) return;
        try { process!.Kill(entireProcessTree: true); } catch { }
    }

    /// Waits up to `ShutdownTimeout` for the emulator to exit on its own, then kills it.
    public void WaitOrTerminate()
    {
        var timeout = ShutdownTimeout;
        if (process == null) return;
        try
        {
            if (!process.WaitForExit(timeout)) Terminate();
        }
        catch { }
    }
}
