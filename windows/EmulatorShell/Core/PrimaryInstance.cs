using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;

namespace EmulatorShell;

/// Of the copies of the app running at once (one per device the SDK forwarder starts, plus the manager), one is
/// the primary: it shows the notification-area icon and keeps the taskbar's jump list, and a launch without a
/// device (the Start menu, the jump list, `emulator-shell`) asks it for its manager instead of opening another.
///
/// A named mutex decides which: the first copy to hold it. The others wait on it, so when the primary ends, its
/// mutex is abandoned and the next copy takes over. Copies installed in different folders (a debug build and
/// the installed one) each have their own.
static partial class PrimaryInstance
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    private static readonly string Key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToLowerInvariant())))[..16];
    private static string MutexName => $@"Local\EmulatorShell.Primary.{Key}";
    private static string PipeName => $"EmulatorShell.Primary.{Environment.UserName}.{Key}";

    /// Asks the primary copy to show its device manager; false if there is none.
    public static bool RequestManager()
    {
        // No copy running has made the mutex: no need to wait for a pipe that will not come.
        if (!Mutex.TryOpenExisting(MutexName, out var existing)) return false;
        existing.Dispose();
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(1000);
            // This launch is what the user just did, so the primary copy may bring its window to the front.
            AllowSetForegroundWindow(-1);
            using var writer = new StreamWriter(pipe, Encoding.UTF8);
            writer.WriteLine("manager");
            return true;
        }
        catch (Exception error)
        {
            Log.Debug($"no primary copy to show the manager: {error.Message}");
            return false;
        }
    }

    /// Runs `becamePrimary` on the calling thread's dispatcher once this copy is the primary one: at once if no
    /// other copy is, else when the one that is ends. From then on `managerRequested` runs there for each request.
    public static void Claim(Action becamePrimary, Action managerRequested)
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        // The mutex belongs to the thread that took it, which therefore lives as long as the app does.
        var thread = new Thread(() =>
        {
            var mutex = new Mutex(false, MutexName);
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // The previous primary ended without releasing it; it is this thread's now.
            }
            Log.Debug("this copy is now the primary one");
            queue.TryEnqueue(() => becamePrimary());
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    if (reader.ReadLine() == "manager") queue.TryEnqueue(() => managerRequested());
                }
                catch (Exception error)
                {
                    Log.Debug($"primary requests: {error.Message}");
                    Thread.Sleep(1000);
                }
            }
        })
        { IsBackground = true, Name = "Primary instance" };
        thread.Start();
    }
}
