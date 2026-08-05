using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace Stellaris.Parser;

/// <summary>
/// 纯文本 $var$ 替换器。
/// 仅进行字符串替换，不涉及任何解析或求值。
/// 用于阶段1的本地化替换和阶段2的内联脚本参数替换。
/// 支持自引用检测：若替换后值与替换前相同，则标记为稳定。
/// </summary>
public class TextReplacer
{
    private readonly Dictionary<string, object?> _variables;
    private readonly HashSet<string> _stableKeys = new();

    /// <summary>
    /// 使用提供的常量字典初始化替换器
    /// </summary>
    public TextReplacer(Dictionary<string, object?> variables)
    {
        _variables = variables ?? new Dictionary<string, object?>();
    }

    /// <summary>
    /// 替换文本中的所有 $var$ 占位符
    /// </summary>
    public string Replace(string text)
    {
        if (string.IsNullOrEmpty(text) || _variables.Count == 0)
            return text;

        string result = text;
        foreach (var kv in _variables)
        {
            if (kv.Value == null)
                continue;
            string placeholder = $"${kv.Key}$";
            string valueStr = kv.Value.ToString() ?? string.Empty;
            result = result.Replace(placeholder, valueStr);
        }
        return result;
    }

    /// <summary>
    /// 替换文本并检测自引用：如果新值与旧值相同，标记该键为稳定
    /// 返回 (新值, 是否发生变化)
    /// </summary>
    public (string NewValue, bool Changed) ReplaceWithStabilityCheck(string text, string? key = null)
    {
        if (string.IsNullOrEmpty(text))
            return (text, false);

        if (key != null && _stableKeys.Contains(key))
            return (text, false);

        string newText = Replace(text);
        bool changed = newText != text;

        if (key != null && !changed)
            _stableKeys.Add(key);

        return (newText, changed);
    }

    /// <summary>
    /// 添加或更新常量
    /// </summary>
    public void SetVariable(string key, object? value)
    {
        _variables[key] = value;
        _stableKeys.Remove(key);
    }

    /// <summary>
    /// 获取所有常量（只读）
    /// </summary>
    public IReadOnlyDictionary<string, object?> Variables => _variables;

    /// <summary>
    /// 重置稳定标记（用于新一轮迭代）
    /// </summary>
    public void ResetStableKeys()
    {
        _stableKeys.Clear();
    }

    // ==================== 新增静态展开方法 ====================
    /// <summary>
    /// 递归展开文本中的所有 $var$ 占位符，依赖关系自动传播。
    /// </summary>
    /// <param name="text">待展开的文本</param>
    /// <param name="rawDict">原始字典（未展开的值）</param>
    /// <param name="resolvedCache">已展开值的缓存（键 -> 展开后字符串）</param>
    /// <param name="visiting">当前递归栈，用于检测循环引用</param>
    /// <param name="depth">当前递归深度</param>
    /// <param name="logger">可选的日志记录器，用于记录循环引用警告</param>
    /// <returns>完全展开后的字符串</returns>
    public static string Expand(string text, Dictionary<string, string> rawDict,
        Dictionary<string, string> resolvedCache, HashSet<string> visiting, int depth,
        ILogger? logger = null)   // 新增可选参数
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (depth > Config.MaxIterationDepth) return text;

        if (!text.Contains('$')) return text;

        var result = text;
        int startIdx = 0;
        while (startIdx < result.Length)
        {
            int dollarStart = result.IndexOf('$', startIdx);
            if (dollarStart == -1) break;
            int dollarEnd = result.IndexOf('$', dollarStart + 1);
            if (dollarEnd == -1) break;

            string varName = result.Substring(dollarStart + 1, dollarEnd - dollarStart - 1);
            if (string.IsNullOrEmpty(varName))
            {
                startIdx = dollarEnd + 1;
                continue;
            }

            string replacement;
            if (resolvedCache.TryGetValue(varName, out string? cached))
            {
                replacement = cached;
            }
            else if (visiting.Contains(varName))
            {
                // ===== 记录循环引用警告 =====
                logger?.LogWarning("检测到本地化循环引用: {VarName}", varName);
                replacement = $"${varName}$";
            }
            else if (rawDict.TryGetValue(varName, out string? rawValue))
            {
                visiting.Add(varName);
                string expandedValue = Expand(rawValue, rawDict, resolvedCache, visiting, depth + 1, logger); // 传递 logger
                visiting.Remove(varName);
                resolvedCache[varName] = expandedValue;
                replacement = expandedValue;
            }
            else
            {
                replacement = $"${varName}$";
            }

            result = result.Remove(dollarStart, dollarEnd - dollarStart + 1)
                           .Insert(dollarStart, replacement);
            startIdx = dollarStart + replacement.Length;
        }

        return result;
    }
}