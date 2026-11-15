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
    /// Shown while this copy is the primary one (see `PrimaryInstance`).
    private TrayIcon? tray;
    /// The AVDs the jump list was last built with, to rebuild it only when they change.
    private string? jumpListAvds;

    public App(string? launchAvd, IReadOnlyList<string> emulatorArguments)
    {
        this.launchAvd = launchAvd;
        this.emulatorArguments = emulatorArguments;
        InitializeComponent();
        // The app ends when it decides to: device windows are hidden while their emulators save snapshots.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        // An error in one handler on the UI thread is logged (with XAML's message, which often carries the original
        // stack where the exception has lost it) and the app carries on: it runs for days in the notification area,
        // and ending it would take every device window and recording with it.
        UnhandledException += (_, e) =>
        {
            Log.Error($"unhandled: {e.Message}\n{e.Exception}");
            e.Handled = true;
        };
        // Off the UI thread too: the streams of physical devices run on threads of their own.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"unhandled on a background thread: {e.ExceptionObject}");
        // An icon Explorer is not told to remove stays in the notification area until the pointer passes over it.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => tray?.Dispose();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Without a device to open, a copy already running shows its manager rather than a second one opening.
        if (launchAvd == null && PrimaryInstance.RequestManager())
        {
            Exit();
            return;
        }
        manager = new DeviceManager();
        manager.ManagerRequested += ShowManager;
        manager.QuitRequested += Quit;
        manager.SessionsChanged += ExitIfDone;
        manager.Changed += UpdateJumpList;
        manager.ListenForShowRequests();
        // The manager window reports errors itself; without it they would go unseen. A launch for a single device
        // that never got a window ends with its error.
        manager.Error += message =>
        {
            if (managerWindow != null) return;
            if (launchAvd != null && !manager.HasSessions) Fail(message);
            else Dialogs.Alert(0, "Emulator Shell", message);
        };
        if (launchAvd == null)
        {
            PrimaryInstance.Claim(BecomePrimary, ShowManager);
            ShowManager();
            managerWindow?.CheckForwarder();
            return;
        }
        if (manager.Avds.FirstOrDefault(a => a.Id == launchAvd) is not { } avd)
        {
            Fail($"没有名为 {launchAvd} 的 AVD。");
            return;
        }
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
        PrimaryInstance.Claim(BecomePrimary, ShowManager);
    }

    // Notification area and jump list

    private void BecomePrimary()
    {
        if (quitting || tray != null) return;
        tray = new TrayIcon("Emulator Shell", Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        tray.Clicked += ShowManager;
        tray.Menu = TrayMenu;
        jumpListAvds = null;
        UpdateJumpList();
    }

    /// The manager, then every AVD with what choosing it does, then quitting.
    private List<TrayMenuItem> TrayMenu()
    {
        var items = new List<TrayMenuItem> { new("设备管理器", ShowManager, IsDefault: true) };
        if (manager == null) return items;
        if (manager.Avds.Count > 0) items.Add(TrayMenuItem.Separator);
        foreach (var avd in manager.Avds)
        {
            var current = avd;
            items.Add(manager.Status(avd).Kind switch
            {
                AvdStatusKind.Running => new TrayMenuItem($"{avd.Name}\t运行中", () => manager.ShowWindow(current), Checked: true),
                AvdStatusKind.Booting => new TrayMenuItem($"{avd.Name}\t正在启动", () => manager.ShowWindow(current), Checked: true),
                AvdStatusKind.Stopping => new TrayMenuItem($"{avd.Name}\t正在关机", Enabled: false),
                // Another program runs it: shown here, or brought forward where another Emulator Shell shows it.
                AvdStatusKind.External => new TrayMenuItem($"{avd.Name}\t{(manager.IsInOtherEmulatorShell(avd) ? "运行中" : "在其他程序中运行")}",
                                                           () => manager.Attach(current), Checked: true),
                _ => new TrayMenuItem($"{avd.Name}\t启动", () => manager.Start(current)),
            });
        }
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem("退出 Emulator Shell", Quit));
        return items;
    }

    private void UpdateJumpList()
    {
        if (tray == null || manager == null) return;
        var avds = manager.Avds.Select(a => (a.Id, a.Name)).ToList();
        var key = string.Join("\n", avds.Select(a => $"{a.Id}\t{a.Name}"));
        if (key == jumpListAvds) return;
        jumpListAvds = key;
        JumpList.Update(avds);
    }

    private void ShowManager() => ShowManager(ManagerTask.Show);

    private void ShowManager(ManagerTask task)
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
        managerWindow.Run(task);
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
        tray?.Dispose();
        tray = null;
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
