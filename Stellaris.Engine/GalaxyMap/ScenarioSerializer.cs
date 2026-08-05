// 文件: Stellaris.Engine/GalaxyMap/ScenarioSerializer.cs
// 场景对象 → AST（规范 2.1 / 2.2 / 4.2 / 4.4a）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stellaris.Parser;

namespace Stellaris.Engine.GalaxyMap;

internal static class ScenarioSerializer
{
    // ==================== 动态地图 ====================

    public static AstNode BuildDynamicRoot(DynamicScenario s)
    {
        var children = new List<AstNode>
        {
            QuotedSimple("name", s.Name),
            Simple("priority", s.Priority),
            Simple("num_stars", s.NumStars),
            Simple("radius", s.Radius)
        };

        // 帝国
        children.Add(IntRangeBlock("num_empires", s.NumEmpires));
        children.Add(Simple("num_empire_default", s.NumEmpireDefault));
        children.Add(Simple("advanced_empire_default", s.AdvancedEmpireDefault));
        children.Add(Simple("fallen_empire_default", s.FallenEmpireDefault));
        children.Add(Simple("fallen_empire_max", s.FallenEmpireMax));
        children.Add(Simple("marauder_empire_default", s.MarauderEmpireDefault));
        children.Add(Simple("marauder_empire_max", s.MarauderEmpireMax));
        children.Add(Simple("nomad_empire_default", s.NomadEmpireDefault));
        children.Add(Simple("nomad_empire_max", s.NomadEmpireMax));

        // 行星与危机
        children.Add(Simple("colonizable_planet_odds", s.ColonizablePlanetOdds));
        children.Add(Simple("primitive_odds", s.PrimitiveOdds));
        children.Add(Simple("crisis_strength", s.CrisisStrength));
        children.Add(DoubleListBlock("extra_crisis_strength", s.ExtraCrisisStrength));

        // 星云
        children.Add(Simple("num_nebulas", s.NumNebulas));
        children.Add(Simple("nebula_size", s.NebulaSize));
        children.Add(Simple("nebula_min_dist", s.NebulaMinDist));

        // 虫洞、星门、航道
        children.Add(IntRangeBlock("num_wormhole_pairs", s.NumWormholePairs));
        children.Add(Simple("num_wormhole_pairs_default", s.NumWormholePairsDefault));
        children.Add(IntRangeBlock("num_gateways", s.NumGateways));
        children.Add(Simple("num_gateways_default", s.NumGatewaysDefault));
        children.Add(DoubleRangeBlock("num_hyperlanes", s.NumHyperlanes));
        children.Add(Simple("num_hyperlanes_default", s.NumHyperlanesDefault));

        // 星系生成
        children.Add(ClusterBlock(s.ClusterCount));
        if (s.ClusterRadius.HasValue) children.Add(Simple("cluster_radius", s.ClusterRadius.Value));
        if (s.ClusterDistanceFromCore.HasValue) children.Add(Simple("cluster_distance_from_core", s.ClusterDistanceFromCore.Value));
        children.Add(Simple("max_hyperlane_distance", s.MaxHyperlaneDistance));
        children.Add(PartitionBlock("home_system_partitions", s.HomeSystemPartitions));
        children.Add(PartitionBlock("open_space_partitions", s.OpenSpacePartitions));

        // 支持形状（文件顺序，规范 3.2a）
        foreach (var shape in s.SupportedShapes)
            children.Add(Simple("supports_shape", shape));

        return new AstNode { Type = NodeType.Block, Key = "setup_scenario", Children = children, OriginalLayout = OriginalLayout.MultiLine };
    }

    // ==================== 静态地图 ====================

    public static AstNode BuildStaticRoot(StaticScenario s, int precision)
    {
        var children = new List<AstNode>
        {
            QuotedSimple("name", s.Name),
            Simple("priority", s.Priority)
        };

        // 共有字段（帝国、行星、危机）
        children.Add(IntRangeBlock("num_empires", s.NumEmpires));
        children.Add(Simple("num_empire_default", s.NumEmpireDefault));
        children.Add(Simple("advanced_empire_default", s.AdvancedEmpireDefault));
        children.Add(Simple("fallen_empire_default", s.FallenEmpireDefault));
        children.Add(Simple("fallen_empire_max", s.FallenEmpireMax));
        children.Add(Simple("marauder_empire_default", s.MarauderEmpireDefault));
        children.Add(Simple("marauder_empire_max", s.MarauderEmpireMax));
        children.Add(Simple("nomad_empire_default", s.NomadEmpireDefault));
        children.Add(Simple("nomad_empire_max", s.NomadEmpireMax));
        children.Add(Simple("colonizable_planet_odds", s.ColonizablePlanetOdds));
        children.Add(Simple("primitive_odds", s.PrimitiveOdds));
        children.Add(Simple("crisis_strength", s.CrisisStrength));
        children.Add(DoubleListBlock("extra_crisis_strength", s.ExtraCrisisStrength));
        // 照抄原版 static_galaxy_example：范围块 + default + random_hyperlanes + core_radius 全写（不吞）
        children.Add(IntRangeBlock("num_wormhole_pairs", s.NumWormholePairs));
        children.Add(Simple("num_wormhole_pairs_default", s.NumWormholePairsDefault));
        children.Add(IntRangeBlock("num_gateways", s.NumGateways));
        children.Add(Simple("num_gateways_default", s.NumGatewaysDefault));
        children.Add(Simple("random_hyperlanes", s.RandomHyperlanes));
        children.Add(Simple("core_radius", s.CoreRadiusPerc));

        // 支持形状（与动态地图一致，作为样式排序基准）
        foreach (var shape in s.SupportedShapes)
            children.Add(Simple("supports_shape", shape));

        // 坐标已固化（4.4a）：不再写 coordinate_transform 块，直接写变换后坐标

        // 系统
        foreach (var system in s.Systems)
            children.Add(SystemBlock(system, precision));

        // 航道
        foreach (var h in s.Hyperlanes)
            children.Add(HyperlaneBlock("add_hyperlane", h));
        foreach (var h in s.PreventedHyperlanes)
            children.Add(HyperlaneBlock("prevent_hyperlane", h));

        // 星云
        foreach (var n in s.Nebulas)
            children.Add(NebulaBlock(n, precision));

        return new AstNode { Type = NodeType.Block, Key = "static_galaxy_scenario", Children = children, OriginalLayout = OriginalLayout.MultiLine };
    }

    private static AstNode SystemBlock(SystemEntry e, int precision)
    {
        var children = new List<AstNode>
        {
            QuotedSimple("id", e.Id)
        };
        if (e.Name.Length > 0) children.Add(QuotedSimple("name", e.Name));

        // position：范围块保留为 { min = .., max = .. }（规范 2.2）；z 默认 0 时省略（原版 2D 平面惯例）
        var posChildren = new List<AstNode>();
        posChildren.Add(AxisNode("x", e.Position, e.Position.XIsRange, e.Position.X, e.Position.XRange, precision));
        posChildren.Add(AxisNode("y", e.Position, e.Position.YIsRange, e.Position.Y, e.Position.YRange, precision));
        if (e.Position.Z != 0 || e.Position.ZIsRange)
            posChildren.Add(AxisNode("z", e.Position, e.Position.ZIsRange, e.Position.Z, e.Position.ZRange, precision));
        children.Add(Block("position", posChildren));

        if (!string.IsNullOrEmpty(e.Initializer)) children.Add(QuotedSimple("initializer", e.Initializer));
        if (!string.IsNullOrEmpty(e.SpawnDesign)) children.Add(QuotedSimple("spawn_design", e.SpawnDesign));
        if (e.SpawnWeight != null) children.Add(SystemEntry.DeepCloneAst(e.SpawnWeight));
        if (e.Effect != null) children.Add(SystemEntry.DeepCloneAst(e.Effect));
        if (e.Category != "normal" && e.Category.Length > 0) children.Add(QuotedSimple("category", e.Category));

        return Block("system", children);
    }

    private static AstNode AxisNode(string key, SystemPosition pos, bool isRange, double value,
        (double Min, double Max) range, int precision)
    {
        if (isRange)
        {
            return Block(key, new List<AstNode>
            {
                Simple("min", FormatDouble(range.Min, precision)),
                Simple("max", FormatDouble(range.Max, precision))
            });
        }
        return Simple(key, FormatDouble(value, precision));
    }

    private static AstNode HyperlaneBlock(string key, Hyperlane h)
        => Block(key, new List<AstNode> { QuotedSimple("from", h.From), QuotedSimple("to", h.To) });

    private static AstNode NebulaBlock(NebulaDef n, int precision)
        => Block("nebula", new List<AstNode>
        {
            QuotedSimple("name", n.Name),
            Simple("x", FormatDouble(n.X, precision)),
            Simple("y", FormatDouble(n.Y, precision)),
            // z 默认 0 时省略（原版 2D 惯例）
            n.Z != 0 ? Simple("z", FormatDouble(n.Z, precision)) : null!,
            Simple("radius", n.Radius)
        }.Where(x => x != null).ToList());

    // ==================== 通用构建辅助 ====================

    internal static AstNode Simple(string key, object value)
        => new()
        {
            Type = NodeType.Simple,
            Key = key,
            Value = value,
            IsQuoted = false,
            OriginalLayout = OriginalLayout.SingleLine
        };

    /// <summary>带引号字符串字段（id/name/from/to/method 等，规范样例均带引号；supports_shape 形状名不带引号）。</summary>
    internal static AstNode QuotedSimple(string key, string value)
        => new()
        {
            Type = NodeType.Simple,
            Key = key,
            Value = value,
            IsQuoted = true,
            OriginalLayout = OriginalLayout.SingleLine
        };

    internal static AstNode Block(string key, List<AstNode> children)
        => new()
        {
            Type = NodeType.Block,
            Key = key,
            Children = children,
            OriginalLayout = OriginalLayout.MultiLine
        };

    private static AstNode IntRangeBlock(string key, IntRange range)
        => Block(key, new List<AstNode> { Simple("min", range.Min), Simple("max", range.Max) });

    private static AstNode DoubleRangeBlock(string key, RangeValue range)
        => Block(key, new List<AstNode> { Simple("min", range.Min), Simple("max", range.Max) });

    private static AstNode DoubleListBlock(string key, List<double> values)
        => Block(key, values.Select(v => (AstNode)Simple(null!, v)).ToList());

    private static AstNode ClusterBlock(ClusterSettings c)
    {
        var children = new List<AstNode>
        {
            QuotedSimple("method", c.Method),
            Simple("value", c.Value)
        };
        if (c.Max.HasValue)
            children.Add(Simple("max", c.Max.Value));
        return Block("cluster_count", children);
    }

    private static AstNode PartitionBlock(string key, PartitionSettings p)
        => Block(key, new List<AstNode>
        {
            Simple("max_systems", p.MaxSystems),
            Simple("min_systems", p.MinSystems),
            Simple("min_bridges", p.MinBridges),
            Simple("max_bridges", p.MaxBridges),
            QuotedSimple("method", p.Method)
        });

    private static string FormatDouble(double value, int precision)
    {
        if (precision <= 0)
            return ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        string fmt = "0." + new string('0', precision);
        return value.ToString(fmt, CultureInfo.InvariantCulture);
    }
}
