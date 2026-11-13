using System.Runtime.InteropServices;

namespace EmulatorShell;

/// Message boxes for errors that have no window of their own to appear in, like NSAlert's modal alerts.
static partial class Dialogs
{
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);

    private const uint MB_OK = 0, MB_OKCANCEL = 1, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000;

    public static void Alert(nint owner, string title, string detail) =>
        MessageBox(owner, $"{title}\n\n{detail}", "Emulator Shell", MB_OK | MB_ICONWARNING | MB_SETFOREGROUND);

    public static void Inform(nint owner, string title, string detail) =>
        MessageBox(owner, $"{title}\n\n{detail}", "Emulator Shell", MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);
}
