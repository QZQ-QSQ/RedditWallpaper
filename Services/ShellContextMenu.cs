using System.Runtime.InteropServices;

namespace RedditWallpaper.Services;

/// <summary>
/// 在图片上点右键时弹出真正的 Windows 资源管理器右键菜单（打开方式、发送到、复制、删除、属性…）。
/// 菜单由 shell 自己绘制，因此跟随系统深色/浅色主题，并自动带系统语言。
/// </summary>
internal static class ShellContextMenu
{
    private const uint CMF_NORMAL = 0x00000000;
    private const uint CMF_EXPLORE = 0x00000004;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const int SW_SHOWNORMAL = 1;
    private const int IdCmdFirst = 1;

    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_DRAWITEM = 0x002B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_MENUCHAR = 0x0120;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public POINT ptInvoke;
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

        [PreserveSig]
        int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);

        void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);

        void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [Guid("000214F5-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);

        void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y,
        IntPtr hwnd, IntPtr lptpm);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass,
        UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass,
        UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData);

    [ThreadStatic] private static IContextMenu2? _menu2;
    [ThreadStatic] private static IContextMenu3? _menu3;
    private static readonly SubclassProc MenuSubclassProcDelegate = MenuSubclassProc;

    /// <summary>在 ownerHwnd 的指定屏幕坐标（物理像素）弹出 path 的 Windows 右键菜单；不传坐标则用鼠标位置。</summary>
    public static void Show(IntPtr ownerHwnd, string path, int? screenX = null, int? screenY = null)
    {
        if (ownerHwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(path)) return;

        IntPtr pidl = IntPtr.Zero;
        IntPtr hMenu = IntPtr.Zero;
        IShellFolder? folder = null;
        IContextMenu? contextMenu = null;
        var hooked = false;

        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return;

            var iidFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidl, ref iidFolder, out folder, out var childPidl) != 0 ||
                folder is null || childPidl == IntPtr.Zero)
                return;

            var iidMenu = typeof(IContextMenu).GUID;
            if (folder.GetUIObjectOf(ownerHwnd, 1, new[] { childPidl }, ref iidMenu,
                    IntPtr.Zero, out var menuPtr) != 0 || menuPtr == IntPtr.Zero)
                return;

            try
            {
                contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            }
            finally
            {
                Marshal.Release(menuPtr);
            }

            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;

            var hr = contextMenu.QueryContextMenu(hMenu, 0, IdCmdFirst, 0x7FFF, CMF_NORMAL | CMF_EXPLORE);
            if (hr < 0) return;

            _menu3 = contextMenu as IContextMenu3;
            _menu2 = (IContextMenu2?)contextMenu;
            hooked = SetWindowSubclass(ownerHwnd, MenuSubclassProcDelegate, (UIntPtr)1, IntPtr.Zero);

            GetCursorPos(out var pt);
            if (screenX is int px && screenY is int py)
            {
                pt.X = px;
                pt.Y = py;
            }
            var cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                pt.X, pt.Y, ownerHwnd, IntPtr.Zero);

            if (cmd >= IdCmdFirst)
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = CMIC_MASK_UNICODE,
                    hwnd = ownerHwnd,
                    // 低 16 位是菜单命令 ID；MAKEINTRESOURCE 语义
                    lpVerb = (IntPtr)(cmd - IdCmdFirst),
                    lpVerbW = (IntPtr)(cmd - IdCmdFirst),
                    nShow = SW_SHOWNORMAL,
                    ptInvoke = pt
                };
                contextMenu.InvokeCommand(ref info);
            }
        }
        catch
        {
            // 右键菜单是增强功能：任何 shell 扩展异常都不应影响主流程
        }
        finally
        {
            if (hooked) RemoveWindowSubclass(ownerHwnd, MenuSubclassProcDelegate, (UIntPtr)1);
            _menu2 = null;
            _menu3 = null;
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (contextMenu is not null && Marshal.IsComObject(contextMenu)) Marshal.ReleaseComObject(contextMenu);
            folder = null;
            if (pidl != IntPtr.Zero) ILFree(pidl);
        }
    }

    private static IntPtr MenuSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        try
        {
            if (uMsg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM)
            {
                if (_menu2 is not null)
                {
                    _menu2.HandleMenuMsg(uMsg, wParam, lParam);
                    return IntPtr.Zero;
                }
            }
            else if (uMsg == WM_MENUCHAR && _menu3 is not null)
            {
                _menu3.HandleMenuMsg2(uMsg, wParam, lParam, out var result);
                return result;
            }
        }
        catch
        {
            // fall through to default proc
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
