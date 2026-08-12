using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Stellaris.Engine.StrategicResource;
using Stellaris.Parser;

namespace Stellaris.Editor.Controls;

/// <summary>
/// 资源 3 表格统一组件：TabControl 3 页（启动消耗 / 每月消耗 / 每月产出）。
/// 外部控制：数据源（Cost/Upkeep/Produces）、资源键列表、高度、资源中文名解析（Adapter + 语言）。
/// 增删改（右键菜单：增加组/修改/删除/倍率/条件）在组件内完成；变更后触发 Changed 事件（宿主可按需刷新）。
/// </summary>
public partial class ResourceTabsControl : UserControl
{
    /// <summary>数据源（资源中文名解析）。</summary>
    public StellarisAdapter? Adapter { get; set; }

    /// <summary>本地化语言（资源中文名用，如 simp_chinese）。</summary>
    public string ModLang { get; set; } = "simp_chinese";

    /// <summary>3 个数据桶（外部提供——法令 cost/upkeep/product）。</summary>
    public StrategicResourceEngine.ResourceBucket Cost { get; set; } = new();
    public StrategicResourceEngine.ResourceBucket Upkeep { get; set; } = new();
    public StrategicResourceEngine.ResourceBucket Produces { get; set; } = new();

    /// <summary>资源键列表（增加弹窗下拉选项）。</summary>
    public List<string> ResourceKeys { get; set; } = new();

    /// <summary>变更后触发（宿主可按需刷新/保存）。</summary>
    public event Action? Changed;

    private DataGrid _costGrid = null!;
    private DataGrid _upkeepGrid = null!;
    private DataGrid _productGrid = null!;

    public ResourceTabsControl()
    {
        InitializeComponent();
        Tabs.Items.Add(new TabItem { Header = "启动消耗", Content = _costGrid = BuildResourceGrid(0) });
        Tabs.Items.Add(new TabItem { Header = "每月消耗", Content = _upkeepGrid = BuildResourceGrid(1) });
        Tabs.Items.Add(new TabItem { Header = "每月产出", Content = _productGrid = BuildResourceGrid(2) });
    }
    /// <summary>高度（外部可控制；设 0 = 自适应）。</summary>
    public double TabsHeight
    {
        get => Tabs.Height;
        set => Tabs.Height = value;
    }

    /// <summary>重填 3 个表格（条目切换/数据变更后调用）。</summary>
    public void Refresh()
    {
        _costGrid.ItemsSource = BuildRows(Cost);
        _upkeepGrid.ItemsSource = BuildRows(Upkeep);
        _productGrid.ItemsSource = BuildRows(Produces);
    }

    private StrategicResourceEngine.ResourceBucket BucketOf(int column)
        => column switch
        {
            0 => Cost,
            1 => Upkeep,
            _ => Produces
        };

    private DataGrid BuildResourceGrid(int column)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0,
            MinHeight = 120,
            CanUserReorderColumns = false,
            CanUserSortColumns = false,
            SelectionMode = DataGridSelectionMode.Single
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "资源",
            Binding = new System.Windows.Data.Binding("ResName"),
            Width = new DataGridLength(25, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "数值",
            Binding = new System.Windows.Data.Binding("Amount"),
            Width = new DataGridLength(20, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "倍率",
            Binding = new System.Windows.Data.Binding("MultText"),
            Width = new DataGridLength(35, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "条件",
            Binding = new System.Windows.Data.Binding("TriggerHashText"),
            Width = new DataGridLength(20, DataGridLengthUnitType.Star)
        });
        var menu = new ContextMenu();
        var add = new MenuItem { Header = "增加" };
        add.Click += (_, _) => AddResourceGroup(column);
        menu.Items.Add(add);
        var modify = new MenuItem { Header = "修改" };
        modify.Click += (_, _) => ModifyResourceRow(grid, column);
        menu.Items.Add(modify);
        var del = new MenuItem { Header = "删除" };
        del.Click += (_, _) => DeleteResourceRow(grid, column);
        menu.Items.Add(del);
        grid.ContextMenu = menu;
        return grid;
    }

    /// <summary>资源表格行（行 = 一个资源；GroupIndex = 所属组——同组行倍率/条件相同）。</summary>
    private sealed class BucketRowVm
    {
        public int GroupIndex { get; }
        public string ResKey { get; }
        public string ResName { get; }
        public string Amount { get; }
        public string MultText { get; }
        public string TriggerHashText { get; }
        public string TriggerText { get; }
        public BucketRowVm(int groupIndex, string resKey, string resName, string amount, string multText, string triggerText)
        {
            GroupIndex = groupIndex;
            ResKey = resKey;
            ResName = resName;
            Amount = amount;
            MultText = string.IsNullOrEmpty(multText) ? "" : "×" + multText;
            TriggerHashText = string.IsNullOrEmpty(triggerText) ? "" : StrategicResourceEngine.TriggerHash(triggerText);
            TriggerText = triggerText;
        }
    }

    private List<BucketRowVm> BuildRows(StrategicResourceEngine.ResourceBucket bucket)
    {
        var rows = new List<BucketRowVm>();
        if (Adapter == null)
            return rows;
        for (int gi = 0; gi < bucket.Groups.Count; gi++)
        {
            var g = bucket.Groups[gi];
            var triggerText = g.Trigger == null ? ""
                : SerializationHelper.Serialize(g.Trigger.Children).Trim();
            foreach (var kv in g.Amounts)
            {
                var resName = Adapter.GetLocalisedText(kv.Key, ModLang) ?? kv.Key;
                rows.Add(new BucketRowVm(gi, kv.Key, resName,
                    kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    g.Multiplier ?? "", triggerText));
            }
        }
        return rows;
    }

    /// <summary>增加：弹窗（资源下拉 + 数值 + 可选倍率 + 可选条件）→ 加一个新组。
    /// 同倍率+条件的组在生成 AST 时由引擎自动整合合并。</summary>
    private void AddResourceGroup(int column)
    {
        if (ResourceKeys.Count == 0)
            return;
        var bucket = BucketOf(column);
        var dlg = new Window
        {
            Title = "增加资源",
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Width = 420,   // 固定宽——输入框撑满剩余横向空间
            SizeToContent = SizeToContent.Height,
            Owner = Window.GetWindow(this)
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        var resCombo = new ComboBox { Width = 180 };
        foreach (var k in ResourceKeys)
        {
            var resName = Adapter != null ? (Adapter.GetLocalisedText(k, ModLang) ?? "") : "";
            resCombo.Items.Add(new ComboBoxItem
            {
                Content = string.IsNullOrEmpty(resName) ? k : k + ": " + resName,
                Tag = k
            });
        }
        resCombo.SelectedIndex = 0;
        var amtBox = new TextBox { Width = 100, Text = "0" };
        var multBox = new TextBox { ToolTip = "数字或 value:xxx（空 = 无）", HorizontalAlignment = HorizontalAlignment.Stretch };   // 撑满剩余
        var trigBox = new TextBox { Height = 80, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalAlignment = HorizontalAlignment.Stretch };   // 撑满剩余
        var okBtn = new Button { Content = "确定", Width = 80, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        okBtn.Click += (_, _) => dlg.DialogResult = true;

        // 资源 + 数值一行；倍率一行；条件大输入框
        var resRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        resRow.Children.Add(new TextBlock { Text = "资源：", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        var amtLabel = new TextBlock { Text = "数值：", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        // 停靠顺序：amtBox 先加（最右）→ amtLabel 后加（在框左侧）——label 不能在框右侧
        DockPanel.SetDock(amtBox, Dock.Right);
        resRow.Children.Add(amtBox);
        DockPanel.SetDock(amtLabel, Dock.Right);
        resRow.Children.Add(amtLabel);
        resRow.Children.Add(resCombo);   // 填充剩余（撑满）

        var multRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        multRow.Children.Add(new TextBlock { Text = "倍率（可选）：", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        multRow.Children.Add(multBox);

        panel.Children.Add(resRow);
        panel.Children.Add(multRow);
        panel.Children.Add(new TextBlock { Text = "条件（可选）：", Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(trigBox);
        panel.Children.Add(okBtn);
        dlg.Content = panel;
        if (dlg.ShowDialog() != true || resCombo.SelectedItem is not ComboBoxItem resItem)
            return;
        var res = resItem.Tag as string ?? "";
        if (!double.TryParse(amtBox.Text?.Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            v = 0;
        var group = new StrategicResourceEngine.ResourceGroup();
        group.Amounts[res] = v;
        var multText = multBox.Text?.Trim() ?? "";
        if (multText.Length > 0)
            group.Multiplier = multText;
        var trigText = trigBox.Text?.Trim();
        if (!string.IsNullOrEmpty(trigText))
            group.Trigger = ParseTriggerBlock(trigText);   // 解析失败 → null（不设条件）
        bucket.Groups.Add(group);
        Refresh();
        Changed?.Invoke();
    }

    private void ModifyResourceRow(DataGrid grid, int column)
    {
        if (grid.SelectedItem is not BucketRowVm row)
            return;
        var group = BucketOf(column).Groups[row.GroupIndex];
        if (ShowInput("修改数值（" + row.ResKey + "）", row.Amount, out var text)
            && double.TryParse(text?.Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            group.Amounts[row.ResKey] = v;
            Refresh();
            Changed?.Invoke();
        }
    }

    private void DeleteResourceRow(DataGrid grid, int column)
    {
        if (grid.SelectedItem is not BucketRowVm row)
            return;
        var bucket = BucketOf(column);
        var group = bucket.Groups[row.GroupIndex];
        group.Amounts.Remove(row.ResKey);
        if (group.Amounts.Count == 0)
            bucket.Groups.RemoveAt(row.GroupIndex);   // 组内资源删空 → 整组删除
        Refresh();
        Changed?.Invoke();
    }

    /// <summary>倍率：multiplier Simple（组的乘法系数，数字或 value:xxx）；空文本 = 清除。</summary>
    /// <summary>把条件文本包成 trigger = { ... } 解析为 Block；失败返回 null。</summary>
    private static AstNode? ParseTriggerBlock(string text)
    {
        try
        {
            var wrapped = "trigger = { " + text + " }";
            var lexer = new Lexer(wrapped);
            var tokens = new List<Token>();
            Token tok;
            while ((tok = lexer.NextToken()).Type != TokenType.Eof)
                tokens.Add(tok);
            var parser = new Stellaris.Parser.Parser(tokens, new[] { wrapped }, "trigger", wrapped);
            var node = parser.Parse().RootNodes.FirstOrDefault();
            if (node != null && (node.Type == NodeType.Block || node.Type == NodeType.List))
                return node;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private bool ShowInput(string title, string initial, out string text)
    {
        var dlg = new Window
        {
            Title = title,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Owner = Window.GetWindow(this)
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
            Owner = Window.GetWindow(this)
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
}
