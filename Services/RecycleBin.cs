using System.Runtime.InteropServices;

namespace RedditWallpaper.Services;

/// <summary>
/// 删除文件到回收站（而不是永久删除），避免误删无法恢复。
/// 使用 SHFileOperation + FOF_ALLOWUNDO。
/// </summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    /// <summary>把文件移到回收站；失败时返回 false（此时文件保持不变，绝不永久删除）。</summary>
    public static bool Delete(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;

            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0", // 双 null 结尾
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
            };

            var result = SHFileOperation(ref op);
            return result == 0 && op.fAnyOperationsAborted == 0;
        }
        catch
        {
            return false;
        }
    }
}
