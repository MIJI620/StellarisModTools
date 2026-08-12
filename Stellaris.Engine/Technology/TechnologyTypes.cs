// 文件: Stellaris.Engine/Technology/TechnologyTypes.cs
// 科技节点图模型：TechNode（单科技条目）+ 布局辅助。
// 引擎只读浏览，数据源全经 StellarisAdapter（GetConfig / ResolveConstantInput / GetLocalisedText）。

using System.Collections.Generic;

namespace Stellaris.Engine.Technology;

/// <summary>科技字段名常量（脏字段追踪——弹窗提交标记修改过的字段，保存只写这些字段回 AST）。</summary>
public static class TechField
{
    public const string Area = "area";
    public const string Tier = "tier";
    public const string Cost = "cost";
    public const string Levels = "levels";
    public const string CostPerLevel = "cost_per_level";
    public const string Category = "category";
    public const string Prerequisites = "prerequisites";
    public const string Icon = "icon";
    public const string Weight = "weight";
    public const string StartTech = "start_tech";
    public const string Potential = "potential";
    public const string Modifier = "modifier";
    public const string WeightModifier = "weight_modifier";
    public const string AiWeight = "ai_weight";
    public const string PrereqForDesc = "prereqfor_desc";
}

/// <summary>单个科技条目（common/technology/*.txt 顶层块解析结果）。</summary>
public sealed class TechNode
{
    /// <summary>科技 key（块 key，如 tech_antimatter_power）。</summary>
    public string Key { get; set; } = "";

    /// <summary>大类（area 字段）：physics / society / engineering（决定卡片标题背景色）。</summary>
    public string Area { get; set; } = "";

    /// <summary>学科（category 数组值，如 biology / materials——决定右侧小图标）。</summary>
    public List<string> Categories { get; } = new();

    /// <summary>tier（@ 常量已解析为数值；解析失败为 -1）。</summary>
    public int Tier { get; set; } = -1;

    /// <summary>cost（@ 常量已解析为数值；解析失败为 0）。</summary>
    public int Cost { get; set; }

    /// <summary>cost **自定义块原文**（cost = { factor = ... } 形态；null = 基础数值）。
    /// 弹窗"自定义"模式编辑，保存写 cost 块；基础模式写 Simple cost = Cost。</summary>
    public string? CostRaw { get; set; }

    /// <summary>levels（可循环次数；-1 = 无限；无字段 = 1）。</summary>
    public int Levels { get; set; } = 1;

    /// <summary>是否有 levels 字段（有才显示循环信息）。</summary>
    public bool HasLevels { get; set; }

    /// <summary>cost_per_level（每次循环成本增加量；@ 常量已解析）。</summary>
    public int CostPerLevel { get; set; }

    /// <summary>是否有 cost_per_level 字段。</summary>
    public bool HasCostPerLevel { get; set; }

    /// <summary>前置科技 key 列表（prerequisites 数组值）。</summary>
    public List<string> Prerequisites { get; } = new();

    /// <summary>is_rare（稀有——紫色边框）。</summary>
    public bool IsRare { get; set; }

    /// <summary>is_dangerous（危险——红色边框，优先于稀有）。</summary>
    public bool IsDangerous { get; set; }

    /// <summary>start_tech（起始科技）。</summary>
    public bool StartTech { get; set; }

    /// <summary>icon 字段值（有则图标 = technologies/{Icon}.dds；无则 = technologies/{Key}.dds）。</summary>
    public string? Icon { get; set; }

    // ===== 弹窗编辑扩展字段（本期内存编辑，不落盘——用户确认） =====

    /// <summary>weight（Simple 值原文；无 = null）。</summary>
    public string? Weight { get; set; }

    /// <summary>prereqfor_desc 块原文（默认空）。</summary>
    public string? PrereqForDesc { get; set; }

    /// <summary>potential 块原文（用于弹窗预设判定/自定义）。</summary>
    public string? PotentialRaw { get; set; }

    /// <summary>modifier 键值条目（参考法令：key = 数值）。</summary>
    public List<(string Key, string Value)> ModifierEntries { get; } = new();

    /// <summary>weight_modifier 块原文（默认 weight = 1）。</summary>
    public string? WeightModifierRaw { get; set; }

    /// <summary>ai_weight 块原文（默认 weight = 1）。</summary>
    public string? AiWeightRaw { get; set; }

    /// <summary>所属文件相对路径（弹窗"所属文件"行；落盘用——用户）。</summary>
    public string? OwnerFile { get; set; }

    /// <summary>**脏字段追踪**（保存用）：弹窗提交时标记修改过的字段（TechField 常量）。
    /// 保存只把这些字段写回 AST 块，未编辑字段（is_rare/starting_potential/technology_swap 等弹窗未覆盖的）保留原样。</summary>
    public HashSet<string> DirtyFields { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>**内存本地化**（lang → 逻辑原文；名称键 = {Key}、描述键 = {Key}_desc）。
    /// 弹窗输入的文本写这里（用户：至少要写到科技自己的特殊内存，保存用；不落盘）。</summary>
    public Dictionary<string, string> NameLocalisations { get; } = new();
    public Dictionary<string, string> DescLocalisations { get; } = new();
}
