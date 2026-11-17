using System.Collections.Concurrent;

namespace RedditWallpaper.Services;

/// <summary>
/// 文件安全策略：
/// 1. 只有本次运行中由程序自己新建的文件，才允许被自动清理（且进回收站）；
/// 2. 任何已存在的文件都不会被覆盖、不会被删除；
/// 3. 绝不使用 File.Delete / Directory.Delete。
/// </summary>
public static class SafeFile
{
    private static readonly ConcurrentDictionary<string, byte> WrittenThisSession =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>记录：这个文件是本次运行由程序写入的。</summary>
    public static void MarkWritten(string path)
    {
        try { WrittenThisSession[Path.GetFullPath(path)] = 1; } catch { }
    }

    public static bool WasWrittenThisSession(string path)
    {
        try { return WrittenThisSession.ContainsKey(Path.GetFullPath(path)); }
        catch { return false; }
    }

    /// <summary>
    /// 仅当文件确由本次运行创建时，才移到回收站。返回 false 表示未做任何删除动作。
    /// </summary>
    public static bool DeleteOwned(string path)
    {
        if (!WasWrittenThisSession(path)) return false;

        var moved = RecycleBin.Delete(path);
        if (moved)
        {
            try { WrittenThisSession.TryRemove(Path.GetFullPath(path), out _); } catch { }
        }
        return moved;
    }

    /// <summary>把文件移入回收站（用于用户手动删除，调用方需先让用户确认）。</summary>
    public static bool MoveToRecycleBin(string path) => RecycleBin.Delete(path);
}
