// 文件: Stellaris.Editor/Pages/GalaxyStylePage.xaml.cs
// 星系样式页（规范 5.1-b）：
//   - 左：形状预览（1:1 等比，边长 = min(高×80%, 宽×50%)；只底色 +
//     极坐标网格（间隔 50 / 半径 500，无 0° 水平线）；颜色可由"颜色"页调整）
//   - 右：顶部全宽样式列表（高 = 界面 20%，本地化名、拖拽排序）+
//     3 页选项卡：形状（1 级+2 级几何参数）/ 其他（图标与描述）/ 颜色（预览配色）

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Stellaris.Editor.Controls;
using Stellaris.Engine.GalaxyStyle;

namespace Stellaris.Editor.Pages;

public partial class GalaxyStylePage : UserControl
{
    /// <summary>形状页参数（1 级 + 2 级几何，Label 为中文名，key 仅作 ToolTip）。</summary>
    private static readonly (string Path, string Key, string Label)[] ShapeParams =
    {
        ("core_radius_perc", "core_radius_perc", "核心半径比例"),
        ("num_stars_core_perc", "num_stars_core_perc", "核心恒星比例"),
        ("stars_min_dist", "stars_min_dist", "恒星最小间距"),
        ("num_arms", "num_arms", "旋臂数量"),
        ("arms.tightness_winding", "arms.tightness_winding", "缠绕度"),
        ("arms.width", "arms.width", "臂宽度(°)"),
        ("arms.fuzz", "arms.fuzz", "旋臂散乱度"),
        ("arms.seperation", "arms.seperation", "臂间夹角(°)"),
        ("ring.width", "ring.width", "环宽度(比例)"),
        ("ring.offset", "ring.offset", "环偏移(比例)")
    };

    /// <summary>其他页参数（距离平方、图标与描述）。</summary>
    private static readonly (string Path, string Key, string Label)[] OtherParams =
    {
        ("countries.ideal_sq_dist_between", "countries.ideal_sq_dist_between", "国家理想距离平方"),
        ("countries.min_sq_dist_between", "countries.min_sq_dist_between", "国家最小距离平方"),
        ("fallen_empires.ideal_sq_dist_between", "fallen_empires.ideal_sq_dist_between", "堕落帝国理想距离平方"),
        ("fallen_empires.min_sq_dist_between", "fallen_empires.min_sq_dist_between", "堕落帝国最小距离平方"),
        ("preview_icon", "preview_icon", "预览图标"),
        ("button_icon", "button_icon", "按钮图标"),
        ("desc", "desc", "描述")
    };

    private readonly EngineServices _services;
    private System.Windows.Threading.DispatcherTimer _styleDebounce = null!;   // 搜索框 2 秒防抖
    private string? _currentStyleName;

    // 拖拽排序状态（支持多选组拖拽）
    private Point _dragStart;
    private readonly List<StyleListItem> _dragItems = new();
    /// <summary>全部样式项（列表搜索过滤的底层数据——ReloadStyles 时备份完整顺序）。</summary>
    private readonly List<StyleListItem> _allStyleItems = new();

    public GalaxyStylePage(EngineServices services)
    {
        _services = services;
        InitializeComponent();
        StyleFilterBox.ToolTip = _services.Localisation.Get("common.list_search");
        StyleFilterSearchButton.ToolTip = _services.Localisation.Get("common.list_search");
        _styleDebounce = Stellaris.Editor.Controls.SearchDebouncer.Attach(StyleFilterBox, () => OnStyleFilterSearch(this, new RoutedEventArgs()));

        var loc = services.Localisation;
        PreviewTitle.Text = loc.Get("style.preview.title");
        TabShape.Header = loc.Get("style.tab.shape");
        TabOther.Header = loc.Get("style.tab.other");
        TabColor.Header = loc.Get("style.tab.color");
        ExportSettingsButton.Content = loc.Get("style.export_settings");
        NormalizeAllButton.Content = loc.Get("style.normalize_all");
        SaveAllButton.Content = loc.Get("style.save_all");

        // 样式列表右键菜单：添加样式（插入到选中项之前/末尾）/ 删除选中样式（支持多选）
        var styleMenu = new ContextMenu();
        var addStyleItem = new MenuItem { Header = loc.Get("style.list.add") };
        addStyleItem.Click += (_, _) => AddNewStyle();
        var copyStyleItem = new MenuItem { Header = loc.Get("style.list.copy") };
        copyStyleItem.Click += (_, _) => CopySelectedStyle();
        var removeStyleItem = new MenuItem { Header = loc.Get("style.list.remove") };
        removeStyleItem.Click += (_, _) => RemoveSelectedStyles();
        styleMenu.Items.Add(addStyleItem);
        styleMenu.Items.Add(copyStyleItem);
        styleMenu.Items.Add(removeStyleItem);
        StyleList.ContextMenu = styleMenu;

        ReloadStyles();
    }

    /// <summary>
    /// 右键添加新样式：默认 key（new_galaxy_style_{n}）由工具生成；
    /// 插入到选中项之前（无选中则追加末尾）。
    /// </summary>
    private void AddNewStyle()
    {
        var engine = _services.StyleEngine;
        if (engine == null)
            return;
        string newName = GenerateNewStyleName();
        int insertIndex = StyleList.SelectedIndex >= 0 ? StyleList.SelectedIndex : StyleList.Items.Count;
        engine.AddStyle(newName, new GalaxyShapeParameters(), null, insertIndex);
        ReloadStyles();
        for (int i = 0; i < StyleList.Items.Count; i++)
        {
            if (StyleList.Items[i] is StyleListItem sli && sli.Name == newName)
            {
                StyleList.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>右键复制样式：复制选中样式的参数，创建 {name}_copy 副本（插入选中项后）。</summary>
    private void CopySelectedStyle()
    {
        if (StyleList.SelectedItem is not StyleListItem sli)
            return;
        var engine = _services.StyleEngine;
        if (engine == null)
            return;
        var def = engine.GetStyle(sli.Name);
        if (def == null)
            return;
        string newName = $"{sli.Name}_copy";
        int n = 1;
        while (engine.GetAllStyleNames().Contains(newName, StringComparer.Ordinal))
            newName = $"{sli.Name}_copy_{++n}";
        int insertIndex = StyleList.SelectedIndex >= 0 ? StyleList.SelectedIndex + 1 : StyleList.Items.Count;
        engine.AddStyle(newName, def.Parameters.Clone(), null, insertIndex);
        ReloadStyles();
        for (int i = 0; i < StyleList.Items.Count; i++)
        {
            if (StyleList.Items[i] is StyleListItem item && item.Name == newName)
            {
                StyleList.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>生成不冲突的默认样式 key：new_galaxy_style_{n}。</summary>
    private string GenerateNewStyleName()
    {
        var existing = _services.StyleEngine!.GetAllStyleNames();
        int n = 1;
        while (existing.Contains($"new_galaxy_style_{n}", StringComparer.Ordinal))
            n++;
        return $"new_galaxy_style_{n}";
    }

    /// <summary>右键删除选中的样式（支持多选），并清理其本地化。</summary>
    private void RemoveSelectedStyles()
    {
        var engine = _services.StyleEngine;
        if (engine == null)
            return;
        var selected = StyleList.SelectedItems.Cast<object>().ToList();
        if (selected.Count == 0)
            return;
        try
        {
            foreach (var o in selected)
            {
                if (o is StyleListItem sli)
                    engine.DeleteStyle(sli.Name);
            }
        }
        finally
        {
            // 无论 DeleteStyle 是否成功/异常，都刷新列表——否则样式表已删但列表残留（"删不掉"假象）
            ReloadStyles();
        }
    }

    // ===== 导出设置 / 全部规整化 / 保存 =====

    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        var win = new ExportSettingsWindow(_services);
        win.Owner = Window.GetWindow(this);
        win.ShowDialog();
    }

    private void OnNormalizeAll(object sender, RoutedEventArgs e)
    {
        var engine = _services.StyleEngine;
        if (engine == null)
            return;
        // 规整化只改内存（转移键、补精灵名、gfx 位置迁移等），**不自动保存**——
        // 落盘必须由用户显式点"保存"触发（数据安全，规范要求）。
        engine.NormalizeAllKeys();
        // 确保精灵表：按样式引用补缺失 spriteType / 修正 texturefile（仅内存，保存时落盘）
        engine.EnsureGalaxySpriteTable();
        // gfx 精灵表位置规整化（仅内存迁移；保存时写回）
        try
        {
            _services.SpriteEngine?.NormalizeSpriteFiles(
                $"interface/game_setup/{_services.ModPrefs?.ModPrefix ?? "smt"}_galaxy_shapes.gfx");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"gfx 规整化失败: {ex.Message}", "Stellaris Mod Tools",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ReloadStyles();
    }

    private void OnSaveAll(object sender, RoutedEventArgs e)
    {
        var engine = _services.StyleEngine;
        if (engine == null)
            return;

        // 规范格式保存：转圈进度窗口 + 后台线程 + 完成关闭 + 仅失败弹窗（SaveRunner 统一）
        SaveRunner.Run(_services, "status.saving",
            () =>
            {
                // useLocalConfig: true —— 保存读取银河类别 galaxy.json（用户勾选的
                // 样式开关与导出参数），并按此导出与回写；避免 useLocalConfig=false
                // 时 snapshot.Available=false 导致勾选的导出被重置/不导出。
                var result = engine.SaveAllStyles(useLocalConfig: true);
                return result != null && result.WriteSuccess;
            },
            onSuccess: () => ReloadStyles(), // 保存后刷新列表名/表单（规整化可能改本地化名）
            failMessage: _services.Localisation.Get("status.save_failed"));
    }

    /// <summary>刷新样式列表（本地化名显示）与参数表单（重扫后调用）。</summary>
    public void ReloadStyles()
    {
        StyleList.Items.Clear();
        ShapePanel.Children.Clear();
        OtherPanel.Children.Clear();
        ColorPanel.Children.Clear();
        _currentStyleName = null;

        var engine = _services.StyleEngine;
        if (engine == null)
            return;

        // 全部样式项（搜索过滤的底层数据——保持完整顺序）
        _allStyleItems.Clear();
        foreach (var name in engine.GetAllStyleNames())
        {
            // 样式名本地化：UI 语言 → mod 本地化语言 → english → 回退 key
            string uiLang = _services.Localisation.CurrentLanguage;
            string display = engine.GetLocalisedText(name, UILocalisationManager.MapUiLangToModLang(uiLang))
                             ?? engine.GetLocalisedText(name, "english")
                             ?? name;
            _allStyleItems.Add(new StyleListItem(name, display));
        }
        ApplyStyleFilter(keepSelection: false);
        if (StyleList.Items.Count > 0)
            StyleList.SelectedIndex = 0;
    }

    // ===== 样式选择 / 拖拽排序 =====

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
            _styleDebounce.Stop();   // 手动搜索后停止防抖计时器（防 2 秒后重复触发）
            OnStyleFilterSearch(this, new RoutedEventArgs());
        }
    }

    private void OnStyleFilterSearch(object sender, RoutedEventArgs e)
    {
        ApplyStyleFilter(keepSelection: true);
    }

    /// <summary>应用样式列表过滤；输入为空时恢复全部。keepSelection=true 时按当前选中键找回选中。</summary>
    private void ApplyStyleFilter(bool keepSelection)
    {
        string? keepName = null;
        if (keepSelection && StyleList.SelectedItem is StyleListItem cur)
            keepName = cur.Name;

        StyleList.Items.Clear();
        var pat = StyleFilterBox?.Text?.Trim();
        foreach (var item in _allStyleItems)
        {
            if (string.IsNullOrEmpty(pat)
                || item.Name.Contains(pat, StringComparison.OrdinalIgnoreCase)
                || item.Display.Contains(pat, StringComparison.OrdinalIgnoreCase))
                StyleList.Items.Add(item);
        }
        if (keepName != null)
        {
            for (int i = 0; i < StyleList.Items.Count; i++)
            {
                if (StyleList.Items[i] is StyleListItem sli && sli.Name == keepName)
                {
                    StyleList.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void OnStyleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (StyleList.SelectedItem is not StyleListItem item)
            return;
        BuildForms(item.Name);
        DrawPreview();
    }

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(StyleList);
        var container = StyleList.ContainerFromElement(e.OriginalSource as DependencyObject);
        var item = (container as ListBoxItem)?.DataContext as StyleListItem;

        _dragItems.Clear();
        if (item != null && StyleList.SelectedItems.Contains(item))
        {
            // 点击已选中项 → 拖拽整个选中组
            foreach (var si in StyleList.SelectedItems)
                if (si is StyleListItem s)
                    _dragItems.Add(s);
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
        var pos = e.GetPosition(StyleList);
        if (Math.Abs(pos.X - _dragStart.X) < 5 && Math.Abs(pos.Y - _dragStart.Y) < 5)
            return;
        DragDrop.DoDragDrop(StyleList, new List<StyleListItem>(_dragItems), DragDropEffects.Move);
        _dragItems.Clear();
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        InsertIndicator.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(typeof(List<StyleListItem>)) is not List<StyleListItem> dragged || dragged.Count == 0)
            return;

        int target = GetDropIndex(e.GetPosition(StyleList));

        // 记录组内各项原索引，移除后修正插入点
        var originalIndex = new Dictionary<StyleListItem, int>();
        for (int i = 0; i < StyleList.Items.Count; i++)
            if (StyleList.Items[i] is StyleListItem si)
                originalIndex[si] = i;
        int before = 0;
        foreach (var d in dragged)
            if (originalIndex.TryGetValue(d, out var idx) && idx < target)
                before++;

        foreach (var item in dragged)
            StyleList.Items.Remove(item);

        int insertAt = Math.Max(0, target - before);
        for (int i = 0; i < dragged.Count; i++)
            StyleList.Items.Insert(Math.Min(insertAt + i, StyleList.Items.Count), dragged[i]);

        StyleList.SelectedItem = dragged[^1];

        // 写回引擎样式顺序（之前只重排 UI，引擎 _styleOrder 不变 → 保存/重建/重启全回旧顺序）
        _services.StyleEngine?.ReorderStyles(
            StyleList.Items.Cast<StyleListItem>().Select(i => i.Name).ToList());
    }

    /// <summary>拖拽悬停：显示插入线指示插入位置（两项之间/某项前/某项后）。</summary>
    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ShowInsertIndicator(GetDropIndex(e.GetPosition(StyleList)));
    }

    private void OnListDragLeave(object sender, DragEventArgs e)
        => InsertIndicator.Visibility = Visibility.Collapsed;

    private void ShowInsertIndicator(int index)
    {
        double y;
        if (index >= 0 && index < StyleList.Items.Count)
        {
            var container = StyleList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            y = container != null
                ? container.TransformToAncestor(StyleList).Transform(new Point(0, 0)).Y - 1
                : 0;
        }
        else
        {
            var last = StyleList.ItemContainerGenerator.ContainerFromIndex(StyleList.Items.Count - 1) as ListBoxItem;
            y = last != null
                ? last.TransformToAncestor(StyleList).Transform(new Point(0, 0)).Y + last.ActualHeight - 1
                : StyleList.ActualHeight - 1;
        }
        InsertIndicator.Margin = new Thickness(0, Math.Max(0, y), 0, 0);
        InsertIndicator.Visibility = Visibility.Visible;
    }

    private int GetDropIndex(Point pos)
    {
        for (int i = 0; i < StyleList.Items.Count; i++)
        {
            var container = StyleList.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (container == null) continue;
            if (pos.Y < container.TransformToAncestor(StyleList).Transform(new Point(0, 0)).Y
                           + container.ActualHeight / 2)
                return i;
        }
        return StyleList.Items.Count;
    }

    // ===== 参数表单（3 页） =====

    private void BuildForms(string styleName)
    {
        ShapePanel.Children.Clear();
        OtherPanel.Children.Clear();
        ColorPanel.Children.Clear();
        _currentStyleName = styleName;

        var def = _services.StyleEngine?.GetStyle(styleName);
        if (def == null)
            return;

        // 第 1 页：形状（参数标签经本地化 json 键 param.{key}）
        var loc1 = _services.Localisation;
        foreach (var (path, key, label) in ShapeParams)
            ShapePanel.Children.Add(BuildFieldRow(def, path, key, loc1.Get($"param.{key}")));

        // ring（HasRing，块存在性）
        var ringLabel = new TextBlock
        {
            Text = loc1.Get("param.ring"),
            Width = 170,
            ToolTip = "ring",
            VerticalAlignment = VerticalAlignment.Center
        };
        var ringBox = new CheckBox
        {
            IsChecked = def.Parameters.HasRing,
            Margin = new Thickness(4, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        ringBox.Checked += (_, _) => { def.Parameters.HasRing = true; DrawPreview(); };
        ringBox.Unchecked += (_, _) => { def.Parameters.HasRing = false; DrawPreview(); };
        var ringRow = new DockPanel { Margin = new Thickness(2, 4, 2, 2) };
        ringRow.Children.Add(ringLabel);
        ringRow.Children.Add(ringBox);
        ShapePanel.Children.Add(ringRow);

        // 第 2 页：其他（本地化编辑框 + 导出开关 + 距离平方/图标/描述）
        OtherPanel.Children.Add(BuildLocalisationBox(def));

        var engine = _services.StyleEngine;
        var loc = _services.Localisation;
        // 导出开关存银河类别（galaxy.json styles.{name}.preview|icon），经引擎读写；环选择框样式（定宽标签 + CheckBox）
        var exportPreviewToggle = new CheckBox
        {
            IsChecked = engine?.GetStyleSwitch(styleName, "preview") ?? true
        };
        exportPreviewToggle.Checked += (_, _) => engine?.SetStyleSwitch(styleName, "preview", true);
        exportPreviewToggle.Unchecked += (_, _) => engine?.SetStyleSwitch(styleName, "preview", false);
        var exportButtonToggle = new CheckBox
        {
            IsChecked = engine?.GetStyleSwitch(styleName, "icon") ?? true
        };
        exportButtonToggle.Checked += (_, _) => engine?.SetStyleSwitch(styleName, "icon", true);
        exportButtonToggle.Unchecked += (_, _) => engine?.SetStyleSwitch(styleName, "icon", false);
        OtherPanel.Children.Add(BuildRingRow(loc.Get("style.export_preview"), exportPreviewToggle));
        OtherPanel.Children.Add(BuildRingRow(loc.Get("style.export_button"), exportButtonToggle));
        // 接受规整化：样式是否参与规整化（做多个密度版本时，部分样式不接受规整化保留原样）
        var normalizeToggle = new CheckBox
        {
            IsChecked = engine?.GetStyleSwitch(styleName, "normalize") ?? true // 规整化默认支持
        };
        normalizeToggle.Checked += (_, _) => engine?.SetStyleSwitch(styleName, "normalize", true);
        normalizeToggle.Unchecked += (_, _) => engine?.SetStyleSwitch(styleName, "normalize", false);
        OtherPanel.Children.Add(BuildRingRow(loc.Get("style.accept_normalize"), normalizeToggle));

        // 参数行（desc 上面一行插入"样式键"输入框，可编辑样式 key → 重命名）
        foreach (var (path, key, label) in OtherParams)
        {
            if (key == "desc")
                OtherPanel.Children.Add(BuildStyleKeyRow(def, loc));
            OtherPanel.Children.Add(BuildFieldRow(def, path, key, loc.Get($"param.{key}")));
        }

        // 第 3 页：颜色（预览填充 / 网格圆 / 网格线）
        BuildColorPanel();
    }

    /// <summary>样式键行（可编辑样式 key → 重命名样式；位于"描述键"上面一行）。</summary>

    /// <summary>环选择框样式的行：定宽标签 + CheckBox（参考"环"选择框）。</summary>
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

    private FrameworkElement BuildStyleKeyRow(GalaxyStyleDefinition def, UILocalisationManager loc)
    {
        var labelBlock = new TextBlock
        {
            Text = loc.Get("style.loc.style_key"),
            Width = 170,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = def.Name,
            VerticalAlignment = VerticalAlignment.Center
        };
        var box = new TextBox
        {
            Text = def.Name,
            Margin = new Thickness(4, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        box.LostFocus += (_, _) =>
        {
            string newName = box.Text.Trim();
            if (newName.Length == 0 || string.Equals(newName, def.Name, StringComparison.Ordinal))
                return;
            try
            {
                _services.StyleEngine!.RenameStyle(def.Name, newName);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, ReloadStyles);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"RenameStyle failed: {ex.Message}", "Stellaris Mod Tools");
                box.Text = def.Name;
            }
        };
        var row = new DockPanel { Margin = new Thickness(2, 4, 2, 2) };
        row.Children.Add(labelBlock);
        row.Children.Add(box);
        return row;
    }

    private FrameworkElement BuildFieldRow(GalaxyStyleDefinition def, string path, string key, string label)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            Width = 170,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = key, // key 仅作提示，不再显示在括号内
            VerticalAlignment = VerticalAlignment.Center
        };
        string display = def.Parameters.RawInputs.TryGetValue(path, out var raw)
            ? raw
            : FormatValue(GetParamValue(def.Parameters, path));
        var box = new TextBox { Tag = path, Text = display, Margin = new Thickness(4, 2, 0, 2) };
        box.LostFocus += OnParamLostFocus;

        var row = new DockPanel { Margin = new Thickness(2, 1, 2, 1) };
        row.Children.Add(labelBlock);
        row.Children.Add(box);
        return row;
    }

    private void OnParamLostFocus(object sender, RoutedEventArgs e)
    {
        if (_currentStyleName == null || sender is not TextBox box || box.Tag is not string path)
            return;
        try
        {
            _services.StyleEngine?.UpdateStyleParam(_currentStyleName, path, box.Text);
            DrawPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"UpdateStyleParam failed: {ex.Message}", "Stellaris Mod Tools");
        }
    }

    // ===== 其他页：本地化编辑框（语种选择 + 名字/描述输入，失焦提交） =====

    private FrameworkElement BuildLocalisationBox(GalaxyStyleDefinition def)
    {
        // 统一本地化组件（LocalisationEditBox）：边框 + 语种下拉 + 名称/描述（逻辑值可编辑 → 显示值只读）
        var engine = _services.StyleEngine!;
        var box = new LocalisationEditBox
        {
            Adapter = _services.Adapter,
            GetNameKey = () => def.Name,
            GetDescKey = () => def.Parameters.DescKey,   // 星系样式描述键可自定义——不能假设 {name}_desc
            GetLangs = () => engine.GetEnabledLanguages(),
            // 引擎版保存：含 MarkLocalisationDirty（统一保存落盘）
            SaveLocalisation = (lang, key, text) =>
            {
                try
                {
                    if (key == def.Name)
                        engine.UpdateLocalisation(def.Name, lang, newTitle: text);
                    else
                        engine.UpdateLocalisation(def.Name, lang, newDescKey: key, newDescText: text);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"UpdateLocalisation failed: {ex.Message}", "Stellaris Mod Tools");
                }
            }
        };
        box.Reload();
        return box;
    }

    /// <summary>本地化编辑行：标签（左侧定宽）+ 控件（填充）。</summary>
    private static FrameworkElement BuildLocRow(string labelText, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(label);
        grid.Children.Add(control);
        return grid;
    }

    // ===== 第 3 页：颜色 =====

    private void BuildColorPanel()
    {
        ColorPanel.Children.Clear();
        var loc = _services.Localisation;

        // 预览颜色跟模组走（存银河类别 galaxy.json 的 global.preview.shape_color / grid_color）
        AddColorRow(loc.Get("style.preview_color"), "shape_color",
            GetPreviewColor("shape_color", "#284488CC"));
        AddColorRow(loc.Get("style.grid_color"), "grid_color",
            GetPreviewColor("grid_color", "#50999999"));
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
        catch
        {
            // 读取失败回退默认
        }
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
        catch
        {
            // 无效颜色忽略
        }
    }

    private void AddColorRow(string label, string colorKey, string currentHex)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center
        };
        var picker = new ColorPickerControl
        {
            Title = label,
            SelectedColorText = currentHex,
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
        row.Children.Add(labelBlock);
        row.Children.Add(picker);
        ColorPanel.Children.Add(row);
    }

    private static Brush? TryParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex.Trim())!);
        }
        catch
        {
            return null;
        }
    }

    // ===== 取值辅助 =====

    private static object? GetParamValue(GalaxyShapeParameters p, string path) => path switch
    {
        "core_radius_perc" => p.CoreRadiusPerc,
        "num_stars_core_perc" => p.NumStarsCorePerc,
        "stars_min_dist" => p.StarsMinDist,
        "num_arms" => p.NumArms,
        "countries.ideal_sq_dist_between" => p.CountriesIdealDist,
        "countries.min_sq_dist_between" => p.CountriesMinDist,
        "fallen_empires.ideal_sq_dist_between" => p.FallenIdealDist,
        "fallen_empires.min_sq_dist_between" => p.FallenMinDist,
        "arms.tightness_winding" => p.Tightness,
        "arms.width" => p.WidthDeg,
        "arms.fuzz" => p.Fuzz,
        "arms.seperation" => p.ArmAngleDeg,
        "ring.width" => p.RingWidth,
        "ring.offset" => p.RingOffset,
        "preview_icon" => p.PreviewIcon,
        "button_icon" => p.ButtonIcon,
        "desc" => p.DescKey,
        _ => null
    };

    private static string FormatValue(object? value) => value switch
    {
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        string s => s,
        null => string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    // ===== 预览绘制（极坐标网格 + 形状填充，颜色可配） =====

    private void DrawPreview()
    {
        PreviewCanvas.Children.Clear();
        double side = Math.Round(Math.Min(PreviewCanvas.Width, PreviewCanvas.Height));
        if (side <= 0) side = 300;
        PreviewCanvas.Width = side;
        PreviewCanvas.Height = side;

        DrawPolarGrid(side);

        var engine = _services.StyleEngine;
        if (engine == null || _currentStyleName == null)
            return;
        var def = engine.GetStyle(_currentStyleName);
        if (def == null)
            return;

        var shapeBrush = TryParseHex(GetPreviewColor("shape_color", "#284488CC"))
                         ?? new SolidColorBrush(Color.FromArgb(0x28, 0x44, 0x88, 0xCC));

        var polys = engine.GetShapePolygonsWithParameters(def.Parameters);
        foreach (var poly in polys)
        {
            var points = new PointCollection();
            foreach (var v in poly)
                points.Add(new Point(
                    (v.X + 500.0) / 1000.0 * side,
                    side - (v.Y + 500.0) / 1000.0 * side));
            if (points.Count >= 3)
            {
                // 强制首尾闭合（Fill 只填充闭合区域，防 0° 方向缝隙；随缩放消除）
                var first = poly[0];
                var last = poly[^1];
                double dx = first.X - last.X, dy = first.Y - last.Y;
                if (dx * dx + dy * dy > 1e-8)
                {
                    points.Add(new Point(
                        (first.X + 500.0) / 1000.0 * side,
                        side - (first.Y + 500.0) / 1000.0 * side));
                }
                PreviewCanvas.Children.Add(new Polygon
                {
                    Points = points,
                    Fill = shapeBrush,
                    // 只填充区域，不描边（0° 缺口已由多边形精确闭合 + Nonzero 修复，
                    // 无需再用同色描边遮盖）
                    FillRule = FillRule.Nonzero,
                    SnapsToDevicePixels = true
                });
            }
        }

        // 核心光圈（白色径向渐变，模拟渲染核心辉光，覆盖中心密集网格避免棋盘感）
        double coreRatio = Math.Min(def.Parameters.CoreRadiusPerc, 0.5);
        double corePx = coreRatio * side / 2.0;
        if (corePx > 1)
        {
            var radial = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0)
                }
            };
            var glow = new Ellipse
            {
                Width = corePx * 2,
                Height = corePx * 2,
                Fill = radial,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(glow, side / 2.0 - corePx);
            Canvas.SetTop(glow, side / 2.0 - corePx);
            PreviewCanvas.Children.Add(glow);
        }
    }

    private void DrawPolarGrid(double side)
    {
        double cx = side / 2.0, cy = side / 2.0;
        var gridBrush = TryParseHex(GetPreviewColor("grid_color", "#50999999"))
                        ?? new SolidColorBrush(Color.FromArgb(0x50, 0x99, 0x99, 0x99));

        // 同心圆：半径 50 ~ 500，间隔 50（与角度线同色）
        for (int r = 50; r <= 500; r += 50)
        {
            double radius = r / 500.0 * side / 2.0;
            var ellipse = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = gridBrush,
                StrokeThickness = 0.7,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(ellipse, cx - radius);
            Canvas.SetTop(ellipse, cy - radius);
            PreviewCanvas.Children.Add(ellipse);
        }

        // 角度线：45° 间隔（含 0°）
        for (int deg = 0; deg < 360; deg += 45)
        {
            double rad = deg * Math.PI / 180.0;
            PreviewCanvas.Children.Add(new Line
            {
                X1 = cx, Y1 = cy,
                X2 = cx + Math.Cos(rad) * side / 2.0,
                Y2 = cy - Math.Sin(rad) * side / 2.0,
                Stroke = gridBrush,
                StrokeThickness = 0.5,
                SnapsToDevicePixels = true
            });
        }
    }

    private bool _refreshScheduled;

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshPreviewSize();
    }

    /// <summary>计算预览边长并重绘。布局未完成（如全屏切换时 ActualHeight 为 0）则延迟到下一帧重算，
    /// 避免全屏下预览尺寸停留在旧值导致样式不可见。</summary>
    private void RefreshPreviewSize()
    {
        double h = PreviewBox.ActualHeight;
        double w = PreviewBox.ActualWidth;
        if (h <= 0 || w <= 0)
        {
            if (!_refreshScheduled)
            {
                _refreshScheduled = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _refreshScheduled = false;
                    RefreshPreviewSize();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            return;
        }
        double side = Math.Min(h * 0.8, w * 0.5);
        if (side < 50) side = 50;
        PreviewCanvas.Width = side;
        PreviewCanvas.Height = side;
        DrawPreview();
    }

    /// <summary>样式列表项：Name = 引擎键名，Display = 本地化名。</summary>
    private sealed class StyleListItem
    {
        public string Name { get; }
        public string Display { get; }

        public StyleListItem(string name, string display)
        {
            Name = name;
            Display = display;
        }

        public override string ToString() => Display;

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
