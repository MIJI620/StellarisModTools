// 文件: Stellaris.Editor/Pages/DictionaryIndexPage.cs
// 索引页（用户 2026-08）：语言字典 + 加成字典 + 图形索引合并为一个页面，
// 内部 3 个选项卡（"语言" / "加成" / "图形"），各自保留自己的搜索框（搜索逻辑不同，不共用）。
// 三页**列宽同步**（用户 2026-08）：左导航/中列表/右详情两个分隔条拖动的列宽，任意页拖动 → 同步到其余两页
// （三页同构：左 150 / 分隔条 / 中 2* / 分隔条 / 右 3*）。

using System;
using System.Windows;
using System.Windows.Controls;

namespace Stellaris.Editor.Pages;

public sealed class DictionaryIndexPage : UserControl
{
    private readonly LanguageDictionaryPage _lang;
    private readonly ModifierDictionaryPage _bonus;
    private readonly SpriteIndexPage _sprite;

    public DictionaryIndexPage(EngineServices services)
    {
        _lang = new LanguageDictionaryPage(services);
        _bonus = new ModifierDictionaryPage(services);
        _sprite = new SpriteIndexPage(services);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem
        {
            Header = services.Localisation.Get("nav.dictionary_lang"),
            Content = _lang
        });
        tabs.Items.Add(new TabItem
        {
            Header = services.Localisation.Get("nav.dictionary_bonus"),
            Content = _bonus
        });
        tabs.Items.Add(new TabItem
        {
            Header = services.Localisation.Get("nav.dictionary_sprite"),
            Content = _sprite
        });
        Content = tabs;
        AttachWidthSync();   // 三页列宽同步（用户 2026-08）
    }

    /// <summary>三页各自的列宽（左导航/中列表/右详情——两个分隔条拖动）调整**通用**：任意页拖分隔条 → 同步到其余两页。</summary>
    private void AttachWidthSync()
    {
        foreach (var page in AllPageRoots())
            foreach (var child in page.Children)
                if (child is GridSplitter sp)
                    sp.DragCompleted += (_, _) => SyncColumns(page);
    }

    private void SyncColumns(Grid from)
    {
        foreach (var g in AllPageRoots())
        {
            if (ReferenceEquals(g, from))
                continue;
            int n = Math.Min(from.ColumnDefinitions.Count, g.ColumnDefinitions.Count);
            for (int i = 0; i < n; i++)
                g.ColumnDefinitions[i].Width = from.ColumnDefinitions[i].Width;   // 拖动后被拖列已转像素，同步像素/star 一致
        }
    }

    private System.Collections.Generic.IEnumerable<Grid> AllPageRoots()
    {
        if (_lang.Content is Grid g1) yield return g1;
        if (_bonus.Content is Grid g2) yield return g2;
        if (_sprite.Content is Grid g3) yield return g3;
    }
}
