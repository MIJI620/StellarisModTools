// 文件: Stellaris.Editor/MainWindow.xaml.cs
// 主窗口：左导航 + 右侧导航驱动的页面容器（规范第五章）。
// 窗口标题随界面语言本地化（app.title）；语言切换入口在设置页（配好后点"重载"刷新）。

using System.Windows;
using Stellaris.Editor.Pages;

namespace Stellaris.Editor;

public partial class MainWindow : Window
{
    private readonly EngineServices _services;
    private readonly MainViewModel _viewModel;

    public MainWindow(EngineServices services)
    {
        _services = services;
        InitializeComponent();

        // 窗口标题随语言本地化
        Title = services.Localisation.Get("app.title");

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        BuildNavItems();
        // 导航切到动态地图页时刷新（星系样式参数更新 → 理论上限同步）
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Selected" && ReferenceEquals(_viewModel.CurrentPage, _dynamicMapPage))
                _dynamicMapPage?.Refresh();
            else if (e.PropertyName == "Selected" && ReferenceEquals(_viewModel.CurrentPage, _staticMapPage))
                _staticMapPage?.Refresh();
        };
        _viewModel.Selected = _viewModel.NavItems.Count > 0 ? _viewModel.NavItems[0] : null;

        // 应用偏好字体/字号
        ApplyUserFont();

        // 恢复上次窗口状态（全屏/尺寸）
        if (_services.Preferences.WindowWidth > 0 && _services.Preferences.WindowHeight > 0)
        {
            Width = _services.Preferences.WindowWidth;
            Height = _services.Preferences.WindowHeight;
        }
        if (_services.Preferences.Maximized)
            WindowState = WindowState.Maximized;

        Closed += OnWindowClosed;
    }

    /// <summary>关闭时记录窗口状态（是否全屏 + 非全屏尺寸），供下次启动恢复。</summary>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        var prefs = _services.Preferences;
        prefs.Maximized = WindowState == WindowState.Maximized;
        if (prefs.Maximized && RestoreBounds.Width > 0 && RestoreBounds.Height > 0)
        {
            prefs.WindowWidth = (int)RestoreBounds.Width;
            prefs.WindowHeight = (int)RestoreBounds.Height;
        }
        else
        {
            prefs.WindowWidth = (int)ActualWidth;
            prefs.WindowHeight = (int)ActualHeight;
        }
        prefs.Save();
    }

    /// <summary>
    /// 界面语言切换后刷新整个界面（设置页"重载"按钮调用）：
    /// 更新窗口标题、重建导航与当前页面（保持选中项）。
    /// </summary>
    public void RefreshUIAfterLanguageChange()
    {
        Title = _services.Localisation.Get("app.title");

        int selIndex = _viewModel.Selected != null ? _viewModel.NavItems.IndexOf(_viewModel.Selected) : 0;
        _viewModel.NavItems.Clear();
        BuildNavItems();
        _viewModel.Selected = selIndex >= 0 && selIndex < _viewModel.NavItems.Count
            ? _viewModel.NavItems[selIndex]
            : (_viewModel.NavItems.Count > 0 ? _viewModel.NavItems[0] : null);
    }

    private void BuildNavItems()
    {
        var loc = _services.Localisation;
        _viewModel.NavItems.Add(new NavItem
        {
            TitleKey = "nav.style",
            Title = loc.Get("nav.style"),
            Page = new GalaxyStylePage(_services)
        });
        // 动态地图页：选中静态地图时切换到静态地图页面（输出接口）
        _dynamicMapPage = new DynamicMapPage(_services);
        _dynamicMapPage.StaticMapRequested += OnStaticMapRequested;
        _viewModel.NavItems.Add(new NavItem { TitleKey = "nav.dynamic", Title = loc.Get("nav.dynamic"), Page = _dynamicMapPage });
        _staticMapPage = new StaticMapPage(_services);
        _staticMapPage.DynamicMapRequested += OnDynamicMapRequested;
        _viewModel.NavItems.Add(new NavItem { TitleKey = "nav.static", Title = loc.Get("nav.static"), Page = _staticMapPage });
        _viewModel.NavItems.Add(new NavItem
        {
            TitleKey = "nav.language_dictionary",
            Title = loc.Get("nav.language_dictionary"),
            Page = new LanguageDictionaryPage(_services)
        });
        _viewModel.NavItems.Add(new NavItem { TitleKey = "nav.settings", Title = loc.Get("nav.settings"), Page = new SettingsPage(_services) });
    }

    private DynamicMapPage? _dynamicMapPage;
    private StaticMapPage? _staticMapPage;

    /// <summary>动态地图页选中静态地图 → 切换到静态地图页面并传入地图名。</summary>
    private void OnStaticMapRequested(object? sender, string mapName)
    {
        var staticNav = _viewModel.NavItems.FirstOrDefault(n => n.TitleKey == "nav.static");
        if (staticNav != null)
        {
            _viewModel.Selected = staticNav;
            _staticMapPage?.SetMap(mapName);
        }
    }

    /// <summary>静态地图页选中动态地图 → 切回动态地图页并选中该地图（双向切换）。</summary>
    private void OnDynamicMapRequested(object? sender, string mapName)
    {
        var dynamicNav = _viewModel.NavItems.FirstOrDefault(n => n.TitleKey == "nav.dynamic");
        if (dynamicNav != null)
        {
            _viewModel.Selected = dynamicNav;
            _dynamicMapPage?.SetMap(mapName);
        }
    }

    /// <summary>应用偏好中的界面字体与字号（设置页切换后亦调用）。</summary>
    public void ApplyUserFont()
    {
        try
        {
            FontFamily = new System.Windows.Media.FontFamily(_services.Preferences.FontFamily);
            FontSize = _services.Preferences.FontSize;
        }
        catch
        {
            // 无效字体名时忽略，保持默认
        }
    }
}
