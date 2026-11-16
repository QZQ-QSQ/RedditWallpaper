using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RedditWallpaper.Models;

namespace RedditWallpaper.Services;

/// <summary>
/// Reddit OAuth「授权码」流程（recommended）：
/// 浏览器登录 Reddit → 用户点「允许」→ 本机 8080 端口接收 code → 换成 refresh_token。
/// 优点：不需要把密码交给程序，账号开了两步验证也能用。
/// </summary>
public static class OAuthHelper
{
    public static async Task<(string? RefreshToken, string? Error)> AuthorizeAsync(
        AppSettings settings, int port = 8080)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId))
            return (null, "未填写 Client ID");

        var redirectUri = $"http://localhost:{port}/";
        var state = Guid.NewGuid().ToString("N");

        var authorizeUrl =
            "https://www.reddit.com/api/v1/authorize" +
            "?client_id=" + Uri.EscapeDataString(settings.ClientId.Trim()) +
            "&response_type=code" +
            "&state=" + state +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&duration=permanent" +
            "&scope=read";

        var listener = new TcpListener(IPAddress.IPv6Any, port);
        try
        {
            listener.Server.DualMode = true;   // 同时接受 IPv4(localhost→127.0.0.1) 和 IPv6(→::1)
            listener.Start();
        }
        catch (Exception ex)
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
            }
            catch
            {
                return (null, $"无法监听本机 {port} 端口（可能被占用）：{ex.Message}");
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            string? code = null;
            string? oauthError = null;

            while (code is null && oauthError is null && !timeout.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);

                // 只接受本机回调，防止局域网其他机器拿 code
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
                {
                    client.Dispose();
                    continue;
                }

                using var stream = client.GetStream();

                // 读取请求行： GET /?state=..&code=.. HTTP/1.1
                var requestLine = await ReadRequestLineAsync(stream, timeout.Token);
                if (requestLine is null) continue;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2) continue;

                var query = ParseQuery(parts[1]);

                if (query.TryGetValue("code", out var c) && query.GetValueOrDefault("state") == state)
                {
                    code = c;
                    await WriteHtmlAsync(stream,
                        "<h2>授权成功</h2><p>可以关闭此页面，回到壁纸程序。</p>");
                }
                else if (query.TryGetValue("error", out var err))
                {
                    oauthError = query.GetValueOrDefault("error_description") ?? err;
                    await WriteHtmlAsync(stream, "<h2>授权被拒绝</h2><p>可以关闭此页面。</p>");
                }
                else
                {
                    // 浏览器可能还会请求 /favicon.ico 等
                    await WriteHtmlAsync(stream, "<h2>等待 Reddit 授权…</h2>");
                }
            }

            if (oauthError is not null) return (null, oauthError);
            if (code is null) return (null, "等待授权超时（3 分钟）");

            return await ExchangeCodeAsync(settings, code, redirectUri);
        }
        catch (OperationCanceledException)
        {
            return (null, "等待授权超时（3 分钟）");
        }
        catch (Exception ex)
        {
            return (null, ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<(string?, string?)> ExchangeCodeAsync(
        AppSettings settings, string code, string redirectUri)
    {
        try
        {
            var http = NetworkHelper.GetClient(settings, NetworkHelper.RedditUserAgent);

            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://www.reddit.com/api/v1/access_token");
            // script/web 应用用 client_id:secret；installed app 的 secret 为空
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{settings.ClientId.Trim()}:{settings.ClientSecret.Trim()}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri
            });

            var resp = await http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();

            if (!text.TrimStart().StartsWith('{'))
                return (null, "Reddit 返回了拦截页（当前出口 IP 被封锁）——请切换节点或稍后重试");

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("refresh_token", out var rt) &&
                rt.GetString() is { Length: > 0 } token)
                return (token, null);

            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            return (null, "换取 token 失败" + (err is null ? "" : "：" + err));
        }
        catch (Exception ex)
        {
            return (null, ex.InnerException?.Message ?? ex.Message);
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new StringBuilder();
        var one = new byte[1];
        while (buffer.Length < 2048)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0) break;
            var ch = (char)one[0];
            if (ch == '\n') break;
            if (ch != '\r') buffer.Append(ch);
        }
        return buffer.Length == 0 ? null : buffer.ToString();
    }

    private static Dictionary<string, string> ParseQuery(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var qIndex = path.IndexOf('?');
        if (qIndex < 0) return result;

        var query = path[(qIndex + 1)..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]);
            var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            result[key] = value;
        }
        return result;
    }

    private static async Task WriteHtmlAsync(NetworkStream stream, string body)
    {
        var html = $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Reddit 授权</title></head>" +
                   $"<body style=\"font-family:Segoe UI,sans-serif;padding:40px\">{body}</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }
}
