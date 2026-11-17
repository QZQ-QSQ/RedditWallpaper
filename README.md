# RedditWallpaper - Reddit / Bing / 本地文件夹 桌面壁纸轮换工具

基于 **WinUI 3** 的桌面程序，非打包（免安装）运行，兼容 **Windows 10 1809 (10.0.17763) 及以上**，
包括 Windows 11。程序会按用户设定的间隔，从 Reddit 社区、Bing 每日壁纸、本地文件夹随机更换桌面壁纸，
下载的图片会保存到自定义文件夹。

## 编译

需要 **.NET 10 SDK**（项目使用 .NET 10 + Windows App SDK 2.6.5；不再依赖 Visual Studio）。

命令行方式（在本目录）：

```bat
dotnet build .\RedditWallpaper.csproj -c Release -p:Platform=x64 -v:minimal --nologo
```

编译产物在 `bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\` 下，`RedditWallpaper.exe` 为独立 exe
（SelfContained + WindowsAppSDKSelfContained，目标机器**不需要**安装 .NET 运行时和 Windows App SDK）。
项目只引用 `Microsoft.WindowsAppSDK.WinUI` 组件包，不带 AI/ML/ONNX 负载；如无需要，不要换成完整
`Microsoft.WindowsAppSDK` 元包（会多约 66 MB）。

要出 32 位版：把 `/p:Platform=x64` 换成 `/p:Platform=x86`。

## 功能

- **Reddit 社区图片**：自定义任意数量的子版块（subreddit），如 `EarthPorn,wallpapers,waterporn`；
  可选排序方式（hot/new/top/rising）、top 的时间范围、最低得分、是否跳过 NSFW。
- **Bing 每日壁纸**：使用 Bing 官方 `HPImageArchive` 接口，优先下载 4K（_UHD）版本。
- **本地文件夹**：递归扫描 jpg/png/bmp，随机选取。
- **自定义轮换间隔**：1 分钟～1440 分钟，界面显示倒计时。
- **壁纸样式**：填充 / 适应 / 拉伸 / 居中 / 平铺。
- **启动即轮换**：可设置启动程序时自动开始轮换。
- **托盘常驻**：关闭窗口收进系统托盘，继续后台轮换；托盘右键可显示窗口/立即更换/退出。
- **可拖拽分隔条**：设置区与图片库之间的边界可用鼠标拖拽调整宽度。
- **开机自启路径自愈**：程序移动位置后，启动时自动修正自启注册表里的 exe 路径。
- **文件安全策略**：程序绝不覆盖任何已存在文件；自动清理仅限“本次运行由程序自己下载”的文件，且删除一律进回收站；手动删除需二次确认。
- **最近使用记录**：界面列出最近 20 张壁纸，点击即可切回。
- **来源优先级**：默认 Reddit → Bing → 本地依次尝试，也可切换为随机。
- **避免重复**：Reddit 已下载过的帖子自动跳过换下一张；本地文件夹不连续使用同一张图。
- **RSS 缓存**：Reddit `.json` 被封锁时走 RSS，候选列表缓存 15 分钟并对 429 限流自动退避。
- **浏览器授权登录**：OAuth 授权码流程，免密码、支持两步验证（2FA）。
- **自定义保存文件夹**：Reddit/Bing 图片按来源归档；若保存文件夹本身已命名为 `reddit`/`bing`，则直接存入，不再多套一层。
- **开机自启**：写入 HKCU Run 注册表键。
- 设置保存在 `%AppData%\RedditWallpaper\settings.json`。

设置壁纸使用 Win32 `SystemParametersInfo(SPI_SETDESKWALLPAPER)`，Windows 10/11 通用。

## 如何获取 Reddit API（OAuth 应用）

1. 登录 Reddit 网页版，打开 **https://www.reddit.com/prefs/apps**
2. 拉到最底部，点 **"create another app..."（创建另一个应用）**
3. 填写：
   - **name**：随意，如 `RedditWallpaperApp`
   - 单选 **script** 类型（重要！只有 script 类型支持用户名密码方式）
   - **description**：可留空
   - **about url**：可留空
   - **redirect uri**：脚本应用不会真正用到，填 `http://localhost:8080` 即可
4. 创建后，应用下方会显示：
   - 应用名下面的一串短字符就是 **Client ID**（例如 `p-jgIkIqoo4QmU`）
   - **secret** 一栏就是 **Client Secret**
5. 把 Client ID（installed app 类型可以不填 secret）填进本程序的 "Reddit API / 账号登录" 折叠面板，
   然后点 **「浏览器授权登录（推荐，免密码）」**：浏览器会打开 Reddit 授权页，点「允许」后程序自动拿到
   refresh token，无需输入密码，**账号开启两步验证也能用**。

   也可以继续用传统的「用户名 + 密码」模式（仅 script 应用、且账号必须关闭 2FA）。

### 注意事项

- **script 类型 + 用户名密码授权（password grant）要求账号未开启两步验证（2FA）**。
  开启了 2FA 的账号密码授权会失败，此时要么关闭 2FA，要么改用其他授权方式（本程序暂未实现）。
- 即使不填 API，程序也能用 Reddit 公开 JSON 接口。实测部分网络下 `www.reddit.com` 会 403，
  `old.reddit.com` 可用，程序已实现：优先 old.reddit.com，失败回退 www.reddit.com。
- User-Agent 已按 Reddit 规范设置；如果做服务器端大规模调用，需要遵守 Reddit API 条款。

## Devvit 路线（不需要 prefs/apps，绕开 500）

Reddit 官方的 Devvit 平台允许自建应用在服务端直接调用 Reddit API（`context.reddit`，平台自动鉴权，
**不需要创建 prefs/apps 应用**，因此不会遇到 500）。`devvit-feed/` 目录是配套的 Devvit 应用：

```
devvit-feed/
  devvit.json                 # server + externalEndpoints 配置
  src/server/index.ts         # GET /external/feed：按社区/排序/得分取图片帖
  package.json                # esbuild 打包成 CJS
```

它会返回 `{ items: [{ id, title, url, score, subreddit }] }`，桌面程序里的
「Devvit 订阅源地址」+「Devvit Managed Token」填上即可，优先于直连 Reddit。

### 使用步骤

1. 安装 Node.js 24.18+ 与 Devvit CLI：`npm install -g devvit`
2. `cd devvit-feed && npm install && npm run build`
3. `devvit login`（浏览器登录 Reddit 账号）
4. `devvit playtest`（自动创建测试子版块并安装应用）
5. **申请 External Endpoints 权限**（官方限制功能，需审核）：
   按 [官方文档](https://developers.reddit.com/docs/capabilities/server/external-endpoints) 填写申请表单
6. 审核通过后，在开发者后台 App Settings → App Tokens 创建 **Managed Token**（`devvit_at_...`）
7. 把端点地址 `https://<app-slug>-<subreddit_id>-external.devvit.net/external/feed`
   和令牌填进桌面程序
8. 若某个子版块流程不熟悉，也可先只跑 playtest，用 `devvit playtest` 的日志确认接口逻辑

> 注意：External Endpoints 是**需要申请的受限能力**（5 请求/秒、请求体 10MB 上限）。
> 申请通过前，桌面程序继续用 RSS 模式即可，二者互不影响。

## 常见问题

### Reddit 提示 ".json 被 Reddit 封锁"

Reddit 会按「IP + 端点」封锁 `.json` 接口（返回 200 + "Welcome to Reddit" 或 403 + "blocked by network security"），
即使同一 IP 的 HTML/RSS 正常。这是 2025-2026 年多个开源项目确认过的行为：

- [mikf/gallery-dl#8838](https://github.com/mikf/gallery-dl/issues/8838)
- [jackwener/OpenCLI#2801](https://github.com/jackwener/OpenCLI/issues/2801)（修复方式：HTML 回退）
- [Panniantong/Agent-Reach#964](https://github.com/Panniantong/Agent-Reach/issues/964)（实测同 IP 下 HTML 200 / .json 403）

本程序的应对：`.json` 失败后自动回退到 RSS 订阅（`/r/{社区}/{排序}/.rss`），
RSS 里包含图片直链（`i.redd.it`），仍能正常下载壁纸。RSS 模式没有得分数据，最低得分/NSFW 过滤不生效。

### 提示 "You've been blocked by network security" / 拦截页

Reddit 对部分数据中心/代理 IP 做 IP 级封锁。切换代理/VPN 节点（优先住宅/家宽/原生 IP，避开机房节点）
通常即可恢复；期间可正常使用 Bing 和本地来源。

如果自己有 VPS，可以在服务器上做反代（IP 干净即可），程序里填「自定义反代地址」：

```nginx
server {
    listen 443 ssl;
    server_name reddit.example.com;
    # ... ssl 配置 ...

    location /r/  { proxy_pass https://old.reddit.com/r/;  proxy_set_header Host old.reddit.com; }
    location /i/  { proxy_pass https://i.redd.it/;         proxy_set_header Host i.redd.it; }
    location /p/  { proxy_pass https://preview.redd.it/;   proxy_set_header Host preview.redd.it; }
}
```

程序对图片路径的约定：`{反代}/i/...` → `i.redd.it`，`{反代}/p/...` → `preview.redd.it`。

### 创建应用一直报 "an error occurred (status: 500)"

Reddit 服务端的老问题，与填写内容无关。依次尝试：确认账号邮箱已验证；
用 `https://old.reddit.com/prefs/apps/` 创建（成功率最高）；无痕窗口并禁用广告拦截扩展；
换一个更独特的应用名；切换网络/代理节点；换一个注册较久、有活跃记录的账号。
