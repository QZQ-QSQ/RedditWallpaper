namespace RedditWallpaper.Models;

/// <summary>Reddit 社区列表项：勾选表示启用。</summary>
public class SubredditEntry
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>应用设置，持久化到 %AppData%\RedditWallpaper\settings.json</summary>
public class AppSettings
{
    /// <summary>浅拷贝（用于把"随机排序"解析成具体排序而不改动原设置）</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>轮换间隔（分钟）</summary>
    public int RotateMinutes { get; set; } = 30;

    // ---------- 图片来源 ----------
    public bool UseReddit { get; set; } = true;
    public bool UseBing { get; set; } = false;
    public bool UseLocal { get; set; } = false;

    // ---------- Reddit ----------
    /// <summary>社区列表，逗号分隔，如 "EarthPorn,wallpapers"（由 SubredditEntries 中勾选的项生成）</summary>
    public string Subreddits { get; set; } = "EarthPorn,wallpapers";

    /// <summary>社区列表（每个社区一行，Enabled=false 表示暂不启用）</summary>
    public List<SubredditEntry> SubredditEntries { get; set; } = new();
    /// <summary>hot / new / top / rising</summary>
    public string RedditSort { get; set; } = "hot";
    /// <summary>top 排序的时间范围：hour/day/week/month/year/all</summary>
    public string RedditTimeRange { get; set; } = "week";
    /// <summary>最低得分，0 表示不过滤</summary>
    public int RedditMinScore { get; set; } = 100;
    /// <summary>跳过 NSFW 帖子</summary>
    public bool SkipNsfw { get; set; } = true;

    /// <summary>壁纸样式：fill / fit / stretch / center / tile</summary>
    public string WallpaperStyle { get; set; } = "fill";

    /// <summary>最低分辨率筛选：any / 1920x1080 / 2560x1440 / 3840x2160 / 3440x1440 / 1080x1920 等；
    /// 语义为"宽≥目标宽 且 高≥目标高"</summary>
    public string WallpaperResolution { get; set; } = "any";

    /// <summary>启动程序时自动开始轮换</summary>
    public bool AutoRotateOnLaunch { get; set; } = true;

    /// <summary>启动时最小化到托盘（不显示主窗口）</summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>按固定优先级尝试来源（Reddit → Bing → 本地）；关闭则随机顺序</summary>
    public bool UseSourcePriority { get; set; } = true;

    /// <summary>最近用过的图片路径（用于避免连续重复）</summary>
    public List<string> RecentImages { get; set; } = new();

    /// <summary>用户在图片库里删除过的图片（文件名主干，不含扩展名）：之后不再自动下载同一张</summary>
    public List<string> DeletedImageNames { get; set; } = new();

    /// <summary>用户点过「舍弃」的图片（文件名主干，不含扩展名）：下载前跳过；再点一次可恢复</summary>
    public List<string> DiscardedImageNames { get; set; } = new();

    /// <summary>某个文件名是否被用户删除过（比较不含扩展名的主干，忽略大小写）</summary>
    public bool IsDeletedImageName(string fileName)
    {
        if (DeletedImageNames.Count == 0 || string.IsNullOrWhiteSpace(fileName)) return false;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (var n in DeletedImageNames)
            if (string.Equals(n, stem, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>某个文件名是否被用户「舍弃」（不下载，但可恢复）</summary>
    public bool IsDiscardedImageName(string fileName)
    {
        if (DiscardedImageNames.Count == 0 || string.IsNullOrWhiteSpace(fileName)) return false;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (var n in DiscardedImageNames)
            if (string.Equals(n, stem, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>下载前的统一拦截：删除过或舍弃过都不下载</summary>
    public bool ShouldSkipDownload(string fileName) =>
        IsDeletedImageName(fileName) || IsDiscardedImageName(fileName);

    /// <summary>切换舍弃状态，返回切换后是否仍处于舍弃状态。</summary>
    public bool ToggleDiscardedImage(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem)) return false;
        if (IsDiscardedImageName(stem))
        {
            DiscardedImageNames.RemoveAll(n => string.Equals(n, stem, StringComparison.OrdinalIgnoreCase));
            return false;
        }
        DiscardedImageNames.Insert(0, stem);
        if (DiscardedImageNames.Count > 1000)
            DiscardedImageNames.RemoveRange(1000, DiscardedImageNames.Count - 1000);
        return true;
    }

    /// <summary>记录一个"不再下载"的文件名主干（最新的排最前，最多保留 1000 条）</summary>
    public void RememberDeletedImage(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem)) return;
        DeletedImageNames.RemoveAll(n => string.Equals(n, stem, StringComparison.OrdinalIgnoreCase));
        DeletedImageNames.Insert(0, stem);
        if (DeletedImageNames.Count > 1000) DeletedImageNames.RemoveRange(1000, DeletedImageNames.Count - 1000);
    }

    /// <summary>是否使用 OAuth 账号/API 方式（否则走公开 JSON 接口）</summary>
    public bool UseOAuth { get; set; } = false;
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedditUsername { get; set; } = "";
    public string RedditPassword { get; set; } = "";

    /// <summary>浏览器授权（authorization code）得到的 refresh token；免密码、支持 2FA</summary>
    public string RedditRefreshToken { get; set; } = "";

    /// <summary>内置浏览器登录后拿到的 reddit_session Cookie（无需 API 应用）</summary>
    public string RedditSessionCookie { get; set; } = "";

    /// <summary>自定义 Reddit 反代/镜像地址（可选），如 https://reddit.example.com；
    /// 设置后公开接口走它，绕过本机出口 IP 被 Reddit 风控的问题</summary>
    public string RedditBaseUrl { get; set; } = "";

    /// <summary>Devvit 应用的外部订阅源地址（可选），如 https://wallpaper-feed-abc123-external.devvit.net/external/feed；
    /// 走 Reddit 官方 Devvit 平台代取数据，不需要 prefs/apps 应用</summary>
    public string DevvitFeedUrl { get; set; } = "";

    /// <summary>Devvit Managed Token（devvit_at_...），仅保存在本机设置文件里</summary>
    public string DevvitToken { get; set; } = "";

    // ---------- 文件夹 ----------
    /// <summary>图片保存文件夹（Reddit/Bing 下载的图都存这里）</summary>
    public string SaveFolder { get; set; } = "";
    /// <summary>本地图片来源文件夹</summary>
    public string LocalFolder { get; set; } = "";
}
