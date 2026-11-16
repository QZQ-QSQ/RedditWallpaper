using System.Text.Json;

namespace RedditWallpaper.Services;

/// <summary>
/// reddit_session 是 JWT，可从中读取签发/过期时间，
/// 用于界面展示"会话有效期至 x（剩余 n 天）"。
/// </summary>
public static class SessionInfo
{
    public static DateTimeOffset? GetExpiry(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return null;

        try
        {
            var parts = cookie.Split('.');
            if (parts.Length != 3) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            var padding = (4 - payload.Length % 4) % 4;
            payload = payload.PadRight(payload.Length + padding, '=');

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.TryGetProperty("exp", out var exp) &&
                exp.TryGetInt64(out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        }
        catch
        {
            // 不是 JWT 或格式异常，忽略
        }
        return null;
    }

    public static string Describe(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return "未登录";

        var expiry = GetExpiry(cookie);
        if (expiry is null) return "已登录（无法读取有效期）";

        var remain = expiry.Value - DateTimeOffset.Now;
        if (remain <= TimeSpan.Zero) return $"会话已过期（{expiry:yyyy-MM-dd}）";

        return $"会话有效期至 {expiry:yyyy-MM-dd}（剩余 {Math.Ceiling(remain.TotalDays)} 天）";
    }
}
