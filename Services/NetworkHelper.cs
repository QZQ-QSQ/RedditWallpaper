using System.Collections.Concurrent;
using System.Net;
using RedditWallpaper.Models;

namespace RedditWallpaper.Services;

/// <summary>统一的 HttpClient 工厂：按登录 Cookie 创建并缓存。</summary>
public static class NetworkHelper
{
    private static readonly ConcurrentDictionary<string, HttpClient> Cache = new();

    public const string RedditUserAgent =
        "windows:RedditWallpaperApp:v1.0 (desktop wallpaper rotator)";

    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    public static HttpClient GetClient(AppSettings settings, string userAgent)
    {
        var cookie = (settings.RedditSessionCookie ?? "").Trim();
        var key = userAgent + "|" + cookie;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        if (cookie.Length > 0)
        {
            handler.UseCookies = true;
            handler.CookieContainer = new CookieContainer();
            try
            {
                handler.CookieContainer.Add(new Cookie("reddit_session", cookie, "/", ".reddit.com"));
                handler.CookieContainer.Add(new Cookie("over18", "1", "/", ".reddit.com"));
            }
            catch
            {
                // Cookie 值非法时忽略，退化为匿名请求
            }
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);

        if (Cache.Count > 8) Cache.Clear();
        Cache[key] = client;
        return client;
    }
}
