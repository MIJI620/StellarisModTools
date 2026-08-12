// 文件: Stellaris.Editor/UILocalisationManager.cs
// 界面本地化模块（Editor 规范第二章）：
// 从 exe 所在目录 localisation/*.json 载入语言键值对，
// 主界面与初始化状态浮层的全部可见文本经此模块提取，禁止硬编码 UI 字符串。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Stellaris.Editor;

/// <summary>
/// 界面本地化管理器。
/// 载入规则（规范 2.1）：exe 所在目录 `localisation/` 下每个语言一个 JSON，
/// 文件名即语言标识（如 `en-US.json`、`zh-CN.json`），内容为扁平键值对。
/// 回退规则（规范 2.4）：当前语言 → 默认语言（en-US）→ 键名本身，永不缺键崩溃。
/// </summary>
public sealed class UILocalisationManager
{
    /// <summary>默认语言（回退基准，规范 2.4）。</summary>
    public const string DefaultLanguage = "en-US";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    // 语言标识（不区分大小写）→ 键 → 文本
    private readonly Dictionary<string, Dictionary<string, string>> _tables =
        new(StringComparer.OrdinalIgnoreCase);

    // 语言声明（languages.json）：语言标识 → 语言自称（endonym，如 zh-CN → 简体中文、en-US → English）
    private readonly Dictionary<string, string> _languageNames = new(StringComparer.OrdinalIgnoreCase);

    private string _currentLanguage = DefaultLanguage;

    /// <summary>语言切换事件：ViewModel 据此刷新全部文本绑定（规范 2.2）。</summary>
    public event EventHandler? LanguageChanged;

    public string CurrentLanguage => _currentLanguage;

    /// <summary>UI 语言代码 → 群星 mod 本地化语言代码（如 zh-CN → simp_chinese）。
    /// 供引擎/页面查询 mod 本地化文本时映射（星系样式/科技图等共用）。</summary>
    public static string MapUiLangToModLang(string uiLang) => uiLang.ToLowerInvariant() switch
    {
        "zh-cn" or "zh-hans" or "zh" => "simp_chinese",
        "zh-tw" or "zh-hant" => "trad_chinese",
        "en" or "en-us" => "english",
        "ja" or "ja-jp" => "japanese",
        "ko" or "ko-kr" => "korean",
        "fr" or "fr-fr" => "french",
        "de" or "de-de" => "german",
        "es" or "es-es" => "spanish",
        "ru" or "ru-ru" => "russian",
        "pt" or "pt-br" => "braz_por",
        "pl" or "pl-pl" => "polish",
        _ => uiLang.ToLowerInvariant()
    };

    public IReadOnlyCollection<string> AvailableLanguages
        => _tables.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();

    /// <summary>
    /// 载入全部语言文件。目录缺失或为空时回退到默认语言（空表），记录调试信息；
    /// 单个文件损坏时跳过该语言并记录警告，不影响其他语言（规范 2.4 / 6.1 防御性）。
    /// </summary>
    /// <param name="directory">本地化目录；缺省为 exe 所在目录下的 localisation/。</param>
    public void Load(string? directory = null)
    {
        _tables.Clear();
        _languageNames.Clear();
        string dir = directory ?? Path.Combine(AppContext.BaseDirectory, "localisation");

        if (!Directory.Exists(dir))
        {
            Debug.WriteLine($"[UILocalisation] 目录不存在: {dir}");
        }
        else
        {
            // 语言声明（languages.json）：告知可选语言及各自称
            LoadLanguageDeclarations(Path.Combine(dir, "languages.json"));

            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                string lang = Path.GetFileNameWithoutExtension(file);
                // languages.json 是声明文件，不是语言表
                if (string.Equals(lang, "languages", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    string json = File.ReadAllText(file);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
                    if (dict != null)
                    {
                        _tables[lang] = dict;
                        Debug.WriteLine($"[UILocalisation] 已载入语言: {lang}（{dict.Count} 键）");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UILocalisation] 跳过损坏的语言文件 {file}: {ex.Message}");
                }
            }
        }

        // 确保默认语言存在（空表兜底），当前语言不可用时回退默认
        if (!_tables.ContainsKey(DefaultLanguage))
            _tables[DefaultLanguage] = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_tables.ContainsKey(_currentLanguage))
            _currentLanguage = DefaultLanguage;

        // 防御性（尽力可用）：当前语言表为空但存在其他非空语言时，
        // 自动切到第一个非空语言，避免界面全部回退为键名。
        if (_tables.TryGetValue(_currentLanguage, out var currentTable) && currentTable.Count == 0)
        {
            var firstNonEmpty = _tables.FirstOrDefault(kv => kv.Value.Count > 0);
            if (firstNonEmpty.Value != null)
                _currentLanguage = firstNonEmpty.Key;
        }
    }

    /// <summary>取指定键的文本（规范 2.2）。回退链：当前语言 → 默认语言 → 键名。</summary>
    public string Get(string key, string? lang = null)
    {
        string l = lang ?? _currentLanguage;

        if (_tables.TryGetValue(l, out var table) && table.TryGetValue(key, out string? value))
            return value;

        if (!string.Equals(l, DefaultLanguage, StringComparison.OrdinalIgnoreCase)
            && _tables.TryGetValue(DefaultLanguage, out var def) && def.TryGetValue(key, out string? defValue))
            return defValue;

        return key; // 兜底：返回键名本身
    }

    /// <summary>取文本并格式化 {0}/{1} 占位符。</summary>
    public string Format(string key, params object?[] args)
        => string.Format(Get(key), args);

    /// <summary>
    /// 语言标识（UI 语言如 zh-CN，或 mod 本地化语言如 simp_chinese）→ 该语言的自称
    /// （endonym）：zh-CN → "简体中文"、en-US → "English"、french → "Français"。
    /// 优先 languages.json 声明；其次 lang.name.{code 小写} 键；未定义回退原标识。
    /// 用于**界面语言下拉**（设置页）。
    /// </summary>
    public string GetLanguageDisplayName(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return lang;
        if (_languageNames.TryGetValue(lang, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;
        return GetLanguageDisplayNameLocalized(lang);
    }

    /// <summary>
    /// 语言标识 → **当前界面语言下的译名**（统一语种）：中文界面下
    /// english → "英语"、simp_chinese → "简体中文"；英文界面下 → "English"/"Simplified Chinese"。
    /// 查 lang.name.{code 小写} 键（各语言文件按自身界面翻译）；未定义回退原标识。
    /// 用于**本地化编辑区的语种下拉**（星系样式"其他"选项卡）。
    /// </summary>
    public string GetLanguageDisplayNameLocalized(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return lang;
        string key = "lang.name." + lang.ToLowerInvariant();
        string name = Get(key);
        return string.Equals(name, key, StringComparison.Ordinal) ? lang : name;
    }

    /// <summary>读取语言声明文件 languages.json：{ "languages": [ { "code": "zh-CN", "name": "简体中文" } ] }。</summary>
    private void LoadLanguageDeclarations(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var doc = JsonSerializer.Deserialize<LanguageDeclarationFile>(File.ReadAllText(path), JsonOptions);
            if (doc?.Languages == null)
                return;
            foreach (var l in doc.Languages)
            {
                if (!string.IsNullOrWhiteSpace(l.Code))
                    _languageNames[l.Code] = string.IsNullOrWhiteSpace(l.Name) ? l.Code : l.Name;
            }
            Debug.WriteLine($"[UILocalisation] 语言声明已载入: {_languageNames.Count} 种");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UILocalisation] 语言声明解析失败 {path}: {ex.Message}");
        }
    }

    private sealed class LanguageDeclarationFile
    {
        public List<LanguageDeclaration>? Languages { get; set; }
    }

    private sealed class LanguageDeclaration
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>切换当前语言并触发 LanguageChanged（规范 2.2）。</summary>
    public void SetLanguage(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            throw new ArgumentException("语言标识不能为空", nameof(lang));
        if (!_tables.ContainsKey(lang))
            throw new ArgumentException($"未载入语言: {lang}", nameof(lang));
        if (string.Equals(_currentLanguage, lang, StringComparison.OrdinalIgnoreCase))
            return;

        _currentLanguage = lang;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
