using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EmulatorShell;

// Usage: EmulatorShell [avd-name [emulator arguments…]]
// With an AVD name the device opens directly, the remaining arguments going to the emulator (this is how the
// SDK forwarder hands over launches from Android Studio and Flutter); without one the device manager opens.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Started from a terminal (`emulator-shell`), log lines go there as they do on macOS.
        if (!Console.IsErrorRedirected) Native.AttachConsole(-1);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ignored =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(args.FirstOrDefault(), args.Skip(1).ToList());
        });
    }
}
