// 文件: Stellaris.Editor/Pages/TechEditDialog.cs
// 科技**新建/修改共用弹窗**（本期内存编辑不落盘——用户确认）。
// 新建：预填右键位置（area=所在行、tier=所在列、cost=所在小列），key 自动生成可改；
// 修改：加载选中科技字段（key 只读）。
// 确定 → 回写/新建 TechNode（引擎内存 AddItem/UpdateItem），页面 RebuildImage 刷新。
// 字段：key/area/tier/cost/levels/category/icon/weight/prereqfor_desc/prerequisites/
//      potential(4通用+1自定义)/modifier(参考法令)/weight_modifier/ai_weight。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Stellaris.Engine.Technology;
using Stellaris.Editor.Controls;

namespace Stellaris.Editor.Pages;

public sealed class TechEditDialog : Window
{
    private readonly TechnologyEngine _engine;
    private readonly TechNode? _existing;   // null = 新建
    private readonly TechNode? _initial;    // 修改时的字段快照（Commit 脏字段比较用；null = 新建）
    private readonly EngineServices _services;   // StaticModifierEngine（加成搜索/本地化——复用法令）
    private readonly string _modLang;   // 卡片本地化语种
    private readonly Func<string, string> _techName;   // 科技 key → 显示名

    private TextBox _keyBox = null!, _tierBox = null!, _costBox = null!, _weightBox = null!,
        _iconBox = null!, _prereqForBox = null!, _customBox = null!, _levelsNBox = null!,
        _costPerLevelBox = null!, _costCustomBox = null!, _weightModBox = null!, _aiWeightBox = null!, _ownerFileBox = null!;
    private Grid _form = null!;   // 统一表单（label 列 Auto 共享最宽）
    private int _formRow;   // 表单当前行号
    private CheckBox _startTechCheck = null!;   // 初始科技勾选框（key 输入框末端右侧）
    private ComboBox _areaCombo = null!, _levelsCombo = null!, _categoryCombo = null!,
        _iconTechCombo = null!, _potentialCombo = null!, _costModeCombo = null!;
    private string _costAutoFactor = "";   // 上次自动填充的 factor 默认（判断用户是否改过自定义内容）
    private List<ComboBoxItem> _iconAllItems = new();   // 图标下拉全部选项（过滤用）
    private DataGrid _preList = null!;   // 已选前置列表（上方：显示选择了哪些 key|本地化；右键删除）
    private TextBox _preSearch = null!;   // 前置搜索输入框
    private ListBox _preResults = null!;   // 结果列表（参考科技页搜索预测列表：单列本地化名；双击填充）
    private IReadOnlyList<TechNode> _allTechs = Array.Empty<TechNode>();
    private DataGrid _modGrid = null!;
    private readonly List<ModRow> _modRows = new();

    /// <summary>提交后的科技（新建/修改都回填）。</summary>
    public TechNode? Result { get; private set; }

    private sealed class ModRow
    {
        public string Key { get; set; } = "";
        public string Loc { get; set; } = "";
        public string ValueText { get; set; } = "";
    }

    /// <summary>前置搜索结果/已选行：key + 本地化名。ListBox 显示 ToString（本地化名，同科技页搜索预测列表）。</summary>
    private sealed class PreRow
    {
        public string Key { get; set; } = "";
        public string Loc { get; set; } = "";
        public override string ToString() => string.IsNullOrEmpty(Loc) ? Key : Loc;
    }

    private enum PotentialPreset { AlwaysYes = 0, AlwaysNo = 1, AiYes = 2, AiNo = 3, Custom = 4 }

    /// <summary>构造。existing=null 新建；否则修改。presetArea/Tier/Cost = 右键位置预填。
    /// get = UI 本地化文本；techName = 科技 key → 显示名。</summary>
    public TechEditDialog(TechnologyEngine engine, IReadOnlyList<TechNode> allTechs,
        IReadOnlyList<string> categories, TechNode? existing,
        string presetArea, int presetTier, int presetCost, Func<string, string> get, Func<string, string> techName,
        EngineServices services, string modLang)
    {
        _engine = engine;
        _existing = existing;
        _services = services;
        _modLang = modLang;
        _techName = techName;
        // 修改时字段快照（脏字段比较：弹窗提交只标记改过的字段，未编辑字段保存时保留原样）
        if (existing != null)
        {
            _initial = new TechNode
            {
                Key = existing.Key, Area = existing.Area, Tier = existing.Tier, Cost = existing.Cost,
                Levels = existing.Levels, HasLevels = existing.HasLevels,
                CostPerLevel = existing.CostPerLevel, HasCostPerLevel = existing.HasCostPerLevel,
                Icon = existing.Icon, Weight = existing.Weight, StartTech = existing.StartTech,
                CostRaw = existing.CostRaw,
                PotentialRaw = existing.PotentialRaw, WeightModifierRaw = existing.WeightModifierRaw,
                AiWeightRaw = existing.AiWeightRaw, PrereqForDesc = existing.PrereqForDesc
            };
            _initial.Categories.AddRange(existing.Categories);
            _initial.Prerequisites.AddRange(existing.Prerequisites);
            foreach (var (k, v) in existing.ModifierEntries)
                _initial.ModifierEntries.Add((k, v));
        }
        else
            _initial = null;
        Title = existing == null ? get("tech.edit_title_new") : get("tech.edit_title_modify");
        Width = 600;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // **统一 Grid**：label 列 Auto（全表共享——自动取最宽标签，避免犬牙交错，用户规则）+ 内容列 Star
        _form = new Grid { Margin = new Thickness(10) };
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // key（新建自动生成可改；修改只读）+ **初始科技勾选框**（key 输入框末端右侧，用户 2026-08）
        var keyRow = new Grid();
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _keyBox = new TextBox
        {
            Text = existing?.Key ?? "tech_new_" + (DateTime.Now.Ticks % 1000000),
            IsReadOnly = existing != null,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = false,
            MinHeight = 24,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_keyBox, 0);
        keyRow.Children.Add(_keyBox);
        _startTechCheck = new CheckBox
        {
            Content = get("tech.edit_start_tech"),
            IsChecked = existing?.StartTech ?? false,   // 原本是初始科技 → 默认自动勾上（用户）
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_startTechCheck, 1);
        keyRow.Children.Add(_startTechCheck);
        AddRow(get("tech.edit_key"), keyRow);
        // **本地化组件**（**放在键下面一行**——用户；参考星系样式页 LocalisationEditBox：语种下拉 = 模组启用语言，
        // 有多少显示多少不足显示模组预设；名称键 = 弹窗 key，描述键 = {key}_desc；
        // **失焦只写本地化引擎内存 + 登记待保存**（用户规则：所有保存必须显式登记，用户触发才落盘——不落盘；
        // 目标文件 technologies_{Prefix}_l_{lang}.yml 由引擎 UpdateItemLocalisation 统一登记）
        var locBox = new LocalisationEditBox
        {
            Adapter = _services.Adapter,
            GetNameKey = () => _keyBox.Text,
            GetDescKey = () => _keyBox.Text + "_desc",
            GetLangs = () => _services.ModPrefs?.EnabledLanguages?.Count > 0
                ? _services.ModPrefs.EnabledLanguages
                : _services.Adapter?.GetLocalisationLanguages() ?? new List<string>(),
            SaveLocalisation = (lang, key, text) =>
            {
                try
                {
                    _services.TechnologyEngine!.UpdateItemLocalisation(lang, key, text,
                        _services.ModPrefs?.ModPrefix ?? "smt");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"UpdateLocalisation failed: {ex.Message}", "Stellaris Mod Tools");
                }
            }
        };
        locBox.Reload();
        AddRow(get("tech.edit_localisation"), locBox);
        // area（预填右键行）+ category——**同一行，各自独立标签**（用户：领域：，类别：）；
        // **"领域:" 放左列**（与其他左列标签对齐，不缩进）；"类别:" 标签**宽度参考左侧标签**（MinWidth 60）；
        // acRow 与 tierRow **同结构 3 列**（Star|Auto|Star）→ 两行严格对齐（用户）
        // area 本地化键**全大写**（PHYSICS/SOCIETY/ENGINEERING），值存小写科技字段（physics/society/engineering）
        var acRow = new Grid();
        acRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        acRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        acRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _areaCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (locKey, valKey) in new[] { ("PHYSICS", "physics"), ("SOCIETY", "society"), ("ENGINEERING", "engineering") })
        {
            var disp = _techName(locKey);
            _areaCombo.Items.Add(new ComboBoxItem
            {
                Content = string.IsNullOrEmpty(disp) || string.Equals(disp, locKey, StringComparison.OrdinalIgnoreCase) ? valKey : disp,
                Tag = valKey
            });
        }
        SelectComboByTag(_areaCombo, string.IsNullOrEmpty(existing?.Area) ? presetArea : existing.Area);
        acRow.Children.Add(_areaCombo);
        var catLabel = new TextBlock
        {
            Text = get("tech.edit_category") + ":",
            Foreground = Brushes.Gray,
            MinWidth = 60,   // 参考左侧标签宽度（用户）
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(catLabel, 1);
        acRow.Children.Add(catLabel);
        // category（**不可修改的选择**——用户：只能从列表选，不能输入）——选项显示本地化名，Tag 存 key
        _categoryCombo = new ComboBox { IsTextSearchEnabled = true, Margin = new Thickness(6, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in categories)
            _categoryCombo.Items.Add(new ComboBoxItem { Content = _techName(c), Tag = c });
        var curCat = existing?.Categories.FirstOrDefault();
        if (!string.IsNullOrEmpty(curCat))
            SelectComboByTag(_categoryCombo, curCat);
        Grid.SetColumn(_categoryCombo, 2);
        acRow.Children.Add(_categoryCombo);
        AddRow(get("tech.edit_area"), acRow);   // "领域:" 在左列（对齐，不缩进）
        // tier / 循环次数（预填右键列）——**同一行，各自标签**（用户：阶数和循环次数放同一行）；"阶数:" 放左列；
        // 与 acRow **同结构 3 列** → 两行对齐；循环次数 = 下拉（单次/有限/无限）+ 次数输入，移到原花费位置（用户 2026-08）
        var tierRow = new Grid();
        tierRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tierRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tierRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _tierBox = new TextBox { Text = existing?.Tier.ToString() ?? presetTier.ToString(), VerticalAlignment = VerticalAlignment.Center };
        tierRow.Children.Add(_tierBox);
        var levelsLabel = new TextBlock
        {
            Text = get("tech.edit_levels") + ":",
            Foreground = Brushes.Gray,
            MinWidth = 60,   // 参考左侧标签宽度（用户）
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(levelsLabel, 1);
        tierRow.Children.Add(levelsLabel);
        // 循环次数控件组（下拉做窄 + 次数输入填满剩余）
        _levelsNBox = new TextBox { Text = "1", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        var levelsCtl = new Grid();
        levelsCtl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        levelsCtl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _levelsCombo = new ComboBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };   // 窄
        _levelsCombo.Items.Add(new ComboBoxItem { Content = get("tech.edit_levels_1") });   // 单次
        _levelsCombo.Items.Add(new ComboBoxItem { Content = get("tech.edit_levels_n") });    // 有限循环
        _levelsCombo.Items.Add(new ComboBoxItem { Content = get("tech.edit_levels_inf") });  // 无限循环
        _levelsCombo.SelectionChanged += (_, _) => _levelsNBox.IsEnabled = _levelsCombo.SelectedIndex == 1;   // 有限循环
        if (existing != null)
        {
            if (existing.Levels == -1) _levelsCombo.SelectedIndex = 2;   // 无限循环
            else if (existing.HasLevels && existing.Levels != 1) { _levelsCombo.SelectedIndex = 1; _levelsNBox.Text = existing.Levels.ToString(); }
            else _levelsCombo.SelectedIndex = 0;   // 单次
        }
        else _levelsCombo.SelectedIndex = 0;
        _levelsNBox.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(_levelsNBox, 1);
        levelsCtl.Children.Add(_levelsCombo);
        levelsCtl.Children.Add(_levelsNBox);
        levelsCtl.Margin = new Thickness(6, 0, 0, 0);   // 与其他行列 2 控件对齐（下拉 Left 对齐贴太左会"突出来"，用户 2026-08）
        Grid.SetColumn(levelsCtl, 2);
        tierRow.Children.Add(levelsCtl);
        AddRow(get("tech.edit_tier"), tierRow);
        // 权重 / 循环增长（用户 2026-08：**原有的权重替换 cost 所在的表格位置**；循环增长留在原行与权重同行）
        // 与 tierRow **同结构 3 列** → 两行对齐
        var weightRow = new Grid();
        weightRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        weightRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        weightRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _weightBox = new TextBox { Text = existing?.Weight ?? "1", VerticalAlignment = VerticalAlignment.Center };   // 权重（默认 1）
        weightRow.Children.Add(_weightBox);
        var growthLabel = new TextBlock
        {
            Text = get("tech.edit_level_growth") + ":",
            Foreground = Brushes.Gray,
            MinWidth = 60,   // 参考左侧标签宽度（用户）
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(growthLabel, 1);
        weightRow.Children.Add(growthLabel);
        _costPerLevelBox = new TextBox
        {
            Text = existing is { HasCostPerLevel: true } ? existing.CostPerLevel.ToString() : "0",   // 没有就填 0（用户 2026-08）
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_costPerLevelBox, 2);
        weightRow.Children.Add(_costPerLevelBox);
        AddRow(get("tech.edit_weight"), weightRow);
        // 花费（用户 2026-08：**移到权重行下一行**；右侧下拉 = 基础/自定义）：
        //   基础 = 下拉不做宽（Auto）+ 右侧数值输入框（填满）；自定义 = 多行输入框（≥3 行，默认 factor = 1）→ 保存为 cost 块
        var costRow = new Grid();
        costRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        costRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _costModeCombo = new ComboBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };   // 基础模式下拉不做宽（用户）
        _costModeCombo.Items.Add(new ComboBoxItem { Content = get("tech.edit_cost_base") });      // 基础
        _costModeCombo.Items.Add(new ComboBoxItem { Content = get("tech.edit_cost_custom") });    // 自定义
        _costBox = new TextBox { VerticalAlignment = VerticalAlignment.Center };   // 基础：数值输入框（填满）
        _costCustomBox = new TextBox
        {
            MinHeight = 60,   // ≥3 行
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(6, 0, 0, 0)
            // 不默认 factor = 1（用户 2026-08：去掉 =1 特例——切到自定义时 factor 默认 = 基础数值）
        };
        _costModeCombo.SelectionChanged += (_, _) =>
        {
            bool custom = _costModeCombo.SelectedIndex == 1;
            _costBox.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
            _costCustomBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            if (!custom)
                return;
            // 切到自定义：factor 默认 = 基础数值（用户 2026-08）；用户已编辑过内容（≠上次自动值）则保留
            if (int.TryParse(_costBox.Text.Trim(), out var baseCost))
            {
                if (string.IsNullOrWhiteSpace(_costCustomBox.Text)
                    || string.Equals(_costCustomBox.Text.Trim(), _costAutoFactor, StringComparison.Ordinal))
                {
                    _costCustomBox.Text = "factor = " + baseCost;
                    _costAutoFactor = _costCustomBox.Text;
                }
            }
        };
        // 初始：已有科技带 cost 块（CostRaw）→ 自定义模式；否则基础（预填右键 cost）
        if (existing is { CostRaw: not null })
        {
            _costModeCombo.SelectedIndex = 1;
            _costCustomBox.Text = existing.CostRaw;
        }
        else
        {
            _costModeCombo.SelectedIndex = 0;
            _costBox.Text = existing?.Cost.ToString() ?? presetCost.ToString();
        }
        Grid.SetColumn(_costBox, 1);
        Grid.SetColumn(_costCustomBox, 1);
        costRow.Children.Add(_costModeCombo);
        costRow.Children.Add(_costBox);
        costRow.Children.Add(_costCustomBox);
        AddRow(get("tech.edit_cost"), costRow);
        // icon：**下拉在左、输入框在右**（用户）；**下拉自动选中 → 联动输入框**（不覆盖）；
        // **可输入过滤**（用户 2026-08）：输入某字 → 按字做**包含匹配**（key 或本地化名）；点下拉只显示包含该字的选项。
        // 实现：IsEditable + 关闭原生前缀搜索（IsTextSearchEnabled=false），TextChanged 重建 Items（保留"（自定义）"项）
        // 输入框**填满一行**（用户：右侧输入框要自动填满）
        var iconRow = new Grid();
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _iconTechCombo = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsEditable = true,
            IsTextSearchEnabled = false,   // 关闭原生前缀搜索，用自定义包含匹配
            StaysOpenOnEdit = true         // 编辑文本时下拉不自动收起（点开才显示过滤结果）
        };
        _iconAllItems = new List<ComboBoxItem>();
        _iconAllItems.Add(new ComboBoxItem { Content = get("tech.edit_icon_custom"), Tag = "" });   // （自定义）
        foreach (var t in allTechs.OrderBy(t => t.Key, StringComparer.Ordinal))
            _iconAllItems.Add(new ComboBoxItem { Content = _techName(t.Key), Tag = t.Key });
        foreach (var it in _iconAllItems)
            _iconTechCombo.Items.Add(it);
        bool iconTextProgrammatic = false;   // 程序回显 Text 时跳过过滤（避免选中后把下拉滤空）
        _iconTechCombo.SelectionChanged += (_, _) =>
        {
            if (_iconTechCombo.SelectedItem is ComboBoxItem it && it.Tag is string k && k.Length > 0)
            {
                _iconBox.Text = k;   // 选科技 → 输入框填其 key（= 图标名；自动选中同样影响输入框）
                // 选中后回显显示名（覆盖过滤词）——程序设 Text，不触发过滤
                iconTextProgrammatic = true;
                _iconTechCombo.Text = it.Content?.ToString() ?? k;
                iconTextProgrammatic = false;
            }
        };
        _iconBox = new TextBox { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };   // 无 Width → 填满一行
        if (existing != null)
            SelectComboByTag(_iconTechCombo, existing.Key);   // 下拉选自己 → 联动输入框 = 自己 key（不覆盖）
        else
            _iconTechCombo.SelectedIndex = 0;
        _iconTechCombo.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (iconTextProgrammatic)
                return;
            string q = _iconTechCombo.Text ?? "";
            var prev = _iconTechCombo.SelectedItem as ComboBoxItem;
            _iconTechCombo.Items.Clear();
            foreach (var it in _iconAllItems)
            {
                var k = it.Tag as string ?? "";
                if (q.Length == 0 || k.Length == 0   // 空输入显示全部；（自定义）恒保留
                    || k.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || _techName(k).Contains(q, StringComparison.OrdinalIgnoreCase))
                    _iconTechCombo.Items.Add(it);
            }
            if (prev != null && _iconTechCombo.Items.Contains(prev))
                _iconTechCombo.SelectedItem = prev;   // 过滤后仍含原选中 → 保持选中
        }));
        Grid.SetColumn(_iconBox, 1);
        iconRow.Children.Add(_iconTechCombo);   // 下拉在左
        iconRow.Children.Add(_iconBox);         // 输入框在右（填满）
        AddRow(get("tech.edit_icon"), iconRow);
        // 权重已上移到"权重/循环增长"行（用户 2026-08：原有的权重替换 cost 所在位置）
        // prereqfor_desc
        _prereqForBox = AddTextRow(get("tech.edit_prereqfor_desc"), existing?.PrereqForDesc ?? "", false, multiLine: true);
        // prerequisites：**输入框 + 已选前置列表（显示选择了哪些：key | 本地化，自动扩展=每添加一个自动多一行）**
        // 布局：**已选列表在上**（右键删除）→ 输入框 → 下方结果列表（双击填充；1 个结果回车自动填充）
        var preWrap = new StackPanel();
        // 已选前置列表（**上方**；左侧 key、右侧本地化；**自动扩展**）
        _preList = new DataGrid
        {
            Height = 90,
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0,
            CanUserAddRows = false,
            CanUserSortColumns = false,
            CanUserReorderColumns = false
        };
        _preList.Columns.Add(new DataGridTextColumn { Header = get("edict.bonus_key"), Binding = new Binding("Key"), Width = new DataGridLength(35, DataGridLengthUnitType.Star) });
        _preList.Columns.Add(new DataGridTextColumn { Header = get("edict.bonus_loc"), Binding = new Binding("Loc"), Width = new DataGridLength(65, DataGridLengthUnitType.Star) });
        var preMenu = new ContextMenu();
        var delPre = new MenuItem { Header = get("edict.bonus_delete") };
        delPre.Click += (_, _) => { if (_preList.SelectedItem is PreRow pr) _preList.Items.Remove(pr); };
        preMenu.Items.Add(delPre);
        _preList.ContextMenu = preMenu;
        if (existing != null)
            foreach (var pk in existing.Prerequisites)
                _preList.Items.Add(new PreRow { Key = pk, Loc = _techName(pk) });
        // 输入框（默认填满一行——Star 列拉伸）
        _preSearch = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 24, Margin = new Thickness(0, 4, 0, 4) };
        _preSearch.TextChanged += (_, _) => RefreshPreResults();
        _preSearch.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FillPreByEnter(); e.Handled = true; } };
        // 结果列表（**下方**；**参考科技页搜索预测列表样式**：单列本地化名 ListBox——用户：我说过参考，不参考还瞎做；
        // 多个结果 → 用户选择并**双击填充**）
        _preResults = new ListBox { MaxHeight = 220, Margin = new Thickness(0, 2, 0, 0) };
        _preResults.MouseDoubleClick += (_, _) => FillPreFromSelected();
        preWrap.Children.Add(_preList);   // 上：已选列表（2 列 key|本地化；右键删）
        preWrap.Children.Add(_preSearch);   // 中：输入框
        preWrap.Children.Add(_preResults);   // 下：结果列表（双击填充）
        AddRow(get("tech.edit_prereqs"), preWrap);
        _allTechs = allTechs;   // 前置搜索结果源
        // potential：**第一行 = 整个下拉选项（填满剩余行空间——用户）**；下面 = **大输入框**（自动换行，默认最少三行）
        _potentialCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };   // 无 Width → 填满内容列
        _potentialCombo.Items.Add(new ComboBoxItem { Content = get("edict.cond_always_yes") });
        _potentialCombo.Items.Add(new ComboBoxItem { Content = get("edict.cond_always_no") });
        _potentialCombo.Items.Add(new ComboBoxItem { Content = get("edict.cond_ai_yes") });
        _potentialCombo.Items.Add(new ComboBoxItem { Content = get("edict.cond_ai_no") });
        _potentialCombo.Items.Add(new ComboBoxItem { Content = get("edict.cond_custom") });
        AddRow(get("tech.edit_potential"), _potentialCombo);
        _customBox = new TextBox { MinHeight = 60, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };   // 3 行
        AddRow("", _customBox);
        InitPotential(existing?.PotentialRaw);
        // modifier：**复用法令做法**（3 列表格：键 / 本地化 / 数值 + 添加/删除/设置——弹窗选静态加成，不创新）
        var modWrap = new StackPanel();
        _modGrid = new DataGrid
        {
            Height = 140,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column
        };
        _modGrid.Columns.Add(new DataGridTextColumn { Header = get("edict.bonus_key"), Binding = new Binding("Key"), Width = new DataGridLength(35, DataGridLengthUnitType.Star) });
        _modGrid.Columns.Add(new DataGridTextColumn { Header = get("edict.bonus_loc"), Binding = new Binding("Loc"), Width = new DataGridLength(65, DataGridLengthUnitType.Star) });
        _modGrid.Columns.Add(new DataGridTextColumn { Header = get("edict.bonus_value"), Binding = new Binding("ValueText"), Width = new DataGridLength(40, DataGridLengthUnitType.Star) });
        if (existing != null)
            foreach (var (k, v) in existing.ModifierEntries)
                _modRows.Add(new ModRow { Key = k, Loc = BonusLoc(k), ValueText = v });
        _modGrid.ItemsSource = _modRows;
        // **右键菜单**（用户：加成怎么搞的？右键菜单怎么变成按钮了？——法令是右键菜单，改回右键菜单）
        var modMenu = new ContextMenu();
        var addMod = new MenuItem { Header = get("edict.bonus_add") };
        addMod.Click += (_, _) => ShowAddBonusDialog();
        var delMod = new MenuItem { Header = get("edict.bonus_delete") };
        delMod.Click += (_, _) => { if (_modGrid.SelectedItem is ModRow r) { _modRows.Remove(r); _modGrid.Items.Refresh(); } };
        var setMod = new MenuItem { Header = get("edict.bonus_set") };
        setMod.Click += (_, _) => ShowSetBonusDialog();
        modMenu.Items.Add(addMod);
        modMenu.Items.Add(delMod);
        modMenu.Items.Add(setMod);
        _modGrid.ContextMenu = modMenu;
        modWrap.Children.Add(_modGrid);
        AddRow(get("tech.edit_modifier"), modWrap);
        // weight_modifier / ai_weight（默认 weight = 1）
        _weightModBox = AddTextRow(get("tech.edit_weight_mod"), existing?.WeightModifierRaw ?? "weight = 1", false, multiLine: true);
        _aiWeightBox = AddTextRow(get("tech.edit_ai_weight"), existing?.AiWeightRaw ?? "weight = 1", false, multiLine: true);
        // 所属文件（**AI 权重下面多一行**；相对路径；落盘用——用户）；显示**去掉 common/technology/ 前缀**；
        // **新建默认 = 00_{ModPrefix}_technologies.txt**（用户）
        var ownerDefault = existing != null ? StripTechPrefix(existing.OwnerFile) : $"00_{_services.ModPrefix}_technologies.txt";
        _ownerFileBox = AddTextRow(get("tech.edit_owner_file"), ownerDefault);

        // 确定 / 取消
        var okRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var okBtn = new Button { Content = get("tech.edit_ok"), Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        okBtn.Click += (_, _) => { if (Commit(get)) DialogResult = true; };
        var cancelBtn = new Button { Content = get("tech.edit_cancel"), Width = 80, IsCancel = true };
        okRow.Children.Add(okBtn);
        okRow.Children.Add(cancelBtn);
        _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 显式行
        Grid.SetRow(okRow, _formRow);   // 放最后一行（用户：之前只看到两个按钮 = okRow 默认 Row 0 覆盖字段行）
        Grid.SetColumnSpan(okRow, 2);   // 横跨 label + 内容两列
        _form.Children.Add(okRow);

        scroll.Content = _form;
        Content = scroll;
    }

    /// <summary>提交：校验 + 回写/新建 TechNode（引擎内存 Add/Update）。</summary>
    private bool Commit(Func<string, string> get)
    {
        if (!int.TryParse(_tierBox.Text.Trim(), out var tier)) { MessageBox.Show(get("tech.edit_err_tier")); return false; }
        var levels = 1;
        if (_levelsCombo.SelectedIndex == 1 && !int.TryParse(_levelsNBox.Text.Trim(), out levels))
        { MessageBox.Show(get("tech.edit_err_levels")); return false; }   // 有限循环：n 输入框
        else if (_levelsCombo.SelectedIndex == 2) levels = -1;   // 无限循环
        int costPerLevel = 0;
        if (!string.IsNullOrWhiteSpace(_costPerLevelBox.Text)
            && !int.TryParse(_costPerLevelBox.Text.Trim(), out costPerLevel))
        { MessageBox.Show(get("tech.edit_err_level_growth")); return false; }   // 循环增长：需为数字
        var key = _keyBox.Text.Trim();
        if (key.Length == 0) { MessageBox.Show(get("tech.edit_err_key_empty")); return false; }
        if (_existing == null && _engine.Get(key) != null) { MessageBox.Show(get("tech.edit_err_key_dup")); return false; }

        var tech = _existing ?? new TechNode();
        tech.Key = key;
        // area/category 取 **Tag（key）**——下拉显示本地化名，值存 key；
        // 修改时原值不在下拉列表（如 other/自定义类别）→ **保留原值**（原样保留信息，不破坏）
        tech.Area = _areaCombo.SelectedItem is ComboBoxItem a && a.Tag is string ak && ak.Length > 0
            ? ak
            : (_existing?.Area ?? "physics");
        tech.Tier = tier;
        // cost：基础 = 数值（Simple）；自定义 = 块原文（cost = { factor = ... }，保存为块）
        if (_costModeCombo.SelectedIndex == 1)
        {
            tech.CostRaw = _costCustomBox.Text?.Trim() ?? "";
            tech.Cost = 0;   // 占位（自定义块以原文为准）
        }
        else
        {
            if (!int.TryParse(_costBox.Text.Trim(), out var cost)) { MessageBox.Show(get("tech.edit_err_cost")); return false; }
            tech.Cost = cost;
            tech.CostRaw = null;
        }
        tech.Levels = levels;
        tech.HasLevels = levels != 1;
        tech.CostPerLevel = costPerLevel;
        tech.HasCostPerLevel = !string.IsNullOrWhiteSpace(_costPerLevelBox.Text);
        if (_categoryCombo.SelectedItem is ComboBoxItem catSel && catSel.Tag is string ck && ck.Length > 0)
        {
            tech.Categories.Clear();
            tech.Categories.Add(ck);
        }
        // 未选中（原类别不在列表）→ 保留原 Categories
        tech.Icon = string.IsNullOrWhiteSpace(_iconBox.Text) ? null : _iconBox.Text.Trim();
        tech.Weight = string.IsNullOrWhiteSpace(_weightBox.Text) ? null : _weightBox.Text.Trim();
        tech.StartTech = _startTechCheck.IsChecked == true;   // 初始科技（勾选框）
        tech.PrereqForDesc = string.IsNullOrWhiteSpace(_prereqForBox.Text) ? null : _prereqForBox.Text.Trim();
        tech.Prerequisites.Clear();
        foreach (PreRow p in _preList.Items)
            if (!tech.Prerequisites.Contains(p.Key))
                tech.Prerequisites.Add(p.Key);
        // potential：预设 → 标准文本；自定义 → 用户文本
        tech.PotentialRaw = _customBox.Text?.Trim() ?? "";
        tech.ModifierEntries.Clear();
        foreach (var r in _modRows)
            if (!string.IsNullOrWhiteSpace(r.Key))
                tech.ModifierEntries.Add((r.Key.Trim(), r.ValueText.Trim()));
        tech.WeightModifierRaw = string.IsNullOrWhiteSpace(_weightModBox.Text) ? null : _weightModBox.Text.Trim();
        tech.AiWeightRaw = string.IsNullOrWhiteSpace(_aiWeightBox.Text) ? null : _aiWeightBox.Text.Trim();
        tech.OwnerFile = string.IsNullOrWhiteSpace(_ownerFileBox.Text) ? null : PrependTechPrefix(_ownerFileBox.Text.Trim());
        // 本地化已由 LocalisationEditBox 失焦即写**本地化引擎内存**（technologies_{Prefix}_l_{lang}.yml）——
        // 不再写 TechNode.NameLocalisations/DescLocalisations 半成品留存（用户 2026-08）

        // 脏字段标记（保存只写改过的字段，未编辑字段保留原样——新建全字段由引擎处理）
        MarkDirty(tech, _initial);

        if (_existing == null)
            _engine.AddItem(tech);
        else
            _engine.UpdateItem(tech);
        Result = tech;
        return true;
    }

    /// <summary>比较弹窗当前值 vs 打开时快照，标记修改过的字段到 tech.DirtyFields（保存写回用）。
    /// initial = null（新建）不标记——引擎对新建块全字段写。</summary>
    private static void MarkDirty(TechNode tech, TechNode? initial)
    {
        if (initial == null)
            return;
        if (!string.Equals(tech.Area, initial.Area, StringComparison.OrdinalIgnoreCase)) tech.DirtyFields.Add(TechField.Area);
        if (tech.Tier != initial.Tier) tech.DirtyFields.Add(TechField.Tier);
        if (tech.Cost != initial.Cost
            || !string.Equals(tech.CostRaw, initial.CostRaw, StringComparison.Ordinal)) tech.DirtyFields.Add(TechField.Cost);
        if (tech.Levels != initial.Levels || tech.HasLevels != initial.HasLevels) tech.DirtyFields.Add(TechField.Levels);
        if (tech.CostPerLevel != initial.CostPerLevel || tech.HasCostPerLevel != initial.HasCostPerLevel) tech.DirtyFields.Add(TechField.CostPerLevel);
        if (!tech.Categories.SequenceEqual(initial.Categories, StringComparer.OrdinalIgnoreCase)) tech.DirtyFields.Add(TechField.Category);
        if (!tech.Prerequisites.SequenceEqual(initial.Prerequisites, StringComparer.OrdinalIgnoreCase)) tech.DirtyFields.Add(TechField.Prerequisites);
        if (!string.Equals(tech.Icon, initial.Icon, StringComparison.OrdinalIgnoreCase)) tech.DirtyFields.Add(TechField.Icon);
        if (!string.Equals(tech.Weight, initial.Weight, StringComparison.Ordinal)) tech.DirtyFields.Add(TechField.Weight);
        if (tech.StartTech != initial.StartTech) tech.DirtyFields.Add(TechField.StartTech);
        if (!string.Equals(tech.PotentialRaw, initial.PotentialRaw, StringComparison.Ordinal)) tech.DirtyFields.Add(TechField.Potential);
        if (!ModifierEntriesEqual(tech, initial)) tech.DirtyFields.Add(TechField.Modifier);
        if (!string.Equals(tech.WeightModifierRaw, initial.WeightModifierRaw, StringComparison.Ordinal)) tech.DirtyFields.Add(TechField.WeightModifier);
        if (!string.Equals(tech.AiWeightRaw, initial.AiWeightRaw, StringComparison.Ordinal)) tech.DirtyFields.Add(TechField.AiWeight);
        if (!string.Equals(tech.PrereqForDesc, initial.PrereqForDesc, StringComparison.Ordinal)) tech.DirtyFields.Add(TechField.PrereqForDesc);
    }

    /// <summary>modifier 条目列表是否相等（顺序敏感）。</summary>
    private static bool ModifierEntriesEqual(TechNode a, TechNode b)
    {
        if (a.ModifierEntries.Count != b.ModifierEntries.Count)
            return false;
        for (int i = 0; i < a.ModifierEntries.Count; i++)
            if (!string.Equals(a.ModifierEntries[i].Key, b.ModifierEntries[i].Key, StringComparison.Ordinal)
                || !string.Equals(a.ModifierEntries[i].Value, b.ModifierEntries[i].Value, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>加载 potential 原文 → 匹配预设或自定义。</summary>
    private void InitPotential(string? raw)
    {
        _customBox.Text = raw ?? "";
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0) _potentialCombo.SelectedIndex = (int)PotentialPreset.AlwaysYes;
        else if (trimmed.Contains("always = no", StringComparison.OrdinalIgnoreCase)) _potentialCombo.SelectedIndex = (int)PotentialPreset.AlwaysNo;
        else if (trimmed.Contains("is_ai = yes", StringComparison.OrdinalIgnoreCase)) _potentialCombo.SelectedIndex = (int)PotentialPreset.AiYes;
        else if (trimmed.Contains("is_ai = no", StringComparison.OrdinalIgnoreCase)) _potentialCombo.SelectedIndex = (int)PotentialPreset.AiNo;
        else _potentialCombo.SelectedIndex = (int)PotentialPreset.Custom;
        _potentialCombo.SelectionChanged += (_, _) =>
        {
            if (_potentialCombo.SelectedIndex == (int)PotentialPreset.AlwaysYes) _customBox.Text = "";
            else if (_potentialCombo.SelectedIndex == (int)PotentialPreset.AlwaysNo) _customBox.Text = "always = no";
            else if (_potentialCombo.SelectedIndex == (int)PotentialPreset.AiYes) _customBox.Text = "is_ai = yes";
            else if (_potentialCombo.SelectedIndex == (int)PotentialPreset.AiNo) _customBox.Text = "is_ai = no";
        };
    }

    // ===== 前置科技：**复用科技搜索算法**（key 精确/包含/本地化名包含；不跳转） =====

    /// <summary>输入即过滤：结果 = key 精确/包含/本地化名包含（排除已选）。</summary>
    private void RefreshPreResults()
    {
        _preResults.Items.Clear();
        var kw = _preSearch.Text?.Trim() ?? "";
        if (kw.Length == 0)
            return;
        foreach (var t in _allTechs.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            bool hit = t.Key.Equals(kw, StringComparison.OrdinalIgnoreCase)
                || t.Key.Contains(kw, StringComparison.OrdinalIgnoreCase)
                || _techName(t.Key).Contains(kw, StringComparison.OrdinalIgnoreCase);
            if (!hit)
                continue;
            if (IsPrePicked(t.Key))
                continue;
            _preResults.Items.Add(new PreRow { Key = t.Key, Loc = _techName(t.Key) });
        }
    }

    /// <summary>回车填充：**恰好 1 个结果 → 自动填充**（用户规则）；多个结果 → 填充当前选中（用户已选）。</summary>
    private void FillPreByEnter()
    {
        if (_preResults.Items.Count == 1 && _preResults.Items[0] is PreRow only)
        {
            AddPre(only);
            return;
        }
        if (_preResults.Items.Count > 1 && _preResults.SelectedItem is PreRow sel)
            AddPre(sel);
    }

    /// <summary>双击结果行 → 填充。</summary>
    private void FillPreFromSelected()
    {
        if (_preResults.SelectedItem is PreRow sel)
            AddPre(sel);
    }

    private bool IsPrePicked(string key)
    {
        foreach (PreRow p in _preList.Items)
            if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void AddPre(PreRow row)
    {
        if (IsPrePicked(row.Key))
            return;
        _preList.Items.Add(row);
        _preSearch.Clear();
        _preResults.Items.Clear();
    }

    // ===== 加成（modifier）：**完全复用法令做法**（3 列表格 键/本地化/数值 + 添加/设置弹窗——不创新） =====

    /// <summary>加成键本地化（base/custom 按当前语种取；无 → 空）。</summary>
    private string BonusLoc(string key)
    {
        var engine = _services.StaticModifierEngine;
        if (engine == null)
            return "";
        var b = engine.GetBaseModifier(key);
        if (b != null && b.Localisations.TryGetValue(_modLang, out var v))
            return v;
        var c = engine.GetCustom(key);
        if (c != null && c.Localisations.TryGetValue(_modLang, out var v2))
            return v2;
        return "";
    }

    /// <summary>添加加成：选 key（输入实时过滤）+ 数值 → 新行。</summary>
    private void ShowAddBonusDialog()
    {
        var picked = ShowBonusPickerDialog(out var key, out var value, "", 1);
        if (!picked)
            return;
        _modRows.Add(new ModRow { Key = key, Loc = BonusLoc(key), ValueText = value.ToString(CultureInfo.InvariantCulture) });
        _modGrid.Items.Refresh();
    }

    /// <summary>设置加成：改当前行的 key（从选项重选）或数值。</summary>
    private void ShowSetBonusDialog()
    {
        if (_modGrid.SelectedItem is not ModRow row)
            return;
        double.TryParse(row.ValueText, NumberStyles.Any, CultureInfo.InvariantCulture, out var oldValue);
        var picked = ShowBonusPickerDialog(out var key, out var value, row.Key, oldValue);
        if (!picked)
            return;
        row.Key = key;
        row.Loc = BonusLoc(key);
        row.ValueText = value.ToString(CultureInfo.InvariantCulture);
        _modGrid.Items.Refresh();
    }

    /// <summary>加成选择弹窗（照抄法令 ShowBonusPickerDialog）：顶部 输入框 | 数值 一行；下方 2 列（键 + 本地化）。</summary>
    private bool ShowBonusPickerDialog(out string key, out double value, string initialKey, double initialValue)
    {
        key = "";
        value = initialValue;
        var pickedKey = "";
        var pickedValue = initialValue;
        var win = new Window
        {
            Title = _services.Localisation.Get("edict.bonus_pick_title"),
            Width = 460,
            Height = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this
        };
        var panel = new DockPanel { Margin = new Thickness(12) };

        // 顶部一行：输入框（左，星列）+ 数值（右，固定宽）
        var topRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90, GridUnitType.Pixel) });
        var searchBox = new TextBox { Text = initialKey, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(searchBox, 0);
        topRow.Children.Add(searchBox);
        var valueBox = new TextBox
        {
            Text = initialValue.ToString(CultureInfo.InvariantCulture),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = _services.Localisation.Get("edict.bonus_value")
        };
        Grid.SetColumn(valueBox, 1);
        topRow.Children.Add(valueBox);
        DockPanel.SetDock(topRow, Dock.Top);
        panel.Children.Add(topRow);

        var okBtn = new Button { Content = _services.Localisation.Get("edict.ok"), Padding = new Thickness(14, 4, 14, 4), IsDefault = true, IsEnabled = false };
        var cancelBtn = new Button { Content = _services.Localisation.Get("edict.cancel"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        DockPanel.SetDock(btnRow, Dock.Bottom);
        panel.Children.Add(btnRow);

        // 下方选项列表：2 列（键 + 本地化）——当前表格样式
        var resultGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0,
            CanUserReorderColumns = false,
            CanUserSortColumns = false,
            SelectionMode = DataGridSelectionMode.Single
        };
        resultGrid.Columns.Add(new DataGridTextColumn
        {
            Header = _services.Localisation.Get("edict.bonus_key"),
            Binding = new Binding("Key"),
            Width = new DataGridLength(35, DataGridLengthUnitType.Star)
        });
        resultGrid.Columns.Add(new DataGridTextColumn
        {
            Header = _services.Localisation.Get("edict.bonus_loc"),
            Binding = new Binding("Loc"),
            Width = new DataGridLength(65, DataGridLengthUnitType.Star)
        });
        panel.Children.Add(resultGrid);
        win.Content = panel;

        void Refresh()
        {
            resultGrid.Items.Clear();
            var kw = searchBox.Text?.Trim() ?? "";
            if (kw.Length == 0 || _services.StaticModifierEngine == null)
            {
                okBtn.IsEnabled = false;
                return;
            }
            foreach (var obj in _services.StaticModifierEngine.Search(kw))
            {
                var name = obj.GetType().GetProperty("Name")?.GetValue(obj) as string;
                if (string.IsNullOrEmpty(name))
                    continue;
                resultGrid.Items.Add(new ModRow { Key = name, Loc = BonusLoc(name), ValueText = "" });
            }
            // 初始 key 已填 → 默认可确定（设置场景保留原 key）
            okBtn.IsEnabled = initialKey.Length > 0 && resultGrid.SelectedItem is null;
        }
        searchBox.TextChanged += (_, _) => Refresh();
        resultGrid.SelectionChanged += (_, _) => okBtn.IsEnabled = resultGrid.SelectedItem is ModRow;
        resultGrid.MouseDoubleClick += (_, _) =>
        {
            if (okBtn.IsEnabled) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        okBtn.Click += (_, _) =>
        {
            var pickedName = (resultGrid.SelectedItem as ModRow)?.Key
                ?? (initialKey.Length > 0 ? searchBox.Text?.Trim() : "");
            if (string.IsNullOrEmpty(pickedName))
                return;
            pickedKey = pickedName;
            if (!double.TryParse(valueBox.Text?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out pickedValue))
                pickedValue = 1;
            win.Close();
        };
        searchBox.Focus();
        searchBox.SelectAll();
        win.ShowDialog();
        key = pickedKey;
        value = pickedValue;
        return !string.IsNullOrEmpty(key);
    }

    /// <summary>下拉按 Tag（key）选中对应项。</summary>
    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem ci && string.Equals(ci.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
    }

    /// <summary>所属文件显示：去掉 common/technology/ 前缀（科技必在其下——用户）。</summary>
    private static string StripTechPrefix(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        const string prefix = "common/technology/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : path;
    }

    /// <summary>所属文件保存：补回 common/technology/ 前缀（存储始终全路径，落盘一致）。</summary>
    private static string PrependTechPrefix(string text)
    {
        const string prefix = "common/technology/";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text : prefix + text;
    }

    // ===== 表单辅助 =====

    /// <summary>表单行：label 列 Auto（**全表共享最宽**——统一宽度避免犬牙交错，用户规则）+ 内容列 Star；空 label 不占空格。
    /// **每行上下留 4px 间距**（用户：糊在一起字看不清——共享 Grid 后必须显式行距）；
    /// **每行显式添加 RowDefinition（Auto）**——Grid 无 RowDefinitions 时隐式行会导致全部叠在 Row 0（用户：只看得到确定/取消按钮）。</summary>
    private void AddRow(string label, FrameworkElement control)
    {
        _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 显式行
        var tb = new TextBlock
        {
            Text = label.Length == 0 ? "" : label + ":",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 6, 4)   // 上 4 下 4（行距）
        };
        Grid.SetRow(tb, _formRow);
        Grid.SetColumn(tb, 0);
        _form.Children.Add(tb);
        // 内容控件：追加垂直 Margin（保留调用处已设的水平 Margin，不覆盖）
        var m = control.Margin;
        control.Margin = new Thickness(m.Left, m.Top + 4, m.Right, m.Bottom + 4);
        Grid.SetRow(control, _formRow);
        Grid.SetColumn(control, 1);
        _form.Children.Add(control);
        _formRow++;
    }

    private TextBox AddTextRow(string label, string text, bool readOnly = false, bool multiLine = false)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = readOnly,
            TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = multiLine,
            MinHeight = multiLine ? 50 : 24,
            VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };
        AddRow(label, box);
        return box;
    }

    private ComboBox AddComboRow(string label, string[] items, string? selected)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, Width = 220 };
        foreach (var it in items)
            combo.Items.Add(new ComboBoxItem { Content = it });
        if (selected != null)
        {
            for (int i = 0; i < combo.Items.Count; i++)
                if (combo.Items[i] is ComboBoxItem ci && string.Equals(ci.Content?.ToString(), selected, StringComparison.OrdinalIgnoreCase))
                    combo.SelectedIndex = i;
        }
        AddRow(label, combo);
        return combo;
    }
}
