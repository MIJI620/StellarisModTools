// 文件: Stellaris.Editor/ExportSettingsWindow.xaml.cs
// 导出设置窗口：4 列成组布局显示银河类别 galaxy.json 全部可配置导出参数
// （global.preview.* / global.icon.*），读写同一类别保证保存生效。
// 分组：宽度与高度成组、背景色与核心色成组、两个辉光成组、密度单独一行。
// 恒星预设：右键添加/删除/设置（6 RGB + 概率），确定后按概率升序排列。
// 全部 UI 文本经本地化 json 键（export.*）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Stellaris.Editor.Controls;
using Stellaris.Engine.GalaxyStyle;
using Stellaris.Engine.LocalConfigManager;

namespace Stellaris.Editor;

public partial class ExportSettingsWindow : Window
{
    private readonly EngineServices _services;
    private readonly UILocalisationManager _loc;
    private readonly Dictionary<string, TextBox> _intBoxes = new();
    private readonly Dictionary<string, TextBox> _doubleBoxes = new();
    private readonly Dictionary<string, CheckBox> _boolBoxes = new();
    private readonly Dictionary<string, ColorPickerControl> _colorPickers = new();

    // 字段布局：按组排列（每节一行内所有字段，每行 2 个并排）；(前缀, 键, 类型, 默认值, 本地化键)
    private static readonly (string? P, string? K, string? Kind, string? Def, string? Lk)?[][] Sections =
    {
        // ---- 预览组 ----
        new (string? P, string? K, string? Kind, string? Def, string? Lk)?[]
        {
            ("global.preview", "outer_width", "int", "562", "export.outer_width"),
            ("global.preview", "outer_height", "int", "236", "export.outer_height"),
            ("global.preview", "inner_width", "int", "200", "export.inner_width"),
            ("global.preview", "inner_height", "int", "200", "export.inner_height"),
            ("global.preview", "background_color", "rgba", "0,0,0,255", "export.background_color"),
            ("global.preview", "core_color", "rgba", "255,255,255,255", "export.core_color"),
            ("global.preview", "glow_arms", "bool", "true", "export.glow_arms"),
            ("global.preview", "glow_core", "bool", "true", "export.glow_core"),
            ("global.preview", "bg_star_density", "double", "0.2", "export.bg_star_density"),
            ("global.preview", "fill_density", "double", "0.25", "export.fill_density")
        },
        // ---- 图标组 ----
        new (string? P, string? K, string? Kind, string? Def, string? Lk)?[]
        {
            ("global.icon", "frame_width", "int", "110", "export.frame_width"),
            ("global.icon", "frame_height", "int", "59", "export.frame_height"),
            ("global.icon", "inner_width", "int", "35", "export.inner_width"),
            ("global.icon", "inner_height", "int", "35", "export.inner_height"),
            ("global.icon", "glow_radius", "int", "9", "export.glow_radius"),
            ("global.icon", "normal_color", "rgba", "13,200,167,255", "export.normal_color"),
            ("global.icon", "highlight_color", "rgba", "249,161,50,255", "export.highlight_color"),
            ("global.icon", "pressed_color", "rgba", "108,255,224,255", "export.pressed_color")
        }
    };

    public ExportSettingsWindow(EngineServices services)
    {
        _services = services;
        _loc = services.Localisation;
        InitializeComponent();

        Title = _loc.Get("style.export_settings");
        OkButton.Content = _loc.Get("roots.ok");
        ExportPreviewsButton.Content = _loc.Get("export.export_previews");
        ExportIconsButton.Content = _loc.Get("export.export_icons");

        // 本地设置没有恒星预设时，先自动写入代码内置默认预设（PreviewOptions.Default.StarPresets），
        // 这样下方列表即可读到。
        if (_services.ConfigManager != null && ReadStarPresets().Count == 0)
            WriteDefaultStarPresets();

        BuildFields();
    }

    /// <summary>把代码内置默认恒星预设写入银河类别 galaxy.json（按概率升序）。</summary>
    private void WriteDefaultStarPresets()
    {
        var defaults = PreviewOptions.Default.StarPresets;
        if (defaults == null || defaults.Count == 0)
            return;
        var batch = new Dictionary<string, object>();
        foreach (var kv in defaults.OrderBy(kv => kv.Value.Weight))
        {
            var s = kv.Value;
            batch[$"global.preview.star_presets.{kv.Key}.color"] = new[] { (int)s.R, (int)s.G, (int)s.B, (int)s.A };
            batch[$"global.preview.star_presets.{kv.Key}.glow_color"] = new[] { (int)s.GlowR, (int)s.GlowG, (int)s.GlowB, (int)s.GlowA };
            batch[$"global.preview.star_presets.{kv.Key}.weight"] = s.Weight;
        }
        _services.ConfigManager!.SetBatch("galaxy", batch);
    }

    private void BuildFields()
    {
        var cm = _services.ConfigManager;
        var groupKeys = new[] { "export.group.preview", "export.group.icon" };
        for (int s = 0; s < Sections.Length; s++)
        {
            var groupTitle = new TextBlock
            {
                Text = _loc.Get(groupKeys[s]),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 6)
            };
            FieldsPanel.Children.Add(groupTitle);

            var fields = Sections[s]!;
            // 每行 2 个字段并排（4 列：标签/控件/标签/控件），成组处理
            for (int i = 0; i < fields.Length; i += 2)
            {
                var pair = new (string? P, string? K, string? Kind, string? Def, string? Lk)?[2];
                pair[0] = fields[i];
                if (i + 1 < fields.Length)
                    pair[1] = fields[i + 1];
                FieldsPanel.Children.Add(BuildRow(cm, pair));
            }
        }

        BuildStarPresetSection();
    }

    /// <summary>构建一行：最多 2 个字段并排（4 列：标签/控件/标签/控件）。</summary>
    private FrameworkElement BuildRow(IConfigManager? cm,
        (string? P, string? K, string? Kind, string? Def, string? Lk)?[] pair)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        for (int i = 0; i < 4; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int f = 0; f < 2; f++)
        {
            if (pair[f] is not { } field || field.K == null)
                continue; // 该半格为空
            string fullKey = $"{field.P}.{field.K}";
            object? current = null;
            if (cm != null)
            {
                try { current = cm.Get("galaxy", fullKey); }
                catch { /* 未设置 → 用默认 */ }
            }

            var label = new TextBlock
            {
                Text = _loc.Get(field.Lk ?? "export.outer_width"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = fullKey,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0)
            };
            FrameworkElement control = BuildControl(field, fullKey, current);

            Grid.SetColumn(label, f * 2);
            Grid.SetColumn(control, f * 2 + 1);
            grid.Children.Add(label);
            grid.Children.Add(control);
        }
        return grid;
    }

    private FrameworkElement BuildControl((string? P, string? K, string? Kind, string? Def, string? Lk) field,
        string fullKey, object? current)
    {
        string kind = field.Kind ?? "int";
        string def = field.Def ?? "";
        switch (kind)
        {
            case "bool":
                var cb = new CheckBox
                {
                    IsChecked = current switch { bool b => b, _ => ParseBool(def) },
                    VerticalAlignment = VerticalAlignment.Center
                };
                _boolBoxes[fullKey] = cb;
                return cb;
            case "rgba":
                // 用拾色器按钮（点击弹出颜色面板），与星系样式颜色选项卡同款
                var curRgba = ReadRgba(current) ?? ParseRgbaDef(def);
                var picker = new ColorPickerControl
                {
                    SelectedColorText = RgbaToHex(curRgba),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                };
                picker.ApplyLocalisation(_loc);
                _colorPickers[fullKey] = picker;
                return picker;
            default: // int / double
                var tb = new TextBox
                {
                    Text = current switch
                    {
                        int i => i.ToString(CultureInfo.InvariantCulture),
                        long l => l.ToString(CultureInfo.InvariantCulture),
                        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                        _ => def
                    }
                };
                (kind == "int" ? _intBoxes : _doubleBoxes)[fullKey] = tb;
                return tb;
        }
    }

    /// <summary>一键全量导出预览图（无视增量；供检查）。</summary>
    private void OnExportPreviews(object sender, RoutedEventArgs e)
    {
        var engine = _services.StyleEngine;
        if (engine == null)
            return;
        SaveRunner.Run(_services, "status.saving",
            () =>
            {
                var r = engine.ExportAllPreviewsEnabled();
                return r.Fail == 0; // 成功不弹窗；有失败才弹
            },
            failMessage: _loc.Get("export.export_all_failed"));
    }

    /// <summary>一键全量导出图标（无视增量；供检查）。</summary>
    private void OnExportIcons(object sender, RoutedEventArgs e)
    {
        var engine = _services.StyleEngine;
        if (engine == null)
            return;
        SaveRunner.Run(_services, "status.saving",
            () =>
            {
                var r = engine.ExportAllIconsEnabled();
                return r.Fail == 0; // 成功不弹窗；有失败才弹
            },
            failMessage: _loc.Get("export.export_all_failed"));
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var cm = _services.ConfigManager;
        if (cm == null)
        {
            MessageBox.Show(_loc.Get("export.no_config"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var values = new Dictionary<string, object>();
        foreach (var section in Sections)
        {
            foreach (var fieldOpt in section!)
            {
                if (fieldOpt is not { } field || field.K == null)
                    continue;
                string fullKey = $"{field.P}.{field.K}";
                string kind = field.Kind ?? "int";
                try
                {
                    switch (kind)
                    {
                        case "bool":
                            if (_boolBoxes.TryGetValue(fullKey, out var cb))
                                values[fullKey] = cb.IsChecked == true;
                            break;
                        case "rgba":
                            if (_colorPickers.TryGetValue(fullKey, out var cp)
                                && HexToRgba(cp.SelectedColorText, out var rgba))
                                values[fullKey] = rgba;
                            break;
                        case "int":
                            if (_intBoxes.TryGetValue(fullKey, out var itb)
                                && int.TryParse(itb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                                values[fullKey] = iv;
                            break;
                        default:
                            if (_doubleBoxes.TryGetValue(fullKey, out var dtb)
                                && double.TryParse(dtb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
                                values[fullKey] = dv;
                            break;
                    }
                }
                catch
                {
                    // 单个字段无效则跳过
                }
            }
        }

        try
        {
            cm.SetBatch("galaxy", values);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(_loc.Format("export.save_failed", ex.Message), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ==================== 恒星预设区 ====================

    private void BuildStarPresetSection()
    {
        var starTitle = new TextBlock
        {
            Text = _loc.Get("export.group.star_presets"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 6)
        };
        FieldsPanel.Children.Add(starTitle);

        var starList = new ListBox { Height = 130, Margin = new Thickness(0, 0, 0, 4) };
        RefreshStarList(starList);

        var starMenu = new ContextMenu();
        var addItem = new MenuItem { Header = _loc.Get("export.star.add") };
        addItem.Click += (_, _) => AddStarPreset(starList);
        var removeItem = new MenuItem { Header = _loc.Get("export.star.remove") };
        removeItem.Click += (_, _) => RemoveStarPreset(starList);
        var editItem = new MenuItem { Header = _loc.Get("export.star.edit") };
        editItem.Click += (_, _) => EditStarPreset(starList);
        starMenu.Items.Add(addItem);
        starMenu.Items.Add(removeItem);
        starMenu.Items.Add(editItem);
        starList.ContextMenu = starMenu;

        FieldsPanel.Children.Add(starList);
    }

    /// <summary>读取银河类别的恒星预设（global.preview.star_presets.{name}）。</summary>
    private Dictionary<string, StarPresetItem> ReadStarPresets()
    {
        var result = new Dictionary<string, StarPresetItem>(StringComparer.Ordinal);
        var cm = _services.ConfigManager;
        if (cm == null)
            return result;
        try
        {
            var node = cm.Get("galaxy", "global.preview.star_presets");
            if (node is not JsonObject obj)
                return result;
            foreach (var kv in obj)
            {
                if (kv.Value is not JsonObject p)
                    continue;
                var item = new StarPresetItem { Name = kv.Key };
                var color = ReadRgbaObj(p, "color");
                if (color != null) item.Color = color;
                var glow = ReadRgbaObj(p, "glow_color");
                if (glow != null) item.Glow = glow;
                if (p.TryGetPropertyValue("weight", out var w) && w is JsonValue jv && jv.TryGetValue<int>(out int weight))
                    item.Weight = weight;
                result[kv.Key] = item;
            }
        }
        catch
        {
            // 读取失败返回空
        }
        return result;
    }

    private static int[]? ReadRgbaObj(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonArray arr || arr.Count < 4)
            return null;
        var result = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (arr[i] is not JsonValue v || !v.TryGetValue<int>(out int x))
                return null;
            result[i] = x;
        }
        return result;
    }

    /// <summary>写入恒星预设：先删除 galaxy.json 现有的全部预设键（含本次被删除的），再按保留列表概率升序重写。</summary>
    private void WriteStarPresets(Dictionary<string, StarPresetItem> presets)
    {
        var cm = _services.ConfigManager;
        if (cm == null)
            return;
        // 删除现有的全部预设（含被删除的）——否则残留键仍留在 galaxy.json
        var existing = ReadStarPresets();
        foreach (var name in existing.Keys.Union(presets.Keys))
        {
            try { cm.Delete("galaxy", $"global.preview.star_presets.{name}"); } catch { }
        }
        var sorted = presets.Values.OrderBy(p => p.Weight).ToList();
        var batch = new Dictionary<string, object>();
        foreach (var p in sorted)
        {
            batch[$"global.preview.star_presets.{p.Name}.color"] = p.Color;
            batch[$"global.preview.star_presets.{p.Name}.glow_color"] = p.Glow;
            batch[$"global.preview.star_presets.{p.Name}.weight"] = p.Weight;
        }
        cm.SetBatch("galaxy", batch);
    }

    private void RefreshStarList(ListBox list)
    {
        list.Items.Clear();
        var presets = ReadStarPresets();
        foreach (var item in presets.Values.OrderBy(p => p.Weight))
            list.Items.Add(item);
    }

    private void AddStarPreset(ListBox list)
    {
        var presets = ReadStarPresets();
        int n = 1;
        string newName = $"star_preset_{n}";
        while (presets.ContainsKey(newName))
            newName = $"star_preset_{++n}";
        var def = new StarPresetItem
        {
            Name = newName,
            Color = new[] { 255, 255, 255, 255 },
            Glow = new[] { 255, 255, 255, 255 },
            Weight = 1
        };
        var item = EditStarDialog(def);
        if (item != null)
        {
            presets[item.Name] = item;
            WriteStarPresets(presets);
            RefreshStarList(list);
        }
    }

    private void RemoveStarPreset(ListBox list)
    {
        if (list.SelectedItem is not StarPresetItem sel)
            return;
        var presets = ReadStarPresets();
        presets.Remove(sel.Name);
        WriteStarPresets(presets);
        RefreshStarList(list);
    }

    private void EditStarPreset(ListBox list)
    {
        if (list.SelectedItem is not StarPresetItem sel)
            return;
        var presets = ReadStarPresets();
        if (!presets.TryGetValue(sel.Name, out var preset))
            return;
        var item = EditStarDialog(preset);
        if (item != null)
        {
            presets.Remove(sel.Name);
            presets[item.Name] = item;
            WriteStarPresets(presets);
            RefreshStarList(list);
        }
    }

    /// <summary>设置对话框：名称 + 颜色 RGB(3) + 辉光 RGB(3) + 概率(1)；确定返回新条目，取消返回 null。</summary>
    private StarPresetItem? EditStarDialog(StarPresetItem preset)
    {
        var win = new Window
        {
            Title = _loc.Get("export.star.edit_title"),
            Width = 340,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = this
        };
        var panel = new StackPanel { Margin = new Thickness(14) };

        panel.Children.Add(new TextBlock { Text = _loc.Get("export.star.name"), Margin = new Thickness(0, 0, 0, 3) });
        var nameBox = new TextBox { Text = preset.Name, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(nameBox);

        // 颜色 / 辉光：用通用拾色器（点击弹出颜色面板）
        panel.Children.Add(new TextBlock { Text = _loc.Get("export.star.color"), Margin = new Thickness(0, 0, 0, 3) });
        var colorPicker = new ColorPickerControl
        {
            SelectedColorText = RgbaToHex(preset.Color),
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        colorPicker.ApplyLocalisation(_loc);
        panel.Children.Add(colorPicker);

        panel.Children.Add(new TextBlock { Text = _loc.Get("export.star.glow"), Margin = new Thickness(0, 0, 0, 3) });
        var glowPicker = new ColorPickerControl
        {
            SelectedColorText = RgbaToHex(preset.Glow),
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        glowPicker.ApplyLocalisation(_loc);
        panel.Children.Add(glowPicker);

        panel.Children.Add(new TextBlock { Text = _loc.Get("export.star.weight"), Margin = new Thickness(0, 0, 0, 3) });
        var weightBox = new TextBox { Text = preset.Weight.ToString(CultureInfo.InvariantCulture) };
        panel.Children.Add(weightBox);

        var ok = new Button { Content = _loc.Get("roots.ok"), Width = 90, Height = 26, Margin = new Thickness(0, 14, 8, 0), IsDefault = true };
        var cancel = new Button { Content = _loc.Get("roots.cancel"), Width = 90, Height = 26, Margin = new Thickness(0, 14, 0, 0), IsCancel = true };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);
        panel.Children.Add(btnRow);

        win.Content = panel;

        StarPresetItem? result = null;
        ok.Click += (_, _) =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0
                || !HexToRgba(colorPicker.SelectedColorText, out var color)
                || !HexToRgba(glowPicker.SelectedColorText, out var glow)
                || !int.TryParse(weightBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight)
                || weight < 0)
            {
                MessageBox.Show(_loc.Get("export.star.invalid"), win.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            result = new StarPresetItem
            {
                Name = name,
                Color = color,
                Glow = glow,
                Weight = weight
            };
            win.DialogResult = true;
        };
        return win.ShowDialog() == true ? result : null;
    }

    /// <summary>恒星预设（UI 条目）：颜色 RGBA + 辉光 RGBA + 概率。</summary>
    private sealed class StarPresetItem
    {
        public string Name { get; set; } = string.Empty;
        public int[] Color { get; set; } = new int[4];
        public int[] Glow { get; set; } = new int[4];
        public int Weight { get; set; }
        public override string ToString() => $"{Name}  (weight {Weight})";
    }

    // ==================== 辅助 ====================

    private static bool ParseBool(string s) => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析默认 RGBA 定义 "r,g,b,a"。</summary>
    private static int[] ParseRgbaDef(string def)
    {
        var parts = (def ?? "0,0,0,255").Split(',');
        var result = new int[4];
        for (int i = 0; i < 4; i++)
        {
            result[i] = int.TryParse(i < parts.Length ? parts[i].Trim() : "0", NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int v) ? Math.Clamp(v, 0, 255) : 0;
        }
        return result;
    }

    /// <summary>RGBA int[4] → ARGB hex 字符串。</summary>
    private static string RgbaToHex(int[] rgba)
    {
        if (rgba.Length < 4)
            return "#FF000000";
        return $"#{rgba[3]:X2}{rgba[0]:X2}{rgba[1]:X2}{rgba[2]:X2}";
    }

    /// <summary>ARGB hex 字符串 → RGBA int[4]。</summary>
    private static bool HexToRgba(string hex, out int[] rgba)
    {
        rgba = new int[4];
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex.Trim())!;
            rgba = new[] { (int)c.R, (int)c.G, (int)c.B, (int)c.A };
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从配置值读取 RGBA（JsonArray 或 int[]），无法解析返回 null。</summary>
    private static int[]? ReadRgba(object? current)
    {
        if (current is JsonArray arr && arr.Count >= 4)
        {
            var result = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (arr[i] is not JsonValue jv || !jv.TryGetValue<int>(out int v))
                    return null;
                result[i] = v;
            }
            return result;
        }
        if (current is int[] ia && ia.Length >= 4)
            return ia;
        return null;
    }

    private static bool TryParseRgba(TextBox[] boxes, out int[] rgba)
    {
        rgba = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!int.TryParse(boxes[i].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return false;
            rgba[i] = Math.Clamp(v, 0, 255);
        }
        return true;
    }
}
