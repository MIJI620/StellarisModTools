using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellaris.Parser;

/// <summary>
/// 【已废弃——请勿使用】旧格式路径解析器（string / (key,value) 元组 / int 简写 + 旧字典 0 起 index）。
/// 仅供解析**历史旧数据**：解析成功 → Errors 提示"功能已废弃" + 仍输出 Hits；
/// 解析失败 → Errors 报错。新代码一律用 SelectorResolver（标准搜索规范）。
/// </summary>
[Obsolete("已废弃：请使用 SelectorResolver（标准搜索规范）")]
public static class LegacySelectorResolver
{
    /// <summary>旧格式路径：string（Key 宽匹配）/ (string,object) 元组 / int（0 起索引）/ 旧字典。</summary>
    public static SelectResult ResolveLegacy(List<AstNode> roots, List<object> path)
    {
        var result = new SelectResult();
        result.Errors.Add(new SelectError { Message = "LegacySelectorResolver 已废弃——请使用 SelectorResolver（标准搜索规范）", Selector = path });
        var space = roots.ToList();
        var hits = new List<AstNode>();
        foreach (var selector in path)
        {
            if (selector is string keySelector)
            {
                hits = space.Where(n => string.Equals(n.Key, keySelector, StringComparison.Ordinal)).ToList();
            }
            else if (selector is (string condKey, object condValue))
            {
                hits = new List<AstNode>();
                foreach (var node in space)
                {
                    if (node.Type == NodeType.Simple && node.Key == condKey && Equals(node.Value, condValue))
                        hits.Add(node);
                    else if (node.Type == NodeType.Block
                        && node.Children.Any(c => c.Type == NodeType.Simple && c.Key == condKey && Equals(c.Value, condValue)))
                        hits.Add(node);
                }
            }
            else if (selector is int indexSelector)
            {
                hits = indexSelector >= 0 && indexSelector < space.Count
                    ? new List<AstNode> { space[indexSelector] }
                    : new List<AstNode>();
            }
            else if (selector is IDictionary<string, object> dict)
            {
                // 旧字典：mode（缺省 Any）+ match（单条件字典）+ index（0 起）——平级
                var mode = dict.TryGetValue("mode", out var mv) ? mv?.ToString() : null;
                var filtered = new List<AstNode>();
                foreach (var node in space)
                {
                    if (!LegacyModeMatches(node, mode))
                        continue;
                    var matchDict = dict.TryGetValue("match", out var ma) ? ma as IDictionary<string, object> : null;
                    if (matchDict == null || LegacyMatch(node, matchDict))
                        filtered.Add(node);
                }
                if (dict.TryGetValue("index", out var iv) && TryLegacyIndex(iv, out int idx))
                {
                    if (idx >= 0 && idx < filtered.Count)
                        filtered = new List<AstNode> { filtered[idx] };
                    else
                        filtered = new List<AstNode>();
                }
                hits = filtered;
            }
            else
            {
                result.Errors.Add(new SelectError { Message = $"不支持的旧选择器类型: {selector.GetType()}", Selector = selector });
                break;
            }
            if (hits.Count == 0)
                break;
            space = hits.SelectMany(n => n.Children).ToList();
        }
        result.Hits.AddRange(hits);
        return result;
    }

    private static bool LegacyModeMatches(AstNode node, string? mode)
        => (mode ?? "Any").ToLowerInvariant() switch
        {
            "simple" => node.Type == NodeType.Simple,
            "block" => node.Type == NodeType.Block || node.Type == NodeType.InlineScript,
            "list" => node.Type == NodeType.List,
            _ => true
        };

    private static bool LegacyMatch(AstNode node, IDictionary<string, object> match)
    {
        var target = match.TryGetValue("target", out var tv) ? tv?.ToString() : "key";
        var type = match.TryGetValue("type", out var ty) ? ty?.ToString() : "equals";
        var keywords = GetStrings(match.TryGetValue("keywords", out var kv) ? kv : null);
        var checkRule = match.TryGetValue("check_rule", out var cv) ? cv?.ToString() : "And";
        if (target == "key")
            return MatchText(node.Key ?? "", keywords, type, checkRule);
        if (node.Type == NodeType.Block || node.Type == NodeType.InlineScript)
        {
            var rule = match.TryGetValue("rule", out var rv) ? rv : null;
            if (rule == null)
                return false;
            var sub = ResolveLegacy(node.Children, ToPath(rule));
            return sub.Hits.Count > 0;
        }
        if (node.Type == NodeType.List)
        {
            if (keywords.Count == 0)
                return true;
            var hits = new List<bool>();
            foreach (var kw in keywords)
                hits.Add(node.Children.Any(el => MatchText(el.Value?.ToString() ?? "", new List<string> { kw }, type, "And")));
            return Combine(hits, checkRule);
        }
        return MatchText(node.Value?.ToString() ?? "", keywords, type, checkRule);
    }

    private static bool MatchText(string text, List<string> keywords, string type, string checkRule)
    {
        if (keywords.Count == 0)
            return true;
        var hits = new List<bool>();
        foreach (var kw in keywords)
        {
            bool hit = type.ToLowerInvariant() switch
            {
                "start" => text.StartsWith(kw, StringComparison.Ordinal),
                "end" => text.EndsWith(kw, StringComparison.Ordinal),
                "contains" => text.Contains(kw, StringComparison.Ordinal),
                _ => string.Equals(text, kw, StringComparison.Ordinal)
            };
            hits.Add(hit);
        }
        return Combine(hits, checkRule);
    }

    private static bool Combine(List<bool> hits, string checkRule)
        => checkRule.ToLowerInvariant() switch
        {
            "or" => hits.Any(h => h),
            "nor" => hits.All(h => !h),
            "nand" => !hits.All(h => h),
            _ => hits.All(h => h)
        };

    private static List<string> GetStrings(object? v)
    {
        if (v is System.Collections.IEnumerable en && v is not string)
        {
            var list = new List<string>();
            foreach (var item in en)
                list.Add(item?.ToString() ?? "");
            return list;
        }
        return new List<string> { v?.ToString() ?? "" };
    }

    private static List<object> ToPath(object rule)
    {
        if (rule is System.Collections.IEnumerable en && rule is not string && rule is not IDictionary<string, object>)
            return en.Cast<object>().ToList();
        return new List<object> { rule };
    }

    private static bool TryLegacyIndex(object? v, out int idx)
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
