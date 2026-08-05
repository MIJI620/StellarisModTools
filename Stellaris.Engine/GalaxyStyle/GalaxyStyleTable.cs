// 文件: Stellaris.Engine/GalaxyStyle/GalaxyStyleTable.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Parser;

namespace Stellaris.Engine.GalaxyStyle;

/// <summary>
/// 星系样式表 - 内存样式管理 + AST 持久化
/// 依赖 StellarisAdapter 进行文件读写，不直接操作磁盘
/// </summary>
internal sealed class GalaxyStyleTable
{
    private readonly StellarisAdapter _adapter;
    private readonly ILogger _logger;
    private readonly Dictionary<string, GalaxyStyleDefinition> _table;
    // 样式显示/落盘顺序（插入序；新增样式可指定插入位置）
    private readonly List<string> _styleOrder = new();
    private bool _loaded;

    private const string ConfigPath = "map/galaxy/galaxy_shapes.txt";

    // 子节点排序顺序（保证输出一致性）
    private static readonly List<string> TopLevelOrder = new()
    {
        "core_radius_perc",
        "num_stars_core_perc",
        "stars_min_dist",
        "countries",
        "fallen_empires",
        "num_arms",
        "arms",
        "ring",
        "preview_icon",
        "button_icon",
        "desc"
    };

    private static readonly List<string> CountriesOrder = new()
    {
        "ideal_sq_dist_between",
        "min_sq_dist_between"
    };

    private static readonly List<string> FallenOrder = new()
    {
        "ideal_sq_dist_between",
        "min_sq_dist_between"
    };

    private static readonly List<string> ArmsOrder = new()
    {
        "tightness_winding",
        "width",
        "fuzz",
        "seperation"
    };

    private static readonly List<string> RingOrder = new()
    {
        "width",
        "offset"
    };

    public GalaxyStyleTable(StellarisAdapter adapter, ILogger? logger = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger ?? NullLogger.Instance;
        _table = new Dictionary<string, GalaxyStyleDefinition>();
        _loaded = false;
    }

    // ===== 公开属性 =====
    public IReadOnlyDictionary<string, GalaxyStyleDefinition> Table => _table;
    public bool Loaded => _loaded;

    // ===== 加载 =====

    /// <summary>
    /// 从 SA 加载 galaxy_shapes.txt 到内存表
    /// </summary>
    public void LoadFromAdapter()
    {
        _table.Clear();
        _styleOrder.Clear();
        _loaded = false;

        var result = _adapter.GetConfig(ConfigPath);
        if (result == null || result.RootNodes == null || result.RootNodes.Count == 0)
        {
            // 文件不存在或为空，空表视为已加载
            _loaded = true;
            return;
        }

        foreach (var node in result.RootNodes)
        {
            // 只处理顶层 Block，且 Key 不是 "spriteTypes"
            if (node.Type == NodeType.Block && node.Key != "spriteTypes")
            {
                var def = ParseStyleBlock(node);
                if (def != null)
                {
                    _table[def.Name] = def;
                    if (!_styleOrder.Contains(def.Name))
                        _styleOrder.Add(def.Name);
                }
            }
        }

        _loaded = true;
    }

    // ===== 保存 =====

    /// <summary>
    /// 将内存表完整写回 galaxy_shapes.txt（原子写入）
    /// </summary>
    public void SaveToAdapter()
    {
        var rootNodes = BuildAllStyleBlocks();
        _logger.LogInformation("SaveToAdapter 样式块顺序: {Order}",
            string.Join(",", _styleOrder.Take(12)));

        // galaxy_shapes.txt 是**唯一**星系样式文件（游戏只支持这一个文件）——直接全替换：
        // 所有根节点 = 当前样式表块（含删除后不再生成的旧块自然消失），无多文件/多删顾虑。
        var result = _adapter.GetConfig(ConfigPath);
        if (result == null)
        {
            _adapter.CreateEmptyFileInMemory(ConfigPath, FileCategory.Config);
            result = _adapter.GetConfig(ConfigPath)!;
        }

        result.RootNodes = rootNodes;

        // 写回
        if (!_adapter.WriteFile(ConfigPath))
        {
            throw new InvalidOperationException($"写入样式文件失败: {ConfigPath}");
        }
    }

    // ===== 增删改查 =====

    public GalaxyStyleDefinition? GetStyle(string name)
    {
        if (!_loaded) LoadFromAdapter();
        if (_table.TryGetValue(name, out var def))
            return CloneDefinition(def);
        return null;
    }

    /// <summary>
    /// 添加样式；index 为显示/落盘顺序的插入位置（-1 = 追加末尾）。
    /// </summary>
    public void AddStyle(GalaxyStyleDefinition def, int index = -1)
    {
        if (!_loaded) LoadFromAdapter();
        if (_table.ContainsKey(def.Name))
            throw new InvalidOperationException($"样式 '{def.Name}' 已存在");

        _table[def.Name] = def;
        if (index >= 0 && index < _styleOrder.Count)
            _styleOrder.Insert(index, def.Name);
        else
            _styleOrder.Add(def.Name);
    }

    public void UpdateStyle(string name, GalaxyShapeParameters newParams)
    {
        if (!_loaded) LoadFromAdapter();
        if (!_table.TryGetValue(name, out var existing))
            throw new KeyNotFoundException($"样式 '{name}' 不存在");

        existing.UpdateParameters(newParams);
    }

    public bool DeleteStyle(string name)
    {
        if (!_loaded) LoadFromAdapter();
        _styleOrder.Remove(name);
        return _table.Remove(name);
    }

    /// <summary>重命名样式：更新字典 key、顺序列表与定义（Name 只读，重建定义）。</summary>
    public bool RenameStyle(string oldName, string newName, GalaxyShapeParameters newParams)
    {
        if (!_loaded) LoadFromAdapter();
        if (!_table.TryGetValue(oldName, out var existing))
            return false;
        var renamed = new GalaxyStyleDefinition(newName, newParams)
        {
            LocalisedName = existing.LocalisedName,
            LocalisedDescription = existing.LocalisedDescription
        };
        _table.Remove(oldName);
        _table[newName] = renamed;
        int idx = _styleOrder.IndexOf(oldName);
        if (idx >= 0)
            _styleOrder[idx] = newName;
        return true;
    }

    public List<string> GetAllNames()
    {
        if (!_loaded) LoadFromAdapter();
        return _styleOrder.ToList();
    }

    /// <summary>按新顺序重排样式（拖拽排序后调用）：只重排已存在的样式，缺失项保留在末尾。</summary>
    public void ReorderStyles(IReadOnlyList<string> order)
    {
        if (!_loaded) LoadFromAdapter();
        var newOrder = new List<string>(order.Count);
        foreach (var name in order)
            if (_table.ContainsKey(name) && !newOrder.Contains(name))
                newOrder.Add(name);
        foreach (var name in _styleOrder)
            if (!newOrder.Contains(name))
                newOrder.Add(name);
        _styleOrder.Clear();
        _styleOrder.AddRange(newOrder);
    }

    // ===== 新增：生成所有样式块（用于序列化，按 _styleOrder 顺序） =====
    public List<AstNode> BuildAllStyleBlocks()
    {
        var blocks = new List<AstNode>();
        foreach (var name in _styleOrder)
        {
            if (_table.TryGetValue(name, out var def))
            {
                var block = BuildStyleBlock(name, def.Parameters);
                blocks.Add(block);
            }
        }
        return blocks;
    }

    // ===== 解析（AST → 参数） =====

    private GalaxyStyleDefinition? ParseStyleBlock(AstNode node)
    {
        var param = new GalaxyShapeParameters();
        var children = node.Children;

        // 1. 顶层简单值
        foreach (var child in children)
        {
            if (child.Type != NodeType.Simple) continue;
            string? raw = GetRawInput(child);
            switch (child.Key)
            {
                case "core_radius_perc":
                    param.CoreRadiusPerc = ParseDouble(child.Value, 0.2);
                    if (raw != null) param.RawInputs["core_radius_perc"] = raw;
                    break;
                case "num_stars_core_perc":
                    param.NumStarsCorePerc = ParseDouble(child.Value, 0.0);
                    if (raw != null) param.RawInputs["num_stars_core_perc"] = raw;
                    break;
                case "stars_min_dist":
                    param.StarsMinDist = ParseDouble(child.Value, 8.0);
                    if (raw != null) param.RawInputs["stars_min_dist"] = raw;
                    break;
                case "num_arms":
                    param.NumArms = ParseInt(child.Value, 0);
                    if (raw != null) param.RawInputs["num_arms"] = raw;
                    break;
                case "preview_icon":
                    param.PreviewIcon = GetTextValue(child);
                    if (raw != null) param.RawInputs["preview_icon"] = raw;
                    break;
                case "button_icon":
                    param.ButtonIcon = GetTextValue(child);
                    if (raw != null) param.RawInputs["button_icon"] = raw;
                    break;
                case "desc":
                    param.DescKey = GetTextValue(child);
                    if (raw != null) param.RawInputs["desc"] = raw;
                    break;
            }
        }

        // 2. countries 块
        var countriesNode = children.FirstOrDefault(c => c.Type == NodeType.Block && c.Key == "countries");
        if (countriesNode != null)
        {
            foreach (var sub in countriesNode.Children)
            {
                if (sub.Type != NodeType.Simple) continue;
                string? raw = GetRawInput(sub);
                switch (sub.Key)
                {
                    case "ideal_sq_dist_between":
                        param.CountriesIdealDist = ParseInt(sub.Value, 5625);
                        if (raw != null) param.RawInputs["countries.ideal_sq_dist_between"] = raw;
                        break;
                    case "min_sq_dist_between":
                        param.CountriesMinDist = ParseInt(sub.Value, 900);
                        if (raw != null) param.RawInputs["countries.min_sq_dist_between"] = raw;
                        break;
                }
            }
        }

        // 3. fallen_empires 块
        var fallenNode = children.FirstOrDefault(c => c.Type == NodeType.Block && c.Key == "fallen_empires");
        if (fallenNode != null)
        {
            foreach (var sub in fallenNode.Children)
            {
                if (sub.Type != NodeType.Simple) continue;
                string? raw = GetRawInput(sub);
                switch (sub.Key)
                {
                    case "ideal_sq_dist_between":
                        param.FallenIdealDist = ParseInt(sub.Value, 15625);
                        if (raw != null) param.RawInputs["fallen_empires.ideal_sq_dist_between"] = raw;
                        break;
                    case "min_sq_dist_between":
                        param.FallenMinDist = ParseInt(sub.Value, 2500);
                        if (raw != null) param.RawInputs["fallen_empires.min_sq_dist_between"] = raw;
                        break;
                }
            }
        }

        // 4. arms 块
        var armsNode = children.FirstOrDefault(c => c.Type == NodeType.Block && c.Key == "arms");
        if (armsNode != null)
        {
            foreach (var sub in armsNode.Children)
            {
                if (sub.Type != NodeType.Simple) continue;
                string? raw = GetRawInput(sub);
                switch (sub.Key)
                {
                    case "tightness_winding":
                        param.Tightness = ParseDouble(sub.Value, 0.2);
                        if (raw != null) param.RawInputs["arms.tightness_winding"] = raw;
                        break;
                    case "width":
                        param.WidthDeg = ParseDouble(sub.Value, 30.0);
                        if (raw != null) param.RawInputs["arms.width"] = raw;
                        break;
                    case "fuzz":
                        param.Fuzz = ParseDouble(sub.Value, 10.0);
                        if (raw != null) param.RawInputs["arms.fuzz"] = raw;
                        break;
                    case "seperation":
                        param.ArmAngleDeg = ParseDouble(sub.Value, 180.0);
                        if (raw != null) param.RawInputs["arms.seperation"] = raw;
                        break;
                }
            }
        }

        // 5. ring 块
        param.HasRing = children.Any(c => c.Type == NodeType.Block && c.Key == "ring");
        var ringNode = children.FirstOrDefault(c => c.Type == NodeType.Block && c.Key == "ring");
        if (ringNode != null)
        {
            foreach (var sub in ringNode.Children)
            {
                if (sub.Type != NodeType.Simple) continue;
                string? raw = GetRawInput(sub);
                switch (sub.Key)
                {
                    case "width":
                        param.RingWidth = ParseDouble(sub.Value, 0.5);
                        if (raw != null) param.RawInputs["ring.width"] = raw;
                        break;
                    case "offset":
                        param.RingOffset = ParseDouble(sub.Value, 0.3);
                        if (raw != null) param.RawInputs["ring.offset"] = raw;
                        break;
                }
            }
        }

        return new GalaxyStyleDefinition(node.Key ?? "unnamed", param);
    }

    // ===== 构建（参数 → AST） =====

    public AstNode BuildStyleBlock(string name, GalaxyShapeParameters param)
    {
        var children = new List<AstNode>();

        // 按顺序添加顶层字段
        children.Add(CreateSimple("core_radius_perc", param.CoreRadiusPerc, GetRaw(param, "core_radius_perc")));
        children.Add(CreateSimple("num_stars_core_perc", param.NumStarsCorePerc, GetRaw(param, "num_stars_core_perc")));
        children.Add(CreateSimple("stars_min_dist", param.StarsMinDist, GetRaw(param, "stars_min_dist")));

        // countries 块
        children.Add(BuildBlock("countries", "countries", new Dictionary<string, object>
        {
            ["ideal_sq_dist_between"] = param.CountriesIdealDist,
            ["min_sq_dist_between"] = param.CountriesMinDist
        }, CountriesOrder, param));

        // fallen_empires 块
        children.Add(BuildBlock("fallen_empires", "fallen_empires", new Dictionary<string, object>
        {
            ["ideal_sq_dist_between"] = param.FallenIdealDist,
            ["min_sq_dist_between"] = param.FallenMinDist
        }, FallenOrder, param));

        children.Add(CreateSimple("num_arms", param.NumArms, GetRaw(param, "num_arms")));

        // arms 块（仅当有旋臂）
        if (param.NumArms > 0)
        {
            children.Add(BuildBlock("arms", "arms", new Dictionary<string, object>
            {
                ["tightness_winding"] = param.Tightness,
                ["width"] = param.WidthDeg,
                ["fuzz"] = param.Fuzz,
                ["seperation"] = param.ArmAngleDeg
            }, ArmsOrder, param));
        }

        // ring 块（仅当有环）
        if (param.HasRing)
        {
            children.Add(BuildBlock("ring", "ring", new Dictionary<string, object>
            {
                ["width"] = param.RingWidth,
                ["offset"] = param.RingOffset
            }, RingOrder, param));
        }

        children.Add(CreateSimple("preview_icon", param.PreviewIcon ?? string.Empty, GetRaw(param, "preview_icon")));
        children.Add(CreateSimple("button_icon", param.ButtonIcon ?? string.Empty, GetRaw(param, "button_icon")));
        children.Add(CreateSimple("desc", param.DescKey ?? string.Empty, GetRaw(param, "desc")));

        // 按顶层顺序排序
        var sorted = SortChildren(children, TopLevelOrder);

        return new AstNode
        {
            Type = NodeType.Block,
            Key = name,
            Children = sorted,
            OriginalLayout = OriginalLayout.MultiLine
        };
    }

    private AstNode BuildBlock(string key, string rawPathPrefix, Dictionary<string, object> fields, List<string> order, GalaxyShapeParameters param)
    {
        var children = new List<AstNode>();
        foreach (var kv in fields)
        {
            children.Add(CreateSimple(kv.Key, kv.Value, GetRaw(param, $"{rawPathPrefix}.{kv.Key}")));
        }
        var sorted = SortChildren(children, order);
        return new AstNode
        {
            Type = NodeType.Block,
            Key = key,
            Children = sorted,
            OriginalLayout = OriginalLayout.MultiLine
        };
    }

    private AstNode CreateSimple(string key, object value, string? rawText = null)
    {
        return new AstNode
        {
            Type = NodeType.Simple,
            Key = key,
            Value = value,
            // 字符串值（preview_icon / button_icon / desc 等）必须带双引号（黑箱测试结论）
            IsQuoted = value is string,
            OriginalLayout = OriginalLayout.SingleLine,
            RawText = rawText
        };
    }

    // ===== 排序工具 =====

    private static List<AstNode> SortChildren(List<AstNode> children, List<string> order)
    {
        var orderMap = order.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);

        return children
            .OrderBy(c =>
            {
                if (c.Key == null) return int.MaxValue;
                return orderMap.TryGetValue(c.Key, out int idx) ? idx : int.MaxValue;
            })
            .ThenBy(c => c.Key ?? string.Empty)
            .ToList();
    }

    // ===== 类型解析辅助 =====

    private static double ParseDouble(object? value, double defaultValue)
    {
        if (value == null) return defaultValue;
        try
        {
            return Convert.ToDouble(value);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static int ParseInt(object? value, int defaultValue)
    {
        if (value == null) return defaultValue;
        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return defaultValue;
        }
    }

    // ===== 参数输入（支持常量引用与引号容错） =====

    /// <summary>
    /// 按参数路径设置单个参数的原始输入。
    /// 输入 "@foo" / "@[foo + 1]"：识别为常量引用，内部经 adapter 自动解析求值，
    /// 运行时值写入强类型属性，原文保留在 RawInputs 中（写回时原样填回 "@"）。
    /// 输入普通文本：自动去除头尾多余双引号后按参数类型转换。
    /// </summary>
    public void SetStyleParam(string styleName, string paramPath, string? input)
    {
        if (!_table.TryGetValue(styleName, out var existing))
            throw new KeyNotFoundException($"样式 '{styleName}' 不存在");

        var param = existing.Parameters.Clone();
        ApplyParamInput(param, paramPath, input);

        _table[styleName] = new GalaxyStyleDefinition(styleName, param)
        {
            LocalisedName = existing.LocalisedName,
            LocalisedDescription = existing.LocalisedDescription
        };
        _logger.LogDebug("设置样式参数: {Style} -> {Path} = {Input}", styleName, paramPath, input);
    }

    private void ApplyParamInput(GalaxyShapeParameters param, string paramPath, string? input)
    {
        string trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new ArgumentException($"参数 '{paramPath}' 的输入不能为空", nameof(input));

        // 常量引用输入：保留原文，内部自动解析求值
        if (trimmed.StartsWith('@'))
        {
            param.RawInputs[paramPath] = trimmed;
            object? resolved;
            try
            {
                resolved = _adapter.ResolveConstantInput(trimmed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "参数 {Path} 的常量引用 {Input} 解析失败，写回时将保留原文", paramPath, trimmed);
                return;
            }

            if (resolved == null)
            {
                _logger.LogWarning("参数 {Path} 的常量引用 {Input} 无法解析（常量未找到），写回时将保留原文", paramPath, trimmed);
                return;
            }

            try
            {
                SetTypedValue(param, paramPath, resolved);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "参数 {Path} 的常量求值结果 {Value} 无法转换为目标类型，写回时将保留原文", paramPath, resolved);
            }
            return;
        }

        // 普通输入：去除头尾多余双引号（无论用户是否记得加引号）
        string clean = StripSurroundingQuotes(trimmed);
        param.RawInputs.Remove(paramPath);
        SetTypedValueFromString(param, paramPath, clean);
    }

    private static void SetTypedValue(GalaxyShapeParameters param, string path, object value)
    {
        switch (path)
        {
            case "core_radius_perc": param.CoreRadiusPerc = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case "num_stars_core_perc": param.NumStarsCorePerc = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case "stars_min_dist": param.StarsMinDist = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case "num_arms": param.NumArms = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "countries.ideal_sq_dist_between": param.CountriesIdealDist = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "countries.min_sq_dist_between": param.CountriesMinDist = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "fallen_empires.ideal_sq_dist_between": param.FallenIdealDist = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "fallen_empires.min_sq_dist_between": param.FallenMinDist = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
            case "arms.tightness_winding": param.Tightness = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case "arms.width": param.WidthDeg = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case "arms.fuzz": param.Fuzz = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case "arms.seperation": param.ArmAngleDeg = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case "ring.width": param.RingWidth = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case "ring.offset": param.RingOffset = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
            case "preview_icon": param.PreviewIcon = value?.ToString() ?? string.Empty; break;
            case "button_icon": param.ButtonIcon = value?.ToString() ?? string.Empty; break;
            case "desc": param.DescKey = value?.ToString() ?? string.Empty; break;
            default:
                throw new ArgumentException($"未知参数路径: {path}", nameof(path));
        }
    }

    private void SetTypedValueFromString(GalaxyShapeParameters param, string path, string clean)
    {
        try
        {
            switch (path)
            {
                case "core_radius_perc":
                    param.CoreRadiusPerc = ParseDoubleInvariant(clean, param.CoreRadiusPerc); break;
                case "num_stars_core_perc":
                    param.NumStarsCorePerc = ParseDoubleInvariant(clean, param.NumStarsCorePerc); break;
                case "stars_min_dist":
                    param.StarsMinDist = ParseDoubleInvariant(clean, param.StarsMinDist); break;
                case "num_arms":
                    param.NumArms = ParseIntInvariant(clean, param.NumArms); break;
                case "countries.ideal_sq_dist_between":
                    param.CountriesIdealDist = ParseIntInvariant(clean, param.CountriesIdealDist); break;
                case "countries.min_sq_dist_between":
                    param.CountriesMinDist = ParseIntInvariant(clean, param.CountriesMinDist); break;
                case "fallen_empires.ideal_sq_dist_between":
                    param.FallenIdealDist = ParseIntInvariant(clean, param.FallenIdealDist); break;
                case "fallen_empires.min_sq_dist_between":
                    param.FallenMinDist = ParseIntInvariant(clean, param.FallenMinDist); break;
                case "arms.tightness_winding":
                    param.Tightness = ParseDoubleInvariant(clean, param.Tightness); break;
                case "arms.width":
                    param.WidthDeg = ParseDoubleInvariant(clean, param.WidthDeg); break;
                case "arms.fuzz":
                    param.Fuzz = ParseDoubleInvariant(clean, param.Fuzz); break;
                case "arms.seperation":
                    param.ArmAngleDeg = ParseDoubleInvariant(clean, param.ArmAngleDeg); break;
                case "ring.width":
                    param.RingWidth = ParseDoubleInvariant(clean, param.RingWidth); break;
                case "ring.offset":
                    param.RingOffset = ParseDoubleInvariant(clean, param.RingOffset); break;
                case "preview_icon": param.PreviewIcon = clean; break;
                case "button_icon": param.ButtonIcon = clean; break;
                case "desc": param.DescKey = clean; break;
                default:
                    throw new ArgumentException($"未知参数路径: {path}", nameof(path));
            }
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "参数 {Path} 的输入 '{Input}' 无法转换，已保留原值", path, clean);
        }
    }

    private static double ParseDoubleInvariant(string s, double fallback)
    {
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }

    private static int ParseIntInvariant(string s, int fallback)
    {
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }

    /// <summary>
    /// 去除输入字符串头尾的成对双引号（避免双重引号）。
    /// </summary>
    private static string StripSurroundingQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }

    // ===== 原始输入辅助 =====

    private static string? GetRaw(GalaxyShapeParameters param, string path)
        => param.RawInputs.TryGetValue(path, out var raw) ? raw : null;

    /// <summary>
    /// 若节点的 RawText 为常量引用（@ 开头），返回原文；否则返回 null。
    /// </summary>
    private static string? GetRawInput(AstNode child)
    {
        string? raw = child.RawText;
        if (!string.IsNullOrEmpty(raw) && raw.StartsWith('@'))
            return raw;
        return null;
    }

    /// <summary>
    /// 安全读取文本参数值：未解析的常量引用（Value 为 ConstantValue）时退回原文或空串。
    /// </summary>
    private static string GetTextValue(AstNode child)
    {
        if (child.Value is ConstantValue)
            return child.RawText ?? string.Empty;
        return child.Value?.ToString() ?? string.Empty;
    }

    // ===== 深拷贝（返回副本） =====

    private static GalaxyStyleDefinition CloneDefinition(GalaxyStyleDefinition def)
    {
        return new GalaxyStyleDefinition(def.Name, def.Parameters.Clone())
        {
            LocalisedName = def.LocalisedName,
            LocalisedDescription = def.LocalisedDescription
        };
    }
}