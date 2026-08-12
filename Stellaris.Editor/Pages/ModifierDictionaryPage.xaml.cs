// 文件: Stellaris.Editor/Pages/ModifierDictionaryPage.xaml.cs
// 加成字典页（只读）：全 AST 扫描 static_modifiers 顶层块（自定义）+ 本地化 mod_ 词条（基础）
// + 任意文件 modifier 引用（weight/ai_weight 父级跳过）。搜索（关键词）+ 类型筛选 + 隐藏筛选。
// 选中条目 → 右侧详情（本地化/隐藏/图标/引用的基础/被谁引用/未知键/来源文件）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Stellaris.Engine.StaticModifier;

namespace Stellaris.Editor.Pages;

public partial class ModifierDictionaryPage : UserControl
{
    private readonly EngineServices _services;
    private readonly UILocalisationManager _loc;
    private System.Windows.Threading.DispatcherTimer _patternDebounce = null!;   // 搜索框 2 秒防抖

    public ModifierDictionaryPage(EngineServices services)
    {
        _services = services;
        _loc = services.Localisation;
        InitializeComponent();
        _patternDebounce = Stellaris.Editor.Controls.SearchDebouncer.Attach(PatternBox, () => OnSearch(this, new RoutedEventArgs()));

        SearchButton.Content = _loc.Get("moddict.search");
        PatternBox.ToolTip = _loc.Get("moddict.search");

        // 列头本地化
        if (ResultGrid.Columns.Count >= 4)
        {
            ResultGrid.Columns[0].Header = _loc.Get("moddict.column_type");
            ResultGrid.Columns[1].Header = _loc.Get("moddict.column_name");
            ResultGrid.Columns[2].Header = _loc.Get("moddict.column_display");
            ResultGrid.Columns[3].Header = _loc.Get("moddict.column_hidden");
        }

        // 类型导航（竖排 ListBox——类似左侧导航栏）：全部/基础/静态（static_modifiers 顶层）/自定义（scripted 顶层）
        TypeNav.Items.Add(new ListBoxItem { Content = _loc.Get("moddict.type_all"), Tag = "all" });
        TypeNav.Items.Add(new ListBoxItem { Content = _loc.Get("moddict.type_base"), Tag = "base" });
        TypeNav.Items.Add(new ListBoxItem { Content = _loc.Get("moddict.type_custom"), Tag = "static" });
        TypeNav.Items.Add(new ListBoxItem { Content = _loc.Get("moddict.type_custom_base"), Tag = "custom" });
        TypeNav.SelectedIndex = 0;

        // 隐藏筛选
        HiddenCombo.Items.Add(new ComboBoxItem { Content = _loc.Get("moddict.hidden_all"), Tag = "all" });
        HiddenCombo.Items.Add(new ComboBoxItem { Content = _loc.Get("moddict.hidden_yes"), Tag = "hidden" });
        HiddenCombo.Items.Add(new ComboBoxItem { Content = _loc.Get("moddict.hidden_no"), Tag = "visible" });
        HiddenCombo.SelectedIndex = 0;

        ExportDetailMenuItem.Header = _loc.Get("moddict.export_detail");
        ResultGrid.ContextMenu!.Opened += (_, _) =>
            ExportDetailMenuItem.IsEnabled = ResultGrid.SelectedItems.Count > 0;

        // 初始全量显示
        OnSearch(this, new RoutedEventArgs());
    }

    private StaticModifierEngine? Engine => _services.StaticModifierEngine;

    /// <summary>结果行视图。</summary>
    public sealed class ModifierEntryView
    {
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public string Display { get; set; } = "";
        public bool Hidden { get; set; }
        public string HiddenText { get; set; } = "";
        public string Icon { get; set; } = "";
        public object? Entry { get; set; }
    }



    private readonly List<string> _detailLines = new();  // 当前详情文本行（全部复制用）
    private string _typeFilter = "all";                 // 类型导航选择
    private List<ModifierEntryView> _allRows = new();   // 最近一次搜索的全部候选（未按类型/隐藏过滤）


    /// <summary>搜索框回车：普通回车 → 触发搜索；Shift+回车 → 插入 \n（统一）。</summary>
    private void OnPatternBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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
            _patternDebounce.Stop();   // 手动搜索后停止防抖计时器（防 2 秒后重复触发）
            OnSearch(this, new RoutedEventArgs());
        }
    }

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        var engine = Engine;
        if (engine == null)
        {
            StatusText.Text = _loc.Get("moddict.engine_unavailable");
            return;
        }

        string keyword = PatternBox.Text.Trim();
        string curLang = _loc.CurrentLanguage;

        var candidates = new List<ModifierEntryView>();
        if (keyword.Length == 0)
        {
            // 无关键词 → 全量（基础 + 自定义）
            foreach (var be in engine.GetAllBaseModifiers())
                candidates.AddRange(MakeBaseRows(be, curLang));
            foreach (var ce in engine.GetStaticModifiers())
                candidates.Add(MakeCustomRow(ce, curLang));
        }
        else
        {
            foreach (var hit in engine.Search(keyword))
            {
                if (hit is StaticModifierEngine.BaseModifier be)
                    candidates.AddRange(MakeBaseRows(be, curLang));
                else if (hit is StaticModifierEngine.StaticModifierEntry ce)
                    candidates.Add(MakeCustomRow(ce, curLang));
            }
        }

        _allRows = candidates;
        ApplyFilters();
        DetailPanel.Children.Clear();
        DetailPanel.Children.Add(new TextBlock
        {
            Text = _loc.Get("moddict.no_selection"),
            Foreground = System.Windows.Media.Brushes.Gray
        });
    }

    /// <summary>按类型导航 + 隐藏筛选本地过滤（不重新搜索）。</summary>
    private void ApplyFilters()
    {
        string hiddenFilter = (HiddenCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var rows = _allRows.Where(r =>
        {
            bool typeOk = _typeFilter == "all" || RowMatchesType(r, _typeFilter);
            bool hiddenOk = hiddenFilter == "all"
                || (hiddenFilter == "hidden" ? r.Hidden : !r.Hidden);
            return typeOk && hiddenOk;
        }).ToList();
        ResultGrid.ItemsSource = rows;
        StatusText.Text = string.Format(_loc.Get("moddict.status_count"), rows.Count);
    }

    /// <summary>行类型匹配：base=普通基础（无定义）；static=static 定义（静态）；custom=scripted 定义（自定义）。</summary>
    private bool RowMatchesType(ModifierEntryView r, string filter)
    {
        if (r.Entry is StaticModifierEngine.StaticModifierEntry)
            return filter == "static";   // static_modifiers 顶层块 → 静态
        if (r.Entry is StaticModifierEngine.BaseModifier be)
        {
            if (be.DefinitionSources.Count == 0)
                return filter == "base";
            return be.IsStaticDefinition && filter == "static"
                || be.IsScriptedDefinition && filter == "custom";
        }
        return false;
    }

    /// <summary>右键导出：选中条目的详情内容保存到 txt（分析用）。</summary>
    private void OnExportSelectedDetails(object sender, RoutedEventArgs e)
    {
        var rows = ResultGrid.SelectedItems.Cast<ModifierEntryView>().ToList();
        if (rows.Count == 0)
            return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = _loc.Get("moddict.export_detail"),
            Filter = "Text|*.txt",
            FileName = "modifier_details.txt"
        };
        if (dlg.ShowDialog() != true)
            return;
        var sb = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendLine("======== " + row.Type + " : " + row.Name + " ========");
            var lines = BuildDetailLines(row);
            foreach (var l in lines)
                sb.AppendLine(l);
            sb.AppendLine();
        }
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString());
            StatusText.Text = string.Format(_loc.Get("moddict.export_done"), rows.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>组装某行的详情文本行（与右侧详情一致）。</summary>
    private List<string> BuildDetailLines(ModifierEntryView row)
    {
        var lines = new List<string>();
        if (row.Entry is StaticModifierEngine.BaseModifier be)
        {
            lines.Add(_loc.Get("moddict.column_name") + ": " + be.Name);
            lines.Add(_loc.Get("moddict.detail_loc_key") + ": " + (be.LocKey ?? be.ModKey));   // 真实键原样
            foreach (var src in be.DefinitionSources)
                lines.Add(_loc.Get("moddict.detail_def_source") + ": " + LocalizeSource(src)
                    + (be.GetActiveFile(src) is { } af ? " (" + af + ") " + _loc.Get("moddict.detail_active") : ""));
            lines.Add(_loc.Get("moddict.detail_localisations") + ": " + string.Join("; ",
                LocalisationDisplayLines(be.Localisations)));
            if (be.Users.Count > 0)
                lines.Add(_loc.Get("moddict.detail_users") + ": " + string.Join(", ", be.Users.Take(10).Select(u => u.Name))
                    + (be.Users.Count > 10 ? " …" : ""));
            if (be.ExternalFiles.Count > 0)
                lines.Add(_loc.Get("moddict.detail_external") + ": " + string.Join(", ", be.ExternalFiles.Take(10))
                    + (be.ExternalFiles.Count > 10 ? " …" : ""));
        }
        else if (row.Entry is StaticModifierEngine.StaticModifierEntry ce)
        {
            lines.Add(_loc.Get("moddict.column_name") + ": " + ce.Name);
            lines.Add(_loc.Get("moddict.detail_hidden") + ": " + (ce.Hidden ? _loc.Get("common.yes") : _loc.Get("common.no")));
            if (ce.Important)
                lines.Add(_loc.Get("moddict.detail_important") + ": " + _loc.Get("common.yes"));
            if (ce.IconFrame != 0)
                lines.Add(_loc.Get("moddict.detail_icon_frame") + ": " + ce.IconFrame);
            if (!string.IsNullOrEmpty(ce.CustomTooltip))
                lines.Add(_loc.Get("moddict.detail_custom_tooltip") + ": " + ce.CustomTooltip!);
            if (ce.ShowOnlyCustomTooltip)
                lines.Add(_loc.Get("moddict.detail_show_only_tooltip") + ": " + _loc.Get("common.yes"));
            if (!string.IsNullOrEmpty(ce.Icon))
                lines.Add(_loc.Get("moddict.detail_icon") + ": " + ce.Icon!);
            if (!string.IsNullOrEmpty(ce.SourceFile))
                lines.Add(_loc.Get("moddict.detail_source") + ": " + ce.SourceFile!);
            // 本地化（核心，上面）→ 自定义提示各语种翻译（次要，下面——用户 2026-08）
            lines.Add(_loc.Get("moddict.detail_localisations") + ": " + string.Join("; ",
                LocalisationDisplayLines(ce.Localisations)));
            if (!string.IsNullOrEmpty(ce.CustomTooltip))
                foreach (var l in LocalisationDisplayLines(KeyLocalisations(ce.CustomTooltip!)))
                    lines.Add("  " + l);
            if (ce.BaseRefs.Count > 0)
                lines.Add(_loc.Get("moddict.detail_bases") + ": " + string.Join(", ",
                    ce.BaseRefs.Take(10).Select(r => r.Key + " = " + r.Value))
                    + (ce.BaseRefs.Count > 10 ? " …" : ""));
            if (ce.UnknownKeys.Count > 0)
                lines.Add(_loc.Get("moddict.detail_unknown") + ": " + string.Join(", ", ce.UnknownKeys.Take(10)));
        }
        return lines;
    }

    private void OnTypeNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeNav.SelectedItem is not ListBoxItem it)
            return;
        _typeFilter = it.Tag as string ?? "all";
        ApplyFilters();
    }

    private List<ModifierEntryView> MakeBaseRows(StaticModifierEngine.BaseModifier be, string curLang)
    {
        string display = DisplayFor(be.Localisations, curLang);
        bool hidden = _services.StaticModifierEngine?.GetCustom(be.Name)?.Hidden == true;
        var rows = new List<ModifierEntryView>();
        // 普通基础（无定义）→ 1 行；有定义 → 每定义 1 行（静态 / 自定义分开——不混合）
        if (be.DefinitionSources.Count == 0)
        {
            rows.Add(new ModifierEntryView
            {
                Type = _loc.Get("moddict.type_base"),
                Name = be.Name,
                Display = display,
                Hidden = hidden,
                HiddenText = hidden ? _loc.Get("common.yes") : _loc.Get("common.no"),
                Icon = _services.StaticModifierEngine?.GetCustom(be.Name)?.Icon ?? "",
                Entry = be
            });
        }
        else
        {
            bool customHas = _services.StaticModifierEngine?.GetCustom(be.Name) != null;
            foreach (var src in be.DefinitionSources)
            {
                bool isStatic = string.Equals(src, "static", StringComparison.OrdinalIgnoreCase);
                // static 定义行由 _customs（静态列表）显示——_bases 不重复；scripted（自定义）行在此显示
                if (isStatic && customHas)
                    continue;
                rows.Add(new ModifierEntryView
                {
                    Type = isStatic ? _loc.Get("moddict.type_custom") : _loc.Get("moddict.type_custom_base"),
                    Name = be.Name,
                    Display = display,
                    Hidden = hidden,
                    HiddenText = hidden ? _loc.Get("common.yes") : _loc.Get("common.no"),
                    Icon = _services.StaticModifierEngine?.GetCustom(be.Name)?.Icon ?? "",
                    Entry = be
                });
            }
        }
        return rows;
    }

    private ModifierEntryView MakeCustomRow(StaticModifierEngine.StaticModifierEntry ce, string curLang)
    {
        return new ModifierEntryView
        {
            Type = _loc.Get("moddict.type_custom"),   // static_modifiers 顶层 → 静态
            Name = ce.Name,
            Display = DisplayFor(ce.Localisations, curLang),
            Hidden = ce.Hidden,
            HiddenText = ce.Hidden ? _loc.Get("common.yes") : _loc.Get("common.no"),
            Icon = ce.Icon ?? "",
            Entry = ce
        };
    }

    /// <summary>取本地化中当前界面语言的翻译（无则取第一个语言）。</summary>
    private string DisplayFor(SortedDictionary<string, string> localisations, string curLang)
    {
        // UI 语言（zh-CN/en-US——界面 json）→ 游戏本地化语言（simp_chinese/english——游戏 yml）
        var gameLang = GameLangFromUI(curLang);
        if (localisations.TryGetValue(gameLang, out var v))
            return v;
        if (!string.Equals(curLang, gameLang, StringComparison.OrdinalIgnoreCase)
            && localisations.TryGetValue(curLang, out var v2))
            return v2;
        return localisations.Values.FirstOrDefault() ?? "";
    }

    /// <summary>UI 界面语言标识 → 游戏本地化语言 key（zh→简体中文、en→英文；其他原样）。</summary>
    private static string GameLangFromUI(string uiLang)
        => uiLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "simp_chinese"
        : uiLang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "english"
        : uiLang;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailPanel.Children.Clear();
        _detailLines.Clear();
        if (ResultGrid.SelectedItem is not ModifierEntryView row || row.Entry == null)
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = _loc.Get("moddict.no_selection"),
                Foreground = System.Windows.Media.Brushes.Gray
            });
            return;
        }
        var copyAll = new Button
        {
            Content = _loc.Get("moddict.detail_copy_all"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(8, 2, 8, 2)
        };
        copyAll.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(string.Join("\n", _detailLines)); } catch { }
        };
        DetailPanel.Children.Add(copyAll);
        AddDetailTitle(_loc.Get("moddict.detail"));
        if (row.Entry is StaticModifierEngine.BaseModifier be)
            FillBaseDetail(be);
        else if (row.Entry is StaticModifierEngine.StaticModifierEntry ce)
            FillCustomDetail(ce);
    }

    private void AddDetailTitle(string text)
    {
        DetailPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
    }

    private void AddDetailRow(string label, string value, bool isFileLink = false)
    {
        if (value.Length == 0)
            return;
        // 行布局：label 左 | 值 中 | 复制按钮行末（右对齐）
        var panel = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lbl = new TextBlock
        {
            Text = label + ": ",
            FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);
        panel.Children.Add(lbl);
        if (isFileLink)
        {
            var link = MakeFileLink(value);
            Grid.SetColumn(link, 1);
            panel.Children.Add(link);
        }
        else
        {
            var tb = new TextBox
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.IsReadOnlyCaretVisible = true;
            Grid.SetColumn(tb, 1);
            panel.Children.Add(tb);
        }
        var copyBtn = MakeCopyButton(label + ": " + value);
        copyBtn.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(copyBtn, 2);
        panel.Children.Add(copyBtn);
        _detailLines.Add(label + ": " + value);
        DetailPanel.Children.Add(panel);
    }

    /// <summary>行复制按钮（单独复制该值）。</summary>
    private Button MakeCopyButton(string text)
    {
        var btn = new Button
        {
            Content = _loc.Get("moddict.detail_copy"),
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        btn.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(text); } catch { }
        };
        return btn;
    }

    /// <summary>文件链接：蓝色可点击——点击用系统默认程序打开文件。</summary>
    private TextBlock MakeFileLink(string relPath)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(relPath))
        {
            ToolTip = relPath
        };
        link.Click += (_, _) => OpenFile(relPath);
        tb.Inlines.Add(link);
        return tb;
    }

    private void OpenFile(string relPath)
    {
        try
        {
            string abs = relPath;
            var root = _services.Adapter?.GetFileRoot(relPath);
            if (!string.IsNullOrEmpty(root))
                abs = System.IO.Path.Combine(root, relPath);
            if (!System.IO.File.Exists(abs))
            {
                StatusText.Text = _loc.Get("moddict.file_not_found") + " " + abs;
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(abs)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = _loc.Get("moddict.file_open_failed") + " " + ex.Message;
        }
    }

    private void AddDetailSection(string label, IEnumerable<string> items, bool files = false)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return;
        AddDetailRow(label, list.Count.ToString());
        foreach (var item in list.Take(10))
        {
            if (files)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 1) };
                row.Children.Add(new TextBlock { Text = "  • " });
                row.Children.Add(MakeFileLink(item));
                DetailPanel.Children.Add(row);
            }
            else
            {
                DetailPanel.Children.Add(new TextBlock
                {
                    Text = "  • " + item,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 1)
                });
            }
        }
        if (list.Count > 10)
            DetailPanel.Children.Add(new TextBlock
            {
                Text = "  …（共 " + list.Count + " 项）",
                Foreground = System.Windows.Media.Brushes.Gray
            });
    }

    private void FillBaseDetail(StaticModifierEngine.BaseModifier be)
    {
        AddDetailRow(_loc.Get("moddict.column_name"), be.Name);
        AddDetailRow(_loc.Get("moddict.detail_loc_key"), be.LocKey ?? be.ModKey);   // 真实键原样（用户 2026-08：不拼 mod_）
        // 定义来源：静态 / 自定义分开（不混合），每定义一行 + 实际启用文件（使用中）
        foreach (var src in be.DefinitionSources)
        {
            var active = be.GetActiveFile(src);
            AddDetailRow(_loc.Get("moddict.detail_def_source"),
                LocalizeSource(src) + (active != null ? "  (" + active + ") " + _loc.Get("moddict.detail_active") : ""),
                isFileLink: active != null);
        }
        // 本地化：本地化组件（只读、语种切换、**无描述**——用户 2026-08 全部复用组件；
        // **真实本地化键 = LocKey**（引擎扫描记录原样大小写——不拼 mod_+Name），先找到再传入）
        var locKey = be.LocKey ?? be.ModKey;
        var locBox = new Controls.LocalisationEditBox
        {
            Adapter = _services.Adapter,
            GetNameKey = () => be.LocKey ?? be.ModKey,
            GetDescKey = () => (be.LocKey ?? be.ModKey) + "_desc",
            ShowDescription = false,          // 只有名称一对
            NameLogicalReadOnly = true,
            DescLogicalReadOnly = true,
            GetLangs = () => KeyLangs(be.LocKey ?? be.ModKey)
        };
        locBox.Reload();
        DetailPanel.Children.Add(locBox);
        AddDetailSection(_loc.Get("moddict.detail_users"), be.Users.Select(u => u.Name));
        AddDetailSection(_loc.Get("moddict.detail_external"), be.ExternalFiles, files: true);
    }

    private void FillCustomDetail(StaticModifierEngine.StaticModifierEntry ce)
    {
        AddDetailRow(_loc.Get("moddict.column_name"), ce.Name);
        AddDetailRow(_loc.Get("moddict.detail_hidden"), ce.Hidden ? _loc.Get("common.yes") : _loc.Get("common.no"));
        if (ce.Important)
            AddDetailRow(_loc.Get("moddict.detail_important"), _loc.Get("common.yes"));
        if (ce.IconFrame != 0)
            AddDetailRow(_loc.Get("moddict.detail_icon_frame"), ce.IconFrame.ToString());
        if (!string.IsNullOrEmpty(ce.CustomTooltip))
            AddDetailRow(_loc.Get("moddict.detail_custom_tooltip"), ce.CustomTooltip!);
        if (ce.ShowOnlyCustomTooltip)
            AddDetailRow(_loc.Get("moddict.detail_show_only_tooltip"), _loc.Get("common.yes"));
        AddDetailRow(_loc.Get("moddict.detail_icon"), ce.Icon ?? "");
        AddDetailRow(_loc.Get("moddict.detail_source"), ce.SourceFile ?? "", isFileLink: true);
        // 本地化（核心，放上面）：名称 → 本地化组件（只读、语种切换、**无描述**——用户 2026-08 全部复用组件；
        // 静态加成本地化键 = Name（不带 mod_ 前缀）；语种下拉只显示有词条的语种）
        var locBox = new Controls.LocalisationEditBox
        {
            Adapter = _services.Adapter,
            GetNameKey = () => ce.Name,
            GetDescKey = () => ce.Name + "_desc",
            ShowDescription = false,          // 只有名称一对
            NameLogicalReadOnly = true,
            DescLogicalReadOnly = true,
            GetLangs = () => KeyLangs(ce.Name)
        };
        locBox.Reload();
        DetailPanel.Children.Add(locBox);
        // 附加（次要，放本地化下面）：自定义提示的各语种翻译（复用本地化组件——只读；用户 2026-08：核心上、次要下）
        if (!string.IsNullOrEmpty(ce.CustomTooltip))
        {
            AddDetailRow(_loc.Get("moddict.detail_custom_tooltip"), ce.CustomTooltip!);
            var ttLoc = new Controls.LocalisationEditBox
            {
                Adapter = _services.Adapter,
                GetNameKey = () => ce.CustomTooltip ?? "",
                GetDescKey = () => (ce.CustomTooltip ?? "") + "_desc",
                ShowDescription = false,          // 只有名称一对
                NameLogicalReadOnly = true,       // 详情只读
                GetLangs = () => KeyLangs(ce.CustomTooltip ?? "")
            };
            ttLoc.Reload();
            DetailPanel.Children.Add(ttLoc);
        }
        AddDetailSection(_loc.Get("moddict.detail_bases"),
            ce.BaseRefs.Select(r => r.Key + " = " + r.Value));
        AddDetailSection(_loc.Get("moddict.detail_unknown"), ce.UnknownKeys);
    }

    /// <summary>定义来源中文化：static → 静态、scripted → 自定义（不在详情留英文）。</summary>
    private string LocalizeSource(string source)
        => string.Equals(source, "static", StringComparison.OrdinalIgnoreCase)
            ? _loc.Get("moddict.type_custom")          // 静态
            : string.Equals(source, "scripted", StringComparison.OrdinalIgnoreCase)
                ? _loc.Get("moddict.type_custom_base") // 自定义
                : source;

    /// <summary>本地化显示行：语种译名（不是语言 key）+ 值；当前界面语种排最前。</summary>
    private IEnumerable<string> LocalisationDisplayLines(SortedDictionary<string, string> localisations)
    {
        string cur = _loc.CurrentLanguage;
        string gameCur = GameLangFromUI(cur);
        return localisations
            .OrderByDescending(kv => string.Equals(kv.Key, gameCur, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.Key, cur, StringComparison.OrdinalIgnoreCase))
            .Select(kv => _loc.GetLanguageDisplayNameLocalized(kv.Key) + ": " + kv.Value);
    }

    /// <summary>按本地化键查各语种逻辑值（custom_tooltip 翻译展示用——引擎未存，临时从 Adapter 查）。</summary>
    private SortedDictionary<string, string> KeyLocalisations(string key)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var langs = _services.Adapter?.GetLocalisationLanguages() ?? new List<string>();
        foreach (var lang in langs)
        {
            var v = _services.Adapter?.GetLocalisedLogicalText(key, lang);
            if (!string.IsNullOrWhiteSpace(v))
                d[lang] = v!;
        }
        return d;
    }

    /// <summary>某键**有词条**的语种（本地化组件语种下拉只显示这些——用户 2026-08：没有的语种不显示，如葡萄牙语；
    /// **空串词条也算有**——GetLocalisedText 无词条返回 null、空串返回 ""，用 != null 判断，用户 2026-08）。</summary>
    private List<string> KeyLangs(string key)
    {
        var adapter = _services.Adapter;
        if (adapter == null || string.IsNullOrEmpty(key))
            return new List<string>();
        return adapter.GetLocalisationLanguages()
            .Where(l => adapter.GetLocalisedText(key, l) != null)
            .ToList();
    }
}
