// 文件: Stellaris.Editor/Controls/ColorPickerControl.xaml.cs
// 可复用颜色选择控件（PS 风格）：
//   底部水平色相滑块 + 左侧垂直明度滑块（上白下黑）+ 右侧垂直透明度滑块
//   （上不透明下透明）+ RGBA 四个输入框 + 预览。点"OK"才提交
//   （更新 SelectedColorText 并触发 ColorChanged）。Title 由调用方传入。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stellaris.Editor.Controls;
public partial class ColorPickerControl : UserControl
{
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ColorPickerControl),
            new PropertyMetadata(string.Empty));

    public string SelectedColorText
    {
        get => (string)GetValue(SelectedColorTextProperty);
        set => SetValue(SelectedColorTextProperty, value);
    }

    public static readonly DependencyProperty SelectedColorTextProperty =
        DependencyProperty.Register(nameof(SelectedColorText), typeof(string), typeof(ColorPickerControl),
            new PropertyMetadata("#FF000000", OnColorTextChanged));

    /// <summary>选色确认事件（参数为新 ARGB hex 字符串）。</summary>
    public event EventHandler<string>? ColorChanged;

    private bool _updating;
    private UILocalisationManager? _loc;

    /// <summary>应用本地化文本（R/G/B/A 标签、选项卡、按钮等）；由调用方传入。</summary>
    public void ApplyLocalisation(UILocalisationManager loc)
    {
        _loc = loc;
        RLabel.Text = loc.Get("color.r");
        GLabel.Text = loc.Get("color.g");
        BLabel.Text = loc.Get("color.b");
        ALabel.Text = loc.Get("color.a");
        ColorTab.Header = loc.Get("color.tab.color");
        GrayTab.Header = loc.Get("color.tab.gray");
        OkButton.Content = loc.Get("color.ok");
        GrayTitle.Text = loc.Get("color.gray_brightness");
        GrayAlphaLabel.Text = loc.Get("color.gray_alpha");
    }

    public ColorPickerControl()
    {
        InitializeComponent();

        HueGradient.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            {
                new GradientStop(Colors.Red, 0.0),
                new GradientStop(Colors.Yellow, 1.0 / 6.0),
                new GradientStop(Colors.Lime, 2.0 / 6.0),
                new GradientStop(Colors.Cyan, 3.0 / 6.0),
                new GradientStop(Colors.Blue, 4.0 / 6.0),
                new GradientStop(Colors.Magenta, 5.0 / 6.0),
                new GradientStop(Colors.Red, 1.0)
            }
        };

        ValueBar.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Colors.Black, 1) }
        };

        GrayBarInit();
        RefreshVisual();
    }

    private void GrayBarInit()
    {
        // 灰度条背景：白 → 黑（灰阶预览）
        GraySwatch.Background = new SolidColorBrush(Colors.Gray);
        GraySlider.ToolTip = "0-255";
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        TitleText.Text = string.IsNullOrEmpty(Title)
            ? (_loc?.Get("color.pick") ?? "选择颜色")
            : Title;
        ColorPopup.IsOpen = true;
        InitPanelFromCurrent();
    }

    private void InitPanelFromCurrent()
    {
        var color = TryParseHex(SelectedColorText) is SolidColorBrush sb ? sb.Color : Colors.Black;
        _updating = true;
        try
        {
            RBox.Text = color.R.ToString();
            GBox.Text = color.G.ToString();
            BBox.Text = color.B.ToString();
            ABox.Text = color.A.ToString();
            HueSlider.Value = RgbToHue(color.R, color.G, color.B);
            ValueSlider.Value = Math.Max(color.R, Math.Max(color.G, color.B)) / 255.0;
            AlphaSlider.Value = color.A;
            // 黑白选项卡：亮度 = 最亮通道，透明度独立
            GraySlider.Value = Math.Max(color.R, Math.Max(color.G, color.B));
            GrayAlphaSlider.Value = color.A;
            UpdateGrayPreview();
            UpdatePreviewFromSliders();
        }
        finally
        {
            _updating = false;
        }
    }

    // ===== 三个滑块：色相 / 明度 / 透明度 → 重算预览 =====

    private void OnHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => RecomputeFromSliders();

    private void OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => RecomputeFromSliders();

    private void OnAlphaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => RecomputeFromSliders();

    private void RecomputeFromSliders()
    {
        if (_updating)
            return;
        _updating = true;
        try
        {
            var (r, g, b) = HsvToRgb(HueSlider.Value, 1.0, ValueSlider.Value);
            int a = (int)Math.Round(AlphaSlider.Value);
            RBox.Text = r.ToString();
            GBox.Text = g.ToString();
            BBox.Text = b.ToString();
            ABox.Text = a.ToString();
            GraySlider.Value = Math.Max(r, Math.Max(g, b));
            GrayAlphaSlider.Value = a;
            UpdateGrayPreview();
            UpdatePreviewFromSliders();
        }
        finally
        {
            _updating = false;
        }
    }

    // ===== 黑白选项卡：亮度 + 透明度 → 灰阶（R=G=B） =====

    private void OnGrayValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
            return;
        _updating = true;
        try
        {
            int v = (int)Math.Round(GraySlider.Value);
            int a = (int)Math.Round(GrayAlphaSlider.Value);
            RBox.Text = v.ToString();
            GBox.Text = v.ToString();
            BBox.Text = v.ToString();
            ABox.Text = a.ToString();
            HueSlider.Value = 0;
            ValueSlider.Value = v / 255.0;
            AlphaSlider.Value = a;
            UpdateGrayPreview();
            UpdatePreviewFromSliders();
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateGrayPreview()
    {
        int v = (int)Math.Round(GraySlider.Value);
        int a = (int)Math.Round(GrayAlphaSlider.Value);
        GraySwatch.Background = new SolidColorBrush(Color.FromArgb((byte)a, (byte)v, (byte)v, (byte)v));
    }

    // ===== RGBA 输入 → 同步滑块 =====

    private void OnRgbChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating)
            return;
        _updating = true;
        try
        {
            if (!TryGetRgba(out int r, out int g, out int b, out int a))
                return;
            HueSlider.Value = RgbToHue(r, g, b);
            ValueSlider.Value = Math.Max(r, Math.Max(g, b)) / 255.0;
            AlphaSlider.Value = a;
            GraySlider.Value = Math.Max(r, Math.Max(g, b));
            GrayAlphaSlider.Value = a;
            UpdateGrayPreview();
            UpdatePreviewFromSliders();
        }
        finally
        {
            _updating = false;
        }
    }

    private bool TryGetRgba(out int r, out int g, out int b, out int a)
    {
        r = Parse(RBox.Text); g = Parse(GBox.Text); b = Parse(BBox.Text); a = Parse(ABox.Text);
        return r >= 0 && g >= 0 && b >= 0 && a >= 0;
    }

    private static int Parse(string s)
        => int.TryParse(s, out int v) ? Math.Clamp(v, 0, 255) : -1;

    private void UpdatePreviewFromSliders()
    {
        if (!TryGetRgba(out int r, out int g, out int b, out int a))
            return;
        BigSwatch.Background = new SolidColorBrush(Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b));

        // 透明度条背景：当前 RGB 色 → 透明
        AlphaBar.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(255, (byte)r, (byte)g, (byte)b), 0),
                new GradientStop(Color.FromArgb(0, (byte)r, (byte)g, (byte)b), 1)
            }
        };
    }

    // ===== 确定提交 =====

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (TryGetRgba(out int r, out int g, out int b, out int a))
        {
            string hex = $"#{a:X2}{r:X2}{g:X2}{b:X2}";
            SelectedColorText = hex;
            ColorChanged?.Invoke(this, hex);
        }
        ColorPopup.IsOpen = false;
    }

    private static void OnColorTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ColorPickerControl)d).RefreshVisual();

    private void RefreshVisual()
    {
        var color = TryParseHex(SelectedColorText) is SolidColorBrush sb ? sb.Color : Colors.Black;
        Swatch.Background = new SolidColorBrush(color);
        HexText.Text = SelectedColorText ?? string.Empty;
    }

    private static Brush? TryParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex.Trim())!);
        }
        catch
        {
            return null;
        }
    }

    // ===== HSV ↔ RGB =====

    private static (int R, int G, int B) HsvToRgb(double h, double s, double v)
    {
        double hue = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        double m = v - c;
        double r, g, b;
        if (hue < 60) { r = c; g = x; b = 0; }
        else if (hue < 120) { r = x; g = c; b = 0; }
        else if (hue < 180) { r = 0; g = c; b = x; }
        else if (hue < 240) { r = 0; g = x; b = c; }
        else if (hue < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    private static double RgbToHue(int r, int g, int b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;
        if (delta < 1e-9)
            return 0;
        double h;
        if (Math.Abs(max - rd) < 1e-9)
            h = 60 * (((gd - bd) / delta) % 6);
        else if (Math.Abs(max - gd) < 1e-9)
            h = 60 * ((bd - rd) / delta + 2);
        else
            h = 60 * ((rd - gd) / delta + 4);
        return h < 0 ? h + 360 : h;
    }
}
