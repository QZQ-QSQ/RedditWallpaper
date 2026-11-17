using Microsoft.Win32;

namespace RedditWallpaper.Services;

/// <summary>开机自启（写 HKCU Run 注册表键，非打包应用最简单可靠的方式）</summary>
public static class StartupHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "RedditWallpaper";

    public static void Set(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ??
                         Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;

        if (enable)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    /// <summary>已启用自启但注册表里的 exe 路径不是当前程序时，静默修正（程序迁移后自愈）。</summary>
    public static void EnsureCurrentPath()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        var stored = key?.GetValue(AppName) as string;
        if (stored is null) return; // 未启用自启，不动

        if (!stored.Contains(exePath, StringComparison.OrdinalIgnoreCase))
            Set(true);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is not null;
    }
}
