using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace EmulatorShell;

/// The program a running emulator was started from, and a way to bring it forward.
sealed partial record ProcessOwner(int Pid, string Name, nint MainWindow)
{
    /// Whether it is another copy of this app.
    public bool IsEmulatorShell => Name.Equals("EmulatorShell", StringComparison.OrdinalIgnoreCase);

    /// The nearest of the emulator process and its ancestors with a window of its own (Android Studio, a
    /// terminal, another Emulator Shell, or the emulator itself when it shows its own window); null if none, or
    /// if that is this app.
    public static ProcessOwner? Of(int pid)
    {
        var parents = Parents();
        var seen = new HashSet<int>();
        while (pid > 4 && seen.Add(pid))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.MainWindowHandle != 0)
                {
                    return process.Id == Environment.ProcessId ? null : new ProcessOwner(process.Id, process.ProcessName, process.MainWindowHandle);
                }
            }
            catch
            {
                return null;
            }
            if (!parents.TryGetValue(pid, out pid)) break;
        }
        return null;
    }

    /// A user-facing name: the program's description ("Android Studio") or its file name.
    public string DisplayName
    {
        get
        {
            try
            {
                using var process = Process.GetProcessById(Pid);
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                return string.IsNullOrWhiteSpace(description) ? Name : description;
            }
            catch
            {
                return Name;
            }
        }
    }

    /// Brings the program's window forward; another Emulator Shell is also asked to bring `avdId`'s window
    /// forward. This app is in front (the user just clicked), so it may hand the foreground on.
    public void Activate(string? avdId)
    {
        AllowSetForegroundWindow(Pid);
        if (IsEmulatorShell && avdId != null) ShowRequests.Send(Pid, avdId);
        if (Native.IsIconic(MainWindow)) Native.ShowWindow(MainWindow, Native.SW_RESTORE);
        Native.SetForegroundWindow(MainWindow);
    }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint Size, Usage, ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(nint snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(nint snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    /// Every process's parent, by process id.
    private static Dictionary<int, int> Parents()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x2, 0);
        if (snapshot == -1) return parents;
        try
        {
            var entry = new PROCESSENTRY32 { Size = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return parents;
    }
}

/// One Emulator Shell asking another to bring a device's window forward, over a named pipe each copy listens on
/// (`EmulatorShell.<pid>`): the line it gets is the AVD's id.
static class ShowRequests
{
    private static string PipeName(int pid) => $"EmulatorShell.{pid}";

    /// Listens for requests to this app until it ends; `show` runs on the calling thread's dispatcher.
    public static void Listen(Action<string> show)
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName(Environment.ProcessId), PipeDirection.In, 1,
                                                               PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    if (await reader.ReadLineAsync() is { Length: > 0 } avdId)
                    {
                        Log.Debug($"asked to show {avdId}");
                        queue.TryEnqueue(() => show(avdId));
                    }
                }
                catch (Exception error)
                {
                    Log.Debug($"show requests: {error.Message}");
                    await Task.Delay(1000);
                }
            }
        });
    }

    public static void Send(int pid, string avdId)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(pid), PipeDirection.Out);
            pipe.Connect(1000);
            using var writer = new StreamWriter(pipe, Encoding.UTF8);
            writer.WriteLine(avdId);
        }
        catch (Exception error)
        {
            Log.Debug($"cannot ask Emulator Shell {pid} to show {avdId}: {error.Message}");
        }
    }
}
