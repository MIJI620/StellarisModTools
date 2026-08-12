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
        // 导航切到地图壳页时刷新当前显示的页（星系样式参数更新 → 理论上限同步；原动态/静态页各自刷新）
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Selected" && ReferenceEquals(_viewModel.CurrentPage, _mapIndexPage))
                _mapIndexPage?.Refresh();
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
        // 最上方：法令/决议（可视化编辑器——本期不落盘）
        // 综合（法令/决议/静态加成/战略资源 4 合 1——用户 2026-08 改名，nav.edict 键也改避免误导）
        _viewModel.NavItems.Add(new NavItem
        {
            TitleKey = "nav.comprehensive",
            Title = loc.Get("nav.comprehensive"),
            Page = new EdictDecisionPage(_services)
        });
        // 战略资源已移入"综合"页（用户 2026-08）——左侧导航项删除
        _viewModel.NavItems.Add(new NavItem
        {
            TitleKey = "nav.tech",
            Title = loc.Get("nav.tech"),
            Page = new TechnologyGraphPage(_services)
        });
        // 星系样式归入"地图"导航项（用户 2026-08：叠放模式，不占左侧导航）
        _mapIndexPage = new MapIndexPage(_services);
        _viewModel.NavItems.Add(new NavItem { TitleKey = "nav.map", Title = loc.Get("nav.map"), Page = _mapIndexPage });
        // 目录索引（用户 2026-08）：语言字典 + 加成字典合并为一个页面（内部 2 选项卡：语言 / 加成）
        _viewModel.NavItems.Add(new NavItem
        {
            TitleKey = "nav.dictionary_index",
            Title = loc.Get("nav.dictionary_index"),
            Page = new DictionaryIndexPage(_services)
        });
        _viewModel.NavItems.Add(new NavItem { TitleKey = "nav.settings", Title = loc.Get("nav.settings"), Page = new SettingsPage(_services) });
    }

    private MapIndexPage? _mapIndexPage;

    /// <summary>应用偏好中的界面字体与字号（设置页切换后亦调用）。</summary>
    public void ApplyUserFont()
    {
        try
        {
            FontFamily = new System.Windows.Media.FontFamily(_services.Preferences.FontFamily);
            FontSize = _services.Preferences.FontSize;
            // 全局统一字号：重建隐式 TextBox 样式（所有输入框立即跟随）
            App.ApplyFontStyle(_services.Preferences.FontSize);
        }
        catch
        {
            // 无效字体名时忽略，保持默认
        }
    }
}
