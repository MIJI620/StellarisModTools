using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Stellaris.Parser;

namespace Stellaris.Editor.Controls;

/// <summary>
/// 本地化统一编辑组件（星系样式"其他"页风格）：
/// 边框 + 语种下拉 + 名称/描述（逻辑值可编辑 → 显示值只读，失焦保存并刷新）。
/// 5 处共用：星系样式 / 法令决议 / 战略资源 / 静态地图 / 动态地图。
/// 宿主职责（差异点）：提供名称键委托（描述键 = {名称键}_desc）、语种列表、保存回调。
/// </summary>
public partial class LocalisationEditBox : UserControl
{
    public LocalisationEditBox()
    {
        InitializeComponent();
        // 标签本地化（5 处共用同一组键：edict.* / resource.* 语义通用——名称/描述）
        NameLogicalLabel.Text = App.CurrentLocalisation?.Get("edict.name") ?? "名称（逻辑值）";
        NameDisplayLabel.Text = App.CurrentLocalisation?.Get("edict.name_display") ?? "名称（显示值）";
        DescLogicalLabel.Text = App.CurrentLocalisation?.Get("edict.desc") ?? "描述（逻辑值）";
        DescDisplayLabel.Text = App.CurrentLocalisation?.Get("edict.desc_display") ?? "描述（显示值）";
        LangCombo.SelectionChanged += (_, _) => Load();
        NameLogicalBox.LostFocus += (_, _) => SaveAndRefresh(NameLogicalBox.Text, NameKey);
        DescLogicalBox.LostFocus += (_, _) => SaveAndRefresh(DescLogicalBox.Text, DescKey);
    }

    /// <summary>数据源（读取本地化 + 默认保存用）。</summary>
    public StellarisAdapter? Adapter { get; set; }

    /// <summary>当前条目的名称本地化键（宿主提供）。</summary>
    public Func<string>? GetNameKey { get; set; }

    /// <summary>当前条目的描述本地化键（宿主提供；缺省 = {名称键}_desc——星系样式等自定义描述键场景必须显式传）。</summary>
    public Func<string>? GetDescKey { get; set; }

    /// <summary>是否显示语种下拉（缺省 true；固定语种场景可关）。</summary>
    public bool ShowLanguageSelector
    {
        get => LangCombo.Visibility == Visibility.Visible;
        set => LangCombo.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>是否显示描述区（缺省 true；只有名称的场景如动态地图名可关）。</summary>
    public bool ShowDescription
    {
        get => DescLogicalLabel.Visibility == Visibility.Visible;
        set
        {
            var v = value ? Visibility.Visible : Visibility.Collapsed;
            DescLogicalLabel.Visibility = v;
            DescLogicalBox.Visibility = v;
            DescDisplayLabel.Visibility = v;
            DescDisplayText.Visibility = v;
        }
    }

    /// <summary>名称逻辑值输入框是否只读（缺省 false 可编辑；只读展示场景如详情页设 true）。</summary>
    public bool NameLogicalReadOnly
    {
        get => _nameLogicalReadOnly;
        set { _nameLogicalReadOnly = value; if (NameLogicalBox != null) NameLogicalBox.IsReadOnly = value; }
    }
    private bool _nameLogicalReadOnly;

    /// <summary>描述逻辑值输入框是否只读（缺省 false 可编辑；只读展示场景设 true）。</summary>
    public bool DescLogicalReadOnly
    {
        get => _descLogicalReadOnly;
        set { _descLogicalReadOnly = value; if (DescLogicalBox != null) DescLogicalBox.IsReadOnly = value; }
    }
    private bool _descLogicalReadOnly;

    /// <summary>语种列表来源（缺省 = Adapter 全部语言）。</summary>
    public Func<IEnumerable<string>>? GetLangs { get; set; }

    /// <summary>保存回调（缺省 = Adapter.UpdateLocalisationEntry 到第一个本地化文件 + Expand + 刷新）。</summary>
    public Action<string, string, string>? SaveLocalisation { get; set; }

    /// <summary>**内存本地化覆盖源**（宿主提供：lang, key → 值；返回非 null 优先于 Adapter 读取——
    /// 未落盘场景（弹窗内存编辑）输入失焦后 Load 回显用，缺省 null 走 Adapter）。</summary>
    public Func<string, string, string?>? MemoryGet { get; set; }

    /// <summary>名称键（当前条目）。</summary>
    public string NameKey => GetNameKey?.Invoke() ?? "";

    /// <summary>描述键：宿主显式传入（GetDescKey）；缺省 = {名称键}_desc。</summary>
    public string DescKey
        => GetDescKey?.Invoke()
           ?? (string.IsNullOrEmpty(NameKey) ? "" : NameKey + "_desc");

    /// <summary>当前选中语种（界面语言映射由宿主在 GetLangs/初始选中前处理——组件缺省按界面语言首字母匹配）。</summary>
    public string SelectedLang => (LangCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>重置语种下拉 + 重新加载（条目切换/新建时调用）。</summary>
    public void Reload()
    {
        var langs = (GetLangs?.Invoke() ?? Adapter?.GetLocalisationLanguages() ?? new List<string>()).ToList();        if (langs.Count == 0)
            langs.Add("english");
        LangCombo.Items.Clear();
        foreach (var l in langs)
            LangCombo.Items.Add(new ComboBoxItem { Content = DisplayNameOf(l), Tag = l });
        // 选中：优先匹配界面语言前缀（zh→simp_chinese；其他→english）
        // ⚠️ 直接读全局 UI 语言（App.CurrentLocalisation）——不依赖 MainWindow 是否构造完：
        // 页面在 MainWindow 构造阶段创建时 MainWindow 属性为 null，旧判定退回 english → 中文界面首次显示英文（用户 2026-08）
        var ui = CurrentUiLang();
        var preferred = ui.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? langs.FirstOrDefault(l => l.StartsWith("simp_chinese", StringComparison.OrdinalIgnoreCase))
            : langs.FirstOrDefault(l => l.StartsWith("english", StringComparison.OrdinalIgnoreCase));
        var selected = preferred ?? (langs.Contains("english") ? "english" : langs.FirstOrDefault());
        foreach (object o in LangCombo.Items)
        {
            if (o is ComboBoxItem it && it.Tag is string code && code == selected)
            {
                LangCombo.SelectedItem = it;
                break;
            }
        }
        Load();
    }

    private string CurrentUiLang()
        => App.CurrentLocalisation?.CurrentLanguage ?? "zh-CN";

    private string DisplayNameOf(string lang)
        => App.CurrentLocalisation?.GetLanguageDisplayNameLocalized(lang) ?? lang;

    /// <summary>按当前选中语种填充 4 行（名称/描述 × 逻辑值/显示值）。</summary>
    public void Load()
    {
        if (Adapter == null)
            return;
        var lang = SelectedLang;
        if (string.IsNullOrEmpty(NameKey) || string.IsNullOrEmpty(lang))
        {
            // 无键（如新条目未填 custom_tooltip）→ **清空显示**，不残留上一个条目的旧值（用户 2026-08：切换后还显示旧的）
            NameLogicalBox.Text = "";
            NameDisplayText.Text = "";
            DescLogicalBox.Text = "";
            DescDisplayText.Text = "";
            return;
        }
        // 内存覆盖优先（未落盘场景回显输入）；否则 Adapter
        var nameLogical = MemoryGet?.Invoke(lang, NameKey) ?? Adapter.GetLocalisedLogicalText(NameKey, lang) ?? "";
        var nameDisplay = MemoryGet?.Invoke(lang, NameKey) ?? Adapter.GetLocalisedText(NameKey, lang) ?? NameKey;
        var descLogical = MemoryGet?.Invoke(lang, DescKey) ?? Adapter.GetLocalisedLogicalText(DescKey, lang) ?? "";
        var descDisplay = MemoryGet?.Invoke(lang, DescKey) ?? Adapter.GetLocalisedText(DescKey, lang) ?? "";
        // 逻辑值：**原样显示**（\n 字面保持字面不转真实换行——用户 2026-08：逻辑值是原样显示）；
        // 显示值：压空白后转真实换行（显示效果）
        NameLogicalBox.Text = nameLogical;
        NameDisplayText.Text = EscapedToLine(Collapse(nameDisplay));
        DescLogicalBox.Text = descLogical;
        DescDisplayText.Text = EscapedToLine(Collapse(descDisplay));
    }

    /// <summary>失焦保存：真实换行 → \n 字面（存本地化）→ 展开显示值 → 刷新。保存回调缺省 = Adapter 版。</summary>
    private void SaveAndRefresh(string newLogicalText, string key)
    {
        if (Adapter == null || string.IsNullOrEmpty(key))
            return;
        var lang = SelectedLang;
        if (string.IsNullOrEmpty(lang))
            return;
        var logical = LineToEscaped(newLogicalText);
        if (SaveLocalisation != null)
        {
            SaveLocalisation(lang, key, logical);
        }
        else
        {
            var files = Adapter.GetLocalisationFiles(lang);
            if (files.Count == 0)
                return;
            Adapter.UpdateLocalisationEntry(lang, files[0], key, logical);
            Adapter.ExpandLocalisationKey(lang, key);
        }
        Load();
    }

    /// <summary>本地化存储的 \n 字面 → 显示/编辑用的真实换行。</summary>
    private static string EscapedToLine(string text)
        => string.IsNullOrEmpty(text) ? text : text.Replace("\\n", "\n");

    /// <summary>编辑/输入的真实换行 → 本地化存储的 \n 字面（\r\n、\r 一并归一）。</summary>
    private static string LineToEscaped(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        return text.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n");
    }

    /// <summary>压缩空白（最少分隔符）——与战略资源页显示一致。</summary>
    private static string Collapse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
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
}
