using System.Runtime.InteropServices;

namespace RedditWallpaper.Services;

/// <summary>
/// 让进程内的原生 Win32 菜单（右键菜单、托盘菜单）跟随系统深色模式。
/// WinUI 3 自己的控件本来就会跟随主题，只有 shell 菜单需要显式启用。
/// </summary>
internal static class DarkMode
{
    // uxtheme 未公开导出：Win10 1903+ / Win11 可用
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int appMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

    private const uint SPI_GETHIGHCONTRAST = 0x0042;
    private const uint HCF_HIGHCONTRASTON = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HIGHCONTRAST
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
        ref HIGHCONTRAST pvParam, uint fWinIni);

    /// <summary>高对比度主题由系统绘制，不要用 DarkMode_Explorer 覆盖。</summary>
    public static bool IsHighContrast()
    {
        try
        {
            var hc = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>() };
            return SystemParametersInfo(SPI_GETHIGHCONTRAST, hc.cbSize, ref hc, 0) &&
                   (hc.dwFlags & HCF_HIGHCONTRASTON) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>AllowDark：系统是深色时菜单就是深色，系统浅色时保持浅色。</summary>
    public static void EnableForSystemTheme()
    {
        if (IsHighContrast()) return;
        try
        {
            SetPreferredAppMode(1); // AllowDark
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 10 1809：没有这个导出，菜单保持系统默认
        }
        catch
        {
            // 主题是外观增强，失败不影响功能
        }
    }

    /// <summary>系统主题切换后刷新已缓存的原生菜单主题。</summary>
    public static void RefreshMenuThemes()
    {
        if (IsHighContrast()) return;
        try
        {
            FlushMenuThemes();
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>给窗口（及其弹出的原生菜单）套用/取消深色窗口主题。</summary>
    public static void ApplyWindowTheme(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero || IsHighContrast()) return;
        try
        {
            SetWindowTheme(hwnd, dark ? "DarkMode_Explorer" : "Explorer", null);
        }
        catch
        {
            // 忽略
        }
    }
}
