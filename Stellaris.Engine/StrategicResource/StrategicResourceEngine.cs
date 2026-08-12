using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Stellaris.Parser;

namespace Stellaris.Engine.StrategicResource;

/// <summary>一个字段方案（来源 root + 值节点——显示文本 + 保存用节点）。</summary>
public sealed class FieldVariant
{
    public string Root { get; set; } = "";
    /// <summary>值节点（Simple = 叶子；Block = 含子节点）——保存时克隆写入。</summary>
    public AstNode ValueNode { get; set; } = null!;
    /// <summary>显示文本：Simple 为值；Block 为子内容（{ … }）。</summary>
    public string DisplayValue { get; set; } = "";
    /// <summary>来源文件显示（root 简称 + 固定路径）。</summary>
    public string SourceLabel { get; set; } = "";
}

/// <summary>字段行（**同 key 合并为一行**——各 root 的方案在右侧下拉里选）。</summary>
public sealed class ResourceFieldRow
{
    public string FieldKey { get; set; } = "";
    public bool IsBlock { get; set; }
    public List<FieldVariant> Variants { get; } = new();
    /// <summary>当前选中方案（默认：同值优先——多个同值选 Roots 更靠后的；不同值选最后一个）。</summary>
    public int SelectedIndex { get; set; }
    /// <summary>用户自定义值（下拉选"自定义"时——第三列输入框填写）。null = 未自定义。</summary>
    public string? CustomValue { get; set; }
    public FieldVariant Selected => Variants.Count == 0 ? null! : Variants[Math.Clamp(SelectedIndex, 0, Variants.Count - 1)];
}

/// <summary>一条资源（顶层 key 合并——本地化名 + 字段行）。</summary>
public sealed class StrategicResourceEntry
{
    public string Key { get; set; } = "";
    /// <summary>本地化名字键（默认 = Key；可编辑——本地化读写走它，描述键 = NameKey + "_desc"）。</summary>
    public string NameKey { get; set; } = "";
    public List<ResourceFieldRow> Rows { get; } = new();
    public List<string> Roots { get; } = new();
    public string NameLogical { get; set; } = "";
    public string NameDisplay { get; set; } = "";
    public string DescLogical { get; set; } = "";
    public string DescDisplay { get; set; } = "";
}

/// <summary>
/// 战略资源引擎：固定路径撞击扫描 + 顶层 key 合并（同 key 字段合并一行 + 方案下拉）。
/// 保存 = 行登记（MarkRowForSave）→ 统一 SaveAll（按选中方案写对应 root 文件——经 SA WriteCollisionFile）。
/// </summary>
public sealed class StrategicResourceEngine
{
    /// <summary>固定资源路径（唯一）。</summary>
    public const string ResourceRelPath = "common/strategic_resources/00_strategic_resources.txt";

    private readonly StellarisAdapter _adapter;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private List<StrategicResourceEntry>? _entries;
    /// <summary>撞击 AST 缓存（root → AST——保存时改字段用）。</summary>
    private Dictionary<string, ParserResult>? _asts;
    /// <summary>root 顺序（_adapter.Roots——索引越大越靠后——同值/默认选更靠后的）。</summary>
    private readonly string[] _rootsOrder;

    public StrategicResourceEngine(StellarisAdapter adapter, ILogger logger)
    {
        _adapter = adapter;
        _logger = logger;
        _rootsOrder = adapter.Roots.ToArray();
    }

    /// <summary>初始化重扫描（幂等）。</summary>
    public void ScanAll()
    {
        lock (_lock)
        {
            (_entries, _asts) = BuildTable();
            _logger.LogInformation("战略资源合并表完成：{Count} 条资源", _entries.Count);
        }
    }

    /// <summary>超大表（顶层 key 合并；同 key 字段一行 + 方案列表）。</summary>
    public IReadOnlyList<StrategicResourceEntry> GetEntries()
    {
        lock (_lock)
        {
            // 必须同时初始化 _asts（SaveAll 依赖它）——否则保存直接跳过（"成功"但没写盘）
            if (_entries == null)
                (_entries, _asts) = BuildTable();
            return _entries;
        }
    }

    /// <summary>扩展接口：目前都有哪些资源种类——返回 key 表格（顶层 key 列表，合并后去重顺序）。</summary>
    public IReadOnlyList<string> GetResourceKeys()
        => GetEntries().Select(e => e.Key).ToList();

    /// <summary>
    /// 法令/决议专用资源解析：从 `resources = { ... }` 块解析启动消耗 cost 与每月消耗 upkeep。
    /// 两种内部信息：cost / upkeep——里面是 `资源 = 数量` 字段（如 influence = 0）。
    /// 返回 (Cost: 资源→数量, Upkeep: 资源→数量)。无 cost/upkeep 块 → 空字典。
    /// </summary>
    public static (Dictionary<string, double> Cost, Dictionary<string, double> Upkeep)
        ParseEdictResources(AstNode resourcesBlock)
    {
        var cost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var upkeep = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (resourcesBlock?.Children == null)
            return (cost, upkeep);
        foreach (var child in resourcesBlock.Children)
        {
            if (child.Children == null)
                continue;
            var target = string.Equals(child.Key, "upkeep", StringComparison.OrdinalIgnoreCase) ? upkeep : cost;
            foreach (var kv in child.Children)
            {
                if (kv.Value == null)
                    continue;
                if (double.TryParse(kv.Value.ToString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var amount))
                    target[kv.Key] = amount;
            }
        }
        return (cost, upkeep);
    }

    /// <summary>读取本地化名/描述（无前缀键 + _desc 描述——当前语言 → english → 回退 key/空）。</summary>
    public void LoadLocalisation(StrategicResourceEntry item, string modLang, string english = "english")
    {
        var name = _adapter.GetLocalisedText(item.Key, modLang)
                   ?? _adapter.GetLocalisedText(item.Key, english)
                   ?? item.Key;
        item.NameDisplay = name;
        item.NameLogical = name;
        var desc = _adapter.GetLocalisedText(item.Key + "_desc", modLang)
                   ?? _adapter.GetLocalisedText(item.Key + "_desc", english)
                   ?? "";
        item.DescDisplay = desc;
        item.DescLogical = desc;
    }

    /// <summary>统一保存（规范）：所有行按界面选中方案修改字段，经 SA 标准 WriteFile 写到
    /// **Roots 最后一位**（写盘根目录决策在 SA：WriteFile 无 targetRoot → roots[^1]；自动创建缺失目录）。
    /// 数据源 = SA GetConfig（磁盘读取的合并后 AST——正确数据，不重建）。</summary>
    public (int Saved, List<string> Errors) SaveAll()
    {
        lock (_lock)
        {
            var errors = new List<string>();
            if (_entries == null)
                return (0, errors);
            // SA 读：合并后 AST（磁盘读的正确数据——不重建）
            var result = _adapter.GetConfig(ResourceRelPath);
            if (result == null)
            {
                // 无任何 root 有该文件——SA 先建空（内存 AST），WriteFile 时创建文件
                _adapter.CreateEmptyFileInMemory(ResourceRelPath, FileCategory.Config);
                result = _adapter.GetConfig(ResourceRelPath);
                if (result == null)
                {
                    errors.Add(ResourceRelPath + ": 无法初始化配置文件");
                    return (0, errors);
                }
            }
            foreach (var entry in _entries)
            {
                foreach (var row in entry.Rows)
                {
                    if (row.Variants.Count == 0)
                        continue;
                    var block = result.RootNodes.FirstOrDefault(n => n.Key == entry.Key);
                    if (block == null)
                    {
                        // 该资源块在目标数据里不存在（可能只存在于其他 root）——补建块
                        block = new AstNode { Type = NodeType.Block, Key = entry.Key };
                        result.RootNodes.Add(block);
                    }
                    var idx = block.Children.FindIndex(c => c.Key == row.FieldKey);
                    // 写入的 AST 必须由 **UI 当前内容**重新解析拼出（不能直接克隆扫描旧节点）：
                    // 自定义 → 输入框文本；非自定义 → 下拉选中方案的显示值。
                    // 解析失败 = 不合规 → 报错跳过（绝不写入非法内容）。
                    var uiValue = row.CustomValue ?? row.Selected.DisplayValue;
                    var clone = ParseCustomValue(uiValue, row.FieldKey);
                    if (clone == null)
                    {
                        errors.Add(entry.Key + "." + row.FieldKey + ": 值不合规（无法解析）: " + uiValue);
                        continue;
                    }
                    clone.Key = row.FieldKey;
                    if (idx >= 0)
                        block.Children[idx] = clone;
                    else
                        block.Children.Add(clone);
                }
            }
            // 有不合规（解析失败）→ 整个保存失败，绝不写入（保证写入文件全合规）
            if (errors.Count > 0)
            {
                _logger.LogError("战略资源保存失败（含不合规值）: {Errors}", string.Join(" | ", errors));
                return (0, errors);
            }
            // SA 写：默认 Roots 最后一位 + 自动创建缺失目录（统一保存规范，引擎不直接碰磁盘）
            var saved = _adapter.WriteFile(ResourceRelPath) ? 1 : 0;
            if (saved == 0)
                errors.Add(ResourceRelPath + ": 写入失败");
            if (errors.Count > 0)
                _logger.LogError("战略资源保存失败: {Errors}", string.Join(" | ", errors));
            return (saved, errors);
        }
    }

    /// <summary>深拷贝节点（避免共享引用——插入前复制）。</summary>
    private static AstNode CloneNode(AstNode n)
        => new AstNode
        {
            Key = n.Key,
            Value = n.Value,
            Type = n.Type,
            SeparatorType = n.SeparatorType,
            Children = n.Children?.Select(CloneNode).ToList() ?? new List<AstNode>()
        };

    /// <summary>把自定义输入文本重新 AST 解析为节点：先清空左右无效前缀（空字符 Trim），
    /// 再接上对应的 key 和 = 解析（合法 Simple/Block 才算有效）。
    /// 无效返回 null（调用方回退下拉第一项）。</summary>
    private AstNode? ParseCustomValue(string text, string fieldKey)
    {
        try
        {
            var trimmed = text?.Trim() ?? "";
            if (trimmed.Length == 0)
                return null;
            // 清空左右无效前缀后接上 key 和 =（统一包装解析——支持裸值/块/引号；经 SA.ParseSingleNode，2026-08）
            var wrapped = fieldKey + " = " + trimmed;
            var node = _adapter.ParseSingleNode(wrapped);
            if (node == null)
                return null;
            // 合法 Simple/Block（Key 必须等于目标字段 key）
            if (!string.Equals(node.Key, fieldKey, StringComparison.Ordinal))
                return null;
            if (node.Type != NodeType.Simple && node.Type != NodeType.Block && node.Type != NodeType.List)
                return null;
            return _adapter.CloneNode(node);
        }
        catch
        {
            return null;
        }
    }

    private (List<StrategicResourceEntry> entries, Dictionary<string, ParserResult> asts) BuildTable()
    {
        var result = new List<StrategicResourceEntry>();
        var astByRoot = new Dictionary<string, ParserResult>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, StrategicResourceEntry>(StringComparer.Ordinal);

        var asts = _adapter.GetCollisionAsts(ResourceRelPath);
        if (asts.Count == 0)
        {
            var single = _adapter.GetConfig(ResourceRelPath);
            if (single != null)
            {
                var root = _adapter.GetFileRoot(ResourceRelPath) ?? "";
                asts = new List<(string, string, ParserResult)> { (root, ResourceRelPath, single) };
            }
        }

        foreach (var (root, _, ast) in asts)
        {
            astByRoot[root] = ast;
            foreach (var node in ast.RootNodes)
            {
                if ((node.Type != NodeType.Block && node.Type != NodeType.List) || string.IsNullOrEmpty(node.Key))
                    continue;
                if (!byKey.TryGetValue(node.Key, out var entry))
                {
                    entry = new StrategicResourceEntry { Key = node.Key };
                    byKey[node.Key] = entry;
                    result.Add(entry);
                }
                if (!entry.Roots.Contains(root))
                    entry.Roots.Add(root);
                if (node.Children == null)
                    continue;
                foreach (var child in node.Children)
                {
                    var row = entry.Rows.FirstOrDefault(r => r.FieldKey == child.Key);
                    if (row == null)
                    {
                        row = new ResourceFieldRow
                        {
                            FieldKey = child.Key,
                            IsBlock = child.Type == NodeType.Block || child.Type == NodeType.List
                        };
                        entry.Rows.Add(row);
                    }
                    var variant = new FieldVariant
                    {
                        Root = root,
                        ValueNode = child,
                        DisplayValue = DisplayNode(child),
                        SourceLabel = root
                    };
                    row.Variants.Add(variant);
                }
            }
        }
        // 默认选中：选 Roots 更靠后的方案（同值/不同值统一——后读覆盖语义）
        foreach (var entry in result)
        {
            foreach (var r in entry.Rows)
            {
                if (r.Variants.Count <= 1)
                    continue;
                r.SelectedIndex = r.Variants
                    .Select((v, i) => (v, i))
                    .OrderByDescending(x => Array.IndexOf(_rootsOrder, x.v.Root))
                    .First().i;
            }
        }
        return (result, astByRoot);
    }

    /// <summary>节点显示文本：Simple 为值；Block 为子内容序列化（{ … }）——经 SA.SerializeNodes，2026-08。</summary>
    private string DisplayNode(AstNode node)
    {
        if (node.Type == NodeType.Block || node.Type == NodeType.List)
        {
            if (node.Children == null || node.Children.Count == 0)
                return "{ }";
            return "{ " + _adapter.SerializeNodes(node.Children).Trim() + " }";
        }
        return node.Value?.ToString() ?? "";
    }

    // ============================================================
    // resources 通用解析/生成（cost/upkeep/produces）——法令/决议/未来通用
    // ============================================================

    /// <summary>一个资源组（cost/upkeep/produces 的一个块）：资源→数值 + multiplier + trigger。
    /// 同桶可有多个组（**资源可重复添加**）；生成 AST 时相同 (multiplier, trigger) 的组自动合并。</summary>
    public sealed class ResourceGroup
    {
        /// <summary>资源 → 数量（Simple 字段，如 alloys = 68）。</summary>
        public Dictionary<string, double> Amounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>倍率原文（数字或 value:xxx 变量；null = 无）。</summary>
        public string? Multiplier { get; set; }

        /// <summary>条件 trigger Block（原样保留；null = 无）。</summary>
        public AstNode? Trigger { get; set; }
    }

    /// <summary>一个桶（cost/upkeep/produces）：可含多个组（重复添加、各自倍率/条件）。</summary>
    public sealed class ResourceBucket
    {
        public List<ResourceGroup> Groups { get; } = new();
    }

    /// <summary>条件文本的 4 位稳定哈希（FNV-1a → 低 16 位 hex）——**仅显示用**（表格紧凑标识），
    /// 分组/合并逻辑用完整文本（杜绝碰撞误合并）；悬停 ToolTip 显示原文。</summary>
    public static string TriggerHash(string triggerText)
    {
        uint hash = 2166136261;
        foreach (var c in triggerText)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return (hash & 0xFFFF).ToString("X4");
    }

    /// <summary>
    /// 解析 `resources = { ... }` 块为三个桶：cost / upkeep / **produces**（游戏语法，非 product）。
    /// 每个 cost/upkeep/produces 块 = 一个组（**可重复出现**——各自资源/倍率/条件）；
    /// 其余子块（category 等）忽略。
    /// </summary>
    public static (ResourceBucket Cost, ResourceBucket Upkeep, ResourceBucket Produces)
        ParseResources(AstNode resourcesBlock)
    {
        var cost = new ResourceBucket();
        var upkeep = new ResourceBucket();
        var produces = new ResourceBucket();
        if (resourcesBlock?.Children == null)
            return (cost, upkeep, produces);
        foreach (var child in resourcesBlock.Children)
        {
            ResourceBucket? bucket;
            if (string.Equals(child.Key, "cost", StringComparison.OrdinalIgnoreCase))
                bucket = cost;
            else if (string.Equals(child.Key, "upkeep", StringComparison.OrdinalIgnoreCase))
                bucket = upkeep;
            else if (string.Equals(child.Key, "produces", StringComparison.OrdinalIgnoreCase))
                bucket = produces;
            else
                continue;   // 只认 cost/upkeep/produces
            bucket.Groups.Add(ParseGroup(child));
        }
        return (cost, upkeep, produces);
    }

    /// <summary>解析单个组块（cost/upkeep/produces 的内部内容）：multiplier Simple / trigger Block / 资源 Simple。</summary>
    private static ResourceGroup ParseGroup(AstNode groupBlock)
    {
        var group = new ResourceGroup();
        if (groupBlock.Children == null)
            return group;
        foreach (var kv in groupBlock.Children)
        {
            if (string.Equals(kv.Key, "multiplier", StringComparison.OrdinalIgnoreCase))
            {
                group.Multiplier = kv.Value?.ToString();
            }
            else if (string.Equals(kv.Key, "trigger", StringComparison.OrdinalIgnoreCase))
            {
                if (kv.Type == NodeType.Block)
                    group.Trigger = CloneNode(kv);
            }
            else if (kv.Value != null && double.TryParse(kv.Value.ToString(), System.Globalization.NumberStyles.Any,
                         System.Globalization.CultureInfo.InvariantCulture, out var amount))
            {
                group.Amounts[kv.Key] = amount;
            }
        }
        return group;
    }

    /// <summary>
    /// 生成**复合型** resources AST：同桶内按 (multiplier 原文, trigger 完整文本) 整合——
    /// 相同的组合并（资源加总），不同的各自成块（同 key 可重复输出——合法 resources）。
    /// category 等非桶字段不生成（由保存层/调用方补）。
    /// </summary>
    public static AstNode BuildResourcesBlock(ResourceBucket? cost, ResourceBucket? upkeep, ResourceBucket? produces)
    {
        var resources = new AstNode { Type = NodeType.Block, Key = "resources" };
        AddBucket(resources, "cost", cost);
        AddBucket(resources, "upkeep", upkeep);
        AddBucket(resources, "produces", produces);
        return resources;
    }

    /// <summary>按 (multiplier, trigger) 分组合并后，每组生成一个块追加到 resources。</summary>
    private static void AddBucket(AstNode resources, string key, ResourceBucket? bucket)
    {
        if (bucket == null || bucket.Groups.Count == 0)
            return;
        var merged = new List<(string GroupKey, ResourceGroup Group)>();
        foreach (var g in bucket.Groups)
        {
            var gkey = (g.Multiplier ?? "") + "\u0001" + TriggerText(g.Trigger);
            var hit = merged.FirstOrDefault(m => string.Equals(m.GroupKey, gkey, StringComparison.Ordinal));
            if (hit.Group != null)
            {
                foreach (var kv in g.Amounts)
                    hit.Group.Amounts[kv.Key] = hit.Group.Amounts.GetValueOrDefault(kv.Key) + kv.Value;
            }
            else
            {
                var target = new ResourceGroup { Multiplier = g.Multiplier, Trigger = g.Trigger };
                foreach (var kv in g.Amounts)
                    target.Amounts[kv.Key] = kv.Value;
                merged.Add((gkey, target));
            }
        }
        foreach (var (_, g) in merged)
            resources.Children.Add(BuildBucketBlock(key, g));
    }

    /// <summary>组块完整文本（合并键用——完整文本比较，杜绝哈希碰撞误合并）。
    /// static 工具链（BuildResourcesBlock 纯函数——不碰引擎状态/磁盘），与 SA.SerializeNodes 同实现（完整不丢内容）。</summary>
    private static string TriggerText(AstNode? trigger)
        => trigger == null ? "" : SerializationHelper.Serialize(trigger.Children).Trim();

    /// <summary>构建单个组块：`key = { 资源 = 数值 ... [multiplier = x] [trigger = { ... }] }`。</summary>
    public static AstNode BuildBucketBlock(string key, ResourceGroup group)
    {
        var block = new AstNode { Type = NodeType.Block, Key = key };
        foreach (var kv in group.Amounts)
        {
            block.Children.Add(new AstNode
            {
                Type = NodeType.Simple,
                Key = kv.Key,
                Value = kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }
        if (!string.IsNullOrEmpty(group.Multiplier))
        {
            block.Children.Add(new AstNode
            {
                Type = NodeType.Simple,
                Key = "multiplier",
                Value = group.Multiplier
            });
        }
        if (group.Trigger != null)
            block.Children.Add(CloneNode(group.Trigger));
        return block;
    }
}
