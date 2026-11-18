using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    // ---------- 轮换预览队列 ----------
    private readonly List<RedditService.RedditImage> _queue = new();
    private string? _queueSignature;
    private string? _prevPreviewPath;
    private string? _nextName;
    private bool _previewBusy;

    /// <summary>供 App.OnLaunched 判断是否需要"启动即隐藏"</summary>
    public static bool StartHidden { get; private set; }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint pt);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _settings = SettingsService.Load();

        // 原生菜单/窗口主题跟随系统深色
        if (Content is FrameworkElement themeRoot)
        {
            void ApplyTheme()
            {
                var dark = themeRoot.ActualTheme == ElementTheme.Dark;
                DarkMode.ApplyWindowTheme(_hwnd, dark);
                DarkMode.RefreshMenuThemes();
            }

            ApplyTheme();
            themeRoot.Loaded += (_, _) => ApplyTheme();
            themeRoot.ActualThemeChanged += (_, _) => ApplyTheme();
        }

        LoadSettingsToUi();
        RefreshHistory();
        RefreshDeletedHint();
        _uiReady = true;
        _ = RefreshPreviewAsync();

        // 「上一张」是图片库里的真实文件：右键也给 Windows 原生菜单
        PrevThumbFrame.ContextRequested += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(_prevPreviewPath) || !File.Exists(_prevPreviewPath)) return;
            e.Handled = true;
            e.TryGetPosition(PrevThumbFrame, out var pos);
            ShowImageShellMenu(_prevPreviewPath, PrevThumbFrame, pos);
        };
        AutomationProperties.SetHelpText(PrevThumbFrame, "右键打开 Windows 菜单");

        // 图片库缩略图：右键或键盘 Shift+F10 都走同一个 Windows 菜单
        // handledEventsToo: GridView 可能先处理右键，这里仍要收到
        ThumbGrid.AddHandler(UIElement.ContextRequestedEvent,
            new Windows.Foundation.TypedEventHandler<UIElement, ContextRequestedEventArgs>(ThumbGrid_ContextRequested),
            true);

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => OnTick();

        Title = "Reddit 壁纸轮换";

        // 按官方指引：AppWindow.Resize 使用物理像素，需乘以 DPI 缩放
        var dpiHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpiScale = GetDpiForWindow(dpiHwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(1440 * dpiScale), (int)Math.Round(880 * dpiScale)));

        // 双栏布局的最小尺寸：左栏 500 + 右栏 520 + 分隔条/内边距，低于此值会裁切内容
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(1060 * dpiScale);
            presenter.PreferredMinimumHeight = (int)Math.Round(640 * dpiScale);
        }

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

        // 启动时不自动删图：先提示，让用户确认/调整上限后再点「保存设置」
        if (_settings.MaxLibraryImages > 0 && _thumbTotal > _settings.MaxLibraryImages)
        {
            SetStatus($"图片库 {_thumbTotal} 张，超过上限 {_settings.MaxLibraryImages} 张；" +
                      $"点「保存设置」会清理最旧的 {_thumbTotal - _settings.MaxLibraryImages} 张（回收站可恢复）。");
        }
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
        LibraryLimitBox.Value = _settings.MaxLibraryImages;
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

    private static string SelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

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

        _settings.RedditSort = SelectedTag(SortCombo, "hot");
        _settings.RedditTimeRange = SelectedTag(RangeCombo, "week");
        _settings.RedditMinScore = Math.Max(0,
            double.IsNaN(MinScoreBox.Value) ? 100 : (int)MinScoreBox.Value);
        _settings.SkipNsfw = SkipNsfwToggle.IsOn;
        _settings.SaveFolder = SaveFolderBox.Text.Trim();
        _settings.MaxLibraryImages = double.IsNaN(LibraryLimitBox.Value)
            ? 100
            : (int)Math.Clamp(LibraryLimitBox.Value, 0, 10000);
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
        _settings.WallpaperStyle = SelectedTag(StyleCombo, "fill") is { Length: > 0 } t ? t : "fill";
        _settings.WallpaperResolution = SelectedTag(ResolutionCombo, "any") is { Length: > 0 } r ? r : "any";
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

        // 上限可能刚被改小：保存时立即按总张数清理，最旧的进回收站
        var pruned = EnforceLibraryLimit(s, showStatus: false);

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RedditWallpaper", "settings.json");
        SetStatus($"设置已保存：{path}" +
                  (pruned > 0 ? $"；图片库超过上限 {s.MaxLibraryImages} 张，已清理最旧的 {pruned} 张（回收站可恢复）" : ""));
        RefreshThumbnails();
        _ = RefreshPreviewAsync();
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
            var newLeft = Math.Clamp(_leftStartWidth + dx, 340, Math.Max(340, total - 520));
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

    private int _thumbTotal;
    private int _thumbShown;

    private void UpdateThumbCountText()
    {
        ThumbCountText.Text = _thumbShown == 0
            ? "文件夹里没有图片"
            : (_thumbTotal > 200 ? $"共 {_thumbTotal} 张（显示最近 200 张）" : $"共 {_thumbTotal} 张");
        UpdateEmptyLibrary(_thumbShown);
    }

    /// <summary>图片库空状态：图标 + 说明，避免大片空白。</summary>
    private void UpdateEmptyLibrary(int count)
    {
        EmptyLibrary.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count > 0) return;
        EmptyLibraryText.Text = string.IsNullOrWhiteSpace(SaveFolderBox.Text)
            ? "还没有设置「图片保存文件夹」"
            : "这个文件夹里还没有图片" + Environment.NewLine + "点「立即更换壁纸」或「打开文件夹」开始";
    }

    private void RefreshThumbs_Click(object sender, RoutedEventArgs e) => RefreshThumbnails();

    /// <summary>按可用宽度重算列宽：整行铺满，右侧不留大片空白。</summary>
    private void ApplyThumbLayout()
    {
        if (ThumbGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        var width = ThumbGrid.ActualWidth;
        if (width <= 0) return;

        const double itemMargin = 8;     // GridViewItem Margin 4 + 4
        const double minCardWidth = 168;
        var columns = Math.Max(1, (int)Math.Floor((width + itemMargin) / (minCardWidth + itemMargin)));
        panel.ItemWidth = Math.Floor(width / columns) - itemMargin;
    }

    private void ThumbGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyThumbLayout();

    /// <summary>
    /// 图片库总张数硬上限：超过时把最旧的图片移到回收站（当前壁纸除外）。
    /// 返回实际移走的张数；0 表示未做任何删除。
    /// </summary>
    private int EnforceLibraryLimit(AppSettings s, bool showStatus)
    {
        if (!_uiReady || s.MaxLibraryImages <= 0) return 0;

        var folder = s.SaveFolder.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;

        List<string> images;
        try
        {
            images = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(IsImageFile)
                .ToList();
        }
        catch (Exception ex)
        {
            if (showStatus) SetStatus("统计图片库失败：" + ex.Message);
            return 0;
        }

        if (images.Count <= s.MaxLibraryImages) return 0;

        // 保护当前壁纸（刚启动时 CurrentWallpaperText 为空，用最近使用的第一张）
        var current = CurrentWallpaperText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
            current = s.RecentImages.FirstOrDefault(File.Exists);

        string? protect = null;
        if (current is not null && IsUnderFolder(folder, current))
        {
            try { protect = Path.GetFullPath(current); } catch { /* 忽略 */ }
        }

        // 最新优先；时间相同时按文件名排序，保证每次结果一致
        var ordered = images
            .Select(path => (Path: path, Time: SafeWriteTime(path)))
            .OrderByDescending(x => x.Time)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Path)
            .ToList();

        var keep = new HashSet<string>(ordered.Take(s.MaxLibraryImages), StringComparer.OrdinalIgnoreCase);
        if (protect is not null && !keep.Contains(protect))
        {
            // 当前壁纸比保留区旧：替换保留区里最旧的一张，总数仍等于上限
            var oldestKept = ordered.Take(s.MaxLibraryImages).LastOrDefault();
            if (oldestKept is not null) keep.Remove(oldestKept);
            keep.Add(protect);
        }

        var moved = 0;
        foreach (var file in ordered.Where(f => !keep.Contains(f)))
        {
            if (RecycleBin.Delete(file)) moved++;
        }

        if (moved == 0)
        {
            if (showStatus)
                SetStatus($"图片库 {images.Count} 张，超过上限 {s.MaxLibraryImages} 张，但最旧的图片没能移入回收站（可能被占用）。");
            return 0;
        }

        s.RecentImages.RemoveAll(p => !File.Exists(p));
        SettingsService.Save(s);
        RefreshThumbnails();
        RefreshHistory();

        if (showStatus)
            SetStatus($"图片库 {images.Count} 张，超过上限 {s.MaxLibraryImages} 张：已把最旧的 {moved} 张移到回收站（可恢复）。");
        return moved;
    }

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png";

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); } catch { return DateTime.MinValue; }
    }

    private static bool IsUnderFolder(string folder, string path)
    {
        try
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void RefreshThumbnails()
    {
        if (!_uiReady) return;

        var folder = SaveFolderBox.Text.Trim();
        ThumbFolderText.Text = string.IsNullOrWhiteSpace(folder) ? "未设置保存文件夹" : folder;
        ThumbGrid.Items.Clear();

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            UpdateEmptyLibrary(0);
            return;
        }

        List<string> files;
        var total = 0;
        try
        {
            var all = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png")
                .OrderByDescending(f => { try { return File.GetLastWriteTime(f); } catch { return DateTime.MinValue; } })
                .ToList();
            total = all.Count;
            files = all.Take(200).ToList();
        }
        catch (Exception ex)
        {
            SetStatus("读取图片列表失败：" + ex.Message);
            return;
        }

        ThumbCountText.Text = files.Count == 0
            ? "文件夹里没有图片"
            : (total > 200 ? $"共 {total} 张（显示最近 200 张）" : $"共 {total} 张");
        _thumbTotal = total;
        _thumbShown = files.Count;

        // 空状态：没图时给出图标 + 说明，而不是一片空白
        UpdateEmptyLibrary(files.Count);

        foreach (var path in files)
        {
            // 自适应卡片：宽度跟随列宽（窗口变宽就多显示几列，不留右侧空白）
            var panel = new StackPanel { Spacing = 4, Height = 200, HorizontalAlignment = HorizontalAlignment.Stretch };

            var frame = new Border
            {
                Height = 112,
                CornerRadius = new CornerRadius(4)
            };
            // 图片按比例裁切填充；圆角用 Clip 跟随实际宽度
            frame.SizeChanged += (_, e) => frame.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };

            // 背景用主题色（取不到就不加，避免 KeyNotFound）
            if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out var brushObj) &&
                brushObj is Microsoft.UI.Xaml.Media.Brush brush)
                frame.Background = brush;

            var image = new Image
            {
                Height = 112,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                Source = new BitmapImage { DecodePixelWidth = 260, UriSource = new Uri(path) }
            };
            frame.Child = image;

            var name = new TextBlock
            {
                Text = Path.GetFileName(path),
                FontSize = 12,
                Height = 34,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.8
            };

            var fileName = Path.GetFileName(path);
            AutomationProperties.SetName(panel, fileName);
            ToolTipService.SetToolTip(frame, fileName);
            ToolTipService.SetToolTip(name, fileName);

            var delete = new Button
            {
                Content = "删除",
                Tag = fileName,
                FontSize = 12,
                Height = 32,
                Padding = new Thickness(8, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var captured = path;
            bool pendingDelete = false;

            // 供右键/键盘菜单定位到具体文件
            panel.Tag = captured;
            AutomationProperties.SetHelpText(panel, "右键或 Shift+F10 打开 Windows 菜单");

            var setBtn = new Button
            {
                Content = "设为壁纸",
                Tag = fileName,
                FontSize = 12,
                Height = 32,
                Padding = new Thickness(8, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            SetCardButtonLabel(delete, "删除");
            SetCardButtonLabel(setBtn, "设为壁纸");

            delete.Click += (_, _) =>
            {
                if (!pendingDelete)
                {
                    // 第一步：卡片进入确认态（不弹窗、不打断其他卡片）
                    pendingDelete = true;
                    panel.Opacity = 0.65;
                    SetCardButtonLabel(delete, "确认删除");
                    SetCardButtonLabel(setBtn, "取消");
                    return;
                }
                // 第二步：确认 → 移到回收站并平滑移除该卡片
                pendingDelete = false;
                _ = DeleteConfirmedAsync(captured, panel, setBtn, delete);
            };

            setBtn.Click += (_, _) =>
            {
                if (pendingDelete)
                {
                    // 取消确认态
                    pendingDelete = false;
                    panel.Opacity = 1;
                    SetCardButtonLabel(delete, "删除");
                    SetCardButtonLabel(setBtn, "设为壁纸");
                    return;
                }
                _ = SetWallpaperFromLibraryAsync(captured);
            };

            var buttons = new Grid { ColumnSpacing = 4, Height = 28 };
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(setBtn, 0);
            Grid.SetColumn(delete, 1);
            buttons.Children.Add(setBtn);
            buttons.Children.Add(delete);

            panel.Children.Add(frame);
            panel.Children.Add(name);
            panel.Children.Add(buttons);
            ThumbGrid.Items.Add(panel);
        }

        // 布局完成后按实际宽度铺满
        DispatcherQueue.TryEnqueue(ApplyThumbLayout);
    }

    /// <summary>从被右键的缩略图元素向上找到 panel.Tag（文件路径）。键盘 Shift+F10 时源是 GridViewItem。</summary>
    private void ThumbGrid_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not DependencyObject src) return;

        string? path = null;
        FrameworkElement? anchor = null;
        for (var d = src; d is not null && !ReferenceEquals(d, ThumbGrid); d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { Tag: string tagged } fe && File.Exists(tagged))
            {
                path = tagged;
                anchor = fe;
                break;
            }

            // 键盘触发时 OriginalSource 是容器，文件路径在容器内容的 Tag 上
            if (d is GridViewItem { Content: FrameworkElement { Tag: string contentTag } } item && File.Exists(contentTag))
            {
                path = contentTag;
                anchor = item;
                break;
            }
        }

        if (path is null || anchor is null) return;
        args.Handled = true;

        var position = new Windows.Foundation.Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2);
        if (args.TryGetPosition(anchor, out var pointer) && (pointer.X != 0 || pointer.Y != 0))
            position = pointer;
        ShowImageShellMenu(path, anchor, position);
    }

    /// <summary>在缩略图的实际位置弹出系统菜单（支持鼠标右键与键盘 Shift+F10）。</summary>
    private void ShowImageShellMenu(string path, UIElement anchor, Windows.Foundation.Point position)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SetStatus("文件已不存在：" + (string.IsNullOrWhiteSpace(path) ? "（未知）" : Path.GetFileName(path)));
            RefreshThumbnails();
            return;
        }

        try
        {
            var root = (UIElement)Content;
            var point = anchor.TransformToVisual(root).TransformPoint(position);
            var scale = GetDpiForWindow(_hwnd) / 96.0;
            var origin = new NativePoint();
            ClientToScreen(_hwnd, ref origin);
            var x = origin.X + (int)Math.Round(point.X * scale);
            var y = origin.Y + (int)Math.Round(point.Y * scale);

            // 不要在 XAML 事件分发过程中直接开原生模态菜单（会重入消息循环），
            // 等这次事件返回后再弹，避免极少数情况下 XAML 内部状态被破坏。
            if (!DispatcherQueue.TryEnqueue(() =>
                {
                    ShellContextMenu.Show(_hwnd, path, x, y);
                    _ = WatchShellMenuResultAsync(path);
                }))
            {
                ShellContextMenu.Show(_hwnd, path, x, y);
                _ = WatchShellMenuResultAsync(path);
            }
            return;
        }
        catch
        {
            // 坐标换算失败就退回鼠标位置
        }

        if (!DispatcherQueue.TryEnqueue(() =>
            {
                ShellContextMenu.Show(_hwnd, path);
                _ = WatchShellMenuResultAsync(path);
            }))
        {
            ShellContextMenu.Show(_hwnd, path);
            _ = WatchShellMenuResultAsync(path);
        }
    }

    /// <summary>
    /// 菜单关闭后检查文件是否被 shell 删除（可能先弹确认框，因此轮询几秒）。
    /// 删除同样写入「不再下载」，保证从 Windows 菜单删掉的图不会自动回来。
    /// </summary>
    private async Task WatchShellMenuResultAsync(string path)
    {
        for (var i = 0; i < 12 && File.Exists(path); i++)
            await Task.Delay(250);

        if (File.Exists(path)) return;

        var name = Path.GetFileName(path);
        _settings.RememberDeletedImage(name);
        SettingsService.Save(_settings);
        RefreshThumbnails();
        RefreshDeletedHint();
        SetStatus($"已删除「{name}」（回收站），以后不会再自动下载。");
        _ = RefreshPreviewAsync();
    }

    /// <summary>把图片库里的某张图设为当前壁纸（并记入最近使用）。</summary>
    private async Task SetWallpaperFromLibraryAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                SetStatus("文件已不存在：" + Path.GetFileName(path));
                RefreshThumbnails();
                return;
            }

            var s = ReadSettingsFromUi();
            var ok = WallpaperService.SetWallpaper(path, s.WallpaperStyle);
            if (!ok)
            {
                SetStatus("设置壁纸失败：" + path);
                return;
            }

            _settings.RecentImages.RemoveAll(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _settings.RecentImages.Insert(0, path);
            if (_settings.RecentImages.Count > 50) _settings.RecentImages.RemoveRange(50, _settings.RecentImages.Count - 50);
            SettingsService.Save(_settings);

            CurrentWallpaperText.Text = path;
            RefreshHistory();
            SetStatus("壁纸已设置：" + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            SetStatus("设置壁纸失败：" + ex.Message);
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 删除流程（卡片内两步确认后调用）：
    /// 释放该卡片对图片的占用 → 移到回收站 → 只移除这一张卡片，不整列重建。
    /// </summary>
    private async Task DeleteConfirmedAsync(string path, StackPanel panel, Button setBtn, Button deleteBtn)
    {
        try
        {
            // 释放该卡片缩略图对文件的占用
            if (panel.Children.OfType<Border>().FirstOrDefault() is Border f) f.Child = null;
            await Task.Delay(150);

            var moved = SafeFile.MoveToRecycleBin(path);
            if (!moved)
            {
                await Task.Delay(400);
                moved = SafeFile.MoveToRecycleBin(path);
            }

            if (moved)
            {
                ThumbGrid.Items.Remove(panel);   // 只移除这一张，其他卡片不动
                _settings.RememberDeletedImage(path);   // 记住：以后不再自动下载这张
                _settings.RecentImages.RemoveAll(p =>
                    string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                SettingsService.Save(_settings);
                RefreshHistory();
                RefreshDeletedHint();
                UpdateThumbCountText();
                SetStatus("已移到回收站：" + Path.GetFileName(path) + "（以后不会再自动下载这张）");
            }
            else
            {
                RestoreCardAfterDelete(panel, setBtn, deleteBtn);
                SetStatus("未能移到回收站（文件可能被占用）：" + Path.GetFileName(path));
            }
        }
        catch (Exception ex)
        {
            RestoreCardAfterDelete(panel, setBtn, deleteBtn);
            SetStatus("删除失败：" + Path.GetFileName(path) + " - " + ex.Message);
        }
        await Task.CompletedTask;
    }

    private static void RestoreCardAfterDelete(StackPanel panel, Button setBtn, Button deleteBtn)
    {
        panel.Opacity = 1;
        SetCardButtonLabel(setBtn, "设为壁纸");
        SetCardButtonLabel(deleteBtn, "删除");
    }

    /// <summary>卡片按钮同时更新可见文字与无障碍名称（读屏会念出文件名）。</summary>
    private static void SetCardButtonLabel(Button button, string text)
    {
        button.Content = text;
        AutomationProperties.SetName(button,
            button.Tag is string file ? $"{text} {file}" : text);
    }

    /// <summary>刷新「不再下载」记录的提示文字。</summary>
    private void RefreshDeletedHint()
    {
        DeletedHintText.Text = _settings.DeletedImageNames.Count == 0
            ? "目前没有「不再下载」的图片。"
            : $"已记录 {_settings.DeletedImageNames.Count} 张删除过的图片，不会再自动下载。";
    }

    /// <summary>清除删除记录（重新允许下载这些图）。</summary>
    private void ClearDeleted_Click(object sender, RoutedEventArgs e)
    {
        var n = _settings.DeletedImageNames.Count;
        if (n == 0)
        {
            SetStatus("「不再下载」记录本来就是空的。");
            return;
        }
        _settings.DeletedImageNames.Clear();
        SettingsService.Save(_settings);
        RefreshDeletedHint();
        SetStatus($"已清除 {n} 条「不再下载」记录，这些图以后可能重新下载。");
    }

    // ---------------- 轮换预览队列 ----------------

    /// <summary>队列的"配置指纹"：影响候选列表的设置一变就重建队列。</summary>
    private static string QueueSignature(AppSettings s) =>
        $"{s.UseReddit}|{s.Subreddits}|{s.RedditSort}|{s.RedditTimeRange}|{s.RedditMinScore}|" +
        $"{s.SkipNsfw}|{s.WallpaperResolution}|{s.UseOAuth}|{s.RedditBaseUrl}";

    private static void SetPreviewThumb(Image image, FontIcon placeholder, string? path)
    {
        var has = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        image.Source = has ? new BitmapImage { DecodePixelWidth = 240, UriSource = new Uri(path!) } : null;
        placeholder.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>按当前舍弃状态刷新按钮文字（Text 同时作为无障碍名称）。</summary>
    private void UpdateDiscardButton(Button button, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            button.IsEnabled = false;
            button.Content = "舍弃";
            AutomationProperties.SetName(button, "舍弃");
            return;
        }

        if (_settings.IsDeletedImageName(fileName))
        {
            button.IsEnabled = false;
            button.Content = "已删除 · 不再下载";
            AutomationProperties.SetName(button, $"已删除，不再下载 {fileName}");
            return;
        }

        button.IsEnabled = true;
        var discarded = _settings.IsDiscardedImageName(fileName);
        button.Content = discarded ? "已舍弃 · 点击恢复" : "舍弃";
        AutomationProperties.SetName(button, $"{(discarded ? "恢复" : "舍弃")} {fileName}");
    }

    private void UpdateRestoreButton()
    {
        var n = _settings.DiscardedImageNames.Count;
        RestoreDiscardBtn.Content = $"已舍弃 {n} 张 · 全部恢复";
        RestoreDiscardBtn.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 刷新「上一张 / 下一张」预览：上一张取当前壁纸，下一张取队列头并提前下载到缓存。
    /// 失败不打扰用户，只把原因写在卡片说明里。
    /// </summary>
    private async Task RefreshPreviewAsync()
    {
        if (_previewBusy) return;
        _previewBusy = true;
        try
        {
            var s = ReadSettingsFromUi();

            // ---------- 上一张 ----------
            var prev = CurrentWallpaperText.Text?.Trim();
            if (string.IsNullOrWhiteSpace(prev) || !File.Exists(prev))
                prev = s.RecentImages.FirstOrDefault(File.Exists);
            _prevPreviewPath = prev;
            SetPreviewThumb(PrevThumb, PrevThumbIcon, prev);
            PrevTitle.Text = string.IsNullOrWhiteSpace(prev) ? "还没有换过壁纸" : Path.GetFileName(prev);
            UpdateDiscardButton(DiscardPrevBtn, string.IsNullOrWhiteSpace(prev) ? null : Path.GetFileName(prev));

            UpdateRestoreButton();

            // ---------- 下一张 ----------
            if (!s.UseReddit)
            {
                _queue.Clear();
                _queueSignature = null;
                _nextName = null;
                SetPreviewThumb(NextThumb, NextThumbIcon, null);
                NextTitle.Text = "未启用 Reddit 来源";
                UpdateDiscardButton(DiscardNextBtn, null);
                PreviewCache.ClearAll();
                return;
            }

            var signature = QueueSignature(s);
            if (!string.Equals(signature, _queueSignature, StringComparison.Ordinal))
            {
                _queueSignature = signature;
                _queue.Clear();
            }

            if (_queue.Count == 0)
            {
                NextTitle.Text = "正在获取候选…";
            NextLoadingRing.IsActive = true;
            NextThumbIcon.Visibility = Visibility.Collapsed;
                var batch = await _reddit.BuildCandidatesAsync(s);
                var redditFolder = string.IsNullOrWhiteSpace(s.SaveFolder) ? "" : SourceFolder(s.SaveFolder, "reddit");
                foreach (var c in batch.Candidates ?? new List<RedditService.RedditImage>())
                {
                    // 删除过 / 舍弃过 / 图片库里已有同名文件 → 不进队列
                    var name = RedditService.FileNameFor(c);
                    if (s.ShouldSkipDownload(name)) continue;
                    if (redditFolder.Length > 0 && File.Exists(Path.Combine(redditFolder, name))) continue;
                    _queue.Add(c);
                }
            }

            if (_queue.Count == 0)
            {
                _nextName = null;
                SetPreviewThumb(NextThumb, NextThumbIcon, null);
                NextTitle.Text = "没有新的候选（都已下载过或已舍弃）";
                UpdateDiscardButton(DiscardNextBtn, null);
                PreviewCache.ClearAll();
                return;
            }

            var next = _queue[0];
            _nextName = RedditService.FileNameFor(next);
            NextTitle.Text = _nextName;
            UpdateDiscardButton(DiscardNextBtn, _nextName);

            PreviewCache.Cleanup(TimeSpan.FromHours(6));
            PreviewCache.CleanupExcept(_nextName);
            var cached = PreviewCache.Find(_nextName);
            if (cached is null)
            {
                NextTitle.Text = _nextName + "（正在预取…）";
                NextLoadingRing.IsActive = true;
                NextThumbIcon.Visibility = Visibility.Collapsed;
                cached = await _reddit.PrefetchAsync(s, next, PreviewCache.CacheDir);
            }

            if (cached is not null && File.Exists(cached))
            {
                // 缓存文件名可能与预估扩展名不同（按 Content-Type 落盘），以实际文件为准
                _nextName = Path.GetFileName(cached);
                NextTitle.Text = _nextName;
                UpdateDiscardButton(DiscardNextBtn, _nextName);
                SetPreviewThumb(NextThumb, NextThumbIcon, cached);
            }
            else
            {
                SetPreviewThumb(NextThumb, NextThumbIcon, null);
                NextTitle.Text = _nextName + "（预取失败，轮换时会再试）";
            }
        }
        catch (Exception ex)
        {
            SetPreviewThumb(NextThumb, NextThumbIcon, null);
            NextTitle.Text = "预览获取失败：" + ex.Message;
        }
        finally
        {
            NextLoadingRing.IsActive = false;
            _previewBusy = false;
        }
    }

    private void DiscardPrev_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_prevPreviewPath)) return;
        var name = Path.GetFileName(_prevPreviewPath);
        var discarded = _settings.ToggleDiscardedImage(name);
        SettingsService.Save(_settings);
        UpdateDiscardButton(DiscardPrevBtn, name);
        UpdateRestoreButton();
        SetStatus(discarded
            ? $"已舍弃「{name}」：以后不会再自动下载，再点一次恢复。"
            : $"已恢复「{name}」：允许再次下载。");
    }

    private void DiscardNext_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_nextName)) return;
        var name = _nextName;
        var discarded = _settings.ToggleDiscardedImage(name);
        SettingsService.Save(_settings);
        UpdateDiscardButton(DiscardNextBtn, name);
        UpdateRestoreButton();
        SetStatus(discarded
            ? $"已舍弃「{name}」：这一张不会被下载，再点一次恢复。"
            : $"已恢复「{name}」：允许下载。");
    }

    private async void RestoreDiscard_Click(object sender, RoutedEventArgs e)
    {
        var n = _settings.DiscardedImageNames.Count;
        if (n == 0) return;
        _settings.DiscardedImageNames.Clear();
        SettingsService.Save(_settings);
        UpdateRestoreButton();
        SetStatus($"已恢复 {n} 张被舍弃的图片。");
        await RefreshPreviewAsync();
    }

    /// <summary>从预览队列取下一张：优先用已预取的缓存，失败再走正常下载；舍弃/删除过的跳过。</summary>
    private async Task<RedditService.DownloadResult> TakeFromQueueAsync(AppSettings s, string saveFolder)
    {
        var lastError = "队列为空";
        while (_queue.Count > 0)
        {
            var img = _queue[0];
            _queue.RemoveAt(0);

            var fileName = RedditService.FileNameFor(img);
            if (s.ShouldSkipDownload(fileName))
            {
                PreviewCache.Remove(PreviewCache.Find(fileName));
                lastError = $"「{fileName}」已舍弃，跳过";
                continue;
            }
            if (File.Exists(Path.Combine(saveFolder, fileName)))
            {
                PreviewCache.Remove(PreviewCache.Find(fileName));
                lastError = $"「{fileName}」图片库里已有，跳过";
                continue;
            }

            var cached = PreviewCache.Find(fileName);
            var path = cached is null ? null : PreviewCache.Commit(cached, saveFolder);
            if (path is null)
            {
                var r = await _reddit.TryDownloadCandidateAsync(s, saveFolder, img);
                if (r.Path is null)
                {
                    lastError = $"{fileName}: {r.Error}";
                    continue;
                }
                return r;
            }

            if (!ImageSize.Matches(path, s.WallpaperResolution))
            {
                SafeFile.DeleteOwned(path);   // 本次程序新建的文件才允许清理
                lastError = $"「{fileName}」不符合最低分辨率 {s.WallpaperResolution}";
                continue;
            }
            return RedditService.DownloadResult.Ok(path);
        }
        return RedditService.DownloadResult.Fail(lastError);
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
        NextThumb.Source = null;
        PrevThumb.Source = null;
        PreviewCache.ClearAll();
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

    /// <summary>来源子目录：保存目录本身已叫 reddit/bing 时不再套一层，避免 reddit\reddit。</summary>
    private static string SourceFolder(string saveFolder, string kind)
    {
        var full = saveFolder.TrimEnd('\\', '/');
        var leaf = Path.GetFileName(full);
        if (string.Equals(leaf, kind, StringComparison.OrdinalIgnoreCase)) return full;
        return Path.Combine(full, kind);
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
                                var d = await _devvit.TryDownloadWallpaperAsync(s, SourceFolder(s.SaveFolder, "reddit"));
                                result = d.Path;
                                if (result is null) errors.Add("Devvit: " + d.Error);
                                else note = d.Note;
                            }
                            if (result is null && _queue.Count > 0 &&
                                string.Equals(QueueSignature(s), _queueSignature, StringComparison.Ordinal))
                            {
                                // 预览队列里的「下一张」优先（已预取就直接落盘）
                                var q = await TakeFromQueueAsync(s, SourceFolder(s.SaveFolder, "reddit"));
                                result = q.Path;
                                if (result is null) errors.Add("Reddit 队列: " + q.Error);
                                else note = "按预览队列顺序";
                            }
                            if (result is null)
                            {
                                var r = await _reddit.TryDownloadWallpaperAsync(s, SourceFolder(s.SaveFolder, "reddit"));
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
                            var b = await _bing.TryDownloadWallpaperAsync(s, SourceFolder(s.SaveFolder, "bing"));
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
                CurrentWallpaperText.Text = result;

                // 图片库总张数上限：当前壁纸已记录，清理时不会把它删掉
                var pruned = ok ? EnforceLibraryLimit(s, showStatus: false) : 0;

                SetStatus((ok
                    ? $"壁纸已更换：{Path.GetFileName(result)}（来源：{CurrentSourceName(usedSource)}）"
                    : $"设置壁纸失败：{result}") +
                    (note is null ? "" : "；" + note) +
                    (oauthIncomplete ? "；注意：OAuth 缺少 Client ID，本次未使用账号密码" : "") +
                    (pruned > 0 ? $"；图片库超过上限 {s.MaxLibraryImages} 张，已清理最旧的 {pruned} 张（回收站可恢复）" : ""));

                // 队列前进一格，重新预取「下一张」
                _ = RefreshPreviewAsync();
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
