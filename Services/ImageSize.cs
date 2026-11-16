namespace RedditWallpaper.Services;

/// <summary>读取图片像素尺寸（JPEG/PNG/BMP 头部解析），并按预设分辨率过滤。</summary>
public static class ImageSize
{
    public static (int Width, int Height)? Read(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            var b0 = br.ReadByte();
            var b1 = br.ReadByte();
            fs.Position = 0;

            if (b0 == 0x89 && b1 == 0x50) return ReadPng(br);   // PNG
            if (b0 == 0xFF && b1 == 0xD8) return ReadJpeg(br);  // JPEG
            if (b0 == 0x42 && b1 == 0x4D) return ReadBmp(br);   // BMP
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static (int, int)? ReadPng(BinaryReader br)
    {
        br.BaseStream.Position = 16;
        var w = ReadBigEndianInt32(br);
        var h = ReadBigEndianInt32(br);
        return w > 0 && h > 0 ? (w, h) : null;
    }

    private static int ReadBigEndianInt32(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }

    private static (int, int)? ReadBmp(BinaryReader br)
    {
        br.BaseStream.Position = 18;
        var w = br.ReadInt32();
        var h = Math.Abs(br.ReadInt32());
        return w > 0 && h > 0 ? (w, h) : null;
    }

    private static (int, int)? ReadJpeg(BinaryReader br)
    {
        br.BaseStream.Position = 2;
        while (br.BaseStream.Position < br.BaseStream.Length - 10)
        {
            if (br.ReadByte() != 0xFF) continue;

            var marker = br.ReadByte();
            // 跳过填充的 0xFF
            while (marker == 0xFF) marker = br.ReadByte();

            // SOF0..SOF15（除 DHT/JPG/DAC 之外的帧头）
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
                or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                var length = (br.ReadByte() << 8) + br.ReadByte();
                _ = br.ReadByte(); // 精度
                var h = (br.ReadByte() << 8) + br.ReadByte();
                var w = (br.ReadByte() << 8) + br.ReadByte();
                return w > 0 && h > 0 ? (w, h) : null;
            }

            // 无长度字段的独立标记
            if (marker is 0xD8 or 0xD9 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;

            var segLength = (br.ReadByte() << 8) + br.ReadByte();
            if (segLength < 2) return null;
            br.BaseStream.Position += segLength - 2;
        }
        return null;
    }

    /// <summary>按"最低分辨率"判断：宽 ≥ 目标宽 且 高 ≥ 目标高 即可；resolution 为 "any"/空 时全部接受。</summary>
    public static bool Matches(string path, string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution) || resolution.Equals("any", StringComparison.OrdinalIgnoreCase))
            return true;

        var parts = resolution.Split('x', '×');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var targetW) ||
            !int.TryParse(parts[1], out var targetH) ||
            targetW <= 0 || targetH <= 0)
            return true;

        var size = Read(path);
        if (size is null) return true; // 读不出尺寸就不拦

        var (w, h) = size.Value;
        return w >= targetW && h >= targetH;
    }
}
