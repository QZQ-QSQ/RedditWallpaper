using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using RedditWallpaper.Models;
using RedditWallpaper.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace RedditWallpaper;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly RedditService _reddit = new();
    private readonly BingService _bing = new();
    private readonly DevvitService _devvit = new();

    private readonly DispatcherTimer _tickTimer;   // 每秒触发，负责倒计时和到点换壁纸
    private DateTime _nextChange = DateTime.MaxValue;
    private bool _rotating;
    private bool _busy;

    private readonly IntPtr _hwnd;
    private TrayIcon? _tray;
    private bool _exiting;
    private bool _uiReady;

    /// <summary>供 App.OnLaunched 判断是否需要"启动即隐藏"</summary>
    public static bool StartHidden { get; private set; }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _settings = SettingsService.Load();
        LoadSettingsToUi();
        RefreshHistory();
        _uiReady = true;

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => OnTick();

        Title = "Reddit 壁纸轮换";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 880));

        // 关闭窗口 → 收进托盘继续轮换
        AppWindow.Closing += OnWindowClosing;
        try
        {
            _tray = new TrayIcon("Reddit 壁纸轮换",
                Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
            _tray.ShowRequested += ShowMainWindow;
            _tray.RotateRequested += () => _ = RotateOnceAsync();
            _tray.ExitRequested += ExitApp;
        }
        catch
        {
            // 托盘创建失败不影响主功能
        }

        // 上次设置要求启动即轮换
        if (_settings.AutoRotateOnLaunch) StartRotation();

        StartHidden = _settings.StartMinimized;
        StartupHelper.EnsureCurrentPath();

        RefreshThumbnails();
    }

    /// <summary>启动即隐藏主窗口（在 Activate 之后调用）</summary>
    public void HideForStartup() => ShowWindow(_hwnd, SW_HIDE);

    // ---------------- 设置 <-> UI ----------------

    private void LoadSettingsToUi()
    {
        RotateMinutes.Value = _settings.RotateMinutes;
        UseRedditToggle.IsOn = _settings.UseReddit;
        UseBingToggle.IsOn = _settings.UseBing;
        UseLocalToggle.IsOn = _settings.UseLocal;
        SourcePriorityToggle.IsOn = _settings.UseSourcePriority;
        LoadSubredditRows();
        MinScoreBox.Value = _settings.RedditMinScore;
        SkipNsfwToggle.IsOn = _settings.SkipNsfw;
        SaveFolderBox.Text = _settings.SaveFolder;
        LocalFolderBox.Text = _settings.LocalFolder;
        UseOAuthToggle.IsOn = _settings.UseOAuth;
        ClientIdBox.Text = _settings.ClientId;
        ClientSecretBox.Text = _settings.ClientSecret;
        UsernameBox.Text = _settings.RedditUsername;
        PasswordBox.Password = _settings.RedditPassword;
        RedditBaseUrlBox.Text = _settings.RedditBaseUrl;
        DevvitFeedUrlBox.Text = _settings.DevvitFeedUrl;
        DevvitTokenBox.Text = _settings.DevvitToken;
        SessionCookieBox.Text = _settings.RedditSessionCookie;
        SessionExpiryText.Text = SessionInfo.Describe(_settings.RedditSessionCookie);
        AutoStartToggle.IsOn = StartupHelper.IsEnabled();

        SelectCombo(SortCombo, _settings.RedditSort);
        SelectCombo(RangeCombo, _settings.RedditTimeRange);
        SelectCombo(StyleCombo, _settings.WallpaperStyle);
        SelectCombo(ResolutionCombo, _settings.WallpaperResolution);
        AutoRotateOnLaunchToggle.IsOn = _settings.AutoRotateOnLaunch;
        StartMinimizedToggle.IsOn = _settings.StartMinimized;
    }

    private static void SelectCombo(ComboBox combo, string tagValue)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if ((string?)item.Tag == tagValue) { combo.SelectedItem = item; return; }
        }
    }

    private static string SelectedTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "hot";

    private AppSettings ReadSettingsFromUi()
    {
        _settings.RotateMinutes = Math.Max(1,
            double.IsNaN(RotateMinutes.Value) ? 30 : (int)RotateMinutes.Value);
        _settings.UseReddit = UseRedditToggle.IsOn;
        _settings.UseBing = UseBingToggle.IsOn;
        _settings.UseLocal = UseLocalToggle.IsOn;
        _settings.UseSourcePriority = SourcePriorityToggle.IsOn;

        // 社区列表：一行一个，勾选=启用
        var entries = new List<SubredditEntry>();
        foreach (var child in SubredditListPanel.Children)
        {
            if (child is not Grid grid) continue;
            var name = grid.Children.OfType<TextBox>().FirstOrDefault()?.Text.Trim() ?? "";
            var enabled = grid.Children.OfType<CheckBox>().FirstOrDefault()?.IsChecked == true;
            if (name.Length == 0) continue;
            entries.Add(new SubredditEntry { Name = name, Enabled = enabled });
        }
        _settings.SubredditEntries = entries;
        _settings.Subreddits = string.Join(",", entries.Where(e => e.Enabled).Select(e => e.Name));

        _settings.RedditSort = SelectedTag(SortCombo);
        _settings.RedditTimeRange = SelectedTag(RangeCombo);
        _settings.RedditMinScore = Math.Max(0,
            double.IsNaN(MinScoreBox.Value) ? 100 : (int)MinScoreBox.Value);
        _settings.SkipNsfw = SkipNsfwToggle.IsOn;
        _settings.SaveFolder = SaveFolderBox.Text.Trim();
        _settings.LocalFolder = LocalFolderBox.Text.Trim();
        _settings.UseOAuth = UseOAuthToggle.IsOn;
        _settings.ClientId = ClientIdBox.Text.Trim();
        _settings.ClientSecret = ClientSecretBox.Text.Trim();
        _settings.RedditUsername = UsernameBox.Text.Trim();
        _settings.RedditPassword = PasswordBox.Password;
        _settings.RedditBaseUrl = RedditBaseUrlBox.Text.Trim();
        _settings.DevvitFeedUrl = DevvitFeedUrlBox.Text.Trim();
        _settings.DevvitToken = DevvitTokenBox.Text.Trim();
        _settings.RedditSessionCookie = SessionCookieBox.Text.Trim();
        SessionExpiryText.Text = SessionInfo.Describe(_settings.RedditSessionCookie);
        _settings.WallpaperStyle = SelectedTag(StyleCombo) is { Length: > 0 } t ? t : "fill";
        _settings.WallpaperResolution = SelectedTag(ResolutionCombo) is { Length: > 0 } r ? r : "any";
        _settings.AutoRotateOnLaunch = AutoRotateOnLaunchToggle.IsOn;
        _settings.StartMinimized = StartMinimizedToggle.IsOn;
        return _settings;
    }

    // ---------------- 事件 ----------------

    private void ToggleTimer_Click(object sender, RoutedEventArgs e)
    {
        var s = ReadSettingsFromUi();
        SettingsService.Save(s);

        if (_rotating) StopRotation();
        else StartRotation();
    }

    private async void RotateNow_Click(object sender, RoutedEventArgs e)
    {
        var s = ReadSettingsFromUi();
        SettingsService.Save(s);
        await RotateOnceAsync();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = ReadSettingsFromUi();
        SettingsService.Save(s);

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RedditWallpaper", "settings.json");
        SetStatus($"设置已保存：{path}");
        RefreshThumbnails();
    }

    // ---------------- 社区列表 ----------------

    private void LoadSubredditRows()
    {
        SubredditListPanel.Children.Clear();

        var entries = _settings.SubredditEntries;
        if (entries.Count == 0 && !string.IsNullOrWhiteSpace(_settings.Subreddits))
        {
            // 从旧版的逗号分隔字段迁移
            entries = _settings.Subreddits
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => new SubredditEntry { Name = name, Enabled = true })
                .ToList();
        }
        if (entries.Count == 0)
            entries.Add(new SubredditEntry { Name = "", Enabled = true });

        foreach (var entry in entries)
            AddSubredditRow(entry.Name, entry.Enabled);
    }

    private void AddSubredditRow(string name, bool enabled)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textBox = new TextBox
        {
            Text = name,
            PlaceholderText = "社区名，如 EarthPorn",
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(textBox, 0);

        var check = new CheckBox
        {
            Content = "使用",
            IsChecked = enabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(check, 1);

        var remove = new Button
        {
            Content = "✕",
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(remove, 2);

        remove.Click += (_, _) =>
        {
            SubredditListPanel.Children.Remove(grid);
            if (SubredditListPanel.Children.Count == 0)
                AddSubredditRow("", true);
        };

        grid.Children.Add(textBox);
        grid.Children.Add(check);
        grid.Children.Add(remove);
        SubredditListPanel.Children.Add(grid);
    }

    private void AddSubreddit_Click(object sender, RoutedEventArgs e)
        => AddSubredditRow("", true);

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;   // 加载设置时的程序性赋值不写注册表
        StartupHelper.Set(AutoStartToggle.IsOn);
    }

    private void StyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;

        var s = ReadSettingsFromUi();
        SettingsService.Save(s);

        var current = CurrentWallpaperText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current)) return;

        var ok = WallpaperService.SetWallpaper(current, s.WallpaperStyle);
        var label = (StyleCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? s.WallpaperStyle;
        SetStatus(ok ? $"壁纸样式已切换为「{label}」（已立即应用）" : "切换壁纸样式失败");
    }

    private async void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add("*");

            // 非打包应用必须把窗口句柄交给 Picker
            InitializeWithWindow.Initialize(picker, _hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            if (sender is Button b && b == PickSaveFolder) { SaveFolderBox.Text = folder.Path; RefreshThumbnails(); }
            else if (sender is Button lb && lb == PickLocalFolder) LocalFolderBox.Text = folder.Path;
        }
        catch (Exception ex)
        {
            SetStatus("选择文件夹失败：" + ex.Message);
        }
    }

    private async void BrowserAuth_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await BrowserAuthCoreAsync();
        }
        catch (Exception ex)
        {
            SetStatus("授权出错：" + ex.Message);
        }
    }

    private async Task BrowserAuthCoreAsync()
    {
        var s = ReadSettingsFromUi();
        SettingsService.Save(s);

        if (string.IsNullOrWhiteSpace(s.ClientId))
        {
            SetStatus("请先填写 Client ID（在 reddit.com/prefs/apps 创建应用后可见）。");
            return;
        }

        SetStatus("已打开浏览器，请登录 Reddit 并点击「允许」…");
        BrowserAuthBtn.IsEnabled = false;
        try
        {
            var (token, error) = await OAuthHelper.AuthorizeAsync(s, 8080);
            if (string.IsNullOrWhiteSpace(token))
            {
                SetStatus("授权失败：" + error);
                return;
            }
            _settings.RedditRefreshToken = token;
            _settings.UseOAuth = true;
            SettingsService.Save(_settings);
            UseOAuthToggle.IsOn = true;
            SetStatus("授权成功！已保存 refresh token（免密码，支持 2FA）。");
        }
        finally
        {
            BrowserAuthBtn.IsEnabled = true;
        }
    }

    private void ClearToken_Click(object sender, RoutedEventArgs e)
    {
        _settings.RedditRefreshToken = "";
        SettingsService.Save(_settings);
        SetStatus("已清除浏览器授权 token。");
    }

    private void ClearSession_Click(object sender, RoutedEventArgs e)
    {
        _settings.RedditSessionCookie = "";
        SessionCookieBox.Text = "";
        SessionExpiryText.Text = "未登录";
        SettingsService.Save(_settings);
        SetStatus("已退出登录（清除会话 Cookie）。");
    }

    private async void VerifySession_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await VerifySessionCoreAsync();
        }
        catch (Exception ex)
        {
            SetStatus("验证出错：" + ex.Message);
        }
    }

    private async Task VerifySessionCoreAsync()
    {
        var s = ReadSettingsFromUi();
        SettingsService.Save(s);

        if (string.IsNullOrWhiteSpace(s.RedditSessionCookie))
        {
            SetStatus("请先粘贴 reddit_session Cookie。");
            return;
        }

        SetStatus("正在验证登录状态…");
        VerifySessionBtn.IsEnabled = false;
        try
        {
            var user = await _reddit.GetLoggedInUserAsync(s);
            SetStatus(user is null
                ? "验证失败：Cookie 无效或已过期（可能是网络封锁导致请求被拦）。"
                : $"登录成功：当前账号 {user}。Reddit 来源将使用账号身份访问。");
        }
        finally
        {
            VerifySessionBtn.IsEnabled = true;
        }
    }

    private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not string path) return;
        if (!File.Exists(path))
        {
            SetStatus("文件已不存在：" + path);
            RefreshHistory();
            return;
        }

        var s = ReadSettingsFromUi();
        SettingsService.Save(s);

        var ok = WallpaperService.SetWallpaper(path, s.WallpaperStyle);
        _settings.RecentImages.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentImages.Insert(0, path);
        SettingsService.Save(_settings);
        RefreshHistory();

        SetStatus(ok ? $"已切回：{Path.GetFileName(path)}" : "设置壁纸失败：" + path);
        if (ok) CurrentWallpaperText.Text = path;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = SaveFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) { SetStatus("请先设置「图片保存文件夹」。"); return; }

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("打开文件夹失败：" + ex.Message); }
    }

    // ---------------- 可拖拽分隔条 ----------------

    private bool _splitterDragging;
    private double _dragStartRootX;
    private double _leftStartWidth;

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _splitterDragging = true;
        var root = (FrameworkElement)Content!;
        _dragStartRootX = e.GetCurrentPoint(root).Position.X;
        _leftStartWidth = LeftColumn.Width.IsAbsolute ? LeftColumn.Width.Value : LeftColumn.ActualWidth;
        ((UIElement)sender).CapturePointer(e.Pointer);
        SetStatus($"分隔条：已按下（起点 {_dragStartRootX:F0}，左栏 {_leftStartWidth:F0}px）");
        e.Handled = true;
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        try
        {
            var root = (FrameworkElement)Content!;
            var dx = e.GetCurrentPoint(root).Position.X - _dragStartRootX;
            var splitterWidth = ((FrameworkElement)sender).ActualWidth;
            var total = root.ActualWidth - splitterWidth;
            var newLeft = Math.Clamp(_leftStartWidth + dx, 380, Math.Max(380, total - 480));
            LeftColumn.Width = new GridLength(newLeft, GridUnitType.Pixel);
            SetStatus($"分隔条拖动中：Δ={dx:F0} → 左栏 {newLeft:F0}px");
            e.Handled = true;
        }
        catch { /* 拖拽中的指针事件异常忽略 */ }
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        _splitterDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        SetStatus($"分隔条释放：左栏 {LeftColumn.Width.Value:F0}px");
        e.Handled = true;
    }

    // ---------------- 中间栏：图片浏览 ----------------

    private void RefreshThumbs_Click(object sender, RoutedEventArgs e) => RefreshThumbnails();

    private void RefreshThumbnails()
    {
        if (!_uiReady) return;

        var folder = SaveFolderBox.Text.Trim();
        ThumbFolderText.Text = string.IsNullOrWhiteSpace(folder) ? "未设置保存文件夹" : folder;
        ThumbGrid.Items.Clear();

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png")
                .OrderByDescending(f => { try { return File.GetLastWriteTime(f); } catch { return DateTime.MinValue; } })
                .Take(200)
                .ToList();
        }
        catch (Exception ex)
        {
            SetStatus("读取图片列表失败：" + ex.Message);
            return;
        }

        ThumbCountText.Text = files.Count == 0 ? "文件夹里没有图片" : $"共 {files.Count} 张（显示最近 {Math.Min(files.Count, 200)} 张）";

        foreach (var path in files)
        {
            var panel = new StackPanel { Spacing = 4, Width = 164, Padding = new Thickness(2) };

            var image = new Image
            {
                Width = 160,
                Height = 110,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                Source = new BitmapImage { DecodePixelWidth = 200, UriSource = new Uri(path) }
            };

            var name = new TextBlock
            {
                Text = Path.GetFileName(path),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.8
            };

            var delete = new Button
            {
                Content = "删除",
                FontSize = 11,
                Padding = new Thickness(10, 2, 10, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var captured = path;
            delete.Click += (_, _) => _ = DeleteImageAsync(captured);

            panel.Children.Add(image);
            panel.Children.Add(name);
            panel.Children.Add(delete);
            ThumbGrid.Items.Add(panel);
        }
    }

    private async Task DeleteImageAsync(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);

            _settings.RecentImages.RemoveAll(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            SettingsService.Save(_settings);
            RefreshHistory();
            RefreshThumbnails();
            SetStatus("已删除：" + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            SetStatus("删除失败：" + Path.GetFileName(path) + " - " + ex.Message);
        }
        await Task.CompletedTask;
    }

    // ---------------- 托盘 / 窗口 ----------------

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        args.Cancel = true;
        ShowWindow(_hwnd, SW_HIDE);   // 收进托盘，继续轮换
    }

    private void ShowMainWindow()
    {
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        _tray?.Dispose();
        Application.Current.Exit();
    }

    // ---------------- 最近使用 ----------------

    private void RefreshHistory()
    {
        HistoryList.Items.Clear();
        foreach (var path in _settings.RecentImages.Take(20))
            HistoryList.Items.Add(path);
    }

    // ---------------- 轮换核心 ----------------

    private void StartRotation()
    {
        if (double.IsNaN(RotateMinutes.Value)) RotateMinutes.Value = _settings.RotateMinutes;
        _rotating = true;
        ToggleTimerBtn.Content = "停止自动轮换";
        _nextChange = DateTime.Now.AddMinutes(Math.Max(1, _settings.RotateMinutes));
        _tickTimer.Start();
        UpdateNextChangeText();
        SetStatus("自动轮换已启动。");
    }

    private void StopRotation()
    {
        _rotating = false;
        _tickTimer.Stop();
        ToggleTimerBtn.Content = "开始自动轮换";
        NextChangeText.Text = "自动轮换：已停止";
    }

    private void OnTick()
    {
        UpdateNextChangeText();
        if (_rotating && !_busy && DateTime.Now >= _nextChange)
        {
            var _ = RotateOnceAsync(reschedule: true);
        }
    }

    private void UpdateNextChangeText()
    {
        if (!_rotating) return;
        var remain = _nextChange - DateTime.Now;
        NextChangeText.Text = remain > TimeSpan.Zero
            ? $"自动轮换：{(int)remain.TotalHours:00}:{remain.Minutes:00}:{remain.Seconds:00} 后更换"
            : "正在更换…";
    }

    /// <summary>从所有启用来源轮换一次壁纸；reschedule=true 时按间隔安排下一次。</summary>
    private async Task RotateOnceAsync(bool reschedule = false)
    {
        if (_busy) return;
        _busy = true;
        RotateNowBtn.IsEnabled = false;

        try
        {
            var s = ReadSettingsFromUi();
            string? result = null;

            // 来源顺序：固定优先级 or 随机
            var sources = new List<string>();
            var usedSource = "";
            if (s.UseReddit) sources.Add("reddit");
            if (s.UseBing) sources.Add("bing");
            if (s.UseLocal) sources.Add("local");

            if (!s.UseSourcePriority)
            {
                for (int i = sources.Count - 1; i > 0; i--)
                {
                    int j = Random.Shared.Next(i + 1);
                    (sources[i], sources[j]) = (sources[j], sources[i]);
                }
            }

            var errors = new List<string>();
            string? note = null;
            var oauthIncomplete = s.UseReddit && s.UseOAuth && string.IsNullOrWhiteSpace(s.ClientId) &&
                                  string.IsNullOrWhiteSpace(s.RedditSessionCookie);
            if (oauthIncomplete)
                errors.Add("OAuth: 已勾选但缺少 Client ID——用户名密码模式同样需要应用 ID，已自动回退 RSS");
            if (s.UseReddit && string.IsNullOrWhiteSpace(s.Subreddits))
                errors.Add("Reddit: 社区列表里没有勾选「使用」的社区");
            foreach (var source in sources)
            {
                try
                {
                    switch (source)
                    {
                        case "reddit":
                            if (string.IsNullOrWhiteSpace(s.SaveFolder))
                            {
                                errors.Add("Reddit: 未设置「图片保存文件夹」");
                                break;
                            }
                            usedSource = source;
                            // 配了 Devvit 订阅源就先走它（Reddit 侧代取数据，有得分/NSFW 过滤）
                            if (!string.IsNullOrWhiteSpace(s.DevvitFeedUrl))
                            {
                                var d = await _devvit.TryDownloadWallpaperAsync(s, Path.Combine(s.SaveFolder, "reddit"));
                                result = d.Path;
                                if (result is null) errors.Add("Devvit: " + d.Error);
                                else note = d.Note;
                            }
                            if (result is null)
                            {
                                var r = await _reddit.TryDownloadWallpaperAsync(s, Path.Combine(s.SaveFolder, "reddit"));
                                result = r.Path;
                                if (result is null) errors.Add("Reddit: " + r.Error);
                                else note = r.Note;
                            }
                            break;
                        case "bing":
                            if (string.IsNullOrWhiteSpace(s.SaveFolder))
                            {
                                errors.Add("Bing: 未设置「图片保存文件夹」");
                                break;
                            }
                            usedSource = source;
                            var b = await _bing.TryDownloadWallpaperAsync(s, Path.Combine(s.SaveFolder, "bing"));
                            result = b.Path;
                            if (result is null) errors.Add("Bing: " + b.Error);
                            break;
                        case "local":
                            usedSource = source;
                            result = LocalImageService.GetRandomImage(s.LocalFolder, s.WallpaperResolution, s.RecentImages);
                            if (result is null)
                                errors.Add("本地: 文件夹无效、没有图片，或没有符合分辨率的图片");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{CurrentSourceName(source)}: {ex.Message}");
                }
                if (result is not null) break;
            }

            if (result is not null)
            {
                var ok = WallpaperService.SetWallpaper(result, s.WallpaperStyle);
                if (ok)
                {
                    // 记录最近用过的图，避免连续重复
                    s.RecentImages.RemoveAll(p =>
                        string.Equals(p, result, StringComparison.OrdinalIgnoreCase));
                    s.RecentImages.Insert(0, result);
                    if (s.RecentImages.Count > 50) s.RecentImages.RemoveRange(50, s.RecentImages.Count - 50);
                    SettingsService.Save(s);
                    RefreshHistory();
                    RefreshThumbnails();
                }
                SetStatus((ok
                    ? $"壁纸已更换：{Path.GetFileName(result)}（来源：{CurrentSourceName(usedSource)}）"
                    : $"设置壁纸失败：{result}") +
                    (note is null ? "" : "；" + note) +
                    (oauthIncomplete ? "；注意：OAuth 缺少 Client ID，本次未使用账号密码" : ""));
                CurrentWallpaperText.Text = result;
            }
            else
            {
                SetStatus("未能获取到图片。各来源情况：" + Environment.NewLine +
                          (errors.Count > 0 ? string.Join(Environment.NewLine, errors) : "未勾选任何来源"));
            }
        }
        catch (Exception ex)
        {
            SetStatus("更换壁纸时出错：" + ex.Message);
        }
        finally
        {
            _busy = false;
            RotateNowBtn.IsEnabled = true;
            if (reschedule && _rotating)
            {
                _nextChange = DateTime.Now.AddMinutes(Math.Max(1, _settings.RotateMinutes));
                UpdateNextChangeText();
            }
        }
    }

    private static string CurrentSourceName(string source) => source switch
    {
        "reddit" => "Reddit",
        "bing" => "Bing",
        _ => "本地文件夹"
    };

    private void SetStatus(string text) => StatusText.Text = text;
}
