// 文件: Stellaris.Editor/Pages/TechCardControl.cs
// 科技卡片控件（节点模式）：WPF 原生控件卡——标题条（Area 色）+ 深灰底板 + 边框 +
// 描述（自动换行）+ 加成列表（左 key 右数值）+ cost + 图标。
// 支持后续交互：点击/悬停/选中高亮（BorderBrush 切换）；矢量文字缩放清晰。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stellaris.Engine.Technology;

namespace Stellaris.Editor.Pages;

public sealed class TechCardControl : Border
{
    private static readonly SolidColorBrush TextLight = new(Color.FromRgb(0xEA, 0xEA, 0xEA));
    private static readonly SolidColorBrush TextDim = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly SolidColorBrush ModLine = new(Color.FromRgb(0x55, 0x55, 0x66));
    private static readonly SolidColorBrush BoardBg = new(Color.FromRgb(0x2A, 0x2A, 0x34));

    private static readonly SolidColorBrush TitlePhysics = new(Color.FromRgb(0x3A, 0x6E, 0xA5));
    private static readonly SolidColorBrush TitleSociety = new(Color.FromRgb(0x3F, 0x7D, 0x51));
    private static readonly SolidColorBrush TitleEngineering = new(Color.FromRgb(0xB0, 0x8D, 0x2E));
    private static readonly SolidColorBrush TitleOther = new(Color.FromRgb(0x55, 0x55, 0x60));

    private static readonly SolidColorBrush BorderNormal = new(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly SolidColorBrush BorderRare = new(Color.FromRgb(0x9B, 0x59, 0xB6));
    private static readonly SolidColorBrush BorderDanger = new(Color.FromRgb(0xC0, 0x39, 0x2B));

    public TechNode Tech { get; }

    /// <summary>点击事件（页面层挂接：选中节点 + 相连线高亮）。</summary>
    public event Action<TechCardControl>? Clicked;

    /// <summary>选中高亮（后续交互：选中/悬停时切换边框色）。</summary>
    public bool IsHighlighted
    {
        get => _highlighted;
        set
        {
            _highlighted = value;
            UpdateBorder();
        }
    }
    private bool _highlighted;
    private readonly SolidColorBrush _baseBorder;
    private static readonly SolidColorBrush HighlightBrush = new(Color.FromRgb(0x4F, 0xC3, 0xF7));

    public TechCardControl(TechNode tech, string title, string desc,
        IReadOnlyList<(string Display, string Value)> mods, string cost,
        ImageSource? icon, ImageSource? catIcon, double fontSize)
    {
        Tech = tech;
        Width = TechnologyLayout.CardWidth;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(4);   // 用户：节点边框太细 → 加粗到 4px
        _baseBorder = tech.IsDangerous ? BorderDanger : tech.IsRare ? BorderRare : BorderNormal;
        BorderBrush = _baseBorder;
        Background = BoardBg;
        ClipToBounds = true;
        // ⚠️ 不再用 CacheMode=BitmapCache：上千卡片各自光栅化位图，加载/滚动卡死（用户反馈"加载不出来/像死循环"）
        // 顶部/底部统一圆角：CornerRadius 只裁 Border 背景，顶部 titleBar（矩形）会盖住顶部圆角
        // （用户反馈"顶部没有圆角"）——用 Clip 圆角矩形统一裁剪全部子元素；圆角 10 上下一致（用户：标题圆角太小）
        SizeChanged += (_, _) =>
        {
            if (ActualWidth > 0 && ActualHeight > 0)
                Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 10, 10);
        };

        var dock = new DockPanel();

        // 标题条（**底色 = 学科色不变**——用户：底色不让改；圆角 10 顶部——标题色块自身圆角，否则盖住顶部圆角）
        var titleBar = new Border
        {
            Height = 26,
            Background = TitleColor(tech.Area),
            CornerRadius = new CornerRadius(10, 10, 0, 0)
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        var titleText = new TextBlock
        {
            Text = title,
            Margin = new Thickness(8, 3, 8, 0),
            FontSize = fontSize + 1,
            FontWeight = FontWeights.Bold,
            // **文字颜色按稀有度**（用户：改文字颜色——危险=红、稀有=紫、常规=白）
            Foreground = tech.IsDangerous ? new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B))
                : tech.IsRare ? new SolidColorBrush(Color.FromRgb(0x9B, 0x59, 0xB6))
                : TextLight,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleBar.Child = titleText;
        dock.Children.Add(titleBar);

        // 内容区：Row0 = 图标 + 描述 + cost；Row1 = 加成列表
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });      // 图标
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 描述
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });      // cost 区

        // Row0: 图标 / 描述 / cost
        if (icon != null)
        {
            var iconImg = new Image
            {
                Source = icon,
                Width = 44,
                Height = 44,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(6, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(iconImg, 0);
            Grid.SetColumn(iconImg, 0);
            grid.Children.Add(iconImg);
        }
        var descText = new TextBlock
        {
            Text = desc,
            Margin = new Thickness(6, 8, 4, 0),
            FontSize = fontSize - 1,
            Foreground = TextLight,
            TextWrapping = TextWrapping.Wrap   // 完整显示（不限行数，卡片高度按实际行数）
        };
        Grid.SetRow(descText, 0);
        Grid.SetColumn(descText, 1);
        grid.Children.Add(descText);

        var costPanel = new DockPanel { Margin = new Thickness(0, 6, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var costText = new TextBlock
        {
            Text = cost,
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = TextLight,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(costText, Dock.Right);
        costPanel.Children.Add(costText);
        if (catIcon != null)
        {
            var catImg = new Image
            {
                Source = catIcon,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 0)
            };
            DockPanel.SetDock(catImg, Dock.Right);
            costPanel.Children.Add(catImg);
        }
        Grid.SetRow(costPanel, 0);
        Grid.SetColumn(costPanel, 2);
        grid.Children.Add(costPanel);

        // Row1: 加成列表（分隔线 + 每行：左 Display 右 Value）；底部空 1 行 = 加成字号（用户字号 -1）
        var modsPanel = new StackPanel { Margin = new Thickness(8, 4, 8, fontSize - 1) };
        if (mods.Count > 0)
        {
            modsPanel.Children.Add(new Border
            {
                Height = 1,
                Background = ModLine,
                Margin = new Thickness(0, 2, 0, 4)
            });
            foreach (var (display, value) in mods)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new TextBlock
                {
                    Text = display,
                    FontSize = fontSize - 1,   // 加成字号 = 用户字号 - 1
                    Foreground = TextDim,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 4, 1)
                };
                var right = new TextBlock
                {
                    Text = value,
                    FontSize = fontSize - 1,   // 加成字号 = 用户字号 - 1
                    Foreground = TextLight,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(left, 0);
                Grid.SetColumn(right, 1);
                row.Children.Add(left);
                row.Children.Add(right);
                modsPanel.Children.Add(row);
            }
        }
        Grid.SetRow(modsPanel, 1);
        Grid.SetColumnSpan(modsPanel, 3);
        grid.Children.Add(modsPanel);

        dock.Children.Add(grid);
        Child = dock;

        // 点击事件（卡片有背景，左键可命中）
        MouseLeftButtonDown += (_, _) => Clicked?.Invoke(this);
    }

    private static SolidColorBrush TitleColor(string area)
    {
        if (string.Equals(area, "physics", StringComparison.OrdinalIgnoreCase)) return TitlePhysics;
        if (string.Equals(area, "society", StringComparison.OrdinalIgnoreCase)) return TitleSociety;
        if (string.Equals(area, "engineering", StringComparison.OrdinalIgnoreCase)) return TitleEngineering;
        return TitleOther;
    }

    private void UpdateBorder()
        => BorderBrush = _highlighted ? HighlightBrush : _baseBorder;
}
