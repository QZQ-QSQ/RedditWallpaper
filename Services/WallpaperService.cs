using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace RedditWallpaper.Services;

/// <summary>
/// 通过 Win32 API（SystemParametersInfo）设置桌面壁纸。
/// 打包/非打包应用在 Windows 10 1809+ 上都可用，是最通用的方式。
/// </summary>
public static class WallpaperService
{
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string? pvParam, uint fWinIni);

    /// <summary>设置桌面壁纸</summary>
    /// <param name="imagePath">图片路径</param>
    /// <param name="style">fill / fit / stretch / center / tile</param>
    public static bool SetWallpaper(string imagePath, string style = "fill")
    {
        if (!File.Exists(imagePath)) return false;

        // WallpaperStyle: 10=填充 6=适应 2=拉伸 0=居中/平铺 22=跨屏；平铺还需 TileWallpaper=1
        var (styleValue, tileValue) = style switch
        {
            "fit" => ("6", "0"),
            "stretch" => ("2", "0"),
            "center" => ("0", "0"),
            "tile" => ("0", "1"),
            _ => ("10", "0"), // fill
        };

        using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true))
        {
            key?.SetValue("WallpaperStyle", styleValue);
            key?.SetValue("TileWallpaper", tileValue);
        }

        return SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }
}
