using System.Runtime.InteropServices;

namespace RedditWallpaper;

/// <summary>
/// 轻量系统托盘图标（Shell_NotifyIcon + 隐藏消息窗口），
/// 不依赖第三方包，Win10/11 通用。必须从 UI 线程创建。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYICON = 0x8000 + 1;   // WM_APP + 1
    private const int NIM_ADD = 0;
    private const int NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int MF_STRING = 0x0000;
    private const int MF_SEPARATOR = 0x0800;
    private const int TPM_RETURNCMD = 0x0100;
    private const int TPM_NONOTIFY = 0x0080;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;
    private const uint LR_SHARED = 0x8000;
    private const int IDI_APPLICATION = 32512;
    private const int WM_NULL = 0x0000;

    private const int MenuShow = 1001;
    private const int MenuRotate = 1002;
    private const int MenuExit = 1003;

    public event Action? ShowRequested;
    public event Action? RotateRequested;
    public event Action? ExitRequested;

    private readonly WndProcDelegate _wndProc;   // 必须保活，否则回调会被 GC
    private readonly string _className = "RedditWallpaperTray_" + Guid.NewGuid().ToString("N");
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly IntPtr _menu;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint flags, IntPtr id, string text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    public TrayIcon(string tooltip, string? iconFile)
    {
        _wndProc = WndProc;
        var hInstance = GetModuleHandle(null);

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className
        };
        if (RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException("RegisterClassEx 失败: " + Marshal.GetLastWin32Error());

        // HWND_MESSAGE (-3)：只收消息、不显示
        _hwnd = CreateWindowEx(0, _className, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx 失败: " + Marshal.GetLastWin32Error());

        _icon = LoadTrayIcon(iconFile);

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
            szTip = tooltip.Length > 120 ? tooltip[..120] : tooltip
        };
        Shell_NotifyIcon(NIM_ADD, ref data);

        _menu = CreatePopupMenu();
        AppendMenu(_menu, MF_STRING, new IntPtr(MenuShow), "显示主窗口");
        AppendMenu(_menu, MF_STRING, new IntPtr(MenuRotate), "立即更换壁纸");
        AppendMenu(_menu, MF_SEPARATOR, IntPtr.Zero, "");
        AppendMenu(_menu, MF_STRING, new IntPtr(MenuExit), "退出");
    }

    private static IntPtr LoadTrayIcon(string? iconFile)
    {
        if (iconFile is not null && File.Exists(iconFile))
        {
            var h = LoadImage(IntPtr.Zero, iconFile, 1 /*IMAGE_ICON*/, 0, 0,
                LR_LOADFROMFILE | LR_DEFAULTSIZE | LR_SHARED);
            if (h != IntPtr.Zero) return h;
        }
        return LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            var mouseMsg = lParam.ToInt64() & 0xFFFF;
            switch (mouseMsg)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    ShowRequested?.Invoke();
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    GetCursorPos(out var pt);
                    SetForegroundWindow(_hwnd);
                    var cmd = TrackPopupMenu(_menu, TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
                    PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                    switch (cmd)
                    {
                        case MenuShow: ShowRequested?.Invoke(); break;
                        case MenuRotate: RotateRequested?.Invoke(); break;
                        case MenuExit: ExitRequested?.Invoke(); break;
                    }
                    return IntPtr.Zero;
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1
        };
        Shell_NotifyIcon(NIM_DELETE, ref data);

        if (_menu != IntPtr.Zero) DestroyMenu(_menu);
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        UnregisterClass(_className, GetModuleHandle(null));
    }
}
