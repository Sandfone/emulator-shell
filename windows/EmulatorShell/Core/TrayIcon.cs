using System.Runtime.InteropServices;

namespace EmulatorShell;

/// One line of the notification-area icon's menu; a null `Text` is a separator.
sealed record TrayMenuItem(string? Text, Action? Click = null, bool Enabled = true, bool Checked = false, bool IsDefault = false)
{
    public static readonly TrayMenuItem Separator = new((string?)null);
}

/// The app's icon in the taskbar's notification area: clicking it runs `Clicked`, right-clicking it shows the
/// menu `Menu` builds at that moment. It belongs to a hidden window of its own on the UI thread, whose messages
/// the app's message loop delivers.
sealed partial class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 1;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_NULL = 0x0000;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    private const int NOTIFYICON_VERSION_4 = 4;
    private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RIGHTALIGN = 0x8, TPM_BOTTOMALIGN = 0x20, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    private const int SM_CXSMICON = 49, SM_MENUDROPALIGNMENT = 40;

    public event Action? Clicked;
    public Func<IReadOnlyList<TrayMenuItem>>? Menu { get; set; }

    private readonly WndProc proc;
    private readonly nint hwnd;
    private readonly nint icon;
    private readonly string tooltip;
    private readonly uint taskbarCreated;
    private bool disposed;

    public TrayIcon(string tooltip, string iconPath)
    {
        this.tooltip = tooltip;
        proc = WindowProc;
        var instance = GetModuleHandleW(0);
        var className = $"EmulatorShell.Tray.{Environment.ProcessId}";
        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = instance,
            lpszClassName = className,
        };
        RegisterClassExW(ref windowClass);
        // A hidden top-level window rather than a message-only one: those miss the TaskbarCreated broadcast.
        hwnd = CreateWindowExW(0, className, tooltip, 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, instance, 0);
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        var size = GetSystemMetrics(SM_CXSMICON);
        icon = LoadImageW(0, iconPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        AllowDarkMenus();
        Add();
    }

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            // Explorer still holds one from a copy that ended without removing it.
            Shell_NotifyIconW(NIM_DELETE, ref data);
            if (!Shell_NotifyIconW(NIM_ADD, ref data)) Log.Error("cannot add the notification-area icon");
        }
        data.uVersionOrTimeout = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    private NOTIFYICONDATAW Data(int flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = hwnd,
        uID = 1,
        uFlags = (uint)flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            switch ((int)(lParam & 0xFFFF))
            {
                case NIN_SELECT or NIN_KEYSELECT:
                    Clicked?.Invoke();
                    break;
                case WM_CONTEXTMENU:
                    ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    break;
            }
            return 0;
        }
        if (message == taskbarCreated && taskbarCreated != 0)
        {
            Add();
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        if (Menu?.Invoke() is not { } items) return;
        var menu = CreatePopupMenu();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Text == null)
            {
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
                continue;
            }
            AppendMenuW(menu, MF_STRING | (item.Enabled ? 0 : MF_GRAYED) | (item.Checked ? MF_CHECKED : 0), (nuint)(i + 1), item.Text);
            if (item.IsDefault) SetMenuDefaultItem(menu, (uint)(i + 1), 0);
        }
        // Without this the menu stays open when clicked away from.
        SetForegroundWindow(hwnd);
        var align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : 0;
        var chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | align, x, y, hwnd, 0);
        PostMessageW(hwnd, WM_NULL, 0, 0);
        DestroyMenu(menu);
        if (chosen > 0 && chosen <= items.Count) items[chosen - 1].Click?.Invoke();
    }

    /// Menus follow the system's dark mode, as the app's own XAML does; uxtheme's SetPreferredAppMode (ordinal 135)
    /// is the switch Explorer and other apps use, undocumented but stable since Windows 10 1903.
    private static unsafe void AllowDarkMenus()
    {
        try
        {
            var uxtheme = LoadLibraryW("uxtheme.dll");
            if (uxtheme == 0) return;
            var setMode = GetProcAddress(uxtheme, 135);
            var flush = GetProcAddress(uxtheme, 136);
            if (setMode != 0) ((delegate* unmanaged[Stdcall]<int, int>)setMode)(1 /* AllowDark */);
            if (flush != 0) ((delegate* unmanaged[Stdcall]<void>)flush)();
        }
        catch (Exception error)
        {
            Log.Debug($"dark menus: {error.Message}");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        if (icon != 0) DestroyIcon(icon);
        if (hwnd != 0) DestroyWindow(hwnd);
    }

    // Win32

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(int exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(nint name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadLibraryW(string name);
    [DllImport("kernel32.dll")] private static extern nint GetProcAddress(nint module, nint ordinal);
}
