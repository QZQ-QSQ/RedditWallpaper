namespace RedditWallpaper.Services;

/// <summary>
/// 轮换预览缓存：把「下一张」提前下载到本机缓存目录，界面用它显示缩略图；
/// 轮换时直接把缓存文件复制进图片库，避免同一张图下载两次。
/// 缓存只存程序自己下载的临时文件，不会碰用户图片。
/// </summary>
public static class PreviewCache
{
    /// <summary>缓存目录：%LocalAppData%\RedditWallpaper\preview</summary>
    public static string CacheDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedditWallpaper", "preview");

    /// <summary>按文件名找已缓存的图片；没有则返回 null（扩展名不一致时按主干名再找一次）。</summary>
    public static string? Find(string fileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            var exact = Path.Combine(CacheDir, fileName);
            if (File.Exists(exact)) return exact;

            var stem = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrEmpty(stem) || !Directory.Exists(CacheDir)) return null;
            foreach (var file in Directory.EnumerateFiles(CacheDir))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把缓存文件复制进图片库：绝不覆盖已存在文件；成功后删除缓存并登记为程序新建文件。
    /// 已存在同名文件时返回 null，调用方应改为重新下载或跳过。
    /// </summary>
    public static string? Commit(string cachedPath, string saveFolder)
    {
        try
        {
            if (!File.Exists(cachedPath)) return null;
            Directory.CreateDirectory(saveFolder);
            var target = Path.Combine(saveFolder, Path.GetFileName(cachedPath));
            if (!Path.GetFullPath(target).StartsWith(
                    Path.GetFullPath(saveFolder).TrimEnd('\\') + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return null;
            if (File.Exists(target)) return null;

            File.Copy(cachedPath, target, overwrite: false);
            SafeFile.MarkWritten(target);
            try { File.Delete(cachedPath); } catch { /* 缩略图还占着句柄，交给定期清理 */ }
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>删除一个缓存文件（只用于本目录下程序自己下载的文件）。</summary>
    public static void Remove(string? cachedPath)
    {
        if (string.IsNullOrWhiteSpace(cachedPath)) return;
        try
        {
            if (File.Exists(cachedPath)) File.Delete(cachedPath);
        }
        catch
        {
            // 忽略：文件可能正被缩略图占用
        }
    }

    /// <summary>清理超过指定时间的缓存文件。</summary>
    public static void Cleanup(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.EnumerateFiles(CacheDir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch
                {
                    // 被占用就下次再清
                }
            }
        }
        catch
        {
            // 缓存目录不可用不影响主流程
        }
    }
}
