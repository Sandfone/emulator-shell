using Microsoft.UI.Xaml;

namespace EmulatorShell;

public partial class App : Application
{
    /// Set when launched as `EmulatorShell <avd>`: that device opens directly, without the manager window.
    private readonly string? launchAvd;
    private readonly IReadOnlyList<string> emulatorArguments;
    private DeviceManager? manager;
    private ManagerWindow? managerWindow;
    private bool quitting;

    public App(string? launchAvd, IReadOnlyList<string> emulatorArguments)
    {
        this.launchAvd = launchAvd;
        this.emulatorArguments = emulatorArguments;
        InitializeComponent();
        // The app ends when it decides to: device windows are hidden while their emulators save snapshots.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => Log.Error($"unhandled: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        manager = new DeviceManager();
        manager.ManagerRequested += ShowManager;
        manager.QuitRequested += Quit;
        manager.SessionsChanged += ExitIfDone;
        manager.ListenForShowRequests();
        if (launchAvd == null)
        {
            ShowManager();
            managerWindow?.CheckForwarder();
            return;
        }
        if (manager.Avds.FirstOrDefault(a => a.Id == launchAvd) is not { } avd)
        {
            Fail($"没有名为 {launchAvd} 的 AVD。");
            return;
        }
        // Opened for a single device, the app ends with it.
        manager.Error += message =>
        {
            if (!manager.HasSessions) Fail(message);
        };
        // A device another program already runs is shown instead of started a second time; one another Emulator
        // Shell shows is left to it, its window brought forward.
        if (manager.Status(avd).Kind == AvdStatusKind.External && manager.IsInOtherEmulatorShell(avd))
        {
            manager.ShowInOwner(avd);
            Exit();
        }
        else if (manager.Status(avd).Kind == AvdStatusKind.External)
        {
            manager.Attach(avd);
        }
        else
        {
            manager.Start(avd, extraArguments: EmulatorForwarder.Forwardable(emulatorArguments));
        }
    }

    private void ShowManager()
    {
        if (manager == null || quitting) return;
        if (managerWindow == null)
        {
            managerWindow = new ManagerWindow(manager);
            managerWindow.Closed += (_, _) =>
            {
                managerWindow = null;
                ExitIfDone();
            };
            managerWindow.QuitRequested += Quit;
        }
        managerWindow.Activate();
    }

    /// Without the manager and devices there is nothing to show; the app ends, as a single-device launch does
    /// on macOS when its window closes.
    private void ExitIfDone()
    {
        if (manager == null || quitting || managerWindow != null || manager.HasSessions) return;
        Exit();
    }

    /// Quitting waits for the emulators to save their snapshots, which takes seconds; the windows go at once and
    /// the app stays responsive meanwhile.
    private async void Quit()
    {
        if (quitting || manager == null) return;
        quitting = true;
        managerWindow?.AppWindow.Hide();
        await manager.StopAllSessions();
        Exit();
    }

    private void Fail(string message)
    {
        Log.Error(message);
        Dialogs.Alert(0, "Emulator Shell", message);
        Exit();
    }
}
