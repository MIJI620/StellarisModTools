// 文件: Stellaris.Engine/Technology/TechnologyEngine.cs
// 科技引擎（只读浏览）：扫描 common/technology/*.txt → TechNode 模型 + 专属索引。
// 铁律：引擎层绝不直接操作磁盘/底层——一律经 StellarisAdapter
// （GetFilesInDirectory / GetConfig / ResolveConstantInput / GetLocalisedText）。
// 复用：modifier 本地化显示 → StaticModifierEngine（基础索引）；图标加载 → ImageAssetEngine。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Parser;
using Stellaris.Engine.StaticModifier;

namespace Stellaris.Engine.Technology;

/// <summary>
/// 科技引擎：负责 common/technology 的扫描、解析与索引（后续拓展：可视化编辑/写回）。
/// 数据源 = SA 读取的合并后 AST（正确数据）；只读浏览，不落盘。
/// </summary>
public sealed class TechnologyEngine
{
    private readonly StellarisAdapter _adapter;
    private readonly StaticModifierEngine _modifiers;
    private readonly ILogger _logger;

    // ==================== 专属索引 ====================
    private readonly Dictionary<string, TechNode> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TechNode>> _byArea = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TechNode>> _byCategory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, List<TechNode>> _byTier = new();
    private readonly Dictionary<string, List<TechNode>> _children = new(StringComparer.OrdinalIgnoreCase);  // 前置 key → 后继科技（反查）
    private readonly Dictionary<string, string> _categoryIcons = new(StringComparer.OrdinalIgnoreCase);      // 学科 → category icon 相对路径
    private readonly Dictionary<string, List<(string Key, string Value)>> _modifierCache = new(StringComparer.OrdinalIgnoreCase);  // 科技 → modifier 条目（语言无关）
    private readonly Dictionary<string, string> _techFileCache = new(StringComparer.OrdinalIgnoreCase);     // 科技 key → 所在文件相对路径
    private Dictionary<string, List<string>>? _unlockIndex;   // 前置 key → 含该前置的 block key（全局，含科技以外）

    /// <summary>学科 icon 相对路径前缀（category 文件内 icon 已是完整相对路径，直接沿用）。</summary>
    private const string TechIconDir = "gfx/interface/icons/technologies/";

    public TechnologyEngine(StellarisAdapter adapter, StaticModifierEngine? modifiers = null, ILogger? logger = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _modifiers = modifiers ?? new StaticModifierEngine(adapter, NullLogger.Instance);
        _logger = logger ?? NullLogger.Instance;
    }

    // ==================== 扫描 ====================

    /// <summary>重新扫描（幂等）：重建全部索引。失败的文件跳过并记日志，不影响其他文件。</summary>
    public void ScanAll()
    {
        _byKey.Clear();
        _byArea.Clear();
        _byCategory.Clear();
        _byTier.Clear();
        _children.Clear();
        _categoryIcons.Clear();

        LoadCategoryIcons();

        var files = _adapter.GetFilesInDirectory("common/technology", "*.txt");
        foreach (var relPath in files)
        {
            try
            {
                var result = _adapter.GetConfig(relPath);
                if (result == null)
                    continue;
                foreach (var node in result.RootNodes)
                {
                    if (node.Type != NodeType.Block)
                        continue;
                    var tech = ParseTech(node);
                    if (tech == null)
                        continue;
                    tech.OwnerFile = relPath;   // 所属文件（相对路径——用户：修改弹窗不能为空，落盘依据）
                    Index(tech);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "科技扫描失败: {Path}", relPath);
            }
        }

        // 后继反查索引
        foreach (var tech in _byKey.Values)
        {
            foreach (var pre in tech.Prerequisites)
            {
                if (!_children.TryGetValue(pre, out var list))
                    _children[pre] = list = new List<TechNode>();
                list.Add(tech);
            }
        }
        _logger.LogInformation("科技引擎索引完成: {Count} 条", _byKey.Count);
    }

    private void LoadCategoryIcons()
    {
        var result = _adapter.GetConfig("common/technology/category/00_category.txt");
        if (result == null)
            return;
        foreach (var node in result.RootNodes)
        {
            if (node.Type != NodeType.Block || string.IsNullOrEmpty(node.Key))
                continue;
            var iconNode = node.Children.FirstOrDefault(c => c.Type == NodeType.Simple
                && string.Equals(c.Key, "icon", StringComparison.OrdinalIgnoreCase));
            var icon = iconNode?.Value?.ToString();
            if (!string.IsNullOrEmpty(icon))
                _categoryIcons[node.Key] = icon;
        }
    }

    private TechNode? ParseTech(AstNode block)
    {
        var tech = new TechNode { Key = block.Key ?? "" };
        if (tech.Key.Length == 0)
            return null;

        foreach (var child in block.Children)
        {
            string key = child.Key ?? "";
            switch (child.Type)
            {
                case NodeType.Simple:
                    string? v = child.Value?.ToString();
                    switch (key)
                    {
                        case "area":
                            tech.Area = v ?? "";
                            break;
                        case "tier":
                            tech.Tier = ResolveInt(v);
                            break;
                        case "cost":
                            tech.Cost = ResolveInt(v);
                            break;
                        case "levels":
                            tech.Levels = ResolveInt(v);
                            tech.HasLevels = true;
                            break;
                        case "cost_per_level":
                            tech.CostPerLevel = ResolveInt(v);
                            tech.HasCostPerLevel = true;
                            break;
                        case "is_rare":
                            tech.IsRare = IsYes(v);
                            break;
                        case "is_dangerous":
                            tech.IsDangerous = IsYes(v);
                            break;
                        case "start_tech":
                            tech.StartTech = IsYes(v);
                            break;
                        case "icon":
                            tech.Icon = v;
                            break;
                        case "weight":
                            tech.Weight = v;   // 弹窗编辑字段（不落盘）
                            break;
                    }
                    break;

                case NodeType.Block:
                case NodeType.List:
                    // category = { biology } / prerequisites = { "tech_x" } 是 List（children Simple，Key 空、Value=元素值）；
                    // 防御兼容 Block 形态（取 Key）。
                    if (string.Equals(key, "cost", StringComparison.OrdinalIgnoreCase))
                    {
                        // cost 块形态（cost = { factor = ... } 动态花费）→ 原文存 CostRaw（弹窗"自定义"模式编辑）
                        tech.CostRaw = BlockToText(child);
                    }
                    else if (string.Equals(key, "category", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var cat in child.Children)
                        {
                            var cv = cat.Value?.ToString();
                            if (!string.IsNullOrEmpty(cv))
                                tech.Categories.Add(cv);
                            else if (!string.IsNullOrEmpty(cat.Key))
                                tech.Categories.Add(cat.Key);
                        }
                    }
                    else if (string.Equals(key, "prerequisites", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var pre in child.Children)
                        {
                            var pv = pre.Value?.ToString();
                            if (!string.IsNullOrEmpty(pv))
                                tech.Prerequisites.Add(pv);
                            else if (!string.IsNullOrEmpty(pre.Key))
                                tech.Prerequisites.Add(pre.Key);
                        }
                    }
                    else if (string.Equals(key, "prereqfor_desc", StringComparison.OrdinalIgnoreCase))
                    {
                        tech.PrereqForDesc = BlockToText(child);   // 弹窗编辑字段
                    }
                    else if (string.Equals(key, "potential", StringComparison.OrdinalIgnoreCase))
                    {
                        tech.PotentialRaw = BlockToText(child);
                    }
                    else if (string.Equals(key, "modifier", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var m in child.Children)
                            if (m.Type == NodeType.Simple && !string.IsNullOrEmpty(m.Key))
                                tech.ModifierEntries.Add((m.Key, m.Value?.ToString() ?? ""));
                    }
                    else if (string.Equals(key, "weight_modifier", StringComparison.OrdinalIgnoreCase))
                    {
                        tech.WeightModifierRaw = BlockToText(child);
                    }
                    else if (string.Equals(key, "ai_weight", StringComparison.OrdinalIgnoreCase))
                    {
                        tech.AiWeightRaw = BlockToText(child);
                    }
                    break;
            }
        }
        return tech;
    }

    private void Index(TechNode tech)
    {
        _byKey[tech.Key] = tech;
        if (tech.Area.Length > 0)
            AddTo(_byArea, tech.Area, tech);
        foreach (var cat in tech.Categories)
            AddTo(_byCategory, cat, tech);
        if (tech.Tier >= 0)
            AddTo(_byTier, tech.Tier, tech);
    }

    private static void AddTo(Dictionary<string, List<TechNode>> dict, string key, TechNode tech)
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = new List<TechNode>();
        list.Add(tech);
    }

    private static void AddTo(Dictionary<int, List<TechNode>> dict, int key, TechNode tech)
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = new List<TechNode>();
        list.Add(tech);
    }

    // ==================== 内存编辑（本期不落盘——用户确认） ====================

    /// <summary>内存新建科技（不落盘）：加入全部索引 + 后继反查。key 必须全局唯一。
    /// 同时登记 dirty + 所属文件到待保存索引（用户规则：所有保存必须显式登记，用户触发才落盘）。</summary>
    public void AddItem(TechNode tech)
    {
        if (tech == null || string.IsNullOrEmpty(tech.Key) || _byKey.ContainsKey(tech.Key))
            return;
        Index(tech);
        foreach (var pk in tech.Prerequisites)
            if (_byKey.TryGetValue(pk, out var p) && p != tech)
                AddTo(_children, pk, tech);
        _dirtyTechKeys.Add(tech.Key);
        RegisterTechFile(tech.OwnerFile);   // 创建 → 所属文件登记待保存
    }

    /// <summary>内存更新科技（不落盘）：key 不变时直接改字段 + 重建索引（area/category/tier 可能变化）；key 变化用两参版本。
    /// 登记 dirty + 新所属文件；OwnerFile 改了且旧文件除本科技外无其他内容 → 旧文件从待写索引移出（用户规则）。</summary>
    public void UpdateItem(TechNode tech)
    {
        if (tech == null || string.IsNullOrEmpty(tech.Key))
            return;
        string? oldFile = _byKey.TryGetValue(tech.Key, out var existing) ? existing.OwnerFile : null;
        RemoveItem(tech.Key);
        AddItem(tech);
        if (!string.IsNullOrEmpty(oldFile)
            && !string.Equals(oldFile, tech.OwnerFile, StringComparison.OrdinalIgnoreCase)
            && TechFileHasOnly(oldFile, tech.Key))
            _pendingTechFiles.Remove(oldFile);   // 旧文件没有其他内容 → 移出索引（不写空/残留）
    }

    /// <summary>内存更新科技（不落盘，key 已改名）：先移除旧 key 再按新 key 加索引。</summary>
    public void UpdateItem(string oldKey, TechNode tech)
    {
        RemoveItem(oldKey);
        AddItem(tech);
    }

    /// <summary>内存移除科技（不落盘）：从全部索引 + 后继反查移除。</summary>
    public void RemoveItem(string key)
    {
        if (!_byKey.TryGetValue(key, out var tech))
            return;
        _byKey.Remove(key);
        if (tech.Area.Length > 0 && _byArea.TryGetValue(tech.Area, out var al))
            al.Remove(tech);
        foreach (var cat in tech.Categories)
            if (_byCategory.TryGetValue(cat, out var cl))
                cl.Remove(tech);
        if (tech.Tier >= 0 && _byTier.TryGetValue(tech.Tier, out var tl))
            tl.Remove(tech);
        foreach (var pk in tech.Prerequisites)
            if (_children.TryGetValue(pk, out var cl2))
                cl2.Remove(tech);
    }

    // ==================== 待保存登记（用户规则：所有保存必须显式登记，用户触发才落盘） ====================

    /// <summary>待写科技 .txt 相对路径（创建/修改/删除涉及的科技文件）。</summary>
    private readonly HashSet<string> _pendingTechFiles = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>创建/修改过的科技 key（保存时把块写入所属文件）。</summary>
    private readonly HashSet<string> _dirtyTechKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>删除登记：科技 key（保存前**不改内存**、绘制跳过；保存落盘成功后才从内存移除——防数据丢失）。</summary>
    private readonly HashSet<string> _removedKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>删除登记：key → 原文件（保存时从该文件 AST 移除块）。</summary>
    private readonly Dictionary<string, string> _removedFiles = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>待写本地化文件："lang\0相对路径"（弹窗失焦登记，保存统一落盘）。</summary>
    private readonly HashSet<string> _pendingLocFiles = new(StringComparer.Ordinal);
    /// <summary>待清理本地化文件："lang\0相对路径"（键移走后写剩余/空头，writeIfEmpty）。</summary>
    private readonly HashSet<string> _pendingLocClean = new(StringComparer.Ordinal);

    /// <summary>删除登记（页面绘制过滤用）。</summary>
    public IReadOnlyCollection<string> RemovedKeys => _removedKeys;

    /// <summary>科技是否已登记删除（页面绘制跳过）。</summary>
    public bool IsRemoved(string key) => _removedKeys.Contains(key);

    /// <summary>是否有待保存改动（保存按钮无改动时提示）。</summary>
    public bool HasDirty => _pendingTechFiles.Count > 0 || _dirtyTechKeys.Count > 0
        || _removedKeys.Count > 0 || _pendingLocFiles.Count > 0 || _pendingLocClean.Count > 0;

    /// <summary>登记科技文件（相对路径，含 common/technology/ 前缀）。</summary>
    public void RegisterTechFile(string relPath)
    {
        if (!string.IsNullOrEmpty(relPath))
            _pendingTechFiles.Add(relPath.Replace('\\', '/').TrimStart('/'));
    }

    /// <summary>删除登记（不改内存——防数据丢失；绘制跳过；保存落盘成功后才移除内存）。</summary>
    public void RegisterRemoved(TechNode tech)
    {
        if (tech == null || string.IsNullOrEmpty(tech.Key) || !_byKey.ContainsKey(tech.Key))
            return;
        _removedKeys.Add(tech.Key);
        if (!string.IsNullOrEmpty(tech.OwnerFile))
        {
            _removedFiles[tech.Key] = tech.OwnerFile;
            RegisterTechFile(tech.OwnerFile);   // 删除 → 所在文件登记待保存（保存时从 AST 移除块）
        }
    }

    /// <summary>本地化修改登记（弹窗失焦调用——只写本地化引擎内存 + 登记文件，**不落盘**；落盘由保存统一执行）。
    /// 键原在别的文件 → 旧位置登记待清理（writeIfEmpty，防磁盘残留重复）。
    /// 目标文件 = localisation/{lang}/technologies_{ModPrefix}_l_{lang}.yml（用户规则）。</summary>
    public void UpdateItemLocalisation(string lang, string key, string text, string modPrefix)
    {
        if (string.IsNullOrEmpty(lang) || string.IsNullOrEmpty(key) || text == null)
            return;
        var fileName = "technologies_" + modPrefix + "_l_" + lang + ".yml";
        var targetPath = $"localisation/{lang}/{fileName}";
        var index = _adapter.GetLocalisationKeyFiles(lang);
        string? oldFile = index.TryGetValue(key, out var cur)
            && !string.Equals(cur, targetPath, StringComparison.OrdinalIgnoreCase)
            ? cur : null;
        _adapter.UpdateLocalisationEntry(lang, targetPath, key, text);
        _adapter.ExpandLocalisationKey(lang, key);
        _pendingLocFiles.Add(lang + "\u0000" + targetPath);
        if (oldFile != null)
            _pendingLocClean.Add(lang + "\u0000" + oldFile);
    }

    /// <summary>重载入（刷新 = 重载入——用户规则）：重扫 AST + 清空全部登记。
    /// 未保存的创建/修改丢弃、删除恢复（删除登记清空后科技重新显示）。</summary>
    public void Reload()
    {
        ScanAll();
        _pendingTechFiles.Clear();
        _dirtyTechKeys.Clear();
        _removedKeys.Clear();
        _removedFiles.Clear();
        _pendingLocFiles.Clear();
        _pendingLocClean.Clear();
    }

    /// <summary>保存语种：已加载语种 ?? 回退 english。</summary>
    private IReadOnlyList<string> EnabledLanguages()
    {
        var loaded = _adapter.GetLocalisationLanguages();
        return loaded.Count > 0 ? loaded : new List<string> { "english" };
    }

    /// <summary>文件 AST 中除指定科技 key 外没有其他顶层块（"改文件名后旧文件无其他内容 → 移出待写索引"）。</summary>
    private bool TechFileHasOnly(string relPath, string key)
    {
        var result = _adapter.GetConfig(relPath);
        if (result == null)
            return true;   // 文件不存在（新建科技尚未落盘）→ 视为无其他内容
        return result.RootNodes.All(n => n.Type != NodeType.Block
            || string.Equals(n.Key, key, StringComparison.Ordinal));
    }

    // ==================== 保存（用户显式触发——右键"保存"；全部经 SA 读写） ====================

    /// <summary>统一保存：写登记的全部科技文件（删除块 + 字段级应用 dirty 块）+ 本地化文件。
    /// 数据源 = SA GetConfig 合并 AST（不重建文件）；写 = SA WriteFile（roots[-1] + 自动建目录）。
    /// 删除：保存时才真正从 AST 移除块（保存前不改内存）；落盘成功后从内存移除删除科技 + 清空登记。
    /// 返回 (成功文件数, 错误列表)。</summary>
    public (int Saved, List<string> Errors) SaveAll(string modPrefix)
    {
        var errors = new List<string>();
        if (!HasDirty)
            return (0, errors);
        int saved = 0;

        // ---- 1) 科技 .txt：删除块 + 应用 dirty 科技块（字段级） ----
        foreach (var rel in _pendingTechFiles.ToList())
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
            // 删除块（登记删除且原文件 == 本文件）
            foreach (var key in _removedKeys.ToList())
            {
                if (_removedFiles.TryGetValue(key, out var rf)
                    && string.Equals(rf, rel, StringComparison.OrdinalIgnoreCase))
                {
                    var block = result.RootNodes.FirstOrDefault(n => n.Type == NodeType.Block
                        && string.Equals(n.Key, key, StringComparison.Ordinal));
                    if (block != null)
                        result.RootNodes.Remove(block);
                }
            }
            // 应用 dirty 科技块（所属文件 == 本文件）
            foreach (var key in _dirtyTechKeys.ToList())
            {
                if (!_byKey.TryGetValue(key, out var tech))
                    continue;
                if (!string.Equals(tech.OwnerFile, rel, StringComparison.OrdinalIgnoreCase))
                    continue;
                ApplyTechBlock(result, tech, errors);
            }
            // 格式化省略规则（用户 2026-08）：文件所有科技块清理冗余字段（icon=key、levels=1、cost_per_level 无循环、
            // 空 prerequisites/modifier/prereqfor_desc）——"就当格式化了"，未修改的科技同样应用
            foreach (var block in result.RootNodes.Where(n => n.Type == NodeType.Block))
                NormalizeTechBlock(block);
            // 登记即写（用户 2026-08：右键保存 = 该文件待保存，即使无改动也写——"就当格式化"）
            if (_adapter.WriteFile(rel))
                saved++;
            else
                errors.Add(rel + ": 写入失败");
        }

        // ---- 2) 本地化：写弹窗登记的文件 + 删除词条清理 ----
        if (errors.Count == 0)
        {
            var files = new HashSet<string>(_pendingLocFiles, StringComparer.Ordinal);
            var cleanFiles = new HashSet<string>(_pendingLocClean, StringComparer.Ordinal);
            // 删除词条：键当前所在文件 → 移除 + 登记清理（writeIfEmpty）
            foreach (var key in _removedKeys)
            {
                foreach (var lang in EnabledLanguages())
                {
                    var index = _adapter.GetLocalisationKeyFiles(lang);
                    foreach (var k in new[] { key, key + "_desc" })
                    {
                        if (index.TryGetValue(k, out var cur))
                        {
                            _adapter.RemoveLocalisationEntry(lang, cur, k);
                            cleanFiles.Add(lang + "\u0000" + cur);
                        }
                    }
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

        // ---- 3) 全部成功：删除的科技从内存移除（落盘安全后）+ 清空登记 ----
        if (errors.Count == 0)
        {
            foreach (var key in _removedKeys.ToList())
                RemoveItem(key);
            _removedKeys.Clear();
            _removedFiles.Clear();
            _pendingTechFiles.Clear();
            _dirtyTechKeys.Clear();
            _pendingLocFiles.Clear();
            _pendingLocClean.Clear();
        }
        return (saved, errors);
    }

    /// <summary>把 dirty 科技块应用到文件 AST（字段级：只写脏字段，未编辑字段保留原样；块不存在 → 新建全字段）。</summary>
    private bool ApplyTechBlock(ParserResult result, TechNode tech, List<string> errors)
    {
        var block = result.RootNodes.FirstOrDefault(n => n.Type == NodeType.Block
            && string.Equals(n.Key, tech.Key, StringComparison.Ordinal));
        bool isNew = block == null;
        if (block == null)
        {
            block = new AstNode { Type = NodeType.Block, Key = tech.Key, Children = new List<AstNode>() };
            result.RootNodes.Add(block);
        }
        if (block.Children == null)
            block.Children = new List<AstNode>();
        var fields = isNew ? AllTechFields : tech.DirtyFields;
        bool ok = true;
        foreach (var f in OrderedTechFields)
        {
            if (!fields.Contains(f))
                continue;
            var node = BuildTechFieldNode(tech, f, out bool valid);
            if (!valid)
            {
                errors.Add(tech.Key + "." + f + ": 字段内容不合规（无法解析）");
                ok = false;
                continue;
            }
            if (node == null)
            {
                // 空内容 → 移除该字段（改过的字段以 UI 内容为准）
                block.Children.RemoveAll(c => string.Equals(c.Key, f, StringComparison.Ordinal));
                continue;
            }
            var idx = block.Children.FindIndex(c => string.Equals(c.Key, f, StringComparison.Ordinal));
            if (idx >= 0)
            {
                // 替换时迁移旧节点的关联注释（保留块内注释/格式）
                var oldNode = block.Children[idx];
                if (oldNode.AssociatedComments.Count > 0)
                {
                    var comments = new List<AstNode>(oldNode.AssociatedComments);
                    comments.AddRange(node.AssociatedComments);
                    node.AssociatedComments = comments;
                }
                block.Children[idx] = node;
            }
            else
                block.Children.Add(node);
        }
        return ok;
    }

    /// <summary>科技字段写回顺序（文件规范：area → 数值/列表 → 文本块类）。</summary>
    private static readonly string[] OrderedTechFields =
    {
        TechField.Area, TechField.Tier, TechField.Cost, TechField.Levels, TechField.CostPerLevel,
        TechField.Category, TechField.Prerequisites, TechField.Icon, TechField.Weight,
        TechField.StartTech, TechField.Potential, TechField.Modifier, TechField.WeightModifier,
        TechField.AiWeight, TechField.PrereqForDesc
    };

    private static readonly HashSet<string> AllTechFields = new(OrderedTechFields, StringComparer.OrdinalIgnoreCase);

    /// <summary>格式化省略规则（用户 2026-08）：科技块冗余字段自动省略——
    /// icon 值 = 自身 key（游戏默认 {Key}.dds）、levels = 1（单次）、cost_per_level 无循环（无 levels 或 levels=1）、
    /// 空 prerequisites/modifier/prereqfor_desc。保存（含"格式化"场景）对文件所有科技块应用。</summary>
    private static void NormalizeTechBlock(AstNode block)
    {
        if (block?.Children == null || block.Children.Count == 0)
            return;
        string? key = block.Key;
        // 第一遍：icon=key、levels=1、空 prerequisites/modifier/prereqfor_desc
        for (int i = block.Children.Count - 1; i >= 0; i--)
        {
            var c = block.Children[i];
            if (c == null)
                continue;
            if (c.Type == NodeType.Simple && string.Equals(c.Key, "icon", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(key)
                && string.Equals(c.Value?.ToString()?.Trim('"'), key, StringComparison.OrdinalIgnoreCase))
            {
                block.Children.RemoveAt(i);
                continue;
            }
            if (c.Type == NodeType.Simple && string.Equals(c.Key, "levels", StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Value?.ToString()?.Trim(), "1", StringComparison.Ordinal))
            {
                block.Children.RemoveAt(i);
                continue;
            }
            if ((string.Equals(c.Key, "prerequisites", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(c.Key, "modifier", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(c.Key, "prereqfor_desc", StringComparison.OrdinalIgnoreCase))
                && (c.Children == null || c.Children.Count == 0))
            {
                block.Children.RemoveAt(i);
            }
        }
        // 第二遍：cost_per_level——无循环（块内无 levels 或 levels=1 已被移除）→ 省略
        for (int i = block.Children.Count - 1; i >= 0; i--)
        {
            var c = block.Children[i];
            if (c == null || c.Type != NodeType.Simple
                || !string.Equals(c.Key, "cost_per_level", StringComparison.OrdinalIgnoreCase))
                continue;
            var levelsNode = block.Children.FirstOrDefault(x => x.Type == NodeType.Simple
                && string.Equals(x.Key, "levels", StringComparison.OrdinalIgnoreCase));
            if (levelsNode == null
                || string.Equals(levelsNode.Value?.ToString()?.Trim(), "1", StringComparison.Ordinal))
                block.Children.RemoveAt(i);
        }
    }

    /// <summary>字段 → AST 节点（UI 内容 → 节点；null + valid=true = 空内容 → 移除该字段）。</summary>
    private AstNode? BuildTechFieldNode(TechNode tech, string field, out bool valid)
    {
        valid = true;
        switch (field)
        {
            case TechField.Area:
                if (string.IsNullOrWhiteSpace(tech.Area)) return null;
                return new AstNode { Type = NodeType.Simple, Key = "area", Value = tech.Area };
            case TechField.Tier:
                return new AstNode { Type = NodeType.Simple, Key = "tier", Value = tech.Tier };
            case TechField.Cost:
                if (!string.IsNullOrWhiteSpace(tech.CostRaw))
                    return ParseFieldBlock(tech.CostRaw, "cost", out valid);   // 自定义块（cost = { factor = ... }）
                return new AstNode { Type = NodeType.Simple, Key = "cost", Value = tech.Cost };
            case TechField.Levels:
                if (!tech.HasLevels) return null;   // 单次 → 移除 levels
                return new AstNode { Type = NodeType.Simple, Key = "levels", Value = tech.Levels };
            case TechField.CostPerLevel:
                if (!tech.HasLevels) return null;   // 无循环 → 自动省略 cost_per_level（用户 2026-08）
                return new AstNode { Type = NodeType.Simple, Key = "cost_per_level", Value = tech.CostPerLevel };
            case TechField.Category:
                if (tech.Categories.Count == 0) return null;
                return BuildListNode("category", tech.Categories);
            case TechField.Prerequisites:
                if (tech.Prerequisites.Count == 0) return null;
                return BuildListNode("prerequisites", tech.Prerequisites);
            case TechField.Icon:
                // 图标 = 自身 key → 自动省略（游戏默认 icon = {Key}.dds——用户 2026-08）
                if (string.IsNullOrWhiteSpace(tech.Icon)
                    || string.Equals(tech.Icon, tech.Key, StringComparison.OrdinalIgnoreCase))
                    return null;
                return new AstNode { Type = NodeType.Simple, Key = "icon", Value = tech.Icon };
            case TechField.Weight:
                if (string.IsNullOrWhiteSpace(tech.Weight)) return null;
                return ParseFieldValue(tech.Weight, "weight", out valid);
            case TechField.StartTech:
                return tech.StartTech
                    ? new AstNode { Type = NodeType.Simple, Key = "start_tech", Value = "yes" }
                    : null;   // 未勾选 → 移除 start_tech
            case TechField.Potential:
                if (string.IsNullOrWhiteSpace(tech.PotentialRaw)) return null;
                return ParseFieldBlock(tech.PotentialRaw, "potential", out valid);
            case TechField.Modifier:
                if (tech.ModifierEntries.Count == 0) return null;
                return BuildModifierNode(tech);
            case TechField.WeightModifier:
                if (string.IsNullOrWhiteSpace(tech.WeightModifierRaw)) return null;
                return ParseFieldBlock(tech.WeightModifierRaw, "weight_modifier", out valid);
            case TechField.AiWeight:
                if (string.IsNullOrWhiteSpace(tech.AiWeightRaw)) return null;
                return ParseFieldBlock(tech.AiWeightRaw, "ai_weight", out valid);
            case TechField.PrereqForDesc:
                if (string.IsNullOrWhiteSpace(tech.PrereqForDesc)) return null;
                return ParseFieldBlock(tech.PrereqForDesc, "prereqfor_desc", out valid);
            default:
                return null;
        }
    }

    /// <summary>List 节点（category/prerequisites）：children = Simple（Key 空、Value = 元素值——群星格式）。</summary>
    private static AstNode BuildListNode(string key, IReadOnlyList<string> values)
    {
        var list = new AstNode { Type = NodeType.List, Key = key, Children = new List<AstNode>() };
        foreach (var v in values)
            list.Children.Add(new AstNode { Type = NodeType.Simple, Value = v });
        return list;
    }

    /// <summary>modifier 块：ModifierEntries（key = 数值）→ modifier = { key = value ... }。</summary>
    private static AstNode BuildModifierNode(TechNode tech)
    {
        var block = new AstNode { Type = NodeType.Block, Key = "modifier", Children = new List<AstNode>() };
        foreach (var (k, v) in tech.ModifierEntries)
            block.Children.Add(new AstNode { Type = NodeType.Simple, Key = k, Value = v });
        return block;
    }

    /// <summary>块文本解析：fieldKey = { 内容 } → 单节点（Key == fieldKey）。用公开解析类（法令同款），不碰磁盘。</summary>
    private AstNode? ParseFieldBlock(string inner, string fieldKey, out bool valid)
    {
        valid = true;
        try
        {
            var wrapped = fieldKey + " = {\n" + (inner ?? "").Trim() + "\n}";
            var node = ParseWrapped(wrapped, fieldKey);
            valid = node != null;
            return node;
        }
        catch
        {
            valid = false;
            return null;
        }
    }

    /// <summary>值解析：fieldKey = 值（weight 等 Simple 值）。</summary>
    private AstNode? ParseFieldValue(string value, string fieldKey, out bool valid)
    {
        valid = true;
        try
        {
            var wrapped = fieldKey + " = " + (value ?? "").Trim();
            var node = ParseWrapped(wrapped, fieldKey);
            valid = node != null;
            return node;
        }
        catch
        {
            valid = false;
            return null;
        }
    }

    /// <summary>文本 → 单节点（统一经 SA.ParseSingleNode——SA 基础服务，禁止自行 new Lexer/Parser，2026-08）。</summary>
    private AstNode? ParseWrapped(string wrapped, string fieldKey)
    {
        var node = _adapter.ParseSingleNode(wrapped);
        if (node == null || !string.Equals(node.Key, fieldKey, StringComparison.Ordinal))
            return null;
        return node;
    }

    /// <summary>块 → 文本（弹窗编辑字段用：统一经 SA.SerializeNodes——**完整递归序列化**，嵌套块/注释/格式全部保留。
    /// 原实现嵌套块只输出 `key = { }` 简写会丢内容（用户 2026-08：前置说明丢东西）。</summary>
    private string BlockToText(AstNode block)
    {
        if (block?.Children == null || block.Children.Count == 0)
            return "";
        return _adapter.SerializeNodes(block.Children).Trim();
    }

    /// <summary>解析数值：@ 常量经 SA 解析（ResolveConstantInput）；纯数字直接转换；失败返回 fallback。</summary>
    private int ResolveInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return -1;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("@", StringComparison.Ordinal))
        {
            var resolved = _adapter.ResolveConstantInput(trimmed);
            if (resolved != null && double.TryParse(resolved.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return (int)Math.Round(d);
            return -1;
        }
        return int.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var i) ? i : -1;
    }

    private static bool IsYes(string? v) =>
        string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);

    // ==================== 索引查询 ====================

    /// <summary>全部科技（按 key 序）。</summary>
    public IReadOnlyList<TechNode> GetAll() => _byKey.Values.OrderBy(t => t.Key, StringComparer.Ordinal).ToList();

    /// <summary>按 key 查询。</summary>
    public TechNode? Get(string key)
        => _byKey.TryGetValue(key, out var t) ? t : null;

    /// <summary>按大类（physics/society/engineering）查询。</summary>
    public IReadOnlyList<TechNode> GetByArea(string area)
        => _byArea.TryGetValue(area, out var l) ? l : (IReadOnlyList<TechNode>)Array.Empty<TechNode>();

    /// <summary>按学科（category）查询。</summary>
    public IReadOnlyList<TechNode> GetByCategory(string category)
        => _byCategory.TryGetValue(category, out var l) ? l : (IReadOnlyList<TechNode>)Array.Empty<TechNode>();

    /// <summary>按 tier 查询。</summary>
    public IReadOnlyList<TechNode> GetByTier(int tier)
        => _byTier.TryGetValue(tier, out var l) ? l : (IReadOnlyList<TechNode>)Array.Empty<TechNode>();

    /// <summary>科技的前置列表（prerequisites）。</summary>
    public IReadOnlyList<string> GetPrerequisites(string key)
        => _byKey.TryGetValue(key, out var t) ? t.Prerequisites : (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>后继科技（前置指向本科技的所有科技）。</summary>
    public IReadOnlyList<TechNode> GetChildren(string key)
        => _children.TryGetValue(key, out var l) ? l : (IReadOnlyList<TechNode>)Array.Empty<TechNode>();

    /// <summary>学科图标相对路径（common/technology/category 的 icon 字段，完整相对路径）；无返回 null。</summary>
    public string? GetCategoryIcon(string category)
        => _categoryIcons.TryGetValue(category, out var p) ? p : null;

    /// <summary>科技图标相对路径：有 icon 字段 → {TechIconDir}{Icon}.dds；否则 {TechIconDir}{Key}.dds。</summary>
    public string GetTechIconPath(TechNode tech)
    {
        var name = tech.Icon;
        if (string.IsNullOrEmpty(name))
            name = tech.Key;
        if (name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            return name;
        return TechIconDir + name + ".dds";
    }

    /// <summary>科技图标路径是否已收录（存在性——供页面决定是否加载图标）。</summary>
    public bool TechIconExists(TechNode tech) => _adapter.FileExists(GetTechIconPath(tech));

    /// <summary>科技名本地化（缺省 english；无词条回退 key）。</summary>
    public string LocalisedName(string key, string lang = "english")
    {
        var text = _adapter.GetLocalisedText(key, lang);
        return string.IsNullOrEmpty(text) ? key : text;
    }

    /// <summary>科技描述本地化（{key}_desc；无词条返回空串）。</summary>
    public string LocalisedDesc(string key, string lang = "english")
        => _adapter.GetLocalisedText(key + "_desc", lang) ?? "";

    /// <summary>modifier 显示行：复用 StaticModifierEngine 查本地化（有翻译用翻译，没翻译用原键——用户规则）。
    /// 返回 (原键, 显示文本, 数值) 列表，供卡片加成列表区显示。
    /// **custom_tooltip 等特殊项不显示**（用户规则：只显示数值加成项）。
    /// 解析结果按 key 缓存（语言无关），Display 每次查本地化——避免 FindTechFile 全扫重复。</summary>
    public IReadOnlyList<(string Key, string Display, string Value)> GetModifierLines(TechNode tech, string lang = "english")
    {
        var result = new List<(string, string, string)>();
        // **内存优先**：弹窗新建/修改后的 ModifierEntries 是最新；新科技无源文件（文件解析会空路径崩溃——用户：新建报错根因）
        List<(string Key, string Value)> entries;
        if (tech.ModifierEntries.Count > 0)
            entries = tech.ModifierEntries;
        else if (_modifierCache.TryGetValue(tech.Key, out var cached))
            entries = cached;
        else
        {
            entries = ParseModifierEntries(tech.Key);
            _modifierCache[tech.Key] = entries;
        }
        foreach (var (key, value) in entries)
        {
            string display = key;
            try
            {
                // StaticModifierEngine 基础名 = 去 mod_ 前缀（大小写不敏感）——科技 modifier 键直接查
                var baseMod = _modifiers.GetBaseModifier(key);
                if (baseMod != null && baseMod.Localisations.TryGetValue(lang, out var loc))
                    display = loc;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "modifier 本地化查询失败: {Key}", key);
            }
            result.Add((key, display, value));
        }
        return result;
    }

    /// <summary>解析科技 modifier 块条目（Key + 数值原文）；custom_tooltip 等特殊项排除。按 key 缓存（语言无关）。</summary>
    private List<(string Key, string Value)> ParseModifierEntries(string techKey)
    {
        var entries = new List<(string, string)>();
        var relPath = FindTechFileCached(techKey);
        if (string.IsNullOrEmpty(relPath))
            return entries;   // 新建/未知科技无源文件——防御（用户：新建报错根因）
        var result = _adapter.GetConfig(relPath);
        if (result == null)
            return entries;
        var block = result.RootNodes.FirstOrDefault(n => n.Type == NodeType.Block
            && string.Equals(n.Key, techKey, StringComparison.Ordinal));
        if (block == null)
            return entries;
        var modifier = block.Children.FirstOrDefault(c => c.Type == NodeType.Block
            && string.Equals(c.Key, "modifier", StringComparison.OrdinalIgnoreCase));
        if (modifier == null)
            return entries;
        foreach (var m in modifier.Children)
        {
            if (m.Type != NodeType.Simple || string.IsNullOrEmpty(m.Key))
                continue;
            if (string.Equals(m.Key, "custom_tooltip", StringComparison.OrdinalIgnoreCase))
                continue;   // 特殊项：不显示
            string? raw = m.Value?.ToString();
            // **只有对应数值的才是加成**（用户规则）：纯数字 或 @ 常量（解析为数值）；
            // yes/no、字符串、块等不是加成 → 排除
            if (!IsNumericModifierValue(raw))
                continue;
            entries.Add((m.Key, raw ?? ""));
        }
        return entries;
    }

    /// <summary>是否为数值型加成值（用户规则：只有数值才是加成）——纯数字 或 @ 常量（解析为数值）。</summary>
    private bool IsNumericModifierValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var t = raw.Trim();
        if (t.StartsWith("@", StringComparison.Ordinal))
        {
            var resolved = _adapter.ResolveConstantInput(t);
            return resolved != null
                && double.TryParse(resolved.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        }
        return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }

    private string? FindTechFileCached(string key)
    {
        if (_techFileCache.TryGetValue(key, out var cached))
            return cached;
        var path = FindTechFile(key);
        _techFileCache[key] = path;
        return path;
    }

    /// <summary>建立全局"解锁"索引：扫描**所有已加载文件的根 block**（含科技以外，**排除舰船文件夹**——由 ShipEngine 处理），
    /// prerequisites 列表含某 key 的 → 该 block key 记入反查（供加成区"解锁"行显示）。
    /// 只扫根 block（用户规则）；懒构建一次，之后 O(1) 查询。</summary>
    private static readonly string[] ShipDirs =
        { "component_sets", "component_templates", "section_templates", "ship_sizes", "global_ship_designs" };

    private void EnsureUnlockIndex()
    {
        if (_unlockIndex != null)
            return;
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relPath, result) in _adapter.GetAllConfigs())
        {
            if (result.RootNodes == null)
                continue;
            if (relPath.StartsWith("common/", StringComparison.OrdinalIgnoreCase)
                && ShipDirs.Any(d => relPath.StartsWith("common/" + d + "/", StringComparison.OrdinalIgnoreCase)))
                continue;   // 舰船文件夹 → ShipEngine 专属索引
            foreach (var node in result.RootNodes)
            {
                if (node.Type != NodeType.Block || string.IsNullOrEmpty(node.Key))
                    continue;
                var pre = node.Children.FirstOrDefault(c =>
                    (c.Type == NodeType.Block || c.Type == NodeType.List)
                    && string.Equals(c.Key, "prerequisites", StringComparison.OrdinalIgnoreCase));
                if (pre == null)
                    continue;
                foreach (var p in pre.Children)
                {
                    var pk = p.Value?.ToString() ?? p.Key;
                    if (string.IsNullOrEmpty(pk))
                        continue;
                    if (!index.TryGetValue(pk, out var list))
                        index[pk] = list = new List<string>();
                    if (!list.Contains(node.Key))
                        list.Add(node.Key);
                }
            }
        }
        _unlockIndex = index;
    }

    /// <summary>解锁当前科技的 block key 列表（全局：科技 + 科技以外的根 block，其 prerequisites 含本科技）。</summary>
    public IReadOnlyList<string> GetUnlockingBlocks(string techKey)
    {
        EnsureUnlockIndex();
        return _unlockIndex.TryGetValue(techKey, out var list)
            ? list
            : (IReadOnlyList<string>)Array.Empty<string>();
    }

    private string? FindTechFile(string key)
    {
        foreach (var relPath in _adapter.GetFilesInDirectory("common/technology", "*.txt"))
        {
            var result = _adapter.GetConfig(relPath);
            if (result != null && result.RootNodes.Any(n => n.Type == NodeType.Block
                && string.Equals(n.Key, key, StringComparison.Ordinal)))
                return relPath;
        }
        return null;
    }
}
