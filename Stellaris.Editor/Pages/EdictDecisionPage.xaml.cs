using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Stellaris.Editor.Controls;
using Stellaris.Engine.EdictDecision;
using Stellaris.Engine.StrategicResource;
using Stellaris.Parser;

namespace Stellaris.Editor.Pages;

/// <summary>法令/决议可视化编辑器：两个选项卡（法令/决议），每个选项卡内含自己的完整页面
/// （列表 + 编辑表单）。**本期不落盘**——编辑停留内存；保存按钮为预留接口。
/// 列表右键菜单：设置所属相对路径 / 删除 / 新建 / 保存（占位）。
/// 资源消耗：从左到右 3 个 2 列表格（启动消耗 cost / 每月消耗 upkeep / 每月产出 product），
/// 右键增加/修改/删除/倍率（multiplier Simple）/条件（trigger Block）。</summary>
public sealed class EdictDecisionPage : UserControl
{
    private readonly EngineServices _services;
    private readonly EdictDecisionEngine _engine;
    private readonly List<string> _resourceKeys;
    private readonly KindForm _edictForm;
    private readonly KindForm _decisionForm;

    public EdictDecisionPage(EngineServices services)
    {
        _services = services;
        _engine = services.EdictDecisionEngine!;
        var loc = services.Localisation;

        // 资源种类下拉（复用资源引擎对外接口 GetResourceKeys）
        _resourceKeys = services.StrategicResourceEngine?.GetResourceKeys().ToList() ?? new();

        // 每个选项卡一个独立完整页面（列表 + 表单）
        _edictForm = new KindForm(EdictDecisionKind.Edict, _engine, services, _resourceKeys);
        _decisionForm = new KindForm(EdictDecisionKind.Decision, _engine, services, _resourceKeys);

        var typeTabs = new TabControl();
        typeTabs.Items.Add(new TabItem { Header = loc.Get("edict.type_edict"), Content = _edictForm.Root });
        typeTabs.Items.Add(new TabItem { Header = loc.Get("edict.type_decision"), Content = _decisionForm.Root });
        // 静态加成（V0.2——内存编辑不落盘；照抄法令左列表 + 右表单结构）
        _staticPage = new StaticModifierEditorPage(services);
        typeTabs.Items.Add(new TabItem { Header = loc.Get("edict.type_static"), Content = _staticPage });
        // 战略资源（用户 2026-08：移入 4 合 1"综合"页，放在静态加成右侧——原页复用）
        _resourcePage = new StrategicResourcePage(services);
        typeTabs.Items.Add(new TabItem { Header = loc.Get("edict.type_resource"), Content = _resourcePage });
        typeTabs.SelectedIndex = 0;
        Content = typeTabs;
        AttachWidthSync();   // 4 选项卡左列表宽度调整通用（用户 2026-08）
    }

    private StaticModifierEditorPage? _staticPage;
    private StrategicResourcePage? _resourcePage;

    /// <summary>综合页 4 选项卡**左列表宽度通用**：任意页拖分隔条，把该页左列宽（实际像素）同步到其余页。
    /// 四页同构（左列表 Star 1 + GridSplitter + 右编辑 Star 4——用户 2026-08）。</summary>
    private void AttachWidthSync()
    {
        foreach (var root in AllFormRoots())
        {
            if (root is not Grid g)
                continue;
            foreach (var child in g.Children)
                if (child is GridSplitter sp)
                    sp.DragCompleted += (_, _) => SyncLeftWidths(g);
        }
    }

    private void SyncLeftWidths(Grid from)
    {
        if (from.ColumnDefinitions.Count < 3)
            return;
        double w = from.ColumnDefinitions[0].ActualWidth;
        if (w <= 0)
            return;
        foreach (var root in AllFormRoots())
            if (root is Grid g && g.ColumnDefinitions.Count >= 3)
                g.ColumnDefinitions[0].Width = new GridLength(w);   // 像素（Star 行用 ActualWidth，用户 2026-08）
    }

    private IEnumerable<FrameworkElement> AllFormRoots()
    {
        yield return _edictForm.Root;
        yield return _decisionForm.Root;
        if (_staticPage?.Content is FrameworkElement se)
            yield return se;
        if (_resourcePage?.Content is FrameworkElement re)
            yield return re;
    }

    // ============================================================
    // 单个选项卡的完整页面：左列表 + 右编辑表单
    // ============================================================
    private sealed class KindForm
    {
        public FrameworkElement Root { get; private set; } = null!;

        private readonly EdictDecisionKind _kind;
        private readonly EdictDecisionEngine _engine;
        private readonly EngineServices _services;
        private readonly List<string> _resourceKeys;

        private ListBox _list = null!;
        private DockPanel? _leftPanel;   // 左列表容器（宽度写死 320——用户规则：参照星系样式固定布局）
        private TextBox _searchBox = null!;   // 左列表搜索框（Key/本地化名过滤）
        private System.Windows.Threading.DispatcherTimer _searchDebounce = null!;   // 搜索框 2 秒防抖
        private TextBox _keyBox = null!;
        private Controls.LocalisationEditBox _locBox = null!;   // 统一本地化组件（名称/描述：逻辑值可编辑 → 显示值只读）
        private TextBox _iconBox = null!;
        private ComboBox _lengthCombo = null!;
        private TextBox _lengthBox = null!;
        private CheckBox _importantCheck = null!;   // 决议：important（重要的）
        private CheckBox _ownedCheck = null!;       // 决议：owned_planets_only（仅限被拥有的星球）
        private TextBox _enactmentBox = null!;      // 决议：enactment_time（延迟时间，默认 0，0 不写）
        private Controls.ResourceTabsControl _resTabs = null!;
        private ComboBox _potentialCombo = null!;
        private TextBox _potentialCustomBox = null!;
        private ComboBox _allowCombo = null!;
        private TextBox _allowCustomBox = null!;
        private TextBox _aiWeightBox = null!;
        private TextBox _effectRawBox = null!;   // 效果（effect——写事件/命令）编辑框
        private TextBox _ownerFileBox = null!;   // 所属文件（文件名；前缀自动隐藏/自动补——用户 2026-08）
        private DataGrid _effectList = null!;
        private readonly List<BonusRowVm> _bonusRows = new();   // 加成行（复制用）
        private Stellaris.Engine.Deposit.DepositEngine _depositEngine = null!;   // 地形引擎（Effect add_deposit/remove_deposit 选择）
        private EdictDecisionItem? _current;
        private bool _loading;   // 加载/刷新表单时抑制保存索引登记（防误登记）

        public KindForm(EdictDecisionKind kind, EdictDecisionEngine engine, EngineServices services, List<string> resourceKeys)
        {
            _kind = kind;
            _engine = engine;
            _services = services;
            _resourceKeys = resourceKeys;
            Build();
        }

        private string ModLang => _services.Localisation.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "simp_chinese" : "english";

        private void Build()
        {
            var loc = _services.Localisation;

            // ===== 左列：搜索行（保存 + 搜索框 + 🔍——参考战略资源页，无复制按钮）+ 列表（右键菜单） =====
            // DockPanel：ListBox 填满可用高度 → 内容超出出现滚动条（StackPanel 会给无限高度导致滚动条消失）
            _leftPanel = new DockPanel { Margin = new Thickness(8, 8, 4, 4) };   // 无静态 Width——填满 Star 列，拖 GridSplitter 真实变宽/变窄（用户）
            var searchRow = new Grid { Margin = new Thickness(8, 6, 8, 4) };   // 边距对齐静态加成页（用户 2026-08——不再偏左上）
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var saveBtn = new Button
            {
                Content = loc.Get("edict.save"), Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
            };
            saveBtn.Click += (_, _) => SaveAll();   // 与右键"保存"同一方法
            Grid.SetColumn(saveBtn, 0);
            searchRow.Children.Add(saveBtn);
            _searchBox = new TextBox { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _searchDebounce = Stellaris.Editor.Controls.SearchDebouncer.Attach(_searchBox, RefreshList);
            _searchBox.KeyDown += (_, e) => { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; _searchDebounce.Stop(); RefreshList(); } };
            Grid.SetColumn(_searchBox, 1);
            searchRow.Children.Add(_searchBox);
            var searchBtn = new Button
            {
                Content = "🔍", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.FindResource("SearchButtonStyle")
            };
            searchBtn.Click += (_, _) => RefreshList();
            Grid.SetColumn(searchBtn, 2);
            searchRow.Children.Add(searchBtn);
            // searchRow 在下方布局中挂到外层 grid 行0（横跨整页）——不放入 leftPanel

            _list = new ListBox
            {
                MinWidth = 200,
                ItemContainerStyle = StretchListBoxItemStyle()   // 项统一撑满列表宽（对齐资源页：无 HorizontalAlignment）
            };
            var listMenu = new ContextMenu();
            var newItem = new MenuItem { Header = "新建" };
            newItem.Click += (_, _) => NewItem();
            listMenu.Items.Add(newItem);
            // 所属文件已改为表单底部行（用户 2026-08）——右键"设置所属相对路径"弹窗移除
            var delItem = new MenuItem { Header = loc.Get("edict.delete") };
            delItem.Click += (_, _) => DeleteSelected();
            listMenu.Items.Add(delItem);
            var saveItem = new MenuItem { Header = loc.Get("edict.save") };
            saveItem.Click += (_, _) => SaveAll();
            listMenu.Items.Add(saveItem);
            _list.ContextMenu = listMenu;
            _list.SelectionChanged += (_, _) => OnItemSelected();

            // ===== 右编辑表单 =====
            // 右侧编辑表单：**抄资源页结构**（用户：抄一下又不寒碜——资源页 scroll 只设 VerticalScrollBarVisibility=Auto、
            // DockPanel 容器最后子元素 Fill 就正常）——去掉手动 Stretch/Disabled/SizeChanged
            var editScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(4, 8, 8, 4) };
            var editPanel = new StackPanel { Margin = new Thickness(8) };
            editScroll.Content = editPanel;
            var rightPanel = new DockPanel();
            rightPanel.Children.Add(editScroll);   // DockPanel 最后子元素 = Fill（占满右侧分配空间）
            // （rightPanel 的 Grid 挂载在下方 grid 声明后统一处理）

            // ---- 基础：语种下拉（全宽）+ key + 名称/描述双值（翻译编辑 + 逻辑值只读）+ 图标 + 锁定 ----
            editPanel.Children.Add(SectionTitle(loc.Get("edict.basic")));
            _keyBox = AddLabeledTextBox(editPanel, loc.Get("edict.key"));
            _locBox = new Controls.LocalisationEditBox
            {
                Adapter = _services.Adapter,
                GetNameKey = () => _current == null ? "" : EdictDecisionEngine.LocalisationKey(_current),
                GetLangs = () =>
                {
                    // 标准语种候选：**按当前法令条目**——词条实际存在的语种 ∪ 模组启用语言（不是全局语种）。
                    // 该法令全语种 → 全语种可选；非全语种 → 只显示模组选定的或它自己有的，除此之外不显示。
                    var modLangs = _services.ModPrefs?.EnabledLanguages ?? new List<string>();
                    if (_current == null)
                        return modLangs.OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var nameKey = EdictDecisionEngine.LocalisationKey(_current);
                    var descKey = nameKey + "_desc";
                    var langs = new List<string>(modLangs);
                    var adapter = _services.Adapter;
                    foreach (var lang in adapter.GetLocalisationLanguages())
                    {
                        var entries = adapter.GetLocalisationEntriesDetailed(lang);
                        if (entries != null && (entries.ContainsKey(nameKey) || entries.ContainsKey(descKey)))
                            langs.Add(lang);
                    }
                    return langs.Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.Ordinal).ToList();
                },
                SaveLocalisation = (lang, key, text) =>
                {
                    try
                    {
                        // 写目标本地化文件（mod 目录：localisation/{lang}/edicts_{ModPrefix}_l_{lang}.yml——自动创建；
                        // 编辑即落盘（WriteLocalisation 单文件））。键原本在别的文件 → **旧位置也写**
                        // （词条移走后写剩余/空头清理，防磁盘残留重复——同引擎层"新旧位置都登记"）。
                        var prefix = _services.ModPrefs?.ModPrefix ?? "smt";
                        var filePrefix = _kind == EdictDecisionKind.Edict ? "edicts_" : "decisions_";
                        var fileName = filePrefix + prefix + "_l_" + lang + ".yml";
                        var targetPath = $"localisation/{lang}/{fileName}";
                        var adapter = _services.Adapter;
                        var index = adapter.GetLocalisationKeyFiles(lang);
                        string? oldFile = index.TryGetValue(key, out var cur)
                            && !string.Equals(cur, targetPath, StringComparison.OrdinalIgnoreCase)
                            ? cur : null;
                        adapter.UpdateLocalisationEntry(lang, targetPath, key, text);
                        adapter.ExpandLocalisationKey(lang, key);
                        adapter.WriteLocalisation(lang, fileName);   // 新位置
                        if (oldFile != null)
                        {
                            var oldName = oldFile.Substring(oldFile.LastIndexOf('/') + 1);
                            adapter.WriteLocalisation(lang, oldName, writeIfEmpty: true);   // 旧位置（剩余/空头）
                        }
                        RefreshLoc();
                        RefreshList();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"UpdateLocalisation failed: {ex.Message}", "Stellaris Mod Tools");
                        RefreshLoc();
                    }
                }
            };
            editPanel.Children.Add(_locBox);
            _locBox.Reload();
            _iconBox = AddInlineTextBox(editPanel, loc.Get("edict.icon"));

            // ---- 持续时间：一行（label + 下拉 + 数值） ----
            var lengthRow = new Grid { Margin = new Thickness(0, 2, 0, 8) };
            lengthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70, GridUnitType.Pixel) });
            lengthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            lengthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            lengthRow.Children.Add(new TextBlock { Text = loc.Get("edict.length"), Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            _lengthCombo = new ComboBox { Width = 100, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            if (_kind == EdictDecisionKind.Edict)
                _lengthCombo.Items.Add(new ComboBoxItem { Content = loc.Get("edict.length_infinite"), Tag = true });
            _lengthCombo.Items.Add(new ComboBoxItem { Content = loc.Get("edict.length_limited"), Tag = false });
            _lengthCombo.SelectedIndex = 0;   // 决议唯一项=有限；法令默认无限
            Grid.SetColumn(_lengthCombo, 1);
            lengthRow.Children.Add(_lengthCombo);
            _lengthBox = new TextBox { Text = "-1", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_lengthBox, 2);
            lengthRow.Children.Add(_lengthBox);
            if (_kind == EdictDecisionKind.Edict)
                editPanel.Children.Add(lengthRow);   // 法令有持续时间（无限/有限）
            else
            {
                // 决议没有持续时间——一行：important / owned_planets_only 勾选 + 延迟时间 label+输入框（撑满右侧）
                var decRow = new Grid { Margin = new Thickness(0, 2, 0, 8) };
                decRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                decRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                decRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                decRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _importantCheck = new CheckBox { Content = loc.Get("edict.important"), VerticalAlignment = VerticalAlignment.Center };
                _importantCheck.Checked += (_, _) => Mark(EdictField.Important);
                _importantCheck.Unchecked += (_, _) => Mark(EdictField.Important);
                decRow.Children.Add(_importantCheck);
                _ownedCheck = new CheckBox { Content = loc.Get("edict.owned_planets_only"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
                _ownedCheck.Checked += (_, _) => Mark(EdictField.OwnedPlanetsOnly);
                _ownedCheck.Unchecked += (_, _) => Mark(EdictField.OwnedPlanetsOnly);
                Grid.SetColumn(_ownedCheck, 1);
                decRow.Children.Add(_ownedCheck);
                decRow.Children.Add(new TextBlock
                {
                    Text = loc.Get("edict.enactment_time"), Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0)
                });
                Grid.SetColumn((TextBlock)decRow.Children[^1], 2);
                _enactmentBox = new TextBox { Text = "0", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
                Grid.SetColumn(_enactmentBox, 3);
                decRow.Children.Add(_enactmentBox);
                _enactmentBox.LostFocus += (_, _) => SaveEnactmentTime();
                editPanel.Children.Add(decRow);
            }

            // ---- 资源消耗：TabControl 3 页（启动消耗 / 每月消耗 / 每月产出） ----
            editPanel.Children.Add(SectionTitle(loc.Get("edict.resources")));
            _resTabs = new Controls.ResourceTabsControl
            {
                Adapter = _services.Adapter,
                ModLang = ModLang,
                ResourceKeys = _resourceKeys,
                TabsHeight = 200,
                Margin = new Thickness(0, 0, 0, 6)
            };
            _resTabs.Changed += () => { RefreshBuckets(); Mark(EdictField.Resources); };
            editPanel.Children.Add(_resTabs);
            // ---- 条件 ----
            editPanel.Children.Add(SectionTitle(loc.Get("edict.conditions")));
            _potentialCombo = BuildConditionRow(editPanel, loc.Get("edict.potential"), out _potentialCustomBox);
            _allowCombo = BuildConditionRow(editPanel, loc.Get("edict.allow"), out _allowCustomBox);

            if (_kind == EdictDecisionKind.Edict)   // 加成栏仅法令有；决议无加成（用 effect）
            {
            // ---- 加成（modifier 修改器——3 列：键 / 本地化 / 数值；右键菜单 添加/删除/设置）----
            // 标题 + 复制按钮同一行
            var bonusHeader = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            bonusHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bonusHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bonusHeader.Children.Add(SectionTitle(loc.Get("edict.bonuses")));
            var copyBtn = new Button
            {
                Content = loc.Get("edict.bonus_copy"),
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            };
            copyBtn.Click += (_, _) => CopyBonuses();
            Grid.SetColumn(copyBtn, 1);
            bonusHeader.Children.Add(copyBtn);
            editPanel.Children.Add(bonusHeader);
            _effectList = new DataGrid
            {
                AutoGenerateColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeaderWidth = 0,
                MinHeight = 100,
                CanUserReorderColumns = false,
                CanUserSortColumns = false,
                SelectionMode = DataGridSelectionMode.Single,
                Margin = new Thickness(0, 2, 0, 6)
            };
            _effectList.Columns.Add(new DataGridTextColumn
            {
                Header = loc.Get("edict.bonus_key"),
                Binding = new System.Windows.Data.Binding("Key"),
                Width = new DataGridLength(30, DataGridLengthUnitType.Star)
            });
            _effectList.Columns.Add(new DataGridTextColumn
            {
                Header = loc.Get("edict.bonus_loc"),
                Binding = new System.Windows.Data.Binding("Loc"),
                Width = new DataGridLength(45, DataGridLengthUnitType.Star)
            });
            _effectList.Columns.Add(new DataGridTextColumn
            {
                Header = loc.Get("edict.bonus_value"),
                Binding = new System.Windows.Data.Binding("ValueText"),
                Width = new DataGridLength(25, DataGridLengthUnitType.Star)
            });
            var effectMenu = new ContextMenu();
            var addBonus = new MenuItem { Header = loc.Get("edict.bonus_add") };
            addBonus.Click += (_, _) => ShowAddBonusDialog();
            var setBonus = new MenuItem { Header = loc.Get("edict.bonus_set") };
            setBonus.Click += (_, _) => ShowSetBonusDialog();
            var delEffect = new MenuItem { Header = loc.Get("edict.bonus_delete") };
            delEffect.Click += (_, _) => DeleteSelectedEffect();
            effectMenu.Items.Add(addBonus);
            effectMenu.Items.Add(setBonus);
            effectMenu.Items.Add(delEffect);
            _effectList.ContextMenu = effectMenu;
            // 单元格直接编辑（双击改数值/键）→ 读**编辑控件当前文本**（CellEditEnding 时源可能未提交——
            // 用户 2026-08：同静态加成 bug，读行对象会拿到旧值）→ 同步回 Effects + 登记
            _effectList.CellEditEnding += (_, e) =>
            {
                if (_loading || _current == null || e.EditAction != DataGridEditAction.Commit)
                    return;
                if (e.EditingElement is TextBox tb && e.Row.Item is BonusRowVm row)
                {
                    var path = (e.Column as DataGridTextColumn)?.Binding is System.Windows.Data.Binding b ? b.Path?.Path ?? "" : "";
                    if (path == "ValueText") row.ValueText = tb.Text;
                    else if (path == "Key") row.Key = tb.Text;
                }
                _current.Effects.Clear();
                foreach (var r in _bonusRows)
                {
                    if (string.IsNullOrWhiteSpace(r.Key))
                        continue;
                    if (double.TryParse(r.ValueText, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var dv))
                        _current.Effects.Add((r.Key, dv));
                }
                Mark(EdictField.Bonuses);
            };
            editPanel.Children.Add(_effectList);
            }

            // ---- 效果（effect——写事件/命令）----
            editPanel.Children.Add(SectionTitle(loc.Get("edict.effect")));
            _effectRawBox = new TextBox
            {
                MinHeight = 80, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 6), FontFamily = new FontFamily("Consolas")
            };
            // 右键菜单自选效果（hidden_effect 放最后）：add_modifier / remove_modifier / add_deposit / remove_deposit / hidden_effect
            _depositEngine = new Stellaris.Engine.Deposit.DepositEngine(_services.Adapter, ModLang);
            var effectRawMenu = new ContextMenu();
            var mAddMod = new MenuItem { Header = loc.Get("edict.effect_add_modifier") };
            mAddMod.Click += (_, _) => InsertAddModifier();
            var mRemMod = new MenuItem { Header = loc.Get("edict.effect_remove_modifier") };
            mRemMod.Click += (_, _) => InsertRemoveModifier();
            var mAddDep = new MenuItem { Header = loc.Get("edict.effect_add_deposit") };
            mAddDep.Click += (_, _) => InsertDeposit(true);
            var mRemDep = new MenuItem { Header = loc.Get("edict.effect_remove_deposit") };
            mRemDep.Click += (_, _) => InsertDeposit(false);
            var mHidden = new MenuItem { Header = loc.Get("edict.effect_hidden_effect") };
            mHidden.Click += (_, _) => InsertHiddenEffect();
            effectRawMenu.Items.Add(mAddMod);
            effectRawMenu.Items.Add(mRemMod);
            effectRawMenu.Items.Add(mAddDep);
            effectRawMenu.Items.Add(mRemDep);
            effectRawMenu.Items.Add(mHidden);   // hidden_effect 放最后
            _effectRawBox.ContextMenu = effectRawMenu;
            editPanel.Children.Add(_effectRawBox);

            // ---- AI 触发权重（显示 ai_weight 块实际内容——有什么显示什么）----
            editPanel.Children.Add(new TextBlock { Text = loc.Get("edict.ai_weight"), Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
            _aiWeightBox = new TextBox
            {
                MinHeight = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 8), FontFamily = new FontFamily("Consolas")
            };
            editPanel.Children.Add(_aiWeightBox);

            // ---- 所属文件（参考科技弹窗：只填文件名，前置相对路径自动隐藏/自动补——用户 2026-08）----
            editPanel.Children.Add(new TextBlock { Text = loc.Get("edict.owner_file"), Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 2) });
            _ownerFileBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
            editPanel.Children.Add(_ownerFileBox);
            _ownerFileBox.LostFocus += (_, _) => SaveOwnerFile();

            // 布局：行0 搜索行（**横跨整页**——保存按钮 + 搜索框填满剩余空间 + 🔍，参考战略资源页）+
            //      行1（左列表 + 右编辑）
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            // 左列表 Star 权重 1（**默认 20%**，用户）、分隔条 Auto、右编辑 Star 权重 4（80%）——拖 Splitter 仍可调
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
            Grid.SetRow(searchRow, 0);
            Grid.SetColumnSpan(searchRow, 3);
            grid.Children.Add(searchRow);
            _leftPanel!.Children.Add(_list);
            Grid.SetRow(_leftPanel, 1);
            Grid.SetColumn(_leftPanel, 0);
            var splitter = new GridSplitter
            {
                Width = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent   // 照抄星系样式（默认 ResizeDirection/Behavior）
            };
            Grid.SetRow(splitter, 1);
            Grid.SetColumn(splitter, 1);
            Grid.SetRow(rightPanel, 1);
            Grid.SetColumn(rightPanel, 2);
            grid.Children.Add(_leftPanel);
            grid.Children.Add(splitter);
            grid.Children.Add(rightPanel);
            Root = grid;

            // 事件（保存索引：输入框变动登记——改哪写哪）
            _keyBox.LostFocus += (_, _) => SaveKey();
            _iconBox.LostFocus += (_, _) => SaveIcon();
            _lengthCombo.SelectionChanged += (_, _) => { OnLengthModeChanged(); Mark(EdictField.Length); };
            _lengthBox.LostFocus += (_, _) => { SaveLength(); Mark(EdictField.Length); };
            _potentialCombo.SelectionChanged += (_, _) => { OnConditionComboChanged(_potentialCombo, _potentialCustomBox); SavePotential(); Mark(EdictField.Potential); };
            _potentialCustomBox.LostFocus += (_, _) => { SavePotential(); Mark(EdictField.Potential); };
            _allowCombo.SelectionChanged += (_, _) => { OnConditionComboChanged(_allowCombo, _allowCustomBox); SaveAllow(); Mark(EdictField.Allow); };
            _allowCustomBox.LostFocus += (_, _) => { SaveAllow(); Mark(EdictField.Allow); };
            _potentialCustomBox.TextChanged += (_, _) => { AutoCustomIfMismatch(_potentialCombo, _potentialCustomBox); Mark(EdictField.Potential); };
            _allowCustomBox.TextChanged += (_, _) => { AutoCustomIfMismatch(_allowCombo, _allowCustomBox); Mark(EdictField.Allow); };
            _aiWeightBox.LostFocus += (_, _) => SaveAiWeight();
            _effectRawBox.LostFocus += (_, _) => SaveEffectRaw();
            _resTabs.Changed += () => { RefreshBuckets(); Mark(EdictField.Resources); };

            RefreshList();
        }

        // ===== UI 辅助 =====

        private static TextBlock SectionTitle(string text) => new()
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 2)
        };

        private static TextBox AddLabeledTextBox(StackPanel panel, string label)
        {
            panel.Children.Add(new TextBlock { Text = label + ":", Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
            var box = new TextBox { Margin = new Thickness(0, 1, 0, 4) };
            panel.Children.Add(box);
            return box;
        }

        /// <summary>label + 输入框同一行（无冒号）——图标等单行字段。</summary>
        private static TextBox AddInlineTextBox(StackPanel panel, string label)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // label 自适应——输入框紧跟，无大间隔
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            panel.Children.Add(row);
            return box;
        }

        private static TextBox AddReadonlyTextBox(StackPanel panel, string label)
        {
            panel.Children.Add(new TextBlock { Text = label + ":", Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
            var box = new TextBox { Margin = new Thickness(0, 1, 0, 4), IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
            box.IsReadOnlyCaretVisible = true;
            panel.Children.Add(box);
            return box;
        }

        private static TextBlock AddReadonlyTextBlock(StackPanel panel, string label)
        {
            panel.Children.Add(new TextBlock { Text = label + ":", Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
            var block = new TextBlock
            {
                Margin = new Thickness(0, 1, 0, 4),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 60,
                Foreground = Brushes.Gray
            };
            panel.Children.Add(block);
            return block;
        }

        // ===== 列表 =====

        private void RefreshList() => RefreshList(_searchBox?.Text);

        private void RefreshList(string? filter)
        {
            var prev = _current?.Key;
            _list.Items.Clear();
            var all = _engine.GetItems(_kind);
            // leftPanel 无静态宽度——填满 Star 列，拖 GridSplitter 时真实变宽/变窄（用户：竖线拖动无效=静态宽度覆盖列宽）
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var f = filter.Trim();
                all = all.Where(i =>
                    i.Key.Contains(f, StringComparison.OrdinalIgnoreCase)
                    || (DisplayName(i)?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            }
            foreach (var item in all)
                _list.Items.Add(new EdictListItem(item, DisplayName(item)));
            if (prev != null)
            {
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    if (_list.Items[i] is EdictListItem li && li.Item.Key == prev)
                    {
                        _list.SelectedIndex = i;
                        return;
                    }
                }
            }
            if (_list.Items.Count > 0)
                _list.SelectedIndex = 0;
            else
                ClearForm();
        }

        private string DisplayName(EdictDecisionItem item)
        {
            if (_services.Adapter == null)
                return item.Key;
            return _engine.LocalisedName(item, _services.Localisation.CurrentLanguage, ModLang) ?? item.Key;
        }

        /// <summary>列表项拉伸样式：每项撑满列表宽（"短时短/宽时宽"根因——ListBoxItem 默认按内容宽）。</summary>
        private static Style StretchListBoxItemStyle()
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            return style;
        }

        private void NewItem()
        {
            var baseKey = _kind == EdictDecisionKind.Edict ? "new_edict" : "new_decision";
            var key = baseKey + "_" + DateTime.Now.ToString("HHmmss");
            var n = 1;
            while (_engine.GetItems(_kind).Any(i => i.Key == key))
                key = baseKey + "_" + DateTime.Now.ToString("HHmmss") + "_" + (++n);
            var ni = _engine.AddItem(_kind, key);
            if (_kind == EdictDecisionKind.Edict)
                ni.Icon = "GFX_edict_type_policy";   // 图标默认填充（用户要求）
            RefreshList();
            for (int i = 0; i < _list.Items.Count; i++)
            {
                if (_list.Items[i] is EdictListItem li && li.Item.Key == key)
                {
                    _list.SelectedIndex = i;
                    break;
                }
            }
        }

        /// <summary>保存（SaveRunner）：只写改动登记的项与字段；成功后清除脏登记并刷新列表。</summary>
        private void SaveAll()
        {
            var modPrefix = _services.ModPrefs?.ModPrefix ?? "smt";
            var engine = _engine;
            SaveRunner.Run(_services, "status.saving",
                () =>
                {
                    var (saved, errors) = engine.SaveAll(modPrefix);
                    if (errors.Count > 0)
                    {
                        return false;
                    }
                    engine.ClearDirty();
                    return true;
                },
                onSuccess: () => RefreshList());
        }

        /// <summary>登记当前项某字段被修改（保存索引——改哪写哪）。</summary>
        private void Mark(EdictField f)
        {
            if (_loading)
                return;   // 加载/刷新表单不登记
            if (_current != null)
                _engine.MarkDirty(_current, f);
        }

        /// <summary>所属文件显示文件名（前置相对路径自动隐藏——用户 2026-08）：SourceRelPath 去目录；
        /// 无（新建项）→ 默认 00_{ModPrefix}_edicts.txt / decisions.txt。</summary>
        private string OwnerFileName(EdictDecisionItem item)
        {
            if (!string.IsNullOrEmpty(item.SourceRelPath))
                return item.SourceRelPath!.Substring(item.SourceRelPath.LastIndexOf('/') + 1);
            var prefix = _services.ModPrefs?.ModPrefix ?? "smt";
            return item.Kind == EdictDecisionKind.Edict
                ? $"00_{prefix}_edicts.txt"
                : $"00_{prefix}_decisions.txt";
        }

        /// <summary>所属文件失焦：文件名 → SourceRelPath（自动补前置目录 common/edicts|decisions/）+ 登记保存。</summary>
        private void SaveOwnerFile()
        {
            if (_loading || _current == null)
                return;
            var name = _ownerFileBox.Text?.Trim() ?? "";
            string? rel = null;
            if (name.Length > 0)
            {
                var dir = _current.Kind == EdictDecisionKind.Edict ? "common/edicts" : "common/decisions";
                rel = dir + "/" + name;
            }
            _current.SourceRelPath = rel;
            _engine.MarkItemDirty(_current);   // 非字段变化：登记条目（保存时写文件）
        }

        private void DeleteSelected()
        {
            if (_list.SelectedItem is not EdictListItem li)
                return;
            _engine.RemoveItem(li.Item);   // 登记式删除（新建项内存删 + 扫描项登记——保存时从文件 AST 移除块，用户 2026-08）
            _current = null;
            RefreshList();
        }

        private void OnItemSelected()
        {
            _current = (_list.SelectedItem as EdictListItem)?.Item;
            if (_current == null)
            {
                ClearForm();
                return;
            }
            _loading = true;   // 加载中：设控件文本不触发保存登记
            try
            {
            _keyBox.Text = _current.Key;
            _locBox.Reload();   // 重置语种下拉——按当前法令条目词条语种 ∪ 模组启用语言（不是全局）
            RefreshLoc();
            _iconBox.Text = _current.Icon;
            _lengthCombo.SelectedIndex = _lengthCombo.Items.Count > 1
                ? (_current.LengthIsInfinite ? 0 : 1)
                : 0;   // 决议仅"有限"一项
            _lengthBox.Text = _current.LengthIsInfinite ? "-1" : _current.LengthValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // 决议：无无限概念——输入框恒可输入（最少 0）
            _lengthBox.IsEnabled = _kind == EdictDecisionKind.Decision || !_current.LengthIsInfinite;
            if (_kind == EdictDecisionKind.Decision && _importantCheck != null)
            {
                _importantCheck.IsChecked = _current.Important;
                _ownedCheck.IsChecked = _current.OwnedPlanetsOnly;
                _enactmentBox.Text = _current.EnactmentTime.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            SetConditionCombo(_potentialCombo, _potentialCustomBox, _current.Potential, _current.PotentialCustom);
            SetConditionCombo(_allowCombo, _allowCustomBox, _current.Allow, _current.AllowCustom);
            _aiWeightBox.Text = _current.AiWeightRaw;
            _effectRawBox.Text = _current.EffectRaw;
            _ownerFileBox.Text = OwnerFileName(_current);   // 所属文件：显示文件名（前缀自动隐藏）
                RefreshEffects();
                RefreshBuckets();
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>按选中语种刷新名称/描述双值（翻译值 + 逻辑值）。</summary>
        private void RefreshLoc()
        {
            if (_current == null || _services.Adapter == null)
                return;
            var lang = ModLang;
            var nameKey = EdictDecisionEngine.LocalisationKey(_current);
            var nameLogical = _services.Adapter.GetLocalisedLogicalText(nameKey, lang) ?? "";
            var nameText = _services.Adapter.GetLocalisedText(nameKey, lang) ?? "";
            _current.NameDisplay = nameText.Length > 0 ? nameText : _current.Key;
            _current.NameLogical = nameLogical.Length > 0 ? nameLogical : _current.NameDisplay;
            var descKey = EdictDecisionEngine.DescKey(_current);
            var descLogical = _services.Adapter.GetLocalisedLogicalText(descKey, lang) ?? "";
            var descText = _services.Adapter.GetLocalisedText(descKey, lang) ?? "";
            _current.DescDisplay = descText.Length > 0 ? descText : "";
            _current.DescLogical = descLogical.Length > 0 ? descLogical : _current.DescDisplay;
            _locBox.Load();
        }

        private void ClearForm()
        {
            _keyBox.Text = "";
            _iconBox.Text = "";
            _locBox.Reload();   // 无当前条目 → 语种下拉只显示模组启用语言
            _lengthCombo.SelectedIndex = 0;
            _lengthBox.Text = _kind == EdictDecisionKind.Decision ? "0" : "-1";
            _lengthBox.IsEnabled = _kind == EdictDecisionKind.Decision;
            if (_kind == EdictDecisionKind.Decision && _importantCheck != null)
            {
                _importantCheck.IsChecked = false;
                _ownedCheck.IsChecked = false;
                _enactmentBox.Text = "0";
            }
            _potentialCustomBox.Text = "";
            _allowCustomBox.Text = "";
            _aiWeightBox.Text = "";
            _effectRawBox.Text = "";
            _ownerFileBox.Text = "";
            RefreshEffects();   // 清加成（决议页 _effectList 为 null——内部已保护）
            _resTabs.Refresh();
        }

        // ===== 输入弹窗（条件区共用；资源表格已组件化——组件内有自己的弹窗） =====

        private bool ShowInput(string title, string initial, out string text)
        {
            var dlg = new Window
            {
                Title = title,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Owner = Window.GetWindow(Root)
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            var box = new TextBox { Width = 300, Text = initial };
            var okBtn = new Button { Content = "确定", Width = 80, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            okBtn.Click += (_, _) => dlg.DialogResult = true;
            panel.Children.Add(box);
            panel.Children.Add(okBtn);
            dlg.Content = panel;
            text = "";
            if (dlg.ShowDialog() != true)
                return false;
            text = box.Text;
            return true;
        }

        private bool ShowInputMultiline(string title, string initial, out string text)
        {
            var dlg = new Window
            {
                Title = title,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Owner = Window.GetWindow(Root)
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            var box = new TextBox { Width = 360, Height = 160, Text = initial, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var okBtn = new Button { Content = "确定", Width = 80, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            okBtn.Click += (_, _) => dlg.DialogResult = true;
            panel.Children.Add(box);
            panel.Children.Add(okBtn);
            dlg.Content = panel;
            text = "";
            if (dlg.ShowDialog() != true)
                return false;
            text = box.Text;
            return true;
        }

        // ===== 表单保存（内存） =====

        private void SaveKey()
        {
            if (_current == null)
                return;
            var text = _keyBox.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                _keyBox.Text = _current.Key;
                return;
            }
            if (text != _current.Key)
            {
                if (_engine.GetItems(_kind).Any(i => i.Key == text && !ReferenceEquals(i, _current)))
                {
                    _keyBox.Text = _current.Key;   // key 冲突——回退
                    return;
                }
                _current.Key = text;
                Mark(EdictField.Key);
                RefreshList();
            }
        }

        private void SaveIcon()
        {
            if (_current == null)
                return;
            _current.Icon = _iconBox.Text?.Trim() ?? "";
            Mark(EdictField.Icon);
        }

        private void OnLengthModeChanged()
        {
            if (_lengthCombo.SelectedItem is not ComboBoxItem it)
                return;
            bool infinite = (bool)it.Tag!;
            _lengthBox.IsEnabled = !infinite;
            if (infinite)
                _lengthBox.Text = "-1";
            else if (_current != null)
                _lengthBox.Text = _current.LengthValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SaveLength();
        }

        private void SaveLength()
        {
            if (_current == null)
                return;
            bool infinite = _lengthCombo.SelectedItem is ComboBoxItem it2 && (bool)it2.Tag!;
            _current.LengthIsInfinite = infinite;
            if (!infinite && int.TryParse(_lengthBox.Text?.Trim(), out var lv))
                _current.LengthValue = lv;
            Mark(EdictField.Length);
        }

        private void SaveAiWeight()
        {
            if (_current == null)
                return;
            _current.AiWeightRaw = _aiWeightBox.Text ?? "";
            Mark(EdictField.AiWeight);
        }

        private void SaveEnactmentTime()
        {
            if (_current == null)
                return;
            if (int.TryParse(_enactmentBox.Text?.Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                _current.EnactmentTime = v;
            Mark(EdictField.EnactmentTime);
        }

        private void SaveEffectRaw()
        {
            if (_current == null)
                return;
            _current.EffectRaw = _effectRawBox.Text ?? "";
            Mark(EdictField.Effect);
        }

        // ===== Effect 右键菜单：自选效果模板，光标处插入（自动换行） =====

        /// <summary>光标处插入文本；模板内 {CURSOR} 标记为插入后光标位置；前后自动补换行。</summary>
        private void InsertEffectText(string text)
        {
            var cursorMark = "{CURSOR}";
            int cursorOffset = text.IndexOf(cursorMark, StringComparison.Ordinal);
            var insert = cursorOffset >= 0 ? text.Replace(cursorMark, "") : text;
            int insertLen = cursorOffset >= 0 ? cursorOffset : insert.Length;
            var before = _effectRawBox.Text ?? "";
            int idx = Math.Clamp(_effectRawBox.CaretIndex, 0, before.Length);
            var prefix = before.Length > 0 && !before.EndsWith("\n") ? "\n" : "";
            var after = before.Substring(idx);
            var suffix = after.Length > 0 && !after.StartsWith("\n") ? "\n" : "";
            _effectRawBox.Text = before.Substring(0, idx) + prefix + insert + suffix + after;
            _effectRawBox.CaretIndex = idx + prefix.Length + insertLen;
            _effectRawBox.Focus();
            Mark(EdictField.Effect);
        }

        /// <summary>add_modifier：选静态加成 → 模板（默认带 days=-1 无限）。</summary>
        private void InsertAddModifier()
        {
            if (PickStaticModifierKey(out var key))
            {
                InsertEffectText($"add_modifier = {{\n    modifier = {key}\n    days = -1\n}}");
            }
        }

        /// <summary>hidden_effect：空块占位，光标留在块内。</summary>
        private void InsertHiddenEffect()
        {
            InsertEffectText("hidden_effect = {\n    {CURSOR}\n}");
        }

        /// <summary>remove_modifier：选静态加成 → 移除模板（Simple：remove_modifier = {key}）。</summary>
        private void InsertRemoveModifier()
        {
            if (PickStaticModifierKey(out var key))
            {
                InsertEffectText($"remove_modifier = {key}");
            }
        }

        /// <summary>add_deposit / remove_deposit：从地形引擎列表选 → 插入 Simple。</summary>
        private void InsertDeposit(bool isAdd)
        {
            if (PickDepositKey(out var key))
            {
                var head = isAdd ? "add_deposit" : "remove_deposit";
                InsertEffectText($"{head} = {key}");
            }
        }

        /// <summary>弹窗选静态加成 key（只列 static 来源的基础加成——不含 scripted 基础、不含自定义）。</summary>
        private bool PickStaticModifierKey(out string key)
        {
            key = "";
            if (_services.StaticModifierEngine == null)
                return false;
            // 静态加成 = static_modifiers 顶层块（引擎 StaticModifierEntry——本地化不带 mod_ 前缀）
            var all = _services.StaticModifierEngine.GetStaticModifiers().ToList();
            if (all.Count == 0)
                return false;
            var picked = "";
            var win = new Window
            {
                Title = _services.Localisation.Get("edict.bonus_pick_title"),
                Width = 460, Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(Root)
            };
            var panel = new DockPanel { Margin = new Thickness(12) };
            var searchBox = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(searchBox, Dock.Top);
            panel.Children.Add(searchBox);
            var list = new ListBox { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(list, Dock.Top);
            panel.Children.Add(list);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = _services.Localisation.Get("edict.ok"), Width = 70, Margin = new Thickness(0, 0, 6, 0) };
            var cancelBtn = new Button { Content = _services.Localisation.Get("edict.cancel"), Width = 70 };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            panel.Children.Add(btnRow);
            win.Content = panel;

            void Reload(string? kw)
            {
                list.Items.Clear();
                foreach (var ce in all)
                {
                    if (!string.IsNullOrEmpty(kw) && !ce.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
                        && !ce.Localisations.Values.Any(v => v.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var loc2 = ce.Localisations.TryGetValue(ModLang, out var l) ? l
                        : ce.Localisations.TryGetValue("english", out var le) ? le : "";
                    var item = new ListBoxItem
                    {
                        Content = string.IsNullOrEmpty(loc2) ? ce.Name : ce.Name + "  " + loc2,
                        Tag = ce.Name
                    };
                    list.Items.Add(item);
                }
            }
            Reload("");
            searchBox.TextChanged += (_, _) => Reload(searchBox.Text);
            okBtn.Click += (_, _) => { if (list.SelectedItem is ListBoxItem it) { picked = it.Tag as string ?? ""; win.DialogResult = true; } };
            list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is ListBoxItem it) { picked = it.Tag as string ?? ""; win.DialogResult = true; } };
            searchBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && list.SelectedItem is ListBoxItem it) { picked = it.Tag as string ?? ""; win.DialogResult = true; } };
            cancelBtn.Click += (_, _) => win.DialogResult = false;
            if (win.ShowDialog() == true && !string.IsNullOrEmpty(picked))
            {
                key = picked;
                return true;
            }
            return false;
        }

        /// <summary>弹窗选 deposit（搜索 key + 本地化）。</summary>
        private bool PickDepositKey(out string key)
        {
            key = "";
            var all = _depositEngine.GetDeposits().ToList();
            if (all.Count == 0)
                return false;
            var picked = "";
            var win = new Window
            {
                Title = _services.Localisation.Get("edict.deposit_pick_title"),
                Width = 480, Height = 430,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(Root)
            };
            var panel = new DockPanel { Margin = new Thickness(12) };
            var searchBox = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(searchBox, Dock.Top);
            panel.Children.Add(searchBox);
            var list = new ListBox { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(list, Dock.Top);
            panel.Children.Add(list);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = _services.Localisation.Get("edict.ok"), Width = 70, Margin = new Thickness(0, 0, 6, 0) };
            var cancelBtn = new Button { Content = _services.Localisation.Get("edict.cancel"), Width = 70 };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            panel.Children.Add(btnRow);
            win.Content = panel;

            void Reload(string? kw)
            {
                list.Items.Clear();
                foreach (var d in all)
                {
                    if (!string.IsNullOrEmpty(kw) && !d.Key.Contains(kw, StringComparison.OrdinalIgnoreCase)
                        && !d.LocName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var item = new ListBoxItem
                    {
                        Content = string.IsNullOrEmpty(d.LocName) ? d.Key : d.Key + "  " + d.LocName,
                        Tag = d.Key
                    };
                    list.Items.Add(item);
                }
            }
            Reload("");
            searchBox.TextChanged += (_, _) => Reload(searchBox.Text);
            okBtn.Click += (_, _) => { if (list.SelectedItem is ListBoxItem it) { picked = it.Tag as string ?? ""; win.DialogResult = true; } };
            list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is ListBoxItem it) { picked = it.Tag as string ?? ""; win.DialogResult = true; } };
            searchBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && list.SelectedItem is ListBoxItem it) { picked = it.Tag as string ?? ""; win.DialogResult = true; } };
            cancelBtn.Click += (_, _) => win.DialogResult = false;
            if (win.ShowDialog() == true && !string.IsNullOrEmpty(picked))
            {
                key = picked;
                return true;
            }
            return false;
        }

        // ===== 加成（modifier）：3 列表格（键 / 本地化 / 数值）+ 右键 添加/删除/设置 =====

        private string BonusLoc(string key)
        {
            // 加成键本地化：base/custom 条目按当前语种取；无 → 空
            var loc = "";
            var engine = _services.StaticModifierEngine;
            if (engine != null)
            {
                var b = engine.GetBaseModifier(key);
                if (b != null && b.Localisations.TryGetValue(ModLang, out var v)) loc = v;
                else
                {
                    var c = engine.GetCustom(key);
                    if (c != null && c.Localisations.TryGetValue(ModLang, out var v2)) loc = v2;
                }
            }
            return loc;
        }

        private void RefreshEffects()
        {
            if (_effectList == null)
                return;   // 决议页无加成栏（Build 未创建 _effectList）
            _effectList.Items.Clear();
            _bonusRows.Clear();
            if (_current == null)
                return;
            foreach (var (b, v) in _current.Effects)
            {
                var row = new BonusRowVm
                {
                    Key = b,
                    Loc = BonusLoc(b),
                    ValueText = v.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
                _bonusRows.Add(row);
                _effectList.Items.Add(row);
            }
        }

        /// <summary>复制全部加成到剪贴板：`key:当前语种翻译 = 数值` 每行一条。</summary>
        private void CopyBonuses()
        {
            if (_bonusRows.Count == 0)
                return;
            var text = string.Join("\r\n", _bonusRows.Select(r => r.Key + ":" + r.Loc + " = " + r.ValueText));

            System.Windows.Clipboard.SetText(text);
        }

        private void DeleteSelectedEffect()
        {
            if (_current == null || _effectList.SelectedItem is not BonusRowVm row)
                return;
            var idx = _current.Effects.FindIndex(e => e.Base == row.Key && Math.Abs(e.Value - double.Parse(row.ValueText, System.Globalization.CultureInfo.InvariantCulture)) < 1e-9);
            if (idx >= 0)
            {
                _current.Effects.RemoveAt(idx);
                Mark(EdictField.Bonuses);
                RefreshEffects();
            }
        }

        // ===== 加成弹窗（添加/设置共用）：顶部 输入框 | 数值 一行；下方 2 列选项（键 + 本地化） =====

        /// <summary>添加加成：选 key（输入实时过滤）+ 数值 → 新行。</summary>
        private void ShowAddBonusDialog()
        {
            var picked = ShowBonusPickerDialog(out var key, out var value, initialKey: "", initialValue: 1);
            if (!picked || _current == null)
                return;
            _current.Effects.Add((key, value));
            Mark(EdictField.Bonuses);
            RefreshEffects();
        }

        /// <summary>设置加成：改当前行的数值，或切换 key（从选项重选）。</summary>
        private void ShowSetBonusDialog()
        {
            if (_current == null || _effectList.SelectedItem is not BonusRowVm row)
                return;
            var picked = ShowBonusPickerDialog(out var key, out var value, initialKey: row.Key, initialValue: double.Parse(row.ValueText, System.Globalization.CultureInfo.InvariantCulture));
            if (!picked)
                return;
            var idx = _current.Effects.FindIndex(e => e.Base == row.Key);
            if (idx >= 0)
                _current.Effects[idx] = (key, value);
            Mark(EdictField.Bonuses);
            RefreshEffects();
        }

        private bool ShowBonusPickerDialog(out string key, out double value, string initialKey, double initialValue)
        {
            key = "";
            value = initialValue;
            var pickedKey = "";
            var pickedValue = initialValue;
            var win = new Window
            {
                Title = _services.Localisation.Get("edict.bonus_pick_title"),
                Width = 460, Height = 430,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(Root)
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
                Text = initialValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
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
                Binding = new System.Windows.Data.Binding("Key"),
                Width = new DataGridLength(35, DataGridLengthUnitType.Star)
            });
            resultGrid.Columns.Add(new DataGridTextColumn
            {
                Header = _services.Localisation.Get("edict.bonus_loc"),
                Binding = new System.Windows.Data.Binding("Loc"),
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
                    resultGrid.Items.Add(new BonusRowVm { Key = name, Loc = BonusLoc(name), ValueText = "" });
                }
                // 初始 key 已填 → 默认可确定（设置场景保留原 key）
                okBtn.IsEnabled = initialKey.Length > 0 && resultGrid.SelectedItem is null;
            }
            searchBox.TextChanged += (_, _) => Refresh();
            resultGrid.SelectionChanged += (_, _) => okBtn.IsEnabled = resultGrid.SelectedItem is BonusRowVm;
            resultGrid.MouseDoubleClick += (_, _) =>
            {
                if (okBtn.IsEnabled) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            okBtn.Click += (_, _) =>
            {
                var pickedName = (resultGrid.SelectedItem as BonusRowVm)?.Key
                    ?? (initialKey.Length > 0 ? searchBox.Text?.Trim() : "");
                if (string.IsNullOrEmpty(pickedName))
                    return;
                pickedKey = pickedName;
                if (!double.TryParse(valueBox.Text?.Trim(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out pickedValue))
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

        // ===== 资源消耗：3 个表格（启动消耗 / 每月消耗 / 每月产出）——行 = 资源，同组行倍率/条件相同 =====

        private void RefreshBuckets()
        {
            if (_current == null)
                return;
            _resTabs.Cost = _current.Cost;
            _resTabs.Upkeep = _current.Upkeep;
            _resTabs.Produces = _current.Produces;
            _resTabs.Refresh();
        }

        // ===== 条件（potential / allow）：预设下拉 + 自定义文本 =====

        private ComboBox BuildConditionRow(StackPanel panel, string label, out TextBox customBox)
        {
            // label + 下拉同一行（水平 Grid）；自定义条件框在下方（多行）
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210, GridUnitType.Pixel) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            var combo = new ComboBox { Width = 150, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            combo.Items.Add(new ComboBoxItem { Content = _services.Localisation.Get("edict.cond_always_yes"), Tag = ConditionPreset.AlwaysYes });
            combo.Items.Add(new ComboBoxItem { Content = _services.Localisation.Get("edict.cond_always_no"), Tag = ConditionPreset.AlwaysNo });
            combo.Items.Add(new ComboBoxItem { Content = _services.Localisation.Get("edict.cond_ai_yes"), Tag = ConditionPreset.AiYes });
            combo.Items.Add(new ComboBoxItem { Content = _services.Localisation.Get("edict.cond_ai_no"), Tag = ConditionPreset.AiNo });
            combo.Items.Add(new ComboBoxItem { Content = _services.Localisation.Get("edict.cond_custom"), Tag = ConditionPreset.Custom });
            combo.SelectedIndex = 0;
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            panel.Children.Add(row);
            customBox = new TextBox { MinHeight = 40, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, IsEnabled = false, Margin = new Thickness(0, 2, 0, 6) };
            panel.Children.Add(customBox);
            return combo;
        }

        private static void SetConditionCombo(ComboBox combo, TextBox customBox, ConditionPreset preset, string custom)
        {
            customBox.Text = custom;
            combo.SelectedIndex = (int)preset;
            // 哪怕符合预设也显示原文；始终可编辑（编辑后不符预设由 TextChanged 自动切自定义）
            customBox.IsEnabled = true;
        }

        private static void OnConditionComboChanged(ComboBox combo, TextBox customBox)
        {
            // 选预设 → 输入框同步标准文本（选"仅限玩家可见" → is_ai = no）
            if (combo.SelectedItem is not ComboBoxItem it)
                return;
            switch ((ConditionPreset)it.Tag!)
            {
                case ConditionPreset.AlwaysYes:
                    customBox.Text = "";   // 无限制 → 清空
                    break;
                case ConditionPreset.AiYes:
                    customBox.Text = "is_ai = yes";
                    break;
                case ConditionPreset.AiNo:
                    customBox.Text = "is_ai = no";
                    break;
                case ConditionPreset.Custom:
                    break;   // 保留用户文本
            }
        }

        /// <summary>编辑条件框后：文本不再匹配当前预设 → 下拉自动切自定义（切换触发 SelectionChanged，不动文本，无循环）。</summary>
        private static void AutoCustomIfMismatch(ComboBox combo, TextBox customBox)
        {
            if (combo.SelectedItem is not ComboBoxItem it)
                return;
            var current = (ConditionPreset)it.Tag!;
            if (current == ConditionPreset.Custom)
                return;   // 已是自定义——不动
            if (EdictDecisionEngine.ClassifyCondition(customBox.Text ?? "") != current)
                combo.SelectedIndex = (int)ConditionPreset.Custom;
        }

        private void SavePotential()
        {
            if (_current == null || _potentialCombo.SelectedItem is not ComboBoxItem it)
                return;
            _current.Potential = (ConditionPreset)it.Tag!;
            _current.PotentialCustom = _potentialCustomBox.Text ?? "";
        }

        private void SaveAllow()
        {
            if (_current == null || _allowCombo.SelectedItem is not ComboBoxItem it)
                return;
            _current.Allow = (ConditionPreset)it.Tag!;
            _current.AllowCustom = _allowCustomBox.Text ?? "";
        }
    }

    private sealed class EdictListItem
    {
        public EdictDecisionItem Item { get; }
        public string Display { get; }
        public EdictListItem(EdictDecisionItem item, string display)
        {
            Item = item;
            Display = display;
        }
        public override string ToString() => Display;
    }

    /// <summary>加成表格行：键 / 本地化 / 数值。</summary>
    private sealed class BonusRowVm
    {
        public string Key { get; set; } = "";
        public string Loc { get; set; } = "";
        public string ValueText { get; set; } = "";
    }
}
