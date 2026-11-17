using Microsoft.UI.Xaml;

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
        _window = new MainWindow();
        _window.Activate();

        // 启动即最小化：必须在 Activate 之后隐藏，否则应用可能直接退出
        if (MainWindow.StartHidden) ((MainWindow)_window).HideForStartup();
    }
}
