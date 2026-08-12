using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellaris.Parser;

/// <summary>解析错误告知（内存对象——不抛异常、不连带上层爆掉；上层按需读取决定处理）。</summary>
public sealed class SelectError
{
    /// <summary>错误/提示信息（如"index=2 越界"、"功能已废弃"）。</summary>
    public string Message { get; set; } = "";
    /// <summary>出错的原始选择器（便于定位）。</summary>
    public object? Selector { get; set; }
    public override string ToString() => Message;
}

/// <summary>标准搜索解析结果：命中节点 + 错误告知列表（模块隔离——一个枝出错不影响其他/上层）。</summary>
public sealed class SelectResult
{
    public List<AstNode> Hits { get; } = new();
    public List<SelectError> Errors { get; } = new();
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// **标准搜索解析器**（SelectorResolver）——SA SelectNodes / CRUD 增删改查 / 引擎定位的唯一标准。
///
/// **规范（定稿）**
/// ----------
/// 路径 = 枝选择器序列，**逐层推进不跳层**：第 1 个枝在根层（RootNodes）匹配，
/// 命中后下一枝在命中的 Children 层匹配；最后返回末枝命中集合。
///
/// **枝**（可继续往下；match 与 index **互斥**）：
///   { "mode": "Any|Simple|Block|List",        // match 枝：mode **必填**（决定类型 + 是否有下一层）
///     "match": { "rule": [ 枝或叶... ],        // 条件列表（多条件）
///                "check_rule": "And|Or|Nor|Nand" } }   // 组合 rule（缺省 And）
///   { "mode": "...", "index": 2 }              // index 枝：抽第 2 个（**1 起**；mode 可选——
///                                              //   无 mode 按顺序数全部，有 mode 数该类型第 N 个；
///                                              //   越界 = 记错误）
///
/// **叶**（判断终止点，二选一）：
///   { "target": "key|value",                   // 检查节点自身 key 或 value
///     "keywords": ["..."],                     // 候选（多值 = 同时命中 AND；Or/Nor 用多叶 + check_rule）
///     "type": "equals|start|end|contains" }    // 匹配方式（缺省 equals）
///   { "index": 2 }                             // 本层第 N 个（1 起）——rule 内 = 候选节点
///                                              //   Children 第 N 个存在 → yes；不存在 → false
///
/// **递归终止**：rule 分支必须终止在叶（target 或 index）——不允许无限嵌套。
/// **存在性取反（单条件 Nor/Nand = not）**：rule 里枝 = 存在性检查；当枝内部
///   match.check_rule=Nor/Nand 且 rule=[单个枝] 时，对该**存在性**取反（"不存在满足
///   该枝的子节点"）——Nor/Nand 提升到存在性层面，而非子节点条件组合。
///   例：排除"key 字段值以 CORVETTE_BIO_ 开头的块" =
///   { "mode": "Any", "match": { "rule": [
///       { "mode": "Simple", "match": { "rule": [
///           { "target": "key", "keywords": ["key"] },
///           { "target": "value", "type": "start", "keywords": ["CORVETTE_BIO_"] } ] } }
///   ], "check_rule": "Nor" } }
/// **错误处理**：越界/语句非法 → 记入 SelectResult.Errors（内存告知），**不抛异常**；
///   一个枝出错不影响其他枝与上层（模块隔离——上层无需抗爆）。
///
/// 废弃：旧格式（string / (key,value) 元组 / int 简写）请用 LegacySelectorResolver（已废弃，
/// 仅解析旧数据：成功提示废弃 + 输出，失败报错）。
/// </summary>
public static class SelectorResolver
{
    /// <summary>逐层解析标准选择路径。autoCreateBlocks=true（Add 场景）：match 为单个
    /// {target:key, keywords:[单值]} 叶且未命中 → 自动创建该 Block。</summary>
    public static SelectResult Resolve(List<AstNode> roots, List<object> path, bool autoCreateBlocks = false)
    {
        var result = new SelectResult();
        var space = roots.ToList();
        var hits = new List<AstNode>();
        foreach (var selector in path)
        {
            if (selector is not IDictionary<string, object> dict)
            {
                result.Errors.Add(new SelectError { Message = "标准搜索路径只接受枝（字典选择器）——旧 string/元组/int 请用已废弃的 LegacySelectorResolver", Selector = selector });
                break;
            }
            hits = EvalBranch(space, dict, autoCreateBlocks, roots, result);
            if (hits.Count == 0)
                break;
            // 推进：下一层搜索空间 = 命中的 Children（展平）
            space = hits.SelectMany(n => n.Children).ToList();
        }
        result.Hits.AddRange(hits);
        return result;
    }

    /// <summary>
    /// 节点条件评估（extract 全树遍历场景）：rule 条件数组 + check_rule 对单个节点求值
    /// （= 枝的 match 部分，mode 恒 Any——不限定节点类型）。
    /// </summary>
    public static bool NodeMatches(List<object> rule, string? checkRule, AstNode node, SelectResult result)
        => EvalRule(node, rule, string.IsNullOrEmpty(checkRule) ? "And" : checkRule!, result);

    // ==================== 枝 ====================

    /// <summary>评估一个枝：match（条件过滤）或 index（抽取）——作用于当前空间。</summary>
    private static List<AstNode> EvalBranch(List<AstNode> space, IDictionary<string, object> dict,
        bool autoCreateBlocks, List<AstNode> roots, SelectResult result)
    {
        bool hasMatch = dict.ContainsKey("match");
        bool hasIndex = dict.ContainsKey("index");
        if (hasMatch && hasIndex)
        {
            result.Errors.Add(new SelectError { Message = "枝中 match 与 index 互斥（二选一）", Selector = dict });
            return new List<AstNode>();
        }
        var mode = GetDictString(dict, "mode");

        if (hasMatch)
        {
            // match 枝：mode 必填（决定类型过滤 + 是否有下一层）
            if (string.IsNullOrEmpty(mode))
            {
                result.Errors.Add(new SelectError { Message = "match 枝的 mode 必填（决定类型与是否有下一层）", Selector = dict });
                return new List<AstNode>();
            }
            var matchDict = GetDict(dict, "match");
            if (matchDict == null)
            {
                result.Errors.Add(new SelectError { Message = "match 必须是字典", Selector = dict });
                return new List<AstNode>();
            }
            var rule = GetRuleList(matchDict, "rule");
            var checkRule = GetDictString(matchDict, "check_rule") ?? "And";
            var hits = new List<AstNode>();
            foreach (var node in space)
            {
                if (!NodeModeMatches(node, mode))
                    continue;
                if (EvalRule(node, rule, checkRule, result))
                    hits.Add(node);
            }
            // autoCreate：Add 场景——match 是单个 {target:key, keywords:[单值]} 叶且未命中 → 建块
            if (hits.Count == 0 && autoCreateBlocks && TryGetCreateKey(rule, out string? createKey))
            {
                var block = CreateBlockUnder(space, roots, createKey!);
                hits.Add(block);
            }
            return hits;
        }

        if (hasIndex)
        {
            // index 枝：抽第 N 个（1 起）。mode 可选——无 mode 按顺序数全部，有 mode 数该类型。
            if (!TryGetIndex(GetDictValue(dict, "index"), out int idx))
            {
                result.Errors.Add(new SelectError { Message = "index 必须是正整数（1 起）", Selector = dict });
                return new List<AstNode>();
            }
            var candidates = string.IsNullOrEmpty(mode)
                ? space
                : space.Where(n => NodeModeMatches(n, mode!)).ToList();
            if (idx >= 1 && idx <= candidates.Count)
                return new List<AstNode> { candidates[idx - 1] };
            result.Errors.Add(new SelectError
            {
                Message = $"index={idx} 越界（该层共 {candidates.Count} 个" + (string.IsNullOrEmpty(mode) ? "" : $" {mode} 类型") + "）",
                Selector = dict
            });
            return new List<AstNode>();
        }

        result.Errors.Add(new SelectError { Message = "枝必须包含 match 或 index", Selector = dict });
        return new List<AstNode>();
    }

    /// <summary>rule 条件列表对单个节点求值（check_rule 组合）——"候选节点内容/属性满足条件"。</summary>
    private static bool EvalRule(AstNode node, List<object> rule, string checkRule, SelectResult result)
    {
        if (rule.Count == 0)
            return true;   // 空 rule = 无附加条件（仅 mode 过滤）
        var hits = new List<bool>();
        foreach (var cond in rule)
        {
            if (cond is not IDictionary<string, object> d)
            {
                result.Errors.Add(new SelectError { Message = "rule 条件必须是叶（target/index）或枝（字典）", Selector = cond });
                hits.Add(false);
                continue;
            }
            if (d.ContainsKey("target"))
            {
                // 叶 target：检查候选节点自身的 key 或 value
                hits.Add(EvalTarget(node, d));
            }
            else if (d.ContainsKey("index") && !d.ContainsKey("mode") && !d.ContainsKey("match"))
            {
                // 叶 index：候选节点 Children 第 N 个存在（1 起）→ yes/false
                if (TryGetIndex(GetDictValue(d, "index"), out int idx))
                    hits.Add(idx >= 1 && idx <= node.Children.Count);
                else
                    hits.Add(false);
            }
            else
            {
                // 枝条件：候选节点的 Children 层里存在满足该枝的节点（存在性检查）。
                // **单条件 Nor/Nand = 对存在性取反**（用户定稿：rule=[单枝], check_rule=Nor/Nand
                // → "不存在满足该枝的子节点"）——Nor/Nand 提升到存在性层面而非子节点条件组合。
                var matchDict = GetDict(d, "match");
                var innerCheck = GetDictString(matchDict, "check_rule") ?? "And";
                var innerRule = GetRuleList(matchDict, "rule");
                if (innerRule.Count == 1
                    && innerRule[0] is IDictionary<string, object> innerBranch
                    && (innerCheck == "Nor" || innerCheck == "Nand"))
                {
                    var innerHits = EvalBranch(node.Children, innerBranch, autoCreateBlocks: false, roots: null!, result);
                    hits.Add(innerHits.Count == 0);   // 不存在 = 取反
                }
                else
                {
                    var sub = EvalBranch(node.Children, d, autoCreateBlocks: false, roots: null!, result);
                    hits.Add(sub.Count > 0);
                }
            }
        }
        return CombineHits(hits, checkRule);
    }

    /// <summary>叶 target：按节点类型匹配 keywords（check_rule 组合：And/Or/Nor/Nand，缺省 And）。</summary>
    private static bool EvalTarget(AstNode node, IDictionary<string, object> d)
    {
        var target = GetDictString(d, "target") ?? "key";
        var type = GetDictString(d, "type") ?? "equals";
        var keywords = GetDictStrings(d, "keywords");
        var checkRule = GetDictString(d, "check_rule") ?? "And";
        if (keywords.Count == 0)
            return true;   // 无 keywords = 仅类型/存在（配合 index 场景）

        // target == "value"——按节点类型分语义（用户定稿）：
        //   Simple：字面值；List：元素集合包含 keywords（每个 kw 至少一个元素命中）；
        //   Block：内容里含该 key（每个 kw 都存在该 key 的子节点）。
        // 各 kw 的命中结果按 check_rule 组合（And=全中 / Or=任一 / Nor=全不中 / Nand=非全中）。
        if (target == "value")
        {
            if (node.Type == NodeType.List)
            {
                if (node.Children.Count == 0)
                    return false;   // 空 List 无元素可含——不命中（任何 check_rule）
                return CombineHits(keywords.Select(kw => node.Children.Any(el => MatchText(el.Value?.ToString() ?? "", new List<string> { kw }, type))).ToList(), checkRule);
            }
            if (node.Type == NodeType.Block || node.Type == NodeType.InlineScript)
                return CombineHits(keywords.Select(kw => node.Children.Any(c => string.Equals(c.Key, kw, StringComparison.Ordinal))).ToList(), checkRule);
            return CombineHits(keywords.Select(kw => MatchText(node.Value?.ToString() ?? "", new List<string> { kw }, type)).ToList(), checkRule);
        }

        // target == "key"
        return CombineHits(keywords.Select(kw => MatchText(node.Key ?? "", new List<string> { kw }, type)).ToList(), checkRule);
    }

    private static bool MatchText(string text, List<string> keywords, string type)
    {
        foreach (var kw in keywords)
        {
            bool hit = type.ToLowerInvariant() switch
            {
                "start" => text.StartsWith(kw, StringComparison.Ordinal),
                "end" => text.EndsWith(kw, StringComparison.Ordinal),
                "contains" => text.Contains(kw, StringComparison.Ordinal),
                _ => string.Equals(text, kw, StringComparison.Ordinal)   // equals（缺省）
            };
            if (!hit)
                return false;
        }
        return true;
    }

    /// <summary>check_rule 组合：And=全命中 / Or=任一 / Nor=全不命中 / Nand=非全命中（缺省 And）。</summary>
    private static bool CombineHits(List<bool> hits, string checkRule)
        => checkRule.ToLowerInvariant() switch
        {
            "or" => hits.Any(h => h),
            "nor" => hits.All(h => !h),
            "nand" => !hits.All(h => h),
            _ => hits.All(h => h)   // And
        };

    private static bool NodeModeMatches(AstNode node, string mode)
        => mode.ToLowerInvariant() switch
        {
            "simple" => node.Type == NodeType.Simple,
            "block" => node.Type == NodeType.Block || node.Type == NodeType.InlineScript,
            "list" => node.Type == NodeType.List,
            _ => true   // Any
        };

    /// <summary>autoCreate：rule 是否为单个 {target:key, keywords:[单值]} 叶（Add 场景建块依据）。</summary>
    private static bool TryGetCreateKey(List<object> rule, out string? key)
    {
        key = null;
        if (rule.Count != 1 || rule[0] is not IDictionary<string, object> d)
            return false;
        if (!string.Equals(GetDictString(d, "target"), "key", StringComparison.OrdinalIgnoreCase))
            return false;
        var kws = GetDictStrings(d, "keywords");
        if (kws.Count != 1)
            return false;
        key = kws[0];
        return true;
    }

    private static AstNode CreateBlockUnder(List<AstNode> space, List<AstNode> roots, string key)
    {
        var block = new AstNode
        {
            Type = NodeType.Block,
            Key = key,
            Children = new List<AstNode>(),
            OriginalLayout = OriginalLayout.MultiLine
        };
        var parent = space.LastOrDefault() ?? roots.LastOrDefault();
        if (parent == null)
            roots.Add(block);
        else
            parent.Children.Add(block);
        return block;
    }

    // ==================== 字典辅助 ====================

    private static object? GetDictValue(IDictionary<string, object> dict, string key)
        => dict.TryGetValue(key, out var v) ? v : null;

    private static string? GetDictString(IDictionary<string, object> dict, string key)
    {
        var v = GetDictValue(dict, key);
        return v switch
        {
            null => null,
            string s => s,
            _ => v?.ToString()
        };
    }

    private static List<string> GetDictStrings(IDictionary<string, object> dict, string key)
    {
        var v = GetDictValue(dict, key);
        if (v is System.Collections.IEnumerable en && v is not string)
        {
            var list = new List<string>();
            foreach (var item in en)
                list.Add(item?.ToString() ?? "");
            return list;
        }
        return new List<string> { v?.ToString() ?? "" };
    }

    private static IDictionary<string, object>? GetDict(IDictionary<string, object> dict, string key)
    {
        var v = GetDictValue(dict, key);
        return v as IDictionary<string, object>;
    }

    private static List<object> GetRuleList(IDictionary<string, object> matchDict, string key)
    {
        var v = GetDictValue(matchDict, key);
        if (v is System.Collections.IEnumerable en && v is not string && v is not IDictionary<string, object>)
            return en.Cast<object>().ToList();
        return new List<object>();
    }

    private static bool TryGetIndex(object? v, out int idx)
    {
        idx = 0;
        switch (v)
        {
            case int i: idx = i; return true;
            case long l: idx = (int)l; return true;
            case double d: idx = (int)d; return true;
            case string s when int.TryParse(s, out idx): return true;
            default: return false;
        }
    }
}
