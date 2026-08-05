// 文件: Stellaris.Parser/StellarisAdapter_Constants.cs
// 常量引用索引、全局常量对外接口与联动传播（规范第四章、第十章）。
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Stellaris.Parser
{
    public partial class StellarisAdapter
    {
        // ==================== 第十章：全局常量表对外接口 ====================

        /// <summary>
        /// 查询全局常量（规范 10.1）。
        /// name 为空抛 ArgumentException；不存在返回 null；线程安全（加锁）。
        /// </summary>
        public object? GetGlobalConstant(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("name 不能为空", nameof(name));

            lock (_stateLock)
            {
                var globals = _globalResolver.GetAllGlobals();
                return globals.TryGetValue(name, out object? val) ? val : null;
            }
        }

        /// <summary>
        /// 修改全局常量并触发常量传播（规范 10.2）。
        /// name 为空或 value 为 null / 非数字类型时抛 ArgumentException；线程安全（加锁）。
        /// </summary>
        public void SetGlobalConstant(string name, object value)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("name 不能为空", nameof(name));
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (!(value is int || value is long || value is float || value is double))
                throw new ArgumentException("value 必须是数字类型（int、long、float、double）", nameof(value));

            lock (_stateLock)
            {
                _globalResolver.SetGlobal(name, value);
                PropagateGlobalConstant(name, value);
            }

            _logger.LogInformation("全局常量已更新: {Name} = {Value}", name, value);
        }

        /// <summary>
        /// 查询引用指定全局常量的节点（规范 10.3）。
        /// name 为空抛 ArgumentException；不存在返回 null；遍历时惰性清理失效弱引用；线程安全（加锁）。
        /// </summary>
        public IReadOnlyList<AstNode>? GetConstantReferences(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("name 不能为空", nameof(name));

            lock (_stateLock)
            {
                if (!_constantReferenceIndex.TryGetValue(name, out var refs))
                    return null;

                var result = new List<AstNode>();
                var dead = new List<WeakReference<AstNode>>();
                foreach (var wr in refs)
                {
                    if (wr.TryGetTarget(out var target) && target != null)
                        result.Add(target);
                    else
                        dead.Add(wr);
                }
                foreach (var d in dead)
                    refs.Remove(d);

                return result;
            }
        }

        /// <summary>
        /// 手动触发常量引用索引全量惰性清理（规范 4.5）。
        /// 移除所有 Target 为 null 或节点已不在任何 AST 中的弱引用。
        /// </summary>
        public void CleanConstantIndex()
        {
            lock (_stateLock)
            {
                int removed = 0;
                foreach (var kv in _constantReferenceIndex)
                {
                    var dead = new List<WeakReference<AstNode>>();
                    foreach (var wr in kv.Value)
                    {
                        if (!wr.TryGetTarget(out var target) || target == null || !IsNodeInAnyTree(target))
                            dead.Add(wr);
                    }
                    foreach (var d in dead)
                        kv.Value.Remove(d);
                    removed += dead.Count;
                }
                _logger.LogDebug("常量引用索引清理完成，共移除 {Count} 个失效引用", removed);
            }
        }

        /// <summary>
        /// 解析用户输入的常量文本（如 "@foo"、"@[foo + 1]"、"42"、"text"），
        /// 返回求值后的逻辑值；无法解析的常量引用返回 null（调用方可保留原文写回，交由游戏端解析）。
        /// 供上层引擎（如 GalaxyStyleEngine）支持常量引用输入时调用；线程安全（加锁）。
        /// </summary>
        public object? ResolveConstantInput(string? input)
        {
            string trimmed = input?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
                return null;

            lock (_stateLock)
            {
                if (trimmed.StartsWith('@'))
                {
                    var evaluator = new ExpressionEvaluator(new ConstantResolver(_globalResolver), _logger);
                    return evaluator.EvaluateValue(BuildConstantValueFromText(trimmed));
                }
                return trimmed;
            }
        }

        /// <summary>
        /// 将 "@name" / "@[expr]" 文本还原为 ConstantValue（供 ResolveConstantInput 使用）。
        /// </summary>
        private static ConstantValue? BuildConstantValueFromText(string text)
        {
            if (text.Length >= 3 && text[1] == '[' && text[^1] == ']')
            {
                string inner = text.Substring(2, text.Length - 3);
                return new ConstantValue { Type = ConstantType.Expression, Text = inner };
            }
            string name = text.Substring(1);
            if (name.Length == 0)
                return null;
            return new ConstantValue { Type = ConstantType.Simple, Name = name };
        }

        // ==================== 4.2 全量构建 ====================

        /// <summary>
        /// 全量构建常量引用索引（规范 4.2），在阶段 3 常量求值完成后调用。
        /// 复杂度 O(N)，N 为所有 AST 节点总数。
        /// </summary>
        internal void BuildConstantReferenceIndex()
        {
            _constantReferenceIndex.Clear();

            int nodeCount = 0;
            foreach (var result in _configResults.Values)
            {
                foreach (var root in result.RootNodes)
                    nodeCount += AddSubtreeToIndex(root);
            }

            _logger.LogDebug("常量引用索引构建完成，覆盖 {NodeCount} 个节点、{ConstCount} 个常量",
                nodeCount, _constantReferenceIndex.Count);
        }

        private int AddSubtreeToIndex(AstNode node)
        {
            if (node == null) return 0;

            int count = 0;
            if (node.Type == NodeType.Simple)
            {
                foreach (var name in ExtractConstantNames(node))
                {
                    if (!_constantReferenceIndex.TryGetValue(name, out var set))
                    {
                        set = new HashSet<WeakReference<AstNode>>(AstNodeWeakRefComparer.Instance);
                        _constantReferenceIndex[name] = set;
                    }
                    set.Add(new WeakReference<AstNode>(node));
                }
                count = 1;
            }

            if (node.Type == NodeType.Block || node.Type == NodeType.List || node.Type == NodeType.InlineScript)
            {
                foreach (var child in node.Children)
                    count += AddSubtreeToIndex(child);
            }
            return count;
        }

        /// <summary>
        /// 从索引中移除节点及其子树的全部弱引用（规范 4.3 步骤 1/4）。
        /// </summary>
        private void RemoveSubtreeFromIndex(AstNode node)
        {
            if (node == null) return;

            if (node.Type == NodeType.Simple)
            {
                foreach (var name in ExtractConstantNames(node))
                {
                    if (_constantReferenceIndex.TryGetValue(name, out var set))
                        set.Remove(new WeakReference<AstNode>(node));
                }
            }

            if (node.Type == NodeType.Block || node.Type == NodeType.List || node.Type == NodeType.InlineScript)
            {
                foreach (var child in node.Children)
                    RemoveSubtreeFromIndex(child);
            }
        }

        /// <summary>
        /// 增量维护入口（规范 4.3 / 8.4）：先移除旧引用，再添加新引用。
        /// 供 CRUD 操作在完成修改后、状态锁释放前调用。
        /// </summary>
        private void UpdateConstantIndexForNode(AstNode? oldNode, AstNode? newNode)
        {
            if (oldNode != null)
                RemoveSubtreeFromIndex(oldNode);
            if (newNode != null)
                AddSubtreeToIndex(newNode);
        }

        /// <summary>
        /// 提取 Simple 节点引用的全局常量名集合（规范 4.2 步骤 3a）。
        /// 同时检查 Value（ConstantValue 类型）与 RawText（求值后 Value 可能已被替换）。
        /// </summary>
        private static HashSet<string> ExtractConstantNames(AstNode node)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (node == null || node.Type != NodeType.Simple)
                return names;

            if (node.Value is ConstantValue cv)
            {
                if (cv.Type == ConstantType.Simple && !string.IsNullOrEmpty(cv.Name))
                    names.Add(cv.Name);
                else if (cv.Type == ConstantType.Expression && !string.IsNullOrEmpty(cv.Text))
                    ExtractNamesFromExpression(cv.Text, names);
            }

            if (!string.IsNullOrEmpty(node.RawText))
                ExtractNamesFromRawText(node.RawText, names);

            return names;
        }

        /// <summary>
        /// 从原始文本中提取 @name / @[expr] 形式的常量引用。
        /// </summary>
        private static void ExtractNamesFromRawText(string rawText, HashSet<string> names)
        {
            int i = 0;
            while (i < rawText.Length)
            {
                if (rawText[i] == '@')
                {
                    i++;
                    if (i < rawText.Length && rawText[i] == '[')
                    {
                        int depth = 1;
                        int j = i + 1;
                        while (j < rawText.Length && depth > 0)
                        {
                            if (rawText[j] == '[') depth++;
                            else if (rawText[j] == ']') depth--;
                            j++;
                        }
                        if (depth == 0)
                        {
                            string inner = rawText.Substring(i + 1, j - i - 2);
                            ExtractNamesFromExpression(inner, names);
                            i = j;
                            continue;
                        }
                    }
                    else if (i < rawText.Length && IsRawIdentStart(rawText[i]))
                    {
                        int j = i;
                        while (j < rawText.Length && IsRawIdentPart(rawText[j]))
                            j++;
                        names.Add(rawText.Substring(i, j - i));
                        i = j;
                        continue;
                    }
                }
                i++;
            }
        }

        /// <summary>
        /// 从表达式文本中提取引用的常量名：
        /// 裸标识符（与 ExpressionEvaluator.ReplaceBareIdentifiers 语义一致），以及 @name / @[expr] 形式。
        /// </summary>
        private static void ExtractNamesFromExpression(string expr, HashSet<string> names)
        {
            if (string.IsNullOrEmpty(expr)) return;

            int i = 0;
            while (i < expr.Length)
            {
                char ch = expr[i];
                if (ch == '@')
                {
                    i++;
                    if (i < expr.Length && expr[i] == '[')
                    {
                        int depth = 1;
                        int j = i + 1;
                        while (j < expr.Length && depth > 0)
                        {
                            if (expr[j] == '[') depth++;
                            else if (expr[j] == ']') depth--;
                            j++;
                        }
                        if (depth == 0)
                        {
                            ExtractNamesFromExpression(expr.Substring(i + 1, j - i - 2), names);
                            i = j;
                            continue;
                        }
                    }
                    else if (i < expr.Length && IsRawIdentStart(expr[i]))
                    {
                        int j = i;
                        while (j < expr.Length && IsExprIdentPart(expr[j]))
                            j++;
                        names.Add(expr.Substring(i, j - i));
                        i = j;
                        continue;
                    }
                }
                else if (char.IsLetter(ch) || ch == '_')
                {
                    int j = i;
                    while (j < expr.Length && IsExprIdentPart(expr[j]))
                        j++;
                    names.Add(expr.Substring(i, j - i));
                    i = j;
                    continue;
                }
                i++;
            }
        }

        // ==================== 4.4 联动传播 ====================

        /// <summary>
        /// 全局常量修改的联动传播算法（规范 4.4）。
        /// 前置条件：_globalResolver 中的值已更新；调用方已持有 _stateLock。
        /// </summary>
        private void PropagateGlobalConstant(string constName, object newValue)
        {
            if (!_constantReferenceIndex.TryGetValue(constName, out var refs))
            {
                _logger.LogInformation("常量 {Name} 已更新，但未找到任何活跃引用节点", constName);
                return;
            }

            var evaluator = new ExpressionEvaluator(new ConstantResolver(_globalResolver), _logger);
            int updatedCount = 0;
            var deadRefs = new List<WeakReference<AstNode>>();

            foreach (var wr in refs)
            {
                if (!wr.TryGetTarget(out var target) || target == null)
                {
                    deadRefs.Add(wr);
                    _logger.LogDebug("常量引用索引：清理已失效的弱引用 ({Name})", constName);
                    continue;
                }

                if (!IsNodeInAnyTree(target))
                {
                    deadRefs.Add(wr);
                    _logger.LogDebug("常量引用索引：清理不在 AST 中的节点引用 ({Name})", constName);
                    continue;
                }

                try
                {
                    // 仅更新 Value 字段，严禁修改 RawText（规范 1.2 不变性规则）
                    object? constantValue = target.Value is ConstantValue existingCv
                        ? existingCv
                        : TryRebuildConstantValue(target);

                    if (constantValue == null)
                        continue;

                    object? newValueForNode = evaluator.EvaluateValue(constantValue);
                    if (newValueForNode != null)
                        target.Value = newValueForNode;
                    updatedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "常量传播求值失败: 常量 {Name} 引用的节点 {Key}", constName, target.Key);
                }
            }

            foreach (var dead in deadRefs)
                refs.Remove(dead);

            _logger.LogInformation("常量 {Name} 已更新为 {Value}，共更新 {Count} 个引用节点",
                constName, newValue, updatedCount);
        }

        /// <summary>
        /// 当节点的 Value 已被求值替换（不再持有 ConstantValue）时，
        /// 从 RawText 重建 ConstantValue，用于重新求值。
        /// </summary>
        private static ConstantValue? TryRebuildConstantValue(AstNode node)
        {
            string? raw = node.RawText;
            if (string.IsNullOrEmpty(raw) || !raw.StartsWith('@'))
                return null;

            if (raw.Length > 2 && raw[1] == '[' && raw[^1] == ']')
            {
                string inner = raw.Substring(2, raw.Length - 3);
                return new ConstantValue { Type = ConstantType.Expression, Text = inner };
            }

            string name = raw.Substring(1);
            if (name.Length == 0)
                return null;
            return new ConstantValue { Type = ConstantType.Simple, Name = name };
        }

        /// <summary>
        /// 检查节点是否仍存在于任一已加载文件的 AST 树中（引用相等）。
        /// </summary>
        private bool IsNodeInAnyTree(AstNode target)
        {
            foreach (var result in _configResults.Values)
            {
                foreach (var root in result.RootNodes)
                {
                    if (ReferenceEquals(root, target) || ContainsNode(root, target))
                        return true;
                }
            }
            return false;
        }

        private static bool ContainsNode(AstNode node, AstNode target)
        {
            foreach (var child in node.Children)
            {
                if (ReferenceEquals(child, target))
                    return true;
                if (ContainsNode(child, target))
                    return true;
            }
            return false;
        }

        // ==================== 标识符字符集辅助（规范 2.3） ====================

        private static bool IsRawIdentStart(char c) => char.IsLetter(c) || c == '_';

        /// <summary>RawText 中 @name 的标识符字符集（与词法分析器 IsIdentChar 一致，含连字符）</summary>
        private static bool IsRawIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';

        /// <summary>表达式内裸标识符字符集（与 ExpressionEvaluator.ReplaceBareIdentifiers 一致）</summary>
        private static bool IsExprIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// 弱引用比较器：按 Target 引用相等比较，使不同 WeakReference 实例指向同一节点时可互删。
        /// </summary>
        private sealed class AstNodeWeakRefComparer : IEqualityComparer<WeakReference<AstNode>>
        {
            public static readonly AstNodeWeakRefComparer Instance = new();

            public bool Equals(WeakReference<AstNode>? x, WeakReference<AstNode>? y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                return x.TryGetTarget(out var tx) && tx != null
                    && y.TryGetTarget(out var ty) && ReferenceEquals(tx, ty);
            }

            public int GetHashCode(WeakReference<AstNode> obj)
                => obj.TryGetTarget(out var t) && t != null ? RuntimeHelpers.GetHashCode(t) : 0;
        }
    }
}
