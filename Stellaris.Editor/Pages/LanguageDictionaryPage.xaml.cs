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

    public LanguageDictionaryPage(EngineServices services)
    {
        _services = services;
        _loc = services.Localisation;
        InitializeComponent();

        LanguageLabel.Text = _loc.Get("langdict.language");
        FieldLabel.Text = _loc.Get("langdict.field");
        SearchButton.Content = _loc.Get("langdict.search");
        CaseSensitiveBox.Content = _loc.Get("langdict.case_sensitive");
        DetailMenuItem.Header = _loc.Get("langdict.detail");

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

        // 语种下拉：全部 + 各语言（显示当前界面语言下的译名）
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem
        {
            Content = _loc.Get("langdict.all_languages"),
            Tag = "*"
        });
        var engine = _services.DictionaryEngine;
        if (engine != null)
        {
            foreach (var lang in engine.GetLanguages())
                LanguageCombo.Items.Add(new ComboBoxItem
                {
                    Content = _loc.GetLanguageDisplayNameLocalized(lang),
                    Tag = lang
                });
        }
        LanguageCombo.SelectedIndex = 0;

        ResultGrid.ContextMenu!.Opened += (_, _) =>
            DetailMenuItem.IsEnabled = ResultGrid.SelectedItem is LocalisationEntryView;
    }

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        var engine = _services.DictionaryEngine;
        if (engine == null)
        {
            StatusText.Text = _loc.Get("langdict.engine_unavailable");
            return;
        }

        string? lang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        string? field = (FieldCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        string pattern = PatternBox.Text;
        try
        {
            bool ignoreCase = CaseSensitiveBox.IsChecked != true;
            var results = field == "value"
                ? engine.Query(lang, null, pattern, ignoreCase)
                : engine.Query(lang, pattern, null, ignoreCase);
            ResultGrid.ItemsSource = results;
            StatusText.Text = string.Format(_loc.Get("langdict.result_count"), results.Count);
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
            ResultGrid.ItemsSource = null;
        }
    }

    private void OnShowDetail(object sender, RoutedEventArgs e)
    {
        if (ResultGrid.SelectedItem is not LocalisationEntryView v)
            return;
        new EntryDetailWindow(_loc, v).ShowDialog();
    }
}

/// <summary>条目详情窗口：4 个重要值（键/值/逻辑值/绝对位置）以只读文本框展示，可选中复制。</summary>
public sealed class EntryDetailWindow : Window
{
    public EntryDetailWindow(UILocalisationManager loc, LocalisationEntryView v)
    {
        Title = loc.Get("langdict.detail_title");
        Width = 560;
        Height = 320;
        MinWidth = 420;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(12) };
        AddRow(panel, loc.Get("langdict.column_key"), v.Key);
        AddRow(panel, loc.Get("langdict.column_language"), v.Language);
        AddRow(panel, loc.Get("langdict.column_display"), v.DisplayValue);
        AddRow(panel, loc.Get("langdict.logical_value"), v.LogicalValue);
        AddRow(panel, loc.Get("langdict.absolute_path"), v.AbsolutePath);
        scroll.Content = panel;
        Content = scroll;
    }

    private static void AddRow(StackPanel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 6, 0, 2)
        });
        panel.Children.Add(new TextBox
        {
            Text = value ?? string.Empty,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 22,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
    }
}
