// 文件: Stellaris.Editor/Pages/StaticMapPage.xaml.cs
// 静态地图页（参照动态地图布局）：左预览 + 右（静态列表 + 参数/其他/形状/颜色）。
// 静态地图参数比动态少（无恒星数/半径/星团/航道/星云等）；
// "参数"页含"生成形状占位符"功能（为静态地图注册占位样式）。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Stellaris.Editor.Controls;
using Stellaris.Engine.GalaxyMap;

namespace Stellaris.Editor.Pages;

public partial class StaticMapPage : UserControl
{
    private readonly EngineServices _services;
    private System.Windows.Threading.DispatcherTimer _mapDebounce = null!;   // 搜索框 2 秒防抖
    private string? _currentMap;
    private string? _currentPreviewShape;

    /// <summary>选中动态地图时触发（参数 = 动态地图名），供上层切回动态地图页。</summary>
    public event EventHandler<string>? DynamicMapRequested;

    // 地图列表拖拽排序状态
    private Point _dragStart;
    private readonly List<MapListItem> _dragItems = new();
    /// <summary>全部地图项（列表搜索过滤的底层数据——ReloadMaps 时备份完整顺序）。</summary>
    private readonly List<MapListItem> _allMapItems = new();
    private bool _dragging;
    private Point _shapeDragStart;
    private readonly List<ShapeRowItem> _shapeDragItems = new();

    // 预览区编辑模式
    private enum EditMode { None, PointSetting, Hyperlane }
    private EditMode _editMode = EditMode.None;
    private SystemEntry? _laneFrom;

    // 选中与框选
    /// <summary>调试/诊断日志统一写到 exe 目录的 editor_debug.log（与引擎同一份，不另起 log）。</summary>
    private static readonly string DiagLogPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "editor_debug.log");
    private void Diag(string msg)
    {
        try { System.IO.File.AppendAllText(DiagLogPath, $"[{DateTime.Now:HH:mm:ss}] [StaticMap] {msg}\n"); } catch { }
    }

    private readonly HashSet<SystemEntry> _selected = new();
    private readonly HashSet<Hyperlane> _selectedLanes = new();
    // 图形（导入的正三角形/矩形/正六边形——点阵生成模板；可与点一起选中，不参与航道）
    private readonly List<ShapeOverlay> _shapes = new();
    private readonly HashSet<ShapeOverlay> _selectedShapes = new();
    // 图像（预留接口——功能后续实现；结构已支持图像选中/组合）
    private readonly List<ImageOverlay> _images = new();
    private readonly HashSet<ImageOverlay> _selectedImages = new();
    private ShapeOverlay? _selLocatorShape;  // 当前选中的定位点所属图形
    private int _selLocatorIndex = -1;       // 定位点索引（-1 = 默认中心）
    private bool _leftDown;
    private Point _leftDownPos;
    private Point? _boxStart;
    private Rectangle? _boxRect;
    // 拖动移动选中点
    private bool _moving;
    private ShapeOverlay? _movingShape;   // 正在拖动的图形（移动中心）
    private ImageOverlay? _movingImage;   // 正在拖动的图像（移动位置）
    private Point _moveLast;
    // 复制/粘贴剪贴板（克隆）
    private List<SystemEntry> _clipboard = new();
    // 剪贴板内选中点之间的航道（源 Id 对）
    private List<(string From, string To)> _clipboardLanes = new();
    private List<ShapeOverlay> _clipboardShapes = new();  // 组合复制：图形
    private List<ImageOverlay> _clipboardImages = new();  // 组合复制：图像

    // 预览缩放（Ctrl+滚轮上滚放大/下滚缩小，以鼠标位置为中心）
    private double _zoom = 1.0;
    // 中键平移（坐标单位偏移）
    private double _panX, _panY;
    private bool _panning;
    private Point _panLast;
    // 拖动式旋转（Shift+右键按住拖动实时预览）
    private bool _rotating;
    private double _rotCenterX, _rotCenterY;
    private double _rotStartAngle;
    private List<(SystemEntry Sys, double X, double Y)> _rotSnap = new();
    // 最近一次画布点击位置（Ctrl+V 粘贴锚点）
    private Point _lastCanvasPos;
    // 点精度（静态地图坐标保留小数位数，读银河类别 galaxy.json）
    private int _pointPrecision = 1;

    public StaticMapPage(EngineServices services)
    {
        _services = services;
        InitializeComponent();
        MapFilterBox.ToolTip = _services.Localisation.Get("common.list_search");
        MapFilterSearchButton.ToolTip = _services.Localisation.Get("common.list_search");
        _mapDebounce = Stellaris.Editor.Controls.SearchDebouncer.Attach(MapFilterBox, () => OnMapFilterSearch(this, new RoutedEventArgs()));

        var loc = services.Localisation;
        TabBasic.Header = loc.Get("dynmap.tab.basic");
        TabOther.Header = loc.Get("dynmap.tab.other");
        TabShapes.Header = loc.Get("dynmap.tab.shapes");
        TabCapacity.Header = loc.Get("dynmap.tab.color");

        // 右键菜单：新建/复制/删除/重命名
        var menu = new ContextMenu();
        var addItem = new MenuItem { Header = loc.Get("dynmap.add_static") };
        addItem.Click += (_, _) => AddMap();
        var copyItem = new MenuItem { Header = loc.Get("dynmap.copy") };
        copyItem.Click += (_, _) => CopyMap();
        var renameItem = new MenuItem { Header = loc.Get("dynmap.rename") };
        renameItem.Click += (_, _) => RenameMap();
        var deleteItem = new MenuItem { Header = loc.Get("dynmap.delete") };
        deleteItem.Click += (_, _) => DeleteMap();
        menu.Items.Add(addItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(renameItem);
        menu.Items.Add(deleteItem);
        StaticMapList.ContextMenu = menu;

        // 形状总表选中行 → 预览渲染该样式
        ShapeListBox.SelectionChanged += (_, _) =>
        {
            if (ShapeListBox.SelectedItem is ShapeRowItem row)
            {
                _currentPreviewShape = row.Name;
                DrawPreview();
            }
        };

        // 预览区 = 恒星点编辑区：左键选中/Shift加选/框选，右键菜单
        PreviewCanvas.MouseLeftButtonDown += OnCanvasLeftDown;
        PreviewCanvas.MouseLeftButtonUp += OnCanvasLeftUp;
        PreviewCanvas.MouseRightButtonDown += OnCanvasRightDown;
        PreviewCanvas.MouseRightButtonUp += OnCanvasRightUp;
        PreviewCanvas.MouseDown += OnCanvasMiddleDown;
        PreviewCanvas.MouseUp += OnCanvasMiddleUp;
        PreviewCanvas.MouseMove += OnCanvasMouseMove;
        PreviewCanvas.MouseWheel += OnCanvasMouseWheel;
        PreviewCanvas.MouseLeave += (_, _) => PreviewCanvas.Cursor = System.Windows.Input.Cursors.Cross;
        // 键盘：ESC 退出编辑模式；Ctrl+C 复制；Ctrl+V 粘贴（焦点在画布时生效）
        PreviewKeyDown += OnPagePreviewKeyDown;

        // 底部固定按钮：全部规整化 / 保存（文本与样式页统一）
        MapNormalizeButton.Content = loc.Get("style.normalize_all");
        MapSaveButton.Content = loc.Get("style.save_all");

        ReloadMaps();
        // 读取点精度（银河类别 galaxy.json global.behavior.point_precision；未设置默认 1）
        try
        {
            var cm = _services.ConfigManager;
            if (cm != null)
            {
                var pv = cm.Get("galaxy", "global.behavior.point_precision");
                if (pv is int pi)
                    _pointPrecision = Math.Clamp(pi, 0, 3);
                else if (pv is long pl)
                    _pointPrecision = Math.Clamp((int)pl, 0, 3);
            }
        }
        catch
        {
            // 默认 1
        }
        // 自动选中第一个静态地图（混排列表：不能选第一项——若第一项是动态地图会触发切回动态页）
        foreach (object o in StaticMapList.Items)
        {
            if (o is MapListItem item && item.IsStatic)
            {
                StaticMapList.SelectedItem = item;
                break;
            }
        }
    }

    /// <summary>动态地图页切换过来时选中指定静态地图（输出接口）。</summary>
    public void SetMap(string mapName)
    {
        ReloadMaps(); // 刷新列表（可能刚新建/删除了地图，旧列表不含该项）
        foreach (object o in StaticMapList.Items)
        {
            if (o is MapListItem item && item.Name == mapName)
            {
                StaticMapList.SelectedItem = item;
                var it = item;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => StaticMapList.ScrollIntoView(it)));
                return;
            }
        }
    }

    // ==================== 列表 ====================

    private void ReloadMaps()
    {
        StaticMapList.Items.Clear();
        var engine = _services.MapEngine;
        if (engine == null)
            return;
        // 动态+静态混合（与动态页同列表）；本地化显示
        var items = new List<MapListItem>();
        foreach (var name in engine.DynamicScenarios.Keys)
            items.Add(new MapListItem(name, isStatic: false, engine.GetDynamicScenario(name)!.Priority, LocMapName(name), engine.GetDynamicScenario(name)!.NumStars));
        foreach (var name in engine.StaticScenarios.Keys)
            items.Add(new MapListItem(name, isStatic: true, engine.GetStaticScenario(name)!.Priority, LocMapName(name), engine.GetStaticScenario(name)!.Systems.Count));
        foreach (var item in items.OrderBy(i => i.Priority).ThenBy(i => i.Name, StringComparer.Ordinal))
            _allMapItems.Add(item);
        ApplyMapFilter(keepSelection: false);
    }

    /// <summary>列表搜索按钮：按输入值过滤（匹配 Name 键 或 本地化显示名，忽略大小写）。</summary>
    /// <summary>列表搜索框回车：普通回车 → 触发搜索；Shift+回车 → 插入 \\n（统一）。</summary>
    private void OnFilterBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
            {
                e.Handled = true;
                var box = (System.Windows.Controls.TextBox)sender;
                var idx = box.CaretIndex;
                box.Text = box.Text.Insert(idx, "\\n");
                box.CaretIndex = idx + 2;
                return;
            }
            e.Handled = true;
            _mapDebounce.Stop();   // 手动搜索后停止防抖计时器（防 2 秒后重复触发）
            OnMapFilterSearch(this, new RoutedEventArgs());
        }
    }

    private void OnMapFilterSearch(object sender, RoutedEventArgs e)
    {
        ApplyMapFilter(keepSelection: true);
    }

    /// <summary>应用地图列表过滤；输入为空时恢复全部。keepSelection=true 时按当前选中键找回选中。</summary>
    private void ApplyMapFilter(bool keepSelection)
    {
        string? keepName = null;
        if (keepSelection && StaticMapList.SelectedItem is MapListItem cur)
            keepName = cur.Name;

        StaticMapList.Items.Clear();
        var pat = MapFilterBox?.Text?.Trim();
        foreach (var item in _allMapItems)
        {
            if (string.IsNullOrEmpty(pat)
                || item.Name.Contains(pat, StringComparison.OrdinalIgnoreCase)
                || item.Display.Contains(pat, StringComparison.OrdinalIgnoreCase))
                StaticMapList.Items.Add(item);
        }
        if (keepName != null)
        {
            for (int i = 0; i < StaticMapList.Items.Count; i++)
            {
                if (StaticMapList.Items[i] is MapListItem mli && mli.Name == keepName)
                {
                    StaticMapList.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    /// <summary>地图键 → 本地化显示名（按当前界面语言；无本地化回退键）。</summary>
    private string LocMapName(string mapKey)
    {
        var adapter = _services.Adapter;
        var loc = _services.Localisation;
        if (adapter == null)
            return mapKey;
        return adapter.GetLocalisedText(mapKey, MapUiLangToModLang(loc.CurrentLanguage)) ?? mapKey;
    }

    private void OnMapSelected(object sender, SelectionChangedEventArgs e)
    {
        if (StaticMapList.SelectedItem is not MapListItem item)
            return;
        // 动态地图 → 切回动态地图页；拖拽排序期间延迟并跳过（否则按下瞬间切换中断拖拽）
        if (!item.IsStatic)
        {
            var name = item.Name;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { if (!_dragging) DynamicMapRequested?.Invoke(this, name); }));
            return;
        }
        _currentMap = item.Name;
        // 确保选中项可见（长列表滚动到该项）
        var it = item;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => StaticMapList.ScrollIntoView(it)));
        BuildForms();
        DrawPreview();
    }

    // ==================== 表单 ====================

    private void BuildForms()
    {
        try
        {
            BuildFormsCore();
        }
        catch (Exception ex)
        {
            try { Diag($"[BuildForms] {ex}"); } catch { }
            MessageBox.Show($"StaticMapPage.BuildForms failed: {ex.Message}", "Stellaris Mod Tools");
        }
    }

    private void BuildFormsCore()
    {
        BasicPanel.Children.Clear();
        OtherPanel.Children.Clear();
        ShapeListBox.Items.Clear();
        ColorPanel.Children.Clear();

        var engine = _services.MapEngine;
        if (engine == null || _currentMap == null)
            return;
        var loc = _services.Localisation;
        var stat = engine.GetStaticScenario(_currentMap);
        if (stat == null)
            return;

        // ---- 参数页（静态精简 + 生成形状占位符）----
        AddEmpireFields(BasicPanel, stat.NumEmpires, stat.NumEmpireDefault,
            (mn, mx, def) => { stat.NumEmpires = new IntRange(mn, mx); stat.NumEmpireDefault = def; });
        BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.advanced_empire"), stat.AdvancedEmpireDefault.ToString(),
            v => { stat.AdvancedEmpireDefault = ParseInt(v, stat.AdvancedEmpireDefault); }));
        BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.fallen_empire"), stat.FallenEmpireDefault.ToString(),
            v => { stat.FallenEmpireDefault = ParseInt(v, stat.FallenEmpireDefault); }));
        BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.marauder_empire"), stat.MarauderEmpireDefault.ToString(),
            v => { stat.MarauderEmpireDefault = ParseInt(v, stat.MarauderEmpireDefault); }));
        BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.nomad_empire"), stat.NomadEmpireDefault.ToString(),
            v => { stat.NomadEmpireDefault = ParseInt(v, stat.NomadEmpireDefault); }));
        BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.planet_odds"), stat.ColonizablePlanetOdds.ToString("0.##"),
            v => { stat.ColonizablePlanetOdds = ParseDouble(v, stat.ColonizablePlanetOdds); }));
        BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.primitive_odds"), stat.PrimitiveOdds.ToString("0.##"),
            v => { stat.PrimitiveOdds = ParseDouble(v, stat.PrimitiveOdds); }));
        BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.crisis"), stat.CrisisStrength.ToString("0.##"),
            v => { stat.CrisisStrength = ParseDouble(v, stat.CrisisStrength); }));
        BasicPanel.Children.Add(BuildRangeRow(loc.Get("dynmap.wormhole"), stat.NumWormholePairs, stat.NumWormholePairsDefault,
            (mn, mx, dv) => { stat.NumWormholePairs = new IntRange(mn, mx); stat.NumWormholePairsDefault = dv; }));
        BasicPanel.Children.Add(BuildRangeRow(loc.Get("dynmap.gateways"), stat.NumGateways, stat.NumGatewaysDefault,
            (mn, mx, dv) => { stat.NumGateways = new IntRange(mn, mx); stat.NumGatewaysDefault = dv; }));
        BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.core_radius"), stat.CoreRadiusPerc.ToString("0.###"),
            v => { stat.CoreRadiusPerc = ParseDouble(v, stat.CoreRadiusPerc); }));
        var rhLabel = new TextBlock
        {
            Text = loc.Get("dynmap.random_hyperlanes"),
            Width = 170,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var rhCheck = new CheckBox
        {
            IsChecked = stat.RandomHyperlanes,
            Margin = new Thickness(4, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        rhCheck.Checked += (_, _) => stat.RandomHyperlanes = true;
        rhCheck.Unchecked += (_, _) => stat.RandomHyperlanes = false;
        var rhRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        rhRow.Children.Add(rhLabel);
        rhRow.Children.Add(rhCheck);
        BasicPanel.Children.Add(rhRow);
        BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.system_count"), stat.Systems.Count.ToString(), _ => { }));

        // 生成形状占位符（为静态地图注册占位样式，供预览/选择）
        // 绑定样式与核心半径比例已移至"其他"选项卡


        // ---- 其他页：地图键（重命名）+ 地图名本地化 ----
        BuildOtherPanel(stat);

        // ---- 形状页：样式总表（勾选，静态无容量）----
        BuildShapePanel(stat);

        // ---- 颜色页 ----
        BuildColorPanel();
    }

    private void BuildOtherPanel(StaticScenario stat)
    {
        var engine = _services.MapEngine!;
        var loc = _services.Localisation;
        string currentMapKey = stat.Name;

        OtherPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.map_key"), stat.Name, v =>
        {
            string newKey = v.Trim();
            if (newKey.Length == 0 || newKey == currentMapKey)
                return;
            if (engine.GetStaticScenario(newKey) != null)
                return;
            // 同步改静态字典 + 占位样式 key + 内存映射
            engine.RenameStaticScenario(currentMapKey, newKey);
            ReloadMaps();
            SelectMap(newKey);
        }));

        // 统一本地化组件（LocalisationEditBox）：边框 + 语种下拉 + 名称/描述（逻辑值可编辑 → 显示值只读）
        var locBox = new LocalisationEditBox
        {
            Adapter = _services.Adapter,
            GetNameKey = () => currentMapKey,
            GetLangs = () => _services.StyleEngine?.GetEnabledLanguages() ?? new List<string> { "english" },
            SaveLocalisation = (lang, key, value) => UpdateMapLocalisation(lang, key, value)
        };
        locBox.Reload();
        OtherPanel.Children.Add(locBox);

        // ---- 绑定样式 + 核心半径比例（静态地图专用）----
        var boundCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
        boundCombo.Items.Add(new ComboBoxItem { Content = loc.Get("staticmap.bind_style_none"), Tag = null });
        foreach (var styleName in _services.StyleEngine?.GetAllStyleNames() ?? new List<string>())
            boundCombo.Items.Add(new ComboBoxItem { Content = styleName, Tag = styleName });
        string? currentBound = engine.GetBoundStyle(stat.Name);
        foreach (object o in boundCombo.Items)
        {
            if (o is ComboBoxItem it && it.Tag is string ts && ts == currentBound)
            {
                boundCombo.SelectedItem = it;
                break;
            }
        }
        if (currentBound == null)
            boundCombo.SelectedIndex = 0;
        boundCombo.SelectionChanged += (_, _) =>
        {
            if (boundCombo.SelectedItem is ComboBoxItem it && it.Tag is string chosen)
                engine.SetBoundStyle(stat.Name, chosen);
            else if (boundCombo.SelectedItem is ComboBoxItem none && none.Tag == null)
                engine.SetBoundStyle(stat.Name, null);
            // 形状页只勾选绑定的样式
            BuildShapePanel(stat);
            DrawPreview();
        };
        OtherPanel.Children.Add(BuildLocControlRow(loc.Get("staticmap.bind_style"), boundCombo));

        // 核心半径比例（影响伪样式的核心半径，仅内存；保存写 galaxy.json）
        var coreRadiusBox = new TextBox
        {
            Text = stat.CoreRadiusPerc.ToString("0.##"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        coreRadiusBox.LostFocus += (_, _) =>
        {
            if (double.TryParse(coreRadiusBox.Text, out double v))
            {
                stat.CoreRadiusPerc = Math.Clamp(v, 0.0, 1.0);
                coreRadiusBox.Text = stat.CoreRadiusPerc.ToString("0.##");
            }
            else
            {
                coreRadiusBox.Text = stat.CoreRadiusPerc.ToString("0.##");
            }
        };
        OtherPanel.Children.Add(BuildLocControlRow(loc.Get("staticmap.core_radius"), coreRadiusBox));

        // ---- 锁定本地化 / 清空本地化 ----
        var lockCheck = new CheckBox
        {
            IsChecked = stat.LockLocalisation,
            Margin = new Thickness(0, 6, 0, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        lockCheck.Checked += (_, _) => stat.LockLocalisation = true;
        lockCheck.Unchecked += (_, _) => stat.LockLocalisation = false;
        OtherPanel.Children.Add(BuildRingRow(loc.Get("staticmap.lock_localisation"), lockCheck));
        var clearCheck = new CheckBox
        {
            IsChecked = stat.ClearFile,
            Margin = new Thickness(0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        clearCheck.Checked += (_, _) => stat.ClearFile = true;
        clearCheck.Unchecked += (_, _) => stat.ClearFile = false;
        OtherPanel.Children.Add(BuildRingRow(loc.Get("staticmap.clear_file"), clearCheck));

        // ---- 生成预览 / 生成图标（绑定样式的静态点集渲染——与样式页一致：仅失败弹窗）----
        var exportRow = new DockPanel { Margin = new Thickness(0, 12, 0, 4) };
        var previewBtn = new Button
        {
            Content = loc.Get("staticmap.export_preview"),
            Width = 110,
            Margin = new Thickness(0, 0, 8, 0)
        };
        previewBtn.Click += (_, _) =>
        {
            string? bound = engine.GetBoundStyle(stat.Name);
            if (string.IsNullOrEmpty(bound))
            {
                MessageBox.Show(loc.Get("staticmap.bind_style_required"), "Stellaris Mod Tools",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // 手动预览 = 设计点集渲染（显示静态地图设计的恒星点）——临时设置点集，导出后清除（不残留）
            var pts = stat.Systems
                .Select(p => new System.Numerics.Vector2((float)p.Position.X, (float)p.Position.Y)).ToList();
            _services.StyleEngine?.SetStaticPointOverride(bound, pts);
            var st = _services.StyleEngine?.ExportSinglePreview(bound);
            _services.StyleEngine?.ClearStaticPointOverrides();
            if (st != null && st != Stellaris.Engine.ImageAsset.OperationStatus.Success)
                MessageBox.Show($"{loc.Get("staticmap.export_preview")}: {st}", "Stellaris Mod Tools",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        var iconBtn = new Button
        {
            Content = loc.Get("staticmap.export_icon"),
            Width = 110
        };
        iconBtn.Click += (_, _) =>
        {
            string? bound = engine.GetBoundStyle(stat.Name);
            if (string.IsNullOrEmpty(bound))
            {
                MessageBox.Show(loc.Get("staticmap.bind_style_required"), "Stellaris Mod Tools",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // 手动图标 = 设计点集渲染——临时设置点集，导出后清除（不残留）
            var pts = stat.Systems
                .Select(p => new System.Numerics.Vector2((float)p.Position.X, (float)p.Position.Y)).ToList();
            _services.StyleEngine?.SetStaticPointOverride(bound, pts);
            var st = _services.StyleEngine?.ExportSingleIcon(bound);
            _services.StyleEngine?.ClearStaticPointOverrides();
            if (st != null && st != Stellaris.Engine.ImageAsset.OperationStatus.Success)
                MessageBox.Show($"{loc.Get("staticmap.export_icon")}: {st}", "Stellaris Mod Tools",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        exportRow.Children.Add(previewBtn);
        exportRow.Children.Add(iconBtn);
        OtherPanel.Children.Add(exportRow);

        // 危机强度扩展列表（大型框，自动从小到大排序，右键添加/删除）
        var crisis = stat.ExtraCrisisStrength;
        var crisisTitle = new TextBlock
        {
            Text = loc.Get("dynmap.crisis_extra"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 4)
        };
        OtherPanel.Children.Add(crisisTitle);
        var crisisList = new ListBox { Height = 220, Margin = new Thickness(0, 0, 0, 4) };
        void RefreshCrisis()
        {
            crisisList.Items.Clear();
            foreach (var v in crisis.OrderBy(x => x))
                crisisList.Items.Add(v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        RefreshCrisis();

        var crisisMenu = new ContextMenu();
        var addCrisisItem = new MenuItem { Header = loc.Get("dynmap.add") };
        addCrisisItem.Click += (_, _) =>
        {
            var dlg = new Window
            {
                Title = loc.Get("dynmap.crisis_extra"),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Owner = Window.GetWindow(this)
            };
            var panel2 = new StackPanel { Margin = new Thickness(14) };
            double extFont = _services.Preferences.FontSize > 0 ? _services.Preferences.FontSize : 12;
            var valueBox = new TextBox { FontSize = extFont, MaxLength = 12, Width = extFont * 7.2 + 8 };
            var okBtn = new Button
            {
                Content = loc.Get("roots.ok"),
                Width = 80,
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true,
                FontSize = extFont
            };
            okBtn.Click += (_, _) => dlg.DialogResult = true;
            panel2.Children.Add(valueBox);
            panel2.Children.Add(okBtn);
            dlg.Content = panel2;
            if (dlg.ShowDialog() == true
                && double.TryParse(valueBox.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double nv))
            {
                crisis.Add(nv);
                RefreshCrisis();
            }
        };
        var removeCrisisItem = new MenuItem { Header = loc.Get("dynmap.remove") };
        removeCrisisItem.Click += (_, _) =>
        {
            if (crisisList.SelectedItem is string s
                && double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double rv))
            {
                crisis.Remove(rv);
                RefreshCrisis();
            }
        };
        crisisMenu.Items.Add(addCrisisItem);
        crisisMenu.Items.Add(removeCrisisItem);
        crisisList.ContextMenu = crisisMenu;
        OtherPanel.Children.Add(crisisList);
    }

    /// <summary>动态地图名本地化文件约定：localisation/{lang}/{modPrefix}_map_l_{lang}.yml。</summary>
    private string MapLocalisationFile(string lang)
    {
        string prefix = _services.ModPrefs?.ModPrefix ?? "smt";
        return $"localisation/{lang}/{prefix}_map_l_{lang}.yml";
    }

    /// <summary>更新地图名/描述本地化（内存）：键已存在（任意文件）→ 更新其当前位置；不存在 → 写入约定文件；随后重算显示值。</summary>
    private void UpdateMapLocalisation(string lang, string key, string value)
    {
        var adapter = _services.Adapter;
        if (adapter == null)
            return;
        try
        {
            var idx = adapter.GetLocalisationKeyFiles(lang);
            if (idx.TryGetValue(key, out var file) && file != null)
                adapter.UpdateLocalisationEntry(lang, file, key, value);
            else
                adapter.AddLocalisationEntry(lang, MapLocalisationFile(lang), key, value);
            adapter.ExpandLocalisationKey(lang, key);
            // 同步占位样式本地化缓存（形状那边显示新值）
            _services.StyleEngine?.RefreshLocalisationCache();
        }
        catch
        {
            // 写入失败忽略（下次仍按未设置处理）
        }
    }

    // ---- 形状总表 ----

    private void BuildShapePanel(StaticScenario stat)
    {
        ShapeListBox.Items.Clear();
        var engine = _services.MapEngine!;
        var styleEngine = _services.StyleEngine;

        var allStyles = styleEngine?.GetAllStyleNames() ?? new List<string>();
        var supportedSet = new HashSet<string>(engine.GetShapeOrder(stat.Name), StringComparer.Ordinal);

        // 形状页勾选 = 完全按 SupportedShapes（加载的/用户勾选的）——**不强制勾选绑定样式**
        foreach (var shape in allStyles)
        {
            bool isChecked = supportedSet.Contains(shape);
            var row = new ShapeRowItem
            {
                Name = shape,
                Display = LocName(shape),
                CapacityText = string.Empty,
                IsChecked = isChecked
            };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShapeRowItem.IsChecked))
                    CommitShapeChecks();
            };
            ShapeListBox.Items.Add(row);
        }

        var menu = new ContextMenu();
        var checkItem = new MenuItem { Header = _services.Localisation.Get("dynmap.check_shape") };
        checkItem.Click += (_, _) =>
        {
            foreach (var o in ShapeListBox.SelectedItems)
                if (o is ShapeRowItem row)
                    row.IsChecked = true;
            CommitShapeChecks();
        };
        var uncheckItem = new MenuItem { Header = _services.Localisation.Get("dynmap.uncheck_shape") };
        uncheckItem.Click += (_, _) =>
        {
            foreach (var o in ShapeListBox.SelectedItems)
                if (o is ShapeRowItem row)
                    row.IsChecked = false;
            CommitShapeChecks();
        };
        menu.Items.Add(checkItem);
        menu.Items.Add(uncheckItem);
        ShapeListBox.ContextMenu = menu;
    }

    private void CommitShapeChecks()
    {
        var engine = _services.MapEngine!;
        var list = ShapeListBox.Items.Cast<ShapeRowItem>()
            .Where(r => r.IsChecked)
            .Select(r => r.Name)
            .ToList();
        engine.SetShapeOrder(_currentMap!, list);
        DrawPreview();
    }

    // ---- 颜色页 ----

    private void BuildColorPanel()
    {
        ColorPanel.Children.Clear();
        var loc = _services.Localisation;
        ColorPanel.Children.Add(BuildColorRow(loc.Get("style.preview_color"), "shape_color", "#284488CC"));
        ColorPanel.Children.Add(BuildColorRow(loc.Get("style.grid_color"), "grid_color", "#50999999"));
        // 网格间距（可调，存 galaxy.json global.preview.grid_spacing）
        var spacingLabel = new TextBlock { Text = loc.Get("staticmap.grid_spacing"), Width = 120, VerticalAlignment = VerticalAlignment.Center };
        var spacingBox = new TextBox
        {
            Text = GetPreviewGridSpacing().ToString(),
            Width = 70,
            Margin = new Thickness(6, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        spacingBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(spacingBox.Text, out int v) && v >= 4 && v <= 1000)
            {
                SetPreviewGridSpacing(v);
                DrawPreview();
            }
            else
            {
                spacingBox.Text = GetPreviewGridSpacing().ToString();
            }
        };
        var spacingRow = new DockPanel { Margin = new Thickness(2, 4, 2, 4) };
        spacingRow.Children.Add(spacingLabel);
        spacingRow.Children.Add(spacingBox);
        ColorPanel.Children.Add(spacingRow);
    }

    private int GetPreviewGridSpacing()
    {
        var cm = _services.ConfigManager;
        if (cm != null)
        {
            try
            {
                var v = cm.Get("galaxy", "global.preview.grid_spacing");
                if (v is int iv && iv >= 4 && iv <= 1000) return iv;
            }
            catch { }
        }
        return 50;
    }

    private void SetPreviewGridSpacing(int spacing)
    {
        var cm = _services.ConfigManager;
        cm?.Set("galaxy", "global.preview.grid_spacing", spacing);
    }

    private FrameworkElement BuildColorRow(string labelText, string colorKey, string fallbackHex)
    {
        var label = new TextBlock { Text = labelText, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        var picker = new Controls.ColorPickerControl
        {
            Title = labelText,
            SelectedColorText = GetPreviewColor(colorKey, fallbackHex),
            Margin = new Thickness(6, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        picker.ApplyLocalisation(_services.Localisation);
        picker.ColorChanged += (_, hex) =>
        {
            SetPreviewColor(colorKey, hex);
            DrawPreview();
        };
        var row = new DockPanel { Margin = new Thickness(2, 4, 2, 4) };
        row.Children.Add(label);
        row.Children.Add(picker);
        return row;
    }

    private string GetPreviewColor(string key, string fallbackHex)
    {
        var cm = _services.ConfigManager;
        if (cm == null)
            return fallbackHex;
        try
        {
            var v = cm.Get("galaxy", "global.preview." + key);
            if (v is System.Text.Json.Nodes.JsonArray arr && arr.Count >= 4)
            {
                int[] rgba = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    if (arr[i] is not System.Text.Json.Nodes.JsonValue jv || !jv.TryGetValue<int>(out int x))
                        return fallbackHex;
                    rgba[i] = x;
                }
                return $"#{rgba[3]:X2}{rgba[0]:X2}{rgba[1]:X2}{rgba[2]:X2}";
            }
        }
        catch { }
        return fallbackHex;
    }

    private void SetPreviewColor(string key, string hex)
    {
        var cm = _services.ConfigManager;
        if (cm == null)
            return;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex.Trim())!;
            cm.SetBatch("galaxy", new Dictionary<string, object>
            {
                ["global.preview." + key] = new[] { (int)c.R, (int)c.G, (int)c.B, (int)c.A }
            });
        }
        catch { }
    }

    // ==================== 预览 ====================

    private void DrawPreview()
    {
        try
        {
            DrawPreviewCore();
        }
        catch (Exception ex)
        {
            try { Diag($"[DrawPreview] {ex}"); } catch { }
            MessageBox.Show($"StaticMapPage.DrawPreview failed: {ex.Message}", "Stellaris Mod Tools");
        }
    }

    private void DrawPreviewCore()
    {
        PreviewCanvas.Children.Clear();
        var engine = _services.MapEngine;
        if (engine == null || _currentMap == null)
            return;
        var loc = _services.Localisation;
        var stat = engine.GetStaticScenario(_currentMap);
        if (stat == null)
            return;

        PreviewTitle.Text = LocMapName(_currentMap);
        double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
        double cx = side / 2.0, cy = side / 2.0;

        // 网格范围 = 可用范围：固定 ±250 / 缩放（静态默认坐标范围，不随内部内容变化；无圈）
        double span = 500 / _zoom;

        double MapX(double v) => cx + (v - _panX) / span * (side / 2.0 - 4);
        double MapY(double v) => cy - (v - _panY) / span * (side / 2.0 - 4);

        // 正方形网格：间距 = 用户配置（默认 50 坐标单位），范围 = 可见区域（封顶 ±500）；±500 边界线用红色
        double gridSpacing = GetPreviewGridSpacing();
        var gridBase = (TryParseHex(GetPreviewColor("grid_color", "#50999999"))
                        ?? new SolidColorBrush(Color.FromArgb(0x50, 0x99, 0x99, 0x99))).Color;
        var gridBrush = new SolidColorBrush(Color.FromArgb((byte)(gridBase.A / 3), gridBase.R, gridBase.G, gridBase.B));
        var boundBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x40, 0x40));
        // 可见区域（坐标单位）
        double visL = _panX - span, visR = _panX + span;
        double visB = _panY - span, visT = _panY + span;
        // 竖线（X 固定）：可见左右范围内，线长 = 可见上下（红线画满 ±500）
        for (double g = Math.Max(-500, Math.Floor(visL / gridSpacing) * gridSpacing); g <= Math.Min(500, visR) + 0.01; g += gridSpacing)
        {
            bool isBound = Math.Abs(g) >= 499.99;
            var stroke = isBound ? boundBrush : gridBrush;
            double th = isBound ? 1.2 : 0.5;
            double y0 = isBound ? MapY(-500) : MapY(Math.Max(-500, visB));
            double y1 = isBound ? MapY(500) : MapY(Math.Min(500, visT));
            PreviewCanvas.Children.Add(new Line { X1 = MapX(g), Y1 = y0, X2 = MapX(g), Y2 = y1, Stroke = stroke, StrokeThickness = th });
        }
        // 横线（Y 固定）：可见上下范围内，线长 = 可见左右（红线画满 ±500）
        for (double g = Math.Max(-500, Math.Floor(visB / gridSpacing) * gridSpacing); g <= Math.Min(500, visT) + 0.01; g += gridSpacing)
        {
            bool isBound = Math.Abs(g) >= 499.99;
            var stroke = isBound ? boundBrush : gridBrush;
            double th = isBound ? 1.2 : 0.5;
            double x0 = isBound ? MapX(-500) : MapX(Math.Max(-500, visL));
            double x1 = isBound ? MapX(500) : MapX(Math.Min(500, visR));
            PreviewCanvas.Children.Add(new Line { X1 = x0, Y1 = MapY(g), X2 = x1, Y2 = MapY(g), Stroke = stroke, StrokeThickness = th });
        }

        // 显示航道（Hyperlane：按 Id 找两端星系坐标连线）
        var laneBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0x88, 0x66, 0xAA));
        var laneById = stat.Systems.ToDictionary(s => s.Id, StringComparer.Ordinal);
        int drawnLanes = 0, skippedLanes = 0;
        foreach (var lane in stat.Hyperlanes)
        {
            if (!laneById.TryGetValue(lane.From, out var f) || !laneById.TryGetValue(lane.To, out var t))
            {
                skippedLanes++;
                continue;
            }
            drawnLanes++;
            bool laneSel = _selectedLanes.Contains(lane);
            PreviewCanvas.Children.Add(new Line
            {
                X1 = MapX(f.Position.X), Y1 = MapY(f.Position.Y),
                X2 = MapX(t.Position.X), Y2 = MapY(t.Position.Y),
                Stroke = laneSel ? System.Windows.Media.Brushes.Gold : laneBrush,
                StrokeThickness = laneSel ? 2.6 : 1.2
            });
        }
        try
        {
            Diag($"[DrawPreview] {_currentMap}: 引擎航道 {stat.Hyperlanes.Count} 条，画了 {drawnLanes} 条，跳过 {skippedLanes} 条");
            if (skippedLanes > 0)
            {
                string sampleSys = string.Join(",", stat.Systems.Take(3).Select(s => s.Id));
                string sampleLane = string.Join(",", stat.Hyperlanes.Take(3).Select(l => l.From + "->" + l.To));
                Diag($"[DrawPreview] 系统Id样例 [{sampleSys}]  航道样例 [{sampleLane}]");
            }
        }
        catch { }

        // 图形（正三角形/矩形/正六边形/圆）：轮廓 + 半透明填充；选中金色边
        foreach (var shape in _shapes)
        {
            var locs = shape.GetLocators();
            bool shapeSel = _selectedShapes.Contains(shape);
            var shapeColor = GetPreviewColor("shape_color", "#284488CC");
            var shapeBrush = TryParseHex(shapeColor) ?? new SolidColorBrush(Color.FromArgb(0xFF, 0x28, 0x44, 0x88));
            if (shape.Kind == ShapeKind.Circle)
            {
                // 圆：椭圆轮廓（半径按画布缩放）
                var rPx = MapX(shape.Center.X + shape.OuterRadius) - MapX(shape.Center.X);
                var rPy = MapY(shape.Center.Y) - MapY(shape.Center.Y + shape.OuterRadius);
                double rr = Math.Max(1, Math.Min(Math.Abs(rPx), Math.Abs(rPy)));
                var ell = new Ellipse
                {
                    Width = rr * 2,
                    Height = rr * 2,
                    Stroke = shapeSel ? System.Windows.Media.Brushes.Gold : shapeBrush,
                    Fill = shapeSel
                        ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xC8, 0x00))
                        : new SolidColorBrush(Color.FromArgb(0x2A, shapeBrush.Color.R, shapeBrush.Color.G, shapeBrush.Color.B)),
                    StrokeThickness = shapeSel ? 2.6 : 1.6
                };
                Canvas.SetLeft(ell, MapX(shape.Center.X) - rr);
                Canvas.SetTop(ell, MapY(shape.Center.Y) - rr);
                PreviewCanvas.Children.Add(ell);
            }
            else
            {
                var pts = new PointCollection();
                foreach (var l in locs.Take(shape.VertexCount))
                    pts.Add(new Point(MapX(l.X), MapY(l.Y)));
                var poly = new Polygon
                {
                    Points = pts,
                    Stroke = shapeSel ? System.Windows.Media.Brushes.Gold : shapeBrush,
                    Fill = shapeSel
                        ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xC8, 0x00))
                        : new SolidColorBrush(Color.FromArgb(0x2A, shapeBrush.Color.R, shapeBrush.Color.G, shapeBrush.Color.B)),
                    StrokeThickness = shapeSel ? 2.6 : 1.6
                };
                PreviewCanvas.Children.Add(poly);
            }
            // 定位点：亮"选中的定位点"（_selLocatorShape 且索引匹配；-1 默认亮中心）；其余灰点
            var centerLoc = locs[^1];
            for (int li = 0; li < locs.Count; li++)
            {
                var l = locs[li];
                bool isCenter = li == locs.Count - 1;
                // 默认（未选定位点）亮中心；选中定位点后亮该点（可切换）
                bool lit;
                if (_selLocatorShape == null)
                    lit = shapeSel && isCenter;
                else
                    lit = shapeSel && ReferenceEquals(_selLocatorShape, shape) && _selLocatorIndex == li;
                var dot = new Ellipse
                {
                    Width = lit ? 8 : 5,
                    Height = lit ? 8 : 5,
                    Fill = lit ? System.Windows.Media.Brushes.Gold : System.Windows.Media.Brushes.LightGray,
                    Stroke = System.Windows.Media.Brushes.White,
                    StrokeThickness = 1
                };
                Canvas.SetLeft(dot, MapX(l.X) - (lit ? 4 : 2.5));
                Canvas.SetTop(dot, MapY(l.Y) - (lit ? 4 : 2.5));
                PreviewCanvas.Children.Add(dot);
            }
        }

        // 图像覆盖层（渲染图片；选中金色边框）
        foreach (var img in _images)
        {
            if (!System.IO.File.Exists(img.Path))
                continue;
            using var ibmp = SkiaSharp.SKBitmap.Decode(img.Path);
            if (ibmp == null)
                continue;
            double wPx = Math.Abs(MapX(img.Position.X + img.Width / 2.0) - MapX(img.Position.X - img.Width / 2.0));
            double hPx = Math.Abs(MapY(img.Position.Y) - MapY(img.Position.Y + img.Height));
            if (wPx < 1 || hPx < 1)
                continue;
            var bmpSrc = ImageToBitmapSource(ibmp);
            var imgCtrl = new System.Windows.Controls.Image
            {
                Source = bmpSrc,
                Width = wPx,
                Height = hPx,
                Stretch = System.Windows.Media.Stretch.Fill
            };
            System.Windows.Controls.Canvas.SetLeft(imgCtrl, MapX(img.Position.X) - wPx / 2.0);
            System.Windows.Controls.Canvas.SetTop(imgCtrl, MapY(img.Position.Y) - hPx / 2.0);
            if (img.Angle != 0)
            {
                imgCtrl.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                imgCtrl.RenderTransform = new System.Windows.Media.RotateTransform(img.Angle);
            }
            PreviewCanvas.Children.Add(imgCtrl);
            if (_selectedImages.Contains(img))
            {
                var border = new System.Windows.Shapes.Rectangle
                {
                    Width = wPx, Height = hPx,
                    Stroke = System.Windows.Media.Brushes.Gold,
                    StrokeThickness = 2.0
                };
                System.Windows.Controls.Canvas.SetLeft(border, MapX(img.Position.X) - wPx / 2.0);
                System.Windows.Controls.Canvas.SetTop(border, MapY(img.Position.Y) - hPx / 2.0);
                PreviewCanvas.Children.Add(border);
            }
        }

        // 显示恒星点（系统坐标）；选中点加金色外圈
        var starBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x33, 0x66, 0xCC));
        var selBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC8, 0x00));
        foreach (var sys in stat.Systems)
        {
            double px = MapX(sys.Position.X), py = MapY(sys.Position.Y);
            PreviewCanvas.Children.Add(new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = starBrush
            });
            Canvas.SetLeft(PreviewCanvas.Children[^1], px - 2.5);
            Canvas.SetTop(PreviewCanvas.Children[^1], py - 2.5);
            if (_selected.Contains(sys))
            {
                PreviewCanvas.Children.Add(new Ellipse
                {
                    Width = 11,
                    Height = 11,
                    Stroke = selBrush,
                    StrokeThickness = 1.5,
                    Fill = Brushes.Transparent
                });
                Canvas.SetLeft(PreviewCanvas.Children[^1], px - 5.5);
                Canvas.SetTop(PreviewCanvas.Children[^1], py - 5.5);
            }
        }
    }

    private string Loc(string key) =>
        _services.Localisation.Get(key) ?? key;

    /// <summary>画布坐标 → 静态坐标（预览区编辑用）。</summary>
    private (double X, double Y) ToMapCoords(Point p)
    {
        double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
        double cx = side / 2.0, cy = side / 2.0;
        double span = 500 / _zoom;
        double half = side / 2.0 - 4;
        return ((p.X - cx) / half * span + _panX, -(p.Y - cy) / half * span + _panY);
    }

    /// <summary>双轴镜像：先做一次 X 轴镜像复制，再做一次 Y 轴镜像复制（对源 + X 副本），最后去重合并。</summary>
    private void MirrorDual()
    {
        if (_currentMap == null) return;
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap);
        if (stat == null) return;
        var sources = _selected.Count > 0 ? _selected.ToList() : stat.Systems.ToList();
        if (sources.Count == 0)
            return;
        // 第一次：X 轴镜像复制（Y 取反）
        var map1 = new Dictionary<string, string>(StringComparer.Ordinal);
        var batch1 = new List<SystemEntry>();
        foreach (var s in sources)
        {
            var c = AddSystemAt((float)s.Position.X, (float)(-s.Position.Y));
            c.Name = s.Name;
            c.Initializer = s.Initializer;
            map1[s.Id] = c.Id;
            batch1.Add(c);
        }
        CopyLanes(stat, map1);
        // 第二次：Y 轴镜像复制（X 取反，对源 + X 副本）
        var map2 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in sources.Concat(batch1))
        {
            var c = AddSystemAt((float)(-s.Position.X), (float)s.Position.Y);
            c.Name = s.Name;
            c.Initializer = s.Initializer;
            map2[s.Id] = c.Id;
        }
        CopyLanes(stat, map2);
        _editMode = EditMode.None;
        DeduplicateAndMerge();
        DrawPreview();
    }

    /// <summary>按"源 Id → 副本 Id"映射复制航道（源航道两端都有副本 → 副本间对应航道）。</summary>
    private static void CopyLanes(StaticScenario stat, Dictionary<string, string> copyMap)
    {
        foreach (var lane in stat.Hyperlanes.ToList())
        {
            if (copyMap.TryGetValue(lane.From, out var f) && copyMap.TryGetValue(lane.To, out var t))
                stat.Hyperlanes.Add(new Hyperlane(f, t));
        }
    }

    /// <summary>去重合并：坐标（按点精度舍入）相同的星系合并为一个（航道继承给保留点）；重复航道（起点终点相同）只保留一条。</summary>
    private void DeduplicateAndMerge()
    {
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap ?? string.Empty);
        if (stat == null || stat.Systems.Count == 0)
            return;
        double pow = Math.Pow(10, _pointPrecision);
        var keep = new Dictionary<(double, double), SystemEntry>();
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal); // 删除点 Id → 保留点 Id
        foreach (var s in stat.Systems.ToList())
        {
            var key = (Math.Round(s.Position.X * pow) / pow, Math.Round(s.Position.Y * pow) / pow);
            if (keep.TryGetValue(key, out var existing))
            {
                idMap[s.Id] = existing.Id;
            }
            else
            {
                keep[key] = s;
            }
        }
        if (idMap.Count == 0)
            return;
        // 航道重定向到保留点；自环删除
        foreach (var lane in stat.Hyperlanes.ToList())
        {
            if (idMap.TryGetValue(lane.From, out var nf)) lane.From = nf;
            if (idMap.TryGetValue(lane.To, out var nt)) lane.To = nt;
            if (lane.From == lane.To)
                stat.Hyperlanes.Remove(lane);
        }
        foreach (var s in stat.Systems.ToList())
        {
            if (idMap.ContainsKey(s.Id))
                stat.Systems.Remove(s);
        }
        // 航道去重：同一条（起点+终点相同，含反向）只保留一条
        var seen = new HashSet<(string, string)>();
        foreach (var lane in stat.Hyperlanes.ToList())
        {
            var k1 = (lane.From, lane.To);
            var k2 = (lane.To, lane.From);
            if (seen.Contains(k1) || seen.Contains(k2))
                stat.Hyperlanes.Remove(lane);
            else
                seen.Add(k1);
        }
    }

    /// <summary>镜像复制：保留原位置，为每个目标点生成镜像副本（sx=-1 → X 取反；sy=-1 → Y 取反）；同步复制航道并去重。</summary>
    private void MirrorSystems(int sx, int sy)
    {
        // **镜像是复制**：图形/图像生成副本（位置取反——原保留 + 新副本），与点镜像一致；
        // 旋转（CustomRotate）才是不复制（原地）。
        foreach (var t in _selectedShapes.ToList())
        {
            var ns = CloneShape(t);
            ns.Center.X = t.Center.X * sx;
            ns.Center.Y = t.Center.Y * sy;
            _shapes.Add(ns);
        }
        foreach (var t in _selectedImages.ToList())
        {
            var ni = new ImageOverlay
            {
                Path = t.Path,
                Position = new SystemPosition { X = t.Position.X * sx, Y = t.Position.Y * sy },
                Width = t.Width, Height = t.Height, Angle = t.Angle,
                GenOptions = t.GenOptions.Clone(), AutoLanes = t.AutoLanes
            };
            _images.Add(ni);
        }
        if (_currentMap == null) return;
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap);
        if (stat == null) return;
        var targets = _selected.Count > 0 ? _selected.ToList() : stat.Systems.ToList();
        var copyMap = new Dictionary<string, string>(StringComparer.Ordinal); // 源 Id → 副本 Id
        foreach (var s in targets)
        {
            var sys = AddSystemAt((float)(s.Position.X * sx), (float)(s.Position.Y * sy));
            sys.Name = s.Name;
            sys.Initializer = s.Initializer;
            copyMap[s.Id] = sys.Id;
        }
        // 复制航道：源航道两端都生成过副本 → 副本间建立对应航道
        CopyLanes(stat, copyMap);
        _editMode = EditMode.None;
        DeduplicateAndMerge();
        DrawPreview();
    }

    /// <summary>自定义镜像（旋转阵列）：复制 N 份，每份绕中心旋转固定角度。</summary>
    /// <summary>自定义旋转：选中点绕指定中心旋转指定角度（方向：顺时针/逆时针），不复制。</summary>
    private void CustomRotate()
    {
        if (_currentMap == null)
            return;
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap);
        if (stat == null || stat.Systems.Count == 0)
            return;
        // 无选中 → 默认全选（与镜像一致）
        var targets = _selected.Count > 0 ? _selected.ToList() : stat.Systems.ToList();
        if (targets.Count == 0)
            return;
        double defCx = targets.Average(t => t.Position.X);
        double defCy = targets.Average(t => t.Position.Y);
        // 旋转中心默认 = 共用记忆（用户上次旋转/镜像使用的中心）；无记忆回退选中组中心
        var rotMem = ReadCustomRotateMemory();
        double? rotCx = rotMem.CenterX, rotCy = rotMem.CenterY;

        var win = new Window
        {
            Title = Loc("staticmap.preview.custom_rotate"),
            Width = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        // 旋转的角度/方向单独记忆（custom_rotate）；中心与镜像共用（custom_mirror.center）
        var rotMem2 = ReadCustomRotateMemory();
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.preview.custom_direction") });
        var dirCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 8) };
        dirCombo.Items.Add(new ComboBoxItem { Content = Loc("staticmap.preview.dir_cw"), Tag = true });
        dirCombo.Items.Add(new ComboBoxItem { Content = Loc("staticmap.preview.dir_ccw"), Tag = false });
        dirCombo.SelectedIndex = rotMem2.Clockwise ? 0 : 1;
        panel.Children.Add(dirCombo);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.preview.custom_angle") });
        var angleBox = new TextBox { Text = rotMem2.Angle != 0 ? rotMem2.Angle.ToString("0.##") : "30", Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(angleBox);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.preview.custom_center_x") });
        var centerXBox = new TextBox { Text = (rotCx ?? defCx).ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(centerXBox);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.preview.custom_center_y") });
        var centerYBox = new TextBox { Text = (rotCy ?? defCy).ToString("0.##"), Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(centerYBox);
        var ok = new Button
        {
            Content = Loc("common.ok"),
            Width = 80,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(ok);
        win.Content = panel;
        if (win.ShowDialog() != true)
            return;
        if (!double.TryParse(angleBox.Text, out double angle) || angle <= 0)
            return;
        if (!double.TryParse(centerXBox.Text, out double ccx) || !double.TryParse(centerYBox.Text, out double ccy))
            return;
        bool clockwise = dirCombo.SelectedItem is ComboBoxItem di && di.Tag is true;
        double sign = clockwise ? -1.0 : 1.0;
        double rad = angle * sign * Math.PI / 180.0;
        double c = Math.Cos(rad), s = Math.Sin(rad);
        foreach (var t in targets)
        {
            double x = t.Position.X - ccx, y = t.Position.Y - ccy;
            t.Position.X = (float)(ccx + x * c - y * s);
            t.Position.Y = (float)(ccy + x * s + y * c);
        }
        // 组合：图形中心、图像位置同步旋转
        foreach (var shape in _selectedShapes)
        {
            double x = shape.Center.X - ccx, y = shape.Center.Y - ccy;
            shape.Center.X = (float)(ccx + x * c - y * s);
            shape.Center.Y = (float)(ccy + x * s + y * c);
        }
        foreach (var img in _selectedImages)
        {
            double x = img.Position.X - ccx, y = img.Position.Y - ccy;
            img.Position.X = (float)(ccx + x * c - y * s);
            img.Position.Y = (float)(ccy + x * s + y * c);
        }
        DrawPreview();
        // 旋转后：角度/方向/中心都存 custom_rotate（各自单独记忆，无共用中心）
        WriteCustomRotateMemory(angle, clockwise, ccx, ccy);
    }

    private sealed class CustomRotateMemory
    {
        public double Angle;
        public bool Clockwise;
        public double? CenterX;
        public double? CenterY;
    }

    private CustomRotateMemory ReadCustomRotateMemory()
    {
        var mem = new CustomRotateMemory { Angle = 0, Clockwise = false };
        var cm = _services.ConfigManager;
        if (cm == null)
            return mem;
        try
        {
            var node = cm.Get("galaxy", "custom_rotate");
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                if (obj["angle"] is System.Text.Json.Nodes.JsonValue av && av.TryGetValue<double>(out double a)) mem.Angle = a;
                if (obj["clockwise"] is System.Text.Json.Nodes.JsonValue wv && wv.TryGetValue<bool>(out bool cw)) mem.Clockwise = cw;
                if (obj["center_x"] is System.Text.Json.Nodes.JsonValue xv && xv.TryGetValue<double>(out double cx)) mem.CenterX = cx;
                if (obj["center_y"] is System.Text.Json.Nodes.JsonValue yv && yv.TryGetValue<double>(out double cy)) mem.CenterY = cy;
            }
        }
        catch { }
        return mem;
    }

    private void WriteCustomRotateMemory(double angle, bool clockwise, double centerX, double centerY)
    {
        var cm = _services.ConfigManager;
        if (cm == null)
            return;
        try
        {
            var dict = new Dictionary<string, object>
            {
                ["angle"] = angle,
                ["clockwise"] = clockwise,
                ["center_x"] = centerX,
                ["center_y"] = centerY
            };
            cm.Set("galaxy", "custom_rotate", dict);
        }
        catch { }
    }

    /// <summary>更新共用旋转中心记忆（保留 custom_mirror 的其他字段）。</summary>
    /// <summary>统计某系统被多少条航道连接（度数）。</summary>
    private int CountLanes(string systemId)
    {
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap!);
        if (stat == null)
            return 0;
        return stat.Hyperlanes.Count(l => l.From == systemId || l.To == systemId);
    }

    /// <summary>融并：把全部选中点（每个都恰好 2 条航道）删除，其两端直接连接。</summary>
    private void MergePoints()
    {
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap!);
        if (stat == null)
            return;
        foreach (var sys in _selected.ToList())
        {
            var lanes = stat.Hyperlanes.Where(l => l.From == sys.Id || l.To == sys.Id).ToList();
            if (lanes.Count != 2)
                continue; // 只处理度数恰为 2 的点（菜单已按此过滤，此处防御）
            var others = lanes.Select(l => l.From == sys.Id ? l.To : l.From).ToList();
            stat.Hyperlanes.RemoveAll(l => l.From == sys.Id || l.To == sys.Id);
            bool already = stat.Hyperlanes.Any(l =>
                (l.From == others[0] && l.To == others[1]) || (l.From == others[1] && l.To == others[0]));
            if (!already && others[0] != others[1])
                stat.Hyperlanes.Add(new Hyperlane(others[0], others[1]));
            stat.Systems.Remove(sys);
            _selected.Remove(sys);
        }
        _selectedLanes.Clear();
        DrawPreview();
    }

    /// <summary>等分弹窗：输入点数 N，把全部选中航道等分为 N+1 段（插入 N 个新点）。</summary>
    private void SplitLanesDialog()
    {
        if (_selectedLanes.Count == 0)
            return;
        var win = new Window
        {
            Title = Loc("staticmap.preview.split_lane"),
            Width = 280,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.preview.split_count") });
        var countBox = new TextBox { Text = "1", Margin = new Thickness(0, 4, 0, 12) };
        panel.Children.Add(countBox);
        var ok = new Button
        {
            Content = Loc("common.ok"),
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(ok);
        win.Content = panel;
        if (win.ShowDialog() != true)
            return;
        if (!int.TryParse(countBox.Text, out int n) || n < 1 || n > 50)
            return;
        SplitLanes(n);
    }

    private void SplitLanes(int n)
    {
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap!);
        if (stat == null)
            return;
        var byId = stat.Systems.ToDictionary(s => s.Id, s => s);
        foreach (var lane in _selectedLanes.ToList())
        {
            if (!byId.TryGetValue(lane.From, out var f) || !byId.TryGetValue(lane.To, out var t))
                continue;
            if (f == t)
                continue;
            stat.Hyperlanes.Remove(lane);
            SystemEntry prev = f;
            for (int i = 1; i <= n; i++)
            {
                double tt = (double)i / (n + 1);
                double x = f.Position.X + (t.Position.X - f.Position.X) * tt;
                double y = f.Position.Y + (t.Position.Y - f.Position.Y) * tt;
                var mid = AddSystemAt((float)Math.Round(x, _pointPrecision), (float)Math.Round(y, _pointPrecision));
                stat.Hyperlanes.Add(new Hyperlane(prev.Id, mid.Id));
                prev = mid;
            }
            stat.Hyperlanes.Add(new Hyperlane(prev.Id, t.Id));
        }
        _selectedLanes.Clear();
        DrawPreview();
    }

    /// <summary>X/Y 轴反转：不复制，直接翻转选中点坐标（flipX → x=-x；flipY → y=-y）。</summary>
    private void FlipSystems(bool flipX, bool flipY)
    {
        foreach (var sys in _selected)
        {
            if (flipX) sys.Position.X = -sys.Position.X;
            if (flipY) sys.Position.Y = -sys.Position.Y;
        }
        // 组合：图形中心、图像位置同步翻转
        foreach (var shape in _selectedShapes)
        {
            if (flipX) shape.Center.X = -shape.Center.X;
            if (flipY) shape.Center.Y = -shape.Center.Y;
        }
        foreach (var img in _selectedImages)
        {
            if (flipX) img.Position.X = -img.Position.X;
            if (flipY) img.Position.Y = -img.Position.Y;
        }
        DrawPreview();
    }

    private static ShapeOverlay CloneShape(ShapeOverlay s) => new()
    {
        Kind = s.Kind, Custom = s.Custom, EdgeCount = s.EdgeCount, Spacing = s.Spacing,
        Width = s.Width, Height = s.Height, WidthDiv = s.WidthDiv, HeightDiv = s.HeightDiv,
        OuterRadius = s.OuterRadius, InnerRadius = s.InnerRadius, RadialDiv = s.RadialDiv, CircumDiv = s.CircumDiv,
        Center = new SystemPosition { X = s.Center.X, Y = s.Center.Y }, Angle = s.Angle,
        Color = s.Color, ZValue = s.ZValue
    };

    /// <summary>删除全部选中的航道。</summary>
    private void DeleteSelectedLanes()
    {
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap!);
        if (stat == null)
            return;
        stat.Hyperlanes.RemoveAll(l => _selectedLanes.Contains(l));
        _selectedLanes.Clear();
        DrawPreview();
    }

    /// <summary>把全部选中点吸附到最近的网格交点（网格间距 = galaxy.json global.preview.grid_spacing）。</summary>
    private void SnapImagesToGrid()
    {
        double spacing = GetPreviewGridSpacing();
        if (spacing <= 0)
            return;
        foreach (var img in _selectedImages)
        {
            img.Position.X = (float)(Math.Round(img.Position.X / spacing) * spacing);
            img.Position.Y = (float)(Math.Round(img.Position.Y / spacing) * spacing);
        }
        DrawPreview();
    }

    private void SnapSelectionToGrid()
    {
        double spacing = GetPreviewGridSpacing();
        if (spacing <= 0)
            return;
        foreach (var sys in _selected)
        {
            sys.Position.X = (float)(Math.Round(sys.Position.X / spacing) * spacing);
            sys.Position.Y = (float)(Math.Round(sys.Position.Y / spacing) * spacing);
        }
        // 选中图形：图形 Center 吸附到网格
        foreach (var shape in _selectedShapes)
        {
            shape.Center.X = (float)(Math.Round(shape.Center.X / spacing) * spacing);
            shape.Center.Y = (float)(Math.Round(shape.Center.Y / spacing) * spacing);
        }
                // 选中图像：图像位置吸附到网格
        foreach (var img in _selectedImages)
        {
            img.Position.X = (float)(Math.Round(img.Position.X / spacing) * spacing);
            img.Position.Y = (float)(Math.Round(img.Position.Y / spacing) * spacing);
        }
// 重叠检查：吸附后坐标相同的点合并（航道重定向/继承、自环删除、航道去重）
        DeduplicateAndMerge();
        DrawPreview();
    }

    private void CustomMirror()
    {
        if (_currentMap == null) return;
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap);
        if (stat == null) return;
        var targets = _selected.Count > 0 ? _selected.ToList() : stat.Systems.ToList();
        if (targets.Count == 0)
            return;
        double defCx = targets.Average(t => t.Position.X);
        double defCy = targets.Average(t => t.Position.Y);
        // 记忆：上次确定的值从 galaxy.json custom_mirror 读取（count/angle/center_x/center_y）
        var mem = ReadCustomMirrorMemory();
        int memCount = mem?.Count ?? 0;
        double memAngle = mem?.Angle ?? 0;
        double? memCx = mem?.CenterX, memCy = mem?.CenterY;

        var win = new Window
        {
            Title = Loc("staticmap.preview.custom_mirror"),
            Width = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.preview.custom_count") });
        var countBox = new TextBox { Text = memCount > 0 ? memCount.ToString() : "5", Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(countBox);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.preview.custom_direction") });
        var dirCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 8) };
        dirCombo.Items.Add(new ComboBoxItem { Content = Loc("staticmap.preview.dir_cw"), Tag = true });
        dirCombo.Items.Add(new ComboBoxItem { Content = Loc("staticmap.preview.dir_ccw"), Tag = false });
        dirCombo.SelectedIndex = mem?.Clockwise == true ? 0 : 1;
        panel.Children.Add(dirCombo);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.preview.custom_angle") });
        var angleBox = new TextBox { Text = memAngle != 0 ? memAngle.ToString("0.##") : "60", Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(angleBox);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.preview.custom_center_x") });
        var centerXBox = new TextBox { Text = (memCx ?? defCx).ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(centerXBox);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.preview.custom_center_y") });
        var centerYBox = new TextBox { Text = (memCy ?? defCy).ToString("0.##"), Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(centerYBox);
        var ok = new Button
        {
            Content = Loc("common.ok"),
            Width = 80,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(ok);
        win.Content = panel;
        if (win.ShowDialog() != true)
            return;
        if (!int.TryParse(countBox.Text, out int count) || count < 1 || count > 100)
            return;
        if (!double.TryParse(angleBox.Text, out double angle))
            return;
        if (!double.TryParse(centerXBox.Text, out double ccx) || !double.TryParse(centerYBox.Text, out double ccy))
            return;
        bool clockwise = dirCombo.SelectedItem is ComboBoxItem di && di.Tag is true;
        double sign = clockwise ? -1.0 : 1.0; // 顺时针 → 负角

        // 复制次数 = 最终总数（含原图）：次数 3 → 原图 + 2 副本（去重后最多 3 个）；最少 1（不复制）
        for (int k = 1; k < count; k++)
        {
            var copyMap = new Dictionary<string, string>(StringComparer.Ordinal); // 源 Id → 本批副本 Id
            foreach (var t in targets)
            {
                double x = t.Position.X - ccx, y = t.Position.Y - ccy;
                // 累积旋转 k 次
                double ang = k * angle * sign * Math.PI / 180.0;
                double c2 = Math.Cos(ang), s2 = Math.Sin(ang);
                var sys = AddSystemAt((float)(ccx + x * c2 - y * s2), (float)(ccy + x * s2 + y * c2));
                sys.Name = t.Name;
                sys.Initializer = t.Initializer;
                copyMap[t.Id] = sys.Id;
            }
            // 复制航道：同一批次副本间对应源航道
            CopyLanes(stat, copyMap);
            // 组合：图形副本（绕中心旋转 k 次）、图像副本
            foreach (var t in _selectedShapes)
            {
                double x = t.Center.X - ccx, y = t.Center.Y - ccy;
                double ang = k * angle * sign * Math.PI / 180.0;
                double c2 = Math.Cos(ang), s2 = Math.Sin(ang);
                var ns = CloneShape(t);
                ns.Center.X = (float)(ccx + x * c2 - y * s2);
                ns.Center.Y = (float)(ccy + x * s2 + y * c2);
                _shapes.Add(ns);
            }
            foreach (var t in _selectedImages)
            {
                double x = t.Position.X - ccx, y = t.Position.Y - ccy;
                double ang = k * angle * sign * Math.PI / 180.0;
                double c2 = Math.Cos(ang), s2 = Math.Sin(ang);
                var ni = new ImageOverlay
                {
                    Path = t.Path,
                    Position = new SystemPosition
                    {
                        X = (float)(ccx + x * c2 - y * s2),
                        Y = (float)(ccy + x * s2 + y * c2)
                    },
                    Width = t.Width, Height = t.Height, Angle = t.Angle,
                    GenOptions = t.GenOptions.Clone(), AutoLanes = t.AutoLanes
                };
                _images.Add(ni);
            }
        }
        _editMode = EditMode.None;
        DeduplicateAndMerge();
        DrawPreview();
        // 记忆：保存本次自定义镜像参数到用户配置（galaxy.json custom_mirror）
        WriteCustomMirrorMemory(count, angle, ccx, ccy, clockwise);
    }

    private sealed class CustomMirrorMemory
    {
        public int Count;
        public double Angle;
        public double? CenterX;
        public double? CenterY;
        public bool? Clockwise;
    }

    private CustomMirrorMemory? ReadCustomMirrorMemory()
    {
        var cm = _services.ConfigManager;
        if (cm == null)
            return null;
        try
        {
            var node = cm.Get("galaxy", "custom_mirror");
            if (node is not System.Text.Json.Nodes.JsonObject obj)
                return null;
            var m = new CustomMirrorMemory();
            if (obj["count"] is System.Text.Json.Nodes.JsonValue cv && cv.TryGetValue<int>(out int cnt)) m.Count = cnt;
            if (obj["angle"] is System.Text.Json.Nodes.JsonValue av && av.TryGetValue<double>(out double ang)) m.Angle = ang;
            if (obj["center_x"] is System.Text.Json.Nodes.JsonValue xv && xv.TryGetValue<double>(out double cx)) m.CenterX = cx;
            if (obj["center_y"] is System.Text.Json.Nodes.JsonValue yv && yv.TryGetValue<double>(out double cy)) m.CenterY = cy;
            if (obj["clockwise"] is System.Text.Json.Nodes.JsonValue wv && wv.TryGetValue<bool>(out bool cw)) m.Clockwise = cw;
            return m;
        }
        catch
        {
            return null;
        }
    }

    private void WriteCustomMirrorMemory(int count, double angle, double centerX, double centerY, bool clockwise)
    {
        var cm = _services.ConfigManager;
        if (cm == null)
            return;
        try
        {
            // 用 Dictionary（ToJsonNode 的 IDictionary 分支处理）——JsonObject 不匹配非泛型 IDictionary，
            // 会走 IEnumerable 逐元素递归导致序列化失败（静默 catch 吞掉 → 没保存）
            var dict = new Dictionary<string, object>
            {
                ["count"] = count,
                ["angle"] = angle,
                ["center_x"] = centerX,
                ["center_y"] = centerY,
                ["clockwise"] = clockwise
            };
            cm.Set("galaxy", "custom_mirror", dict);
        }
        catch { }
    }

    /// <summary>点设置弹窗：X / Y / 命名键（可编辑，退出焦点自动查本地化）/ 命名逻辑值（只读）/ 命名显示值（只读）/ 星系预设（下拉）。Id 自动分配不可编辑。</summary>
    private void ShowPointSetting(SystemEntry sys)
    {
        var win = new Window
        {
            Title = Loc("staticmap.preview.point_setting"),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize
        };
        var grid = new Grid { Margin = new Thickness(10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var xBox = new TextBox { Text = sys.Position.X.ToString("0.##"), Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var yBox = new TextBox { Text = sys.Position.Y.ToString("0.##"), Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var zBox = new TextBox { Text = sys.Position.Z.ToString("0.##"), Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        // 命名键（可编辑）
        var nameBox = new TextBox { Text = sys.Name, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        // 命名逻辑值（可编辑）：修改后保持原相对路径（根目录改为 mod，覆盖性兼容）
        string modLang = MapUiLangToModLang(_services.Localisation.CurrentLanguage);
        var nameLogical = new TextBox
        {
            Text = _services.Adapter?.GetLocalisedLogicalText(sys.Name, modLang) ?? string.Empty,
            Width = 320,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        // 命名显示值（只读，按 mod 语言翻译）
        var nameDisplay = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60))
        };
        // 退出命名键焦点时搜索 key：不存在 → 逻辑值/显示值清空；存在 → 调取对应内容展示
        void FillNameDisplay()
        {
            string key = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(key))
            {
                nameLogical.Text = string.Empty;
                nameDisplay.Text = string.Empty;
                return;
            }
            nameLogical.Text = _services.Adapter?.GetLocalisedLogicalText(key, modLang) ?? string.Empty;
            nameDisplay.Text = _services.Adapter?.GetLocalisedText(key, modLang) ?? string.Empty;
        }
        FillNameDisplay();
        nameBox.LostFocus += (_, _) => FillNameDisplay();
        // 退出逻辑值焦点：在原相对路径更新（根目录改为 mod）；新 key 写约定文件；刷新显示值
        nameLogical.LostFocus += (_, _) =>
        {
            string key = nameBox.Text.Trim();
            string logical = nameLogical.Text;
            if (string.IsNullOrEmpty(key))
                return;
            var adapter = _services.Adapter;
            if (adapter == null)
                return;
            string modRoot = _services.Roots.Count > 0 ? _services.Roots[^1] : string.Empty;
            if (string.IsNullOrEmpty(modRoot))
                return;
            string? path = null;
            var index = adapter.GetLocalisationKeyFiles(modLang);
            if (index != null && index.TryGetValue(key, out var f))
                path = f;
            if (string.IsNullOrEmpty(path))
                path = _services.StyleEngine?.StyleLocalisationFile(modLang)
                       ?? $"localisation/{modLang}/smt_style_l_{modLang}.yml";
            adapter.AddLocalisationEntry(modLang, path, key, logical, modRoot);
            nameDisplay.Text = adapter.GetLocalisedText(key, modLang) ?? string.Empty;
        };
        // initializer：下拉可选（solar_system_initializers 目录），可输入
        var initCombo = new ComboBox
        {
            IsEditable = true,
            Text = sys.Initializer ?? string.Empty,
            Width = 320,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var inits = _services.SystemInitializerEngine?.GetAvailableInitializers() ?? new List<string>();
        foreach (var i in inits)
            initCombo.Items.Add(i);
        if (sys.Initializer != null && !inits.Contains(sys.Initializer, StringComparer.Ordinal))
            initCombo.Items.Add(sys.Initializer);

        void AddRow(int row, string label, UIElement editor)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 6, 2) });
            Grid.SetColumn(grid.Children[^1], 0);
            Grid.SetRow(grid.Children[^1], row);
            grid.Children.Add(editor);
            Grid.SetColumn(grid.Children[^1], 1);
            Grid.SetRow(grid.Children[^1], row);
        }
        AddRow(0, Loc("staticmap.preview.x"), xBox);
        AddRow(1, Loc("staticmap.preview.y"), yBox);
        AddRow(2, Loc("staticmap.preview.z"), zBox);
        AddRow(3, Loc("staticmap.preview.name_key"), nameBox);
        AddRow(4, Loc("staticmap.preview.name_logical"), nameLogical);
        AddRow(5, Loc("staticmap.preview.name_display"), nameDisplay);
        AddRow(6, Loc("staticmap.preview.initializer"), initCombo);

        var ok = new Button { Content = Loc("common.ok"), Width = 80, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (double.TryParse(xBox.Text, out var x) && double.TryParse(yBox.Text, out var y))
            {
                sys.Position.X = x;
                sys.Position.Y = y;
                if (double.TryParse(zBox.Text, out var z))
                    sys.Position.Z = Math.Clamp(z, -10, 10);
            }
            sys.Name = nameBox.Text;
            string init = initCombo.Text.Trim();
            sys.Initializer = string.IsNullOrWhiteSpace(init) ? null : init;
            win.Close();
            _editMode = EditMode.None;
            DrawPreview();
        };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(ok);
        Grid.SetColumn(grid.Children[^1], 1);
        Grid.SetRow(grid.Children[^1], 6);
        win.Content = grid;
        win.ShowDialog();
    }

    /// <summary>中键按下：开始平移画布（记录起点，捕获鼠标）。</summary>
    private void OnCanvasMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        _panning = true;
        _panLast = e.GetPosition(PreviewCanvas);
        PreviewCanvas.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>中键抬起：结束平移。</summary>
    private void OnCanvasMiddleUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        _panning = false;
        PreviewCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>Ctrl+滚轮缩放（传统方向：上滚放大、下滚缩小），以鼠标位置为中心。</summary>
    private void OnCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        ZoomAt(e.GetPosition(PreviewCanvas), e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    /// <summary>以画布点 p 为不动点缩放（factor>1 放大）。</summary>
    private void ZoomAt(Point p, double factor)
    {
        var (vx, vy) = ToMapCoords(p); // 缩放前该点坐标
        double nz = Math.Clamp(_zoom * factor, 0.15, 10.0);
        double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
        double cx = side / 2.0, cy = side / 2.0;
        double half = side / 2.0 - 4;
        double span = 500 / nz;
        _panX = vx - (p.X - cx) / half * span;
        _panY = vy + (p.Y - cy) / half * span;
        _zoom = nz;
        DrawPreview();
    }

    /// <summary>Shift+右键按下：开始拖动式旋转（点在点上绕该点、空处绕点击处；Shift+Alt 不启动，留给弹窗）。</summary>
    private void OnCanvasRightDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || _selected.Count == 0)
            return;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            return; // Shift+Alt+右键 = 弹窗精确角度（在 Up 处理）
        _rotating = true;
        var p = e.GetPosition(PreviewCanvas);
        var hit = HitSystem(p);
        if (hit != null)
        {
            _rotCenterX = hit.Position.X;
            _rotCenterY = hit.Position.Y;
        }
        else
        {
            var (mx, my) = ToMapCoords(p);
            _rotCenterX = mx;
            _rotCenterY = my;
        }
        _rotSnap = _selected.Select(s => (s, s.Position.X, s.Position.Y)).ToList();
        _rotStartAngle = RotAngleDeg(p);
        PreviewCanvas.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>画布点 p 相对旋转中心的极角（度）。</summary>
    private double RotAngleDeg(Point p)
    {
        double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
        double span = 500 / _zoom, half = side / 2.0 - 4;
        double cpx = side / 2.0 + (_rotCenterX - _panX) / span * half;
        double cpy = side / 2.0 - (_rotCenterY - _panY) / span * half;
        return Math.Atan2(p.Y - cpy, p.X - cpx) * 180.0 / Math.PI;
    }

    /// <summary>键盘：ESC 退出编辑模式；Ctrl+C 复制 / Ctrl+V 粘贴（焦点在编辑画布时生效）。</summary>
    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement != PreviewCanvas)
            return;
        if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_editMode != EditMode.None)
            {
                _editMode = EditMode.None;
                _laneFrom = null;
                PreviewTitle.Text = LocMapName(_currentMap ?? string.Empty);
                DrawPreview();
                e.Handled = true;
            }
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.C && (_selected.Count > 0 || _selectedShapes.Count > 0 || _selectedImages.Count > 0))
            {
                CopySelection();
                e.Handled = true;
            }
            else if (e.Key == Key.V
                && (_clipboard.Count > 0 || _clipboardShapes.Count > 0 || _clipboardImages.Count > 0))
            {
                PasteSelection(_lastCanvasPos);
                e.Handled = true;
            }
            else if (e.Key == Key.X
                && (_selected.Count > 0 || _selectedShapes.Count > 0 || _selectedImages.Count > 0))
            {
                // Ctrl+X 剪切：复制选中（点+图形+图像组合）后删除（DeleteSelected 组合删除）
                CopySelection();
                DeleteSelected();
                e.Handled = true;
            }
        }
    }

    /// <summary>命中检测：点击位置最近的星系（命中半径 6px）。</summary>
    private SystemEntry? HitSystem(Point p)
    {
        if (_currentMap == null)
            return null;
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap);
        if (stat == null)
            return null;
        SystemEntry? hit = null;
        double best = double.MaxValue;
        foreach (var sys in stat.Systems)
        {
            double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
            double cx = side / 2.0, cy = side / 2.0;
            double span = 500 / _zoom, half = side / 2.0 - 4;
            double px = cx + (sys.Position.X - _panX) / span * half;
            double py = cy - (sys.Position.Y - _panY) / span * half;
            double d = (p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py);
            if (d < best) { best = d; hit = sys; }
        }
        return hit != null && best < 36 ? hit : null;
    }

    /// <summary>左键按下：记录起点；若点在已选中点上且非编辑模式 → 进入移动模式（拖动选中点）。</summary>
    private void OnCanvasLeftDown(object sender, MouseButtonEventArgs e)
    {
        _leftDown = true;
        _leftDownPos = e.GetPosition(PreviewCanvas);
        _lastCanvasPos = _leftDownPos;
        _boxStart = null;
        _moving = false;
        PreviewCanvas.Focus(); // 编辑画布获得键盘焦点（Ctrl+C/V / ESC 生效）
        if (_editMode == EditMode.None)
        {
            // 点击定位点 → 不进入拖动（LeftUp 负责定位点切换）；否则拖动选中图像/图形/点
            if (HitLocator(_leftDownPos) != null)
            {
                _moving = false;
            }
            else
            {
                var hitImg = HitImage(_leftDownPos);
                if (hitImg != null && _selectedImages.Contains(hitImg))
                {
                    _moving = true;          // 拖动选中的图像 → 移动位置
                    _movingImage = hitImg;
                    _moveLast = _leftDownPos;
                }
                else
                {
                    var hitShape = HitShape(_leftDownPos);
                    if (hitShape != null && _selectedShapes.Contains(hitShape))
                    {
                        _moving = true;          // 拖动选中的图形 → 移动中心
                        _movingShape = hitShape;
                        _moveLast = _leftDownPos;
                    }
                    else
                    {
                        var hit = HitSystem(_leftDownPos);
                        if (hit != null && _selected.Contains(hit))
                        {
                            _moving = true; // 拖动已选中的点 → 移动（不是框选）
                            _moveLast = _leftDownPos;
                        }
                    }
                }
            }
        }
        PreviewCanvas.CaptureMouse();
    }

    /// <summary>左键抬起：点选（Shift 加选/取消）或框选；编辑模式（点设置/航道）优先。</summary>
    private void OnCanvasLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_leftDown)
            return;
        _leftDown = false;
        PreviewCanvas.ReleaseMouseCapture();
        if (_boxRect != null) { PreviewCanvas.Children.Remove(_boxRect); _boxRect = null; }
        var p = e.GetPosition(PreviewCanvas);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        double dx = p.X - _leftDownPos.X, dy = p.Y - _leftDownPos.Y;
        bool dragged = Math.Abs(dx) > 4 || Math.Abs(dy) > 4;

        // 拖动移动结束（不触发选中/框选）
        if (_moving)
        {
            _moving = false;
            _movingShape = null;
            _movingImage = null;
            return;
        }

        // 编辑模式优先
        if (_editMode == EditMode.PointSetting)
        {
            var hit = HitSystem(p);
            if (hit != null)
            {
                ShowPointSetting(hit);
            }
            else
            {
                var (x, y) = ToMapCoords(p);
                var sys = AddSystemAt((float)Math.Round(x), (float)Math.Round(y));
                ShowPointSetting(sys);
            }
            return;
        }
        if (_editMode == EditMode.Hyperlane)
        {
            var stat = _services.MapEngine?.GetStaticScenario(_currentMap!)!;
            var hit = HitSystem(p);
            SystemEntry target = hit!;
            if (hit == null)
            {
                // 点击空处 → 在该位置新建一个 system，并连到起点（若有）
                var (x, y) = ToMapCoords(p);
                target = AddSystemAt((float)Math.Round(x), (float)Math.Round(y));
                DrawPreview();
            }
            if (_laneFrom == null)
            {
                _laneFrom = target;
                PreviewTitle.Text = string.Format(Loc("staticmap.preview.hyperlane_selected"), _laneFrom.Id);
            }
            else if (_laneFrom != target)
            {
                // 重复航道排查：同一条（含反向）不重复添加
                bool dup = stat.Hyperlanes.Any(l =>
                    (l.From == _laneFrom.Id && l.To == target.Id)
                    || (l.From == target.Id && l.To == _laneFrom.Id));
                if (!dup)
                    stat.Hyperlanes.Add(new Hyperlane(_laneFrom.Id, target.Id));
                DrawPreview();
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    // Shift 连续创建：上一个结束点 = 下一个开始点
                    _laneFrom = target;
                    PreviewTitle.Text = string.Format(Loc("staticmap.preview.hyperlane_cont"), _laneFrom.Id);
                }
                else
                {
                    _laneFrom = null;
                    _editMode = EditMode.None;
                    PreviewTitle.Text = string.Format(Loc("staticmap.preview.hyperlane_done"), stat.Hyperlanes[^1].From, stat.Hyperlanes[^1].To);
                }
            }
            return;
        }

        // 框选：起点在空处且拖动超过阈值
        if (dragged && _boxStart.HasValue)
        {
            var rect = GetBoxRect(_boxStart.Value, p);
            var stat = _services.MapEngine?.GetStaticScenario(_currentMap!);
            if (stat != null)
            {
                double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
                double cx = side / 2.0, cy = side / 2.0;
                double span = 500 / _zoom, half = side / 2.0 - 4;
                double MapX(double v) => cx + (v - _panX) / span * half;
                double MapY(double v) => cy - (v - _panY) / span * half;

                // 点
                var inBox = new List<SystemEntry>();
                foreach (var sys in stat.Systems)
                {
                    if (rect.Contains(new Point(MapX(sys.Position.X), MapY(sys.Position.Y))))
                        inBox.Add(sys);
                }
                // 图形（包围盒与框选矩形相交）
                var inShapes = new List<ShapeOverlay>();
                foreach (var shape in _shapes)
                {
                    var locs = shape.GetLocators();
                    if (locs.Count == 0)
                        continue;
                    double minX = locs.Min(l => l.X), maxX = locs.Max(l => l.X);
                    double minY = locs.Min(l => l.Y), maxY = locs.Max(l => l.Y);
                    var box = new Rect(new Point(MapX(minX), MapY(maxY)), new Point(MapX(maxX), MapY(minY)));
                    if (rect.IntersectsWith(box))
                        inShapes.Add(shape);
                }
                // 图像（矩形与框选矩形相交）
                var inImages = new List<ImageOverlay>();
                foreach (var img in _images)
                {
                    var box = new Rect(
                        new Point(MapX(img.Position.X - img.Width / 2.0), MapY(img.Position.Y + img.Height / 2.0)),
                        new Point(MapX(img.Position.X + img.Width / 2.0), MapY(img.Position.Y - img.Height / 2.0)));
                    if (rect.IntersectsWith(box))
                        inImages.Add(img);
                }
                if (shift)
                {
                    foreach (var s in inBox) _selected.Add(s);
                    foreach (var sh in inShapes) _selectedShapes.Add(sh);
                    foreach (var im in inImages) _selectedImages.Add(im);
                }
                else
                {
                    _selected.Clear();
                    _selectedShapes.Clear();
                    _selectedImages.Clear();
                    foreach (var s in inBox) _selected.Add(s);
                    foreach (var sh in inShapes) _selectedShapes.Add(sh);
                    foreach (var im in inImages) _selectedImages.Add(im);
                }
            }
            DrawPreview();
            return;
        }

        // 定位点命中：选中该定位点（并选中图形）——默认中心（-1）
        var hitLoc = HitLocator(p);
        if (hitLoc != null && !dragged)
        {
            _selLocatorShape = hitLoc.Value.Shape;
            _selLocatorIndex = hitLoc.Value.Index;
            if (!shift)
            {
                _selected.Clear();
                _selectedLanes.Clear();
                _selectedShapes.Clear();
            }
            _selectedShapes.Add(hitLoc.Value.Shape);
            DrawPreview();
            return;
        }

        // 图像命中：与点/图形可同时选中
        var hitImgSel = HitImage(p);
        if (hitImgSel != null && !dragged)
        {
            _selectedLanes.Clear();
            if (shift)
            {
                if (!_selectedImages.Remove(hitImgSel))
                    _selectedImages.Add(hitImgSel);
            }
            else
            {
                _selectedImages.Clear();
                _selectedImages.Add(hitImgSel);
            }
            DrawPreview();
            return;
        }

        // 图形命中：与点可同时选中（Shift 多选；非 Shift 单选图形并清点）
        var hitShape = HitShape(p);
        if (hitShape != null && !dragged)
        {
            _selectedLanes.Clear();
            if (shift)
            {
                if (!_selectedShapes.Remove(hitShape))
                    _selectedShapes.Add(hitShape);
            }
            else
            {
                _selected.Clear();
                _selectedShapes.Clear();
                _selectedShapes.Add(hitShape);
            }
            DrawPreview();
            return;
        }

        var hit2 = HitSystem(p);
        if (hit2 != null)
        {
            _selectedLanes.Clear();
            if (shift)
            {
                if (!_selected.Remove(hit2))
                    _selected.Add(hit2);
            }
            else
            {
                _selected.Clear();
                _selected.Add(hit2);
            }
        }
        else if (!dragged)
        {
            // 未点中点 → 航道命中：选中航道（Shift 多选）
            var lane = HitLane(p);
            if (lane != null)
            {
                _selected.Clear();
                _selectedShapes.Clear();
                _selLocatorShape = null;
                if (shift)
                {
                    if (!_selectedLanes.Remove(lane))
                        _selectedLanes.Add(lane);
                }
                else
                {
                    _selectedLanes.Clear();
                    _selectedLanes.Add(lane);
                }
            }
            else
            {
                // 点空处：取消点/航道/图形/定位点选中
                _selected.Clear();
                _selectedLanes.Clear();
                _selectedShapes.Clear();
                _selLocatorShape = null;
            }
        }
        DrawPreview();
    }

    /// <summary>命中航道：点到线段（屏幕坐标）距离小于 6px 返回该航道，否则 null。</summary>
    private Hyperlane? HitLane(Point p)
    {
        if (_currentMap == null)
            return null;
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap);
        if (stat == null)
            return null;
        var byId = stat.Systems.ToDictionary(s => s.Id, s => s);
        double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
        double cx = side / 2.0, cy = side / 2.0;
        double span = 500 / _zoom, half = side / 2.0 - 4;
        double MapX(double v) => cx + (v - _panX) / span * half;
        double MapY(double v) => cy - (v - _panY) / span * half;

        foreach (var lane in stat.Hyperlanes)
        {
            if (!byId.TryGetValue(lane.From, out var f) || !byId.TryGetValue(lane.To, out var t))
                continue;
            var a = new Point(MapX(f.Position.X), MapY(f.Position.Y));
            var b = new Point(MapX(t.Position.X), MapY(t.Position.Y));
            if (DistToSegment(p, a, b) < 6.0)
                return lane;
        }
        return null;
    }

    /// <summary>命中图像：点在图像矩形（中心 ± 宽高/2）内返回该图像，否则 null（旋转忽略，简化）。</summary>
    private ImageOverlay? HitImage(Point p)
    {
        double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
        double cx = side / 2.0, cy = side / 2.0;
        double span = 500 / _zoom, half = side / 2.0 - 4;
        double MapX(double v) => cx + (v - _panX) / span * half;
        double MapY(double v) => cy - (v - _panY) / span * half;

        foreach (var img in _images)
        {
            double left = MapX(img.Position.X - img.Width / 2.0);
            double right = MapX(img.Position.X + img.Width / 2.0);
            double top = MapY(img.Position.Y + img.Height / 2.0);
            double bottom = MapY(img.Position.Y - img.Height / 2.0);
            if (p.X >= Math.Min(left, right) && p.X <= Math.Max(left, right)
                && p.Y >= Math.Min(top, bottom) && p.Y <= Math.Max(top, bottom))
                return img;
        }
        return null;
    }

    /// <summary>SKBitmap → WPF BitmapSource（图像渲染用）。</summary>
    private static System.Windows.Media.Imaging.BitmapSource ImageToBitmapSource(SkiaSharp.SKBitmap bmp)
    {
        using var pixmap = bmp.PeekPixels();
        var info = pixmap.Info;
        var data = pixmap.GetPixels();
        int stride = info.RowBytes;
        int byteCount = stride * info.Height;
        byte[] pixels = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(data, pixels, 0, byteCount);
        var src = System.Windows.Media.Imaging.BitmapSource.Create(
            info.Width, info.Height, 96, 96,
            info.ColorType == SkiaSharp.SKColorType.Bgra8888
                ? System.Windows.Media.PixelFormats.Bgra32
                : System.Windows.Media.PixelFormats.Bgr32,
            null, pixels, stride);
        src.Freeze();
        return src;
    }

    /// <summary>命中图形定位点（顶角/中心圆，距离 < 8px）：返回 (图形, 定位点索引)；无命中返回 null。</summary>
    private (ShapeOverlay Shape, int Index)? HitLocator(Point p)
    {
        double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
        double cx = side / 2.0, cy = side / 2.0;
        double span = 500 / _zoom, half = side / 2.0 - 4;
        double MapX(double v) => cx + (v - _panX) / span * half;
        double MapY(double v) => cy - (v - _panY) / span * half;

        foreach (var shape in _shapes)
        {
            var locs = shape.GetLocators();
            for (int i = 0; i < locs.Count; i++)
            {
                var l = locs[i];
                double sx = MapX(l.X), sy = MapY(l.Y);
                if (Math.Abs(p.X - sx) < 8 && Math.Abs(p.Y - sy) < 8)
                    return (shape, i);
            }
        }
        return null;
    }

    /// <summary>命中图形：点在图形内部或距边 < 8px 返回该图形，否则 null。</summary>
    private ShapeOverlay? HitShape(Point p)
    {
        double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
        double cx = side / 2.0, cy = side / 2.0;
        double span = 500 / _zoom, half = side / 2.0 - 4;
        double MapX(double v) => cx + (v - _panX) / span * half;
        double MapY(double v) => cy - (v - _panY) / span * half;

        foreach (var shape in _shapes)
        {
            if (shape.Kind == ShapeKind.Circle)
            {
                // 圆：点到圆心距离 <= 半径（屏幕像素）
                var c = new Point(MapX(shape.Center.X), MapY(shape.Center.Y));
                double rr = Math.Abs(MapX(shape.Center.X + shape.OuterRadius) - MapX(shape.Center.X));
                if ((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y) <= rr * rr)
                    return shape;
                continue;
            }
            var locs = shape.GetLocators();
            var screen = locs.Take(shape.VertexCount)
                .Select(l => new Point(MapX(l.X), MapY(l.Y))).ToList();
            if (PointInPolygon(p, screen))
                return shape;
            // 边距检查
            for (int i = 0; i < screen.Count; i++)
            {
                var a = screen[i];
                var b = screen[(i + 1) % screen.Count];
                if (DistToSegment(p, a, b) < 8.0)
                    return shape;
            }
        }
        return null;
    }

    private static bool PointInPolygon(Point p, List<Point> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var pi = poly[i];
            var pj = poly[j];
            if ((pi.Y > p.Y) != (pj.Y > p.Y) &&
                p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }
        return inside;
    }

    private static double DistToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 <= 0.0001)
            return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        t = Math.Clamp(t, 0.0, 1.0);
        double qx = a.X + t * dx, qy = a.Y + t * dy;
        return Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
    }

    /// <summary>创建航道：2 个选中点直接连；1 个选中点以其为起点进入航道模式；否则普通模式。</summary>
    private void StartHyperlaneMode()
    {
        _editMode = EditMode.Hyperlane;
        if (_selected.Count == 2)
        {
            var sel = _selected.ToList();
            var stat = _services.MapEngine?.GetStaticScenario(_currentMap!);
            if (stat != null)
            {
                // 重复航道排查：同一条（含反向）不重复添加
                bool dup = stat.Hyperlanes.Any(l =>
                    (l.From == sel[0].Id && l.To == sel[1].Id)
                    || (l.From == sel[1].Id && l.To == sel[0].Id));
                if (!dup)
                    stat.Hyperlanes.Add(new Hyperlane(sel[0].Id, sel[1].Id));
                _editMode = EditMode.None;
                _laneFrom = null;
                PreviewTitle.Text = string.Format(Loc("staticmap.preview.hyperlane_done"), sel[0].Id, sel[1].Id);
                DrawPreview();
                return;
            }
        }
        if (_selected.Count == 1)
        {
            _laneFrom = _selected.First();
            PreviewTitle.Text = string.Format(Loc("staticmap.preview.hyperlane_selected"), _laneFrom.Id);
        }
        else
        {
            _laneFrom = null;
            PreviewTitle.Text = Loc("staticmap.preview.hyperlane_hint");
        }
    }

    /// <summary>右键：Ctrl → 旋转选中点（绕点击点/点击位置）；否则菜单（复制/删除/设置/粘贴/添加/镜像/导入/航道）。</summary>
    private void OnCanvasRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_currentMap == null)
            return;
        var p = e.GetPosition(PreviewCanvas);
        _lastCanvasPos = p;
        PreviewCanvas.Focus();

        // Shift+右键拖动旋转结束（不弹菜单）
        if (_rotating)
        {
            _rotating = false;
            _rotSnap.Clear();
            PreviewCanvas.ReleaseMouseCapture();
            return;
        }
        // Shift+Alt+右键 → 弹窗输入精确角度
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && (Keyboard.Modifiers & ModifierKeys.Alt) != 0 && _selected.Count > 0)
        {
            var hit = HitSystem(p);
            double cx, cy;
            if (hit != null)
            {
                cx = hit.Position.X;
                cy = hit.Position.Y;
            }
            else
            {
                var (mx, my) = ToMapCoords(p);
                cx = mx;
                cy = my;
            }
            RotateSelected(cx, cy);
            return;
        }

        var hit2 = HitSystem(p);
        var hitLane = hit2 == null ? HitLane(p) : null;
        var hitShapeR = hit2 == null && hitLane == null ? HitShape(p) : null;
        var hitImageR = hit2 == null && hitLane == null && hitShapeR == null ? HitImage(p) : null;
        bool hasPts = _selected.Count > 0;
        bool hasLanes = _selectedLanes.Count > 0;
        bool hasShapes = _selectedShapes.Count > 0;
        bool hasImages = _selectedImages.Count > 0;

        var menu = new ContextMenu();
        MenuItem Mi(string key, Action act, bool enabled = true)
        {
            var it = new MenuItem { Header = Loc(key), IsEnabled = enabled };
            it.Click += (_, _) => act();
            return it;
        }

        var copyPt = Mi("staticmap.preview.copy", CopySelection, hasPts);
        var pasteAny = Mi("staticmap.preview.paste", () => PasteSelection(p),
            _clipboard.Count > 0 || _clipboardShapes.Count > 0 || _clipboardImages.Count > 0);
        var delPt = Mi("staticmap.preview.delete", DeleteSelected, hasPts);
        var setPt = Mi("staticmap.preview.point_setting", () => ShowPointSetting(_selected.First()), hasPts && _selected.Count == 1);
        var snapPt = Mi("staticmap.preview.snap_to_grid", SnapSelectionToGrid, hasPts || hasShapes);
        bool allDeg2 = hasPts && _selected.All(s => CountLanes(s.Id) == 2);
        var mergePt = Mi("staticmap.preview.merge_points", MergePoints, allDeg2);
        var mirrorX = Mi("staticmap.preview.mirror_x", () => MirrorSystems(1, -1), hasPts);
        var mirrorY = Mi("staticmap.preview.mirror_y", () => MirrorSystems(-1, 1), hasPts);
        var mirrorD = Mi("staticmap.preview.mirror_dual", MirrorDual, hasPts);
        var mirrorO = Mi("staticmap.preview.mirror_origin", () => MirrorSystems(-1, -1), hasPts);
        var mirrorC = Mi("staticmap.preview.custom_mirror", CustomMirror, hasPts);
        var rotateC = Mi("staticmap.preview.custom_rotate", CustomRotate, hasPts);
        var flipX = Mi("staticmap.preview.flip_x", () => FlipSystems(true, false), hasPts);
        var flipY = Mi("staticmap.preview.flip_y", () => FlipSystems(false, true), hasPts);
        var copyLane = Mi("staticmap.preview.copy", CopyLaneSelection, hasLanes);
        var splitLane = Mi("staticmap.preview.split_lane", SplitLanesDialog, hasLanes);
        var delLane = Mi("staticmap.preview.delete_lane", DeleteSelectedLanes, hasLanes);
        var laneCreate = Mi("staticmap.preview.hyperlane", StartHyperlaneMode, hasPts && _selected.Count <= 2);
        var clearSel = Mi("staticmap.preview.clear_selection", () => { _selected.Clear(); _selectedLanes.Clear(); _selectedShapes.Clear(); DrawPreview(); });
        var shapeSet = Mi("staticmap.shape.settings", ShapeSettingsDialog, hasShapes && _selectedShapes.Count == 1);
        var shapeApply = Mi("staticmap.shape.apply", () => ApplyShapes(_selectedShapes.ToList()), hasShapes);
        var shapeDel = Mi("staticmap.preview.delete", () => { foreach (var s in _selectedShapes.ToList()) { _shapes.Remove(s); _selectedShapes.Remove(s); } DrawPreview(); }, hasShapes);
        var shapeSnap = Mi("staticmap.preview.snap_to_grid", SnapSelectionToGrid, hasShapes);
        // 图像操作（预留——功能后续实现）
        var imgSet = Mi("staticmap.preview.point_setting", ImageSettingsDialog, hasImages && _selectedImages.Count == 1);
        var imgSnap = Mi("staticmap.preview.snap_to_grid", SnapImagesToGrid, hasImages);
        var imgApply = Mi("staticmap.shape.apply", () => ApplyImages(_selectedImages.ToList()), hasImages);
        var imgDel = Mi("staticmap.preview.delete", () => { foreach (var im in _selectedImages.ToList()) { _images.Remove(im); _selectedImages.Remove(im); } DrawPreview(); }, hasImages);

        // 相邻双选（2 个选中点且之间有航道）：等分 / 删除该航道
        Hyperlane? pairLane = null;
        if (hasPts && _selected.Count == 2)
        {
            var stat = _services.MapEngine?.GetStaticScenario(_currentMap ?? string.Empty);
            var two = _selected.ToList();
            if (stat != null)
                pairLane = stat.Hyperlanes.FirstOrDefault(l =>
                    (l.From == two[0].Id && l.To == two[1].Id) || (l.From == two[1].Id && l.To == two[0].Id));
        }
        MenuItem? pairSplit = null, pairDelLane = null;
        if (pairLane != null)
        {
            pairSplit = Mi("staticmap.preview.split_lane", () => { _selectedLanes.Clear(); _selectedLanes.Add(pairLane!); SplitLanesDialog(); });
            pairDelLane = Mi("staticmap.preview.delete_lane", () => { var stat = _services.MapEngine?.GetStaticScenario(_currentMap ?? string.Empty); if (stat != null) stat.Hyperlanes.Remove(pairLane!); DrawPreview(); });
        }

        // R1 未选中：右击位置决定
        if (!hasPts && !hasLanes && !hasShapes)
        {
            if (hit2 != null)
            {
                _selected.Add(hit2); _selectedLanes.Clear(); DrawPreview();
                hasPts = true;
                copyPt.IsEnabled = delPt.IsEnabled = setPt.IsEnabled = true;
                snapPt.IsEnabled = true; mergePt.IsEnabled = CountLanes(hit2.Id) == 2;
                mirrorX.IsEnabled = mirrorY.IsEnabled = mirrorD.IsEnabled = mirrorO.IsEnabled = true;
                mirrorC.IsEnabled = rotateC.IsEnabled = flipX.IsEnabled = flipY.IsEnabled = true;
                laneCreate.IsEnabled = true;
                menu.Items.Add(copyPt); menu.Items.Add(pasteAny); menu.Items.Add(delPt); menu.Items.Add(setPt);
                menu.Items.Add(snapPt); menu.Items.Add(mergePt); menu.Items.Add(new Separator());
                menu.Items.Add(mirrorX); menu.Items.Add(mirrorY); menu.Items.Add(mirrorD); menu.Items.Add(mirrorO);
                menu.Items.Add(mirrorC); menu.Items.Add(rotateC); menu.Items.Add(flipX); menu.Items.Add(flipY);
                menu.Items.Add(new Separator());
            }
            else if (hitLane != null)
            {
                _selectedLanes.Add(hitLane); DrawPreview();
                hasLanes = true;
                copyLane.IsEnabled = delLane.IsEnabled = true;
                menu.Items.Add(copyLane); menu.Items.Add(pasteAny); menu.Items.Add(delLane);
            }
            else if (hitShapeR != null)
            {
                _selectedShapes.Clear(); _selectedShapes.Add(hitShapeR); DrawPreview();
                hasShapes = true;
                shapeSet.IsEnabled = shapeSnap.IsEnabled = shapeApply.IsEnabled = shapeDel.IsEnabled = true;
                menu.Items.Add(Mi("staticmap.preview.copy", CopySelection, true));
                menu.Items.Add(pasteAny);
                menu.Items.Add(shapeSet); menu.Items.Add(shapeSnap); menu.Items.Add(shapeApply); menu.Items.Add(shapeDel);
                menu.Items.Add(new Separator());
                mirrorX.IsEnabled = mirrorY.IsEnabled = mirrorD.IsEnabled = mirrorO.IsEnabled = true;
                mirrorC.IsEnabled = rotateC.IsEnabled = flipX.IsEnabled = flipY.IsEnabled = true;
                menu.Items.Add(mirrorX); menu.Items.Add(mirrorY); menu.Items.Add(mirrorD); menu.Items.Add(mirrorO);
                menu.Items.Add(mirrorC); menu.Items.Add(rotateC); menu.Items.Add(flipX); menu.Items.Add(flipY);
            }
            else if (hitImageR != null)
            {
                // R1C4：图像命中（预留）——单选该图像后显示图像菜单
                _selectedImages.Clear(); _selectedImages.Add(hitImageR); DrawPreview();
                hasImages = true;
                imgSet.IsEnabled = imgSnap.IsEnabled = imgApply.IsEnabled = imgDel.IsEnabled = true;
                menu.Items.Add(Mi("staticmap.preview.copy", CopySelection, true)); // 图像复制（复制所选图像）
                menu.Items.Add(pasteAny);
                menu.Items.Add(imgSet); menu.Items.Add(imgSnap); menu.Items.Add(imgApply); menu.Items.Add(imgDel);
                menu.Items.Add(new Separator());
                mirrorX.IsEnabled = mirrorY.IsEnabled = mirrorD.IsEnabled = mirrorO.IsEnabled = true;
                mirrorC.IsEnabled = rotateC.IsEnabled = true;
                flipX.IsEnabled = flipY.IsEnabled = true;
                menu.Items.Add(mirrorX); menu.Items.Add(mirrorY); menu.Items.Add(mirrorD); menu.Items.Add(mirrorO);
                menu.Items.Add(mirrorC); menu.Items.Add(rotateC); menu.Items.Add(flipX); menu.Items.Add(flipY);
            }
            else
            {
                var copyAll = Mi("staticmap.preview.copy", () => { _selected.Clear(); var all = _services.MapEngine?.GetStaticScenario(_currentMap ?? string.Empty)?.Systems; if (all != null) foreach (var s in all) _selected.Add(s); CopySelection(); });
                var delAll = Mi("staticmap.preview.delete", () => { var stat = _services.MapEngine?.GetStaticScenario(_currentMap ?? string.Empty); if (stat != null) { stat.Systems.Clear(); stat.Hyperlanes.Clear(); DrawPreview(); } });
                var snapAll = Mi("staticmap.preview.snap_to_grid", () => { var stat = _services.MapEngine?.GetStaticScenario(_currentMap ?? string.Empty); if (stat == null) return; _selected.Clear(); foreach (var s in stat.Systems) _selected.Add(s); SnapSelectionToGrid(); });
                mirrorX.IsEnabled = mirrorY.IsEnabled = mirrorD.IsEnabled = mirrorO.IsEnabled = true;
                mirrorC.IsEnabled = rotateC.IsEnabled = flipX.IsEnabled = flipY.IsEnabled = true;
                menu.Items.Add(copyAll); menu.Items.Add(pasteAny); menu.Items.Add(delAll); menu.Items.Add(snapAll);
                menu.Items.Add(new Separator());
                menu.Items.Add(mirrorX); menu.Items.Add(mirrorY); menu.Items.Add(mirrorD); menu.Items.Add(mirrorO);
                menu.Items.Add(mirrorC); menu.Items.Add(rotateC); menu.Items.Add(flipX); menu.Items.Add(flipY);
                menu.Items.Add(new Separator());
            }
        }
        // R2 选中点
        else if (hasPts && !hasLanes && !hasShapes)
        {
            laneCreate.IsEnabled = _selected.Count >= 1 && _selected.Count <= 2;
            menu.Items.Add(copyPt); menu.Items.Add(pasteAny); menu.Items.Add(delPt); menu.Items.Add(setPt);
            if (pairSplit != null) menu.Items.Add(pairSplit);
            if (pairDelLane != null) menu.Items.Add(pairDelLane);
            menu.Items.Add(snapPt); menu.Items.Add(mergePt);
            menu.Items.Add(new Separator());
            menu.Items.Add(mirrorX); menu.Items.Add(mirrorY); menu.Items.Add(mirrorD); menu.Items.Add(mirrorO);
            menu.Items.Add(mirrorC); menu.Items.Add(rotateC); menu.Items.Add(flipX); menu.Items.Add(flipY);
            menu.Items.Add(new Separator()); menu.Items.Add(clearSel);
        }
        // R3 选中边
        else if (hasLanes && !hasPts && !hasShapes)
        {
            menu.Items.Add(copyLane); menu.Items.Add(splitLane); menu.Items.Add(delLane); menu.Items.Add(clearSel);
        }
        // R4 选中图形
        else if (hasShapes && !hasPts && !hasLanes)
        {
            menu.Items.Add(Mi("staticmap.preview.copy", CopySelection, true));
            menu.Items.Add(pasteAny);
            menu.Items.Add(shapeDel); menu.Items.Add(shapeSet); menu.Items.Add(shapeSnap); menu.Items.Add(shapeApply);
            menu.Items.Add(new Separator());
            mirrorX.IsEnabled = mirrorY.IsEnabled = mirrorD.IsEnabled = mirrorO.IsEnabled = true;
            mirrorC.IsEnabled = rotateC.IsEnabled = flipX.IsEnabled = flipY.IsEnabled = true;
            menu.Items.Add(mirrorX); menu.Items.Add(mirrorY); menu.Items.Add(mirrorD); menu.Items.Add(mirrorO);
            menu.Items.Add(mirrorC); menu.Items.Add(rotateC); menu.Items.Add(flipX); menu.Items.Add(flipY);
            menu.Items.Add(clearSel);
        }
        // R7 点+图形
        else if (hasPts && hasShapes && !hasLanes)
        {
            menu.Items.Add(copyPt); menu.Items.Add(pasteAny); menu.Items.Add(delPt); menu.Items.Add(shapeDel);
            menu.Items.Add(snapPt); menu.Items.Add(mergePt);
            menu.Items.Add(new Separator());
            menu.Items.Add(mirrorX); menu.Items.Add(mirrorY); menu.Items.Add(mirrorD); menu.Items.Add(mirrorO);
            menu.Items.Add(mirrorC); menu.Items.Add(rotateC); menu.Items.Add(flipX); menu.Items.Add(flipY);
            menu.Items.Add(clearSel);
        }
        // R5 选中图像 / R6 图形+图像 / R8 点+图像 / R9 点+图形+图像（预留）
        else if (hasImages)
        {
            menu.Items.Add(Mi("staticmap.preview.copy", CopySelection, hasPts || hasShapes || hasImages));
            menu.Items.Add(pasteAny);
            menu.Items.Add(imgDel);
            if (hasShapes) menu.Items.Add(shapeDel);
            if (hasPts) menu.Items.Add(delPt);
            menu.Items.Add(imgSet); menu.Items.Add(imgSnap); menu.Items.Add(imgApply);
            if (hasPts)
            {
                mergePt.IsEnabled = allDeg2;
                menu.Items.Add(mergePt);
            }
            menu.Items.Add(new Separator());
            mirrorX.IsEnabled = mirrorY.IsEnabled = mirrorD.IsEnabled = mirrorO.IsEnabled = true;
            mirrorC.IsEnabled = rotateC.IsEnabled = flipX.IsEnabled = flipY.IsEnabled = true;
            menu.Items.Add(mirrorX); menu.Items.Add(mirrorY); menu.Items.Add(mirrorD); menu.Items.Add(mirrorO);
            menu.Items.Add(mirrorC); menu.Items.Add(rotateC); menu.Items.Add(flipX); menu.Items.Add(flipY);
            menu.Items.Add(clearSel);
        }
        // 其他组合（未覆盖）
        else
        {
            menu.Items.Add(copyPt); menu.Items.Add(pasteAny); menu.Items.Add(delPt); menu.Items.Add(clearSel);
        }

        // 导入图形入口
        var importShapeMenu = new MenuItem { Header = Loc("staticmap.shape.import") };
        var triMenu = new MenuItem { Header = Loc("staticmap.shape.triangle") };
        var triRegular = new MenuItem { Header = Loc("staticmap.shape.triangle_regular") };
        triRegular.Click += (_, _) => ImportShape(ShapeKind.Triangle, p);
        var triCustom = new MenuItem { Header = Loc("staticmap.shape.triangle_custom") };
        triCustom.Click += (_, _) => ImportShapeCustom(ShapeKind.Triangle, p);
        triMenu.Items.Add(triRegular); triMenu.Items.Add(triCustom);
        var rectMenu = new MenuItem { Header = Loc("staticmap.shape.rectangle") };
        var rectSquare = new MenuItem { Header = Loc("staticmap.shape.rect_square") };
        rectSquare.Click += (_, _) => ImportShape(ShapeKind.Rectangle, p);
        var rectLong = new MenuItem { Header = Loc("staticmap.shape.rect_long") };
        rectLong.Click += (_, _) => ImportShapeCustom(ShapeKind.Rectangle, p, longRect: true);
        var rectCustom = new MenuItem { Header = Loc("staticmap.shape.rect_custom") };
        rectCustom.Click += (_, _) => ImportShapeCustom(ShapeKind.Rectangle, p);
        rectMenu.Items.Add(rectSquare); rectMenu.Items.Add(rectLong); rectMenu.Items.Add(rectCustom);
        var hexItem = new MenuItem { Header = Loc("staticmap.shape.hexagon") };
        hexItem.Click += (_, _) => ImportShape(ShapeKind.Hexagon, p);
        var circleItem = new MenuItem { Header = Loc("staticmap.shape.circle") };
        circleItem.Click += (_, _) => ImportShape(ShapeKind.Circle, p);
        importShapeMenu.Items.Add(triMenu); importShapeMenu.Items.Add(rectMenu);
        importShapeMenu.Items.Add(hexItem); importShapeMenu.Items.Add(circleItem);
        // 顶部公共项：添加点 + 创建航道（所有右键菜单最上面）
        var addPointItem = new MenuItem { Header = Loc("staticmap.preview.add_point") };
        addPointItem.Click += (_, _) =>
        {
            var (mx, my) = ToMapCoords(p);
            var nsys = AddSystemAt((float)mx, (float)my);
            _selected.Clear();
            _selected.Add(nsys);
            DrawPreview();
        };
        menu.Items.Insert(0, addPointItem);
        menu.Items.Insert(1, laneCreate);
        menu.Items.Add(importShapeMenu);
        // 导入图像（预留——一直有效；图像功能后续实现）
        var importImageItem = new MenuItem { Header = Loc("staticmap.shape.import_image") };
        importImageItem.Click += (_, _) => ImportImage(p);
        menu.Items.Add(importImageItem);
        menu.PlacementTarget = PreviewCanvas;
        menu.IsOpen = true;
        menu.PlacementTarget = PreviewCanvas;
        menu.IsOpen = true;
    }

    /// <summary>复制选中点到剪贴板（克隆 + 选中点之间的航道，Id 保留供粘贴重建航道）。</summary>
    private void CopySelection()
    {
        _clipboard = _selected.Select(s => new SystemEntry
        {
            Id = s.Id,
            Name = s.Name,
            Position = new SystemPosition { X = s.Position.X, Y = s.Position.Y },
            Initializer = s.Initializer
        }).ToList();
        // 组合复制：选中的图形/图像一并进剪贴板
        _clipboardShapes = _selectedShapes.Select(s => new ShapeOverlay
        {
            Kind = s.Kind, Custom = s.Custom, EdgeCount = s.EdgeCount, Spacing = s.Spacing,
            Width = s.Width, Height = s.Height, WidthDiv = s.WidthDiv, HeightDiv = s.HeightDiv,
            OuterRadius = s.OuterRadius, InnerRadius = s.InnerRadius, RadialDiv = s.RadialDiv, CircumDiv = s.CircumDiv,
            Center = new SystemPosition { X = s.Center.X, Y = s.Center.Y }, Angle = s.Angle,
            Color = s.Color, ZValue = s.ZValue
        }).ToList();
        _clipboardImages = _selectedImages.Select(im => new ImageOverlay
        {
            Path = im.Path, Position = new SystemPosition { X = im.Position.X, Y = im.Position.Y },
            Width = im.Width, Height = im.Height, Angle = im.Angle,
            GenOptions = im.GenOptions.Clone(), AutoLanes = im.AutoLanes
        }).ToList();
        // 记录选中点之间的航道（源 Id 对，供粘贴重建）
        _clipboardLanes = new List<(string From, string To)>();
        var selIds = new HashSet<string>(_selected.Select(s => s.Id), StringComparer.Ordinal);
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap ?? string.Empty);
        if (stat != null)
        {
            foreach (var lane in stat.Hyperlanes)
            {
                if (selIds.Contains(lane.From) && selIds.Contains(lane.To))
                    _clipboardLanes.Add((lane.From, lane.To));
            }
        }
    }

    /// <summary>复制选中的航道及其头尾两个恒星（点 + 航道一起进剪贴板）。</summary>
    private void CopyLaneSelection()
    {
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap ?? string.Empty);
        _clipboard = new List<SystemEntry>();
        _clipboardLanes = new List<(string From, string To)>();
        _clipboardShapes = new List<ShapeOverlay>();
        _clipboardImages = new List<ImageOverlay>();
        if (stat == null)
            return;
        var laneIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in _selectedLanes)
        {
            laneIds.Add(lane.From);
            laneIds.Add(lane.To);
        }
        foreach (var s in stat.Systems)
        {
            if (!laneIds.Contains(s.Id))
                continue;
            _clipboard.Add(new SystemEntry
            {
                Id = s.Id,
                Name = s.Name,
                Position = new SystemPosition { X = s.Position.X, Y = s.Position.Y },
                Initializer = s.Initializer
            });
        }
        foreach (var lane in _selectedLanes)
            _clipboardLanes.Add((lane.From, lane.To));
    }

    /// <summary>粘贴：以点击位置为锚点（保持组内相对布局）创建新点并选中；重建选中点之间的航道。</summary>
    private void PasteSelection(Point p)
    {
        if (_clipboard.Count == 0 && _clipboardShapes.Count == 0 && _clipboardImages.Count == 0)
            return;
        var (bx, by) = ToMapCoords(p);
        double cxo = _clipboard.Count > 0 ? _clipboard.Average(s => s.Position.X)
            : _clipboardShapes.Count > 0 ? _clipboardShapes.Average(s => s.Center.X)
            : _clipboardImages.Average(im => im.Position.X);
        double cyo = _clipboard.Count > 0 ? _clipboard.Average(s => s.Position.Y)
            : _clipboardShapes.Count > 0 ? _clipboardShapes.Average(s => s.Center.Y)
            : _clipboardImages.Average(im => im.Position.Y);
        double offX = bx - cxo, offY = by - cyo;
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap!);
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal); // 源 Id → 新 Id
        _selected.Clear();
        foreach (var c in _clipboard)
        {
            var sys = AddSystemAt((float)(c.Position.X + offX), (float)(c.Position.Y + offY));
            sys.Name = c.Name;
            sys.Initializer = c.Initializer;
            idMap[c.Id] = sys.Id;
            _selected.Add(sys);
        }
        // 重建航道（选中点之间的）
        if (stat != null)
        {
            foreach (var (f, t) in _clipboardLanes)
            {
                if (idMap.TryGetValue(f, out var nf) && idMap.TryGetValue(t, out var nt))
                {
                    bool dup = stat.Hyperlanes.Any(l =>
                        (l.From == nf && l.To == nt) || (l.From == nt && l.To == nf));
                    if (!dup)
                        stat.Hyperlanes.Add(new Hyperlane(nf, nt));
                }
            }
        }
        // 粘贴图形/图像（相对锚点偏移）
        _selectedShapes.Clear();
        foreach (var c in _clipboardShapes)
        {
            var ns = new ShapeOverlay
            {
                Kind = c.Kind, Custom = c.Custom, EdgeCount = c.EdgeCount, Spacing = c.Spacing,
                Width = c.Width, Height = c.Height, WidthDiv = c.WidthDiv, HeightDiv = c.HeightDiv,
                OuterRadius = c.OuterRadius, InnerRadius = c.InnerRadius, RadialDiv = c.RadialDiv, CircumDiv = c.CircumDiv,
                Center = new SystemPosition { X = (float)(c.Center.X + offX), Y = (float)(c.Center.Y + offY) },
                Angle = c.Angle, Color = c.Color, ZValue = c.ZValue
            };
            _shapes.Add(ns);
            _selectedShapes.Add(ns);
        }
        _selectedImages.Clear();
        foreach (var c in _clipboardImages)
        {
            var ni = new ImageOverlay
            {
                Path = c.Path,
                Position = new SystemPosition { X = (float)(c.Position.X + offX), Y = (float)(c.Position.Y + offY) },
                Width = c.Width, Height = c.Height, Angle = c.Angle,
                GenOptions = c.GenOptions.Clone(), AutoLanes = c.AutoLanes
            };
            _images.Add(ni);
            _selectedImages.Add(ni);
        }
        DrawPreview();
    }

    /// <summary>旋转选中点：绕 (cx, cy) 旋转（弹窗输入角度，度）。</summary>
    private void RotateSelected(double cx, double cy)
    {
        var win = new Window
        {
            Title = Loc("staticmap.preview.rotate"),
            Width = 300,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.preview.rotate_angle") });
        var angleBox = new TextBox { Text = "90", Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(angleBox);
        var ok = new Button
        {
            Content = Loc("common.ok"),
            Width = 80,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(ok);
        win.Content = panel;
        if (win.ShowDialog() != true)
            return;
        if (!double.TryParse(angleBox.Text, out double angle))
            return;
        double rad = angle * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        foreach (var s in _selected)
        {
            double x = s.Position.X - cx, y = s.Position.Y - cy;
            s.Position.X = cx + x * cos - y * sin;
            s.Position.Y = cy + x * sin + y * cos;
        }
        _editMode = EditMode.None;
        DrawPreview();
    }

    /// <summary>添加新恒星点（返回新条目，Id 自动分配，避开现有 Id 与航道引用；坐标按点精度舍入）。</summary>
    /// <summary>实时读取点精度（galaxy.json global.behavior.point_precision；未设置默认 1）——改设置后立即生效。</summary>
    private int GetCurrentPrecision()
    {
        try
        {
            var cm = _services.ConfigManager;
            if (cm != null)
            {
                var pv = cm.Get("galaxy", "global.behavior.point_precision");
                if (pv is int pi)
                    return Math.Clamp(pi, 0, 3);
                else if (pv is long pl)
                    return Math.Clamp((int)pl, 0, 3);
            }
        }
        catch
        {
            // 默认 1
        }
        return 1;
    }

    private SystemEntry AddSystemAt(float x, float y)
    {
        var stat = _services.MapEngine!.GetStaticScenario(_currentMap!)!;
        int n = 1;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in stat.Systems)
            used.Add(s.Id);
        foreach (var lane in stat.Hyperlanes)
        {
            used.Add(lane.From);
            used.Add(lane.To);
        }
        while (used.Contains(n.ToString()))
            n++;
        double pow = Math.Pow(10, GetCurrentPrecision());
        var sys = new SystemEntry
        {
            Id = n.ToString(),
            Position = new SystemPosition { X = Math.Round(x * pow) / pow, Y = Math.Round(y * pow) / pow }
        };
        stat.Systems.Add(sys);
        return sys;
    }

    /// <summary>删除选中点，并删除所有涉及它们的超空间航道。</summary>
    private void DeleteSelected()
    {
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap ?? string.Empty);
        // 组合删除：选中的图形/图像一并删除
        foreach (var s in _selectedShapes.ToList()) { _shapes.Remove(s); _selectedShapes.Remove(s); }
        foreach (var im in _selectedImages.ToList()) { _images.Remove(im); _selectedImages.Remove(im); }
        if (stat == null || _selected.Count == 0)
            return;
        var ids = new HashSet<string>(_selected.Select(s => s.Id), StringComparer.Ordinal);
        stat.Hyperlanes.RemoveAll(l => ids.Contains(l.From) || ids.Contains(l.To));
        foreach (var s in _selected.ToList())
            stat.Systems.Remove(s);
        _selected.Clear();
        DrawPreview();
    }

    /// <summary>预览区移动：框选进行中画虚线矩形；中键平移进行中移动画布。</summary>
    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(PreviewCanvas);
        // Shift+右键拖动旋转（实时预览，从按下时刻状态相对旋转）
        if (_rotating)
        {
            double cur = RotAngleDeg(p);
            double delta = _rotStartAngle - cur; // 画布 Y 向下，视觉方向需取反
            double rad = delta * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            foreach (var (sys, sx, sy) in _rotSnap)
            {
                double x = sx - _rotCenterX, y = sy - _rotCenterY;
                sys.Position.X = _rotCenterX + x * cos - y * sin;
                sys.Position.Y = _rotCenterY + x * sin + y * cos;
            }
            DrawPreview();
            return;
        }
        // 中键拖动平移画布
        if (e.MiddleButton == MouseButtonState.Pressed && _panning)
        {
            double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
            double half = side / 2.0 - 4;
            double unitsPerPx = (500 / _zoom) / half;
            double dxPx = p.X - _panLast.X, dyPx = p.Y - _panLast.Y;
            _panX -= dxPx * unitsPerPx;
            _panY += dyPx * unitsPerPx;
            _panLast = p;
            DrawPreview();
            return;
        }
        if (_leftDown)
        {
            // 移动已选中点（拖动）
            if (_moving)
            {
                double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
                double half = side / 2.0 - 4;
                double unitsPerPx = (500 / _zoom) / half;
                double dx = (p.X - _moveLast.X) * unitsPerPx;
                double dy = -(p.Y - _moveLast.Y) * unitsPerPx; // 画布 Y 向下，坐标 Y 向上
                // 多选拖动：拖动任一选中图形/图像 → 全部选中对象一起相对移动（与点一致）
                if (_movingImage != null)
                {
                    foreach (var im in _selectedImages)
                    {
                        im.Position.X += dx;
                        im.Position.Y += dy;
                    }
                }
                if (_movingShape != null)
                {
                    foreach (var sh in _selectedShapes)
                    {
                        sh.Center.X += dx;
                        sh.Center.Y += dy;
                    }
                }
                foreach (var s in _selected)
                {
                    s.Position.X += dx;
                    s.Position.Y += dy;
                }
                _moveLast = p;
                DrawPreview();
                return;
            }
            double ddx = p.X - _leftDownPos.X, ddy = p.Y - _leftDownPos.Y;
            if (_boxStart == null && (Math.Abs(ddx) > 4 || Math.Abs(ddy) > 4))
                _boxStart = _leftDownPos; // 空处开始框选
            if (_boxStart.HasValue)
            {
                if (_boxRect == null)
                {
                    _boxRect = new Rectangle
                    {
                        Stroke = Brushes.DodgerBlue,
                        StrokeDashArray = new DoubleCollection { 4, 3 },
                        StrokeThickness = 1,
                        Fill = new SolidColorBrush(Color.FromArgb(0x20, 0x1E, 0x90, 0xFF))
                    };
                    PreviewCanvas.Children.Add(_boxRect);
                }
                var r = GetBoxRect(_boxStart.Value, p);
                Canvas.SetLeft(_boxRect, r.X);
                Canvas.SetTop(_boxRect, r.Y);
                _boxRect.Width = r.Width;
                _boxRect.Height = r.Height;
            }
        }
        else
        {
            PreviewCanvas.Cursor = _editMode == EditMode.None
                ? System.Windows.Input.Cursors.Arrow
                : System.Windows.Input.Cursors.Cross;
        }
    }

    private static Rect GetBoxRect(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    // ==================== 拖拽排序（静态列表 + 形状总表） ====================

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        _dragStart = e.GetPosition(list);
        var container = list.ContainerFromElement(e.OriginalSource as DependencyObject);
        var item = (container as ListBoxItem)?.Content as MapListItem;
        _dragItems.Clear();
        if (item != null && list.SelectedItems.Contains(item))
        {
            foreach (var si in list.SelectedItems)
                if (si is MapListItem m)
                    _dragItems.Add(m);
        }
        else if (item != null)
        {
            _dragItems.Add(item);
        }
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItems.Count == 0)
            return;
        var list = (ListBox)sender;
        var pos = e.GetPosition(list);
        if (Math.Abs(pos.X - _dragStart.X) < 5 && Math.Abs(pos.Y - _dragStart.Y) < 5)
            return;
        _dragging = true; // 拖拽排序期间不触发页面切换
        try
        {
            var ddata = new DataObject("SMT.MapListItems", new List<MapListItem>(_dragItems));
            DragDrop.DoDragDrop(list, ddata, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
        }
        _dragItems.Clear();
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("SMT.MapListItems"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            var list = (ListBox)sender;
            var pos = e.GetPosition(list);
            ShowInsertIndicator(GetDropIndex(list, pos));
            // 拖拽自动滚动：鼠标贴近可见区顶部/底部时列表自动上滑/下滑
            var scroller = FindVisualChild<ScrollViewer>(list);
            if (scroller != null)
            {
                if (pos.Y < 24)
                    scroller.LineUp();
                else if (pos.Y > list.ActualHeight - 24)
                    scroller.LineDown();
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                return typed;
            var found = FindVisualChild<T>(child);
            if (found != null)
                return found;
        }
        return null;
    }

    private void OnListDragLeave(object sender, DragEventArgs e)
        => InsertIndicator.Visibility = Visibility.Collapsed;

    private void ShowInsertIndicator(int index)
    {
        double y;
        if (index >= 0 && index < StaticMapList.Items.Count)
        {
            var container = StaticMapList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            y = container != null ? container.TransformToAncestor(StaticMapList).Transform(new Point(0, 0)).Y : 0;
        }
        else
        {
            var last = StaticMapList.ItemContainerGenerator.ContainerFromIndex(StaticMapList.Items.Count - 1) as ListBoxItem;
            y = last != null ? last.TransformToAncestor(StaticMapList).Transform(new Point(0, 0)).Y + last.ActualHeight : 0;
        }
        InsertIndicator.Margin = new Thickness(4, Math.Max(0, y - 1), 4, 0);
        InsertIndicator.Visibility = Visibility.Visible;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        InsertIndicator.Visibility = Visibility.Collapsed;
        var list = (ListBox)sender;
        if (e.Data.GetData("SMT.MapListItems") is not List<MapListItem> dragged || dragged.Count == 0)
            return;
        int target = GetDropIndex(list, e.GetPosition(list));
        var items = list.Items.Cast<MapListItem>().ToList();
        var draggedSet = new HashSet<string>(dragged.Select(d => d.Name), StringComparer.Ordinal);
        var before = items.Take(target).Count(i => draggedSet.Contains(i.Name));
        foreach (var d in dragged)
            items.RemoveAll(i => i.Name == d.Name);
        int insertAt = Math.Max(0, target - before);
        for (int i = 0; i < dragged.Count; i++)
            items.Insert(Math.Min(insertAt + i, items.Count), dragged[i]);
        ApplyOrder(items);
    }

    /// <summary>按列表顺序重算静态地图 priority（暂不落盘，保存功能待统一添加）。</summary>
    private void ApplyOrder(List<MapListItem> items)
    {
        var engine = _services.MapEngine;
        if (engine == null)
            return;
        // 混合列表：动态+静态共享 priority（与动态页 ApplyOrderAndSave 一致），拖动后统一重算
        int priority = 0;
        foreach (var item in items)
        {
            if (item.IsStatic)
            {
                var s = engine.GetStaticScenario(item.Name);
                if (s != null) s.Priority = priority++;
            }
            else
            {
                var d = engine.GetDynamicScenario(item.Name);
                if (d != null) d.Priority = priority++;
            }
        }
        ReloadMaps();
    }

    private static int GetDropIndex(ListBox list, Point pos)
    {
        for (int i = 0; i < list.Items.Count; i++)
        {
            var container = list.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (container != null && pos.Y < container.TransformToAncestor(list).Transform(new Point(0, 0)).Y + container.ActualHeight / 2)
                return i;
        }
        return list.Items.Count;
    }

    // ---- 形状总表拖拽 ----

    private void OnShapeListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("SMT.ShapeRowItems") is not List<ShapeRowItem> dragged || dragged.Count == 0)
            return;
        var list = (ListBox)sender;
        var items = list.Items.Cast<ShapeRowItem>().ToList();
        int target = GetShapeDropIndex(list, e.GetPosition(list));
        var draggedSet = new HashSet<ShapeRowItem>(dragged);
        var before = items.Take(target).Count(draggedSet.Contains);
        foreach (var d in dragged)
            items.Remove(d);
        int insertAt = Math.Max(0, target - before);
        for (int i = 0; i < dragged.Count; i++)
            items.Insert(Math.Min(insertAt + i, items.Count), dragged[i]);
        list.Items.Clear();
        foreach (var item in items)
            list.Items.Add(item);
        CommitShapeChecks();
    }

    private void OnShapeListMouseDown(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        _shapeDragStart = e.GetPosition(list);
        var container = list.ContainerFromElement(e.OriginalSource as DependencyObject);
        var item = (container as ListBoxItem)?.Content as ShapeRowItem;
        _shapeDragItems.Clear();
        if (item != null && list.SelectedItems.Contains(item))
        {
            foreach (var si in list.SelectedItems)
                if (si is ShapeRowItem s)
                    _shapeDragItems.Add(s);
        }
        else if (item != null)
        {
            _shapeDragItems.Add(item);
        }
    }

    private void OnShapeListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _shapeDragItems.Count == 0)
            return;
        var list = (ListBox)sender;
        var pos = e.GetPosition(list);
        if (Math.Abs(pos.X - _shapeDragStart.X) < 5 && Math.Abs(pos.Y - _shapeDragStart.Y) < 5)
            return;
        var sddata = new DataObject("SMT.ShapeRowItems", new List<ShapeRowItem>(_shapeDragItems));
            DragDrop.DoDragDrop(list, sddata, DragDropEffects.Move);
        _shapeDragItems.Clear();
    }

    private void OnShapeListDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("SMT.ShapeRowItems"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            ShowShapeInsertIndicator(GetShapeDropIndex((ListBox)sender, e.GetPosition((ListBox)sender)));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnShapeListDragLeave(object sender, DragEventArgs e)
        => ShapeInsertIndicator.Visibility = Visibility.Collapsed;

    private void ShowShapeInsertIndicator(int index)
    {
        double y;
        if (index >= 0 && index < ShapeListBox.Items.Count)
        {
            var container = ShapeListBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            y = container != null ? container.TransformToAncestor(ShapeListBox).Transform(new Point(0, 0)).Y : 0;
        }
        else
        {
            var last = ShapeListBox.ItemContainerGenerator.ContainerFromIndex(ShapeListBox.Items.Count - 1) as ListBoxItem;
            y = last != null ? last.TransformToAncestor(ShapeListBox).Transform(new Point(0, 0)).Y + last.ActualHeight : 0;
        }
        ShapeInsertIndicator.Margin = new Thickness(2, Math.Max(0, y - 1), 2, 0);
        ShapeInsertIndicator.Visibility = Visibility.Visible;
    }

    private static int GetShapeDropIndex(ListBox list, Point pos)
    {
        for (int i = 0; i < list.Items.Count; i++)
        {
            var container = list.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (container != null && pos.Y < container.TransformToAncestor(list).Transform(new Point(0, 0)).Y + container.ActualHeight / 2)
                return i;
        }
        return list.Items.Count;
    }

    // ==================== 右键操作 ====================

    /// <summary>全部规整化：地图本地化规整化（仅内存；保存时落盘）。</summary>
    private void OnMapNormalize(object sender, RoutedEventArgs e)
    {
        var engine = _services.MapEngine;
        if (engine == null)
            return;
        try
        {
            engine.NormalizeLocalisation(); // 成功不弹窗（仅失败弹窗）
        }
        catch (Exception ex)
        {
            MessageBox.Show($"NormalizeLocalisation failed: {ex.Message}", "Stellaris Mod Tools",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>保存全部地图（统一保存，用户显式触发）。</summary>
    private void OnMapSave(object sender, RoutedEventArgs e)
        => SaveAllMaps();

    /// <summary>导航切到本页时刷新（重读点精度设置等）。</summary>
    public void Refresh()
    {
        try
        {
            var cm = _services.ConfigManager;
            if (cm != null)
            {
                var pv = cm.Get("galaxy", "global.behavior.point_precision");
                if (pv is int pi)
                    _pointPrecision = Math.Clamp(pi, 0, 3);
                else if (pv is long pl)
                    _pointPrecision = Math.Clamp((int)pl, 0, 3);
            }
        }
        catch
        {
            // 默认 1
        }
        if (_currentMap != null)
        {
            BuildForms();
            DrawPreview();
        }
    }

    /// <summary>统一保存：动态 + 静态全部地图（文件名 = 地图 key）+ 地图本地化 + 占位样式映射（写银河类别）。</summary>
    private void SaveAllMaps()
    {
        var engine = _services.MapEngine;
        if (engine == null)
            return;

        // 规范格式保存（SaveRunner）：转圈进度窗口 + 后台线程 + 完成关闭 + 仅失败弹窗
        SaveRunner.Run(_services, "status.saving",
            () => engine.SaveAllScenarios(),
            onSuccess: () =>
            {
                SaveMapConfig(engine);
                // 保存后刷新列表（重编号/优先级变化等）
                ReloadMaps();
                if (_currentMap != null)
                    SelectMap(_currentMap);
            },
            failMessage: _services.Localisation.Get("staticmap.save_failed"));
    }

    /// <summary>把静态地图绑定样式 / 锁定本地化 / 清空本地化 写入银河类别 galaxy.json 的 maps 节点（2 级：maps.{mapName}.*）。</summary>
    private void SaveMapConfig(GalaxyMapEngine engine)
    {
        var cm = _services.ConfigManager;
        if (cm == null)
            return;
        var mapsObj = new Dictionary<string, object>();
        foreach (var (mapName, styleName) in engine.StaticStyleMapping)
            mapsObj[mapName] = new Dictionary<string, object> { ["bound_style"] = styleName };
        // 锁定本地化 / 清空本地化（动态 + 静态）
        foreach (var (name, d) in engine.DynamicScenarios)
        {
            if (!mapsObj.TryGetValue(name, out var eo))
            {
                eo = new Dictionary<string, object>();
                mapsObj[name] = eo;
            }
            var e2 = (Dictionary<string, object>)eo;
            e2["lock_localisation"] = d.LockLocalisation;
            e2["clear_file"] = d.ClearFile;
        }
        foreach (var (name, s) in engine.StaticScenarios)
        {
            if (!mapsObj.TryGetValue(name, out var eo))
            {
                eo = new Dictionary<string, object>();
                mapsObj[name] = eo;
            }
            var e2 = (Dictionary<string, object>)eo;
            e2["lock_localisation"] = s.LockLocalisation;
            e2["clear_file"] = s.ClearFile;
        }
        try
        {
            cm.SetBatch("galaxy", new Dictionary<string, object> { ["maps"] = mapsObj });
        }
        catch
        {
            // 映射写失败不阻断主保存
        }
    }

    private void AddMap()
    {
        var engine = _services.MapEngine;
        if (engine == null)
            return;
        string name = $"{_services.Localisation.Get("dynmap.new_static")}_{DateTime.Now:HHmmss}";
        engine.AddStaticScenario(new StaticScenario { Name = name, SupportedShapes = new List<string>() });
        ReloadMaps();
        SelectMap(name);
    }

    private void CopyMap()
    {
        var engine = _services.MapEngine;
        if (engine == null || _currentMap == null)
            return;
        var stat = engine.GetStaticScenario(_currentMap);
        if (stat == null)
            return;
        string newName = $"{_currentMap}_copy";
        int n = 1;
        while (engine.GetStaticScenario(newName) != null)
            newName = $"{_currentMap}_copy_{++n}";
        var copy = stat.Clone();
        copy.Name = newName;
        engine.AddStaticScenario(copy);
        ReloadMaps();
        SelectMap(newName);
    }

    private void RenameMap()
    {
        var engine = _services.MapEngine;
        if (engine == null || _currentMap == null)
            return;
        var box = new TextBox { Text = _currentMap, MinWidth = 180 };
        var dlg = new Window
        {
            Title = _services.Localisation.Get("dynmap.rename"),
            Width = 280,
            Height = 120,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this)
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(box);
        var ok = new Button
        {
            Content = _services.Localisation.Get("roots.ok"),
            Width = 80,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        panel.Children.Add(ok);
        dlg.Content = panel;
        if (dlg.ShowDialog() != true)
            return;
        string newName = box.Text.Trim();
        if (newName.Length == 0 || newName == _currentMap || engine.GetStaticScenario(newName) != null)
            return;
        // 同步改静态字典 + 占位样式 key + 内存映射
        engine.RenameStaticScenario(_currentMap, newName);
        ReloadMaps();
        SelectMap(newName);
    }

    private void DeleteMap()
    {
        var engine = _services.MapEngine;
        if (engine == null)
            return;
        var selected = StaticMapList.SelectedItems.Cast<MapListItem>().ToList();
        if (selected.Count == 0)
            return;
        foreach (var item in selected)
            engine.DeleteScenario(item.Name);
        ReloadMaps();
    }

    private void SelectMap(string name)
    {
        foreach (object o in StaticMapList.Items)
        {
            if (o is MapListItem item && item.Name == name)
            {
                StaticMapList.SelectedItem = item;
                // 延迟滚动确保容器已生成（新增/切换后项可见）
                var it = item;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => StaticMapList.ScrollIntoView(it)));
                break;
            }
        }
    }

    // ==================== 辅助 ====================

    private string LocName(string shape)
    {
        var styleEngine = _services.StyleEngine;
        var loc = _services.Localisation;
        if (styleEngine != null)
        {
            string uiLang = loc.CurrentLanguage;
            string modLang = MapUiLangToModLang(uiLang);
            return styleEngine.GetLocalisedText(shape, modLang)
                   ?? styleEngine.GetLocalisedText(shape, "english")
                   ?? shape;
        }
        return shape;
    }

    private static string MapUiLangToModLang(string uiLang) => uiLang.ToLowerInvariant() switch
    {
        "zh-cn" => "simp_chinese",
        "zh-tw" => "trad_chinese",
        "ja" or "ja-jp" => "japanese",
        "ko" or "ko-kr" => "korean",
        "fr" or "fr-fr" => "french",
        "de" or "de-de" => "german",
        "es" or "es-es" => "spanish",
        "ru" or "ru-ru" => "russian",
        "pt" or "pt-br" => "braz_por",
        "pl" or "pl-pl" => "polish",
        _ => "english"
    };

    /// <summary>范围行：标签 + 最小 + 最大 + 默认 四个输入（照抄原版 num_wormhole_pairs 等范围块，不吞参数）。</summary>
    private FrameworkElement BuildRangeRow(string labelText, IntRange range, int defValue, Action<int, int, int> onCommit)
    {
        var label = new TextBlock
        {
            Text = labelText,
            Width = 170,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var minBox = new TextBox { Text = range.Min.ToString(), Width = 42, Margin = new Thickness(4, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
        var maxBox = new TextBox { Text = range.Max.ToString(), Width = 42, Margin = new Thickness(4, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
        var defBox = new TextBox { Text = defValue.ToString(), Width = 42, Margin = new Thickness(4, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
        void Commit()
        {
            if (int.TryParse(minBox.Text, out int mn) && int.TryParse(maxBox.Text, out int mx)
                && int.TryParse(defBox.Text, out int dv))
                onCommit(mn, mx, dv);
        }
        minBox.LostFocus += (_, _) => Commit();
        maxBox.LostFocus += (_, _) => Commit();
        defBox.LostFocus += (_, _) => Commit();
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(label);
        row.Children.Add(defBox);
        row.Children.Add(new TextBlock { Text = "def", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gray });
        row.Children.Add(maxBox);
        row.Children.Add(new TextBlock { Text = "max", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gray });
        row.Children.Add(minBox);
        row.Children.Add(new TextBlock { Text = "min", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gray });
        return row;
    }

    private FrameworkElement BuildFieldRow(string labelText, string value, Action<string> onCommit)
    {
        var label = new TextBlock
        {
            Text = labelText,
            Width = 170,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var box = new TextBox
        {
            Text = value,
            Margin = new Thickness(4, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        box.LostFocus += (_, _) => onCommit(box.Text);
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(label);
        row.Children.Add(box);
        return row;
    }

    private void AddEmpireFields(StackPanel panel, IntRange range, int def, Action<int, int, int> commit)
    {
        var loc = _services.Localisation;
        panel.Children.Add(BuildFieldRow(loc.Get("dynmap.empire_min"), range.Min.ToString(),
            v => commit(ParseInt(v, range.Min), range.Max, def)));
        panel.Children.Add(BuildFieldRow(loc.Get("dynmap.empire_max"), range.Max.ToString(),
            v => commit(range.Min, ParseInt(v, range.Max), def)));
        panel.Children.Add(BuildFieldRow(loc.Get("dynmap.empire_default"), def.ToString(),
            v => commit(range.Min, range.Max, ParseInt(v, def))));
    }


    /// <summary>环选择框样式的行：定宽标签 + CheckBox（参考星系样式"环"选择框）。</summary>
    private static FrameworkElement BuildRingRow(string labelText, CheckBox box)
    {
        var label = new TextBlock
        {
            Text = labelText,
            Width = 170,
            VerticalAlignment = VerticalAlignment.Center
        };
        box.Margin = new Thickness(4, 2, 0, 2);
        box.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(label);
        row.Children.Add(box);
        return row;
    }

    private static FrameworkElement BuildLocControlRow(string labelText, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = labelText, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(label);
        grid.Children.Add(control);
        return grid;
    }

    private static int ParseInt(string s, int fallback)
        => int.TryParse(s, out int v) ? v : fallback;

    private static double ParseDouble(string s, double fallback)
        => double.TryParse(s, out double v) ? v : fallback;

    private static SolidColorBrush? TryParseHex(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex.Trim())!);
        }
        catch
        {
            return null;
        }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 列表高度由 XAML Row0 的 2* Star 行驱动（随窗口等比放大 + splitter 可拖），不再用像素 Height 覆盖
        DrawPreview();
    }

    /// <summary>形状总表项：样式名 + 勾选 + 本地化名 + 上限。</summary>
    private sealed class ShapeRowItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
        public string CapacityText { get; set; } = string.Empty;
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>地图列表项：类型标记 + 本地化显示名（动态/静态）。</summary>
    private sealed class MapListItem
    {
        public string Name { get; }
        public bool IsStatic { get; }
        public int Priority { get; }
        public string Display { get; }
        public int StarCount { get; }

        public MapListItem(string name, bool isStatic, int priority, string display, int starCount)
        {
            Name = name;
            IsStatic = isStatic;
            Priority = priority;
            Display = display;
            StarCount = starCount;
        }

        public override string ToString() => $"{(IsStatic ? "[静态] " : "[动态] ")}{Display}";

    }

    /// <summary>列表分隔条拖完：行高转回 Star 比例——窗口缩放时列表跟随放大（WPF 默认转固定 px）。</summary>
    private void OnListSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (sender is not GridSplitter splitter || splitter.Parent is not Grid grid || grid.RowDefinitions.Count < 3)
            return;
        double total = 0;
        foreach (var r in grid.RowDefinitions)
            total += r.ActualHeight;
        if (total <= 0)
            return;
        double ratio = grid.RowDefinitions[0].ActualHeight / total;
        grid.RowDefinitions[0].Height = new GridLength(ratio, GridUnitType.Star);
        grid.RowDefinitions[2].Height = new GridLength(Math.Max(0.05, 1 - ratio), GridUnitType.Star);
    }



public enum ShapeKind { Triangle, Rectangle, Hexagon, Circle }

/// <summary>图像覆盖层（预留接口）：图像功能后续实现——结构已支持图像选中/组合/右键菜单。</summary>
public sealed class ImageOverlay
{
    public string Path = string.Empty;          // 图像文件路径
    public SystemPosition Position = new();     // 位置
    public double Width = 100;                  // 宽度
    public double Height = 100;                 // 高度
    public double Angle = 0;                    // 旋转角度
    public Stellaris.Engine.GalaxyMap.ImageGenerationOptions GenOptions = new(); // 生成选项（通道/密度等）
    public bool AutoLanes = true;               // 自动生成超空间航道（三角格网）
}

/// <summary>图形覆盖层：正三角形 / 矩形（正方形）/ 正六边形——点阵生成模板（应用后生成恒星点 + 格网航道）。</summary>
public sealed class ShapeOverlay
{
    public ShapeKind Kind;
    public bool Custom;            // 自定义形状（自定义三角形 / 长方形 / 自定义矩形）
    public int EdgeCount = 5;      // 规则形状：一条边上的间距数（正整数）
    public double Spacing = 20;    // 规则形状：间距长度（小数）
    public double Width = 200;     // 自定义：宽（长度）
    public double Height = 150;    // 自定义：高（长度）
    public double ZValue = 0;      // 图形高度（Z）——应用时生成点统一高度
    public double OuterRadius = 200;  // 圆环：外半径
    public double InnerRadius = 100;  // 圆环：内半径（>0 时环形空洞）
    public int RadialDiv = 2;      // 圆：径向份数（环数）
    public int CircumDiv = 36;     // 圆：圆周份数
    public int WidthDiv = 5;       // 自定义：宽分成几份（正整数，格点数）
    public int HeightDiv = 5;      // 自定义：高分成几份（正整数）
    public SystemPosition Center = new();
    public double Angle = 0;       // 角度（度，绕中心）
    public System.Windows.Media.Color Color = System.Windows.Media.Color.FromArgb(0xC0, 0x2E, 0xCC, 0x71);

    /// <summary>定位点数量 = 顶角数 + 1 个中心（三角形4、矩形5、六边形7）。</summary>
    public int LocatorCount
        => Kind switch { ShapeKind.Triangle => 4, ShapeKind.Rectangle => 5, ShapeKind.Circle => 1, _ => 7 };

    public int VertexCount => Kind switch { ShapeKind.Triangle => 3, ShapeKind.Rectangle => 4, ShapeKind.Circle => 1, _ => 6 };

    /// <summary>外接半径：三角形 R=side/√3；矩形（正方形）R=半对角线；六边形 R=边长。</summary>
    public double Circumradius
    {
        get
        {
            double side = EdgeCount * Spacing;
            return Kind switch
            {
                ShapeKind.Triangle => side / Math.Sqrt(3),
                ShapeKind.Rectangle => side / 2.0 * Math.Sqrt(2),
                _ => side
            };
        }
    }

    /// <summary>
    /// 定位点（世界坐标，含旋转）：前 n 个为顶角，最后一个为中心。
    /// 矩形（正方形）**横平竖直**（顶角 = 中心 ± 半边长，角度 0 时不歪 45°）；
    /// 三角形/六边形按外接圆等分（角度 0 时顶点在 0°/120°/240°、0°/60°/…）。
    /// </summary>
    /// <summary>
    /// 定位点（世界坐标，含旋转）：**顶点直接按创建参数（边长 = EdgeCount×Spacing / 宽高份数）计算**，
    /// 与应用生成的点阵顶点完全一致（不偷懒用外接圆）——矩形横平竖直、三角形顶角朝上、六边形标准。
    /// 最后一个 = 中心。
    /// </summary>
    public List<SystemPosition> GetLocators()
    {
        double s = Spacing;
        double sq3 = Math.Sqrt(3.0);
        var list = new List<SystemPosition>();
        var corners = new List<(double X, double Y)>();
        if (Kind == ShapeKind.Circle)
        {
            list.Add(new SystemPosition { X = Center.X, Y = Center.Y });
            return list;
        }
        if (Kind == ShapeKind.Rectangle)
        {
            double hw = Custom ? Width / 2.0 : EdgeCount * s / 2.0;
            double hh = Custom ? Height / 2.0 : EdgeCount * s / 2.0;
            corners.Add((-hw, -hh));
            corners.Add((hw, -hh));
            corners.Add((hw, hh));
            corners.Add((-hw, hh));
        }
        else if (Kind == ShapeKind.Triangle)
        {
            if (Custom)
            {
                // 自定义三角形 = 等腰：中心 = 几何重心（底边上方 H/3）
                // 顶点相对重心：底左/底右 y=-H/3、顶 y=2H/3
                corners.Add((-Width / 2.0, -Height / 3.0));   // 底左
                corners.Add((Width / 2.0, -Height / 3.0));    // 底右
                corners.Add((0, Height * 2.0 / 3.0));         // 顶点
            }
            else
            {
                // 规则等边三角：三角格顶点 (n,0)、(0,n)、(0,0) 的世界坐标（与 GridToWorld 居中一致）
                double n = EdgeCount;
                double g = n / 3.0;
                corners.Add(((n - g * 1.5) * s, (-g) * sq3 / 2.0 * s));            // 右下
                corners.Add(((n / 2.0 - g * 1.5) * s, (n - g) * sq3 / 2.0 * s));   // 顶部
                corners.Add(((-g * 1.5) * s, (-g) * sq3 / 2.0 * s));               // 左下
            }
        }
        else
        {
            // 三角格六边形 6 顶点（与 GridToWorld 一致）
            double n = EdgeCount;
            corners.Add((n * s, 0));
            corners.Add((n / 2.0 * s, n * sq3 / 2.0 * s));
            corners.Add((-n / 2.0 * s, n * sq3 / 2.0 * s));
            corners.Add((-n * s, 0));
            corners.Add((-n / 2.0 * s, -n * sq3 / 2.0 * s));
            corners.Add((n / 2.0 * s, -n * sq3 / 2.0 * s));
        }

        double rad = Angle * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        foreach (var (cx, cy) in corners)
        {
            double rx = cx * cos - cy * sin;
            double ry = cx * sin + cy * cos;
            list.Add(new SystemPosition { X = Center.X + (float)rx, Y = Center.Y + (float)ry });
        }
        list.Add(new SystemPosition { X = Center.X, Y = Center.Y });
        return list;
    }

    /// <summary>图形外接包围盒（屏幕坐标转换前，世界坐标）。</summary>
    public double BoundsRadius => Circumradius + 4;
}

    /// <summary>导入图形：在点击位置创建默认图形（边长 5 间距、默认间距、中心=点击位置）。</summary>
    /// <summary>导入图像：选 PNG → 创建 ImageOverlay（默认宽度 200 地图坐标、保持比例、中心=点击位置）。</summary>
    private void ImportImage(Point p)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图像文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Title = Loc("staticmap.shape.import_image")
        };
        if (dlg.ShowDialog() != true)
            return;
        var (cx, cy) = ToMapCoords(p);
        using var bmp = SkiaSharp.SKBitmap.Decode(dlg.FileName);
        double imgW = bmp?.Width ?? 100.0, imgH = bmp?.Height ?? 100.0;
        double scale = 200.0 / Math.Max(1, imgW);
        var img = new ImageOverlay
        {
            Path = dlg.FileName,
            Position = new SystemPosition { X = (float)Math.Round(cx), Y = (float)Math.Round(cy) },
            Width = imgW * scale,
            Height = imgH * scale,
            Angle = 0,
            GenOptions = new Stellaris.Engine.GalaxyMap.ImageGenerationOptions
            {
                UseR = true, UseG = true, UseB = true, UseA = true,
                Invert = false,
                Mode = Stellaris.Engine.GalaxyMap.GenerationMode.Spacing,
                MinDistance = 10.0,
                Density = 0.25
            },
            AutoLanes = true
        };
        _images.Add(img);
        _selectedImages.Clear();
        _selectedImages.Add(img);
        DrawPreview();
    }

    private void ImportShape(ShapeKind kind, Point p)
    {
        var (cx, cy) = ToMapCoords(p);
        var shape = new ShapeOverlay
        {
            Kind = kind,
            EdgeCount = 5,
            Spacing = GetPreviewGridSpacing(),
            Center = new SystemPosition { X = (float)Math.Round(cx), Y = (float)Math.Round(cy) },
            Angle = 0
        };
        _shapes.Add(shape);
        _selectedShapes.Clear();
        _selectedShapes.Add(shape);
        Diag($"[Shape] ImportShape kind={kind} 位置=({cx:0.##},{cy:0.##})");
        DrawPreview();
    }

    /// <summary>图像设置弹窗：宽/高/旋转 + 通道组合（ARGB 4 个 CheckBox + 反向）+ 密度% + 自动航道。</summary>
    private void ImageSettingsDialog()
    {
        var img = _selectedImages.FirstOrDefault();
        if (img == null)
            return;
        var win = new Window
        {
            Title = Loc("staticmap.shape.import_image"),
            MinWidth = 360, MinHeight = 320, Width = 420, Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.CanResize
        };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.image.width") });
        var wBox = new TextBox { Text = img.Width.ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(wBox);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.image.height") });
        var hBox = new TextBox { Text = img.Height.ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(hBox);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.image.angle") });
        var aBox = new TextBox { Text = img.Angle.ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(aBox);
        // 通道组合
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.image.channels") });
        var chR = new CheckBox { Content = "R", IsChecked = img.GenOptions.UseR, Margin = new Thickness(0, 4, 0, 2) };
        var chG = new CheckBox { Content = "G", IsChecked = img.GenOptions.UseG, Margin = new Thickness(0, 2, 0, 2) };
        var chB = new CheckBox { Content = "B", IsChecked = img.GenOptions.UseB, Margin = new Thickness(0, 2, 0, 2) };
        var chA = new CheckBox { Content = "A", IsChecked = img.GenOptions.UseA, Margin = new Thickness(0, 2, 0, 2) };
        var chInv = new CheckBox { Content = Loc("staticmap.image.invert"), IsChecked = img.GenOptions.Invert, Margin = new Thickness(0, 2, 0, 4) };
        panel.Children.Add(chR); panel.Children.Add(chG); panel.Children.Add(chB); panel.Children.Add(chA); panel.Children.Add(chInv);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.image.interval") });
        var ivBox = new TextBox
        {
            Text = img.GenOptions.MinDistance > 0 ? img.GenOptions.MinDistance.ToString("0.##") : "10",
            Margin = new Thickness(0, 4, 0, 8)
        };
        panel.Children.Add(ivBox);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.image.density") });
        var dBox = new TextBox { Text = (img.GenOptions.Density * 100.0).ToString("0"), Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(dBox);
        var autoLanes = new CheckBox { Content = Loc("staticmap.image.auto_lanes"), IsChecked = img.AutoLanes, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(autoLanes);
        var ok = new Button { Content = Loc("common.ok"), Width = 80, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        ok.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(ok);
        scroll.Content = panel;
        win.Content = scroll;
        if (win.ShowDialog() != true)
            return;
        if (double.TryParse(wBox.Text, out double w) && w > 0) img.Width = w;
        if (double.TryParse(hBox.Text, out double h) && h > 0) img.Height = h;
        if (double.TryParse(aBox.Text, out double ag)) img.Angle = ag;
        img.GenOptions.UseR = chR.IsChecked == true;
        img.GenOptions.UseG = chG.IsChecked == true;
        img.GenOptions.UseB = chB.IsChecked == true;
        img.GenOptions.UseA = chA.IsChecked == true;
        img.GenOptions.Invert = chInv.IsChecked == true;
        if (double.TryParse(ivBox.Text, out double iv) && iv > 0 && iv <= 500) img.GenOptions.MinDistance = iv;
        if (double.TryParse(dBox.Text, out double dp) && dp >= 0 && dp <= 100)
            img.GenOptions.Density = dp / 100.0;
        img.AutoLanes = autoLanes.IsChecked == true;
        DrawPreview();
    }

    /// <summary>应用图像：调引擎转点集（通道/密度），按需自动三角格航道。</summary>
    private void ApplyImages(List<ImageOverlay> images)
    {
        var engine = _services.MapEngine;
        if (engine == null || _currentMap == null)
            return;
        foreach (var img in images)
        {
            if (!System.IO.File.Exists(img.Path))
                continue;
            try
            {
                // 生成范围 = 用户设置的图像宽/高（地图坐标）——否则固定映射 ±500 全图导致点爆开
                img.GenOptions.TargetWidth = img.Width;
                img.GenOptions.TargetHeight = img.Height;
                // 生成中心 = 图像所在位置（否则固定生成到地图中心）
                img.GenOptions.CenterX = img.Position.X;
                img.GenOptions.CenterY = img.Position.Y;
                engine.GeneratePointsFromImage(_currentMap, img.Path, img.GenOptions);
            }
            catch (Exception ex)
            {
                Diag($"[Image] 应用失败: {ex.Message}");
                MessageBox.Show($"图像应用失败: {ex.Message}", "Stellaris Mod Tools",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }
            // 自动航道（三角格网）：对新生成点按最小间距邻近连接（简化三角格）
            if (img.AutoLanes)
                AutoTriangulateNewPoints();
            _images.Remove(img);
            _selectedImages.Remove(img);
        }
        DrawPreview();
    }

    /// <summary>图像应用后自动航道：新点按最小间距邻近连接（三角格网近似——每点连最近两点）。</summary>
    private void AutoTriangulateNewPoints()
    {
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap ?? string.Empty);
        if (stat == null)
            return;
        // 连接每个点与其最近的两个未连接点（间距阈值内）
        double maxDist = 30.0;
        var systems = stat.Systems;
        for (int i = 0; i < systems.Count; i++)
        {
            var a = systems[i];
            var near = systems
                .Where(b => b != a)
                .Select(b => (b, D: (b.Position.X - a.Position.X) * (b.Position.X - a.Position.X)
                    + (b.Position.Y - a.Position.Y) * (b.Position.Y - a.Position.Y)))
                .Where(x => x.D <= maxDist * maxDist)
                .OrderBy(x => x.D)
                .Take(2);
            foreach (var (b, _) in near)
            {
                bool dup = stat.Hyperlanes.Any(l =>
                    (l.From == a.Id && l.To == b.Id) || (l.From == b.Id && l.To == a.Id));
                if (!dup && a.Id != b.Id)
                    stat.Hyperlanes.Add(new Hyperlane(a.Id, b.Id));
            }
        }
    }

    /// <summary>导入自定义图形（自定义三角形 / 长方形 / 自定义矩形）：宽/高按份数（设置里可调）。</summary>
    private void ImportShapeCustom(ShapeKind kind, Point p, bool longRect = false)
    {
        var (cx, cy) = ToMapCoords(p);
        var shape = new ShapeOverlay
        {
            Kind = kind,
            EdgeCount = 5,
            Spacing = GetPreviewGridSpacing(),
            Center = new SystemPosition { X = (float)Math.Round(cx), Y = (float)Math.Round(cy) },
            Angle = 0,
            Custom = true,
            WidthDiv = 5,
            HeightDiv = longRect ? 3 : 5
        };
        _shapes.Add(shape);
        _selectedShapes.Clear();
        _selectedShapes.Add(shape);
        DrawPreview();
    }

    /// <summary>图形设置弹窗：边长（正整数，一条边上的间距数）/ 间距（小数）/ 角度 / 颜色。</summary>
    private void ShapeSettingsDialog()
    {
        var shape = _selectedShapes.FirstOrDefault();
        if (shape == null)
            return;
        Diag($"[Shape] Settings kind={shape.Kind} Custom={shape.Custom}");
        var win = new Window
        {
            Title = Loc("staticmap.shape.settings"),
            MinWidth = 360,
            MinHeight = 320,
            Width = 420,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.CanResize
        };
        var winScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(14) };
        TextBox? edgeBox = null, spacingBox = null, widthLenBox = null, heightLenBox = null, widthBox = null, heightBox = null;
        TextBox? radiusBox = null, innerBox = null, radialBox = null, circumBox = null;
        if (shape.Kind == ShapeKind.Circle)
        {
            panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.outer_radius") });
            radiusBox = new TextBox { Text = shape.OuterRadius.ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(radiusBox);
            panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.inner_radius") });
            innerBox = new TextBox { Text = shape.InnerRadius.ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(innerBox);
            panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.radial_div") });
            radialBox = new TextBox { Text = shape.RadialDiv.ToString(), Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(radialBox);
            panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.circum_div") });
            circumBox = new TextBox { Text = shape.CircumDiv.ToString(), Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(circumBox);
        }
        else if (shape.Custom)
        {
            // 自定义：宽（长度）/ 高（长度）/ 宽分成几份 / 高分成几份
            panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.width") });
            widthLenBox = new TextBox { Text = shape.Width.ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(widthLenBox);
            panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.height") });
            heightLenBox = new TextBox { Text = shape.Height.ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(heightLenBox);
            panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.width_div") });
            widthBox = new TextBox { Text = shape.WidthDiv.ToString(), Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(widthBox);
            panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.height_div") });
            heightBox = new TextBox { Text = shape.HeightDiv.ToString(), Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(heightBox);
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.edge_count") });
            edgeBox = new TextBox { Text = shape.EdgeCount.ToString(), Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(edgeBox);
            panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.spacing") });
            spacingBox = new TextBox { Text = shape.Spacing.ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
            panel.Children.Add(spacingBox);
        }
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.angle") });
        var angleBox = new TextBox { Text = shape.Angle.ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(angleBox);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.zheight") });
        var zBox = new TextBox { Text = shape.ZValue.ToString("0.##"), Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(zBox);
        // 定位点位置（当前选中的定位点——默认中心；顶角可点击切换）：X / Y 各一个输入框
        var locs = shape.GetLocators();
        int litIdx = (_selLocatorShape == null || !ReferenceEquals(_selLocatorShape, shape)) ? locs.Count - 1 : _selLocatorIndex;
        var litLoc = locs[Math.Clamp(litIdx, 0, locs.Count - 1)];
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.locator") });
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.locator_x"), Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 4, 0, 2) });
        var locXBox = new TextBox { Text = litLoc.X.ToString("0.##"), Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(locXBox);
        panel.Children.Add(new TextBlock { Text = Loc("staticmap.shape.locator_y"), Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 2) });
        var locYBox = new TextBox { Text = litLoc.Y.ToString("0.##"), Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(locYBox);
        var ok = new Button
        {
            Content = Loc("common.ok"),
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(ok);
        winScroll.Content = panel;
        win.Content = winScroll;
        if (win.ShowDialog() != true)
            return;
        if (radiusBox != null && double.TryParse(radiusBox.Text, out double rd) && rd > 0) shape.OuterRadius = rd;
        if (innerBox != null && double.TryParse(innerBox.Text, out double ird) && ird >= 0 && ird < shape.OuterRadius) shape.InnerRadius = ird;
        if (radialBox != null && int.TryParse(radialBox.Text, out int rvd) && rvd >= 1 && rvd <= 50) shape.RadialDiv = rvd;
        if (circumBox != null && int.TryParse(circumBox.Text, out int cvd) && cvd >= 3 && cvd <= 500) shape.CircumDiv = cvd;
        if (edgeBox != null && int.TryParse(edgeBox.Text, out int ec) && ec >= 2 && ec <= 200) shape.EdgeCount = ec;
        if (spacingBox != null && double.TryParse(spacingBox.Text, out double sp) && sp > 0) shape.Spacing = sp;
        if (double.TryParse(zBox.Text, out double zv)) shape.ZValue = Math.Clamp(zv, -10, 10);
        if (double.TryParse(angleBox.Text, out double ang))
        {
            double delta = ang - shape.Angle;
            shape.Angle = ang;
            // 旋转绕**选中的定位点**（该定位点不动，其他绕它转）
            if (Math.Abs(delta) > 1e-9 && _selLocatorShape != null && ReferenceEquals(_selLocatorShape, shape))
            {
                double dr = delta * Math.PI / 180.0;
                double cosD = Math.Cos(dr), sinD = Math.Sin(dr);
                double ox = shape.Center.X - litLoc.X, oy = shape.Center.Y - litLoc.Y;
                shape.Center.X = litLoc.X + (float)(ox * cosD - oy * sinD);
                shape.Center.Y = litLoc.Y + (float)(ox * sinD + oy * cosD);
            }
        }
        if (double.TryParse(locXBox.Text, out double nx) && double.TryParse(locYBox.Text, out double ny))
        {
            double dx = nx - litLoc.X;
            double dy = ny - litLoc.Y;
            shape.Center.X += dx;
            shape.Center.Y += dy;
        }
        if (widthLenBox != null && double.TryParse(widthLenBox.Text, out double wl) && wl > 0) shape.Width = wl;
        if (heightLenBox != null && double.TryParse(heightLenBox.Text, out double hl) && hl > 0) shape.Height = hl;
        if (widthBox != null && int.TryParse(widthBox.Text, out int wd) && wd >= 2 && wd <= 200) shape.WidthDiv = wd;
        if (heightBox != null && int.TryParse(heightBox.Text, out int hd) && hd >= 2 && hd <= 200) shape.HeightDiv = hd;
        DrawPreview();
    }

    /// <summary>应用图形：在图形内按间距生成恒星点 + 按格网连接航道（三角格/矩形格/六角格）。</summary>
    private void ApplyShapes(List<ShapeOverlay> shapes)
    {
        var stat = _services.MapEngine?.GetStaticScenario(_currentMap ?? string.Empty);
        if (stat == null)
            return;
        foreach (var shape in shapes)
        {
            var points = GenerateShapePoints(shape);
            var sysMap = new Dictionary<(int, int), SystemEntry>();
            double rad = shape.Angle * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            int precision = GetCurrentPrecision();
            foreach (var (gx, gy) in points)
            {
                // 世界坐标 = 中心 + 旋转(标准基向量格点)——欧氏距离
                var (wx, wy) = GridToWorld(shape, gx, gy);
                double x = shape.Center.X + (wx * c - wy * s);
                double y = shape.Center.Y + (wx * s + wy * c);
                var sys = AddSystemAt((float)Math.Round(x, precision), (float)Math.Round(y, precision));
                sys.Position.Z = shape.ZValue;   // 图形高度
                sysMap[(gx, gy)] = sys;
            }
            // 格网连航道
            LinkGridLanes(stat, shape, sysMap);
        }
        DeduplicateAndMerge();
        // 应用后删除旧图形（已转为真实点）
        foreach (var shape in shapes)
        {
            _shapes.Remove(shape);
            _selectedShapes.Remove(shape);
        }
        DrawPreview();
    }

    /// <summary>生成图形内的整数格点集（格网坐标，按边长为 EdgeCount 个间距裁剪到图形内）。</summary>
    /// <summary>
    /// 生成图形内的格点（世界相对坐标，未旋转——标准欧氏距离，非曼哈顿/轴向近似）：
    /// 等边三角格用基向量 e1=(s,0)、e2=(s/2, s√3/2)；矩形横平竖直；六边形=标准三角格裁剪。
    /// </summary>
    /// <summary>
    /// 生成图形内的整数轴向格点 (i,j)（不带 spacing/居中——ApplyShapes 按标准基向量转世界）：
    /// 矩形 i∈[0,n]×[0,n]；等边三角 i≥0,j≥0,i+j≤n；六边形 = 三角格裁剪 |i|,|j|,|i+j|≤n。
    /// </summary>
    private static List<(int X, int Y)> GenerateShapePoints(ShapeOverlay shape)
    {
        var pts = new List<(int, int)>();
        if (shape.Custom)
        {
            int w = Math.Max(2, shape.WidthDiv);
            int h = Math.Max(2, shape.HeightDiv);
            if (shape.Kind == ShapeKind.Triangle)
            {
                // 自定义等腰三角：**斜格按实际宽/高**（非等边——非常规等腰）——
                // 格点 (i,j)：x = (i + j*0.5) * (W/w_div)（底边方向 + 半份斜移）、y = j * (H/h_div)
                // 裁剪到等腰轮廓：|x| <= W/2 * (1 - y/H)
                // 斜格（i∈[0..w_div] 底边铺满 + 每行偏移 0.5 格——顶角 x=0 有格点）+ 等腰收缩
                double gxT = shape.Width / w;
                double gyT = shape.Height / h;
                for (int j = 0; j <= h; j++)
                {
                    double y = j * gyT;
                    double halfW = shape.Width / 2.0 * (1.0 - y / shape.Height);
                    for (int i = 0; i <= w; i++)
                    {
                        double x = (i - w / 2.0 + j * 0.5) * gxT;
                        if (Math.Abs(x) <= halfW + gxT * 0.4)
                            pts.Add((i, j));
                    }
                }
            }
            else
            {
                for (int x = 0; x <= w; x++)
                    for (int y = 0; y <= h; y++)
                        pts.Add((x, y));
            }
            return pts;
        }

        int n = shape.EdgeCount;
        switch (shape.Kind)
        {
            case ShapeKind.Circle:
            {
                int rDiv = Math.Max(1, shape.RadialDiv);
                int cDiv = Math.Max(3, shape.CircumDiv);
                // 环形：层 k 半径 = 内半径 + (外-内)×k/份数；每层点数 = 圆周份数（36，不减少）
                for (int k = 0; k <= rDiv; k++)
                {
                    for (int j = 0; j < cDiv; j++)
                        pts.Add((k, j));
                }
                return pts;
            }
            case ShapeKind.Rectangle:
                for (int x = 0; x <= n; x++)
                    for (int y = 0; y <= n; y++)
                        pts.Add((x, y));
                break;
            case ShapeKind.Triangle:
                for (int i = 0; i <= n; i++)
                    for (int j = 0; j <= n - i; j++)
                        pts.Add((i, j));
                break;
            case ShapeKind.Hexagon:
                for (int i = -n; i <= n; i++)
                    for (int j = -n; j <= n; j++)
                        if (Math.Abs(i) <= n && Math.Abs(j) <= n && Math.Abs(i + j) <= n)
                            pts.Add((i, j));
                break;
        }
        return pts;
    }

    /// <summary>
    /// 轴向格 (i,j) → 世界相对坐标（标准欧氏：矩形直格；三角/六边形等边三角基向量）。
    /// 居中按几何中心：矩形减 n/2；等边三角减重心 (n/3, n/3)；六边形对称；自定义减 w/2、h/2。
    /// </summary>
    private static (double X, double Y) GridToWorld(ShapeOverlay shape, int i, int j)
    {
        double s = shape.Spacing;
        if (shape.Custom)
        {
            if (shape.Kind == ShapeKind.Triangle)
            {
                // 自定义等腰三角：斜格（底边铺满 + 每行偏移 0.5 格），相对重心（底边上方 H/3）
                double gxT = shape.Width / shape.WidthDiv;
                double gyT = shape.Height / shape.HeightDiv;
                return ((i - shape.WidthDiv / 2.0 + j * 0.5) * gxT, j * gyT - shape.Height / 3.0);
            }
            // 自定义矩形：宽 Width 均匀分 WidthDiv 份、高 Height 均匀分 HeightDiv 份
            double gx = shape.Width / shape.WidthDiv;
            double gy = shape.Height / shape.HeightDiv;
            return ((i - shape.WidthDiv / 2.0) * gx, (j - shape.HeightDiv / 2.0) * gy);
        }
        if (shape.Kind == ShapeKind.Rectangle)
            return ((i - shape.EdgeCount / 2.0) * s, (j - shape.EdgeCount / 2.0) * s);
        if (shape.Kind == ShapeKind.Circle)
        {
            int rDiv = Math.Max(1, shape.RadialDiv);
            int cDiv = Math.Max(3, shape.CircumDiv);
            double rk = shape.InnerRadius + (shape.OuterRadius - shape.InnerRadius) * i / rDiv;
            // 每层同点数（36）+ 交错（奇数层偏移半格）——内层点对应外层两点间的劣弧（不在同一角度）
            double th = (j + (i % 2 == 1 ? 0.5 : 0.0)) * 2.0 * Math.PI / cDiv;
            return (rk * Math.Cos(th), rk * Math.Sin(th));
        }
        double sq3 = Math.Sqrt(3.0);
        if (shape.Kind == ShapeKind.Triangle)
        {
            double g = shape.EdgeCount / 3.0; // 重心（等边三角格重心）
            return ((i + j * 0.5 - g * 1.5) * s, (j - g) * sq3 / 2.0 * s);
        }
        // 六边形对称（0,0 居中）
        return ((i + j * 0.5) * s, j * sq3 / 2.0 * s);
    }

    /// <summary>格网连航道：矩形横竖；三角形三角格；六边形六角格（相邻格点连边）。</summary>
    private void LinkGridLanes(StaticScenario stat, ShapeOverlay shape, Dictionary<(int, int), SystemEntry> sysMap)
    {
        if (shape.Kind == ShapeKind.Circle)
        {
            int rDiv = Math.Max(1, shape.RadialDiv);
            int cDiv = Math.Max(3, shape.CircumDiv);
            int Ck(int k) => cDiv; // 每层同点数（圆周份数），角方向对应
            void Link((int, int) a, (int, int) b)
            {
                if (sysMap.TryGetValue(a, out var sa) && sysMap.TryGetValue(b, out var sb))
                {
                    bool dup = stat.Hyperlanes.Any(l =>
                        (l.From == sa.Id && l.To == sb.Id) || (l.From == sb.Id && l.To == sa.Id));
                    if (!dup) stat.Hyperlanes.Add(new Hyperlane(sa.Id, sb.Id));
                }
            }
            for (int j = 0; j < Ck(1); j++)
                Link((0, 0), (1, j));
            for (int k = 1; k <= rDiv; k++)
            {
                int ck = Ck(k);
                for (int j = 0; j < ck; j++)
                {
                    Link((k, j), (k, (j + 1) % ck));
                    if (k < rDiv)
                    {
                        int ck2 = Ck(k + 1);
                        int j2 = (int)((long)j * ck2 / ck);
                        Link((k, j), (k + 1, j2));
                        Link((k, j), (k + 1, (j2 + 1) % ck2));
                    }
                }
            }
            return;
        }
        var dirs = shape.Kind switch
        {
            ShapeKind.Rectangle => new[] { (1, 0), (0, 1) },
            ShapeKind.Triangle => new[] { (1, 0), (0, 1), (1, -1) },
            _ => new[] { (1, 0), (0, 1), (1, -1) } // 六边形简化：轴向邻格
        };
        foreach (var ((x, y), a) in sysMap)
        {
            foreach (var (dx, dy) in dirs)
            {
                if (sysMap.TryGetValue((x + dx, y + dy), out var b))
                {
                    bool dup = stat.Hyperlanes.Any(l =>
                        (l.From == a.Id && l.To == b.Id) || (l.From == b.Id && l.To == a.Id));
                    if (!dup)
                        stat.Hyperlanes.Add(new Hyperlane(a.Id, b.Id));
                }
            }
        }
    }

}
