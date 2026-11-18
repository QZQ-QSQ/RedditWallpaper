using Microsoft.UI.Xaml;
using RedditWallpaper.Services;

namespace RedditWallpaper;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 原生右键菜单/托盘菜单跟随系统深色模式（必须在创建窗口前）
        DarkMode.EnableForSystemTheme();

        _window = new MainWindow();
        _window.Activate();

        // 启动即最小化：必须在 Activate 之后隐藏，否则应用可能直接退出
        if (MainWindow.StartHidden) ((MainWindow)_window).HideForStartup();
    }
}
