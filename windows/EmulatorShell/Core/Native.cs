using System.Runtime.InteropServices;

namespace EmulatorShell;

/// The Win32 calls the device windows need beyond what WinUI offers.
static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    public const int WM_MOVING = 0x0216;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_ACTIVATEAPP = 0x001C;
    public const int HTCAPTION = 2;
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200, SWP_NOREDRAW = 0x0008;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;
    public const int SW_SHOWNORMAL = 1, SW_HIDE = 0, SW_MINIMIZE = 6, SW_RESTORE = 9;
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_BORDER = 0x00800000, WS_DLGFRAME = 0x00400000, WS_THICKFRAME = 0x00040000;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
    public const int RGN_OR = 2;
    public const int MONITOR_DEFAULTTONEAREST = 2;
    public const int HORZSIZE = 4, VERTSIZE = 6;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_DONOTROUND = 1;
    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3, DWMWA_NCRENDERING_POLICY = 2, DWMNCRP_DISABLED = 1;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int key);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);
    public const int SM_SWAPBUTTON = 23;

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    public static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromRect(in RECT rect, int flags);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(POINT point, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    public delegate bool MonitorEnumProc(nint monitor, nint dc, ref RECT rect, nint data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern nint CreateDC(string driver, string device, string? output, nint data);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDeviceCaps(nint dc, int index);

    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    public static partial int CombineRgn(nint destination, nint a, nint b, int mode);

    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmExtendFrameIntoClientArea(nint hwnd, in MARGINS margins);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);

    public delegate nint SubclassProc(nint hwnd, int message, nint wParam, nint lParam, nint id, nint data);

    [DllImport("comctl32.dll")]
    public static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nint id, nint data);

    [DllImport("comctl32.dll")]
    public static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nint id);

    [DllImport("comctl32.dll")]
    public static extern nint DefSubclassProc(nint hwnd, int message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetConsoleWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    /// Windows.UI.Composition, which a window's system backdrop is made with, needs a Windows.System
    /// DispatcherQueue on the thread; WinUI only makes its own kind.
    public static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
        var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 0 };
        CreateDispatcherQueueController(options, out _);
    }

    /// Whether the window in front belongs to this app.
    public static bool IsAppActive
    {
        get
        {
            var foreground = GetForegroundWindow();
            if (foreground == 0) return false;
            GetWindowThreadProcessId(foreground, out var process);
            return process == Environment.ProcessId;
        }
    }

    public static bool IsLeftButtonDown => (GetAsyncKeyState(0x01) & 0x8000) != 0;
    /// The button that clicks: GetAsyncKeyState reads the physical buttons, which a left-handed setup swaps.
    public static bool IsPrimaryButtonDown => (GetAsyncKeyState(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? 0x02 : 0x01) & 0x8000) != 0;

    public static Windows.Graphics.PointInt32 CursorPosition =>
        GetCursorPos(out var point) ? new Windows.Graphics.PointInt32(point.X, point.Y) : default;
}

/// The device windows' AppKit counterparts work in points with the origin at the bottom left of the main screen;
/// these windows work in physical pixels with the origin at the top left of the primary monitor, and convert to
/// DIPs (effective pixels) with the scale of the monitor they are on.
readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;

    public PixelRect Inflate(double dx, double dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);
    public PixelRect Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };
    public PixelRect Rounded() => new(Math.Round(X), Math.Round(Y), Math.Round(Width), Math.Round(Height));
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    public PixelRect Union(PixelRect other)
    {
        double left = Math.Min(Left, other.Left), top = Math.Min(Top, other.Top);
        return new PixelRect(left, top, Math.Max(Right, other.Right) - left, Math.Max(Bottom, other.Bottom) - top);
    }

    public static PixelRect From(Native.RECT rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    public Native.RECT ToRect() =>
        new() { Left = (int)Math.Round(Left), Top = (int)Math.Round(Top), Right = (int)Math.Round(Right), Bottom = (int)Math.Round(Bottom) };
}

/// A monitor: its whole area and the part not taken by the taskbar, in pixels, and its scale.
sealed record Screen(nint Handle, PixelRect Frame, PixelRect WorkArea, double Scale, string DeviceName)
{
    public static Screen ForRect(PixelRect rect) => FromHandle(Native.MonitorFromRect(rect.ToRect(), Native.MONITOR_DEFAULTTONEAREST));

    public static Screen ForPoint(int x, int y) =>
        FromHandle(Native.MonitorFromPoint(new Native.POINT { X = x, Y = y }, Native.MONITOR_DEFAULTTONEAREST));

    public static Screen Primary => ForPoint(0, 0);

    public static List<Screen> All
    {
        get
        {
            var screens = new List<Screen>();
            Native.EnumDisplayMonitors(0, 0, (monitor, _, ref _, _) =>
            {
                screens.Add(FromHandle(monitor));
                return true;
            }, 0);
            return screens;
        }
    }

    public static Screen FromHandle(nint monitor)
    {
        var info = new Native.MONITORINFOEX { Size = Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(monitor, ref info);
        Native.GetDpiForMonitor(monitor, 0, out var dpi, out _);
        return new Screen(monitor, PixelRect.From(info.Monitor), PixelRect.From(info.Work), dpi == 0 ? 1 : dpi / 96.0, info.Device);
    }

    /// The monitor's size in millimetres as its EDID reports it; null when unknown.
    public (double Width, double Height)? PhysicalSize
    {
        get
        {
            var dc = Native.CreateDC("DISPLAY", DeviceName, null, 0);
            if (dc == 0) return null;
            try
            {
                int width = Native.GetDeviceCaps(dc, Native.HORZSIZE), height = Native.GetDeviceCaps(dc, Native.VERTSIZE);
                return width > 0 && height > 0 ? (width, height) : null;
            }
            finally
            {
                Native.DeleteDC(dc);
            }
        }
    }
}
