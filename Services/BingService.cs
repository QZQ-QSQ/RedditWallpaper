using System.Text.Json;
using RedditWallpaper.Models;

namespace RedditWallpaper.Services;

/// <summary>
/// Bing 每日壁纸：官方图片归档接口 HPImageArchive（JSON 格式）。
/// 中国大陆优先用 cn.bing.com，失败时回退 www.bing.com。
/// </summary>
public sealed class BingService
{
    public async Task<RedditService.DownloadResult> TryDownloadWallpaperAsync(AppSettings s, string saveFolder)
    {
        var http = NetworkHelper.GetClient(s, NetworkHelper.BrowserUserAgent);
        var lastError = "";

        foreach (var host in new[] { "https://cn.bing.com", "https://www.bing.com" })
        {
            try
            {
                var json = await http.GetStringAsync($"{host}/HPImageArchive.aspx?format=js&idx=0&n=8");
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("images", out var images) ||
                    images.GetArrayLength() == 0) { lastError = $"{host} 返回数据无图片"; continue; }

                // 随机挑最近 8 天里的一天
                var idx = Random.Shared.Next(images.GetArrayLength());
                var img = images[idx];

                var date = img.TryGetProperty("enddate", out var ed) ? ed.GetString() ?? DateTime.Now.ToString("yyyyMMdd") : DateTime.Now.ToString("yyyyMMdd");
                var urlBase = img.TryGetProperty("urlbase", out var ub) ? ub.GetString() : null;
                var relUrl = img.TryGetProperty("url", out var u) ? u.GetString() : null;

                // 1) 优先尝试超清 _UHD（3840x2160），失败再用接口自带的 1920x1080
                if (!string.IsNullOrEmpty(urlBase))
                {
                    var path = await DownloadAsync(http, $"{host}{urlBase}_UHD.jpg", saveFolder,
                        BuildFileName(urlBase, date, uhd: true));
                    if (path is not null)
                    {
                        if (!ImageSize.Matches(path, s.WallpaperResolution))
                        { try { File.Delete(path); } catch { } }
                        else return RedditService.DownloadResult.Ok(path);
                    }
                }
                if (!string.IsNullOrEmpty(relUrl))
                {
                    var full = relUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? relUrl : host + relUrl;
                    var path = await DownloadAsync(http, full, saveFolder,
                        BuildFileName(urlBase, date, uhd: false));
                    if (path is not null)
                    {
                        if (!ImageSize.Matches(path, s.WallpaperResolution))
                        { try { File.Delete(path); } catch { } }
                        else return RedditService.DownloadResult.Ok(path);
                    }
                }
                lastError = $"{host} 图片下载失败或不符合分辨率 {s.WallpaperResolution}（已删除）";
            }
            catch (Exception ex)
            {
                lastError = $"{host} {ex.InnerException?.Message ?? ex.Message}";
            }
        }
        return RedditService.DownloadResult.Fail(lastError.Length > 0 ? lastError : "未知错误");
    }

    /// <summary>用 Bing 原图 ID（OHR.xxx，位于 urlBase 的 id 参数里）作为文件名</summary>
    private static string BuildFileName(string? urlBase, string date, bool uhd)
    {
        var name = $"bing_{date}";
        if (!string.IsNullOrEmpty(urlBase))
        {
            var idx = urlBase.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var id = Uri.UnescapeDataString(urlBase[(idx + 3)..].Split('&')[0]);
                if (id.Length > 0) name = id;
            }
        }

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name + (uhd ? "_UHD" : "") + ".jpg";
    }

    private static async Task<string?> DownloadAsync(HttpClient http, string url, string folder, string fileName)
    {
        try
        {
            Directory.CreateDirectory(folder);
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!ct.StartsWith("image/", StringComparison.Ordinal)) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 50_000) return null;

            var path = Path.Combine(folder, fileName);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
