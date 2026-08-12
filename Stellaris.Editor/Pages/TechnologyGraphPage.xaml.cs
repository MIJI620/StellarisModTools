// 文件: Stellaris.Editor/Pages/TechnologyGraphPage.xaml.cs
// 科技节点图（**文本标签模式**——WPF 原生控件卡，支持交互：选中/悬停/高亮）。
// ⚠️ 旧"动态生成连线图"模式（BuildConnections/WPF Path 连线）= **失败的试验性产物，已隐藏（2026-08）**，
//    代码仅存档保留不再调用；当前 = 左右尖角框标签（前置左侧、后继右侧）+ 学科色六边形密铺行背景。
// 卡片用 TechCardControl（矢量文字缩放清晰）——**节点构造未改动**。
// 导出整图 PNG 走文本标签模式（TechnologyRenderer.RenderLabel）。
// 交互：横/纵滚动条 + Ctrl+滚轮缩放 + Chrome 中键自动滚动；右键导出整图。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Stellaris.Engine.Technology;

namespace Stellaris.Editor.Pages;

public sealed class TechnologyGraphPage : UserControl
{
    private const double AutoScrollSpeed = 0.08;  // 每 tick 滚动 = 偏移 × 系数（Chrome 中键自动滚动）

    private readonly EngineServices _services;
    private readonly TechnologyEngine _engine;
    private readonly TechnologyRenderer _renderer;   // 导出整图（图片模式保留）
    private readonly ScrollViewer _scroller = new();
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };   // 透明背景参与命中（空白处右键弹菜单）
    private TechLayout _layout = null!;
    private string _lang = "english";                          // 卡片本地化语种（跟随界面语言）
    private double _zoom = 1.0;                                   // Ctrl+滚轮缩放（0.2~4.0）
    private float _unifiedRightZone;                              // 所有卡统一右侧占用（描述宽度一致）
    private readonly Dictionary<TechNode, float> _descHeights = new();   // 卡描述实际高度（WPF Measure）
    private bool _autoScrolling;                                  // Chrome 中键自动滚动模式
    private Point _scrollAnchor;
    private Point _mousePos;
    private readonly DispatcherTimer _autoScrollTimer = new()
    { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Dictionary<string, BitmapSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    // ==================== 点击高亮（选中节点 + 相连线） ====================
    private sealed class TechEdge
    {
        public LayoutTech From = null!;
        public LayoutTech To = null!;
        public System.Windows.Shapes.Path Path = null!;
    }
    private static readonly SolidColorBrush LineNormal = new(Color.FromRgb(0x77, 0x8A, 0xB5));
    private static readonly SolidColorBrush LineHighlight = new(Color.FromRgb(0xFF, 0xD9, 0x00));   // 黄
    private static readonly SolidColorBrush GlowBrush = new(Color.FromRgb(0xFF, 0xD9, 0x00));
    private static readonly SolidColorBrush PreGlowBrush = new(Color.FromRgb(0xE5, 0x39, 0x35));   // 亮红（前置节点外圈/标签边框）
    private static readonly SolidColorBrush KidGlowBrush = new(Color.FromRgb(0x66, 0xBB, 0x6A));   // 浅绿（后继节点外圈/标签边框）
    private readonly List<TechEdge> _edges = new();
    private readonly List<(LayoutTech Node, Border Glow)> _glows = new();
    private string? _selectedKey;   // 当前选中节点 key（null = 无选中）
    private TechCardControl? _contextCard;   // 右键命中的卡片（修改/删除用）
    private Point _contextPos;   // 右键菜单位置（新建弹窗预填行/列用）
    private TextBlock _status = null!;   // 顶部状态文本（共 N 项科技 / 正在计算——RebuildImage 异步刷新）
    private readonly List<(Rect Area, TechNode Target, bool IsPre)> _tagHits = new();   // 标签命中表（点击坐标反查 + 高亮：IsPre=前置标签）
    private readonly List<System.Windows.Shapes.Path> _tagBoxes = new();   // 标签尖角框 Path（与 _tagHits 一一对应；选中时改边框色——边框高亮）
    private ListBox? _resultList;   // 搜索结果下拉列表（多结果时显示）
    private List<TechNode>? _searchMatches;   // 搜索多结果高亮（匹配节点黄圈）
    private static readonly Dictionary<int, ImageBrush> _hexBrushes = new();   // 学科行 → 六边形位图 tile 画刷（缓存，仅 3 张）

    /// <summary>可见科技 = 引擎全部 − 删除登记（删除不改内存，绘制时跳过——用户规则；保存落盘成功后才移除内存）。</summary>
    private IReadOnlyList<TechNode> GetVisibleTechs()
        => _engine.GetAll().Where(t => !_engine.IsRemoved(t.Key)).ToList();

    public TechnologyGraphPage(EngineServices services)
    {
        _services = services;
        _engine = services.TechnologyEngine!;
        _renderer = new TechnologyRenderer(_engine, LoadIconSkia);
        // 卡片名/描述/效果本地化语种 = 界面语言对应 mod 语种
        _lang = UILocalisationManager.MapUiLangToModLang(services.Localisation.CurrentLanguage);
        // 用户设置（user_prefs.json）：科技卡片最小宽/高 + 卡片字号基准
        TechnologyLayout.CardWidth = Math.Max(services.Preferences.TechCardMinWidth, 200);
        TechnologyLayout.CardHeight = Math.Max(services.Preferences.TechCardMinHeight, 80);
        _renderer.FontSizeScale = (float)Math.Max(6, services.Preferences.FontSize);
        _renderer.ShipEngine = services.ShipEngine;   // 导出图解锁行（武器/舰船）与页面一致
        _renderer.UnlockTag = services.Localisation.Get("tech.unlocks") ?? "解锁";   // 兜底：查不到时用默认文本

        // 引擎已在 App 启动时序中同步初始化完成（禁止回调/补扫）

        // 顶部搜索行（**参考法令/资源页搜索**——输入框特性 + 🔍按钮，用户规则）：搜索框(填满) + 🔍 按钮 + 状态文本
        _status = new TextBlock
        {
            Name = "StatusText",
            Margin = new Thickness(8),
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var searchRow = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchBox = new TextBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,   // 输入框特性（参考法令搜索框：自动换行）
            ToolTip = _services.Localisation.Get("tech.search_hint") ?? "输入科技名/Key，回车跳转"
        };
        // 输入框特性：Enter（无 Shift）跳转；Shift+Enter 换行（参考法令/资源页）
        searchBox.KeyDown += (s, e) =>
        {
            if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                return;
            e.Handled = true;
            DoTechSearch();
        };
        Grid.SetColumn(searchBox, 0);
        searchRow.Children.Add(searchBox);
        var searchBtn = new Button
        {
            Content = "🔍",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("SearchButtonStyle")   // 按钮样式参考其他页搜索
        };
        searchBtn.Click += (_, _) => DoTechSearch();
        Grid.SetColumn(searchBtn, 1);
        searchRow.Children.Add(searchBtn);
        // 顶部：searchRow（**Star 填满横向空间**——用户：搜索输入框没自动填满；StackPanel 水平排列会按内容宽）
        // + 状态文本（Auto 右侧）+ 搜索结果下拉列表（行 1，多结果时显示）
        var top = new Grid();
        top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(searchRow, 0);
        Grid.SetColumn(searchRow, 0);
        Grid.SetRow(_status, 0);
        Grid.SetColumn(_status, 1);
        _resultList = new ListBox
        {
            MaxHeight = 220,
            Margin = new Thickness(8, 0, 8, 4),
            Visibility = Visibility.Collapsed   // 多结果时显示
        };
        _resultList.SelectionChanged += (_, _) =>
        {
            if (_resultList.SelectedItem is SearchResultItem it)
            {
                GoToTech(it.Node);   // 用户选择结果 → 跳转
                _resultList.SelectedItem = null;
            }
        };
        Grid.SetRow(_resultList, 1);
        Grid.SetColumnSpan(_resultList, 2);
        top.Children.Add(searchRow);
        top.Children.Add(_status);
        top.Children.Add(_resultList);

        // 搜索（回车 / 🔍 / **停笔 2 秒自动**）：**唯一确定结果 → 自动跳转**；多个结果 → 高亮 + 下拉列表选一个
        Stellaris.Editor.Controls.SearchDebouncer.Attach(searchBox, () => DoTechSearch(auto: true));   // 用户：停 2 秒自动搜索功能
        void DoTechSearch(bool auto = false)
        {
            string q = searchBox.Text.Trim();
            if (q.Length == 0)
            {
                _resultList.Visibility = Visibility.Collapsed;
                _searchMatches = null;
                UpdateHighlight();
                return;
            }
            // 1) 精确匹配（key 完全相等）→ 唯一确定 → 自动跳转
            var exact = _layout.Nodes.FirstOrDefault(n => n.Node.Key.Equals(q, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                GoToTech(exact.Node);
                if (!auto) searchBox.SelectAll();
                return;
            }
            // 2) 模糊匹配（key 包含 / 本地化名包含）
            var matches = _layout.Nodes
                .Where(n => n.Node.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || _engine.LocalisedName(n.Node.Key, _lang).Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                _status.Text = string.Format(_services.Localisation.Get("tech.search_miss") ?? "未找到: {0}", q);
                _resultList.Visibility = Visibility.Collapsed;
                _searchMatches = null;
                UpdateHighlight();
                return;
            }
            if (matches.Count == 1)   // 唯一 → 自动跳转
            {
                GoToTech(matches[0].Node);
                if (!auto) searchBox.SelectAll();
                return;
            }
            // 3) 多个结果 → 全部高亮 + 下拉列表（用户选择跳转或修改搜索词）
            _searchMatches = matches.Select(m => m.Node).ToList();
            _resultList.Items.Clear();
            foreach (var m in matches)
                _resultList.Items.Add(new SearchResultItem(m.Node, _engine.LocalisedName(m.Node.Key, _lang)));
            _resultList.Visibility = Visibility.Visible;
            UpdateHighlight();
        }

        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.IsDeferredScrollingEnabled = true;   // 性能：滚动只移动内容不重绘、松手才渲染（数千控件时显著减卡——用户反馈"很卡"）
        _scroller.Content = _canvas;

        // Chrome 中键自动滚动
        _autoScrollTimer.Tick += (_, _) =>
        {
            if (!_autoScrolling)
                return;
            double dx = (_mousePos.X - _scrollAnchor.X) * AutoScrollSpeed;
            double dy = (_mousePos.Y - _scrollAnchor.Y) * AutoScrollSpeed;
            _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset + dx);
            _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + dy);
        };
        _scroller.PreviewMouseDown += OnCanvasMouseDown;   // Preview 隧道事件：卡片控件不拦截，任何位置中键按下生效
        _scroller.PreviewMouseMove += OnCanvasMouseMove;
        _scroller.PreviewMouseUp += OnCanvasMouseUp;

        // Ctrl+滚轮缩放（LayoutTransform——控件矢量缩放清晰）；**锚点 = 鼠标位置**（用户：鼠标靠左就往左侧缩放）
        _scroller.PreviewMouseWheel += (s, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;
            double factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
            double newZoom = Math.Clamp(_zoom * factor, 0.2, 4.0);
            // 锚点 = 鼠标在视口内的位置（用户规则：缩放跟随鼠标，不是视口中心）
            var mp = e.GetPosition(_scroller);
            double cx = (_scroller.HorizontalOffset + mp.X) / _zoom;
            double cy = (_scroller.VerticalOffset + mp.Y) / _zoom;
            _zoom = newZoom;
            _canvas.LayoutTransform = new ScaleTransform(_zoom, _zoom);
            _scroller.ScrollToHorizontalOffset(cx * _zoom - mp.X);
            _scroller.ScrollToVerticalOffset(cy * _zoom - mp.Y);
            e.Handled = true;
        };

        // 右键菜单：新建 / 修改 / 删除 / 保存 / 导出（任意位置可用；修改/删除需右键在科技卡上）
        var loc = _services.Localisation;
        var newItem = new MenuItem { Header = loc.Get("tech.menu_new") };
        newItem.Click += (_, _) => OpenNewTechDialog();
        var editItem = new MenuItem { Header = loc.Get("tech.menu_edit") };
        editItem.Click += (_, _) => OpenEditTechDialog();
        var deleteItem = new MenuItem { Header = loc.Get("tech.menu_delete") };
        deleteItem.Click += (_, _) => DeleteContextCard();
        var refreshItem = new MenuItem { Header = loc.Get("tech.menu_refresh") };   // 刷新 = 重载入（用户：从 AST 重新读取，删除恢复）
        refreshItem.Click += (_, _) =>
        {
            _engine.Reload();   // 重扫 AST + 清空全部登记（未保存的创建/修改丢弃、删除恢复）
            _descHeights.Clear();   // 重载入后科技对象换新，高度缓存失效
            _iconCache.Clear();
            RebuildImage();
        };
        var saveItem = new MenuItem { Header = loc.Get("tech.menu_save") };
        // 延迟到右键菜单关闭后再弹旋转窗口——从 ContextMenu 点击直接 Show 无边框半透明窗口会渲染成黑块（用户 2026-08）
        saveItem.Click += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(SaveAll));
        var exportItem = new MenuItem { Header = loc.Get("tech.menu_export") };
        exportItem.Click += async (_, _) => await ExportFullImageAsync();
        var ctxMenu = new ContextMenu();
        ctxMenu.Items.Add(newItem);
        ctxMenu.Items.Add(editItem);
        ctxMenu.Items.Add(deleteItem);
        ctxMenu.Items.Add(saveItem);
        ctxMenu.Items.Add(exportItem);
        ctxMenu.Opened += (_, _) =>
        {
            // PlacementTarget 是挂菜单的 _canvas（不是右键命中的卡片）——用鼠标位置命中测试找卡片
            var pos = Mouse.GetPosition(_canvas);
            _contextPos = pos;   // 新建预填用（右键位置 → 行/列）
            var hit = _canvas.InputHitTest(pos) as DependencyObject;
            while (hit != null && hit is not TechCardControl)
                hit = VisualTreeHelper.GetParent(hit);
            _contextCard = hit as TechCardControl;
            bool onTech = _contextCard != null;
            editItem.IsEnabled = onTech;    // 修改/删除必须右键在某科技上
            deleteItem.IsEnabled = onTech;
        };
        _canvas.ContextMenu = ctxMenu;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(top, 0);
        Grid.SetRow(_scroller, 1);
        root.Children.Add(top);
        root.Children.Add(_scroller);
        Content = root;

        RebuildImage();   // 异步重建（RebuildImageCore 完成时统一刷新科技总数状态）
    }

    /// <summary>重新计算布局 + 重建卡片（首次/语言切换/删除后调用）。
    /// **伪异步无缝加载**（用户：创建/删除期间 UI 正常、显示"正在计算"、旧图保留到最后）：
    /// 先即时渲染"正在计算"状态（Dispatcher.Background 让渲染先完成），再同步计算重建；
    /// **desc 高度缓存增量**（_descHeights 不清全量——旧卡用缓存、新卡自动补算），避免全量 Measure 卡死。
    /// onDone = 计算完成后回调（如新建后切中心到新科技——用户）。</summary>
    public void RebuildImage(Action? onDone = null)
    {
        var computing = _services.Localisation.Get("tech.graph_computing");
        _status.Text = computing;   // 即时显示"正在计算"（旧图保留）
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try
            {
                RebuildImageCore();
                onDone?.Invoke();
            }
            catch (Exception ex)
            {
                _status.Text = _services.Localisation.Get("tech.graph_status_failed");
                // 记录异常详情（editor_debug.log）——失败不再静默，便于定位（2026-08：曾因静默吞异常
                // 无法从日志确认"布局计算失败"根因 = 空行 FirstOrDefault 值元组缺省陷阱）
                try
                {
                    Stellaris.Parser.LoggerSetup.GetFactory()
                        .CreateLogger("TechnologyGraphPage")
                        .LogError(ex, "科技布局计算失败");
                }
                catch { /* 日志失败不影响界面 */ }
            }
        });
    }

    private void RebuildImageCore()
    {
        var visibleTechs = GetVisibleTechs();
        // 统一右侧占用（所有卡描述可用宽度一致）：全局 max(cost 宽, 数值列宽上限 90, 图标 24) + 12
        _unifiedRightZone = 0f;
        foreach (var t in visibleTechs)
        {
            using var costFont = new SKFont(SKTypeface.Default, Math.Max(6f, _renderer.FontSizeScale));
            float costW = costFont.MeasureText(t.Cost.ToString());
            using var modFont = new SKFont(SKTypeface.Default, Math.Max(6f, _renderer.FontSizeScale - 1));
            float maxValW = 0f;
            foreach (var m in _engine.GetModifierLines(t, _lang))
                maxValW = Math.Max(maxValW, modFont.MeasureText(m.Value ?? ""));
            _unifiedRightZone = Math.Max(_unifiedRightZone, Math.Max(costW, Math.Max(Math.Min(maxValW, 90f), 24f)) + 12);
        }
        _renderer.UnifiedRightZone = _unifiedRightZone;
        _renderer.ModLineH = ModRowHeightFor();   // 加成行实际行高（WPF 实测——导出行距与页面一致）

        // 预测量每卡描述实际高度（WPF 换行与字符测量一致）——**缓存增量**：不清全量，
        // 旧卡沿用缓存、新建/修改的卡自动补算（CardHeightFor 内 TryGetValue fallback）
        foreach (var t in visibleTechs)
            if (!_descHeights.ContainsKey(t))
                _descHeights[t] = DescHeightFor(t);

        _layout = TechnologyLayout.ComputeLabelMode(visibleTechs, CardHeightFor);   // ✅ 文本标签模式布局（旧连线 Compute 已隐藏）
        _canvas.Children.Clear();
        _tagHits.Clear();
        _tagBoxes.Clear();
        _iconCache.Clear();
        _canvas.Width = _layout.Width + 2;
        _canvas.Height = Math.Max(1, _layout.Height + 2);

        BuildRowBackgrounds();   // 行背景：标题条/竖线/行底线（六边形网格线已删）
        BuildLabels();           // 左右尖角框标签（替代旧连线）
        BuildCards();            // 节点卡片（构造未改动）

        _status.Text = string.Format(_services.Localisation.Get("tech.graph_status"), _layout.Nodes.Count);   // 实时刷新科技总数（用户）
    }

    // ==================== 行背景（学科色六边形密铺）+ 左右尖角框标签（当前模式） ====================

    /// <summary>行背景 = 学科色六边形网格密铺（物理蓝/社会绿/工程黄；other 灰）。
    /// **循环绘制**（与导出 Skia 一致，每行一个合并 Path + Clip 到行带）——废弃 tile 平铺
    /// （tile 平铺有"4 个一组 + 拼缝线 + 竖直观感变形"问题，用户反馈）。
    /// 同时绘制：tier 列学科色标题条（行顶，白字"1阶"）+ 学科色竖线 + 行底学科色直线（单线颜色——渐变已去掉）。</summary>
    private void BuildRowBackgrounds()
    {
        for (int i = 0; i < _layout.Rows.Count; i++)
        {
            var row = _layout.Rows[i];
            if (row.Height <= 0f)
                continue;
            var color = RowColor(row.Row);
            // 行背景六边形网格线已删除（用户规则：背景网格线直接删了，没什么用）——行内仅标题条/竖线/行底线

            // 行顶学科色标题条（**贯穿整行**——用户规则：写阶数的一行占满整行，不是分 tier 段）+ 阶数文本 + 分隔竖线
            var rowBar = new Border
            {
                Width = _layout.Width,
                Height = 34,   // 用户：色彩大标签再加几个像素（文字上下太狭小，底部空间不够）
                Background = new SolidColorBrush(color),   // 蓝/绿/黄标签
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rowBar, 0);
            Canvas.SetTop(rowBar, row.Y);
            _canvas.Children.Add(rowBar);

            foreach (var band in _layout.Bands.Where(b => b.Row == row.Row))
            {
                var tb = new TextBlock
                {
                    Text = band.Tier >= 0 ? (IsChinese ? $"{band.Tier}阶" : $"Tier {band.Tier}") : "?",
                    FontSize = _services.Preferences.FontSize + 7,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,           // 白字
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(tb, band.X + 10);
                // 文字垂直居中于 34px 标题条（用户：文字偏上、底部空间不够）
                Canvas.SetTop(tb, row.Y + Math.Max(0, (34 - (int)(_services.Preferences.FontSize + 7)) / 2.0));
                _canvas.Children.Add(tb);

                var line = new System.Windows.Shapes.Line
                {
                    X1 = band.X + band.Width, Y1 = row.Y + 34,
                    X2 = band.X + band.Width, Y2 = row.Y + row.Height,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x99, color.R, color.G, color.B)),
                    StrokeThickness = 1.5,
                    IsHitTestVisible = false
                };
                _canvas.Children.Add(line);
            }

            // 行底学科色直线（画在内容底部 2px——行间无白色空行，用户规则）
            var bottomLine = new System.Windows.Shapes.Rectangle
            {
                Width = _layout.Width,
                Height = 2,
                Fill = new SolidColorBrush(color),
                IsHitTestVisible = false
                // ⚠️ 不用 CacheMode（加载卡死根因之一）
            };
            Canvas.SetLeft(bottomLine, 0);
            Canvas.SetTop(bottomLine, row.Y + row.Height - 2);
            _canvas.Children.Add(bottomLine);
        }
    }

    /// <summary>预渲染 hex 网格位图 tile（每学科色一张，缓存）+ ImageBrush 平铺——大尺寸少平铺，GPU 高效。
    /// tile 宽 = round(√3·s×20)≈901、高 = 3·s×2=156（垂直精确周期、水平亚像素误差不可见）——替代旧"逐六边形矢量 Path"。</summary>
    private static ImageBrush MakeHexTileBrush(int row)
    {
        if (_hexBrushes.TryGetValue(row, out var cached))
            return cached;
        var color = RowColor(row);
        const double side = 26.0;
        double hexW = side * 1.73205;                  // 水平周期
        double tileW = Math.Round(hexW * 20);          // ≈ 901
        double tileH = side * 3.0 * 2;                 // 156（垂直周期 3s × 2）
        var lineColor = Color.FromArgb(0x8C, color.R, color.G, color.B);   // 只画网格线（不填充）

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int p = 0; p < 2; p++)
            {
                for (int r = 0; r < 2; r++)
                {
                    double cy = p * side * 3.0 + side * 0.866025 + r * side * 1.5;
                    double cx0 = r % 2 == 0 ? 0 : hexW / 2;
                    int n = r % 2 == 0 ? 21 : 20;   // 偏移 0 的行两端裁半（周期边界拼合），偏移 hexW/2 的行完整
                    for (int c = 0; c < n; c++)
                    {
                        double cx = cx0 + c * hexW;
                        var pts = new Point[6];
                        for (int i = 0; i < 6; i++)
                        {
                            double ang = Math.PI / 3.0 * i - Math.PI / 2.0;   // 尖顶朝上
                            pts[i] = new Point(cx + side * Math.Cos(ang), cy + side * Math.Sin(ang));
                        }
                        ctx.BeginFigure(pts[0], true, true);
                        for (int i = 1; i < 6; i++)
                            ctx.LineTo(pts[i], true, false);
                    }
                }
            }
        }
        geo.Freeze();
        var drawing = new GeometryDrawing
        {
            Geometry = geo,
            // 只保留网格线（不设 Brush 填充）——用户规则：网格线稳定、填充色易丢（社会学颜色丢失）
            Pen = new Pen(new SolidColorBrush(lineColor), 1.5)
        };
        var group = new DrawingGroup();
        group.Children.Add(drawing);
        group.Freeze();

        // 渲染为位图（大尺寸，防缩放模糊；只 3 张，内存小）
        var rtb = new RenderTargetBitmap((int)tileW, (int)tileH, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawDrawing(group);
        rtb.Render(dv);
        rtb.Freeze();

        var brush = new ImageBrush(rtb)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, tileW, tileH),
            ViewportUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        _hexBrushes[row] = brush;
        return brush;
    }

    /// <summary>⚠️ 废弃（2026-08）：逐六边形矢量 StreamGeometry Path——改为 MakeHexTileBrush 大位图平铺。代码仅存档。</summary>
    private static StreamGeometry BuildHexGeometry(double x0, double y0, double w, double h)
    {
        const double side = 26.0;
        double hexW = side * 1.73205;   // 水平中心距 = √3·s
        double hexH = side * 1.5;       // 垂直行距 = 1.5·s
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            int rows = (int)(h / hexH) + 2;
            int cols = (int)(w / hexW) + 2;
            if (rows <= 0 || cols <= 0)
                return geo;
            for (int r = 0; r < rows; r++)
            {
                double cy = y0 + side * 0.866025 + r * hexH;
                double cx0 = x0 + (r % 2 == 0 ? 0 : hexW / 2);   // 交替行水平错位
                for (int c = 0; c < cols; c++)
                {
                    double cx = cx0 + c * hexW;
                    var pts = new Point[6];
                    for (int i = 0; i < 6; i++)
                    {
                        double ang = Math.PI / 3.0 * i - Math.PI / 2.0;   // 尖顶朝上
                        pts[i] = new Point(cx + side * Math.Cos(ang), cy + side * Math.Sin(ang));
                    }
                    ctx.BeginFigure(pts[0], true, true);
                    for (int i = 1; i < 6; i++)
                        ctx.LineTo(pts[i], true, false);
                }
            }
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>⚠️ 废弃（2026-08）：DrawingBrush tile 平铺方案——有"4 个一组/拼缝线/竖直观感变形"问题，
    /// 已被 BuildHexGeometry 循环绘制替代。代码仅存档。</summary>
    private static Brush MakeHexBrush(Color baseColor)
    {
        const double side = 26.0;
        double hexW = side * 1.73205;      // 周期宽 = √3·s
        double tileH = side * 3.0;         // 周期高 = 3·s（两行交错）
        var fillColor = Color.FromArgb(0x30, baseColor.R, baseColor.G, baseColor.B);
        var lineColor = Color.FromArgb(0x8C, baseColor.R, baseColor.G, baseColor.B);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int rowIdx = 0; rowIdx < 2; rowIdx++)
            {
                double cy = side * 0.866025 + rowIdx * side * 1.5;
                double cx0 = rowIdx % 2 == 0 ? 0.0 : hexW / 2;
                for (int c = 0; c < 2; c++)
                {
                    double cx = cx0 + c * hexW;
                    var pts = new Point[6];
                    for (int i = 0; i < 6; i++)
                    {
                        double ang = Math.PI / 3.0 * i - Math.PI / 2.0;   // 尖顶朝上
                        pts[i] = new Point(cx + side * Math.Cos(ang), cy + side * Math.Sin(ang));
                    }
                    ctx.BeginFigure(pts[0], true, true);
                    for (int i = 1; i < 6; i++)
                        ctx.LineTo(pts[i], true, false);
                }
            }
        }
        geo.Freeze();
        var fill = new GeometryDrawing
        {
            Geometry = geo,
            Brush = new SolidColorBrush(fillColor),
            Pen = new Pen(new SolidColorBrush(lineColor), 1.5)
        };
        var group = new DrawingGroup();
        group.Children.Add(fill);
        group.Freeze();
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, hexW, tileH),
            ViewportUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>行号 → 学科色（Row: 0=physics 1=society 2=engineering 3=other）。</summary>
    private static Color RowColor(int row)
    {
        switch (row)
        {
            case 0: return Color.FromRgb(0x3A, 0x6E, 0xA5);   // 物理蓝
            case 1: return Color.FromRgb(0x3F, 0x7D, 0x51);   // 社会绿
            case 2: return Color.FromRgb(0xB0, 0x8D, 0x2E);   // 工程黄
            default: return Color.FromRgb(0x55, 0x55, 0x60);
        }
    }

    /// <summary>是否中文界面语种（Tier 标题显示"1阶"而非"Tier 1"）。</summary>
    private bool IsChinese => _lang.Contains("chinese", StringComparison.OrdinalIgnoreCase)
        || _lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>左右尖角框标签（替代连线）：每个节点左侧放前置科技标签、右侧放后继科技标签。
    /// 跨学科前置/后继同样以文本标签表达（标签显示全部关系）。</summary>
    private void BuildLabels()
    {
        var byKey = _layout.Nodes.ToDictionary(n => n.Node.Key, StringComparer.OrdinalIgnoreCase);
        // 后继索引（反查——含跨学科）
        var succ = new Dictionary<string, List<TechNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in _layout.Nodes)
        {
            foreach (var pk in n.Node.Prerequisites)
            {
                if (string.IsNullOrEmpty(pk) || !byKey.ContainsKey(pk))
                    continue;
                if (!succ.TryGetValue(pk, out var l))
                    succ[pk] = l = new List<TechNode>();
                if (!l.Contains(n.Node))
                    l.Add(n.Node);
            }
        }
        double fontSize = _services.Preferences.FontSize;
        double tagFs = Math.Max(8, fontSize - 1);   // 用户：前置/后继标签字体放大（至少 +1）
        foreach (var lt in _layout.Nodes)
        {
            // 前置标签（左侧，**左对齐**：左缘固定 = 小列左缘；宽随字符；线/边框色 = 前置科技学科色）
            var pres = lt.Node.Prerequisites
                .Where(p => byKey.ContainsKey(p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (pres.Count > 0)
            {
                float stackH = TechnologyLayout.TagStackHeight(pres.Count);
                double top = lt.Y + Math.Max(0, (lt.Height - stackH) / 2);
                double right = lt.X - 14;   // 框右缘贴节点左缘前 14px（跟节点走，非背景）
                for (int i = 0; i < pres.Count; i++)
                {
                    double ty = top + i * (TechnologyLayout.TagHeight + TechnologyLayout.TagGap);
                    string label = _engine.LocalisedName(pres[i], _lang);
                    double w = Math.Min(MeasureTextWidth(label, tagFs) + 20, TechnologyLayout.LabelZoneWidth - 8);
                    AddTagBox(right - w, ty, w, label, tipRight: true, fontSize, byKey[pres[i]].Node, lt.X);
                }
            }
            // 后继标签（右侧，**右对齐**：框左缘贴节点右缘后 14px，向右展开；宽随字符；线/边框色 = 后继科技学科色）
            if (succ.TryGetValue(lt.Node.Key, out var kids) && kids.Count > 0)
            {
                var sorted = kids.OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
                float stackH = TechnologyLayout.TagStackHeight(sorted.Count);
                double top = lt.Y + Math.Max(0, (lt.Height - stackH) / 2);
                double left = lt.X + TechnologyLayout.CardWidth + 14;
                for (int i = 0; i < sorted.Count; i++)
                {
                    double ty = top + i * (TechnologyLayout.TagHeight + TechnologyLayout.TagGap);
                    string label = _engine.LocalisedName(sorted[i].Key, _lang);
                    double w = Math.Min(MeasureTextWidth(label, tagFs) + 20, TechnologyLayout.LabelZoneWidth - 8);
                    AddTagBox(left, ty, w, label, tipRight: false, fontSize, sorted[i], lt.X + TechnologyLayout.CardWidth);
                }
            }
        }
    }

    /// <summary>尖角框标签（WPF 纯绘制：Path + TextBlock + 科技线——**非独立可点击控件**）。
    /// 白色不透明底 + **边框 = 对应科技学科色**；前置（tipRight）文字左对齐、后继（tipLeft）文字右对齐；
    /// 框宽随字符自适应；点击命中由画布坐标反查 _tagHits（用户规则：算在节点/画布内，少创建控件）。</summary>
    private void AddTagBox(double x, double y, double w, string text, bool tipRight, double fontSize,
        TechNode target, double nodeEdgeX)
    {
        double h = TechnologyLayout.TagHeight;
        double tip = TechnologyLayout.TagTipSize;
        double bodyW = Math.Max(8, w - tip);
        double cy = y + h / 2;
        var borderColor = RowColor(TechnologyLayout.RowIndexOf(target.Area));   // 边框 = 对应科技学科色

        // 尖角框（白底 + 学科色边框；纯绘制，不挂事件）
        var boxGeo = new StreamGeometry();
        using (var ctx = boxGeo.Open())
        {
            if (tipRight)
            {
                ctx.BeginFigure(new Point(x, y), true, true);
                ctx.LineTo(new Point(x + bodyW, y), true, false);
                ctx.LineTo(new Point(x + bodyW + tip, cy), true, false);
                ctx.LineTo(new Point(x + bodyW, y + h), true, false);
                ctx.LineTo(new Point(x, y + h), true, false);
            }
            else
            {
                ctx.BeginFigure(new Point(x + bodyW, y), true, true);
                ctx.LineTo(new Point(x + bodyW, y + h), true, false);
                ctx.LineTo(new Point(x + tip, y + h), true, false);
                ctx.LineTo(new Point(x, cy), true, false);
                ctx.LineTo(new Point(x + tip, y), true, false);
            }
        }
        boxGeo.Freeze();
        var box = new System.Windows.Shapes.Path
        {
            Data = boxGeo,
            Fill = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x34)),   // 背景 = 节点卡片底色（用户规则：和节点一样）
            Stroke = new SolidColorBrush(borderColor),
            StrokeThickness = 1.5,
            IsHitTestVisible = false   // 命中由画布坐标反查（不创建可点击控件）
        };

        // 文字：前置左对齐、后继右对齐（宽 = 框内宽）；**文本色按稀有度**；字号自适应（长名缩小完整显示——用户：标签比文字小）
        double tagFs = Math.Max(8, fontSize - 1);
        while (tagFs > 7 && MeasureTextWidth(text, tagFs) > bodyW - 12)
            tagFs -= 1;
        var tb = new TextBlock
        {
            Text = text,
            Width = bodyW - 12,
            TextAlignment = tipRight ? TextAlignment.Left : TextAlignment.Right,
            FontSize = tagFs,
            Foreground = new SolidColorBrush(TagTextColor(target)),   // 危险=红、稀有=紫、常规=白
            TextTrimming = TextTrimming.CharacterEllipsis,   // 极小空间兜底
            IsHitTestVisible = false
        };
        Canvas.SetLeft(tb, x + 6);
        Canvas.SetTop(tb, y + (h - tb.FontSize) / 2 - 1);

        // 科技线：标签尖角 ↔ 节点边缘（颜色 = 对应科技学科色）
        var link = new System.Windows.Shapes.Line
        {
            X1 = tipRight ? x + bodyW + tip : nodeEdgeX,
            Y1 = cy,
            X2 = tipRight ? nodeEdgeX : x,
            Y2 = cy,
            Stroke = new SolidColorBrush(borderColor),
            StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
        _canvas.Children.Add(link);
        _canvas.Children.Add(box);
        _canvas.Children.Add(tb);

        // 命中登记（点击坐标反查——不依赖控件，用户规则；IsPre=前置标签，高亮用）
        _tagHits.Add((new Rect(x, y, w, h), target, tipRight));
        _tagBoxes.Add(box);   // 边框高亮用（与 _tagHits 一一对应）
    }

    /// <summary>标签文本色按科技稀有度（用户规则）：危险=红、稀有=紫、常规=白（与卡片文字色一致）。</summary>
    private static Color TagTextColor(TechNode t)
    {
        if (t.IsDangerous) return Color.FromRgb(0xC0, 0x39, 0x2B);
        if (t.IsRare) return Color.FromRgb(0x9B, 0x59, 0xB6);
        return Color.FromRgb(0xEA, 0xEA, 0xEA);
    }

    /// <summary>搜索结果下拉列表项（显示名 + 科技）。</summary>
    private sealed class SearchResultItem
    {
        public TechNode Node { get; }
        private readonly string _display;
        public SearchResultItem(TechNode node, string display)
        {
            Node = node;
            _display = display;
        }
        public override string ToString() => _display;
    }

    /// <summary>标签按钮点击：选中目标科技并滚动到它（前往上一代/下一代）。</summary>
    private void GoToTech(TechNode tech)
    {
        _searchMatches = null;   // 清除搜索多结果高亮
        if (_resultList != null)
            _resultList.Visibility = Visibility.Collapsed;
        var lt = _layout.Nodes.FirstOrDefault(n => n.Node.Key == tech.Key);
        if (lt == null)
            return;
        SelectNode(lt);
        _scroller.ScrollToHorizontalOffset(Math.Max(0, lt.X * _zoom - _scroller.ViewportWidth / 2));
        _scroller.ScrollToVerticalOffset(Math.Max(0, lt.Y * _zoom - _scroller.ViewportHeight / 2));
    }

    /// <summary>按像素宽截断文本（省略号）。</summary>
    private static string TruncateText(string text, double maxWidth, double fontSize)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return text;
        if (MeasureTextWidth(text, fontSize) <= maxWidth)
            return text;
        string result = text;
        while (result.Length > 1 && MeasureTextWidth(result + "…", fontSize) > maxWidth)
            result = result[..^1];
        return result + "…";
    }

    /// <summary>TextBlock 测量文本宽度（与显示同字体，保证截断一致）。</summary>
    private static double MeasureTextWidth(string text, double fontSize)
    {
        var tb = new TextBlock { Text = text, FontSize = fontSize };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize.Width;
    }

    // ==================== 连线（WPF Path，可单独高亮） ====================
    // ⚠️ 旧"动态生成连线图"模式 = 失败的试验性产物，已隐藏（2026-08）：本方法不再被调用，
    // 仅存档保留（页面当前用 BuildLabels 左右标签替代连线）。

    private void BuildConnections()
    {
        _edges.Clear();
        var allPts = new List<(string Source, List<(float X, float Y)> Pts)>();   // 全部折线（同来源相交点圆点用）
        var laneTable = new TechnologyLayout.TurnLaneTable();   // 垂直线转向分道登记（用户方案）
        var byKey = _layout.Nodes.ToDictionary(n => n.Node.Key, StringComparer.OrdinalIgnoreCase);
        // 预计算 tier 列左缘（该列最小 X）——主干拐弯点 = 列左缘前空隙（同列后继共享，在一起拐弯）
        var colLeft = new Dictionary<int, float>();
        foreach (var n in _layout.Nodes)
        {
            if (!colLeft.TryGetValue(n.Node.Tier, out var v) || n.X < v)
                colLeft[n.Node.Tier] = n.X;
        }
        foreach (var node in _layout.Nodes)
        {
            var (lx, ly) = TechnologyLayout.LeftCenter(node);
            foreach (var pre in node.Node.Prerequisites)
            {
                if (!byKey.TryGetValue(pre, out var preNode))
                    continue;
                var (rx, ry) = TechnologyLayout.RightCenter(preNode);
                // 主干：线先水平延伸，到"后继所在列左缘前空隙"再拐弯（同列后继共享拐弯 X——在一起拐弯）；
                // 该 X 被卡占或反向（后继在左侧）则退化到 P 右侧
                float turnX = colLeft.TryGetValue(node.Node.Tier, out var cl) ? cl - 80f : rx + 80f;   // 垂直线距节点 ≥ 80px
                if (turnX < rx + 80f)   // 空隙不足：退化到距 A 右缘 80
                    turnX = rx + 80f;
                bool trunkOk = true;
                foreach (var n in _layout.Nodes)
                {
                    if (n == node || n == preNode)
                        continue;
                    if (turnX >= n.X && turnX <= n.X + TechnologyLayout.CardWidth)
                    {
                        trunkOk = false;
                        break;
                    }
                }
                if (!trunkOk)
                    turnX = TechnologyLayout.RouteOrthogonalX(rx, lx, _layout.Nodes, node, preNode);
                // 不同起点的线转向 X 错开（同源同 X，不同源在空隙内 ±20 错开——减少同点撞车）
                turnX += TechnologyLayout.LineVerticalOffset(pre, "");
                // clamp：垂直线距节点 ≥ 80px（偏移不能突破；空隙不足时退到距 A 右缘 80）
                float maxTx = colLeft.TryGetValue(node.Node.Tier, out var cl2) ? cl2 - 80f : float.MaxValue;
                turnX = Math.Clamp(turnX, rx + 80f, Math.Max(rx + 80f, maxTx));
                // **转向表分道**（用户方案）：垂直线登记 Y 区间，冲突则优先选"Y 占用最多"的 X、
                // 再分道（40→20px 步进）；仍不够 → **跳线字母标记**（线延伸一段 + 方框 AA…ZZ，两端配对）
                float vMin = Math.Min(ry, ly), vMax = Math.Max(ry, ly);
                var laneX = laneTable.Register(turnX, vMin, vMax, pre);
                if (laneX == null)
                {
                    string tag = laneTable.AllocJumpTag();
                    DrawJumpTag(tag, rx, ry, lx, ly);
                    continue;
                }
                turnX = laneX.Value;
                // 穿卡绕行（RouteOrtho：折线任一段穿卡 → 竖-横-竖绕行；否则 横-竖-横）
                var pts = TechnologyLayout.RouteOrtho(rx, ry, turnX, lx, ly, _layout.Nodes, node, preNode);
                allPts.Add((pre, pts));
                var path = new System.Windows.Shapes.Path
                {
                    Stroke = LineNormal,
                    StrokeThickness = 1.5,
                    Data = MakePolyline(pts)
                };
                Canvas.SetLeft(path, 0);
                Canvas.SetTop(path, 0);
                _canvas.Children.Add(path);
                _edges.Add(new TechEdge { From = preNode, To = node, Path = path });   // 保存引用（点击高亮用）
            }
        }
        // 相交点圆点：有圆点 = 线在这里相交（撞车）；没有 = 线只是错开（用户规则）
        var dotBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x38));
        foreach (var (cx, cy) in TechnologyLayout.FindCrossings(allPts))
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = dotBrush
            };
            Canvas.SetLeft(dot, cx - 3);
            Canvas.SetTop(dot, cy - 3);
            _canvas.Children.Add(dot);
        }
    }

    /// <summary>跳线标记：线延伸一段停住（不连到底），两端各画一个方框+2 位字母配对（用户方案）。</summary>
    private void DrawJumpTag(string tag, float rx, float ry, float lx, float ly)
    {
        const float ext = 80f;
        // 源端延伸段 + 目标端延伸段（不连到底）
        var seg1 = new System.Windows.Shapes.Line
        {
            X1 = rx, Y1 = ry, X2 = rx + ext, Y2 = ry,
            Stroke = LineNormal, StrokeThickness = 1.5
        };
        var seg2 = new System.Windows.Shapes.Line
        {
            X1 = lx - ext, Y1 = ly, X2 = lx, Y2 = ly,
            Stroke = LineNormal, StrokeThickness = 1.5
        };
        _canvas.Children.Add(seg1);
        _canvas.Children.Add(seg2);
        DrawTagBox(rx + ext, ry, tag);
        DrawTagBox(lx - ext, ly, tag);
    }

    /// <summary>方框 + 2 位字母（跳线配对标记）。</summary>
    private void DrawTagBox(float cx, float cy, string tag)
    {
        var box = new Border
        {
            Width = 24,
            Height = 16,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = tag,
                FontSize = 9,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(box, cx - 12);
        Canvas.SetTop(box, cy - 8);
        _canvas.Children.Add(box);
    }

    /// <summary>折线（点序列 → WPF PathGeometry）。</summary>
    private static Geometry MakePolyline(List<(float X, float Y)> pts)
    {
        var fig = new PathFigure { StartPoint = new Point(pts[0].X, pts[0].Y) };
        for (int i = 1; i < pts.Count; i++)
            fig.Segments.Add(new LineSegment(new Point(pts[i].X, pts[i].Y), true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    // ==================== 卡片（TechCardControl） ====================

    /// <summary>解锁行（非科技）：prerequisites 含本科技的根 block，**排除科技本身**——
    /// 科技间解锁已由连线图展示（tier 前置连线），不重复写入加成解锁栏；舰船由 ShipEngine 单独合并。</summary>
    private List<string> GetUnlockRows(TechNode tech)
    {
        return _engine.GetUnlockingBlocks(tech.Key)
            .Where(uk => _engine.Get(uk) == null)
            .ToList();
    }

    private void BuildCards()
    {
        double fontSize = _services.Preferences.FontSize;
        foreach (var lt in _layout.Nodes)
        {
            var tech = lt.Node;
            string title = _engine.LocalisedName(tech.Key, _lang);
            string desc = _engine.LocalisedDesc(tech.Key, _lang)?.Replace("\\n", "\n") ?? "";
            var mods = _engine.GetModifierLines(tech, _lang)
                .Select(m => (m.Display, m.Value)).ToList();
            // 解锁行：全局根 block（prerequisites 含本科技）→ 左侧翻译名、右侧"解锁"
            // 排除"解锁的科技"（科技间解锁已由连线图展示，不重复写入解锁栏）
            string unlocksText = _services.Localisation.Get("tech.unlocks");
            foreach (var uk in GetUnlockRows(tech))
                mods.Add((_engine.LocalisedName(uk, _lang), unlocksText));
            // 解锁行（舰船）：舰船文件夹根 block（prerequisites 含本科技）→ 本地化名（命名规则特殊）
            if (_services.ShipEngine != null)
                foreach (var sk in _services.ShipEngine.GetUnlockingBlocks(tech.Key))
                    mods.Add((_services.ShipEngine.LocalisedName(sk, _lang), unlocksText));
            string cost = tech.Cost.ToString();
            var icon = LoadIconBitmap(_engine.GetTechIconPath(tech));
            var catIcon = tech.Categories.Count > 0 ? LoadIconBitmap(_engine.GetCategoryIcon(tech.Categories[0])) : null;

            var card = new TechCardControl(tech, title, desc, mods, cost, icon, catIcon, fontSize)
            {
                Height = lt.Height
            };
            Canvas.SetLeft(card, lt.X);
            Canvas.SetTop(card, lt.Y);
            _canvas.Children.Add(card);

            // 黄色高亮外圈：叠加在卡片外沿（**不影响原有边框色**——危险红/稀有紫/普通白保留），
            // 选中时显示；不拦截命中（IsHitTestVisible=false）
            var glow = new Border
            {
                Width = TechnologyLayout.CardWidth + 8,
                Height = lt.Height + 8,
                BorderBrush = GlowBrush,
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(glow, lt.X - 4);
            Canvas.SetTop(glow, lt.Y - 4);
            _canvas.Children.Add(glow);
            var ltRef = lt;
            card.Clicked += _ => SelectNode(ltRef);
            _glows.Add((lt, glow));
        }
    }

    // ==================== 点击高亮 ====================

    /// <summary>选中节点：该节点黄圈 + 与之相连（入/出）的线变黄加粗；其余恢复默认。仅节点与线，相连另一端节点不变色。</summary>
    private void SelectNode(LayoutTech lt)
    {
        _selectedKey = lt.Node.Key;
        UpdateHighlight();
    }

    private void ClearSelection()
    {
        _selectedKey = null;
        _searchMatches = null;   // 清搜索多结果高亮
        if (_resultList != null)
            _resultList.Visibility = Visibility.Collapsed;
        UpdateHighlight();
    }

    /// <summary>选中高亮（用户规则）：本节点黄圈；**前置节点 + 前置标签亮红**、**后继节点 + 后继标签浅绿**。
    /// 搜索多结果模式（_searchMatches 非空）：匹配节点全部黄圈，标签边框恢复学科色。</summary>
    private void UpdateHighlight()
    {
        // 搜索多结果模式：匹配节点黄圈（用户：多个结果一起被高亮）
        if (_searchMatches is { Count: > 0 })
        {
            var set = new HashSet<string>(_searchMatches.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var (node, glow) in _glows)
            {
                bool hit = set.Contains(node.Node.Key);
                if (hit)
                {
                    if (glow.Visibility != Visibility.Visible || !ReferenceEquals(glow.BorderBrush, GlowBrush))
                    {
                        glow.Visibility = Visibility.Visible;
                        glow.BorderBrush = GlowBrush;
                    }
                }
                else if (glow.Visibility != Visibility.Collapsed)
                {
                    glow.Visibility = Visibility.Collapsed;
                }
            }
            // 标签边框恢复学科色
            for (int i = 0; i < _tagBoxes.Count && i < _tagHits.Count; i++)
            {
                var (_, target, _) = _tagHits[i];
                _tagBoxes[i].Stroke = new SolidColorBrush(RowColor(TechnologyLayout.RowIndexOf(target.Area)));
                _tagBoxes[i].StrokeThickness = 1.5;
            }
            return;
        }
        // 前置/后继集合（含跨学科）
        var preKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kidKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_selectedKey != null)
        {
            var byKey = _layout.Nodes.ToDictionary(n => n.Node.Key, StringComparer.OrdinalIgnoreCase);
            if (byKey.TryGetValue(_selectedKey, out var sel))
            {
                foreach (var p in sel.Node.Prerequisites)
                    if (byKey.ContainsKey(p))
                        preKeys.Add(p);
            }
            foreach (var n in _layout.Nodes)
            {
                if (string.Equals(n.Node.Key, _selectedKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (n.Node.Prerequisites.Contains(_selectedKey))
                    kidKeys.Add(n.Node.Key);
            }
        }

        // 节点外圈：本节点黄、前置红、后继绿（状态无变化则跳过赋值——避免每次点击全量失效）
        foreach (var (node, glow) in _glows)
        {
            if (node.Node.Key == _selectedKey)
            {
                if (glow.Visibility != Visibility.Visible || !ReferenceEquals(glow.BorderBrush, GlowBrush))
                {
                    glow.Visibility = Visibility.Visible;
                    glow.BorderBrush = GlowBrush;
                }
            }
            else if (_selectedKey != null && preKeys.Contains(node.Node.Key))
            {
                if (glow.Visibility != Visibility.Visible || !ReferenceEquals(glow.BorderBrush, PreGlowBrush))
                {
                    glow.Visibility = Visibility.Visible;
                    glow.BorderBrush = PreGlowBrush;
                }
            }
            else if (_selectedKey != null && kidKeys.Contains(node.Node.Key))
            {
                if (glow.Visibility != Visibility.Visible || !ReferenceEquals(glow.BorderBrush, KidGlowBrush))
                {
                    glow.Visibility = Visibility.Visible;
                    glow.BorderBrush = KidGlowBrush;
                }
            }
            else if (glow.Visibility != Visibility.Collapsed)
            {
                glow.Visibility = Visibility.Collapsed;
            }
        }

        // 标签**边框高亮**（前置红、后继绿——改边框色/加粗，不覆盖背景、不超出边框，用户规则）
        for (int i = 0; i < _tagBoxes.Count && i < _tagHits.Count; i++)
        {
            var (_, target, isPre) = _tagHits[i];
            Brush stroke = new SolidColorBrush(RowColor(TechnologyLayout.RowIndexOf(target.Area)));
            double thick = 1.5;
            if (_selectedKey != null)
            {
                if (isPre && preKeys.Contains(target.Key))
                {
                    stroke = PreGlowBrush;
                    thick = 2.5;
                }
                else if (!isPre && kidKeys.Contains(target.Key))
                {
                    stroke = KidGlowBrush;
                    thick = 2.5;
                }
            }
            _tagBoxes[i].Stroke = stroke;
            _tagBoxes[i].StrokeThickness = thick;
        }

        // 旧连线高亮（_edges 已弃用——BuildConnections 不再调用，循环为空）
        foreach (var edge in _edges)
        {
            bool hit = _selectedKey != null
                && (string.Equals(edge.From.Node.Key, _selectedKey, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(edge.To.Node.Key, _selectedKey, StringComparison.OrdinalIgnoreCase));
            edge.Path.Stroke = hit ? LineHighlight : LineNormal;
            edge.Path.StrokeThickness = hit ? 3.0 : 1.5;
        }
    }

    // ==================== 卡片高度（与内容一致） ====================

    /// <summary>WPF Measure 描述实际高度（目标描述宽度下换行后的真实高度）。</summary>
    private float DescHeightFor(TechNode t)
    {
        var desc = _engine.LocalisedDesc(t.Key, _lang)?.Replace("\\n", "\n");
        if (string.IsNullOrEmpty(desc))
            desc = "—";
        float textW = TechnologyLayout.CardWidth - 58 - _unifiedRightZone;
        var tb = new TextBlock
        {
            Text = desc,
            FontSize = _services.Preferences.FontSize - 1,
            TextWrapping = TextWrapping.Wrap,
            Width = textW
        };
        tb.Measure(new Size(textW, double.PositiveInfinity));
        return (float)Math.Max(0, tb.DesiredSize.Height);
    }

    private float CardHeightFor(TechNode t)
    {
        var mods = _engine.GetModifierLines(t, _lang);
        int shipCount = _services.ShipEngine?.GetUnlockingBlocks(t.Key).Count ?? 0;
        int modCount = mods.Count + GetUnlockRows(t).Count + shipCount;   // 数值加成 + 解锁行（非科技 + 舰船）
        float fs = (float)_renderer.FontSizeScale;
        float descH = _descHeights.TryGetValue(t, out var dh) ? dh : DescHeightFor(t);   // 描述实际高度（不截断）
        float modTopRel = Math.Max(32 + 12 + descH + 6, 32 + 50);   // 描述区（实际行数）或图标底
        float modRowH = ModRowHeightFor();                           // 加成行实际行高（WPF Measure——与渲染一致，不再估算）
        float modsH = modCount * modRowH;                          // 加成 + 解锁行
        // 底部只留加成区实际 margin（字号-1）+ 4px 余量——不再多算 1 整行（用户指出原多 2 行）
        float bottomPad = (fs - 1) + 4f;
        return Math.Max(TechnologyLayout.CardHeight, modTopRel + 12 + modsH + bottomPad);
    }

    /// <summary>加成行实际行高（WPF Measure 单行 TextBlock + margin）——缓存，页面/导出共用。</summary>
    private float _modRowH = -1f;
    private float ModRowHeightFor()
    {
        if (_modRowH > 0f)
            return _modRowH;
        var tb = new TextBlock
        {
            Text = "Ag",
            FontSize = _services.Preferences.FontSize - 1,
            Margin = new Thickness(0, 1, 4, 1)   // 与 TechCardControl 加成行一致
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _modRowH = (float)tb.DesiredSize.Height;
        return _modRowH;
    }

    // ==================== 图标（dds → BitmapSource，带缓存） ====================

    private BitmapSource? LoadIconBitmap(string relPath)
    {
        if (string.IsNullOrEmpty(relPath))
            return null;
        if (_iconCache.TryGetValue(relPath, out var cached))
            return cached;
        try
        {
            _services.ImageEngine.LoadImage(relPath);
            var ps = _services.ImageEngine.Result;
            if (ps == null)
                return null;
            int w = ps.Width, h = ps.Height;
            byte[] px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = (y * w + x) * 4;
                    var p = ps.Data[y][x];
                    px[idx] = p[2];       // B
                    px[idx + 1] = p[1];   // G
                    px[idx + 2] = p[0];   // R
                    px[idx + 3] = p.Length >= 4 ? p[3] : (byte)255;
                }
            }
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            src.Freeze();
            _iconCache[relPath] = src;
            return src;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>导出整图 PNG 用的 Skia 图标加载（图片模式渲染保留）。</summary>
    private SKBitmap? LoadIconSkia(string relPath)
    {
        var src = LoadIconBitmap(relPath);
        if (src == null)
            return null;
        // BitmapSource → SKBitmap（Rgba8888）
        int w = src.PixelWidth, h = src.PixelHeight;
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = new byte[w * h * 4];
        src.CopyPixels(pixels, w * 4, 0);
        // Bgra32 → Rgba8888
        for (int i = 0; i + 3 < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        return bmp;
    }

    // ==================== 右键删除（内存级，保存功能后做） ====================

    /// <summary>删除右键命中的科技（内存移除 + 重新布局；不落盘，重启恢复）。</summary>
    /// <summary>右键"新建"→ 科技编辑弹窗（预填右键位置：area=所在行、tier=所在列、cost=所在小列；key 自动生成可改）。</summary>
    private void OpenNewTechDialog()
    {
        var all = GetVisibleTechs().ToList();
        var categories = all.SelectMany(t => t.Categories).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        // 右键位置 → 行（area）/ tier 列 / cost 小列（用户：右键在哪新建就自动填那一行/列的值）
        string area = "physics";
        int tier = 0, cost = 0;
        var row = _layout.Rows.FirstOrDefault(r => _contextPos.Y >= r.Y && _contextPos.Y < r.Y + Math.Max(1, r.Height));
        if (row.Row >= 0 && row.Row < 4)
            area = new[] { "physics", "society", "engineering", "other" }[row.Row];
        var band = _layout.Bands.FirstOrDefault(b => b.Row == row.Row && _contextPos.X >= b.X && _contextPos.X < b.X + b.Width);
        if (band.Width > 0f)
            tier = band.Tier;
        var near = _layout.Nodes.Where(n => string.Equals(n.Node.Area, area, StringComparison.OrdinalIgnoreCase) && n.Node.Tier == tier)
            .OrderBy(n => Math.Abs(n.X - _contextPos.X)).FirstOrDefault();
        if (near != null)
            cost = near.Node.Cost;
        var dlg = new TechEditDialog(_engine, all, categories, null, area, tier, cost, k => _services.Localisation.Get(k), LocalisedName, _services, _lang);
        dlg.Owner = Window.GetWindow(this);
        if (dlg.ShowDialog() == true)
        {
            // 用户：新建期间显示"正在计算"→ 计算完成自动把画面中心切到最新新建的科技
            RebuildImage(() =>
            {
                if (dlg.Result != null)
                    GoToTech(dlg.Result);
            });
        }
    }

    /// <summary>右键"修改"→ 科技编辑弹窗（加载右键命中的科技；key 只读）。</summary>
    private void OpenEditTechDialog()
    {
        if (_contextCard == null)
            return;
        var all = GetVisibleTechs().ToList();
        var categories = all.SelectMany(t => t.Categories).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        var dlg = new TechEditDialog(_engine, all, categories, _contextCard.Tech, "", 0, 0, k => _services.Localisation.Get(k), LocalisedName, _services, _lang);
        dlg.Owner = Window.GetWindow(this);
        if (dlg.ShowDialog() == true)
        {
            if (dlg.Result != null)
                _descHeights.Remove(dlg.Result);   // 修改后该卡描述高度重算（缓存增量）
            RebuildImage();
        }
    }

    /// <summary>本地化名（弹窗显示用）。</summary>
    private string LocalisedName(string key) => _engine.LocalisedName(key, _lang);

    private void DeleteContextCard()
    {
        if (_contextCard == null)
            return;
        _engine.RegisterRemoved(_contextCard.Tech);   // 删除登记（不改内存——绘制跳过；保存落盘成功后才移除内存）
        _descHeights.Remove(_contextCard.Tech);   // 删除卡高度缓存清理（增量）
        _contextCard = null;
        RebuildImage();
    }

    /// <summary>保存（SaveRunner——用户规则：保存必须显式登记，用户触发才落盘）：
    /// 写登记的全部科技文件 + 本地化文件；成功后清登记。
    /// **用户一个没改过**：右键在某科技上点保存 → 该科技所在文件登记待保存（"就当格式化"——用户 2026-08）。
    /// 保存后：仅当有删除（内存移除的科技）才重建布局——无删除（格式化/仅落盘）布局不变，
    /// 全量重建 679 卡会卡 UI（用户 2026-08："UI界面卡"）。</summary>
    private void SaveAll()
    {
        var modPrefix = _services.ModPrefs?.ModPrefix ?? "smt";
        var engine = _engine;
        if (!engine.HasDirty && _contextCard != null)
            engine.RegisterTechFile(_contextCard.Tech.OwnerFile);   // 无改动 + 右键命中科技 → 格式化登记其文件
        int removedBefore = engine.RemovedKeys.Count;
        SaveRunner.Run(_services, "status.saving",
            () =>
            {
                var (saved, errors) = engine.SaveAll(modPrefix);
                return errors.Count == 0;
            },
            onSuccess: () =>
            {
                // 数据未变（除删除的科技已从内存移除）；有删除 → **同步**重建布局——
                // 旋转窗口保持到重建完才关（用户 2026-08："转圈一直转到重建完"）；无删除（格式化/仅落盘）不重建
                if (removedBefore > 0)
                    RebuildImageCore();
            });
    }

    // ==================== 导出整图（图片模式） ====================

    private async Task ExportFullImageAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片|*.png",
            FileName = "technology_map.png"
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            var png = await Task.Run(() =>
            {
                // **分块渲染 + 拼合**（防 "Unable to allocate a bitmap"：用户 mod 布局巨大——
                // 全尺寸位图内存超上限时自动按比例缩小导出，分块 RenderLabelTile 每块 4096px 渲染内存可控）
                int fullW = Math.Max(1, (int)Math.Ceiling(_layout.Width));
                int fullH = Math.Max(1, (int)Math.Ceiling(_layout.Height));
                const int tile = 4096;
                const long MaxBytes = 2L * 1024 * 1024 * 1024;   // 全尺寸位图内存上限 2GB
                long fullBytes = (long)fullW * fullH * 4;
                double scale = fullBytes > MaxBytes ? Math.Sqrt((double)MaxBytes / fullBytes) : 1.0;
                int outW = Math.Max(1, (int)(fullW * scale));
                int outH = Math.Max(1, (int)(fullH * scale));
                using var outBmp = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                using (var canvas = new SKCanvas(outBmp))
                {
                    canvas.Clear(SKColors.White);
                    for (int y0 = 0; y0 < fullH; y0 += tile)
                    {
                        for (int x0 = 0; x0 < fullW; x0 += tile)
                        {
                            int x1 = Math.Min(x0 + tile, fullW);
                            int y1 = Math.Min(y0 + tile, fullH);
                            using var part = _renderer.RenderLabelTile(_layout, x0, x1, y0, y1, _lang);
                            using var partImg = SKImage.FromBitmap(part);
                            // scale<1 时缩放绘制到目标位图（保留完整布局、降低分辨率）
                            canvas.DrawImage(partImg,
                                new SKRect((float)(x0 * scale), (float)(y0 * scale), (float)(x1 * scale), (float)(y1 * scale)));
                        }
                    }
                }
                using var img = SKImage.FromBitmap(outBmp);
                using var enc = img.Encode(SKEncodedImageFormat.Png, 90);
                return enc.ToArray();
            });
            await File.WriteAllBytesAsync(dlg.FileName, png);
            MessageBox.Show($"{_services.Localisation.Get("tech.export_done")}\n{dlg.FileName}",
                _services.Localisation.Get("tech.export_title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{_services.Localisation.Get("tech.export_failed")}\n{ex.Message}",
                _services.Localisation.Get("tech.export_title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== Chrome 中键自动滚动 ====================

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            // 标签命中反查（用户规则：标签不是独立控件，点击画布按坐标计算——少创建控件/命中开销）
            var pos = e.GetPosition(_canvas);
            foreach (var (area, target, _) in _tagHits)
            {
                if (area.Contains(pos))
                {
                    GoToTech(target);
                    e.Handled = true;
                    return;
                }
            }
            // 左键点击：清空高亮（Preview 隧道先于卡片 Clicked 触发——点卡片会先清后设，最终状态正确）
            ClearSelection();
            return;
        }
        if (e.ChangedButton == MouseButton.Middle)
        {
            _autoScrolling = true;
            _scrollAnchor = e.GetPosition(this);
            _mousePos = _scrollAnchor;
            _scroller.CaptureMouse();
            _autoScrollTimer.Start();
            e.Handled = true;
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_autoScrolling)
            return;
        _mousePos = e.GetPosition(this);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _autoScrolling)
        {
            _autoScrolling = false;
            _autoScrollTimer.Stop();
            _scroller.ReleaseMouseCapture();
            e.Handled = true;
        }
    }
}
