// 文件: Stellaris.Parser/StellarisAdapter_CRUD.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Stellaris.Parser
{
    /// <summary>AddConfigNode 插入位置：Append（缺省——父节点 children 末尾）| Before/After（目标节点同层前/后）。</summary>
    public enum AddPosition
    {
        Append,
        Before,
        After
    }

    public partial class StellarisAdapter
    {
        // ==================== 16.3 创建空内存文件 ====================

        /// <summary>
        /// 在指定的内存缓存中创建一个空的容器条目。
        /// 对于 Localisation 类型，此方法确保 _localisationTable 中存在该语言的字典。
        /// </summary>
        public void CreateEmptyFileInMemory(string relativePath, FileCategory category)
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new ArgumentNullException(nameof(relativePath));

            lock (_stateLock)
            {
                string normPath = NormalizePath(relativePath);
                switch (category)
                {
                    case FileCategory.Config:
                        if (!_configResults.ContainsKey(normPath))
                        {
                            var result = new ParserResult();
                            result.RootNodes = new List<AstNode>();
                            _configResults[normPath] = result;
                            _logger.LogDebug("在内存中创建空配置文件: {Path}", normPath);
                        }
                        break;
                    case FileCategory.Localisation:
                        // 从 relativePath 中提取语言标识
                        // 格式: localisation/{lang}/{fileName}
                        string lang = ExtractLangFromPath(normPath);
                        if (!string.IsNullOrEmpty(lang))
                        {
                            if (!_localisationTable.ContainsKey(lang))
                            {
                                _localisationTable[lang] = new Dictionary<string, LocalisationEntry>();
                                _logger.LogDebug("在内存中创建空本地化语言表: {Lang}", lang);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("无法从路径提取语言标识: {Path}", normPath);
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(category));
                }
            }
        }

        private static string ExtractLangFromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            // 格式: localisation/{lang}/...
            var parts = path.Split('/');
            if (parts.Length >= 2 && parts[0] == "localisation")
                return parts[1];
            return string.Empty;
        }

        // ==================== 16.4 配置文件内存操作接口 ====================

        /// <summary>
        /// **正向**节点查询（与 FindStringValues 的**反向**"值 → 位置"相对）：按标准选择路径从指定文件
        /// 选择节点，返回 SelectResult（Hits + Errors 内存告知——不抛异常）。
        /// 标准选择路径见 SelectorResolver 规范：路径 = 枝序列（逐层推进不跳层），枝 = mode 必填 +
        /// match.rule 数组（check_rule 组合）/ index 抽取（1 起，越界记错误）。
        /// </summary>
        public SelectResult SelectNodes(string relativePath, List<object> path)
        {
            lock (_stateLock)
            {
                string normPath = NormalizePath(relativePath);
                if (!_configResults.TryGetValue(normPath, out var result))
                    return new SelectResult();
                return SelectorResolver.Resolve(result.RootNodes, path);
            }
        }

        /// <summary>
        /// 在指定文件的 AST 中，于父路径下添加一个完整的 AST 节点。
        /// existingPredicate（可选）：自定义"已存在判定"——父节点下第一个满足谓词的节点
        /// 视为已存在（转更新/替换）；无则添加。默认 null = 按 Key 同名（Block 场景可传
        /// "第一层子节点含指定 Simple/List" 的谓词，如 spriteType 按 name 字段定位）。
        /// position（可选）：Append（缺省）——parentPath 定位**父节点**，新节点追加到 children
        /// 末尾；Before/After——parentPath 定位**目标节点本身**（list/simple/block），新节点插入到
        /// 目标同层前/后（相对定位；目标不存在 → 静默返回；多个 → 抛异常）。
        /// </summary>
        public void AddConfigNode(string relativePath, List<object> parentPath, AstNode newNode,
            Func<AstNode, bool>? existingPredicate = null, AddPosition position = AddPosition.Append)
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new ArgumentNullException(nameof(relativePath));
            if (parentPath == null)
                throw new ArgumentNullException(nameof(parentPath));
            if (newNode == null)
                throw new ArgumentNullException(nameof(newNode));

            lock (_stateLock)
            {
                string normPath = NormalizePath(relativePath);
                EnsureConfigFileExists(normPath);

                var result = _configResults[normPath];

                // 相对定位（Before/After）：parentPath 定位目标节点本身，插到它同层前/后
                if (position != AddPosition.Append)
                {
                    var targetResolve = SelectorResolver.Resolve(result.RootNodes, parentPath, autoCreateBlocks: false);
                    if (targetResolve.HasErrors)
                        foreach (var err in targetResolve.Errors) _logger.LogWarning("AddConfigNode 定位提示: {Msg}", err.Message);
                    var targets = targetResolve.Hits;
                    if (targets.Count == 0)
                    {
                        _logger.LogDebug("AddConfigNode[{Position}]: 目标不存在，静默返回", position);
                        return;
                    }
                    if (targets.Count > 1)
                        throw new InvalidOperationException($"相对定位到多个目标节点({targets.Count}): {string.Join(" -> ", parentPath)} 目标: {string.Join(",", targets.Select(t => t.Key + "=" + (t.Value?.ToString() ?? t.Type.ToString())))}");
                    var target = targets.First();
                    var targetParent = FindParentContaining(result.RootNodes, target);
                    // 顶层节点（无父）→ 父 = 文件根列表（RootNodes）——同样可相对插入
                    var parentList = targetParent != null ? targetParent.Children : result.RootNodes;
                    if (parentList.Count == 0)
                    {
                        _logger.LogDebug("AddConfigNode[{Position}]: 目标无父层（顶层），静默返回", position);
                        return;
                    }
                    int index = parentList.IndexOf(target);
                    if (index == -1)
                        return;
                    var clone = CloneNode(newNode);
                    parentList.Insert(position == AddPosition.Before ? index : index + 1, clone);
                    UpdateConstantIndexForNode(null, clone);
                    _logger.LogDebug("添加配置节点[{Position}]: {Path} -> {Key}（目标: {TargetKey}）",
                        position, normPath, newNode.Key ?? "<无Key>", target.Key ?? "<无Key>");
                    return;
                }

                var resolveResult = SelectorResolver.Resolve(result.RootNodes, parentPath, autoCreateBlocks: true);
                if (resolveResult.HasErrors)
                    foreach (var err in resolveResult.Errors) _logger.LogWarning("AddConfigNode 定位提示: {Msg}", err.Message);
                var parentNodes = resolveResult.Hits;
                if (parentNodes.Count == 0)
                    throw new InvalidOperationException($"父路径定位失败: {string.Join(" -> ", parentPath)}");

                var parent = parentNodes.First();
                if (parent.Type != NodeType.Block && parent.Type != NodeType.List)
                    throw new InvalidOperationException($"父节点不是 Block 或 List，实际类型: {parent.Type}");

                if (parent.Type == NodeType.List)
                {
                    if (newNode.Type != NodeType.Simple || !string.IsNullOrEmpty(newNode.Key))
                        throw new InvalidOperationException("List 节点只允许添加 Key 为 null 的 Simple 节点");
                }

                // 检查是否已存在：默认按 Key 同名；existingPredicate 提供时按谓词判定
                // （如 spriteType 按第一层 name 字段定位，不同 name 的 spriteType 均可添加）。
                AstNode? existing = null;
                if (existingPredicate != null)
                {
                    existing = parent.Children.FirstOrDefault(existingPredicate);
                }
                else if (newNode.Type == NodeType.Simple && !string.IsNullOrEmpty(newNode.Key))
                {
                    existing = parent.Children.FirstOrDefault(c => c.Type == NodeType.Simple && c.Key == newNode.Key);
                }
                else if (newNode.Type == NodeType.Block && !string.IsNullOrEmpty(newNode.Key))
                {
                    existing = parent.Children.FirstOrDefault(c => c.Type == NodeType.Block && c.Key == newNode.Key);
                }

                if (existing != null)
                {
                    if (existingPredicate != null)
                    {
                        // 条件命中的已存在节点：直接替换内容（保留位置），
                        // 避免按 Key 定位失效（如同名 spriteType 块）。
                        int index = parent.Children.IndexOf(existing);
                        if (index == -1)
                            throw new InvalidOperationException("已存在节点不在父节点列表中（内部错误）");
                        var clone = CloneNode(newNode);
                        parent.Children[index] = clone;
                        UpdateConstantIndexForNode(existing, clone);
                        _logger.LogDebug("AddConfigNode: 条件命中已存在节点，替换: {Key}", newNode.Key ?? "<无Key>");
                        return;
                    }

                    _logger.LogDebug("AddConfigNode: 节点已存在，自动转为更新: {Key}", newNode.Key);
                    // 标准搜索追加段（新规范）：定位 parent 下同 Key 的 Simple/Block 字段
                    var appendSelector = new Dictionary<string, object>
                    {
                        ["mode"] = newNode.Type == NodeType.Simple ? "Simple" : "Block",
                        ["match"] = new Dictionary<string, object>
                        {
                            ["rule"] = new List<object>
                            {
                                new Dictionary<string, object> { ["target"] = "key", ["keywords"] = new List<object> { newNode.Key! } }
                            }
                        }
                    };
                    UpdateConfigNode(relativePath,
                        parentPath.Concat(new object[] { appendSelector }).ToList(),
                        newNode, fullReplace: true);
                    return;
                }

                var clone2 = CloneNode(newNode);
                parent.Children.Add(clone2);
                UpdateConstantIndexForNode(null, clone2);
                _logger.LogDebug("添加配置节点: {Path} -> {Key}", normPath, newNode.Key ?? "<无Key>");
            }
        }

        /// <summary>
        /// 删除指定文件 AST 中由路径定位到的节点。
        /// 若定位到多个节点，抛出 InvalidOperationException。
        /// </summary>
        public void RemoveConfigNode(string relativePath, List<object> targetPath)
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new ArgumentNullException(nameof(relativePath));
            if (targetPath == null)
                throw new ArgumentNullException(nameof(targetPath));

            lock (_stateLock)
            {
                string normPath = NormalizePath(relativePath);
                if (!_configResults.TryGetValue(normPath, out var result))
                    return;

                var resolveResult = SelectorResolver.Resolve(result.RootNodes, targetPath);
                if (resolveResult.HasErrors)
                    foreach (var err in resolveResult.Errors) _logger.LogWarning("定位提示: {Msg}", err.Message);
                var nodes = resolveResult.Hits;
                if (nodes.Count == 0)
                    return;

                if (nodes.Count > 1)
                    throw new InvalidOperationException($"目标路径定位到多个节点，无法删除: {string.Join(" -> ", targetPath)}");

                var target = nodes.First();
                // 目标所在父：从文件根遍历找真正包含 target 的父。
                // （不能用 parentPath 前段定位——Key 下钻到叶后前段返回叶自身；int 选同层第几个时前段多匹配。）
                var parent = FindParentContaining(result.RootNodes, target);
                if (parent == null)
                    return;
                if (parent.Children.Remove(target))
                {
                    UpdateConstantIndexForNode(target, null);
                    _logger.LogDebug("删除配置节点: {Path} -> {Key}", normPath, target.Key ?? "无Key");
                }
            }
        }

        /// <summary>
        /// 重命名标准选择路径定位到的节点的 Key（Simple/Block/List 均可）。
        /// Key 与值（Value/RawText）独立——RawText 只记录值的原始文本（不含 Key），改名不影响值，
        /// 故 RawText 保留。无 Key 索引依赖（常量/本地化索引按值/路径，改名安全）。
        /// 定位到多个节点抛异常；定位不到静默返回（记日志）。
        /// </summary>
        public void RenameKey(string relativePath, List<object> targetPath, string newKey)
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new ArgumentNullException(nameof(relativePath));
            if (targetPath == null)
                throw new ArgumentNullException(nameof(targetPath));
            if (string.IsNullOrEmpty(newKey))
                throw new ArgumentNullException(nameof(newKey));

            lock (_stateLock)
            {
                string normPath = NormalizePath(relativePath);
                if (!_configResults.TryGetValue(normPath, out var result))
                    return;

                var resolveResult = SelectorResolver.Resolve(result.RootNodes, targetPath);
                if (resolveResult.HasErrors)
                    foreach (var err in resolveResult.Errors) _logger.LogWarning("定位提示: {Msg}", err.Message);
                var nodes = resolveResult.Hits;
                if (nodes.Count == 0)
                {
                    _logger.LogDebug("RenameKey: 目标不存在，静默返回: {Path}", string.Join(" -> ", targetPath));
                    return;
                }
                if (nodes.Count > 1)
                    throw new InvalidOperationException($"目标路径定位到多个节点，无法重命名: {string.Join(" -> ", targetPath)}");

                var target = nodes.First();
                string? oldKey = target.Key;
                target.Key = newKey;
                // 值不变 → Value/RawText 均保留（序列化输出 {新Key} = {RawText ?? 由Value生成}）
                _logger.LogDebug("重命名节点: {Path} -> {Old} → {New}", normPath, oldKey ?? "<无Key>", newKey);
            }
        }

        /// <summary>
        /// 更新指定文件 AST 中由路径定位到的节点。
        /// targetPredicate（可选）：自定义"目标定位/验证"——从定位结果中取第一个满足谓词的
        /// 节点作为目标；找不到（或都不满足）→ 视为需要 Add（upsert，把谓词作为 Add 的
        /// 已存在判定）。默认 null = 现有行为（定位结果首个节点，定位不到按 fullReplace 决定）。
        /// 若定位到多个节点，抛出 InvalidOperationException。
        /// </summary>
        public void UpdateConfigNode(string relativePath, List<object> targetPath, AstNode newNode,
            bool fullReplace = false, Func<AstNode, bool>? targetPredicate = null)
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new ArgumentNullException(nameof(relativePath));
            if (targetPath == null)
                throw new ArgumentNullException(nameof(targetPath));
            if (newNode == null)
                throw new ArgumentNullException(nameof(newNode));

            lock (_stateLock)
            {
                string normPath = NormalizePath(relativePath);

                EnsureConfigFileExists(normPath);

                var result = _configResults[normPath];
                var resolveResult = SelectorResolver.Resolve(result.RootNodes, targetPath);
                if (resolveResult.HasErrors)
                    foreach (var err in resolveResult.Errors) _logger.LogWarning("定位提示: {Msg}", err.Message);
                var nodes = resolveResult.Hits;

                // 目标定位：targetPredicate 提供时取第一个满足谓词的节点
                AstNode? target = null;
                if (nodes.Count > 0)
                {
                    target = targetPredicate == null ? nodes.First() : nodes.FirstOrDefault(targetPredicate);
                }

                if (target == null)
                {
                    // 找不到（或都不满足条件）→ 视为需要 Add（upsert）
                    if (fullReplace || targetPredicate != null)
                    {
                        var parentPath = targetPath.Take(targetPath.Count - 1).ToList();
                        var last = targetPath.LastOrDefault();
                        if (targetPredicate != null
                            || (last is string key && !string.IsNullOrEmpty(key)))
                        {
                            _logger.LogDebug("UpdateConfigNode: 目标不存在，自动转为添加: {Key}", last);
                            AddConfigNode(relativePath, parentPath, newNode, existingPredicate: targetPredicate);
                            return;
                        }
                        throw new InvalidOperationException("无法自动转换：路径最后一段不是 KeySelector");
                    }
                    throw new InvalidOperationException($"目标节点不存在: {string.Join(" -> ", targetPath)}");
                }

                if (targetPredicate == null && nodes.Count > 1)
                    throw new InvalidOperationException($"目标路径定位到多个节点，无法更新: {string.Join(" -> ", targetPath)}");

                if (fullReplace)
                {
                    var parentPath = targetPath.Take(targetPath.Count - 1).ToList();
                    var parentsResolve = SelectorResolver.Resolve(result.RootNodes, parentPath);
                if (parentsResolve.HasErrors)
                    foreach (var err in parentsResolve.Errors) _logger.LogWarning("UpdateConfigNode 父定位提示: {Msg}", err.Message);
                var parents = parentsResolve.Hits;
                    if (parents.Count == 0)
                        throw new InvalidOperationException("父节点不存在");
                    var parent = parents.First();
                    int index = parent.Children.IndexOf(target);
                    if (index == -1)
                        throw new InvalidOperationException($"目标节点不在父节点列表中（内部错误）: parent={parent.Key ?? "<无Key>"} target={target.Key ?? "<无Key>"} type={target.Type}");
                    var clone = CloneNode(newNode);
                    parent.Children[index] = clone;
                    UpdateConstantIndexForNode(target, clone);
                    _logger.LogDebug("完全替换节点: {Path} -> {Key}", normPath, newNode.Key ?? "无Key");
                }
                else
                {
                    if (target.Type != NodeType.Simple)
                        throw new InvalidOperationException($"增量更新仅支持 Simple 节点，实际类型: {target.Type}");
                    if (newNode.Type != NodeType.Simple)
                        throw new InvalidOperationException($"增量更新仅允许 Simple 节点作为新值，实际类型: {newNode.Type}");
                    // 先移除旧引用（此时 target 仍为旧状态），修改，再注册新引用（规范 4.3 / 8.2 / 8.4）
                    RemoveSubtreeFromIndex(target);
                    target.Value = newNode.Value;
                    target.IsQuoted = newNode.IsQuoted;
                    // RawText 规则（规范 8.2）：新节点 RawText 非 null 时覆盖；为 null 时保留原值
                    if (newNode.RawText != null)
                        target.RawText = newNode.RawText;
                    AddSubtreeToIndex(target);
                    _logger.LogDebug("增量更新 Simple 节点: {Path} -> {Key} = {Value}", normPath, target.Key, target.Value);
                }
            }
        }

        // ==================== 16.5 本地化内存操作接口 ====================

        /// <summary>
        /// 16.5.1 添加本地化条目。
        /// 若 key 已存在且新 root 优先级更高，则覆盖；否则抛出异常。
        /// </summary>
        public void AddLocalisationEntry(string lang, string path, string key, string value, string? root = null, string? oldPath = null)
        {
            if (string.IsNullOrEmpty(lang))
                throw new ArgumentNullException(nameof(lang));
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            string effectiveRoot = GetEffectiveRoot(root);

            lock (_stateLock)
            {
                _localisationFiles.Add(lang + " " + path); // 文件登记（供落盘清理旧文件）

                EnsureLocalisationLanguageExists(lang);

                var dict = _localisationTable[lang];
                if (dict.TryGetValue(key, out var existing))
                {
                    // 无条件覆盖：写入路径/根目录由引擎层决定（默认 Roots 最后一位），底层不因 root 优先级拒绝
                    string prevPath = existing.CurrentPath;
                    existing.Value = value;
                    existing.LogicalValue = value;
                    existing.CurrentPath = path;
                    existing.OldPath = prevPath;
                    existing.Root = effectiveRoot;
                    _logger.LogDebug("AddLocalisationEntry: 覆盖已有条目 {Key}", key);
                }
                else
                {
                    // key 不存在，直接插入；oldPath 用于记录"从旧文件转移而来"（键新旧文件不同 → 保存时重写旧文件）
                    dict[key] = new LocalisationEntry
                    {
                        Value = value,
                        LogicalValue = value,
                        CurrentPath = path,
                        OldPath = string.IsNullOrEmpty(oldPath) ? path : oldPath,
                        Root = effectiveRoot
                    };
                    _logger.LogDebug("AddLocalisationEntry: 添加新条目 {Key} -> {Value} (oldPath={OldPath})", key, value, oldPath);
                }
            }
        }

        /// <summary>
        /// 16.5.2 删除本地化条目。
        /// 仅当条目的 CurrentPath == path 时才删除，否则静默返回。
        /// </summary>
        public void RemoveLocalisationEntry(string lang, string path, string key)
        {
            if (string.IsNullOrEmpty(lang))
                throw new ArgumentNullException(nameof(lang));
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            lock (_stateLock)
            {
                if (!_localisationTable.TryGetValue(lang, out var dict))
                    return;

                if (!dict.TryGetValue(key, out var entry))
                    return;

                if (entry.CurrentPath == path)
                {
                    dict.Remove(key);
                    _logger.LogDebug("RemoveLocalisationEntry: 删除条目 {Key} 从路径 {Path}", key, path);
                }
                // else: 该 key 不在目标路径下，静默返回
            }
        }

        /// <summary>
        /// 16.5.3 更新本地化条目。
        /// 若 key 不存在，则自动转为添加。
        /// 若 key 存在，更新 Value，若 CurrentPath != path 则更新 CurrentPath。
        /// </summary>
        public void UpdateLocalisationEntry(string lang, string path, string key, string newValue, string? root = null)
        {
            if (string.IsNullOrEmpty(lang))
                throw new ArgumentNullException(nameof(lang));
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));
            if (newValue == null)
                throw new ArgumentNullException(nameof(newValue));

            string effectiveRoot = GetEffectiveRoot(root);

            lock (_stateLock)
            {
                EnsureLocalisationLanguageExists(lang);

                var dict = _localisationTable[lang];
                if (dict.TryGetValue(key, out var existing))
                {
                    // key 存在
                    bool pathChanged = existing.CurrentPath != path;
                    bool rootChanged = false;

                    if (pathChanged)
                    {
                        // 检查新 root 优先级是否高于现有 root
                        int existingRootIndex = GetRootIndex(existing.Root);
                        int newRootIndex = GetRootIndex(effectiveRoot);
                        if (newRootIndex >= existingRootIndex)
                        {
                            // 新 root 优先级 >= 现有 root，允许移动
                            existing.CurrentPath = path;
                            if (newRootIndex > existingRootIndex)
                            {
                                existing.Root = effectiveRoot;
                                rootChanged = true;
                            }
                            // OldPath 保持不变（由 WriteLocalisation 负责更新）
                        }
                        else
                        {
                            // 新 root 优先级低于现有 root，拒绝移动
                            throw new InvalidOperationException(
                                $"无法将条目 '{key}' 移动到 '{path}'：新 root '{effectiveRoot}' 优先级低于现有 root '{existing.Root}'");
                        }
                    }

                    // 更新 Value 与 LogicalValue（逻辑值 = 原文，显示值 = 未展开；由 ExpandLocalisationKey 展开显示值）
                    existing.Value = newValue;
                    existing.LogicalValue = newValue;
                    _logger.LogDebug("UpdateLocalisationEntry: 更新条目 {Key} -> {Value} (PathChanged: {PathChanged}, RootChanged: {RootChanged})",
                        key, newValue, pathChanged, rootChanged);
                }
                else
                {
                    // key 不存在，自动转为添加
                    _logger.LogDebug("UpdateLocalisationEntry: key 不存在，自动转为添加: {Key}", key);
                    AddLocalisationEntry(lang, path, key, newValue, effectiveRoot);
                }
            }
        }

        /// <summary>
        /// 16.5.4 获取指定语言中所有条目（只读副本）。
        /// </summary>
        public IReadOnlyDictionary<string, (string Value, string Path, string Root)>? GetLocalisationEntries(string lang)
        {
            if (string.IsNullOrEmpty(lang))
                throw new ArgumentNullException(nameof(lang));

            lock (_stateLock)
            {
                if (!_localisationTable.TryGetValue(lang, out var dict))
                    return null;

                var result = new Dictionary<string, (string Value, string Path, string Root)>();
                foreach (var kv in dict)
                {
                    result[kv.Key] = (kv.Value.Value, kv.Value.CurrentPath, kv.Value.Root);
                }
                return result;
            }
        }

        /// <summary>
        /// 16.5.5 批量添加本地化条目（用于扫描阶段）。
        /// </summary>
        public void AddLocalisationEntries(string lang, string path, string root, Dictionary<string, string> entries)
        {
            if (string.IsNullOrEmpty(lang))
                throw new ArgumentNullException(nameof(lang));
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (string.IsNullOrEmpty(root))
                throw new ArgumentNullException(nameof(root));
            if (entries == null)
                throw new ArgumentNullException(nameof(entries));

            if (entries.Count == 0)
                return;

            string effectiveRoot = NormalizePath(root);

            lock (_stateLock)
            {
                EnsureLocalisationLanguageExists(lang);

                var dict = _localisationTable[lang];

                foreach (var kv in entries)
                {
                    if (dict.TryGetValue(kv.Key, out var existing))
                    {
                        // 无条件覆盖：写入路径/根目录由引擎层决定（默认 Roots 最后一位），底层不因 root 优先级拒绝
                        string oldPath = existing.CurrentPath;
                        existing.Value = kv.Value;
                        existing.LogicalValue = kv.Value;
                        existing.CurrentPath = path;
                        existing.OldPath = oldPath;
                        existing.Root = effectiveRoot;
                        _logger.LogDebug("AddLocalisationEntries: 覆盖已有条目 {Key}", kv.Key);
                    }
                    else
                    {
                        dict[kv.Key] = new LocalisationEntry
                        {
                            Value = kv.Value,
                            LogicalValue = kv.Value,
                            CurrentPath = path,
                            OldPath = path,
                            Root = effectiveRoot
                        };
                    }
                }
                _logger.LogDebug("AddLocalisationEntries: 批量添加 {Count} 个条目到 {Lang} 的 {Path}", entries.Count, lang, path);
            }
        }

        /// <summary>
        /// 16.5.6 删除指定路径对应的文件中的所有条目。
        /// </summary>
        public void RemoveLocalisationFile(string lang, string path)
        {
            if (string.IsNullOrEmpty(lang))
                throw new ArgumentNullException(nameof(lang));
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            lock (_stateLock)
            {
                if (!_localisationTable.TryGetValue(lang, out var dict))
                    return;

                var keysToRemove = dict
                    .Where(kv => kv.Value.CurrentPath == path)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    dict.Remove(key);
                    _logger.LogDebug("RemoveLocalisationFile: 移除条目 {Key} 从路径 {Path}", key, path);
                }
            }
        }

        /// <summary>
        /// 16.5.7 获取指定路径对应的文件中的所有条目（键 -> 值）。
        /// </summary>
        public Dictionary<string, string> GetLocalisationFile(string lang, string path)
        {
            if (string.IsNullOrEmpty(lang))
                throw new ArgumentNullException(nameof(lang));
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            lock (_stateLock)
            {
                var result = new Dictionary<string, string>();
                if (!_localisationTable.TryGetValue(lang, out var dict))
                    return result;

                foreach (var kv in dict)
                {
                    if (kv.Value.CurrentPath == path)
                    {
                        result[kv.Key] = kv.Value.Value;
                    }
                }
                return result;
            }
        }

        // ==================== 内部辅助方法 ====================

        private void EnsureConfigFileExists(string normPath)
        {
            if (!_configResults.ContainsKey(normPath))
            {
                CreateEmptyFileInMemory(normPath, FileCategory.Config);
            }
        }

        private void EnsureLocalisationLanguageExists(string lang)
        {
            if (!_localisationTable.ContainsKey(lang))
            {
                _localisationTable[lang] = new Dictionary<string, LocalisationEntry>();
                _logger.LogDebug("创建本地化语言表: {Lang}", lang);
            }
        }

        private string GetEffectiveRoot(string? root)
        {
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                return NormalizePath(root);
            if (_roots.Count > 0)
                return _roots[^1];
            throw new InvalidOperationException("没有可用的根目录");
        }

        private int GetRootIndex(string root)
        {
            return _roots.IndexOf(root);
        }

        public AstNode CloneNode(AstNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var clone = new AstNode
            {
                Type = node.Type,
                Key = node.Key,
                Value = node.Value,
                IsQuoted = node.IsQuoted,
                StartLine = node.StartLine,
                EndLine = node.EndLine,
                StartColumn = node.StartColumn,
                EndColumn = node.EndColumn,
                IndentWidth = node.IndentWidth,
                OriginalLayout = node.OriginalLayout,
                SeparatorType = node.SeparatorType,
                RawText = node.RawText
            };
            foreach (var child in node.Children)
                clone.Children.Add(CloneNode(child));
            foreach (var comment in node.AssociatedComments)
                clone.AssociatedComments.Add(CloneNode(comment));
            return clone;
        }

        /// <summary>
        /// 解析路径，返回定位到的节点列表。
        /// </summary>
        /// <summary>从根节点列表递归查找包含 target 的父节点（RemoveConfigNode 用——任何 targetPath 形式通用）。</summary>
        private static AstNode? FindParentContaining(List<AstNode> roots, AstNode target)
        {
            foreach (var n in roots)
            {
                if (n.Children.Contains(target))
                    return n;
                var found = FindParentContaining(n.Children, target);
                if (found != null)
                    return found;
            }
            return null;
        }

        [Obsolete("已放弃：宽匹配半成品——请用 SelectorResolver（标准搜索）")]
        private List<AstNode> ResolvePath(ParserResult result, List<object> path, bool autoCreateBlocks)
            => ResolveNodes(result.RootNodes, path, autoCreateBlocks);

        /// <summary>
        [Obsolete("已放弃：宽匹配半成品——请用 SelectorResolver（标准搜索）")]
        /// 路径选择器解析核心（仅旧 ResolvePath 用——已放弃）：
        /// 选择器类型——string（Key 宽匹配，自身/子节点都收）、(string,object) 元组（字段=值条件）、
        /// int（当前列表第几个）、字典（**条件式查询**：mode/match/index——见 SelectNodes 文档）。
        /// </summary>
        private List<AstNode> ResolveNodes(List<AstNode> roots, List<object> path, bool autoCreateBlocks)
        {
            if (path == null || path.Count == 0)
                return roots.ToList();

            var currentNodes = roots.ToList();

            foreach (var selector in path)
            {
                if (selector is string keySelector)
                {
                    var matched = new List<AstNode>();
                    foreach (var node in currentNodes)
                    {
                        // 当前节点自身匹配（第一级 selector 命中 RootNodes，如 "spriteTypes"）
                        if ((node.Type == NodeType.Block || node.Type == NodeType.List || node.Type == NodeType.InlineScript)
                            && node.Key == keySelector)
                        {
                            matched.Add(node);
                        }
                        // 子节点匹配（下一级 selector 深入）
                        if (node.Type == NodeType.Block || node.Type == NodeType.List || node.Type == NodeType.InlineScript)
                        {
                            matched.AddRange(node.Children.Where(c => c.Key == keySelector));
                        }
                    }
                    if (matched.Count == 0 && autoCreateBlocks)
                    {
                        AstNode parent = currentNodes.LastOrDefault() ?? roots.LastOrDefault();
                        if (parent == null)
                        {
                            var newBlock = new AstNode
                            {
                                Type = NodeType.Block,
                                Key = keySelector,
                                Children = new List<AstNode>(),
                                OriginalLayout = OriginalLayout.MultiLine
                            };
                            roots.Add(newBlock);
                            matched.Add(newBlock);
                        }
                        else
                        {
                            var newBlock = new AstNode
                            {
                                Type = NodeType.Block,
                                Key = keySelector,
                                Children = new List<AstNode>(),
                                OriginalLayout = OriginalLayout.MultiLine
                            };
                            parent.Children.Add(newBlock);
                            matched.Add(newBlock);
                        }
                        _logger.LogDebug("自动创建 Block 节点: {Key}", keySelector);
                    }
                    currentNodes = matched;
                }
                // 元组条件选择器（如 ("name", "GFX_xxx")）：用元组模式匹配，
                // 兼容调用方传 ValueTuple<string,string> 或 ValueTuple<string,object>
                // （装箱后 is ValueTuple<string,object> 对前者恒 false 的缺陷）。
                else if (selector is (string condKey, object condValue))
                {
                    var filtered = new List<AstNode>();
                    foreach (var node in currentNodes)
                    {
                        if (node.Type == NodeType.Simple && node.Key == condKey && Equals(node.Value, condValue))
                            filtered.Add(node);
                        else if (node.Type == NodeType.Block)
                        {
                            // 情况 A：node 自身含匹配字段（currentNodes 已是目标块，如 spriteType）
                            if (node.Children.Any(c => c.Type == NodeType.Simple && c.Key == condKey && Equals(c.Value, condValue)))
                                filtered.Add(node);
                            // 情况 B：node 的 Block 子节点含匹配字段（如 spriteTypes 下的 spriteType）
                            foreach (var sub in node.Children)
                            {
                                if (sub.Type == NodeType.Block
                                    && sub.Children.Any(c => c.Type == NodeType.Simple && c.Key == condKey && Equals(c.Value, condValue)))
                                    filtered.Add(sub);
                            }
                        }
                    }
                    currentNodes = filtered;
                }
                else if (selector is int indexSelector)
                {
                    // int = "当前列表第几个"（同层第几个——配合 Key 下钻用于区分重名；不做下钻）
                    if (indexSelector >= 0 && indexSelector < currentNodes.Count)
                        currentNodes = new List<AstNode> { currentNodes[indexSelector] };
                    else
                        currentNodes = new List<AstNode>();
                }
                else if (selector is IDictionary<string, object> dictSelector)
                {
                    // 字典选择器属新标准（SelectorResolver）——旧宽匹配不支持
                    throw new ArgumentException("字典选择器请用 SelectorResolver（标准搜索）");
                }
                else
                {
                    throw new ArgumentException($"不支持的路径选择器类型: {selector.GetType()}");
                }

                if (currentNodes.Count == 0)
                    break;
            }

            return currentNodes;
        }


    }
}