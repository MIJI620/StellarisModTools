// 文件: Stellaris.Editor/Pages/DynamicMapPage.xaml.cs
// 动态地图页（参照星系样式布局）：
//   左：预览区（当前地图形状 + 终止半径，套壳）
//   右：顶部地图列表（动态+静态混合、拖拽排序，保存自动重算 priority：
//       数字越小优先级越高，列表最上优先）+ 选项卡（基础 / 结构 / 形状排序 / 容量）
//   形状排序行：样式本地化名（左）+ 该样式在当前终止半径下的理论恒星上限（右）

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Stellaris.Editor.Controls;
using Stellaris.Engine.GalaxyMap;

namespace Stellaris.Editor.Pages;

public partial class DynamicMapPage : UserControl
{
    private readonly EngineServices _services;
    private System.Windows.Threading.DispatcherTimer _mapDebounce = null!;   // 搜索框 2 秒防抖
    private string? _currentMap;
    // 预览渲染的样式（用户在形状总表中选中；无选中则第一个勾选，再无则总表第一个）
    private string? _currentPreviewShape;

    /// <summary>选中静态地图时触发（参数 = 静态地图名），供上层切换到静态地图页面。</summary>
    public event EventHandler<string>? StaticMapRequested;

    // 地图列表拖拽排序状态
    private Point _dragStart;
    private readonly List<MapListItem> _dragItems = new();
    /// <summary>全部地图项（列表搜索过滤的底层数据——ReloadMaps 时备份完整顺序）。</summary>
    private readonly List<MapListItem> _allMapItems = new();
    private bool _dragging;
    // 形状总表拖拽状态
    private Point _shapeDragStart;
    private readonly List<ShapeRowItem> _shapeDragItems = new();

    public DynamicMapPage(EngineServices services)
    {
        _services = services;
        InitializeComponent();
        MapFilterBox.ToolTip = _services.Localisation.Get("common.list_search");
        MapFilterSearchButton.ToolTip = _services.Localisation.Get("common.list_search");
        _mapDebounce = Stellaris.Editor.Controls.SearchDebouncer.Attach(MapFilterBox, () => OnMapFilterSearch(this, new RoutedEventArgs()));

        var loc = services.Localisation;
        PreviewTitle.Text = loc.Get("dynmap.preview.title");
        TabBasic.Header = loc.Get("dynmap.tab.basic");
        TabOther.Header = loc.Get("dynmap.tab.other");
        TabShapes.Header = loc.Get("dynmap.tab.shapes");
        TabCapacity.Header = loc.Get("dynmap.tab.color");

        // 右键菜单：新建动态/新建静态/复制/删除/重命名
        var menu = new ContextMenu();
        var addDynamic = new MenuItem { Header = loc.Get("dynmap.add_dynamic") };
        addDynamic.Click += (_, _) => AddMap(isStatic: false);
        var addStatic = new MenuItem { Header = loc.Get("dynmap.add_static") };
        addStatic.Click += (_, _) => AddMap(isStatic: true);
        var copyItem = new MenuItem { Header = loc.Get("dynmap.copy") };
        copyItem.Click += (_, _) => CopyMap();
        var renameItem = new MenuItem { Header = loc.Get("dynmap.rename") };
        renameItem.Click += (_, _) => RenameMap();
        var deleteItem = new MenuItem { Header = loc.Get("dynmap.delete") };
        deleteItem.Click += (_, _) => DeleteMap();
        menu.Items.Add(addDynamic);
        menu.Items.Add(addStatic);
        menu.Items.Add(new Separator());
        menu.Items.Add(copyItem);
        menu.Items.Add(renameItem);
        menu.Items.Add(deleteItem);
        MapList.ContextMenu = menu;

        // 形状总表选中行 → 预览渲染该样式
        ShapeListBox.SelectionChanged += (_, _) =>
        {
            if (ShapeListBox.SelectedItem is ShapeRowItem row)
            {
                _currentPreviewShape = row.Name;
                DrawPreview();
            }
        };

        // 底部固定按钮：全部规整化 / 保存（文本与样式页统一）
        MapNormalizeButton.Content = loc.Get("style.normalize_all");
        MapSaveButton.Content = loc.Get("style.save_all");

        ReloadMaps();
        // 自动选中第一个动态地图（混排列表：不能选第一项——若第一项是静态地图会触发切到静态页）
        foreach (object o in MapList.Items)
        {
            if (o is MapListItem item && !item.IsStatic)
            {
                MapList.SelectedItem = item;
                break;
            }
        }
    }

    // ==================== 列表 ====================

    /// <summary>导航切到本页时刷新（重建当前地图表单，含理论上限——星系样式参数更新后同步）。</summary>
    public void Refresh()
    {
        if (_currentMap == null)
            return;
        BuildForms();
        DrawPreview();
    }

    /// <summary>静态地图页切换过来时选中指定动态地图（双向切换）。</summary>
    public void SetMap(string mapName)
    {
        ReloadMaps(); // 刷新列表（可能刚新建/删除了地图，旧列表不含该项）
        foreach (object o in MapList.Items)
        {
            if (o is MapListItem item && item.Name == mapName)
            {
                MapList.SelectedItem = item;
                var it = item;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => MapList.ScrollIntoView(it)));
                return;
            }
        }
    }

    private void ReloadMaps()
    {
        MapList.Items.Clear();
        var engine = _services.MapEngine;
        if (engine == null)
            return;
        // 动态+静态混合，按 priority 升序（数字小 = 优先级高）；显示本地化名（按当前界面语言）
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
        if (keepSelection && MapList.SelectedItem is MapListItem cur)
            keepName = cur.Name;

        MapList.Items.Clear();
        var pat = MapFilterBox?.Text?.Trim();
        foreach (var item in _allMapItems)
        {
            if (string.IsNullOrEmpty(pat)
                || item.Name.Contains(pat, StringComparison.OrdinalIgnoreCase)
                || item.Display.Contains(pat, StringComparison.OrdinalIgnoreCase))
                MapList.Items.Add(item);
        }
        if (keepName != null)
        {
            for (int i = 0; i < MapList.Items.Count; i++)
            {
                if (MapList.Items[i] is MapListItem mli && mli.Name == keepName)
                {
                    MapList.SelectedIndex = i;
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
        if (MapList.SelectedItem is not MapListItem item)
            return;
        // 静态地图 → 切到静态地图页面；拖拽排序期间延迟并跳过（否则按下瞬间切换中断拖拽）
        if (item.IsStatic)
        {
            var name = item.Name;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { if (!_dragging) StaticMapRequested?.Invoke(this, name); }));
            return;
        }
        _currentMap = item.Name;
        BuildForms();
        DrawPreview();
    }

    // ==================== 表单 ====================

    private void BuildForms()
    {
        BasicPanel.Children.Clear();
        OtherPanel.Children.Clear();
        ShapeListBox.Items.Clear();
        ColorPanel.Children.Clear();

        var engine = _services.MapEngine;
        if (engine == null || _currentMap == null)
            return;
        var loc = _services.Localisation;

        var dynamic = engine.GetDynamicScenario(_currentMap);
        var stat = engine.GetStaticScenario(_currentMap);
        if (dynamic == null && stat == null)
            return;

        // ---- 第 1 页"参数"：基础 + 结构合并（priority 不人工编辑，保存时自动分配） ----
        if (dynamic != null)
        {
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.num_stars"), dynamic.NumStars.ToString(),
                v => { dynamic.NumStars = ParseInt(v, dynamic.NumStars); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.radius"), dynamic.Radius.ToString(),
                v => { dynamic.Radius = ParseInt(v, dynamic.Radius); DrawPreview(); }));
            AddEmpireFields(BasicPanel, dynamic.NumEmpires, dynamic.NumEmpireDefault,
                (mn, mx, def) => { dynamic.NumEmpires = new IntRange(mn, mx); dynamic.NumEmpireDefault = def; });
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.advanced_empire"), dynamic.AdvancedEmpireDefault.ToString(),
                v => { dynamic.AdvancedEmpireDefault = ParseInt(v, dynamic.AdvancedEmpireDefault); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.fallen_empire"), dynamic.FallenEmpireDefault.ToString(),
                v => { dynamic.FallenEmpireDefault = ParseInt(v, dynamic.FallenEmpireDefault); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.marauder_empire"), dynamic.MarauderEmpireDefault.ToString(),
                v => { dynamic.MarauderEmpireDefault = ParseInt(v, dynamic.MarauderEmpireDefault); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.nomad_empire"), dynamic.NomadEmpireDefault.ToString(),
                v => { dynamic.NomadEmpireDefault = ParseInt(v, dynamic.NomadEmpireDefault); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.planet_odds"), dynamic.ColonizablePlanetOdds.ToString("0.##"),
                v => { dynamic.ColonizablePlanetOdds = ParseDouble(v, dynamic.ColonizablePlanetOdds); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.primitive_odds"), dynamic.PrimitiveOdds.ToString("0.##"),
                v => { dynamic.PrimitiveOdds = ParseDouble(v, dynamic.PrimitiveOdds); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.crisis"), dynamic.CrisisStrength.ToString("0.##"),
                v => { dynamic.CrisisStrength = ParseDouble(v, dynamic.CrisisStrength); }));
            // 结构参数（合并进参数页）
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.cluster_count"), dynamic.ClusterCount.Value.ToString(),
                v => { dynamic.ClusterCount.Value = ParseInt(v, dynamic.ClusterCount.Value); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.cluster_radius"), (dynamic.ClusterRadius ?? 0).ToString(),
                v => { dynamic.ClusterRadius = ParseInt(v, dynamic.ClusterRadius ?? 0); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.cluster_dist"), (dynamic.ClusterDistanceFromCore ?? 0).ToString(),
                v => { dynamic.ClusterDistanceFromCore = ParseInt(v, dynamic.ClusterDistanceFromCore ?? 0); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.max_hyperlane"), dynamic.MaxHyperlaneDistance.ToString(),
                v => { dynamic.MaxHyperlaneDistance = ParseInt(v, dynamic.MaxHyperlaneDistance); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.nebulas"), dynamic.NumNebulas.ToString(),
                v => { dynamic.NumNebulas = ParseInt(v, dynamic.NumNebulas); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.nebula_size"), dynamic.NebulaSize.ToString(),
                v => { dynamic.NebulaSize = ParseInt(v, dynamic.NebulaSize); }));
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.nebula_min_dist"), dynamic.NebulaMinDist.ToString(),
                v => { dynamic.NebulaMinDist = ParseInt(v, dynamic.NebulaMinDist); }));
            BasicPanel.Children.Add(BuildRangeRow(loc.Get("dynmap.wormhole"), dynamic.NumWormholePairs, dynamic.NumWormholePairsDefault,
                (mn, mx, dv) => { dynamic.NumWormholePairs = new IntRange(mn, mx); dynamic.NumWormholePairsDefault = dv; }));
            BasicPanel.Children.Add(BuildRangeRow(loc.Get("dynmap.gateways"), dynamic.NumGateways, dynamic.NumGatewaysDefault,
                (mn, mx, dv) => { dynamic.NumGateways = new IntRange(mn, mx); dynamic.NumGatewaysDefault = dv; }));
        }
        else
        {
            AddEmpireFields(BasicPanel, stat!.NumEmpires, stat.NumEmpireDefault,
                (mn, mx, def) => { stat.NumEmpires = new IntRange(mn, mx); stat.NumEmpireDefault = def; });
            BasicPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.system_count"), stat.Systems.Count.ToString(), _ => { }));
        }


        // ---- 第 2 页"其他"：地图键（重命名）+ 地图名本地化（各语言逻辑值/显示值） ----
        BuildOtherPanel(dynamic ?? (object)stat!);

        // ---- 第 3 页"形状与排序" ----
        BuildShapePanel(dynamic ?? (object)stat!);

        // ---- 第 4 页"颜色" ----
        BuildColorPanel();
    }

    /// <summary>其他页：地图键（可编辑 → 重命名）+ 地图名本地化（逻辑值可编辑 / 显示值只读）。</summary>
    private void BuildOtherPanel(object scenario)
    {
        var engine = _services.MapEngine!;
        var loc = _services.Localisation;
        string mapKey = scenario is DynamicScenario d ? d.Name : ((StaticScenario)scenario).Name;
        string currentMapKey = mapKey;

        // 地图键（可编辑 → 重命名；失焦即改内存，列表位置不变）
        OtherPanel.Children.Add(BuildFieldRow(loc.Get("dynmap.map_key"), mapKey, v =>
        {
            string newKey = v.Trim();
            if (newKey.Length == 0 || newKey == currentMapKey)
                return;
            if (engine.GetDynamicScenario(newKey) != null || engine.GetStaticScenario(newKey) != null)
                return;
            bool ok = scenario is DynamicScenario
                ? engine.RenameDynamicScenario(currentMapKey, newKey)
                : engine.RenameStaticScenario(currentMapKey, newKey);
            if (ok)
            {
                ReloadMaps();
                SelectMap(newKey);
            }
        }));

        // 地图名本地化：各语言逻辑值（可编辑）/ 显示值（只读）——用"启用语言"（模组设置）
        // 统一本地化组件（LocalisationEditBox）：边框 + 语种下拉 + 名称（逻辑值可编辑 → 显示值只读）
        var locBox = new LocalisationEditBox
        {
            Adapter = _services.Adapter,
            GetNameKey = () => currentMapKey,
            GetLangs = () => _services.StyleEngine?.GetEnabledLanguages() ?? new List<string> { "english" },
            SaveLocalisation = (lang, key, value) => UpdateMapLocalisation(lang, key, value),
            ShowDescription = false   // 动态地图只有名称（无描述）
        };
        locBox.Reload();
        OtherPanel.Children.Add(locBox);

        // 危机强度扩展列表（大型框，自动从小到大排序，可增删单个强度）
        var crisis = scenario is DynamicScenario dd2 ? dd2.ExtraCrisisStrength
            : ((StaticScenario)scenario).ExtraCrisisStrength;
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

        // 右键菜单：添加（弹出输入框，按回车确认）/ 删除（删选中项）
        var crisisMenu = new ContextMenu();
        var addCrisisItem = new MenuItem { Header = loc.Get("dynmap.add") };
        addCrisisItem.Click += (_, _) =>
        {
            var dlg = new Window
            {
                Title = loc.Get("dynmap.crisis_extra"),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                // 弹窗随内容自动（输入框本身才是固定 12 字符宽）
                SizeToContent = SizeToContent.WidthAndHeight,
                Owner = Window.GetWindow(this)
            };
            var panel2 = new StackPanel { Margin = new Thickness(14) };
            // 字号按外部字号设置；长度约 12 个字符
            double extFont = _services.Preferences.FontSize > 0 ? _services.Preferences.FontSize : 12;
            var valueBox = new TextBox
            {
                FontSize = extFont,
                MaxLength = 12,
                Width = extFont * 7.2 + 8
            };
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

        // ---- 锁定本地化 / 清空本地化 ----
        var lockCheck = new CheckBox
        {
            IsChecked = scenario is DynamicScenario dd1 ? dd1.LockLocalisation : ((StaticScenario)scenario).LockLocalisation,
            Margin = new Thickness(0, 6, 0, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        lockCheck.Checked += (_, _) => { if (scenario is DynamicScenario d1) d1.LockLocalisation = true; else ((StaticScenario)scenario).LockLocalisation = true; };
        lockCheck.Unchecked += (_, _) => { if (scenario is DynamicScenario d1) d1.LockLocalisation = false; else ((StaticScenario)scenario).LockLocalisation = false; };
        OtherPanel.Children.Add(BuildRingRow(loc.Get("staticmap.lock_localisation"), lockCheck));
        var clearCheck = new CheckBox
        {
            IsChecked = scenario is DynamicScenario cd ? cd.ClearFile : ((StaticScenario)scenario).ClearFile,
            Margin = new Thickness(0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        clearCheck.Checked += (_, _) => { if (scenario is DynamicScenario c2) c2.ClearFile = true; else ((StaticScenario)scenario).ClearFile = true; };
        clearCheck.Unchecked += (_, _) => { if (scenario is DynamicScenario c2) c2.ClearFile = false; else ((StaticScenario)scenario).ClearFile = false; };
        OtherPanel.Children.Add(BuildRingRow(loc.Get("staticmap.clear_file"), clearCheck));
    }

    /// <summary>标签（左定宽）+ 控件（右填充）行。</summary>

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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(label);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>动态地图名本地化文件约定：localisation/{lang}/{modPrefix}_map_l_{lang}.yml。</summary>
    private string MapLocalisationFile(string lang)
    {
        string prefix = _services.ModPrefs?.ModPrefix ?? "smt";
        return $"localisation/{lang}/{prefix}_map_l_{lang}.yml";
    }

    /// <summary>更新地图名本地化（内存）：键已存在（任意文件）→ 更新其当前位置；不存在 → 写入约定文件；随后重算显示值。</summary>
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

    /// <summary>颜色选项卡：形状填充色 / 网格色（存银河类别 galaxy.json，与星系样式共享）。</summary>
    private void BuildColorPanel()
    {
        ColorPanel.Children.Clear();
        var loc = _services.Localisation;
        ColorPanel.Children.Add(BuildColorRow(loc.Get("style.preview_color"), "shape_color", "#284488CC"));
        ColorPanel.Children.Add(BuildColorRow(loc.Get("style.grid_color"), "grid_color", "#50999999"));
    }

    private FrameworkElement BuildColorRow(string labelText, string colorKey, string fallbackHex)
    {
        var label = new TextBlock
        {
            Text = labelText,
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center
        };
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
            // 颜色改动实时反映到左侧预览
            DrawPreview();
        };
        var row = new DockPanel { Margin = new Thickness(2, 4, 2, 4) };
        row.Children.Add(label);
        row.Children.Add(picker);
        return row;
    }

    /// <summary>从银河类别读取预览颜色（RGBA 数组 → ARGB hex），缺失/损坏回退默认。</summary>
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

    /// <summary>把预览颜色写入银河类别（ARGB hex → RGBA int[]）。</summary>
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

    /// <summary>
    /// 样式总表：显示**全部**星系样式，勾选 = 该地图要的样式（supports_shape）。
    /// 可选中行（Ctrl/Shift 多选）、左键拖拽排序（勾选行之间）、右键取消勾选。
    /// 新建地图时全不打勾。
    /// </summary>
    private void BuildShapePanel(object scenario)
    {
        ShapeListBox.Items.Clear();
        var engine = _services.MapEngine!;
        var loc = _services.Localisation;
        var styleEngine = _services.StyleEngine;

        // 总表顺序：拖拽排序后记录在内存（GetShapeTableOrder），未拖过则回退样式表顺序
        var allStyles = engine.GetShapeTableOrder(_currentMap!)
            ?? styleEngine?.GetAllStyleNames() ?? new List<string>();
        var shapeOrder = engine.GetShapeOrder(_currentMap!);
        var supportedSet = new HashSet<string>(shapeOrder, StringComparer.Ordinal);
        var capacityMap = scenario is DynamicScenario d
            ? engine.GetEstimatedCapacity(_currentMap!).MaxStarsPerShape
            : new Dictionary<string, int>(StringComparer.Ordinal);
        // 预估数颜色：≥ 地图恒星数 95% 灰色；< 95% 红色（与是否勾选无关，全部样式都显示）
        double target = scenario is DynamicScenario dyn ? dyn.NumStars * 0.95 : 0;
        var grayBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
        var redBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30));

        // 样式总表：按星系样式列表顺序显示全部样式
        foreach (var shape in allStyles)
        {
            bool hasCap = capacityMap.TryGetValue(shape, out var cap) && cap > 0;
            var row = new ShapeRowItem
            {
                Name = shape,
                Display = LocName(shape),
                CapacityText = hasCap
                    ? loc.Format("dynmap.capacity_text", cap)
                    : string.Empty,
                CapacityBrush = !hasCap || cap >= target ? grayBrush : redBrush,
                IsChecked = supportedSet.Contains(shape),
                RowEnabled = true
            };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShapeRowItem.IsChecked))
                    CommitShapeChecks();
            };
            ShapeListBox.Items.Add(row);
        }

        // 右键菜单：启用 / 禁用（批量作用于选中行）/ 禁用不足（预估 < 95% 的样式自动禁用，不可再勾选）
        var menu = new ContextMenu();
        var enableItem = new MenuItem { Header = loc.Get("dynmap.enable_shape") };
        enableItem.Click += (_, _) =>
        {
            foreach (var o in ShapeListBox.SelectedItems)
            {
                if (o is ShapeRowItem row)
                {
                    row.RowEnabled = true;
                    row.IsChecked = true;
                }
            }
            CommitShapeChecks();
        };
        var disableItem = new MenuItem { Header = loc.Get("dynmap.disable_shape") };
        disableItem.Click += (_, _) =>
        {
            foreach (var o in ShapeListBox.SelectedItems)
            {
                if (o is ShapeRowItem row)
                {
                    row.RowEnabled = false;
                    row.IsChecked = false;
                }
            }
            CommitShapeChecks();
        };
        var disableInsuf = new MenuItem { Header = loc.Get("dynmap.disable_insufficient") };
        disableInsuf.Click += (_, _) =>
        {
            // 禁用不足：预估恒星数量 < 95% 地图恒星数量的样式，自动禁用（取消勾选 + 不可再勾选）
            if (_currentMap == null)
                return;
            var scenario = engine.GetDynamicScenario(_currentMap);
            if (scenario == null)
                return;
            double target = scenario.NumStars * 0.95;
            var capacityMap = engine.GetEstimatedCapacity(_currentMap).MaxStarsPerShape;
            foreach (object o in ShapeListBox.Items)
            {
                if (o is ShapeRowItem row)
                {
                    int cap = capacityMap.TryGetValue(row.Name, out var c) ? c : 0;
                    if (cap < target)
                    {
                        row.RowEnabled = false;
                        row.IsChecked = false;
                    }
                }
            }
            CommitShapeChecks();
        };
        menu.Items.Add(enableItem);
        menu.Items.Add(disableItem);
        menu.Items.Add(disableInsuf);
        ShapeListBox.ContextMenu = menu;
    }

    /// <summary>把勾选结果写回该地图的 supports_shape（顺序 = 总表中的勾选行顺序），并刷新预览。</summary>
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

    /// <summary>形状总表拖拽排序：只移动勾选的行（保持总表顺序），重算 supports_shape。</summary>
    private void OnShapeListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("SMT.ShapeRowItems") is not List<ShapeRowItem> dragged || dragged.Count == 0)
            return;
        var engine = _services.MapEngine!;
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
        // 重排总表，勾选行顺序随之更新
        list.Items.Clear();
        foreach (var item in items)
            list.Items.Add(item);
        // 记录总表顺序（含未勾选），重建形状页时保持拖拽顺序
        if (_currentMap != null)
            engine.SetShapeTableOrder(_currentMap!, items.Select(i => i.Name).ToList());
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

    // ==================== 预览（套壳：形状 + 终止半径圆） ====================

    private void DrawPreview()
    {
        PreviewCanvas.Children.Clear();
        var engine = _services.MapEngine;
        if (engine == null || _currentMap == null)
            return;
        var loc = _services.Localisation;

        double radius = 200;
        try
        {
            var dynamic = engine.GetDynamicScenario(_currentMap);
            if (dynamic != null)
                radius = dynamic.Radius;
            else
            {
                var stat = engine.GetStaticScenario(_currentMap);
                radius = stat?.Systems.Count > 0 ? 220 : 200;
            }
        }
        catch { }

        // 预览样式：用户在形状总表中选中的（_currentPreviewShape）→ 第一个勾选 → 总表第一个
        string previewShape = string.Empty;
        var order = engine.GetShapeOrder(_currentMap);
        var allShapes = _services.StyleEngine?.GetAllStyleNames() ?? new List<string>();
        if (!string.IsNullOrEmpty(_currentPreviewShape)
            && (allShapes.Contains(_currentPreviewShape)))
        {
            previewShape = _currentPreviewShape;
        }
        else if (order.Count > 0)
        {
            previewShape = order[0];
        }
        else if (allShapes.Count > 0)
        {
            previewShape = allShapes[0];
        }
        var shapeName = LocName(previewShape);
        PreviewTitle.Text = loc.Format("dynmap.preview.title_full", LocMapName(_currentMap), radius)
                            + (shapeName.Length > 0 ? $" · {shapeName}" : string.Empty);
        double side = Math.Min(PreviewCanvas.Width, PreviewCanvas.Height);
        double cx = side / 2.0, cy = side / 2.0;

        // 终止半径圆：基于该动态地图设置的终止半径（radius），形状按同比例缩放（scale = radius/500）
        double scale = Math.Max(0.01, radius / 500.0);
        double rpx = scale * side / 2.0;
        PreviewCanvas.Children.Add(new Ellipse
        {
            Width = rpx * 2,
            Height = rpx * 2,
            Stroke = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 }
        });
        Canvas.SetLeft(PreviewCanvas.Children[^1], cx - rpx);
        Canvas.SetTop(PreviewCanvas.Children[^1], cy - rpx);

        // 极坐标参考网格：**基于静态尺寸**（满画布 side/2），不随终止半径缩放；
        // 颜色取银河类别 grid_color（与星系样式网格色一致）
        double gridR = side / 2.0 - 2;
        var gridBase = (TryParseHex(GetPreviewColor("grid_color", "#50999999"))
                        ?? new SolidColorBrush(Color.FromArgb(0x50, 0x99, 0x99, 0x99))).Color;
        var gridBrush = new SolidColorBrush(Color.FromArgb(
            (byte)(gridBase.A / 2), gridBase.R, gridBase.G, gridBase.B));
        foreach (var frac in new[] { 0.25, 0.5, 0.75, 1.0 })
        {
            double gr = gridR * frac;
            PreviewCanvas.Children.Add(new Ellipse
            {
                Width = gr * 2,
                Height = gr * 2,
                Stroke = gridBrush,
                StrokeThickness = 0.6
            });
            Canvas.SetLeft(PreviewCanvas.Children[^1], cx - gr);
            Canvas.SetTop(PreviewCanvas.Children[^1], cy - gr);
        }
        for (int a = 0; a < 4; a++)
        {
            double ang = a * Math.PI / 2;
            PreviewCanvas.Children.Add(new Line
            {
                X1 = cx,
                Y1 = cy,
                X2 = cx + gridR * Math.Cos(ang),
                Y2 = cy - gridR * Math.Sin(ang),
                Stroke = gridBrush,
                StrokeThickness = 0.6
            });
        }

        // 渲染选中的星系形状（极坐标，坐标 × scale 使终止边缘落在半径圆上）；填充色取自银河类别
        var styleEngine = _services.StyleEngine;
        var style = string.IsNullOrEmpty(previewShape) ? null : styleEngine?.GetStyle(previewShape);
        var fillHex = GetPreviewColor("shape_color", "#284488CC");
        var brush = TryParseHex(fillHex) ?? new SolidColorBrush(Color.FromArgb(0x28, 0x44, 0x88, 0xCC));
        if (style != null && styleEngine != null)
        {
            try
            {
                // 动态地图渲染：形状需填满终止半径，endRadius 传满 500（不用样式预览的 0.9 安全余量）
                var polys = styleEngine.GetShapePolygonsWithParameters(style.Parameters, endRadius: 500f);
                foreach (var poly in polys)
                {
                    var points = new PointCollection();
                    foreach (var v in poly)
                        points.Add(new Point(
                            (v.X * scale + 500.0) / 1000.0 * side,
                            side - (v.Y * scale + 500.0) / 1000.0 * side));
                    if (points.Count >= 3)
                        PreviewCanvas.Children.Add(new Polygon
                        {
                            Points = points,
                            Fill = brush,
                            FillRule = FillRule.Nonzero
                        });
                }
            }
            catch
            {
                // 形状生成失败忽略
            }
        }
        else
        {
            // 无支持形状：显示大致形状轮廓（GetEstimatedShape 兜底）
            try
            {
                var polys = engine.GetEstimatedShape(_currentMap);
                foreach (var poly in polys)
                {
                    var points = new PointCollection();
                    foreach (var v in poly)
                        points.Add(new Point((v.X + 500.0) / 1000.0 * side, side - (v.Y + 500.0) / 1000.0 * side));
                    if (points.Count >= 3)
                        PreviewCanvas.Children.Add(new Polygon { Points = points, Fill = brush, FillRule = FillRule.Nonzero });
                }
            }
            catch
            {
                // 无数据忽略
            }
        }
    }

    // ==================== 拖拽排序 ====================

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
        if (index >= 0 && index < MapList.Items.Count)
        {
            var container = MapList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            y = container != null ? container.TransformToAncestor(MapList).Transform(new Point(0, 0)).Y : 0;
        }
        else
        {
            var last = MapList.ItemContainerGenerator.ContainerFromIndex(MapList.Items.Count - 1) as ListBoxItem;
            y = last != null ? last.TransformToAncestor(MapList).Transform(new Point(0, 0)).Y + last.ActualHeight : 0;
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

        ApplyOrderAndSave(items);
    }

    /// <summary>按新列表顺序重算全部地图的 priority（索引 0 = 最小 priority = 优先级最高）；暂不落盘（保存功能待动态调整好后统一添加）。</summary>
    private void ApplyOrderAndSave(List<MapListItem> items)
    {
        var engine = _services.MapEngine;
        if (engine == null)
            return;
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

    private void AddMap(bool isStatic)
    {
        var engine = _services.MapEngine;
        if (engine == null)
            return;
        string name = $"{(_services.Localisation.Get(isStatic ? "dynmap.new_static" : "dynmap.new_dynamic"))}_{DateTime.Now:HHmmss}";
        if (isStatic)
        {
            engine.AddStaticScenario(new StaticScenario { Name = name, SupportedShapes = new List<string>() });
        }
        else
        {
            engine.AddDynamicScenario(new DynamicScenario { Name = name, NumStars = 200, Radius = 200 });
        }
        ReloadMaps();
        SelectMap(name);
    }

    private void CopyMap()
    {
        var engine = _services.MapEngine;
        if (engine == null || _currentMap == null)
            return;
        var dynamic = engine.GetDynamicScenario(_currentMap);
        var stat = engine.GetStaticScenario(_currentMap);
        string newName = $"{_currentMap}_copy";
        int n = 1;
        while (engine.GetDynamicScenario(newName) != null || engine.GetStaticScenario(newName) != null)
            newName = $"{_currentMap}_copy_{++n}";
        if (dynamic != null)
        {
            var copy = dynamic.Clone();
            copy.Name = newName;
            engine.AddDynamicScenario(copy);
        }
        else if (stat != null)
        {
            var copy = stat.Clone();
            copy.Name = newName;
            engine.AddStaticScenario(copy);
        }
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
        if (newName.Length == 0 || newName == _currentMap)
            return;
        if (engine.GetDynamicScenario(newName) != null || engine.GetStaticScenario(newName) != null)
            return;
        // 改名 = 删除旧 + 添加新（引擎无直接改名接口）
        var dynamic = engine.GetDynamicScenario(_currentMap);
        var stat = engine.GetStaticScenario(_currentMap);
        if (dynamic != null)
        {
            dynamic.Name = newName;
            engine.AddDynamicScenario(dynamic);
        }
        else if (stat != null)
        {
            stat.Name = newName;
            engine.AddStaticScenario(stat);
        }
        engine.DeleteScenario(_currentMap);
        ReloadMaps();
        SelectMap(newName);
    }

    private void DeleteMap()
    {
        var engine = _services.MapEngine;
        if (engine == null || _currentMap == null)
            return;
        var selected = MapList.SelectedItems.Cast<MapListItem>().ToList();
        if (selected.Count == 0)
            return;
        foreach (var item in selected)
            engine.DeleteScenario(item.Name);
        ReloadMaps();
    }

    private void SelectMap(string name)
    {
        foreach (object o in MapList.Items)
        {
            if (o is MapListItem item && item.Name == name)
            {
                MapList.SelectedItem = item;
                // 延迟滚动确保容器已生成（新增/切换后项可见）
                var it = item;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => MapList.ScrollIntoView(it)));
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

    /// <summary>帝国参数：min/max/default 三输入行。</summary>
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

    private static FrameworkElement BuildLocRow(string labelText, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = labelText, VerticalAlignment = VerticalAlignment.Center };
        var val = new TextBlock
        {
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(val, 1);
        grid.Children.Add(label);
        grid.Children.Add(val);
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

    /// <summary>形状总表项：样式名 + 勾选（= 该地图要的样式）+ 本地化名 + 理论上限。</summary>
    private sealed class ShapeRowItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
        public string CapacityText { get; set; } = string.Empty;
        public System.Windows.Media.Brush CapacityBrush { get; set; }
            = System.Windows.Media.Brushes.Gray;

        private bool _rowEnabled = true;
        public bool RowEnabled
        {
            get => _rowEnabled;
            set
            {
                if (_rowEnabled != value)
                {
                    _rowEnabled = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowEnabled)));
                }
            }
        }

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
}
