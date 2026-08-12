// 文件: Stellaris.Editor/Pages/MapIndexPage.cs
// 地图壳页（用户 2026-08）：**选项卡只包"列表 + 搜索输入框 + 🔍"那一小块**，嵌在页面右编辑区原列表位置
// （右上角——不是悬浮窗）：宽度随右编辑区拖动（页面自身 GridSplitter）、高度随列表区拖动（页面自身分隔条）；
// 三页实例叠放（预览+参数+按钮 = 原本内容，选择性显示）；切换显示页时选项卡随页面移动（嵌入该页右编辑区 Row0）。
// 三页代码零改动（列表控件由壳层移入选项卡；x:Name 字段同程序集可访问）。

using System.Windows;
using System.Windows.Controls;

namespace Stellaris.Editor.Pages;

public sealed class MapIndexPage : UserControl
{
    private readonly DynamicMapPage _dynamic;
    private readonly StaticMapPage _static;
    private readonly GalaxyStylePage _style;
    private readonly Grid _pageHost;
    private TabControl _tabs = null!;
    private TabItem _mapTab = null!, _styleTab = null!;

    public MapIndexPage(EngineServices services)
    {
        _dynamic = new DynamicMapPage(services);
        _static = new StaticMapPage(services);
        _style = new GalaxyStylePage(services);

        // 列表控件移入选项卡（壳层重组）；静态页列表不单独出现（一个混排列表——用户 2026-08）
        var mapListPanel = BuildListPanel(_dynamic.MapFilterBox, _dynamic.MapFilterSearchButton, _dynamic.MapList, _dynamic.InsertIndicator);
        CollapseControls(_static.MapFilterBox, _static.MapFilterSearchButton, _static.StaticMapList, _static.InsertIndicator);
        var styleListPanel = BuildListPanel(_style.StyleFilterBox, _style.StyleFilterSearchButton, _style.StyleList, _style.InsertIndicator);

        // 三页实例叠放（预览+参数+按钮 = 原本内容；同时只显示一个——选择性显示）
        _pageHost = new Grid();
        _pageHost.Children.Add(_dynamic);
        _pageHost.Children.Add(_static);
        _pageHost.Children.Add(_style);
        _static.Visibility = Visibility.Collapsed;
        _style.Visibility = Visibility.Collapsed;
        _dynamic.StaticMapRequested += (_, name) =>
        {
            _dynamic.Visibility = Visibility.Collapsed;
            _static.Visibility = Visibility.Visible;
            _static.SetMap(name);
            MoveTabs(_static);   // 选项卡随显示页移动（嵌入其右编辑区 Row0）
        };
        _static.DynamicMapRequested += (_, name) =>
        {
            _static.Visibility = Visibility.Collapsed;
            _dynamic.Visibility = Visibility.Visible;
            _dynamic.SetMap(name);
            MoveTabs(_dynamic);
        };

        // 选项卡（内容 = 列表+搜索那一小块）
        _tabs = new TabControl();
        _mapTab = new TabItem { Header = services.Localisation.Get("nav.map"), Content = mapListPanel };
        _styleTab = new TabItem { Header = services.Localisation.Get("nav.style"), Content = styleListPanel };
        _tabs.Items.Add(_mapTab);
        _tabs.Items.Add(_styleTab);
        _tabs.SelectionChanged += (_, _) =>
        {
            bool map = _tabs.SelectedItem == _mapTab;
            if (map)
            {
                // 恢复显示动态/静态（保持当前选择）
                _style.Visibility = Visibility.Collapsed;
                if (_static.Visibility == Visibility.Visible)
                    MoveTabs(_static);
                else
                {
                    _dynamic.Visibility = Visibility.Visible;
                    MoveTabs(_dynamic);
                }
            }
            else
            {
                _dynamic.Visibility = Visibility.Collapsed;
                _static.Visibility = Visibility.Collapsed;
                _style.Visibility = Visibility.Visible;
                MoveTabs(_style);
            }
        };

        Content = _pageHost;
        _tabs.SelectedIndex = 0;   // 默认"地图"（MoveTabs 在 SelectionChanged 触发）
        AttachSizeSync();   // 横向/竖向尺寸调整三页通用（用户 2026-08：拖一页分隔条，其他页同步）
    }

    /// <summary>三页各自的列宽（右编辑区）/ 行高（列表区）调整**通用**：任意页拖分隔条，把该页尺寸同步到其余两页。</summary>
    private void AttachSizeSync()
    {
        foreach (var page in new FrameworkElement[] { _dynamic, _static, _style })
            foreach (var splitter in FindSplitters(page))
                splitter.DragCompleted += (_, _) => SyncSizesFrom(page);
    }

    private static IEnumerable<GridSplitter> FindSplitters(FrameworkElement page)
    {
        var list = new List<GridSplitter>();
        if (page is UserControl uc && uc.Content is Grid root)
        {
            foreach (var child in root.Children)
                if (child is GridSplitter gs)
                    list.Add(gs);
            if (root.Children.Count >= 3 && root.Children[2] is Border edit
                && edit.Child is DockPanel dp && dp.Children.Count > 0 && dp.Children[^1] is Grid main)
                foreach (var child in main.Children)
                    if (child is GridSplitter gs2)
                        list.Add(gs2);
        }
        return list;
    }

    /// <summary>以触发页为准，把右编辑区列宽 + 列表区行高同步到三页（含自身，幂等）。</summary>
    private void SyncSizesFrom(FrameworkElement from)
    {
        SyncGrid(from, _dynamic);
        SyncGrid(from, _static);
        SyncGrid(from, _style);
    }

    private static void SyncGrid(FrameworkElement from, FrameworkElement to)
    {
        if (from is not UserControl f || f.Content is not Grid fr
            || to is not UserControl t || t.Content is not Grid tr)
            return;
        // 横向：右编辑区列宽（列 2）
        if (fr.ColumnDefinitions.Count >= 3 && tr.ColumnDefinitions.Count >= 3)
            tr.ColumnDefinitions[2].Width = fr.ColumnDefinitions[2].Width;
        // 竖向：列表区行高——用**实际渲染像素高度**（拖动的是 Star 行，Height 可能仍是 Star 比例，
        // 同步 Star 等于没同步——用户 2026-08：纵向拖动不同步）
        if (fr.Children.Count >= 3 && fr.Children[2] is Border fb && fb.Child is DockPanel fdp
            && fdp.Children.Count > 0 && fdp.Children[^1] is Grid fg
            && tr.Children.Count >= 3 && tr.Children[2] is Border tb && tb.Child is DockPanel tdp
            && tdp.Children.Count > 0 && tdp.Children[^1] is Grid tg)
        {
            if (fg.RowDefinitions.Count > 0 && tg.RowDefinitions.Count > 0)
            {
                double h = fg.RowDefinitions[0].ActualHeight;
                if (h > 0)
                    tg.RowDefinitions[0].Height = new GridLength(h);
            }
        }
    }

    /// <summary>把页面列表控件移入壳层选项卡容器（搜索行 + 列表 + 插入线），并保持事件（页面内已订阅）。</summary>
    private static Grid BuildListPanel(TextBox filter, Button search, ListBox list, Border insert)
    {
        RemoveFromParent(filter);
        RemoveFromParent(search);
        RemoveFromParent(list);
        RemoveFromParent(insert);
        var grid = new Grid { Margin = new Thickness(4) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(filter);
        Grid.SetColumn(search, 1);
        row.Children.Add(search);
        Grid.SetRow(row, 0);
        grid.Children.Add(row);
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        Grid.SetRow(insert, 1);
        insert.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(insert);
        return grid;
    }

    /// <summary>把选项卡嵌入页面右编辑区原列表位置（Row0）——不悬浮；宽高随页面自身分隔条拖动调整。</summary>
    private void MoveTabs(FrameworkElement page)
    {
        // 先从当前父容器移除
        if (_tabs.Parent is Panel old)
            old.Children.Remove(_tabs);
        var editGrid = FindEditGrid(page);
        if (editGrid == null)
            return;
        Grid.SetRow(_tabs, 0);
        editGrid.Children.Add(_tabs);
    }

    /// <summary>定位页面右编辑区主 Grid（结构同构：根 Grid 列 2 Border → DockPanel → 最后一个子 = 主 Grid）。</summary>
    private static Grid? FindEditGrid(FrameworkElement page)
    {
        if (page is UserControl uc && uc.Content is Grid root
            && root.Children.Count >= 3
            && root.Children[2] is Border edit
            && edit.Child is DockPanel dp
            && dp.Children.Count > 0
            && dp.Children[^1] is Grid main)
            return main;
        return null;
    }

    private static void RemoveFromParent(FrameworkElement el)
        => (el.Parent as Panel)?.Children.Remove(el);

    private static void CollapseControls(params FrameworkElement[] els)
    {
        foreach (var el in els)
            el.Visibility = Visibility.Collapsed;
    }

    /// <summary>切到本页时刷新当前显示的页（原 MainWindow 按 Selected 刷新动态/静态页）。</summary>
    public void Refresh()
    {
        if (_style.Visibility == Visibility.Visible)
            return;   // 星系样式页自行管理
        if (_static.Visibility == Visibility.Visible)
            _static.Refresh();
        else
            _dynamic.Refresh();
    }
}
