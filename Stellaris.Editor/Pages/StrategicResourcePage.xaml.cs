using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Stellaris.Editor.Controls;
using Stellaris.Engine.StrategicResource;

namespace Stellaris.Editor.Pages;

/// <summary>战略资源页（可视化选择性合并 v3）：
/// 顶部搜索栏（**左侧保存整个文件按钮**）+ 左资源列表 + 右字段行三列
/// （key | 值下拉（同值无下拉；含"自定义"）| 来源/自定义输入框）+ 描述区（名字 + 语种切换 + 描述逻辑值）。</summary>
public sealed class StrategicResourcePage : UserControl
{
    private readonly EngineServices _services;
    private readonly StrategicResourceEngine _engine;
    private TextBox _searchBox = null!;
    private System.Windows.Threading.DispatcherTimer _searchDebounce = null!;   // 搜索框 2 秒防抖
    private ListBox _list = null!;
    private DockPanel? _leftPanel;   // 左列表容器（无静态宽度——填满 Star 列，动态缩放）
    private DataGrid _rowsGrid = null!;
    private TextBox _nameKeyBox = null!;      // 标识键（可编辑——本地化读写用）
    private Controls.LocalisationEditBox _locBox = null!;   // 统一本地化组件
    private StrategicResourceEntry? _current;

    public StrategicResourcePage(EngineServices services)
    {
        _services = services;
        _engine = services.StrategicResourceEngine!;
        var loc = services.Localisation;

        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // 左列表 Star 权重 1（**默认 20%**，用户）、分隔条 Auto、右详情 Star 权重 4（80%）——拖 Splitter 可调
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });

        // ===== 行0：保存 + 复制（左） + 搜索栏（填满） =====
        var topRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var leftBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var saveAllBtn = new Button
        {
            Content = loc.Get("resource.save_all"),
            Padding = new Thickness(8, 2, 8, 2),   // 保存按钮统一样式（照静态加成/法令页——用户 2026-08）
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        saveAllBtn.Click += (_, _) => SaveAll();
        leftBtns.Children.Add(saveAllBtn);
        // 复制所有 key（换行分隔）——修模组快速取 key 列表用
        var copyBtn = new Button
        {
            Content = loc.Get("resource.copy_keys"),
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        copyBtn.Click += (_, _) =>
        {
            var keys = _engine.GetResourceKeys();
            if (keys.Count > 0)
                System.Windows.Clipboard.SetText(string.Join("\n", keys));
        };
        leftBtns.Children.Add(copyBtn);
        Grid.SetColumn(leftBtns, 0);
        topRow.Children.Add(leftBtns);
        _searchBox = new TextBox { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _searchBox.ToolTip = loc.Get("common.list_search");
        _searchDebounce = Stellaris.Editor.Controls.SearchDebouncer.Attach(_searchBox, RefreshList);
        _searchBox.KeyDown += (_, e) => { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; _searchDebounce.Stop(); RefreshList(); } };
        Grid.SetColumn(_searchBox, 1);
        topRow.Children.Add(_searchBox);
        var searchBtn = new Button
        {
            Content = "🔍", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("SearchButtonStyle")
        };
        searchBtn.Click += (_, _) => RefreshList();
        Grid.SetColumn(searchBtn, 2);
        topRow.Children.Add(searchBtn);
        Grid.SetRow(topRow, 0);
        Grid.SetColumnSpan(topRow, 3);
        root.Children.Add(topRow);

        // ===== 行1：左资源列表 + 右详情 =====
        // DockPanel：ListBox 填满可用高度 → 内容超出出现滚动条（StackPanel 会给无限高度导致滚动条消失）
        _leftPanel = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };   // 无静态 Width——填满 Star 列（用户：左侧可拖动大小）
        _list = new ListBox
        {
            MinHeight = 200,
            ItemContainerStyle = StretchListBoxItemStyle()   // 项统一撑满列表宽（"短时短/宽时宽"根因：ListBoxItem 默认按内容宽）
        };
        _list.SelectionChanged += (_, _) => OnSelected();
        _leftPanel.Children.Add(_list);
        Grid.SetRow(_leftPanel, 1);
        Grid.SetColumn(_leftPanel, 0);

        var rightPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };

        // 本地化区（顶部）：资源键（可编辑）在**最上**，下面语种下拉（撑满整行），
        // 再下面依次显示 名字逻辑值 / 名字显示值 / 描述逻辑值 / 描述显示值
        var descPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(descPanel, Dock.Top);
        AddLocRow(descPanel, loc.Get("resource.name_key"), out _nameKeyBox, readOnly: false);
        _nameKeyBox.LostFocus += (_, _) => SaveNameKey();
        // 统一本地化组件（LocalisationEditBox）：语种下拉 + 名称/描述（逻辑值可编辑 → 显示值只读）
        _locBox = new Controls.LocalisationEditBox
        {
            Adapter = _services.Adapter,
            GetNameKey = () => string.IsNullOrEmpty(_current?.NameKey) ? (_current?.Key ?? "") : _current.NameKey,
            SaveLocalisation = (lang, key, text) =>
            {
                try
                {
                    var files = _services.Adapter.GetLocalisationFiles(lang);
                    if (files.Count == 0)
                        return;
                    _services.Adapter.UpdateLocalisationEntry(lang, files[0], key, text);
                    _services.Adapter.ExpandLocalisationKey(lang, key);
                    RefreshDesc();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"UpdateLocalisation failed: {ex.Message}", "Stellaris Mod Tools");
                    RefreshDesc();
                }
            }
        };
        descPanel.Children.Add(_locBox);
        rightPanel.Children.Add(descPanel);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _rowsGrid = CreateRowsGrid();
        scroll.Content = _rowsGrid;
        rightPanel.Children.Add(scroll);
        var splitter = new GridSplitter
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent
        };
        Grid.SetRow(splitter, 1);
        Grid.SetColumn(splitter, 1);
        Grid.SetRow(rightPanel, 1);
        Grid.SetColumn(rightPanel, 2);
        root.Children.Add(_leftPanel);
        root.Children.Add(splitter);
        root.Children.Add(rightPanel);

        Content = root;
        _locBox.Reload();
        RefreshList();
    }

    private string ModLang => _services.Localisation.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        ? "simp_chinese" : "english";

    private void RefreshList()
    {
        _list.Items.Clear();
        var pat = _searchBox.Text?.Trim() ?? "";
        // leftPanel 无静态宽度——填满 Star 列，拖 GridSplitter 真实变宽/变窄（用户：动态宽度；静态 Width 会覆盖列宽拉伸）
        foreach (var entry in _engine.GetEntries())
        {
            _engine.LoadLocalisation(entry, ModLang);
            if (pat.Length > 0
                && !entry.Key.Contains(pat, StringComparison.OrdinalIgnoreCase)
                && !entry.NameDisplay.Contains(pat, StringComparison.OrdinalIgnoreCase))
                continue;
            _list.Items.Add(new ResourceListItem(entry));
        }
        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;
        else
        {
            _current = null;
            _rowsGrid.ItemsSource = null;
            _locBox.Load();
        }
    }

    /// <summary>列表项拉伸样式：每项撑满列表宽（"短时短/宽时宽"根因——ListBoxItem 默认按内容宽）。</summary>
    private static Style StretchListBoxItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    /// <summary>本地化行：label（灰）与输入框**同一行**（节约空间）——逻辑值可编辑；显示值只读可选中复制；Shift+回车 → \n。</summary>
    private static void AddLocRow(StackPanel panel, string label, out TextBox box, bool readOnly)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90, GridUnitType.Pixel) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label + ":", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        box = new TextBox { IsReadOnly = readOnly, TextWrapping = TextWrapping.Wrap, MinHeight = 22, VerticalAlignment = VerticalAlignment.Center };
        box.IsReadOnlyCaretVisible = readOnly;
        AttachShiftEnterNewline(box);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        panel.Children.Add(row);
    }

    /// <summary>Shift+回车 → 在光标处插入 \n（换行转义）——统一辅助（普通回车保持默认）。</summary>
    internal static void AttachShiftEnterNewline(TextBox box)
    {
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                e.Handled = true;
                var idx = box.CaretIndex;
                box.Text = box.Text.Insert(idx, "\\n");
                box.CaretIndex = idx + 2;
            }
        };
    }

    private void OnSelected()
    {
        _current = (_list.SelectedItem as ResourceListItem)?.Entry;
        BuildRows();
        RefreshDesc();
    }

    /// <summary>本地化刷新：标识键 + 组件（语种下拉内状态保留，只重填文本）。</summary>
    private void RefreshDesc()
    {
        if (_current == null || _services.Adapter == null)
            return;
        var nameKey = string.IsNullOrEmpty(_current.NameKey) ? _current.Key : _current.NameKey;
        _nameKeyBox.Text = nameKey;
        _locBox.Load();
    }

    private void SaveNameKey()
    {
        if (_current == null)
            return;
        var text = _nameKeyBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            _nameKeyBox.Text = _current.Key;
            return;
        }
        _current.NameKey = text;
        RefreshDesc();
    }

    /// <summary>逻辑值可编辑（抄星系样式）：更新本地化原文 → 展开 → 刷新显示值。</summary>


    /// <summary>该语种下的本地化文件路径（第一个现有文件；无 → null）。</summary>

    /// <summary>最少分隔符：连续空白（含回车/多空格）压缩为 1 个空格——显示紧凑。</summary>
    private static string Collapse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        var sb = new System.Text.StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace && sb.Length > 0)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>字段行 DataGrid（三列——列宽可调）：key | 值下拉（去重选项含自定义）| 来源/自定义输入框。</summary>
    private static DataGrid CreateRowsGrid()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0,
            Background = System.Windows.Media.Brushes.White,
            CanUserReorderColumns = true,
            CanUserResizeColumns = true,
            CanUserSortColumns = false,
            SelectionMode = DataGridSelectionMode.Single
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Key",
            Binding = new System.Windows.Data.Binding("FieldKey"),
            Width = new DataGridLength(180)
        });
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "值",
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            CellTemplate = MakeValueTemplate()
        });
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "来源",
            Width = new DataGridLength(280),
            CellTemplate = MakeSourceTemplate()
        });
        return grid;
    }

    /// <summary>值列模板：ComboBox（选项 = RowVm.DisplayOptions——去重值 + 自定义标记）。</summary>
    private static DataTemplate MakeValueTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(ComboBox));
        factory.SetValue(ComboBox.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.SetBinding(ComboBox.ItemsSourceProperty, new System.Windows.Data.Binding("DisplayOptions"));
        factory.SetBinding(ComboBox.SelectedIndexProperty, new System.Windows.Data.Binding("SelectedIndex") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        factory.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler(OnValueComboChanged));
        return new DataTemplate { VisualTree = factory };
    }

    /// <summary>来源列模板：非自定义显示 TextBlock（来源 root）；自定义显示 TextBox（输入框——填满列宽 + 自动换行）。
    /// 用绑定 + BooleanToVisibilityConverter 切换（FrameworkElementFactory 设的 Name 不注册 NameScope——不能用 DataTrigger TargetName）。</summary>
    private static DataTemplate MakeSourceTemplate()
    {
        var conv = new System.Windows.Controls.BooleanToVisibilityConverter();
        var grid = new FrameworkElementFactory(typeof(Grid));
        var srcText = new FrameworkElementFactory(typeof(TextBlock));
        srcText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("SourceText"));
        srcText.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        srcText.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        srcText.SetValue(TextBlock.MarginProperty, new Thickness(6, 0, 0, 0));
        srcText.SetBinding(TextBlock.VisibilityProperty, new System.Windows.Data.Binding("IsNotCustom") { Converter = conv });
        grid.AppendChild(srcText);
        var customBox = new FrameworkElementFactory(typeof(TextBox));
        customBox.SetValue(TextBox.VerticalAlignmentProperty, VerticalAlignment.Center);
        customBox.SetValue(TextBox.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        customBox.SetValue(TextBox.MarginProperty, new Thickness(6, 0, 0, 0));
        customBox.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
        customBox.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("CustomValue") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus });
        customBox.SetBinding(TextBox.VisibilityProperty, new System.Windows.Data.Binding("IsCustom") { Converter = conv });
        customBox.AddHandler(TextBox.PreviewKeyDownEvent, new KeyEventHandler(OnCustomBoxPreviewKeyDown));
        grid.AppendChild(customBox);
        return new DataTemplate { VisualTree = grid };
    }

    /// <summary>自定义输入框（特殊）：Shift+回车 = 在输入框内**真实换行**（不是 \n 转义符）。
    /// 普通回车保持默认。</summary>
    private static void OnCustomBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox box && e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            e.Handled = true;
            var idx = box.CaretIndex;
            box.Text = box.Text.Insert(idx, "\n");
            box.CaretIndex = idx + 1;
        }
    }

    /// <summary>下拉变化：自定义 → row.CustomValue；值 → 选 Roots 更靠后的同值方案。之后重建（列 3 切换）。</summary>
    private static void OnValueComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem it)
            return;
        if (combo.DataContext is not RowVm vm)
            return;
        if (ReferenceEquals(it.Tag, RowVm.CustomTag))
        {
            vm.Row.CustomValue = vm.Row.Selected?.DisplayValue ?? "";
            vm.Refresh();
        }
        else if (it.Tag is string val)
        {
            vm.Row.CustomValue = null;
            var idx = vm.Row.Variants
                .Select((v, i) => (v, i))
                .Where(x => x.v.DisplayValue == val)
                .OrderByDescending(x => x.i)
                .First().i;
            vm.Row.SelectedIndex = idx;
            vm.Refresh();
        }
    }

    /// <summary>词条行视图模型（DataGrid 行对象——转发到 ResourceFieldRow）。</summary>
    private sealed class RowVm : System.ComponentModel.INotifyPropertyChanged
    {
        /// <summary>"自定义"选项标记（对象引用比较——不会与字符串值冲突）。</summary>
        public static readonly object CustomTag = new();

        public ResourceFieldRow Row { get; }
        public List<ComboBoxItem> DisplayOptions { get; } = new();

        public RowVm(ResourceFieldRow row, string customText)
        {
            Row = row;
            foreach (var v in row.Variants.Select(x => x.DisplayValue).Distinct())
                DisplayOptions.Add(new ComboBoxItem { Content = Collapse(v), Tag = v });
            DisplayOptions.Add(new ComboBoxItem { Content = customText, Tag = CustomTag });
        }

        public string FieldKey => Row.FieldKey;
        public string SourceText => Row.Selected?.Root ?? "";
        public string? CustomValue
        {
            get => Row.CustomValue;
            set { Row.CustomValue = value; Refresh(); }
        }
        public bool IsCustom => Row.CustomValue != null;
        public bool IsNotCustom => Row.CustomValue == null;
        public int SelectedIndex
        {
            get
            {
                if (Row.CustomValue != null)
                    return DisplayOptions.Count - 1;
                var val = Row.Selected?.DisplayValue ?? "";
                var match = DisplayOptions.Take(DisplayOptions.Count - 1).Select((o, i) => (o, i))
                    .FirstOrDefault(x => string.Equals((x.o.Tag as string), val, StringComparison.Ordinal)).i;
                return match < 0 ? DisplayOptions.Count - 1 : match;
            }
            set
            {
                if (value >= 0 && value < DisplayOptions.Count - 1 && DisplayOptions[value].Tag is string s)
                {
                    var idx = Row.Variants.Select((v, i) => (v, i))
                        .Where(x => x.v.DisplayValue == s)
                        .OrderByDescending(x => x.i)
                        .First().i;
                    Row.SelectedIndex = idx;
                    Row.CustomValue = null;
                }
            }
        }

        public void Refresh() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(""));
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private void BuildRows()
    {
        if (_rowsGrid == null)
            return;
        _rowsGrid.ItemsSource = null;
        if (_current == null)
            return;
        var customText = _services.Localisation.Get("resource.custom");
        _rowsGrid.ItemsSource = _current.Rows.Select(r => new RowVm(r, customText)).ToList();
    }

    private void SaveAll()
    {
        SaveRunner.Run(_services, "status.saving",
            () =>
            {
                // 保存规范：引擎 SaveAll 经 SA 标准 WriteFile（Roots 最后一位 + 自动建目录）；
                // 失败详情由引擎 _logger 写 editor_debug.log。
                var (saved, errors) = _engine.SaveAll();
                if (errors.Count == 0)
                    _engine.ScanAll();   // 选择性重载：保存成功后重新扫描资源文件（界面显示新数据，用户确认已保存）
                return errors.Count == 0;
            },
            onSuccess: () => BuildRows(),
            failMessage: _services.Localisation.Get("status.save_failed"));
    }

    private sealed class ResourceListItem
    {
        public StrategicResourceEntry Entry { get; }
        public ResourceListItem(StrategicResourceEntry entry) => Entry = entry;
        public override string ToString() => Entry.NameDisplay;
    }
}
