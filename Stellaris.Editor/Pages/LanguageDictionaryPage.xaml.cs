// 文件: Stellaris.Editor/Pages/LanguageDictionaryPage.xaml.cs
// 语言字典页（只读）：语种（自选/全部）+ 键/值 下拉 + 正则匹配 → 表格显示 key + 显示值。
// 右键条目 → 详情（key / 显示值 / 逻辑值 / 绝对位置）。
// 正则仅限本页使用（用户授权：语言字典引擎只读）。

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Stellaris.Engine.Localisation;

namespace Stellaris.Editor.Pages;

public partial class LanguageDictionaryPage : UserControl
{
    private readonly EngineServices _services;
    private readonly UILocalisationManager _loc;
    private System.Windows.Threading.DispatcherTimer _patternDebounce = null!;   // 搜索框 2 秒防抖

    public LanguageDictionaryPage(EngineServices services)
    {
        _services = services;
        _loc = services.Localisation;
        InitializeComponent();
        _patternDebounce = Stellaris.Editor.Controls.SearchDebouncer.Attach(PatternBox, () => OnSearch(this, new RoutedEventArgs()));

        // 语种列转换器：语言 key → 当前 UI 语种下的译名（跟随界面语言）
        Resources["LangName"] = new LanguageNameConverter(_loc);

        SearchButton.Content = _loc.Get("langdict.search");
        // 区分大小写：Aa 带下划线图标（悬停提示文字——省空间）
        CaseSensitiveBox.Content = new TextBlock
        {
            Text = "Aa",
            TextDecorations = TextDecorations.Underline,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        };
        CaseSensitiveBox.ToolTip = _loc.Get("langdict.case_sensitive");

        // 列头本地化（随界面语言）
        if (ResultGrid.Columns.Count >= 3)
        {
            ResultGrid.Columns[0].Header = _loc.Get("langdict.column_language");
            ResultGrid.Columns[1].Header = _loc.Get("langdict.column_key");
            ResultGrid.Columns[2].Header = _loc.Get("langdict.column_display");
        }

        // 键/值 共用下拉：Key 或 显示值
        FieldCombo.Items.Add(new ComboBoxItem { Content = _loc.Get("langdict.column_key"), Tag = "key" });
        FieldCombo.Items.Add(new ComboBoxItem { Content = _loc.Get("langdict.column_display"), Tag = "value" });
        FieldCombo.SelectedIndex = 0;

        // 语种导航（竖排 ListBox——类似左侧导航栏）：全部 + 各语言（显示当前界面语言下的译名）
        var engine = _services.DictionaryEngine;
        LangNav.Items.Add(new ListBoxItem { Content = _loc.Get("langdict.all_languages"), Tag = "*" });
        var defLang = _services.Localisation.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "simp_chinese" : "english";
        int defIdx = 0;
        if (engine != null)
        {
            foreach (var lang in engine.GetLanguages())
            {
                LangNav.Items.Add(new ListBoxItem
                {
                    Content = _loc.GetLanguageDisplayNameLocalized(lang),
                    Tag = lang
                });
                if (string.Equals(lang, defLang, StringComparison.OrdinalIgnoreCase))
                    defIdx = LangNav.Items.Count - 1;
            }
        }
        // 默认选中用户选择的语种（如用户选中文 → 简体中文）
        LangNav.SelectedIndex = defIdx;
        // 初始显示全部（无搜索——显示当前语种全部）
        OnSearch(this, new RoutedEventArgs());

    }

    private string? _displayLang;              // 当前语言导航选择（null=全部）
    private List<LocalisationEntryView> _lastRows = new();   // 最近一次搜索结果（全部语言）

    private void OnLangNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LangNav.SelectedItem is not ListBoxItem it)
            return;
        var tag = it.Tag as string;
        _displayLang = tag == "*" ? null : tag;
        // 重新按 key 合并显示（不重新搜索）
        ApplyDisplay();
    }

    /// <summary>合并显示：相同本地化键只显示一个条目（用户 2026-08）——
    /// 优先取"当前导航语言 ?? 用户界面语言"的条目，无则任意语言第一行（按语言名序）。</summary>
    private void ApplyDisplay()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string pref = _displayLang ?? (_loc.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "simp_chinese" : "english");
        var ordered = _lastRows
            .OrderByDescending(v => string.Equals(v.Language, pref, StringComparison.OrdinalIgnoreCase))
            .ThenBy(v => v.Language, StringComparer.Ordinal);
        var merged = ordered.Where(v => seen.Add(v.Key)).ToList();
        ResultGrid.ItemsSource = merged;
        StatusText.Text = string.Format(_loc.Get("langdict.result_count"), merged.Count);
    }


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
        var engine = _services.DictionaryEngine;
        if (engine == null)
        {
            StatusText.Text = _loc.Get("langdict.engine_unavailable");
            return;
        }

        // 搜索默认全部语言（language=null，任意语言匹配——用户 2026-08），再按 key 合并显示
        string? field = (FieldCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        string pattern = PatternBox.Text;
        try
        {
            bool ignoreCase = CaseSensitiveBox.IsChecked != true;
            var results = field == "value"
                ? engine.Query(null, null, pattern, ignoreCase)
                : engine.Query(null, pattern, null, ignoreCase);
            _lastRows = results;
            ApplyDisplay();
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
            ResultGrid.ItemsSource = null;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailPanel.Children.Clear();
        if (ResultGrid.SelectedItem is not LocalisationEntryView v)
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = _loc.Get("langdict.no_selection"),
                Foreground = System.Windows.Media.Brushes.Gray
            });
            return;
        }
        AddDetailRow(_loc.Get("langdict.column_key"), v.Key);
        // 本地化组件：语种下拉切换看不同语种的翻译（只读——语言字典为只读页；**无描述一对**——用户 2026-08；
        // 下拉只显示**该键有词条的语种**——没有的语种不显示，用户 2026-08）
        var locBox = new Controls.LocalisationEditBox
        {
            Adapter = _services.Adapter,
            GetNameKey = () => v.Key,
            GetDescKey = () => v.Key + "_desc",
            ShowDescription = false,          // 只有名称一对（逻辑值 + 显示值）
            NameLogicalReadOnly = true,
            DescLogicalReadOnly = true,
            GetLangs = () =>
            {
                var adapter = _services.Adapter;
                if (adapter == null)
                    return new List<string>();
                // 空串词条也算有（无词条返回 null、空串返回 ""——用 != null，用户 2026-08）
                return adapter.GetLocalisationLanguages()
                    .Where(l => adapter.GetLocalisedText(v.Key, l) != null)
                    .ToList();
            }
        };
        locBox.Reload();
        DetailPanel.Children.Add(locBox);
        AddDetailRow(_loc.Get("langdict.absolute_path"), v.AbsolutePath);
    }

    /// <summary>详情行（label + 只读可复制 TextBox——行末复制按钮，与加成字典一致）。</summary>
    private void AddDetailRow(string label, string value)
    {
        var panel = new Grid { Margin = new Thickness(0, 2, 0, 2) };
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
        var tb = new TextBox
        {
            Text = value ?? string.Empty,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        };
        tb.IsReadOnlyCaretVisible = true;
        Grid.SetColumn(tb, 1);
        panel.Children.Add(tb);
        var copy = new Button
        {
            Content = _loc.Get("moddict.detail_copy"),
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        copy.Click += (_, _) => { try { System.Windows.Clipboard.SetText(label + ": " + value); } catch { } };
        Grid.SetColumn(copy, 2);
        panel.Children.Add(copy);
        DetailPanel.Children.Add(panel);
    }
}