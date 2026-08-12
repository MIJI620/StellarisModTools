// 文件: Stellaris.Editor/Pages/SpriteIndexPage.cs
// 图形索引页（目录索引第 3 个选项卡，用户 2026-08）：
// - 数据 = SpriteManagementEngine 子图形索引（注册键）+ gfx/ 目录递归扫描的 .dds 相对路径（只记 .dds）；
// - 左侧导航 3 选项：全部 / 注册键 / 路径；
// - 列表只显示注册键 或 相对路径——被注册键（texturefile）引用的 .dds 不单独显示；
// - 详情：注册键（如有）+ 相对路径 + 图像预览：注册键按 NoOfFrames 切帧垂直排列、文件整图，
//   横向宽度占满、纵向靠滚动条。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stellaris.Engine.ImageAsset;

namespace Stellaris.Editor.Pages;

public sealed class SpriteIndexPage : UserControl
{
    private readonly EngineServices _services;
    private readonly UILocalisationManager _loc;

    private ListBox _nav = null!;          // 全部 / 注册键 / 路径
    private DataGrid _grid = null!;
    private StackPanel _detailPanel = null!;
    private string _navFilter = "all";
    private List<SpriteRow> _rows = new();

    private sealed class SpriteRow
    {
        public string Type { get; set; } = "";      // 类型显示（注册 / 路径）
        public string TypeTag { get; set; } = "";   // registered / path
        public string Name { get; set; } = "";      // 注册键 或 相对路径
        public string? Key { get; set; }            // 注册键（路径条目 null）
        public string? Path { get; set; }           // 贴图相对路径（注册键条目 = TextureFile；路径条目 = 自身）
    }

    public SpriteIndexPage(EngineServices services)
    {
        _services = services;
        _loc = services.Localisation;
        Build();
    }

    private void Build()
    {
        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

        // 左导航：全部 / 注册键 / 路径
        _nav = new ListBox { Background = Brushes.White };
        _nav.Items.Add(new ListBoxItem { Content = _loc.Get("sprite.nav_all"), Tag = "all" });
        _nav.Items.Add(new ListBoxItem { Content = _loc.Get("sprite.nav_registered"), Tag = "registered" });
        _nav.Items.Add(new ListBoxItem { Content = _loc.Get("sprite.nav_path"), Tag = "path" });
        _nav.SelectedIndex = 0;
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is ListBoxItem it)
            {
                _navFilter = it.Tag as string ?? "all";
                ApplyFilter();
            }
        };
        Grid.SetColumn(_nav, 0);
        grid.Children.Add(_nav);

        // 中列表：类型 + 名称
        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeaderWidth = 0,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            CanUserSortColumns = false,
            Background = Brushes.White
        };
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = _loc.Get("sprite.column_type"),
            Binding = new System.Windows.Data.Binding("Type"),
            Width = new DataGridLength(60)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = _loc.Get("sprite.column_name"),
            Binding = new System.Windows.Data.Binding("Name"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        _grid.SelectionChanged += (_, _) => OnSelectionChanged();
        Grid.SetColumn(_grid, 2);
        grid.Children.Add(_grid);

        // 右详情：滚动 + 垂直排列（图像横向占满、纵向滚动）
        var detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _detailPanel = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        detailScroll.Content = _detailPanel;
        Grid.SetColumn(detailScroll, 4);
        grid.Children.Add(detailScroll);

        Content = grid;
        BuildRows();
    }

    // ==================== 数据 ====================

    private void BuildRows()
    {
        _rows.Clear();
        var engine = _services.SpriteEngine;
        if (engine == null)
            return;
        // 注册键条目（全部 spriteType）
        foreach (var kv in engine.GetAllSpriteNames())
        {
            var def = engine.GetSpriteDefinition(kv.Key);
            _rows.Add(new SpriteRow
            {
                Type = _loc.Get("sprite.type_registered"),
                TypeTag = "registered",
                Name = kv.Key,
                Key = kv.Key,
                Path = def?.TextureFile
            });
        }
        // 路径条目：gfx/ 下 .dds 中**未被任何注册键 texturefile 引用**的（被引用不单独显示——用户 2026-08）
        var referenced = engine.GetReferencedTextureFiles();
        foreach (var p in engine.GetGfxDdsFiles())
        {
            if (referenced.Contains(p))
                continue;
            _rows.Add(new SpriteRow
            {
                Type = _loc.Get("sprite.type_path"),
                TypeTag = "path",
                Name = p,
                Path = p
            });
        }
        _rows = _rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var shown = _navFilter == "all"
            ? _rows
            : _rows.Where(r => r.TypeTag == _navFilter).ToList();
        _grid.ItemsSource = shown;
        if (_grid.Items.Count > 0)
            _grid.SelectedIndex = 0;   // 默认选中第一项
        else
            ShowNoSelection();
    }

    private void ShowNoSelection()
    {
        _detailPanel.Children.Clear();
        _detailPanel.Children.Add(new TextBlock
        {
            Text = _loc.Get("sprite.no_selection"),
            Foreground = Brushes.Gray
        });
    }

    // ==================== 详情 ====================

    private void OnSelectionChanged()
    {
        _detailPanel.Children.Clear();
        if (_grid.SelectedItem is not SpriteRow row)
        {
            ShowNoSelection();
            return;
        }
        if (row.Key != null)
            AddDetailRow(_loc.Get("sprite.detail_registered"), row.Key);
        if (!string.IsNullOrEmpty(row.Path))
            AddDetailRow(_loc.Get("sprite.detail_source"), row.Path!);
        AddImagePreview(row);
    }

    private void AddDetailRow(string label, string value)
    {
        var panel = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.Children.Add(new TextBlock
        {
            Text = label + ": ",
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        });
        var tb = new TextBox
        {
            Text = value,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        };
        tb.IsReadOnlyCaretVisible = true;
        Grid.SetColumn(tb, 1);
        panel.Children.Add(tb);
        _detailPanel.Children.Add(panel);
    }

    /// <summary>图像预览：注册键 → QuerySprite 帧切分垂直排列；路径 → 整图。横向占满（Stretch=Uniform）、纵向滚动。</summary>
    private void AddImagePreview(SpriteRow row)
    {
        try
        {
            if (row.Key != null && _services.SpriteEngine != null)
            {
                using var qr = _services.SpriteEngine.QuerySprite(row.Key);
                if (qr.Found)
                    foreach (var f in qr.Frames)
                        _detailPanel.Children.Add(MakeImage(PixelSetToSource(f.PixelData)));
            }
            else if (row.Path != null && _services.ImageEngine != null)
            {
                _services.ImageEngine.LoadImage(row.Path);
                var ps = _services.ImageEngine.Result;
                if (ps != null)
                    _detailPanel.Children.Add(MakeImage(PixelSetToSource(ps)));
            }
        }
        catch
        {
            // 图像加载失败不阻塞详情（文件缺失/损坏）
        }
    }

    private static Image MakeImage(BitmapSource? src)
    {
        var img = new Image
        {
            Source = src,
            Stretch = Stretch.Uniform,                 // 横向占满、高度按比例
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 2)
        };
        if (src == null)
            img.Visibility = Visibility.Collapsed;
        return img;
    }

    /// <summary>PixelSet → Bgra32 BitmapSource（拷贝像素，独立于 PixelSet 生命周期）。</summary>
    private static BitmapSource PixelSetToSource(PixelSet ps)
    {
        int w = ps.Width, h = ps.Height;
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                var p = ps.Data[y][x];
                px[idx] = p[2];       // B
                px[idx + 1] = p[1];   // G
                px[idx + 2] = p[0];   // R
                px[idx + 3] = p.Length >= 4 ? p[3] : (byte)255;
            }
        }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        src.Freeze();
        return src;
    }
}
