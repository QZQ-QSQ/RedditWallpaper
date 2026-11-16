using RedditWallpaper.Models;

namespace RedditWallpaper.Services;

/// <summary>从本地文件夹随机挑选一张图片作为壁纸</summary>
public static class LocalImageService
{
    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp" };

    public static string? GetRandomImage(string folder, string resolution,
        IReadOnlyCollection<string>? recent = null)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => ImageSize.Matches(f, resolution))
            .ToList();

        if (files.Count == 0) return null;

        if (recent is { Count: > 0 })
        {
            var fresh = files.Where(f => !recent.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            if (fresh.Count > 0) files = fresh; // 全是旧图时才允许重复
        }

        return files[Random.Shared.Next(files.Count)];
    }
}
