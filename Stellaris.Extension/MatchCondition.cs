using System;
using System.Text.Json;
using Stellaris.Parser;

namespace Stellaris.Extension;

/// <summary>
/// 【已废弃——不再使用】v2.0 的条件树求值（all/any/not + key/path/has + key_* / value_* 模式）。
/// 已被 SelectorResolver 枝/叶语法取代（CLI 的 extract.match 现在用 NodeMatches 评估）。
/// 保留本文件仅供历史参考；新代码一律用 SelectorResolver。
/// </summary>
public static class MatchCondition
{
    /// <summary>条件树求值：cond 满足 → true。null/空对象 → 匹配全部节点。</summary>
    public static bool Eval(JsonElement? cond, AstNode node, string relPath)
    {
        if (cond == null || cond.Value.ValueKind != JsonValueKind.Object)
            return true;
        var obj = cond.Value;

        if (obj.TryGetProperty("all", out var all))
        {
            if (all.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var c in all.EnumerateArray())
                if (!Eval(c, node, relPath))
                    return false;
            return true;
        }

        if (obj.TryGetProperty("any", out var any))
        {
            if (any.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var c in any.EnumerateArray())
                if (Eval(c, node, relPath))
                    return true;
            return false;
        }

        if (obj.TryGetProperty("not", out var not))
            return !Eval(not, node, relPath);

        // 叶子三件套 + key 模式匹配（独立字段）
        bool matched = false;
        if (obj.TryGetProperty("key", out var key))
        {
            if (string.Equals(node.Key, key.GetString(), StringComparison.Ordinal))
                matched = true;
            else
                return false;
        }
        if (obj.TryGetProperty("key_starts", out var keyStarts))
        {
            // key 开头匹配（如 "shelter_"）
            if (node.Key != null && node.Key.StartsWith(keyStarts.GetString() ?? "", StringComparison.Ordinal))
                matched = true;
            else
                return false;
        }
        if (obj.TryGetProperty("key_contains", out var keyContains))
        {
            // key 包含匹配（如 "reactor"）
            if (node.Key != null && node.Key.Contains(keyContains.GetString() ?? "", StringComparison.Ordinal))
                matched = true;
            else
                return false;
        }
        if (obj.TryGetProperty("key_ends", out var keyEnds))
        {
            // key 结尾匹配（如 "_core"）
            if (node.Key != null && node.Key.EndsWith(keyEnds.GetString() ?? "", StringComparison.Ordinal))
                matched = true;
            else
                return false;
        }
        if (obj.TryGetProperty("value_starts", out var valueStarts))
        {
            // **值**开头匹配（如 key 的 value 以 "shelter_" 开头）
            if (node.Value != null && node.Value.ToString()!.StartsWith(valueStarts.GetString() ?? "", StringComparison.Ordinal))
                matched = true;
            else
                return false;
        }
        if (obj.TryGetProperty("value_contains", out var valueContains))
        {
            // 值包含匹配
            if (node.Value != null && node.Value.ToString()!.Contains(valueContains.GetString() ?? "", StringComparison.Ordinal))
                matched = true;
            else
                return false;
        }
        if (obj.TryGetProperty("value_ends", out var valueEnds))
        {
            // 值结尾匹配
            if (node.Value != null && node.Value.ToString()!.EndsWith(valueEnds.GetString() ?? "", StringComparison.Ordinal))
                matched = true;
            else
                return false;
        }
        if (obj.TryGetProperty("path", out var path))
        {
            // 目录边界匹配：前缀后必须紧跟 '/' 或路径结束（避免误伤同前缀目录）
            if (IsPathUnder(relPath, path.GetString() ?? ""))
                matched = true;
            else
                return false;
        }
        if (obj.TryGetProperty("has", out var has))
        {
            if (HasField(node, has))
                matched = true;
            else
                return false;
        }
        return matched;
    }

    /// <summary>目录边界匹配：relPath 等于 prefix，或前缀后紧跟 '/'（relPath 统一正斜杠）。</summary>
    private static bool IsPathUnder(string relPath, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return true;
        if (relPath.Length < prefix.Length)
            return false;
        if (string.Compare(relPath, 0, prefix, 0, prefix.Length, StringComparison.Ordinal) != 0)
            return false;
        return relPath.Length == prefix.Length || relPath[prefix.Length] == '/';
    }

    /// <summary>
    /// has 条件求值：
    /// - 含 "key" 属性 = 现有叶子语义：子树递归查找 字段=值（省略 value = 只查字段存在）；
    ///   值条件支持精确（value）或模式（value_starts/value_contains/value_ends，可组合=AND）。
    /// - 否则 = **嵌套对象语法**：每层属性名 = 子块路径段，逐层下钻；
    ///   最内层字符串/数值 = Simple 字段=值；null 值 = 只查字段存在。
    ///   例：{"potential": {"from": {"country_uses_bio_ships": "no"}}}。
    /// </summary>
    private static bool HasField(AstNode node, JsonElement has)
    {
        if (has.TryGetProperty("key", out var keyEl))
        {
            var key = keyEl.GetString();
            bool hasValue = has.TryGetProperty("value", out var v);
            var value = hasValue ? v.GetString() : null;
            bool hasVStarts = has.TryGetProperty("value_starts", out var vStartEl);
            bool hasVContains = has.TryGetProperty("value_contains", out var vContEl);
            bool hasVEnds = has.TryGetProperty("value_ends", out var vEndEl);
            bool noValueCond = !hasValue && !hasVStarts && !hasVContains && !hasVEnds;   // 只查存在
            foreach (var child in node.Children)
            {
                if (!string.Equals(child.Key, key, StringComparison.Ordinal))
                {
                    // 任意深度：key 不匹配的节点也继续下钻查找（真正的子树递归）
                    if (HasField(child, has))
                        return true;
                    continue;
                }
                // 值条件：多个模式字段同时写 = AND（与叶子字段风格一致）
                var cv = child.Value?.ToString();
                bool ok = true;
                if (hasValue && !string.Equals(cv, value, StringComparison.Ordinal)) ok = false;
                if (ok && hasVStarts && !(cv != null && cv.StartsWith(vStartEl.GetString() ?? "", StringComparison.Ordinal))) ok = false;
                if (ok && hasVContains && !(cv != null && cv.Contains(vContEl.GetString() ?? "", StringComparison.Ordinal))) ok = false;
                if (ok && hasVEnds && !(cv != null && cv.EndsWith(vEndEl.GetString() ?? "", StringComparison.Ordinal))) ok = false;
                if (ok)
                    return true;
                if (HasField(child, has))
                    return true;
            }
            return false;
        }

        // 嵌套对象语法：每层属性 = 子块路径段
        foreach (var prop in has.EnumerateObject())
        {
            bool found = false;
            foreach (var child in node.Children)
            {
                if (!string.Equals(child.Key, prop.Name, StringComparison.Ordinal))
                    continue;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (HasField(child, prop.Value))
                    {
                        found = true;
                        break;
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.Null)
                {
                    // null 值 = 只查该字段存在（Simple/Block 都算存在）
                    found = true;
                    break;
                }
                else if (child.Type == NodeType.Simple
                         && string.Equals(child.Value?.ToString(), prop.Value.ToString(), StringComparison.Ordinal))
                {
                    // 字符串/数值 → Simple 字段=值
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }
        return true;
    }
}
