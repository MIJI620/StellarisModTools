using Microsoft.Extensions.Logging;
using Stellaris.Parser;

namespace Stellaris.Engine.StaticModifier;

/// <summary>
/// 加成字典引擎（只读）：全 roots 扫描，建立三类加成索引（用户概念）：
/// - 静态加成（StaticModifierEntry）= `common/static_modifiers/` 顶层块——本地化键**不带 mod_ 前缀**
///   （如 6monthsocietycost / player_empire；icon / icon_frame / hide_from_country_list）。
/// - 自定义（scripted_modifiers 顶层块）= `common/scripted_modifiers/` 顶层块——本地化键**带 mod_ 前缀**
///   （并入 BaseModifier.DefinitionSources["scripted"]，页面按"自定义"分类显示）。
/// - 基础（BaseModifier）= **从 modifier 引用读出的键**（本地化 `mod_` 前缀词条对应的属性名；
///   全 AST 遍历任意文件 `modifier = { ... }` 块内键；`weight`/`ai_weight` 为父键的 modifier 块是
///   ai 权重条件，忽略）。
/// 纯查询——不落盘、不登记、不改 AST。
/// </summary>
public sealed class StaticModifierEngine
{
    /// <summary>静态加成可保存字段（字段级脏追踪——保存只写这些字段，未编辑/未知字段保留 AST 原样）。</summary>
    public static class StaticField
    {
        public const string Icon = "icon";
        public const string IconFrame = "icon_frame";
        public const string Hidden = "hide_from_country_list";
        public const string Important = "important";
        public const string CustomTooltip = "custom_tooltip";
        public const string ShowOnly = "show_only_custom_tooltip";
        public const string Refs = "refs";   // 引用键表（BaseRefs——多 Simple 节点）
    }

    private readonly StellarisAdapter _adapter;
    private readonly ILogger _logger;
    private readonly Parser.Rules.RulesReader _rules = new();

    // ==================== 字段 ====================
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? _allLocs;   // 本地化全集（lang → key → value）
    private readonly Dictionary<string, BaseModifier> _bases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StaticModifierEntry> _statics = new(StringComparer.Ordinal);   // 自定义大小写敏感（用户规则）
    private readonly Dictionary<string, List<StaticModifierEntry>> _staticIcons = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    /// <summary>排除父键（AST modifier 块祖先链检查）：key → 检查深度列表（0=自身、1=父、2=祖父……）。</summary>
    private readonly Dictionary<string, List<int>> _excludeKeys = new(StringComparer.Ordinal);
    /// <summary>排除关键词：modifier 块内**引用键**包含该词（忽略大小写）即排除（如 "$"）。</summary>
    private readonly HashSet<string> _excludeKeywords = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>完全匹配排除：modifier 块内引用键**精确等于**该列表（add/mult/trigger/factor 等语法成分）→ 不当作引用。</summary>
    private readonly HashSet<string> _excludeExact = new(StringComparer.Ordinal);
    /// <summary>value 拒绝：引用键的值**精确等于**该列表（默认 yes/no——开关/标志）→ 默认无效，不当作引用。</summary>
    private readonly HashSet<string> _excludeValues = new(StringComparer.OrdinalIgnoreCase);

    public StaticModifierEngine(StellarisAdapter adapter, ILogger logger,
        IReadOnlyDictionary<string, List<int>>? excludeKeys = null,
        IReadOnlyCollection<string>? excludeKeywords = null,
        IReadOnlyCollection<string>? excludeExact = null,
        IReadOnlyCollection<string>? excludeValues = null)
    {
        _adapter = adapter;
        _logger = logger;
        if (excludeKeys != null)
            foreach (var kv in excludeKeys)
                _excludeKeys[kv.Key] = new List<int>(kv.Value);
        if (excludeKeywords != null)
            _excludeKeywords.UnionWith(excludeKeywords);
        if (excludeExact != null)
            _excludeExact.UnionWith(excludeExact);
        if (excludeValues != null)
            _excludeValues.UnionWith(excludeValues);
        if (excludeKeys == null && excludeKeywords == null && excludeExact == null && excludeValues == null)
            LoadFromRules();
    }

    /// <summary>从专用规则读取器加载排除规则（所有规则统一走 RulesReader，模块不自己读文件）。</summary>
    private void LoadFromRules()
    {
        foreach (var kv in _rules.ExcludeKeys)
            _excludeKeys[kv.Key] = new List<int>(kv.Value);
        _excludeKeywords.UnionWith(_rules.ExcludeKeywords);
        _excludeExact.UnionWith(_rules.ExcludeExact);
        _excludeValues.UnionWith(_rules.ExcludeValues);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        ScanAll();   // ScanAll 自己全程持锁 + 幂等——后台预热与 UI 查询并发时不会写坏
    }

    // ==================== 数据模型 ====================

    /// <summary>基础条目：本地化 `mod_` 词条对应属性名（如 mod_country_produces_mult → country_produces_mult）。</summary>
    public sealed class BaseModifier
    {
        /// <summary>属性名（去 mod_ 前缀）。</summary>
        public string Name { get; internal set; } = string.Empty;
        /// <summary>本地化键 = mod_ + Name（拼的缺省——**真实键见 LocKey**）。</summary>
        public string ModKey => "mod_" + Name;
        /// <summary>**真实本地化键**（扫描时找到的首个命中键**原样大小写**——本地化前缀无视大小写，
        /// 真实键可能是 `MOD_SHIP_SPEED_MULT`；无 mod_ 词条时为不带前缀键。null = 无词条）。
        /// 查询/显示一律用真实键（用户 2026-08：不拼 mod_+Name）。</summary>
        public string? LocKey { get; internal set; }
        /// <summary>语种 → 本地化显示值（逻辑值原文）。</summary>
        public SortedDictionary<string, string> Localisations { get; } = new(StringComparer.Ordinal);
        /// <summary>调用它的自定义（static_modifiers 块内键命中）。</summary>
        public List<StaticModifierEntry> Users { get; } = new();
        /// <summary>外部引用文件（任意文件 modifier 块内键命中——去重）。</summary>
        public List<string> ExternalFiles { get; } = new();
        /// <summary>定义来源集合（"static"/"scripted"——**同一 key 可两者都有**，视为 2 个独立定义，
        /// 不混合、不覆盖）。</summary>
        public HashSet<string> DefinitionSources { get; } = new(StringComparer.Ordinal);
        /// <summary>各来源的声明文件（source → 相对路径列表——同 key 同来源可多个文件）。
        /// 游戏覆盖语义：static 只读一次（升序**最早**启用）、scripted 后读覆盖（升序**最晚**启用）。</summary>
        public Dictionary<string, List<string>> DefinitionFiles { get; } = new(StringComparer.Ordinal);
        /// <summary>各来源的覆盖规则（source → "只读一次"/"后读覆盖"…——从 rules/overwrite_rules.json 读取）。</summary>
        public Dictionary<string, string> OverwriteRules { get; } = new(StringComparer.Ordinal);
        /// <summary>是否有定义（static 或 scripted）——自定义基础的翻译不回退不带前缀词条。</summary>
        public bool IsCustomBase => DefinitionSources.Count > 0;
        /// <summary>是否有 static_modifiers 定义（静态）。</summary>
        public bool IsStaticDefinition => DefinitionSources.Contains("static");
        /// <summary>是否有 scripted_modifiers 定义（自定义）。</summary>
        public bool IsScriptedDefinition => DefinitionSources.Contains("scripted");

        /// <summary>取某来源**实际被游戏启用**的文件：按覆盖规则——
        /// "只读一次" = 相对路径升序第一个；"后读覆盖" = 升序最后一个；未配置默认后读覆盖。</summary>
        public string? GetActiveFile(string source)
        {
            if (!DefinitionFiles.TryGetValue(source, out var list) || list.Count == 0)
                return null;
            var sorted = list.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var rule = OverwriteRules.TryGetValue(source, out var r) ? r : null;
            return rule == "只读一次" ? sorted[0] : sorted[^1];
        }
    }

    /// <summary>自定义条目：static_modifiers 顶层块。</summary>
    public sealed class StaticModifierEntry
    {
        /// <summary>引用记录：块内键 + 值 + 命中的基础。</summary>
        public sealed class BaseRef
        {
            public string Key { get; internal set; } = string.Empty;
            public string Value { get; internal set; } = string.Empty;
            public BaseModifier Base { get; internal set; } = null!;
        }

        public string Name { get; internal set; } = string.Empty;
        /// <summary>原始块引用（解析时保存——保存时字段级更新，非脏字段/未知字段保留 AST 原样，用户 2026-08）。</summary>
        internal AstNode? OriginalBlock { get; set; }
        /// <summary>icon 路径（icon = "gfx/..."）。</summary>
        public string? Icon { get; internal set; }
        public int IconFrame { get; internal set; }
        /// <summary>hide_from_country_list = yes。</summary>
        public bool Hidden { get; internal set; }
        /// <summary>important = yes（是否重要——静态加成特殊字段，用户 2026-08）。</summary>
        public bool Important { get; internal set; }
        /// <summary>custom_tooltip = "本地化键"（自定义提示——本地化组件键 = 此值）。</summary>
        public string? CustomTooltip { get; internal set; }
        /// <summary>show_only_custom_tooltip = yes（只显示自定义提示）。</summary>
        public bool ShowOnlyCustomTooltip { get; internal set; }
        public string? SourceFile { get; internal set; }
        /// <summary>引用的基础（块内键命中 _bases）。</summary>
        public List<BaseModifier> Bases { get; } = new();
        /// <summary>引用的基础（含引用键与值——详情展示 `键 = 值`）。</summary>
        public List<BaseRef> BaseRefs { get; } = new();
        /// <summary>待解析引用（扫描时暂存——所有基础注册完后再统一解析，顺序无关）。
        /// **排除规则只对 AST**（WalkModifierRefs）——自定义块内键不应用排除。</summary>
        internal List<(string Key, string Value)> PendingRefs { get; } = new();
        /// <summary>块内未判定键（icon 等特殊键除外——条件/未知）。</summary>
        public List<string> UnknownKeys { get; } = new();
        public SortedDictionary<string, string> Localisations { get; } = new(StringComparer.Ordinal);
    }

    // ==================== 扫描 ====================

    /// <summary>执行全量扫描（幂等——App 可后台预热；查询 EnsureLoaded 不会重复扫）。
    /// 全程持锁：后台预热与 UI 查询并发时串行化，避免非并发 Dictionary 写坏。
    /// 扫描前清空旧索引，结束后置 _loaded=true。</summary>
    public void ScanAll()
    {
        lock (_bases)
        {
            if (_loaded) return;
            _bases.Clear();
        _statics.Clear();
        _staticIcons.Clear();

        // 0) 数据准备：本地化全集引用（static/scripted 翻译与最后一步本地化搜索共用）
        _allLocs = _adapter.GetAllLocalisations();

        // 2) common/static_modifiers 顶层块 → 自定义
        foreach (var rel in _adapter.GetFilesRecursive("common/static_modifiers", "*.txt"))
        {
            var cfg = _adapter.GetConfig(rel);
            if (cfg == null) continue;
            foreach (var node in cfg.RootNodes)
            {
                // 顶层块 = 自定义；空块 `x = { }` 解析为 List（解析器规则）——也收
                if ((node.Type != NodeType.Block && node.Type != NodeType.List)
                    || string.IsNullOrEmpty(node.Key))
                    continue;
                var ce = ParseCustomBlock(node, rel);
                if (ce == null) continue;
                _statics[ce.Name] = ce;
                // static 定义直接创建/标记（代码里有就要显示；同 key 与 scripted 视为 2 个独立定义）
                var be = GetOrAddBase(ce.Name);
                be.DefinitionSources.Add("static");
                be.OverwriteRules["static"] = _rules.GetOverwriteRule("static_modifiers") ?? "只读一次";
                if (!be.DefinitionFiles.TryGetValue("static", out var sfl))
                    be.DefinitionFiles["static"] = sfl = new List<string>();
                sfl.Add(rel);   // 声明处（static_modifiers 文件）
                // 静态加成（static_modifiers 顶层块）的本地化键 = **不带 mod_ 前缀**（如 6monthsocietycost）；
                // 也兼容个别带 mod_ 前缀的词条（两者都收，不带前缀优先）。
                if (_allLocs != null)
                {
                    foreach (var (lang, dict) in _allLocs)
                    {
                        if (dict.TryGetValue(ce.Name, out var v))
                            ce.Localisations[lang] = v;   // 不带前缀
                        else if (dict.TryGetValue("mod_" + ce.Name, out var vm))
                            ce.Localisations[lang] = vm;   // 兼容带前缀
                    }
                }
                if (!string.IsNullOrEmpty(ce.Icon))
                {
                    if (!_staticIcons.TryGetValue(ce.Icon, out var list))
                        _staticIcons[ce.Icon] = list = new List<StaticModifierEntry>();
                    list.Add(ce);
                }
            }
        }

        // 3) common/scripted_modifiers 顶层块 → **自定义基础**（代码里有就要显示——创建基础；
        //    名 = 代码 key 原样；翻译 = mod_ + 代码 key 词条；**不回退**不带前缀词条）
        foreach (var rel in _adapter.GetFilesRecursive("common/scripted_modifiers", "*.txt"))
        {
            var cfg = _adapter.GetConfig(rel);
            if (cfg == null) continue;
            foreach (var node in cfg.RootNodes)
            {
                if ((node.Type != NodeType.Block && node.Type != NodeType.List)
                    || string.IsNullOrEmpty(node.Key))
                    continue;
                var sbe = GetOrAddBase(node.Key!);
                sbe.DefinitionSources.Add("scripted");
                sbe.OverwriteRules["scripted"] = _rules.GetOverwriteRule("scripted_modifiers") ?? "后读覆盖";
                if (!sbe.DefinitionFiles.TryGetValue("scripted", out var sfl2))
                    sbe.DefinitionFiles["scripted"] = sfl2 = new List<string>();
                sfl2.Add(rel);   // 声明处（scripted_modifiers 文件）
                // scripted 翻译键 = mod_ + 代码 key（mod_trade_league_3 → mod_mod_trade_league_3）
                var locKey = "mod_" + node.Key;
                if (_allLocs != null)
                {
                    foreach (var (lang, dict) in _allLocs)
                    {
                        if (dict.TryGetValue(locKey, out var v))
                            sbe.Localisations[lang] = v;
                    }
                }
            }
        }

        // 3.5) 解析自定义引用（此刻 static + scripted 基础已注册——引用即断言）
        foreach (var ce in _statics.Values)
        {
            foreach (var (refKey, refValue) in ce.PendingRefs)
            {
                if (TryResolveBase(refKey, out var rbe))
                {
                    ce.Bases.Add(rbe);
                    ce.BaseRefs.Add(new StaticModifierEntry.BaseRef
                    {
                        Key = refKey,
                        Value = refValue,
                        Base = rbe
                    });
                    rbe.Users.Add(ce);
                }
                else
                    ce.UnknownKeys.Add(refKey);
            }
            ce.PendingRefs.Clear();
        }

        // 4) 全 AST 遍历：任意文件 modifier 块 → 引用（weight/ai_weight 父键跳过）
        foreach (var (rel, result) in _adapter.GetAllConfigs())
        {
            // 排除目录：common/script_values 内的 modifier 块不提取（脚本化数值，非加成）
            if (rel.StartsWith("common/script_values/", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var node in result.RootNodes)
                WalkModifierRefs(node, rel, new List<string>());
        }

    
        // 5) 本地化 mod_ 词条 → 基础（**最后才去搜本地化**——先自定义基础、再代码引用、最后本地化）
        //    前缀无视大小写（用户规则）：MOD_SHIP_SPEED_MULT / mod_ship_speed_mult 都关联到 ship_speed_mult 基础
        _allLocs = _adapter.GetAllLocalisations();
        foreach (var (lang, dict) in _allLocs)
        {
            foreach (var (key, value) in dict)
            {
                if (!key.StartsWith("mod_", StringComparison.OrdinalIgnoreCase))
                    continue;
                var name = key.Substring(4);
                var be = GetOrAddBase(name);   // _bases 大小写不敏感——MOD_SHIP_SPEED_MULT 命中 ship_speed_mult
                be.Localisations[lang] = value;
                be.LocKey ??= key;   // 真实键原样（首命中——大写 MOD_ 保留，用户 2026-08）
            }
        }


        // 6) 非自定义基础翻译回退：找不到带前缀（mod_K）词条时找不带前缀（K 词条）
        //     （自定义基础不回退——用户规则："自定义的基础无论如何不能在本地话中直接搜不带本地化前缀的"）
        foreach (var be in _bases.Values)
        {
            if (be.IsCustomBase || _allLocs == null)
                continue;
            if (be.LocKey != null)
            {
                // 有真实 mod_ 键：按原逻辑补齐缺失语言值（不带前缀 TryGetValue——与原行为一致）
                foreach (var (lang, dict) in _allLocs)
                    if (dict.TryGetValue(be.Name, out var v) && !be.Localisations.ContainsKey(lang))
                        be.Localisations[lang] = v;
                continue;
            }
            // 无 mod_ 词条：**无视大小写**找不带前缀真实键（用户规则：基础不敏感；真实键原样记录 LocKey）
            foreach (var (lang, dict) in _allLocs)
            {
                if (be.Localisations.ContainsKey(lang))
                    continue;
                foreach (var k in dict.Keys)
                {
                    if (string.Equals(k, be.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        be.Localisations[lang] = dict[k];
                        be.LocKey ??= k;
                        break;
                    }
                }
            }
        }

        _logger.LogInformation("加成字典索引完成：基础 {Bases} 个、自定义 {Customs} 个",
                _bases.Count, _statics.Count);
            _loaded = true;
        }
    }

    private static readonly HashSet<string> SpecialKeys = new(StringComparer.Ordinal)
    {
        "icon", "icon_frame", "hide_from_country_list", "custom_tooltip", "format", "is_custom_tooltip",
        "show_only_custom_tooltip", "important"
    };

    /// <summary>解析引用键 → 基础：**代码 key 原样**（引用 `mod_xxx` 的名字就是 mod_xxx——不删前缀）。
    /// 未命中时**引用即断言**——创建基础（本地化/scripted 单独都不能断言，但任意 modifier 里
    /// 引用 xxx=n 就是第三方证据，直接断言有 xxx）。</summary>
    private bool TryResolveBase(string key, out BaseModifier be)
    {
        if (_bases.TryGetValue(key, out be))
            return true;
        be = GetOrAddBase(key);
        return true;
    }

    private BaseModifier GetOrAddBase(string name)
    {
        if (!_bases.TryGetValue(name, out var be))
            _bases[name] = be = new BaseModifier { Name = name };
        return be;
    }

    private StaticModifierEntry? ParseCustomBlock(AstNode block, string rel)
    {
        var ce = new StaticModifierEntry
        {
            Name = block.Key!,
            SourceFile = rel,
            OriginalBlock = block   // 原始块引用（保存时字段级更新用）
        };
        foreach (var child in block.Children)
        {
            if (child.Type != NodeType.Simple || string.IsNullOrEmpty(child.Key))
                continue;
            var key = child.Key;
            // 特殊字段（icon/icon_frame/hide_from_country_list——自身语义，不是引用）
            if (key == "icon" && child.Value is string iconStr)
            {
                ce.Icon = iconStr;
                continue;
            }
            if (key == "icon_frame")
            {
                if (child.Value is int fi)
                    ce.IconFrame = fi;
                else if (child.Value is string fs && int.TryParse(fs, out var f2))
                    ce.IconFrame = f2;
                continue;
            }
            if (key == "hide_from_country_list"
                && string.Equals(child.Value as string, "yes", StringComparison.OrdinalIgnoreCase))
            {
                ce.Hidden = true;
                continue;
            }
            // 静态加成特殊字段（用户 2026-08：important/custom_tooltip/show_only_custom_tooltip 自身语义，不是引用）
            if (key == "important"
                && string.Equals(child.Value as string, "yes", StringComparison.OrdinalIgnoreCase))
            {
                ce.Important = true;
                continue;
            }
            if (key == "custom_tooltip" && child.Value is string ct)
            {
                ce.CustomTooltip = ct.Trim().Trim('"');
                continue;
            }
            if (key == "show_only_custom_tooltip"
                && string.Equals(child.Value as string, "yes", StringComparison.OrdinalIgnoreCase))
            {
                ce.ShowOnlyCustomTooltip = true;
                continue;
            }
            // 通用 Simple 判断（连接符 = / exclude / 值类型）——与 modifier 块内一致，
            // 不因在 static_modifiers 顶层块内而跳过；Block 子节点按 Block 判断（Type != Simple 已过滤）
            if (!IsBaseReferenceCandidate(child))
                continue;
            // 引用键：暂存（待所有基础注册后统一解析——scripted/static 定义先后无关）
            ce.PendingRefs.Add((key, child.Value?.ToString() ?? ""));
        }
        return ce;
    }

    /// <summary>Simple 是否可作为基础引用（**Block 是 Block 的判断、Simple 是 Simple 的判断**——
    /// 此处只判断 Simple）：连接符必须 =；键非 exclude_exact/exclude_keywords/exclude_values；
    /// 值必须数值或 @ 常量引用。static_modifiers 顶层块内字段同样走此判断（不因在静态内跳过）。</summary>
    private bool IsBaseReferenceCandidate(AstNode child)
    {
        if (child.Type != NodeType.Simple || string.IsNullOrEmpty(child.Key))
            return false;
        if (SpecialKeys.Contains(child.Key) || _excludeExact.Contains(child.Key))
            return false;
        foreach (var kw in _excludeKeywords)
        {
            if (child.Key.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (child.SeparatorType.HasValue && child.SeparatorType.Value != TokenType.Equals)
            return false;
        if (child.Value is string yv && _excludeValues.Contains(yv))
            return false;
        // 数值判定：强类型数值（SA 解析层已把 +/- 紧跟数字解析为数值——2026-08 修复，引擎无需字符串兜底）
        bool numericValue = child.Value is double or float or int or long or decimal;
        bool constRef = child.RawText != null
            && child.RawText.TrimStart().StartsWith("@", StringComparison.Ordinal);
        return numericValue || constRef;
    }

    private void WalkModifierRefs(AstNode node, string rel, List<string> ancestors)
    {
        // 随机概率类（**只对 AST**）：modifier 块按 exclude_keys 配置的深度检查祖先链——
        // ancestors[0]=父、[1]=祖父……；depth 0 = 查自身（modifier 块 key）。
        // 命中（或祖先 key 含排除关键词）→ 跳过（不当作引用）
        bool isProb = false;
        if (node.Type == NodeType.Block
            && string.Equals(node.Key, "modifier", StringComparison.Ordinal))
        {
            foreach (var (exKey, depths) in _excludeKeys)
            {
                foreach (var d in depths)
                {
                    // ancestors 按递归序存放（最早=最外层）；depth 1 = 父 = 列表最后一个
                    string? target = d == 0 ? node.Key
                        : (d >= 1 && ancestors.Count >= d ? ancestors[ancestors.Count - d] : null);
                    if (target != null && string.Equals(target, exKey, StringComparison.Ordinal))
                    {
                        isProb = true;
                        break;
                    }
                }
                if (isProb) break;
            }
        }
        if (node.Type == NodeType.Block
            && string.Equals(node.Key, "modifier", StringComparison.Ordinal)
            && !isProb)
        {
            foreach (var child in node.Children)
            {
                if (!IsBaseReferenceCandidate(child))
                    continue;
                if (TryResolveBase(child.Key, out var be))
                {
                    if (!be.ExternalFiles.Contains(rel, StringComparer.OrdinalIgnoreCase))
                        be.ExternalFiles.Add(rel);
                }
            }
        }
        foreach (var child in node.Children)
        {
            ancestors.Add(node.Key ?? "");
            WalkModifierRefs(child, rel, ancestors);
            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }

    // ==================== 查询 ====================

    /// <summary>全部基础（按名排序）。</summary>
    public IReadOnlyList<BaseModifier> GetAllBaseModifiers()
    {
        EnsureLoaded();
        return _bases.Values.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>全部自定义（按名排序）。</summary>
    public IReadOnlyList<StaticModifierEntry> GetStaticModifiers()
    {
        EnsureLoaded();
        return _statics.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ==================== 内存编辑（本期不落盘——用户 2026-08） ====================

    /// <summary>内存新建条目（不落盘）：加入 _newItems，与扫描条目合并显示。</summary>
    private readonly List<StaticModifierEntry> _newItems = new();
    /// <summary>待保存登记（字段级——保存只写这些字段；空集 = 只写文件，用户 2026-08 参考法令）。</summary>
    private readonly Dictionary<StaticModifierEntry, HashSet<string>> _dirty = new();
    /// <summary>删除登记（保存时从文件 AST 移除块 + 删本地化词条；不直接改内存——防数据丢失）。</summary>
    private readonly HashSet<StaticModifierEntry> _removed = new();

    /// <summary>全部条目 = 扫描现有 + 内存新建（按名排序；新建项不重复扫描 key；**删除登记项过滤**——用户 2026-08）。</summary>
    public IReadOnlyList<StaticModifierEntry> GetItems()
    {
        var result = GetStaticModifiers().Where(e => !_removed.Contains(e)).ToList();
        result.AddRange(_newItems.Where(n => !_statics.ContainsKey(n.Name)));
        return result.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>内存新建（不落盘——登记待保存，用户触发保存才落盘）。key 已存在（扫描或内存）返回 null。</summary>
    public StaticModifierEntry? AddItem(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || _statics.ContainsKey(key)
            || _newItems.Any(n => string.Equals(n.Name, key, StringComparison.OrdinalIgnoreCase)))
            return null;
        var entry = new StaticModifierEntry { Name = key };
        _newItems.Add(entry);
        _dirty[entry] = new HashSet<string>(StringComparer.Ordinal);   // 新建：登记待保存（保存时全字段写）
        return entry;
    }

    /// <summary>删除条目（**登记式**——保存时从文件 AST 移除块 + 删本地化词条；新建项同时从内存移除；用户 2026-08）。</summary>
    public void RemoveItem(StaticModifierEntry entry)
    {
        if (entry == null)
            return;
        _newItems.RemoveAll(n => ReferenceEquals(n, entry) || string.Equals(n.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        _dirty.Remove(entry);
        _removed.Add(entry);
    }

    /// <summary>登记某字段被修改（页面编辑控件触发；保存时只写这些字段——参考法令）。</summary>
    public void MarkDirty(StaticModifierEntry entry, string field)
    {
        if (entry == null)
            return;
        if (!_dirty.TryGetValue(entry, out var set))
            _dirty[entry] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(field);
    }

    /// <summary>登记条目有改动（空字段集——只写文件不动字段；供所属文件等非字段变化，用户 2026-08）。</summary>
    public void MarkItemDirty(StaticModifierEntry entry)
    {
        if (entry == null)
            return;
        if (!_dirty.TryGetValue(entry, out _))
            _dirty[entry] = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>更新条目引用键表（页面加成表格 → 条目；供保存写回），并登记 Ref 字段。</summary>
    public void SetEntryRefs(StaticModifierEntry entry, IEnumerable<(string Key, string Value)> refs)
    {
        if (entry == null)
            return;
        entry.Bases.Clear();
        entry.BaseRefs.Clear();
        foreach (var (k, v) in refs)
        {
            if (string.IsNullOrWhiteSpace(k))
                continue;
            var be = GetOrAddBase(k);
            entry.Bases.Add(be);
            entry.BaseRefs.Add(new StaticModifierEntry.BaseRef { Key = k, Value = v, Base = be });
        }
        MarkDirty(entry, StaticField.Refs);
    }

    /// <summary>是否有待保存改动。</summary>
    public bool HasDirty => _dirty.Count > 0 || _removed.Count > 0;

    /// <summary>更新条目 icon（内存；不落盘）。</summary>
    public void UpdateItemIcon(StaticModifierEntry entry, string? icon)
    {
        if (entry != null)
            entry.Icon = string.IsNullOrWhiteSpace(icon) ? null : icon;
    }

    /// <summary>更新条目静态加成特殊字段（内存；不落盘）：hide_from_country_list / important / icon_frame /
    /// show_only_custom_tooltip / custom_tooltip。null = 不改；customTooltip 非 null 即设置（空串 = 清空）。</summary>
    public void UpdateItemMeta(StaticModifierEntry entry, bool? hidden = null, bool? important = null,
        int? iconFrame = null, bool? showOnlyTooltip = null, string? customTooltip = null)
    {
        if (entry == null)
            return;
        if (hidden.HasValue)
            entry.Hidden = hidden.Value;
        if (important.HasValue)
            entry.Important = important.Value;
        if (iconFrame.HasValue)
            entry.IconFrame = iconFrame.Value;
        if (showOnlyTooltip.HasValue)
            entry.ShowOnlyCustomTooltip = showOnlyTooltip.Value;
        if (customTooltip != null)
            entry.CustomTooltip = string.IsNullOrWhiteSpace(customTooltip) ? null : customTooltip.Trim();
    }

    /// <summary>更新条目所属文件（内存；不落盘——本期静态加成不落盘，用户 2026-08 仅显示/记录）。</summary>
    public void UpdateItemSourceFile(StaticModifierEntry entry, string? sourceFile)
    {
        if (entry != null)
            entry.SourceFile = string.IsNullOrWhiteSpace(sourceFile) ? null : sourceFile;
    }

    // ==================== 保存（用户显式触发——参考法令：待保存索引 + 字段级 + 本地化；全部经 SA） ====================

    /// <summary>目标文件：所属文件 ?? 默认 common/static_modifiers/00_{prefix}_static_modifiers.txt。</summary>
    public string TargetRelPath(StaticModifierEntry entry, string modPrefix)
        => entry.SourceFile ?? $"common/static_modifiers/00_{modPrefix}_static_modifiers.txt";

    /// <summary>统一保存：写登记的全部 static_modifiers 文件（删除块 + 字段级应用 dirty 块）+ 本地化
    /// （localisation/{lang}/modifiers_{prefix}_l_{lang}.yml——static_ 删掉，用户 2026-08）。
    /// 数据源 = SA GetConfig 合并 AST（不重建文件）；写 = SA WriteFile（roots[-1] + 自动建目录）。
    /// 返回 (成功文件数, 错误列表)。</summary>
    public (int Saved, List<string> Errors) SaveAll(string modPrefix)
    {
        var errors = new List<string>();
        if (!HasDirty)
            return (0, errors);
        int saved = 0;

        // ---- 1) static_modifiers .txt：删除块 + 字段级应用 dirty 块 ----
        var byFile = new Dictionary<string, List<StaticModifierEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _dirty.Keys)
        {
            var rel = TargetRelPath(e, modPrefix);
            if (!byFile.TryGetValue(rel, out var l))
                byFile[rel] = l = new List<StaticModifierEntry>();
            l.Add(e);
        }
        var removedByFile = new Dictionary<string, List<StaticModifierEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _removed)
        {
            var rel = TargetRelPath(e, modPrefix);
            if (!removedByFile.TryGetValue(rel, out var l))
                removedByFile[rel] = l = new List<StaticModifierEntry>();
            l.Add(e);
        }
        var allFiles = new HashSet<string>(byFile.Keys.Concat(removedByFile.Keys), StringComparer.OrdinalIgnoreCase);
        foreach (var rel in allFiles)
        {
            var result = _adapter.GetConfig(rel);
            if (result == null)
            {
                _adapter.CreateEmptyFileInMemory(rel, FileCategory.Config);
                result = _adapter.GetConfig(rel);
                if (result == null)
                {
                    errors.Add(rel + ": 无法初始化配置文件");
                    continue;
                }
            }
            if (removedByFile.TryGetValue(rel, out var removedItems))
                foreach (var e in removedItems)
                {
                    var block = result.RootNodes.FirstOrDefault(n => n.Type == NodeType.Block
                        && (string.Equals(n.Key, e.Name, StringComparison.Ordinal)
                            || e.OriginalBlock != null && ReferenceEquals(n, e.OriginalBlock)));
                    if (block != null)
                        result.RootNodes.Remove(block);
                }
            if (byFile.TryGetValue(rel, out var items))
                foreach (var e in items)
                    ApplyEntryBlock(result, e);
            if (_adapter.WriteFile(rel))
                saved++;
            else
                errors.Add(rel + ": 写入失败");
        }

        // ---- 2) 本地化：modifiers_{prefix}_l_{lang}.yml + 删除词条清理 ----
        if (errors.Count == 0)
        {
            var files = new HashSet<string>(StringComparer.Ordinal);      // "lang\0相对路径"
            var cleanFiles = new HashSet<string>(StringComparer.Ordinal); // 旧位置/删除清理（writeIfEmpty:true）
            foreach (var e in _dirty.Keys)
                foreach (var lang in EnabledLanguages())
                    WriteEntryLocalisation(e, lang, modPrefix, errors, files, cleanFiles);
            foreach (var e in _removed)
                foreach (var lang in EnabledLanguages())
                {
                    var index = _adapter.GetLocalisationKeyFiles(lang);
                    foreach (var k in new[] { e.Name, e.Name + "_desc" })
                        if (index.TryGetValue(k, out var cur))
                        {
                            _adapter.RemoveLocalisationEntry(lang, cur, k);
                            cleanFiles.Add(lang + "\u0000" + cur);
                        }
                }
            if (errors.Count == 0)
            {
                foreach (var f in files)
                {
                    var parts = f.Split('\u0000');
                    var rel = parts[1];
                    var fileName = rel.Substring(rel.LastIndexOf('/') + 1);
                    if (!_adapter.WriteLocalisation(parts[0], fileName))
                        errors.Add("本地化写入失败: " + parts[0] + "/" + rel);
                }
                foreach (var f in cleanFiles)
                {
                    var parts = f.Split('\u0000');
                    var rel = parts[1];
                    var fileName = rel.Substring(rel.LastIndexOf('/') + 1);
                    if (!_adapter.WriteLocalisation(parts[0], fileName, writeIfEmpty: true))
                        errors.Add("本地化删除写入失败: " + parts[0] + "/" + rel);
                }
            }
        }

        // ---- 3) 全部成功：清空登记 ----
        if (errors.Count == 0)
        {
            _dirty.Clear();
            _removed.Clear();
        }
        return (saved, errors);
    }

    /// <summary>把 dirty 静态加成块应用到文件 AST（字段级：只写脏字段，未编辑/未知字段保留 AST 原样；
    /// 块不存在 → 新建全字段）。</summary>
    private void ApplyEntryBlock(ParserResult result, StaticModifierEntry e)
    {
        var block = e.OriginalBlock
            ?? result.RootNodes.FirstOrDefault(n => n.Type == NodeType.Block
                && string.Equals(n.Key, e.Name, StringComparison.Ordinal));
        bool isNew = block == null;
        if (block == null)
        {
            block = new AstNode { Type = NodeType.Block, Key = e.Name, Children = new List<AstNode>() };
            result.RootNodes.Add(block);
            e.OriginalBlock = block;
        }
        if (block.Children == null)
            block.Children = new List<AstNode>();
        var fields = isNew
            ? new HashSet<string>(StaticOrderedFields, StringComparer.Ordinal)
            : (_dirty.TryGetValue(e, out var s) ? s : new HashSet<string>(StringComparer.Ordinal));
        foreach (var f in StaticOrderedFields)
        {
            if (!fields.Contains(f))
                continue;
            if (f == StaticField.Refs)
            {
                // 引用键表：整组替换——移除所有非特殊键 Simple 节点（引用键），按 BaseRefs 重建
                block.Children.RemoveAll(c => c.Type == NodeType.Simple && !StaticSpecialKeys.Contains(c.Key));
                foreach (var r in e.BaseRefs)
                    block.Children.Add(new AstNode { Type = NodeType.Simple, Key = r.Key, Value = r.Value });
                continue;
            }
            var node = BuildStaticFieldNode(e, f);
            if (node == null)
            {
                block.Children.RemoveAll(c => string.Equals(c.Key, f, StringComparison.Ordinal));
                continue;
            }
            var idx = block.Children.FindIndex(c => string.Equals(c.Key, f, StringComparison.Ordinal));
            if (idx >= 0)
                block.Children[idx] = node;
            else
                block.Children.Add(node);
        }
    }

    /// <summary>字段写回顺序。</summary>
    private static readonly string[] StaticOrderedFields =
    {
        StaticField.Icon, StaticField.IconFrame, StaticField.Hidden, StaticField.Important,
        StaticField.CustomTooltip, StaticField.ShowOnly, StaticField.Refs
    };

    /// <summary>特殊键（引用键表重建时保留这些 Simple 节点）。</summary>
    private static readonly HashSet<string> StaticSpecialKeys = new(StringComparer.Ordinal)
    {
        "icon", "icon_frame", "hide_from_country_list", "important", "custom_tooltip",
        "show_only_custom_tooltip", "format", "is_custom_tooltip"
    };

    /// <summary>字段 → AST 节点（null = 空值 → 移除该字段）。</summary>
    private static AstNode? BuildStaticFieldNode(StaticModifierEntry e, string field)
    {
        switch (field)
        {
            case StaticField.Icon:
                if (string.IsNullOrWhiteSpace(e.Icon))
                    return null;
                return new AstNode { Type = NodeType.Simple, Key = "icon", Value = e.Icon };
            case StaticField.IconFrame:
                if (e.IconFrame == 0)
                    return null;
                return new AstNode { Type = NodeType.Simple, Key = "icon_frame", Value = e.IconFrame };
            case StaticField.Hidden:
                return e.Hidden
                    ? new AstNode { Type = NodeType.Simple, Key = "hide_from_country_list", Value = "yes" }
                    : null;
            case StaticField.Important:
                return e.Important
                    ? new AstNode { Type = NodeType.Simple, Key = "important", Value = "yes" }
                    : null;
            case StaticField.CustomTooltip:
                if (string.IsNullOrWhiteSpace(e.CustomTooltip))
                    return null;
                return new AstNode { Type = NodeType.Simple, Key = "custom_tooltip", Value = e.CustomTooltip };
            case StaticField.ShowOnly:
                return e.ShowOnlyCustomTooltip
                    ? new AstNode { Type = NodeType.Simple, Key = "show_only_custom_tooltip", Value = "yes" }
                    : null;
            default:
                return null;
        }
    }

    /// <summary>保存本地化的语种：已加载语种 ?? 默认。</summary>
    private IReadOnlyList<string> EnabledLanguages()
    {
        var loaded = _adapter.GetLocalisationLanguages();
        if (loaded.Count > 0)
            return loaded;
        return new List<string> { "english", "simp_chinese" };
    }

    /// <summary>写单项本地化词条（名称键 = Name 不带前缀；描述 {Name}_desc）：目标 modifiers_{prefix}_l_{lang}.yml；
    /// 键已存在且不在目标 → 旧位置登记清理。名称没填用 key（参考法令）。</summary>
    private void WriteEntryLocalisation(StaticModifierEntry e, string lang, string modPrefix,
        List<string> errors, HashSet<string> files, HashSet<string> cleanFiles)
    {
        var fileName = $"modifiers_{modPrefix}_l_{lang}.yml";
        var targetPath = $"localisation/{lang}/{fileName}";
        try
        {
            var index = _adapter.GetLocalisationKeyFiles(lang);
            // 当前逻辑值（弹窗编辑过 → adapter 内存最新；否则扫描值）
            var nameCur = _adapter.GetLocalisedLogicalText(e.Name, lang);
            var nameValue = string.IsNullOrWhiteSpace(nameCur)
                ? (e.Localisations.TryGetValue(lang, out var lv) && !string.IsNullOrWhiteSpace(lv) ? lv : e.Name)
                : nameCur;
            if (index.TryGetValue(e.Name, out var nf) && !string.Equals(nf, targetPath, StringComparison.OrdinalIgnoreCase))
                cleanFiles.Add(lang + "\u0000" + nf);
            _adapter.UpdateLocalisationEntry(lang, targetPath, e.Name, nameValue);
            files.Add(lang + "\u0000" + targetPath);
            var descCur = _adapter.GetLocalisedLogicalText(e.Name + "_desc", lang);
            if (!string.IsNullOrWhiteSpace(descCur))
            {
                if (index.TryGetValue(e.Name + "_desc", out var df) && !string.Equals(df, targetPath, StringComparison.OrdinalIgnoreCase))
                    cleanFiles.Add(lang + "\u0000" + df);
                _adapter.UpdateLocalisationEntry(lang, targetPath, e.Name + "_desc", descCur);
                files.Add(lang + "\u0000" + targetPath);
            }
        }
        catch (Exception ex)
        {
            errors.Add(e.Name + ": 本地化写入失败（" + lang + "） " + ex.Message);
        }
    }

    /// <summary>取单个基础（null 不存在）。</summary>
    public BaseModifier? GetBaseModifier(string name)
    {
        EnsureLoaded();
        return _bases.TryGetValue(name, out var be) ? be : null;
    }

    /// <summary>取单个自定义（null 不存在）。</summary>
    public StaticModifierEntry? GetCustom(string name)
    {
        EnsureLoaded();
        return _statics.TryGetValue(name, out var ce) ? ce : null;
    }

    /// <summary>某一基础 → 所有调用它的自定义。</summary>
    public IReadOnlyList<StaticModifierEntry> GetCustomsOf(string baseName)
    {
        var be = GetBaseModifier(baseName);
        return be == null ? Array.Empty<StaticModifierEntry>() : be.Users;
    }

    /// <summary>某一自定义 → 它调用的基础。</summary>
    public IReadOnlyList<BaseModifier> GetBasesOf(string customName)
    {
        var ce = GetCustom(customName);
        return ce == null ? Array.Empty<BaseModifier>() : ce.Bases;
    }

    /// <summary>某一自定义 → 未判定键。</summary>
    public IReadOnlyList<string> GetUnknownKeys(string customName)
    {
        var ce = GetCustom(customName);
        return ce == null ? Array.Empty<string>() : ce.UnknownKeys;
    }

    /// <summary>图标路径 → 使用它的自定义。</summary>
    public IReadOnlyList<StaticModifierEntry> GetByIcon(string iconPath)
    {
        EnsureLoaded();
        return _staticIcons.TryGetValue(iconPath, out var list)
            ? list.ToList()
            : Array.Empty<StaticModifierEntry>();
    }

    /// <summary>按隐藏状态筛选自定义。</summary>
    public IReadOnlyList<StaticModifierEntry> FilterByHidden(bool hidden)
    {
        EnsureLoaded();
        return _statics.Values.Where(c => c.Hidden == hidden)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>关键词搜索（正则/子串，只搜不替换）：基础 + 自定义混合。
    /// 命中范围：名称 / 本地化显示值 / 自定义图标路径。按类型排序（基础在前）。</summary>
    public IReadOnlyList<object> Search(string keyword)
    {
        EnsureLoaded();
        var trimmed = keyword?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return Array.Empty<object>();
        bool isRegex = trimmed.Length > 1
            && (trimmed.Contains('*') || trimmed.Contains('?') || trimmed.Contains('|'));
        bool Matches(string hay)
        {
            if (hay.Length == 0) return false;
            return isRegex ? SimpleGlobMatch(hay, trimmed) : hay.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
        }
        var results = new List<object>();
        foreach (var be in _bases.Values)
        {
            if (Matches(be.Name) || be.Localisations.Values.Any(v => Matches(v)))
                results.Add(be);
        }
        foreach (var ce in _statics.Values)
        {
            if (Matches(ce.Name) || ce.Localisations.Values.Any(v => Matches(v))
                || (ce.Icon != null && Matches(ce.Icon)))
                results.Add(ce);
        }
        return results;
    }

    /// <summary>简单通配匹配（* = 任意串，? = 单字符）——不用正则（项目禁正则）。</summary>
    private static bool SimpleGlobMatch(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                t++; p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
