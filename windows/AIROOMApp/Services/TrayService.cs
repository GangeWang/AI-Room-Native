using System.Runtime.InteropServices;

namespace AIROOMApp.Services;

/// <summary>
/// The system tray icon, built directly on Shell_NotifyIcon (H.NotifyIcon's
/// WinUI path crashed inside System.Drawing). Left click brings the window to
/// the front; right click opens a menu with 全部中止 and 結束. The icon is the
/// executable's own.
/// </summary>
public sealed class TrayService : IDisposable
{
    const uint WM_APP_TRAY = 0x8000 + 0x400; // WM_APP + 0x400, unlikely to clash
    const uint WM_LBUTTONUP = 0x0202;
    const uint WM_RBUTTONUP = 0x0205;
    const uint WM_COMMAND = 0x0111;

    const int MenuStopAll = 2000;
    const int MenuExit = 2001;

    readonly Guid windowClass = Guid.NewGuid();
    IntPtr window;
    IntPtr icon;
    NOTIFYICONDATAW data;
    bool visible;
    WndProcDelegate proc;

    public event Action? StopAllRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        window = CreateWindow();
        icon = ExtractIcon(GetModuleHandleW(null), Environment.ProcessPath, 0);
        Show();
    }

    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    IntPtr CreateWindow()
    {
        proc = WndProc;
        var wndClass = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            lpszClassName = windowClass.ToString("N"),
            hInstance = GetModuleHandleW(null),
        };
        _ = RegisterClassW(ref wndClass);
        // A message-only window (HWND_MESSAGE): it renders nothing and only
        // receives the tray callback messages.
        return CreateWindowExW(0, wndClass.lpszClassName, "AIROOM tray", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
    }

    void Show()
    {
        if (window == IntPtr.Zero || icon == IntPtr.Zero) return;
        data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = window,
            uID = 1,
            uFlags = 0x01 | 0x02 | 0x04, // NIF_MESSAGE | NIF_ICON | NIF_TIP
            uCallbackMessage = WM_APP_TRAY,
            hIcon = icon,
            szTip = "AI Room Native",
        };
        Shell_NotifyIconW(0 /* NIM_ADD */, ref data);
        visible = true;
    }

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            var message = (uint)(lParam.ToInt64() & 0xFFFF);
            if (message == WM_LBUTTONUP)
            {
                App.Window?.BringToFront();
            }
            else if (message == WM_RBUTTONUP)
            {
                ShowMenu();
            }
            return IntPtr.Zero;
        }
        if (msg == WM_COMMAND)
        {
            var command = wParam.ToInt32() & 0xFFFF;
            if (command == MenuStopAll) StopAllRequested?.Invoke();
            if (command == MenuExit) ExitRequested?.Invoke();
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    void ShowMenu()
    {
        var menu = CreatePopupMenu();
        _ = AppendMenuW(menu, 0 /* MF_STRING */, MenuStopAll, "全部中止");
        _ = AppendMenuW(menu, 0, MenuExit, "結束 AIROOM");
        GetCursorPos(out var point);
        _ = SetForegroundWindow(window); // so the menu dismisses on outside click
        TrackPopupMenu(menu, 0, point.X, point.Y, 0, window, IntPtr.Zero);
        _ = DestroyMenu(menu);
    }

    public void Dispose()
    {
        if (visible) Shell_NotifyIconW(2 /* NIM_DELETE */, ref data);
        if (window != IntPtr.Zero) DestroyWindow(window);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        visible = false;
        window = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassW(ref WNDCLASSW wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint style, string className, string title,
        uint styleEx, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr ExtractIcon(IntPtr instance, string path, uint index);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    static extern bool AppendMenuW(IntPtr menu, uint flags, int id, string text);

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }
}
