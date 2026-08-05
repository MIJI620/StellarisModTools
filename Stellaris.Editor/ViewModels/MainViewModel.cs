// 文件: Stellaris.Editor/ViewModels/MainViewModel.cs
// 主窗口 ViewModel：导航项 + 导航驱动的页面容器（规范 5.1）。

using System.Collections.ObjectModel;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Stellaris.Editor;

/// <summary>导航项：标题（可本地化刷新）+ 对应功能页。</summary>
public sealed class NavItem : ObservableObject
{
    public required string TitleKey { get; init; }
    public required UserControl Page { get; init; }

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}

/// <summary>主窗口 ViewModel：切换导航项 → 内容区整体切换页面。</summary>
public sealed class MainViewModel : ObservableObject
{
    public ObservableCollection<NavItem> NavItems { get; } = new();

    private NavItem? _selected;
    public NavItem? Selected
    {
        get => _selected;
        set
        {
            SetProperty(ref _selected, value);
            OnPropertyChanged(nameof(CurrentPage));
        }
    }

    public UserControl? CurrentPage => _selected?.Page;

    /// <summary>语言切换后刷新全部导航标题（页面内部文本刷新由各页自行处理）。</summary>
    public void RefreshTitles(EngineServices services)
    {
        foreach (var item in NavItems)
            item.Title = services.Localisation.Get(item.TitleKey);
    }
}
