namespace RedditWallpaper.Services;

/// <summary>从图片 URL 提取原始文件名（用于保存下载的图片）。</summary>
public static class FileNameHelper
{
    /// <summary>从 Reddit 帖子链接中提取 slug，如 /r/X/comments/1xxf5jt/shower_2_xxx/ → shower_2_xxx</summary>
    public static string SlugFromPermalink(string? permalink)
    {
        if (string.IsNullOrWhiteSpace(permalink)) return "";

        var parts = permalink.Trim('/').Split('/');
        var index = Array.FindIndex(parts, p => p.Equals("comments", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 2 < parts.Length ? parts[index + 2] : "";
    }

    /// <summary>
    /// 优先用帖子 slug 作为文件名（如 shower_2_veronika_wonka_roma_roma_2018_3840x2160），
    /// 没有 slug 时退化为 URL 原文件名，最后才用 ID。
    /// </summary>
    public static string FromSlug(string? slug, string url, string fallbackId, string fallbackExt)
    {
        var ext = NormalizeExt(fallbackExt);

        var name = Sanitize(slug ?? "");
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        name = name.Trim(' ', '.');
        if (name.Length > 150) name = name[..150].Trim();

        if (name.Length == 0) return FromUrl(url, fallbackId, ext);

        var urlExt = Path.GetExtension(name).ToLowerInvariant();
        return urlExt is ".jpg" or ".jpeg" or ".png" ? name : name + ext;
    }

    /// <summary>
    /// 优先用 URL 路径里的原文件名（如 i.redd.it/nhx7y1eytv1i1.jpeg），
    /// 拿不到或扩展名不合法时用 fallbackId + 内容类型对应扩展名。
    /// </summary>
    public static string FromUrl(string url, string fallbackId, string fallbackExt)
    {
        var ext = NormalizeExt(fallbackExt);

        string name = "";
        try
        {
            name = Path.GetFileName(new Uri(url).AbsolutePath);
        }
        catch
        {
            // URL 解析失败，走 fallback
        }

        name = Sanitize(name);
        var urlExt = Path.GetExtension(name).ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(name) || urlExt is not (".jpg" or ".jpeg" or ".png"))
        {
            name = Sanitize(fallbackId);
            return name + ext;
        }

        return name;
    }

    private static string NormalizeExt(string ext) =>
        ext is ".jpg" or ".jpeg" or ".png" ? ext : ".jpg";

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }
}
