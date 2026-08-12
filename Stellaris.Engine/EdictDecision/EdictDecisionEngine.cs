using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Stellaris.Parser;
using Stellaris.Engine.StrategicResource;

namespace Stellaris.Engine.EdictDecision;

/// <summary>条目类型：法令 / 星球决议。</summary>
public enum EdictDecisionKind { Edict, Decision }

/// <summary>法令可保存字段（字段级脏追踪：改动登记、保存只写改动字段、没改的保留原节点含注释）。</summary>
public enum EdictField
{
    Key, Icon, Length, Resources, Potential, Allow, Bonuses, Effect, AiWeight,
    Important, OwnedPlanetsOnly, EnactmentTime   // 决议专用：important / owned_planets_only / enactment_time（延迟时间）
}

/// <summary>条件预设：无条件 / 无条件禁用 / 仅电脑 / 仅玩家 / 自定义文本。</summary>
public enum ConditionPreset { AlwaysYes, AlwaysNo, AiYes, AiNo, Custom }

/// <summary>法令/决议条目（内存模型——本期不落盘）。</summary>
public sealed class EdictDecisionItem
{
    public EdictDecisionKind Kind { get; set; }
    public string Key { get; set; } = "";
    /// <summary>本地化名逻辑值（原文，含 $var$——只读显示）；显示名 = 当前语种翻译（可编辑）。</summary>
    public string NameLogical { get; set; } = "";
    public string NameDisplay { get; set; } = "";
    /// <summary>描述本地化逻辑值（法令 edict_{key}_desc；决议 {key}_desc）。</summary>
    public string DescLogical { get; set; } = "";
    public string DescDisplay { get; set; } = "";
    public string Icon { get; set; } = "";
    /// <summary>持续时间：无限 → LengthValue 忽略（显示 -1）；有限 → 数值（年/月，按游戏语义）。</summary>
    public bool LengthIsInfinite { get; set; } = true;
    public int LengthValue { get; set; }
    /// <summary>决议：important（重要的）——勾选 = yes。</summary>
    public bool Important { get; set; }
    /// <summary>决议：owned_planets_only（仅限被拥有的星球——殖民后才可开启）。</summary>
    public bool OwnedPlanetsOnly { get; set; }
    /// <summary>决议：enactment_time（延迟时间，天数）；0 = 不写。</summary>
    public int EnactmentTime { get; set; }
    /// <summary>启动消耗（resources.cost）：多个组（各自资源 + 倍率 + 条件）。</summary>
    public StrategicResourceEngine.ResourceBucket Cost { get; } = new();
    /// <summary>每月消耗（resources.upkeep）。</summary>
    public StrategicResourceEngine.ResourceBucket Upkeep { get; } = new();
    /// <summary>每月产出（resources.produces——游戏语法）。</summary>
    public StrategicResourceEngine.ResourceBucket Produces { get; } = new();
    /// <summary>可见性限制 potential（没有 = 无限制允许）；Custom 时用 PotentialCustom 原文。</summary>
    public ConditionPreset Potential { get; set; } = ConditionPreset.AlwaysYes;
    public string PotentialCustom { get; set; } = "";
    /// <summary>可点击限制 allow（没有 = 无限制允许）；Custom 时用 AllowCustom 原文。</summary>
    public ConditionPreset Allow { get; set; } = ConditionPreset.AlwaysYes;
    public string AllowCustom { get; set; } = "";
    /// <summary>AI 触发权重（ai_weight.weight，默认 0 = 不用）。</summary>
    public double AiWeight { get; set; }
    /// <summary>AI 触发权重块原始内容（ai_weight = { ... } 序列化文本——"有什么显示什么"；可编辑，保存写回）。</summary>
    public string AiWeightRaw { get; set; } = "";
    /// <summary>效果块原始内容（effect = { ... } 序列化文本——写事件/命令；可编辑）。</summary>
    public string EffectRaw { get; set; } = "";
    /// <summary>效果：基础名 + 数值（来自 modifier 块子键；排除规则与加成字典一致）。</summary>
    public List<(string Base, double Value)> Effects { get; } = new();
    /// <summary>所属文件相对路径（扫描项 = 来源；新建项可右键设置——保存目标）。</summary>
    public string? SourceRelPath { get; set; }

    /// <summary>初始/上次已保存的 key（Key 字段保存后更新）——Key 改动时用于从目标文件移出旧块。</summary>
    public string? OriginalKey { get; set; }
}

/// <summary>
/// 法令/决议可视化引擎：扫描 common/edicts 与 common/decisions（经 SA，读现有条目），
/// 支持内存新建条目。**本期不落盘**——所有编辑仅停留内存，保存后续再加。
/// </summary>
public sealed class EdictDecisionEngine
{
    private readonly StellarisAdapter _adapter;
    private readonly ILogger _logger;
    private readonly Lazy<IReadOnlyList<EdictDecisionItem>> _scanned;
    private readonly IReadOnlyList<string>? _enabledLanguages;
    /// <summary>内存新建/编辑的条目（不写盘）。</summary>
    private readonly List<EdictDecisionItem> _newItems = new();

    /// <summary>字段级脏登记（保存索引）：项 → 改过的字段集——保存只写这些；保存后 ClearDirty。</summary>
    private readonly Dictionary<EdictDecisionItem, HashSet<EdictField>> _dirty = new();

    /// <summary>删除登记（保存索引）：保存时把这些项从目标文件移出 + 删配套本地化词条；保存后清空。</summary>
    private readonly List<EdictDecisionItem> _removed = new();

    /// <summary>登记某字段被修改（输入框变动时调用）。</summary>
    public void MarkDirty(EdictDecisionItem item, EdictField field)
    {
        if (item == null)
            return;
        if (!_dirty.TryGetValue(item, out var set))
            _dirty[item] = set = new HashSet<EdictField>();
        set.Add(field);
    }

    /// <summary>登记条目有改动（空字段集——保存时只写文件不写字段；供"所属文件"改动等非字段变化登记，用户 2026-08）。</summary>
    public void MarkItemDirty(EdictDecisionItem item)
    {
        if (item == null)
            return;
        if (!_dirty.TryGetValue(item, out _))
            _dirty[item] = new HashSet<EdictField>();
    }

    /// <summary>保存后清除全部脏登记（下次改动重新登记）；所有项 OriginalKey 更新为当前 Key。</summary>
    public void ClearDirty()
    {
        _dirty.Clear();
        _removed.Clear();
        foreach (var item in _scanned.Value.Concat(_newItems))
            item.OriginalKey = item.Key;
    }

    /// <summary>是否有待保存改动（保存按钮无改动时提示）。</summary>
    public bool HasDirty => _dirty.Count > 0 || _removed.Count > 0;

    public EdictDecisionEngine(StellarisAdapter adapter, ILogger logger, IReadOnlyList<string>? enabledLanguages = null)
    {
        _adapter = adapter;
        _logger = logger;
        _enabledLanguages = enabledLanguages;
        _scanned = new Lazy<IReadOnlyList<EdictDecisionItem>>(ScanAll);
    }

    /// <summary>全部条目 = 扫描现有 + 内存新建（按类型过滤；扫描结果缓存；**删除登记项过滤**——用户 2026-08）。</summary>
    public IReadOnlyList<EdictDecisionItem> GetItems(EdictDecisionKind kind)
        => _scanned.Value.Where(i => i.Kind == kind && !_removed.Contains(i))
            .Concat(_newItems.Where(i => i.Kind == kind)).ToList();

    /// <summary>新建条目（内存——不落盘）。</summary>
    public EdictDecisionItem AddItem(EdictDecisionKind kind, string key)
    {
        var item = new EdictDecisionItem { Kind = kind, Key = key, OriginalKey = key };
        // 决议持续时间只能是有限（最少 0，无无限——-1）——默认有限 0
        if (kind == EdictDecisionKind.Decision)
            item.LengthIsInfinite = false;
        _newItems.Add(item);
        return item;
    }

    /// <summary>删除条目：从新建列表移除 + 撤脏登记（防保存复活）+ 登记删除（保存时移出块、删本地化词条）。</summary>
    public void RemoveItem(EdictDecisionItem item)
    {
        _newItems.Remove(item);
        _dirty.Remove(item);
        _removed.Add(item);
    }

    // ==================== 保存（字段级脏追踪——改哪写哪，没改的保留原节点含注释） ====================

    /// <summary>目标文件：SourceRelPath（"设置"指定）?? 默认（common/edicts/00_{prefix}_edicts.txt / decisions）。</summary>
    public string TargetRelPath(EdictDecisionItem item, string modPrefix)
    {
        if (!string.IsNullOrEmpty(item.SourceRelPath))
            return item.SourceRelPath!;
        var dir = item.Kind == EdictDecisionKind.Edict ? "common/edicts" : "common/decisions";
        var file = item.Kind == EdictDecisionKind.Edict ? "edicts" : "decisions";
        return $"{dir}/00_{modPrefix}_{file}.txt";
    }

    /// <summary>统一保存（规范）：只写**改动登记**的项与字段——没改的不动（保留注释/格式）。
    /// 数据源 = SA GetConfig 合并 AST（不重建文件）；写 = SA WriteFile（Roots 最后一位 + 自动建目录）。
    /// Key 字段改动 = 文件级：旧 key 块移出、新 key 块移入。返回 (成功文件数, 错误列表)。</summary>
    public (int Saved, List<string> Errors) SaveAll(string modPrefix)
    {
        var errors = new List<string>();
        if (_dirty.Count == 0 && _removed.Count == 0)
            return (0, errors);
        int saved = 0;
        // 删除项：块从目标文件移出（GetConfig 合并 AST → 移除 → WriteFile 写 roots[-1]）
        var removedByFile = new Dictionary<string, List<EdictDecisionItem>>(StringComparer.Ordinal);
        foreach (var item in _removed)
        {
            var rel = TargetRelPath(item, modPrefix);
            if (!removedByFile.TryGetValue(rel, out var list))
                removedByFile[rel] = list = new List<EdictDecisionItem>();
            list.Add(item);
        }
        foreach (var (rel, items) in removedByFile)
        {
            var result = _adapter.GetConfig(rel);
            if (result == null)
                continue;   // 文件不存在——无需移出
            bool changed = false;
            foreach (var item in items)
            {
                var block = result.RootNodes.FirstOrDefault(n => string.Equals(n.Key, item.Key, StringComparison.Ordinal)
                    || (!string.IsNullOrEmpty(item.OriginalKey) && string.Equals(n.Key, item.OriginalKey, StringComparison.Ordinal)));
                if (block != null)
                {
                    result.RootNodes.Remove(block);
                    changed = true;
                }
            }
            // 格式化省略（用户 2026-08，与科技 NormalizeTechBlock 同思想）：只删符合规范可省略的空块，
            // 未知/新字段一律保留——不能保证以后有没有新关键词
            if (changed)
                foreach (var b in result.RootNodes.Where(n => n.Type == NodeType.Block))
                    NormalizeEdictBlock(b);
            if (changed && _adapter.WriteFile(rel))
                saved++;
        }
        // 按目标文件分组——每文件一个 AST，一次写
        var byFile = new Dictionary<string, List<EdictDecisionItem>>(StringComparer.Ordinal);
        foreach (var item in _dirty.Keys)
        {
            var rel = TargetRelPath(item, modPrefix);
            if (!byFile.TryGetValue(rel, out var list))
                byFile[rel] = list = new List<EdictDecisionItem>();
            list.Add(item);
        }
        foreach (var (rel, items) in byFile)
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
            bool fileOk = true;
            foreach (var item in items)
            {
                var fields = _dirty[item];
                if (!ApplyItemToBlock(result, item, fields, modPrefix, errors))
                    fileOk = false;
            }
            if (!fileOk)
                continue;
            // 格式化省略（用户 2026-08）：文件所有法令/决议块应用（"就当格式化"——未修改的块同样清理）
            foreach (var b in result.RootNodes.Where(n => n.Type == NodeType.Block))
                NormalizeEdictBlock(b);
            if (_adapter.WriteFile(rel))
                saved++;
            else
                errors.Add(rel + ": 写入失败");
        }
        // 本地化一并写（用户确认）：每个 dirty 项创建/更新名称与描述词条（自动创建，没填名称用 key）
        if (errors.Count == 0)
        {
            var files = new HashSet<string>(StringComparer.Ordinal);      // "lang\0相对路径"——待写文件（含键所在源文件，不做全局保存）
            var cleanFiles = new HashSet<string>(StringComparer.Ordinal); // 删除涉及的文件（删空写空头 writeIfEmpty:true）
            foreach (var item in _dirty.Keys)
            {
                foreach (var lang in EnabledLanguages())
                {
                    // 法令/决议本地化**分开**：edicts_ 文件 / decisions_ 文件（用户要求不混存）
                    var filePrefix = item.Kind == EdictDecisionKind.Edict ? "edicts_" : "decisions_";
                    var fileName = filePrefix + modPrefix + "_l_" + lang + ".yml";
                    WriteItemLocalisation(item, lang, fileName, errors, files, cleanFiles);
                }
            }
            // 删除项：按键当前所在文件移除配套本地化词条（名称 + 描述；曾用 key 残留一并清）
            foreach (var item in _removed)
            {
                foreach (var lang in EnabledLanguages())
                {
                    var index = _adapter.GetLocalisationKeyFiles(lang);
                    foreach (var k in LocalisationKeysOf(item))
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
        return (saved, errors);
    }

    /// <summary>项对应的本地化键（名称 + 描述；含曾用 key——改 Key 保存后旧词条残留一并清）。</summary>
    private IEnumerable<string> LocalisationKeysOf(EdictDecisionItem item)
    {
        var keys = new List<string>();
        foreach (var k in new[] { item.Key, item.OriginalKey }.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal))
        {
            var nameKey = item.Kind == EdictDecisionKind.Edict ? "edict_" + k : k;
            keys.Add(nameKey);
            keys.Add(nameKey + "_desc");
        }
        return keys;
    }

    /// <summary>写单项本地化词条（名称 + 描述）：键已存在 → 更新其**所在源文件**（源文件受影响）；
    /// 键不存在 → 写入目标文件 localisation/{lang}/{fileName}（edicts_ 规范文件）。
    /// 涉及的文件登记到 files（"lang\0相对路径"），由调用方统一 WriteLocalisation 落盘（不做全局保存）。</summary>
    private void WriteItemLocalisation(EdictDecisionItem item, string lang, string fileName, List<string> errors,
        HashSet<string> files, HashSet<string> cleanFiles)
    {
        var nameKey = item.Kind == EdictDecisionKind.Edict ? "edict_" + item.Key : item.Key;
        var descKey = nameKey + "_desc";
        var targetPath = $"localisation/{lang}/{fileName}";
        try
        {
            var index = _adapter.GetLocalisationKeyFiles(lang);   // 键 → 当前所在文件（O(1)）
            // 键已存在且不在目标文件 → **旧位置登记待保存**（移动后写剩余/空头清理，防磁盘残留重复）；
            // 新位置写入目标文件（edicts_ 规范文件）→ 新位置登记待保存
            var nameValue = string.IsNullOrWhiteSpace(item.NameLogical) ? item.Key : item.NameLogical;
            if (index.TryGetValue(nameKey, out var nf) && !string.Equals(nf, targetPath, StringComparison.OrdinalIgnoreCase))
                cleanFiles.Add(lang + "\u0000" + nf);
            _adapter.UpdateLocalisationEntry(lang, targetPath, nameKey, nameValue);
            files.Add(lang + "\u0000" + targetPath);
            if (!string.IsNullOrWhiteSpace(item.DescLogical))
            {
                if (index.TryGetValue(descKey, out var df) && !string.Equals(df, targetPath, StringComparison.OrdinalIgnoreCase))
                    cleanFiles.Add(lang + "\u0000" + df);
                _adapter.UpdateLocalisationEntry(lang, targetPath, descKey, item.DescLogical);
                files.Add(lang + "\u0000" + targetPath);
            }
        }
        catch (Exception ex)
        {
            errors.Add(item.Key + ": 本地化写入失败（" + lang + "） " + ex.Message);
        }
    }

    /// <summary>保存本地化的语种：模组启用语种（优先）?? 已加载语种 ?? 界面映射默认。</summary>
    private IReadOnlyList<string> EnabledLanguages()
    {
        if (_enabledLanguages != null && _enabledLanguages.Count > 0)
            return _enabledLanguages;
        var loaded = _adapter.GetLocalisationLanguages();
        if (loaded.Count > 0)
            return loaded;
        return new List<string> { "english", "simp_chinese" };
    }

    /// <summary>把项的所有脏字段应用到目标文件 AST 的法令块（Key 改动 = 旧块移出/新块建入；其余字段定位替换）。</summary>
    private bool ApplyItemToBlock(ParserResult result, EdictDecisionItem item, HashSet<EdictField> fields,
        string modPrefix, List<string> errors)
    {
        // Key 字段：文件级——旧块**改名**为新 key（保留块内容/注释），不是删+建空块
        var block = result.RootNodes.FirstOrDefault(n => string.Equals(n.Key, item.Key, StringComparison.Ordinal)
            && (n.Type == NodeType.Block || n.Type == NodeType.List));
        if (fields.Contains(EdictField.Key)
            && !string.IsNullOrEmpty(item.OriginalKey)
            && !string.Equals(item.OriginalKey, item.Key, StringComparison.Ordinal))
        {
            var old = result.RootNodes.FirstOrDefault(n => string.Equals(n.Key, item.OriginalKey, StringComparison.Ordinal));
            if (old != null)
            {
                old.Key = item.Key;   // 改名——内容/注释原样保留
                block = old;
            }
        }
        if (block == null)
        {
            block = new AstNode { Type = NodeType.Block, Key = item.Key, Children = new List<AstNode>() };
            result.RootNodes.Add(block);
            // 新建块：**全字段写**（不能只写登记字段——否则新建法令缺字段，相关信息都要保存）
            fields = new HashSet<EdictField>((EdictField[])Enum.GetValues(typeof(EdictField)));
        }
        if (block.Children == null)
            block.Children = new List<AstNode>();
        bool ok = true;
        // 固定字段顺序写入（文件规范：... → modifier(Bonuses) → effect → ai_weight 最后）
        var orderedFields = new[]
        {
            EdictField.Icon, EdictField.Length, EdictField.Resources,
            EdictField.Potential, EdictField.Allow,
            EdictField.Bonuses, EdictField.Effect, EdictField.AiWeight,
            EdictField.Important, EdictField.OwnedPlanetsOnly, EdictField.EnactmentTime
        };
        foreach (var f in orderedFields)
        {
            if (!fields.Contains(f))
                continue;
            var node = BuildFieldNode(item, f, out bool valid);
            if (!valid)
            {
                errors.Add(item.Key + "." + f + ": 字段内容不合规（无法解析）");
                ok = false;
                continue;
            }
            var fieldKey = FieldKeyOf(f);
            if (node == null)
            {
                // 空内容 → 移除该字段（改过的字段以 UI 内容为准）
                block.Children.RemoveAll(c => string.Equals(c.Key, fieldKey, StringComparison.Ordinal));
                continue;
            }
            var idx = block.Children.FindIndex(c => string.Equals(c.Key, fieldKey, StringComparison.Ordinal));
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

    /// <summary>法令/决议格式化省略规则（用户 2026-08，与科技 NormalizeTechBlock 同思想）：
    /// **只删符合规范可省略的空块字段**（potential/allow/effect/modifier 空 Children——空条件/空效果/空加成无意义）；
    /// 其它字段（icon/length/resources/ai_weight/important/owned_planets_only/enactment_time/
    /// hide_from_country_list 等**未知/新关键词**）一律保留——不能保证以后有没有新关键词，规整化绝不丢未知字段。
    /// 保存（含删除/修改）时对文件所有法令/决议块应用。</summary>
    private static void NormalizeEdictBlock(AstNode block)
    {
        if (block?.Children == null || block.Children.Count == 0)
            return;
        for (int i = block.Children.Count - 1; i >= 0; i--)
        {
            var c = block.Children[i];
            if (c == null)
                continue;
            if ((string.Equals(c.Key, "potential", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(c.Key, "allow", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(c.Key, "effect", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(c.Key, "modifier", StringComparison.OrdinalIgnoreCase))
                && (c.Children == null || c.Children.Count == 0))
                block.Children.RemoveAt(i);
        }
    }

    /// <summary>字段 → 法令块内子键。</summary>
    private static string FieldKeyOf(EdictField f) => f switch
    {
        EdictField.Icon => "icon",
        EdictField.Length => "length",
        EdictField.Resources => "resources",
        EdictField.Potential => "potential",
        EdictField.Allow => "allow",
        EdictField.AiWeight => "ai_weight",
        EdictField.Effect => "effect",
        EdictField.Bonuses => "modifier",
        EdictField.Important => "important",
        EdictField.OwnedPlanetsOnly => "owned_planets_only",
        EdictField.EnactmentTime => "enactment_time",
        _ => ""
    };

    /// <summary>按字段构建新节点（UI 内容 → AST）；valid=false = 内容不合规；null+valid = 空内容 → 移除该字段。</summary>
    private AstNode? BuildFieldNode(EdictDecisionItem item, EdictField field, out bool valid)
    {
        valid = true;
        switch (field)
        {
            case EdictField.Icon:
                if (string.IsNullOrWhiteSpace(item.Icon))
                    return null;   // 空 → 移除
                return new AstNode { Type = NodeType.Simple, Key = "icon", Value = item.Icon, IsQuoted = true };
            case EdictField.Length:
                return BuildLengthNode(item);
            case EdictField.Resources:
            {
                // resources **总是生成**（用户要求：无论是否选具体资源都要有基础 resource）——
                // category 必填（法令 edicts / 决议 decisions），即使 cost/upkeep/produces 全空
                var resources = StrategicResourceEngine.BuildResourcesBlock(item.Cost, item.Upkeep, item.Produces);
                var category = item.Kind == EdictDecisionKind.Edict ? "edicts" : "decisions";
                resources.Children.Insert(0, new AstNode { Type = NodeType.Simple, Key = "category", Value = category });
                return resources;
            }
            case EdictField.Potential:
                return BuildConditionNode(item.Potential, item.PotentialCustom, "potential", out valid);
            case EdictField.Allow:
                return BuildConditionNode(item.Allow, item.AllowCustom, "allow", out valid);
            case EdictField.AiWeight:
                // ai_weight **总是生成**（用户要求：默认 weight = 0——即使没填）
                return string.IsNullOrWhiteSpace(item.AiWeightRaw)
                    ? ParseTextBlock("weight = 0", "ai_weight", out valid)
                    : ParseTextBlock(item.AiWeightRaw, "ai_weight", out valid);
            case EdictField.Effect:
                if (string.IsNullOrWhiteSpace(item.EffectRaw))
                    return null;
                return ParseTextBlock(item.EffectRaw, "effect", out valid);
            case EdictField.Bonuses:
                if (item.Effects.Count == 0)
                    return null;   // 无加成 → 移除 modifier
                return BuildModifierNode(item);
            case EdictField.Important:
                // 决议专用：勾选 → important = yes；否则移除
                if (item.Kind != EdictDecisionKind.Decision || !item.Important)
                    return null;
                return new AstNode { Type = NodeType.Simple, Key = "important", Value = "yes" };
            case EdictField.OwnedPlanetsOnly:
                if (item.Kind != EdictDecisionKind.Decision || !item.OwnedPlanetsOnly)
                    return null;
                return new AstNode { Type = NodeType.Simple, Key = "owned_planets_only", Value = "yes" };
            case EdictField.EnactmentTime:
                // 决议专用：延迟时间（天）；0 不写
                if (item.Kind != EdictDecisionKind.Decision || item.EnactmentTime <= 0)
                    return null;
                return new AstNode
                {
                    Type = NodeType.Simple, Key = "enactment_time",
                    Value = item.EnactmentTime.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
            default:
                valid = false;
                return null;
        }
    }

    private static AstNode? BuildLengthNode(EdictDecisionItem item)
        => new AstNode { Type = NodeType.Simple, Key = "length",
            Value = item.LengthIsInfinite ? "-1" : item.LengthValue.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>条件字段：AlwaysYes + 空自定义 → 移除；Custom → 自定义文本；AiYes/AiNo → 标准单行。</summary>
    private AstNode? BuildConditionNode(ConditionPreset preset, string custom, string key, out bool valid)
    {
        valid = true;
        string inner;
        if (preset == ConditionPreset.AlwaysYes && string.IsNullOrWhiteSpace(custom))
            return null;   // 无限制 → 移除 potential/allow 块
        if (preset == ConditionPreset.Custom || preset == ConditionPreset.AlwaysYes)
            inner = custom;   // Custom = 用户文本；AlwaysYes 但保留文本（用户编辑过）→ 原样
        else if (preset == ConditionPreset.AiYes)
            // 仅限 AI：套 solar_system > owner 壳（行星/星系场景判定国家——直接 is_ai 判定不到）
            inner = "solar_system = {\n\towner = {\n\t\tis_ai = yes\n\t}\n}";
        else
            inner = "solar_system = {\n\towner = {\n\t\tis_ai = no\n\t}\n}";
        if (string.IsNullOrWhiteSpace(inner))
            return null;
        return ParseTextBlock(inner, key, out valid);
    }

    /// <summary>加成 modifier 块：Effects 列表 → modifier = { key = value ... }。</summary>
    private static AstNode BuildModifierNode(EdictDecisionItem item)
    {
        var block = new AstNode { Type = NodeType.Block, Key = "modifier", Children = new List<AstNode>() };
        foreach (var (b, v) in item.Effects)
            block.Children.Add(new AstNode { Type = NodeType.Simple, Key = b, Value = v });
        return block;
    }

    /// <summary>文本包装解析：`fieldKey = {\n 内容 \n}` → 单节点（Key == fieldKey）。</summary>
    private AstNode? ParseTextBlock(string inner, string fieldKey, out bool valid)
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

    /// <summary>值包装解析：`fieldKey = 值`（icon 等 Simple 值）。</summary>
    private AstNode? ParseTextValue(string value, string fieldKey, out bool valid)
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


    private IReadOnlyList<EdictDecisionItem> ScanAll()
    {
        var result = new List<EdictDecisionItem>();
        try
        {
            foreach (var (rel, ast) in _adapter.GetAllConfigs())
            {
                if (!rel.StartsWith("common/edicts", StringComparison.OrdinalIgnoreCase)
                    && !rel.StartsWith("common/decisions", StringComparison.OrdinalIgnoreCase))
                    continue;
                var kind = rel.StartsWith("common/edicts", StringComparison.OrdinalIgnoreCase)
                    ? EdictDecisionKind.Edict : EdictDecisionKind.Decision;
                foreach (var node in ast.RootNodes)
                {
                    // 顶层块 = 法令/决议条目；空块 `x = { }` 解析为 List——也收
                    if ((node.Type != Parser.NodeType.Block && node.Type != Parser.NodeType.List)
                        || string.IsNullOrEmpty(node.Key))
                        continue;
                    var item = ParseItem(kind, node, rel);
                    result.Add(item);
                }
            }
            _logger.LogInformation("法令/决议扫描完成：法令 {Edicts} 个、决议 {Decisions} 个",
                result.Count(i => i.Kind == EdictDecisionKind.Edict),
                result.Count(i => i.Kind == EdictDecisionKind.Decision));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "法令/决议扫描失败");
        }
        return result;
    }

    /// <summary>解析一个顶层块为条目（基础字段 + modifier 效果 + 条件预设粗判）。</summary>
    private EdictDecisionItem ParseItem(EdictDecisionKind kind, AstNode block, string rel)
    {
        var item = new EdictDecisionItem { Kind = kind, Key = block.Key, SourceRelPath = rel, OriginalKey = block.Key };
        if (block.Children == null)
            return item;
        foreach (var child in block.Children)
        {
            switch (child.Key)
            {
                case "icon":
                    item.Icon = child.Value?.ToString() ?? "";
                    break;
                case "length":
                    if (child.Value != null && double.TryParse(child.Value.ToString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var lv))
                    {
                        if (lv < 0)
                            item.LengthIsInfinite = true;   // -1 = 无限
                        else
                        {
                            item.LengthIsInfinite = false;
                            item.LengthValue = (int)lv;
                        }
                    }
                    break;
                case "important":
                    item.Important = string.Equals(child.Value?.ToString(), "yes", StringComparison.OrdinalIgnoreCase);
                    break;
                case "owned_planets_only":
                    item.OwnedPlanetsOnly = string.Equals(child.Value?.ToString(), "yes", StringComparison.OrdinalIgnoreCase);
                    break;
                case "enactment_time":
                    if (child.Value != null && int.TryParse(child.Value.ToString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var et))
                        item.EnactmentTime = et;
                    break;
                case "resources":
                {
                    var (cost, upkeep, produces) = StrategicResourceEngine.ParseResources(child);
                    item.Cost.Groups.AddRange(cost.Groups);
                    item.Upkeep.Groups.AddRange(upkeep.Groups);
                    item.Produces.Groups.AddRange(produces.Groups);
                    break;
                }
                case "potential":
                    ParseCondition(child,
                        p => item.Potential = p,
                        s => item.PotentialCustom = s);
                    break;
                case "allow":
                    ParseCondition(child,
                        p => item.Allow = p,
                        s => item.AllowCustom = s);
                    break;
                case "ai_weight":
                    if (child.Children != null)
                    {
                        // "有什么显示什么"——去掉外层 ai_weight = { } 外壳，只存块内内容（weight/factor/modifier 等）
                        item.AiWeightRaw = _adapter.SerializeNodes(child.Children);
                        foreach (var w in child.Children)
                        {
                            if (w.Key == "weight" && w.Value != null
                                && double.TryParse(w.Value.ToString(), System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var aw))
                                item.AiWeight = aw;
                        }
                    }
                    break;
                case "effect":
                    // 只显示 effect 块内内容（去掉 effect = { } 外壳——同 ai_weight）
                    item.EffectRaw = _adapter.SerializeNodes(child.Children);
                    break;
                case "modifier":
                    ParseEffects(child, item.Effects);
                    break;
            }
        }
        return item;
    }

    /// <summary>modifier 块子键 → 效果（排除键：add/mult/trigger_scope/factor 等语法成分不当作效果）。</summary>
    private static void ParseEffects(AstNode modifier, List<(string, double)> effects)
    {
        if (modifier.Children == null)
            return;
        foreach (var child in modifier.Children)
        {
            var key = child.Key;
            if (key is "add" or "mult" or "trigger_scope" or "factor" or "base" or "set" or "mode")
                continue;
            if (key.Contains('$'))
                continue;
            double val = 1;
            if (child.Value != null)
                double.TryParse(child.Value.ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out val);
            effects.Add((key, val));
        }
    }

    /// <summary>解析条件块（potential/allow）：空块 → AlwaysYes；有内容 → 内容文本，
    /// 粗判 AI（含 is_ai = yes/no）→ AiYes/AiNo，否则 Custom（用户可手动改预设）。</summary>
    /// <summary>条件文本 → 预设判定（与 ParseCondition 一致）：**精确匹配**——
    /// 空 → AlwaysYes；整段文本就是 "is_ai = yes" → AiYes；就是 "is_ai = no" → AiNo；
    /// 否则 Custom（哪怕含 is_ai 但还有别的条件也算自定义——预设只代表"纯这一条"）。</summary>
    public static ConditionPreset ClassifyCondition(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
            return ConditionPreset.AlwaysYes;
        var compact = CompactCondition(t);
        // 识别三种格式（星球不是国家——必须经 owner/solar_system 定位国家；裸 is_ai 仅兼容旧数据）：
        // 1) 生成格式 solar_system > owner > is_ai；2) 可行格式 owner > is_ai；3) 旧格式裸 is_ai
        if (string.Equals(compact, "is_ai=yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, "solar_system={owner={is_ai=yes}}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, "owner={is_ai=yes}", StringComparison.OrdinalIgnoreCase))
            return ConditionPreset.AiYes;
        if (string.Equals(compact, "is_ai=no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, "solar_system={owner={is_ai=no}}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, "owner={is_ai=no}", StringComparison.OrdinalIgnoreCase))
            return ConditionPreset.AiNo;
        return ConditionPreset.Custom;
    }

    /// <summary>去全部空白（空格/tab/换行）——用于条件预设精确比较（格式缩进无关）。</summary>
        private static string CompactCondition(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private void ParseCondition(AstNode node, Action<ConditionPreset> setPreset, Action<string> setCustom)
    {
        if (node.Children == null || node.Children.Count == 0)
        {
            setPreset(ConditionPreset.AlwaysYes);
            setCustom("");
            return;
        }
        string text = _adapter.SerializeNodes(node.Children).Trim();
        setCustom(text);
        // 精确匹配（与 ClassifyCondition 一致）：Compact 后识别旧格式/套壳格式；
        // 含额外条件的 potential/allow 不算纯预设 → 自定义
        var compact = CompactCondition(text);
        if (string.Equals(compact, "is_ai=yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, "solar_system={owner={is_ai=yes}}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, "owner={is_ai=yes}", StringComparison.OrdinalIgnoreCase))
            setPreset(ConditionPreset.AiYes);
        else if (string.Equals(compact, "is_ai=no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, "solar_system={owner={is_ai=no}}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, "owner={is_ai=no}", StringComparison.OrdinalIgnoreCase))
            setPreset(ConditionPreset.AiNo);
        else
            setPreset(ConditionPreset.Custom);
    }

    /// <summary>本地化键：法令 edict_{key}；决议 {key}（无前缀）。</summary>
    public static string LocalisationKey(EdictDecisionItem item)
        => item.Kind == EdictDecisionKind.Edict ? "edict_" + item.Key : item.Key;

    /// <summary>描述本地化键：法令 edict_{key}_desc；决议 {key}_desc。</summary>
    public static string DescKey(EdictDecisionItem item)
        => LocalisationKey(item) + "_desc";

    /// <summary>读取名字/描述本地化（逻辑值 + 翻译值——当前界面语言 → english → 回退 key/空），写入条目内存。返回显示名。</summary>
    public string LocalisedName(EdictDecisionItem item, string uiLang, string modLang, string english = "english")
    {
        var nameKey = LocalisationKey(item);
        var loc = _adapter.GetLocalisedText(nameKey, modLang)
                  ?? _adapter.GetLocalisedText(nameKey, english)
                  ?? item.Key;
        var nameLogical = _adapter.GetLocalisedLogicalText(nameKey, modLang) ?? "";
        item.NameDisplay = loc;
        item.NameLogical = nameLogical.Length > 0 ? nameLogical : loc;
        var descKey = DescKey(item);
        var desc = _adapter.GetLocalisedText(descKey, modLang)
                   ?? _adapter.GetLocalisedText(descKey, english)
                   ?? "";
        var descLogical = _adapter.GetLocalisedLogicalText(descKey, modLang) ?? "";
        item.DescDisplay = desc;
        item.DescLogical = descLogical.Length > 0 ? descLogical : desc;
        return loc;
    }
}
