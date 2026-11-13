#if DEBUG
namespace EmulatorShell;

/// Debug builds only: `EMULATOR_SHELL_MANAGER_SCRIPT` drives the manager window, for checking its dialogs without
/// the mouse. Commands are separated by `;`: `wait <ms>`, `select <avd id>`, `new`, `edit`, `images`, `hide` (closes
/// the dialog showing), `shot <name>` (a marker for a screen capture), `forwarder install|uninstall|state`, `create`
/// (a new AVD from the default form), `delete <avd id>`, `owner <avd id>`, `stop <avd id>`, `quit`.
sealed partial class ManagerWindow
{
    partial void RunTestScript()
    {
        if (Environment.GetEnvironmentVariable("EMULATOR_SHELL_MANAGER_SCRIPT") is not { Length: > 0 } script) return;
        _ = Execute(script);

        async Task Execute(string text)
        {
            foreach (var raw in text.Split(';'))
            {
                var parts = raw.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                var argument = parts.Length > 1 ? parts[1] : "";
                Log.Info($"test script: {raw.Trim()}");
                switch (parts[0])
                {
                    case "wait": await Task.Delay(int.Parse(argument)); break;
                    case "select":
                        manager.Selection = argument;
                        Render();
                        break;
                    case "new": _ = ShowForm(manager.NewForm()); break;
                    case "edit":
                        if (manager.SelectedAvd is { } avd) _ = ShowForm(manager.EditForm(avd));
                        break;
                    case "images": _ = ShowImages(null); break;
                    case "hide": currentDialog?.Hide(); break;
                    case "shot": await Task.Delay(1200); break;
                    case "forwarder":
                        try
                        {
                            if (argument == "install") EmulatorForwarder.Install();
                            if (argument == "uninstall") EmulatorForwarder.Uninstall();
                        }
                        catch (Exception error)
                        {
                            Log.Error($"forwarder {argument}: {error.Message}");
                        }
                        Log.Info($"forwarder state: {EmulatorForwarder.State}, app {EmulatorForwarder.InstalledAppPath}");
                        break;
                    case "create":
                        var form = manager.NewForm();
                        Log.Info($"creating {form.Name} ({form.DeviceTypeId}, {form.ImageId}): {await manager.Submit(form) ?? "done"}");
                        break;
                    case "delete":
                        if (manager.Avds.FirstOrDefault(a => a.Id == argument) is { } doomed) manager.Delete(doomed);
                        break;
                    case "owner":
                        if (manager.Avds.FirstOrDefault(a => a.Id == argument) is { } shown)
                            Log.Info($"owner of {argument}: {manager.Owner(shown)?.DisplayName ?? "none"}, other Emulator Shell: {manager.IsInOtherEmulatorShell(shown)}");
                        break;
                    case "stop":
                        if (manager.Avds.FirstOrDefault(a => a.Id == argument) is { } stopped) manager.Stop(stopped);
                        break;
                    case "quit": QuitRequested?.Invoke(); break;
                }
            }
        }
    }
}
#endif
