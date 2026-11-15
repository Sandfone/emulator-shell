#if DEBUG
namespace EmulatorShell;

/// Debug builds only: `EMULATOR_SHELL_MANAGER_SCRIPT` drives the manager window, for checking its dialogs without
/// the mouse. Commands are separated by `;`: `wait <ms>`, `select <avd id>`, `new`, `edit`, `images`, `hide` (closes
/// the dialog showing), `shot <name>` (a marker for a screen capture), `forwarder install|uninstall|state`, `create`
/// (a new AVD from the default form), `delete <avd id>`, `owner <avd id>`, `stop <avd id>`, `quit`; for physical devices
/// `phones` (logs them), `mirror <device id>`, `phoneshot <device id>`, `record <device id>` (starts or stops) and
/// `pairing` (opens the Wi-Fi pairing dialog); `theme dark|light|default` sets the window's theme, as the system
/// switching it does; `fail` throws on the UI thread, as an unexpected error would.
sealed partial class ManagerWindow
{
    /// The window's content as a PNG in the temp folder (`<name>.png`), drawn by XAML itself, so that it does not
    /// matter what covers the window.
    private async Task SaveSnapshot(string name)
    {
        if (name.Length == 0 || Content is not Microsoft.UI.Xaml.UIElement content) return;
        try
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(content);
            var pixels = await bitmap.GetPixelsAsync();
            var frame = new VideoFrame(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels),
                                       bitmap.PixelWidth, bitmap.PixelHeight, 0);
            await File.WriteAllBytesAsync(Path.Combine(Path.GetTempPath(), $"{name}.png"), await Pictures.EncodePng(frame));
        }
        catch (Exception error)
        {
            Log.Error($"snapshot {name}: {error.Message}");
        }
    }

    /// An error on the UI thread after an await, the way the app's async handlers raise them.
    private static async void Fail()
    {
        await Task.Yield();
        throw new ArgumentException("test failure");
    }

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
                    case "shot":
                        await Task.Delay(1200);
                        await SaveSnapshot(argument);
                        break;
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
                    case "phones":
                        foreach (var phone in manager.Phones.Devices)
                            Log.Info($"phone {phone.Id}: {phone.Name}, {phone.State}, serial {phone.Serial}, {phone.Info?.Width}×{phone.Info?.Height}");
                        break;
                    case "mirror":
                        if (manager.Phones.Device(argument) is { } mirrored) manager.Mirror(mirrored);
                        break;
                    case "phoneshot": manager.Phones.Screenshot(argument); break;
                    case "record": manager.Phones.ToggleRecording(argument); break;
                    case "pairing": _ = ShowPairing(); break;
                    case "fail": Fail(); break;
                    case "theme":
                        if (Content is Microsoft.UI.Xaml.FrameworkElement root)
                            root.RequestedTheme = argument switch
                            {
                                "dark" => Microsoft.UI.Xaml.ElementTheme.Dark,
                                "light" => Microsoft.UI.Xaml.ElementTheme.Light,
                                _ => Microsoft.UI.Xaml.ElementTheme.Default,
                            };
                        break;
                    case "quit": QuitRequested?.Invoke(); break;
                }
            }
        }
    }
}
#endif
