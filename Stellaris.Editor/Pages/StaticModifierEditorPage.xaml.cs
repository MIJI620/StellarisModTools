// 文件: Stellaris.Editor/Pages/StaticModifierEditorPage.xaml.cs
// 静态加成编辑器（**本期内存编辑不落盘**——用户 2026-08，V0.2 范围）：
// 抄法令页结构——左列表 + 右表单（key → 本地化 → icon → 底部加成信息表格）。
// 数据源 = StaticModifierEngine.GetItems()（扫描 common/static_modifiers 顶层块 + 内存新建）。
// 静态加成本地化键**不带 mod_ 前缀**（用户概念：名称 = {key}、描述 = {key}_desc）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Stellaris.Engine.StaticModifier;
using StaticEntry = Stellaris.Engine.StaticModifier.StaticModifierEngine.StaticModifierEntry;

namespace Stellaris.Editor.Pages;

public sealed class StaticModifierEditorPage : UserControl
{
    private readonly EngineServices _services;
    private readonly StaticModifierEngine _engine;
    private readonly UILocalisationManager _loc;

    private StaticEntry? _current;
    private bool _loading;   // 加载/刷新表单时抑制编辑登记

    private ListBox _list = null!;
    private TextBox _searchBox = null!;
    private TextBox _keyBox = null!, _iconBox = null!;
    private CheckBox _hiddenBox = null!, _importantBox = null!, _showOnlyBox = null!;   // 是否隐藏/是否重要/只显示自定义提示
    private TextBox _iconFrameBox = null!, _customTooltipBox = null!;                   // 边框类型/自定义提示（本地化键）
    private TextBox _sourceFileBox = null!;                                            // 所属文件（文件名；前缀自动隐藏——用户 2026-08）
    private Controls.LocalisationEditBox _locBox = null!, _tooltipLocBox = null!;
    private DataGrid _bonusGrid = null!;
    private readonly List<BonusRow> _bonusRows = new();   // 加成行（内存编辑，不落盘）
    private readonly Dictionary<string, string> _iconEdits = new(StringComparer.OrdinalIgnoreCase);   // key → icon（内存编辑）

    /// <summary>加成表格行：键 / 本地化 / 数值。</summary>
    private sealed class BonusRow
    {
        public string Key { get; set; } = "";
        public string Loc { get; set; } = "";
        public string ValueText { get; set; } = "";
    }

    public StaticModifierEditorPage(EngineServices services)
    {
        _services = services;
        _engine = services.StaticModifierEngine!;
        _loc = services.Localisation;
        Build();
    }

    private string ModLang => _loc.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        ? "simp_chinese" : "english";

    private void Build()
    {
        // ===== 顶部搜索行（横跨整页——参考法令/资源页：保存按钮预留 + 搜索框 Star + 🔍） =====
        var searchRow = new Grid { Margin = new Thickness(8, 6, 8, 4) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var saveBtn = new Button
        {
            Content = _loc.Get("edict.save"),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = true   // 保存（用户 2026-08：参考法令——待保存索引 + SaveRunner，用户触发才落盘）
        };
        saveBtn.Click += (_, _) => SaveAll();
        Grid.SetColumn(saveBtn, 0);
        searchRow.Children.Add(saveBtn);
        _searchBox = new TextBox { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Stellaris.Editor.Controls.SearchDebouncer.Attach(_searchBox, RefreshList);
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; RefreshList(); }
        };
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

        // ===== 左列：列表（右键菜单：新建/删除——删除仅新建项，不落盘） =====
        var leftPanel = new DockPanel { Margin = new Thickness(8, 0, 4, 4) };
        _list = new ListBox { MinWidth = 200 };
        var listMenu = new ContextMenu();
        var newItem = new MenuItem { Header = _loc.Get("edict.new") ?? "新建" };
        newItem.Click += (_, _) => NewItem();
        listMenu.Items.Add(newItem);
        var delItem = new MenuItem { Header = _loc.Get("edict.delete") };
        delItem.Click += (_, _) => DeleteSelected();
        listMenu.Items.Add(delItem);
        _list.ContextMenu = listMenu;
        _list.SelectionChanged += (_, _) => OnItemSelected();
        DockPanel.SetDock(_list, Dock.Bottom);
        leftPanel.Children.Add(_list);

        // ===== 右编辑表单（抄法令：ScrollViewer + StackPanel，DockPanel 容器最后子元素 Fill） =====
        var editScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(4, 8, 8, 4) };
        var editPanel = new StackPanel { Margin = new Thickness(8) };
        editScroll.Content = editPanel;
        var rightPanel = new DockPanel();
        rightPanel.Children.Add(editScroll);

        // ---- key（新建可改；修改只读）+ 本地化 + icon ----
        editPanel.Children.Add(SectionTitle(_loc.Get("edict.basic")));
        _keyBox = AddLabeledTextBox(editPanel, _loc.Get("edict.key"));
        _locBox = new Controls.LocalisationEditBox
        {
            Adapter = _services.Adapter,
            GetNameKey = () => _current?.Name ?? "",
            GetDescKey = () => (_current?.Name ?? "") + "_desc",
            GetLangs = () =>
            {
                // 默认多语种（已加载全部语种）；语种不足（未加载任何）时才仅显示设定模组语种（用户 2026-08）
                var loaded = _services.Adapter?.GetLocalisationLanguages() ?? new List<string>();
                if (loaded.Count > 0)
                    return loaded;
                return _services.ModPrefs?.EnabledLanguages?.Count > 0
                    ? _services.ModPrefs.EnabledLanguages
                    : loaded;
            }
            // SaveLocalisation 缺省 = adapter.UpdateLocalisationEntry（只写本地化内存，不落盘——本期静态加成不落盘）
        };
        _locBox.Reload();
        editPanel.Children.Add(_locBox);
        // icon：标签与输入框**同一行**（用户 2026-08——不占两行）
        var iconRow = new Grid();
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        iconRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var iconLabel = new TextBlock
        {
            Text = _loc.Get("edict.icon") + ":",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 6, 2)
        };
        Grid.SetColumn(iconLabel, 0);
        iconRow.Children.Add(iconLabel);
        _iconBox = new TextBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        Grid.SetColumn(_iconBox, 1);
        iconRow.Children.Add(_iconBox);
        editPanel.Children.Add(iconRow);
        _iconBox.LostFocus += (_, _) => SaveIcon();

        // ---- 静态加成特殊字段（用户 2026-08，内存编辑不落盘）----
        // 一行：hide_from_country_list（是否隐藏）+ important（是否重要）+ icon_frame（边框类型）——放图标下面
        var metaRow1 = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        metaRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _hiddenBox = new CheckBox { Content = _loc.Get("edict.static_hide"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        metaRow1.Children.Add(_hiddenBox);
        _importantBox = new CheckBox { Content = _loc.Get("edict.static_important"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(_importantBox, 1);
        metaRow1.Children.Add(_importantBox);
        var frameLabel = new TextBlock { Text = _loc.Get("edict.static_icon_frame") + ":", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(frameLabel, 2);
        metaRow1.Children.Add(frameLabel);
        _iconFrameBox = new TextBox { Text = "0", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_iconFrameBox, 3);
        metaRow1.Children.Add(_iconFrameBox);
        editPanel.Children.Add(metaRow1);
        _hiddenBox.Checked += (_, _) => SaveMeta();
        _hiddenBox.Unchecked += (_, _) => SaveMeta();
        _importantBox.Checked += (_, _) => SaveMeta();
        _importantBox.Unchecked += (_, _) => SaveMeta();
        _iconFrameBox.LostFocus += (_, _) => SaveIconFrame();

        // 一行：show_only_custom_tooltip（只显示自定义提示）+ custom_tooltip（自定义提示 = 本地化键）
        var metaRow2 = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        metaRow2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaRow2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaRow2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _showOnlyBox = new CheckBox { Content = _loc.Get("edict.static_show_only_tooltip"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        metaRow2.Children.Add(_showOnlyBox);
        var tooltipLabel = new TextBlock { Text = _loc.Get("edict.static_custom_tooltip") + ":", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(tooltipLabel, 1);
        metaRow2.Children.Add(tooltipLabel);
        _customTooltipBox = new TextBox { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_customTooltipBox, 2);
        metaRow2.Children.Add(_customTooltipBox);
        editPanel.Children.Add(metaRow2);
        _showOnlyBox.Checked += (_, _) => SaveMeta();
        _showOnlyBox.Unchecked += (_, _) => SaveMeta();
        _customTooltipBox.LostFocus += (_, _) => SaveCustomTooltip();

        // 本地化组件（只有名称一对——无描述；名称键 = custom_tooltip 的值，用户 2026-08）
        _tooltipLocBox = new Controls.LocalisationEditBox
        {
            Adapter = _services.Adapter,
            GetNameKey = () => _current?.CustomTooltip ?? "",
            GetDescKey = () => (_current?.CustomTooltip ?? "") + "_desc",
            ShowDescription = false,   // 只有第一对（名称）
            GetLangs = () =>
            {
                var loaded = _services.Adapter?.GetLocalisationLanguages() ?? new List<string>();
                if (loaded.Count > 0)
                    return loaded;
                return _services.ModPrefs?.EnabledLanguages?.Count > 0
                    ? _services.ModPrefs.EnabledLanguages
                    : loaded;
            }
            // SaveLocalisation 缺省 = adapter.UpdateLocalisationEntry（只写本地化内存，不落盘）
        };
        _tooltipLocBox.Reload();
        editPanel.Children.Add(_tooltipLocBox);

        // ---- 底部：加成信息（3 列：键 / 本地化 / 数值；右键菜单 添加/删除/设置——照抄法令） ----
        editPanel.Children.Add(SectionTitle(_loc.Get("edict.bonuses")));
        _bonusGrid = new DataGrid
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
        _bonusGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        {
            Header = _loc.Get("edict.bonus_key"),
            Binding = new System.Windows.Data.Binding("Key"),
            Width = new DataGridLength(30, DataGridLengthUnitType.Star)
        });
        _bonusGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        {
            Header = _loc.Get("edict.bonus_loc"),
            Binding = new System.Windows.Data.Binding("Loc"),
            Width = new DataGridLength(45, DataGridLengthUnitType.Star)
        });
        _bonusGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        {
            Header = _loc.Get("edict.bonus_value"),
            Binding = new System.Windows.Data.Binding("ValueText"),
            Width = new DataGridLength(25, DataGridLengthUnitType.Star)
        });
        var bonusMenu = new ContextMenu();
        var addBonus = new MenuItem { Header = _loc.Get("edict.bonus_add") };
        addBonus.Click += (_, _) => ShowAddBonusDialog();
        var setBonus = new MenuItem { Header = _loc.Get("edict.bonus_set") };
        setBonus.Click += (_, _) => ShowSetBonusDialog();
        var delBonus = new MenuItem { Header = _loc.Get("edict.bonus_delete") };
        delBonus.Click += (_, _) => { if (_bonusGrid.SelectedItem is BonusRow r) { _bonusRows.Remove(r); _bonusGrid.Items.Refresh(); SaveRefs(); } };
        bonusMenu.Items.Add(addBonus);
        bonusMenu.Items.Add(setBonus);
        bonusMenu.Items.Add(delBonus);
        _bonusGrid.ContextMenu = bonusMenu;
        _bonusGrid.ItemsSource = _bonusRows;
        // 单元格直接编辑（双击改数值/键——用户 2026-08：-10000 保存变 1 根因）→ 读**编辑控件当前文本**
        // （CellEditEnding 触发时绑定源可能未提交，读 _bonusRows 会拿到旧值）→ 显式更新行 + 登记
        _bonusGrid.CellEditEnding += (_, e) =>
        {
            if (_loading || _current == null || e.EditAction != DataGridEditAction.Commit)
                return;
            if (e.EditingElement is TextBox tb && e.Row.Item is BonusRow row)
            {
                var path = (e.Column as DataGridTextColumn)?.Binding is System.Windows.Data.Binding b ? b.Path?.Path ?? "" : "";
                if (path == "ValueText") row.ValueText = tb.Text;
                else if (path == "Key") row.Key = tb.Text;
            }
            SaveRefs();
        };
        editPanel.Children.Add(_bonusGrid);

        // ---- 所属文件（参考法令/科技：只填文件名，前置 common/static_modifiers/ 自动隐藏——用户 2026-08；
        // 本期静态加成不落盘，仅内存记录）----
        editPanel.Children.Add(new TextBlock { Text = _loc.Get("edict.owner_file"), Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 2) });
        _sourceFileBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        editPanel.Children.Add(_sourceFileBox);
        _sourceFileBox.LostFocus += (_, _) => SaveSourceFile();

        // ===== 外层：搜索行（行 0 横跨整页）+ 左列表（Star）+ GridSplitter + 右侧（Star） =====
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // 左列表 Star 权重 1（**默认 20%**，与法令页一致）、分隔条 Auto、右编辑 Star 权重 4（80%）——拖 Splitter 仍可调（用户 2026-08）
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        Grid.SetRow(searchRow, 0);
        Grid.SetColumnSpan(searchRow, 3);
        grid.Children.Add(searchRow);
        Grid.SetRow(leftPanel, 1);
        Grid.SetColumn(leftPanel, 0);
        grid.Children.Add(leftPanel);
        var splitter = new GridSplitter
        {
            Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Background = Brushes.Transparent
        };
        Grid.SetRow(splitter, 1);
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        Grid.SetRow(rightPanel, 1);
        Grid.SetColumn(rightPanel, 2);
        grid.Children.Add(rightPanel);
        Content = grid;

        RefreshList();
    }

    // ==================== 列表 ====================

    private void RefreshList()
    {
        var q = _searchBox?.Text?.Trim() ?? "";
        var items = _engine.GetItems();
        if (q.Length > 0)
        {
            items = items.Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (e.Localisations.TryGetValue(ModLang, out var lv) && lv.Contains(q, StringComparison.OrdinalIgnoreCase))
                || (e.Localisations.TryGetValue("english", out var le) && le.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        _list.Items.Clear();
        foreach (var e in items)
            _list.Items.Add(new ListBoxItem { Content = DisplayName(e), Tag = e });
        // 默认选中第一项（首次切页/删除/搜索后无选中时回落——不显示空表单，用户 2026-08）
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0)
            _list.SelectedIndex = 0;
    }

    /// <summary>条目显示名：用户语种本地化 → english → 回退 key（默认按用户语种显示；实时读 SA 本地化表——编辑后立即刷新）。</summary>
    private string DisplayName(StaticEntry e)
    {
        var t = _services.Adapter?.GetLocalisedText(e.Name, ModLang);
        if (!string.IsNullOrWhiteSpace(t))
            return t;
        var te = _services.Adapter?.GetLocalisedText(e.Name, "english");
        if (!string.IsNullOrWhiteSpace(te))
            return te;
        return e.Name;
    }

    private void OnItemSelected()
    {
        _current = (_list.SelectedItem as ListBoxItem)?.Tag as StaticEntry;
        LoadForm(_current);
    }

    // ==================== 表单加载/保存（内存） ====================

    private void LoadForm(StaticEntry? entry)
    {
        _loading = true;
        if (entry == null)
        {
            _keyBox.Text = "";
            _iconBox.Text = "";
            _hiddenBox.IsChecked = false;
            _importantBox.IsChecked = false;
            _iconFrameBox.Text = "0";
            _showOnlyBox.IsChecked = false;
            _customTooltipBox.Text = "";
            _sourceFileBox.Text = "";
            _bonusRows.Clear();
            _bonusGrid.Items.Refresh();
            _loading = false;
            return;
        }
        _keyBox.Text = entry.Name;
        _keyBox.IsReadOnly = true;   // 修改只读（新建时放开）
        _iconBox.Text = _iconEdits.TryGetValue(entry.Name, out var ie) ? ie : (entry.Icon ?? "");
        _hiddenBox.IsChecked = entry.Hidden;
        _importantBox.IsChecked = entry.Important;
        _iconFrameBox.Text = entry.IconFrame.ToString();
        _showOnlyBox.IsChecked = entry.ShowOnlyCustomTooltip;
        _customTooltipBox.Text = entry.CustomTooltip ?? "";
        _sourceFileBox.Text = OwnerFileName(entry);   // 所属文件：显示文件名（前缀自动隐藏）
        _bonusRows.Clear();
        foreach (var br in entry.BaseRefs)
            _bonusRows.Add(new BonusRow
            {
                Key = br.Key,
                Loc = BonusLoc(br),
                ValueText = br.Value
            });
        _bonusGrid.Items.Refresh();
        _locBox.Reload();   // 选中条目后重新加载本地化框（构造时 NameKey 为空 Load 早退——切语种才显示 bug 根因，用户 2026-08）
        _tooltipLocBox.Reload();   // custom_tooltip 键变化 → 本地化组件按新键加载
        _loading = false;
    }

    /// <summary>基础本地化（当前语言 → english → 原键）。</summary>
    private string BonusLoc(StaticEntry.BaseRef br)
    {
        if (br.Base != null && br.Base.Localisations.TryGetValue(ModLang, out var v))
            return v;
        if (br.Base != null && br.Base.Localisations.TryGetValue("english", out var ve))
            return ve;
        return br.Key;
    }

    private void SaveIcon()
    {
        if (_loading || _current == null)
            return;
        _engine.UpdateItemIcon(_current, _iconBox.Text?.Trim() ?? "");
        _iconEdits[_current.Name] = _iconBox.Text?.Trim() ?? "";
        _engine.MarkDirty(_current, StaticModifierEngine.StaticField.Icon);
    }

    /// <summary>勾选类特殊字段（隐藏/重要/只显示自定义提示）→ 内存更新 + 登记。</summary>
    private void SaveMeta()
    {
        if (_loading || _current == null)
            return;
        _engine.UpdateItemMeta(_current,
            hidden: _hiddenBox.IsChecked == true,
            important: _importantBox.IsChecked == true,
            showOnlyTooltip: _showOnlyBox.IsChecked == true);
        _engine.MarkDirty(_current, StaticModifierEngine.StaticField.Hidden);
        _engine.MarkDirty(_current, StaticModifierEngine.StaticField.Important);
        _engine.MarkDirty(_current, StaticModifierEngine.StaticField.ShowOnly);
    }

    /// <summary>icon_frame（边框类型，数字）失焦 → 内存更新 + 登记。</summary>
    private void SaveIconFrame()
    {
        if (_loading || _current == null)
            return;
        if (int.TryParse(_iconFrameBox.Text?.Trim(), out var frame))
        {
            _engine.UpdateItemMeta(_current, iconFrame: frame);
            _engine.MarkDirty(_current, StaticModifierEngine.StaticField.IconFrame);
        }
    }

    /// <summary>custom_tooltip（自定义提示 = 本地化键）失焦 → 内存更新 + 登记 + 本地化组件按新键重载。</summary>
    private void SaveCustomTooltip()
    {
        if (_loading || _current == null)
            return;
        _engine.UpdateItemMeta(_current, customTooltip: _customTooltipBox.Text);
        _engine.MarkDirty(_current, StaticModifierEngine.StaticField.CustomTooltip);
        _tooltipLocBox.Reload();   // 键变化 → 本地化组件显示新键词条
    }

    /// <summary>所属文件显示文件名（前置 common/static_modifiers/ 自动隐藏——用户 2026-08）；
    /// 无（新建项）→ 默认 00_{ModPrefix}_static_modifiers.txt。</summary>
    private string OwnerFileName(StaticEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.SourceFile))
            return entry.SourceFile!.Substring(entry.SourceFile.LastIndexOf('/') + 1);
        var prefix = _services.ModPrefs?.ModPrefix ?? "smt";
        return $"00_{prefix}_static_modifiers.txt";
    }

    /// <summary>所属文件失焦：文件名 → SourceFile（自动补 common/static_modifiers/ 前缀）+ 登记保存。</summary>
    private void SaveSourceFile()
    {
        if (_loading || _current == null)
            return;
        var name = _sourceFileBox.Text?.Trim() ?? "";
        _engine.UpdateItemSourceFile(_current,
            name.Length > 0 ? "common/static_modifiers/" + name : null);
        _engine.MarkItemDirty(_current);   // 非字段变化：登记条目（保存时写文件）
    }

    /// <summary>保存（SaveRunner——参考法令：用户显式触发才落盘；写登记的全部文件 + 本地化）。</summary>
    private void SaveAll()
    {
        var modPrefix = _services.ModPrefs?.ModPrefix ?? "smt";
        var engine = _engine;
        if (!engine.HasDirty)
            return;
        SaveRunner.Run(_services, "status.saving",
            () =>
            {
                var (saved, errors) = engine.SaveAll(modPrefix);
                return errors.Count == 0;
            },
            onSuccess: () => RefreshList());
    }

    // ==================== 新建 / 删除 ====================

    private void NewItem()
    {
        var key = PromptKey(_loc.Get("edict.new"), "");
        if (string.IsNullOrWhiteSpace(key))
            return;
        if (_engine.AddItem(key) == null)
        {
            MessageBox.Show(_loc.Get("tech.edit_err_key_dup"), "Stellaris Mod Tools",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        RefreshList();
        // 选中新条目并放开 key 编辑
        foreach (object o in _list.Items)
        {
            if (o is ListBoxItem li && li.Tag is StaticEntry e
                && string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                _list.SelectedItem = li;
                _keyBox.IsReadOnly = false;
                _keyBox.Focus();
                break;
            }
        }
    }

    private void DeleteSelected()
    {
        if (_current == null)
            return;
        _engine.RemoveItem(_current);   // 登记式删除（保存时从文件 AST 移除块 + 删本地化词条——用户 2026-08）
        _current = null;
        RefreshList();
        LoadForm(null);
    }

    private string PromptKey(string title, string initial)
    {
        var win = new Window
        {
            Title = title, Width = 360, Height = 130,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this)
        };
        var box = new TextBox { Text = initial, Margin = new Thickness(12, 18, 12, 6) };
        var ok = new Button { Content = _loc.Get("edict.ok") ?? "确定", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = _loc.Get("edict.cancel") ?? "取消", Width = 80, IsCancel = true };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 12) };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        var panel = new DockPanel();
        DockPanel.SetDock(box, Dock.Top);
        DockPanel.SetDock(row, Dock.Bottom);
        panel.Children.Add(box);
        panel.Children.Add(row);
        win.Content = panel;
        ok.Click += (_, _) => { win.DialogResult = true; };
        return win.ShowDialog() == true ? box.Text.Trim() : "";
    }

    /// <summary>加成表格 → 条目引用键表（登记 Ref 字段，保存时写回）。</summary>
    private void SaveRefs()
    {
        if (_loading || _current == null)
            return;
        _engine.SetEntryRefs(_current, _bonusRows.Select(r => (r.Key, r.ValueText)));
    }

    // ==================== 加成表格（照抄法令：选择弹窗 输入过滤 + 数值） ====================

    private void ShowAddBonusDialog()
    {
        if (_current == null)
            return;
        var picked = ShowBonusPickerDialog(out var key, out var value, "", "1");
        if (!picked)
            return;
        _bonusRows.RemoveAll(r => string.Equals(r.Key, key, StringComparison.Ordinal));
        var bm = _engine.GetBaseModifier(key);
        _bonusRows.Add(new BonusRow
        {
            Key = key,
            Loc = bm != null && bm.Localisations.TryGetValue(ModLang, out var lv) ? lv : key,
            ValueText = value
        });
        _bonusGrid.Items.Refresh();
        SaveRefs();
    }

    private void ShowSetBonusDialog()
    {
        if (_current == null || _bonusGrid.SelectedItem is not BonusRow row)
            return;
        var picked = ShowBonusPickerDialog(out var key, out var value, row.Key, row.ValueText);
        if (!picked)
            return;
        row.Key = key;
        row.ValueText = value;
        var bm = _engine.GetBaseModifier(key);
        row.Loc = bm != null && bm.Localisations.TryGetValue(ModLang, out var lv) ? lv : key;
        _bonusGrid.Items.Refresh();
        SaveRefs();
    }

    /// <summary>选择基础加成（**照抄法令弹窗**：顶部搜索+数值一行、DataGrid 2 列（键+本地化）、
    /// 选中联动确定、双击确定、搜索框 Focus/SelectAll、数值解析失败默认 1——用户 2026-08）。
    /// 数据源 = GetAllBaseModifiers（静态加成引用 = 基础）。</summary>
    private bool ShowBonusPickerDialog(out string key, out string value, string initialKey, string initialValue)
    {
        key = "";
        value = initialValue;
        var pickedKey = "";
        var pickedValue = initialValue;
        var win = new Window
        {
            Title = _loc.Get("edict.bonus_pick_title"),
            Width = 460, Height = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this)
        };
        var panel = new DockPanel { Margin = new Thickness(12) };

        // 顶部一行：搜索输入框（左，星列）+ 数值（右，固定宽）——照抄法令
        var topRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90, GridUnitType.Pixel) });
        var searchBox = new TextBox { Text = initialKey, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(searchBox, 0);
        topRow.Children.Add(searchBox);
        var valueBox = new TextBox
        {
            Text = initialValue, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = _loc.Get("edict.bonus_value")
        };
        Grid.SetColumn(valueBox, 1);
        topRow.Children.Add(valueBox);
        DockPanel.SetDock(topRow, Dock.Top);
        panel.Children.Add(topRow);

        var okBtn = new Button { Content = _loc.Get("edict.ok") ?? "确定", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, IsEnabled = false };
        var cancelBtn = new Button { Content = _loc.Get("edict.cancel") ?? "取消", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        DockPanel.SetDock(btnRow, Dock.Bottom);
        panel.Children.Add(btnRow);

        // 选项列表：DataGrid 2 列（键 + 本地化）——照抄法令
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
            Header = _loc.Get("edict.bonus_key"),
            Binding = new System.Windows.Data.Binding("Key"),
            Width = new DataGridLength(35, DataGridLengthUnitType.Star)
        });
        resultGrid.Columns.Add(new DataGridTextColumn
        {
            Header = _loc.Get("edict.bonus_loc"),
            Binding = new System.Windows.Data.Binding("Loc"),
            Width = new DataGridLength(65, DataGridLengthUnitType.Star)
        });
        panel.Children.Add(resultGrid);
        win.Content = panel;

        void Refresh()
        {
            resultGrid.Items.Clear();
            var kw = searchBox.Text?.Trim() ?? "";
            if (kw.Length == 0)
            {
                okBtn.IsEnabled = false;
                return;
            }
            foreach (var bm in _engine.GetAllBaseModifiers())
            {
                var display = bm.Name;
                if (bm.Localisations.TryGetValue(ModLang, out var lv))
                    display = lv;
                if (bm.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
                    || display.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    resultGrid.Items.Add(new BonusRow { Key = bm.Name, Loc = display, ValueText = "" });
            }
            // 初始 key 已填 → 默认可确定（设置场景保留原 key）——照抄法令
            okBtn.IsEnabled = initialKey.Length > 0 && resultGrid.SelectedItem is null;
        }
        searchBox.TextChanged += (_, _) => Refresh();
        resultGrid.SelectionChanged += (_, _) => okBtn.IsEnabled = resultGrid.SelectedItem is BonusRow;
        resultGrid.MouseDoubleClick += (_, _) =>
        {
            if (okBtn.IsEnabled) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        okBtn.Click += (_, _) =>
        {
            var pickedName = (resultGrid.SelectedItem as BonusRow)?.Key
                ?? (initialKey.Length > 0 ? searchBox.Text?.Trim() : "");
            if (string.IsNullOrEmpty(pickedName))
                return;
            pickedKey = pickedName;
            pickedValue = double.TryParse(valueBox.Text?.Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var dv)
                ? dv.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "1";
            win.Close();
        };
        searchBox.Focus();
        searchBox.SelectAll();
        win.ShowDialog();
        key = pickedKey;
        value = pickedValue;
        return !string.IsNullOrEmpty(key);
    }

    // ==================== 小工具（照抄法令样式） ====================

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.Bold,
        Foreground = Brushes.Gray,
        Margin = new Thickness(0, 8, 0, 4)
    };

    private static TextBox AddLabeledTextBox(StackPanel panel, string label)
    {
        panel.Children.Add(new TextBlock { Text = label + ":", Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 2) });
        var box = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(box);
        return box;
    }
}
