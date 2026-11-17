using System.Net.Http.Headers;
using System.Text.Json;
using RedditWallpaper.Models;

namespace RedditWallpaper.Services;

/// <summary>
/// Devvit 订阅源：由 Reddit 官方 Devvit 平台上的自建应用代取数据
/// （context.reddit，服务端 API，不受客户端 IP 的 .json 封锁影响）。
/// 端点返回 { items: [{ id, title, url, score }], errors: [] }。
/// </summary>
public sealed class DevvitService
{
    private sealed record FeedItem(string Id, string Title, string Url, int Score, string Slug);

    // 分辨率不匹配的帖子 ID 记录，避免每轮重复下载（切换分辨率时清空）
    private readonly HashSet<string> _resolutionSkip = new(StringComparer.OrdinalIgnoreCase);
    private string _resolutionSkipKey = "";

    public async Task<RedditService.DownloadResult> TryDownloadWallpaperAsync(AppSettings s, string saveFolder)
    {
        if (string.IsNullOrWhiteSpace(s.DevvitFeedUrl))
            return RedditService.DownloadResult.Fail("未配置 Devvit 订阅源地址");

        var resKey = s.WallpaperResolution ?? "any";
        if (!string.Equals(resKey, _resolutionSkipKey, StringComparison.OrdinalIgnoreCase))
        {
            _resolutionSkip.Clear();
            _resolutionSkipKey = resKey;
        }

        try
        {
            var http = NetworkHelper.GetClient(s, NetworkHelper.RedditUserAgent);

            if (s.RedditSort == "random")
            {
                s = s.Clone();
                s.RedditSort = RedditService.ResolveSort("random");
                // 随机排序：忽略时间范围与最低得分
                s.RedditMinScore = 0;
                s.RedditTimeRange = "all";
            }

            var url = BuildUrl(s);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(s.DevvitToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.DevvitToken.Trim());

            using var resp = await http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return RedditService.DownloadResult.Fail($"Devvit 端点 HTTP {(int)resp.StatusCode}");

            var items = Parse(text, s);
            if (items.Count == 0)
                return RedditService.DownloadResult.Fail("Devvit 端点没有返回可用图片（检查社区名/最低得分）");

            items.RemoveAll(i => _resolutionSkip.Contains(i.Id));

            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }

            foreach (var item in items)
            {
                var path = await DownloadAsync(http, item, saveFolder, skipIfExists: true);
                if (path is null) continue;
                if (!ImageSize.Matches(path, s.WallpaperResolution))
                {
                    try { File.Delete(path); } catch { }
                    _resolutionSkip.Add(item.Id);
                    continue;
                }
                return RedditService.DownloadResult.Ok(path, "Devvit 源");
            }

            // 都下过了 → 复用已有文件
            try
            {
                var existing = Directory.EnumerateFiles(saveFolder, "*.*")
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png")
                    .Where(f => ImageSize.Matches(f, s.WallpaperResolution))
                    .ToList();
                if (existing.Count > 0)
                    return RedditService.DownloadResult.Ok(existing[Random.Shared.Next(existing.Count)], "Devvit 源（复用已有图片）");
            }
            catch { }

            return RedditService.DownloadResult.Fail("Devvit 候选图片下载失败（网络或存储权限）");
        }
        catch (Exception ex)
        {
            return RedditService.DownloadResult.Fail("Devvit 请求失败：" + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    private static string BuildUrl(AppSettings s)
    {
        var baseUrl = s.DevvitFeedUrl.Trim();
        var separator = baseUrl.Contains('?') ? "&" : "?";
        return baseUrl + separator +
               "subs=" + Uri.EscapeDataString(s.Subreddits) +
               "&sort=" + Uri.EscapeDataString(RedditService.ResolveSort(s.RedditSort)) +
               "&t=" + Uri.EscapeDataString(s.RedditTimeRange) +
               "&limit=100" +
               "&minScore=" + s.RedditMinScore +
               "&nsfw=" + (s.SkipNsfw ? "1" : "0");
    }

    private static List<FeedItem> Parse(string json, AppSettings s)
    {
        var list = new List<FeedItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return list;

            foreach (var item in items.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;

                var cleanUrl = url.Split('?')[0];
                if (!cleanUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                    !cleanUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                    !cleanUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;

                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var score = item.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : 0;
                var permalink = item.TryGetProperty("permalink", out var pl) ? pl.GetString() ?? "" : "";

                list.Add(new FeedItem(id, title, cleanUrl, score, FileNameHelper.SlugFromPermalink(permalink)));
            }
        }
        catch
        {
            // JSON 解析失败按空结果处理
        }
        return list;
    }

    private static async Task<string?> DownloadAsync(HttpClient http, FeedItem item, string folder, bool skipIfExists)
    {
        try
        {
            Directory.CreateDirectory(folder);
            using var resp = await http.GetAsync(item.Url);
            if (!resp.IsSuccessStatusCode) return null;

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var ext = contentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                _ => Path.GetExtension(new Uri(item.Url).AbsolutePath).ToLowerInvariant()
            };
            if (ext is not (".jpg" or ".jpeg" or ".png")) return null;

            var path = Path.Combine(folder, FileNameHelper.FromSlug(item.Slug, item.Url, $"reddit_{item.Id}", ext));
            if (skipIfExists && File.Exists(path)) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 10_000) return null;

            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
