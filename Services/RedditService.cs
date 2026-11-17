using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RedditWallpaper.Models;

namespace RedditWallpaper.Services;

/// <summary>
/// Reddit 图片获取（三条路，逐级降级）：
/// 1) OAuth API（oauth.reddit.com，需在 reddit.com/prefs/apps 建 script 应用）；
/// 2) 公开 JSON 接口（old/www .json）——Reddit 会按 IP+端点封锁，返回 "blocked by network security" 页；
/// 3) RSS 订阅（/{sub}/{sort}/.rss）——实测封锁环境仍返回真实 Atom，图片直链在 content 里。
/// 参考 GitHub：mikf/gallery-dl#8838、jackwener/OpenCLI#2801、Panniantong/Agent-Reach#964。
/// </summary>
public sealed class RedditService
{
    public record RedditImage(string Url, string Id, string Title, int Score, string Slug = "");

    public record DownloadResult(string? Path, string? Error, string? Note = null)
    {
        public static DownloadResult Ok(string path, string? note = null) => new(path, null, note);
        public static DownloadResult Fail(string error) => new(null, error);
    }

    private string? _token;
    private DateTime _tokenExpiry = DateTime.MinValue;

    // RSS 限流很严（连续请求会 429），缓存候选列表，轮换时直接从缓存取
    private readonly Dictionary<string, (List<RedditImage> Items, DateTime Fetched)> _rssCache = new();
    private static readonly TimeSpan RssCacheTtl = TimeSpan.FromMinutes(15);

    // 分辨率不匹配的帖子 ID 记录，避免每轮重复下载同一批不匹配图片（切换分辨率时清空）
    private readonly HashSet<string> _resolutionSkip = new(StringComparer.OrdinalIgnoreCase);
    private string _resolutionSkipKey = "";

    // .json 被封锁后冷却一段时间，避免每轮都白撞一次
    private DateTime _jsonBlockedUntil = DateTime.MinValue;

    /// <summary>Reddit 支持的排序（含 best / controversial）</summary>
    public static readonly string[] Sorts = { "hot", "new", "top", "rising", "best", "controversial" };

    /// <summary>把 "random" 解析成具体排序，其他值原样返回</summary>
    public static string ResolveSort(string sort) =>
        sort == "random"
            ? Sorts[Random.Shared.Next(Sorts.Length)]
            : sort;

    private static bool IsValidSort(string sort) => Sorts.Contains(sort);

    /// <summary>尝试从 Reddit 获取一张图片并下载到保存文件夹；失败返回 Error 原因。</summary>
    public async Task<DownloadResult> TryDownloadWallpaperAsync(AppSettings s, string saveFolder)
    {
        var randomMode = s.RedditSort == "random";
        if (randomMode)
        {
            s = s.Clone();
            s.RedditSort = ResolveSort("random");
            // 随机排序：忽略时间范围与最低得分
            s.RedditMinScore = 0;
            s.RedditTimeRange = "all";
        }

        var http = NetworkHelper.GetClient(s, NetworkHelper.RedditUserAgent);

        var resKey = s.WallpaperResolution ?? "any";
        if (!string.Equals(resKey, _resolutionSkipKey, StringComparison.OrdinalIgnoreCase))
        {
            _resolutionSkip.Clear();
            _resolutionSkipKey = resKey;
        }

        var subs = string.Join("+", s.Subreddits
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim().TrimStart('r', '/').TrimStart('/')));

        if (subs.Length == 0) return DownloadResult.Fail("未填写社区名称");

        var errors = new List<string>();
        List<RedditImage>? candidates = null;
        string? note = null;

        // ---------- 1/2. OAuth 或公开 JSON（被封锁后冷却 10 分钟）----------
        if (DateTime.UtcNow >= _jsonBlockedUntil)
        {
            try
            {
                var json = await FetchJsonAsync(http, s, subs);
                if (json is not null)
                {
                    candidates = ParseImages(json, s);
                    if (candidates.Count == 0)
                    {
                        errors.Add("JSON 中没有符合条件的静态图片（检查社区名/最低得分）");
                        candidates = null;
                    }
                }
            }
            catch (RedditBlockedException)
            {
                _jsonBlockedUntil = DateTime.UtcNow.AddMinutes(10);
                errors.Add(".json 被 Reddit 封锁（当前 IP 端点级封锁）");
            }
            catch (Exception ex)
            {
                errors.Add("JSON 接口: " + ShortError(ex));
            }
        }

        // ---------- 3. 新版 HTML（www.reddit.com，shreddit-post，带得分）----------
        if (candidates is null && !string.IsNullOrWhiteSpace(s.RedditSessionCookie))
        {
            var html = await FetchFromNewHtmlAsync(http, s, subs);
            if (html?.Items is { Count: > 0 })
            {
                candidates = html.Value.Items;
                note = "HTML 模式（新版页面）：已按最低得分/NSFW 过滤";
            }
            else if (html is not null)
            {
                errors.Add($"新版页面共 {html.Value.ImageCount} 张图片，均低于最低得分 {s.RedditMinScore}（可调低最低得分）");
            }
            else
            {
                errors.Add("新版 HTML 页面不可用（返回挑战页）");
            }
        }

        // ---------- 4. RSS 回退（带缓存）----------
        if (candidates is null)
        {
            var rss = await GetRssCandidatesAsync(http, s, subs);
            if (rss is { Count: > 0 })
            {
                candidates = rss;
                note = "RSS 模式（.json 被封锁）；RSS 不含得分，最低得分/NSFW 过滤不生效";
            }
            else
            {
                errors.Add("RSS 订阅也没有可用图片");
            }
        }

        if (candidates is null || candidates.Count == 0)
            return DownloadResult.Fail(string.Join("；", errors));

        candidates.RemoveAll(c => _resolutionSkip.Contains(c.Id));
        if (candidates.Count == 0)
            return DownloadResult.Fail("候选图片均不符合当前最低分辨率（已跳过历史不匹配项）");

        if (randomMode)
            note = (note is null ? "" : note + "；") + "随机排序：忽略时间范围与最低得分";

        // 随机打乱，避免每次都是同一张热门图
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        var mismatch = 0;
        // 第一轮：跳过已经下载过的帖子，选没见过的新图
        foreach (var img in candidates)
        {
            var path = await DownloadImageAsync(http, img.Url, saveFolder, img.Id, img.Slug, s.RedditBaseUrl);
            if (path is null) continue;
            if (!AcceptResolution(path, s)) { _resolutionSkip.Add(img.Id); mismatch++; continue; }
            return DownloadResult.Ok(path, note);
        }

        // 缓存里的图都用过了 → 强制刷新一次 RSS（多抓新帖），再试一轮
        var refreshed = await GetRssCandidatesAsync(http, s, subs, forceRefresh: true);
        if (refreshed is { Count: > 0 })
        {
            candidates = refreshed;
            candidates.RemoveAll(c => _resolutionSkip.Contains(c.Id));
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
            foreach (var img in candidates)
            {
                var path = await DownloadImageAsync(http, img.Url, saveFolder, img.Id, img.Slug, s.RedditBaseUrl);
                if (path is null) continue;
                if (!AcceptResolution(path, s)) { _resolutionSkip.Add(img.Id); mismatch++; continue; }
                return DownloadResult.Ok(path, note);
            }
        }

        // 候选全都下载过 → 从已有文件里随机复用一张（仍需符合分辨率）
        var existing = SafeListExisting(saveFolder)
            .Where(f => ImageSize.Matches(f, s.WallpaperResolution))
            .ToList();
        if (existing.Count > 0)
            return DownloadResult.Ok(existing[Random.Shared.Next(existing.Count)], note);

        // 兜底：再完整下载一次
        foreach (var img in candidates)
        {
            var path = await DownloadImageAsync(http, img.Url, saveFolder, img.Id, img.Slug, s.RedditBaseUrl);
            if (path is null) continue;
            if (!AcceptResolution(path, s)) { _resolutionSkip.Add(img.Id); mismatch++; continue; }
            return DownloadResult.Ok(path, note);
        }

        if (mismatch > 0)
            return DownloadResult.Fail($"有 {mismatch} 张图片不符合最低分辨率 {s.WallpaperResolution}，已删除未保存（可调低「最低分辨率」）");
        return DownloadResult.Fail($"解析到 {candidates.Count} 个候选但下载全部失败（网络、图片域名被墙或存储权限）");
    }

    /// <summary>检查分辨率；不符合时只清理"本次运行由程序自己下载"的文件（进回收站），其余不动。</summary>
    private static bool AcceptResolution(string path, AppSettings s)
    {
        if (ImageSize.Matches(path, s.WallpaperResolution)) return true;
        SafeFile.DeleteOwned(path); // 非本次下载的文件绝不删除
        return false;
    }

    /// <summary>OAuth 优先，其次公开 JSON（自定义反代 &gt; old.reddit.com &gt; www.reddit.com）</summary>
    private async Task<string?> FetchJsonAsync(HttpClient http, AppSettings s, string subs)
    {
        if (s.UseOAuth && !string.IsNullOrWhiteSpace(s.ClientId) &&
            (!string.IsNullOrWhiteSpace(s.RedditUsername) ||
             !string.IsNullOrWhiteSpace(s.RedditRefreshToken)))
        {
            var token = await GetTokenAsync(s, http);
            if (token is null)
                throw new InvalidOperationException("OAuth 登录失败（检查 ID/Secret/用户名/密码，账号需未开两步验证）");

            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://oauth.reddit.com/r/{subs}/{s.RedditSort}?limit=100&raw_json=1&t={s.RedditTimeRange}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await SendAsync(http, req);
        }

        var hosts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.RedditBaseUrl))
            hosts.Add(s.RedditBaseUrl.Trim().TrimEnd('/'));
        else if (!string.IsNullOrWhiteSpace(s.RedditSessionCookie))
            // 已有登录会话：先试 www（登录后 .json 通常可用），再试 old
            hosts.AddRange(new[] { "https://www.reddit.com", "https://old.reddit.com" });
        else
            hosts.AddRange(new[] { "https://old.reddit.com", "https://www.reddit.com" });

        RedditBlockedException? blocked = null;
        string lastError = "";
        foreach (var host in hosts)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{host}/r/{subs}/{s.RedditSort}.json?limit=100&raw_json=1&t={s.RedditTimeRange}");
                return await SendAsync(http, req);
            }
            catch (RedditBlockedException ex) { blocked = ex; }
            catch (Exception ex) { lastError = $"{host} {ShortError(ex)}"; }
        }
        if (blocked is not null) throw blocked;
        throw new HttpRequestException($"公开接口不可达（{lastError}）");
    }

    /// <summary>取 RSS 候选：优先内存缓存，过期才请求；请求失败/429 时回退到过期缓存</summary>
    private async Task<List<RedditImage>?> GetRssCandidatesAsync(HttpClient http, AppSettings s, string subs,
        bool forceRefresh = false)
    {
        var key = $"{s.RedditBaseUrl}|{subs}|{s.RedditSort}|{s.RedditTimeRange}";
        var hasCache = _rssCache.TryGetValue(key, out var cached);

        if (!forceRefresh && hasCache && DateTime.UtcNow - cached.Fetched < RssCacheTtl)
            return cached.Items;

        var fresh = await FetchFromRssAsync(http, s, subs);
        if (fresh is { Count: > 0 })
        {
            _rssCache[key] = (fresh, DateTime.UtcNow);
            return fresh;
        }

        // 请求失败（常见 429）→ 用旧缓存兜底
        if (hasCache && cached.Items.Count > 0) return cached.Items;
        return null;
    }

    /// <summary>
    /// RSS 回退：Reddit 封锁 .json 时，/{sub}/{sort}/.rss 通常仍返回真实 Atom。
    /// 每条 entry 的 content 里包含 i.redd.it 图片直链（或 media:thumbnail 的 preview 链接）。
    /// </summary>
    private static async Task<List<RedditImage>?> FetchFromRssAsync(HttpClient http, AppSettings s, string subs)
    {
        var host = string.IsNullOrWhiteSpace(s.RedditBaseUrl)
            ? "https://www.reddit.com"
            : s.RedditBaseUrl.Trim().TrimEnd('/');
        var sort = RedditService.ResolveSort(s.RedditSort);
        if (!RedditService.Sorts.Contains(sort)) sort = "hot";
        var url = $"{host}/r/{subs}/{sort}/.rss?limit=100";
        if (sort is "top" or "controversial") url += $"&t={s.RedditTimeRange}";

        try
        {
            string xml = "";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var resp = await http.GetAsync(url);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt == 0) { await Task.Delay(3000); continue; } // RSS 限流较严：429 稍等重试
                    return null;
                }
                if (!resp.IsSuccessStatusCode) return null;
                xml = await resp.Content.ReadAsStringAsync();
                break;
            }
            // RSS 被拦截时通常返回 HTML 页面
            if (xml.Contains("blocked by network security", StringComparison.OrdinalIgnoreCase) ||
                xml.TrimStart().StartsWith("<!")) return null;

            var list = new List<RedditImage>();
            foreach (Match m in Regex.Matches(xml, "<entry>(.*?)</entry>", RegexOptions.Singleline))
            {
                var entry = m.Groups[1].Value;

                var titleMatch = Regex.Match(entry, "<title>(.*?)</title>", RegexOptions.Singleline);
                var title = WebUtility.HtmlDecode(titleMatch.Success ? titleMatch.Groups[1].Value : "").Trim();

                if (s.SkipNsfw &&
                    (title.Contains("NSFW", StringComparison.OrdinalIgnoreCase) ||
                     title.Contains("NSFL", StringComparison.OrdinalIgnoreCase))) continue;

                var linkMatch = Regex.Match(entry, "<link href=\"([^\"]+)\"");
                var link = linkMatch.Success ? linkMatch.Groups[1].Value : "";

                var contentMatch = Regex.Match(entry, "<content type=\"html\">(.*?)</content>", RegexOptions.Singleline);
                var content = WebUtility.HtmlDecode(contentMatch.Success ? contentMatch.Groups[1].Value : "");

                // 优先 i.redd.it 原图直链
                var imgMatch = Regex.Match(content,
                    "href=\"(https?://i\\.redd\\.it/[^\"]+?\\.(?:jpg|jpeg|png))\"",
                    RegexOptions.IgnoreCase);
                string? imageUrl = imgMatch.Success ? WebUtility.HtmlDecode(imgMatch.Groups[1].Value) : null;

                // 其次 media:thumbnail（preview.redd.it，分辨率低一些）
                if (imageUrl is null)
                {
                    var thumb = Regex.Match(entry, "media:thumbnail[^>]*url=\"([^\"]+)\"");
                    if (thumb.Success)
                    {
                        var candidate = WebUtility.HtmlDecode(thumb.Groups[1].Value);
                        if (candidate.Contains("preview.redd.it")) imageUrl = candidate;
                    }
                }

                if (imageUrl is null) continue;

                var idMatch = Regex.Match(link, "/comments/([a-z0-9]+)");
                var id = idMatch.Success ? idMatch.Groups[1].Value : Guid.NewGuid().ToString("N");

                list.Add(new RedditImage(imageUrl, id, title, 0, FileNameHelper.SlugFromPermalink(link)));
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用当前会话 Cookie 验证登录状态，返回用户名（失败返回 null）</summary>
    public async Task<string?> GetLoggedInUserAsync(AppSettings s)
    {
        try
        {
            var http = NetworkHelper.GetClient(s, NetworkHelper.RedditUserAgent);
            using var resp = await http.GetAsync("https://www.reddit.com/api/me.json");
            var text = await resp.Content.ReadAsStringAsync();
            if (!text.TrimStart().StartsWith('{')) return null;

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("name", out var name)) return null;
            return name.GetString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 新版页面回退（www.reddit.com，不用 old.reddit）：
    /// 列表页内是 &lt;shreddit-post&gt; 组件，属性含 score / content-href / permalink，
    /// 服务端只渲染前 9 条，但足以按得分过滤；需要更多候选时由 RSS 兜底。
    /// </summary>
    private static async Task<(List<RedditImage>? Items, int ImageCount)?> FetchFromNewHtmlAsync(
        HttpClient http, AppSettings s, string subs)
    {
        var sort = RedditService.ResolveSort(s.RedditSort);
        if (!RedditService.Sorts.Contains(sort)) sort = "hot";
        var url = $"https://www.reddit.com/r/{subs}/{sort}/";
        if (sort is "top" or "controversial") url += $"?t={s.RedditTimeRange}";

        try
        {
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var html = await resp.Content.ReadAsStringAsync();
            if (html.Contains("blocked by network security", StringComparison.OrdinalIgnoreCase) ||
                html.Contains("<title>Welcome to Reddit</title>", StringComparison.OrdinalIgnoreCase))
                return null;

            var list = new List<RedditImage>();
            var imageCount = 0;
            var matches = Regex.Matches(html, "<shreddit-post\\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase);

            foreach (Match m in matches)
            {
                var attrs = m.Groups["attrs"].Value;

                var hrefMatch = Regex.Match(attrs, "content-href=\"([^\"]+)\"");
                if (!hrefMatch.Success) continue;

                var imageUrl = WebUtility.HtmlDecode(hrefMatch.Groups[1].Value).Split('?')[0];
                if (!IsStaticImageUrl(imageUrl)) continue;
                imageCount++;

                // NSFW 帖子带 nsfw 属性
                if (s.SkipNsfw &&
                    Regex.IsMatch(attrs, "(^|\\s)nsfw(\\s|=|$)", RegexOptions.IgnoreCase)) continue;

                var scoreMatch = Regex.Match(attrs, "score=\"(-?\\d+)\"");
                var score = scoreMatch.Success ? int.Parse(scoreMatch.Groups[1].Value) : 0;
                if (s.RedditMinScore > 0 && score < s.RedditMinScore) continue;

                var idMatch = Regex.Match(attrs, "id=\"t3_([a-z0-9]+)\"");
                var permalinkMatch = Regex.Match(attrs, "permalink=\"([^\"]+)\"");
                var permalink = permalinkMatch.Success ? WebUtility.HtmlDecode(permalinkMatch.Groups[1].Value) : "";
                if (!idMatch.Success) idMatch = Regex.Match(permalink, "/comments/([a-z0-9]+)");
                var id = idMatch.Success ? idMatch.Groups[1].Value : Guid.NewGuid().ToString("N");

                var titleMatch = Regex.Match(attrs, "post-title=\"([^\"]*)\"");
                var title = titleMatch.Success ? WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim() : "";

                list.Add(new RedditImage(imageUrl, id, title, score, FileNameHelper.SlugFromPermalink(permalink)));
            }
            return (list, imageCount);
        }
        catch
        {
            return null;
        }
    }

    private static string ShortError(Exception ex) =>
        ex.InnerException?.Message ?? ex.Message;

    private static List<string> SafeListExisting(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    var e = Path.GetExtension(f).ToLowerInvariant();
                    return e is ".jpg" or ".jpeg" or ".png";
                })
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// 获取 OAuth token：
    /// 1) 有 refresh_token（浏览器授权）→ 用它换 access_token（推荐，支持 2FA）；
    /// 2) 否则用用户名密码（script 应用，账号不能开 2FA）。
    /// </summary>
    private async Task<string?> GetTokenAsync(AppSettings s, HttpClient http)
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpiry) return _token;

        if (!string.IsNullOrWhiteSpace(s.RedditRefreshToken))
        {
            try
            {
                var json = await RequestTokenAsync(s, http, new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = s.RedditRefreshToken
                });
                var token = ParseToken(json);
                if (token is not null) return token;
            }
            catch
            {
                // 回退密码模式
            }
        }

        try
        {
            var json = await RequestTokenAsync(s, http, new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = s.RedditUsername,
                ["password"] = s.RedditPassword,
            });
            return ParseToken(json);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> RequestTokenAsync(AppSettings s, HttpClient http,
        Dictionary<string, string> form)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://www.reddit.com/api/v1/access_token")
        {
            Content = new FormUrlEncodedContent(form)
        };
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{s.ClientId.Trim()}:{s.ClientSecret.Trim()}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        return await SendAsync(http, req);
    }

    private string? ParseToken(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var at)) return null;

        _token = at.GetString();
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
        return _token;
    }

    /// <summary>Reddit 风控拦截页（"Welcome to Reddit" / "blocked by network security" HTML），非 JSON 即视为被拦</summary>
    public sealed class RedditBlockedException : Exception
    {
        public RedditBlockedException()
            : base("返回了拦截页而非 JSON（Reddit 对当前 IP 的 .json 端点封锁）") { }
    }

    private static async Task<string> SendAsync(HttpClient http, HttpRequestMessage req)
    {
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync();
        if (!text.TrimStart().StartsWith('{')) throw new RedditBlockedException();
        return text;
    }

    /// <summary>解析 Reddit JSON，提取所有图片帖</summary>
    private static List<RedditImage> ParseImages(string? json, AppSettings s)
    {
        var list = new List<RedditImage>();
        if (string.IsNullOrEmpty(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
            if (!data.TryGetProperty("children", out var children)) return list;

            foreach (var child in children.EnumerateArray())
            {
                if (!child.TryGetProperty("data", out var d)) continue;

                if (s.SkipNsfw &&
                    d.TryGetProperty("over_18", out var nsfw) &&
                    nsfw.ValueKind == JsonValueKind.True) continue;

                if (s.RedditMinScore > 0 &&
                    d.TryGetProperty("score", out var score) &&
                    score.ValueKind == JsonValueKind.Number &&
                    score.GetInt32() < s.RedditMinScore) continue;

                string? url = null;

                // 优先用 preview 里的原图地址（分辨率更高），并且排除动图
                if (d.TryGetProperty("preview", out var preview) &&
                    preview.TryGetProperty("images", out var images) &&
                    images.GetArrayLength() > 0)
                {
                    var first = images[0];
                    if (first.TryGetProperty("variants", out var variants) &&
                        (variants.TryGetProperty("gif", out _) || variants.TryGetProperty("mp4", out _)))
                    {
                        continue; // 动图不适合做壁纸
                    }
                    if (first.TryGetProperty("source", out var source) &&
                        source.TryGetProperty("url", out var srcUrl))
                    {
                        url = srcUrl.GetString()?.Replace("&amp;", "&");
                    }
                }

                // 其次用帖子本身的直链
                url ??= d.TryGetProperty("url", out var u) ? u.GetString() : null;

                if (url is null || !IsStaticImageUrl(url)) continue;

                var id = d.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                var title = d.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var permalink = d.TryGetProperty("permalink", out var pl) ? pl.GetString() ?? "" : "";

                list.Add(new RedditImage(url, id, title, 0, FileNameHelper.SlugFromPermalink(permalink)));
            }
        }
        catch
        {
            // JSON 解析失败按无结果处理
        }
        return list;
    }

    private static bool IsStaticImageUrl(string url) =>
        url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        url.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> DownloadImageAsync(HttpClient http, string url, string folder,
        string id, string slug, string? baseUrl = null)
    {
        try
        {
            // 配置了反代时，图片也按约定路径走反代：
            //   {base}/i/...   → i.redd.it
            //   {base}/p/...   → preview.redd.it
            if (!string.IsNullOrWhiteSpace(baseUrl) &&
                Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var host = uri.Host.ToLowerInvariant();
                if (host == "i.redd.it")
                    url = baseUrl.TrimEnd('/') + "/i" + uri.PathAndQuery;
                else if (host == "preview.redd.it")
                    url = baseUrl.TrimEnd('/') + "/p" + uri.PathAndQuery;
            }

            Directory.CreateDirectory(folder);
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var ext = contentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                _ => Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant()
            };
            if (ext is not (".jpg" or ".jpeg" or ".png")) return null;

            var path = Path.Combine(folder, FileNameHelper.FromSlug(slug, url, $"reddit_{id}", ext));

            // 绝不覆盖已存在的文件（可能是用户自己的同名图片）
            if (File.Exists(path)) return null;

            // 路径逃逸防护：只允许写到目标文件夹内
            var fullPath = Path.GetFullPath(path);
            var folderFull = Path.GetFullPath(folder);
            if (!fullPath.StartsWith(folderFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 10_000) return null; // 太小的多半是缩略图/占位图

            await File.WriteAllBytesAsync(path, bytes);
            SafeFile.MarkWritten(path);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
