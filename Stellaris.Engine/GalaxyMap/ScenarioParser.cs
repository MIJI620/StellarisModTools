// 文件: Stellaris.Engine/GalaxyMap/ScenarioParser.cs
// AST → 场景对象（规范 2.1 / 2.2 / 4.4a）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stellaris.Parser;

namespace Stellaris.Engine.GalaxyMap;

internal static class ScenarioParser
{
    // ==================== 动态地图 ====================

    public static DynamicScenario ParseDynamic(AstNode block, string fileName)
    {
        var s = new DynamicScenario { Name = fileName };

        foreach (var child in block.Children)
        {
            if (child.Key == null) continue;
            switch (child.Type)
            {
                case NodeType.Simple:
                    ApplyDynamicSimple(s, child);
                    break;
                case NodeType.Block:
                case NodeType.List:
                    // 纯数字/列表块（如 extra_crisis_strength = { 10 25 ... }）解析为 List，
                    // 同样按键分派到 ApplyDynamicBlock（内部 DoubleList/IntRange 处理 Children）
                    ApplyDynamicBlock(s, child);
                    break;
            }
        }

        if (s.Name.Length == 0)
            s.Name = fileName;
        return s;
    }

    private static void ApplyDynamicSimple(DynamicScenario s, AstNode child)
    {
        switch (child.Key)
        {
            case "name": s.Name = Text(child); break;
            case "priority": s.Priority = Int(child, 0); break;
            case "num_stars": s.NumStars = Int(child, 200); break;
            case "radius": s.Radius = Int(child, 200); break;
            case "num_empire_default": s.NumEmpireDefault = Int(child, 5); break;
            case "advanced_empire_default": s.AdvancedEmpireDefault = Int(child, 0); break;
            case "fallen_empire_default": s.FallenEmpireDefault = Int(child, 0); break;
            case "fallen_empire_max": s.FallenEmpireMax = Int(child, 6); break;
            case "marauder_empire_default": s.MarauderEmpireDefault = Int(child, 0); break;
            case "marauder_empire_max": s.MarauderEmpireMax = Int(child, 3); break;
            case "nomad_empire_default": s.NomadEmpireDefault = Int(child, 0); break;
            case "nomad_empire_max": s.NomadEmpireMax = Int(child, 3); break;
            case "colonizable_planet_odds": s.ColonizablePlanetOdds = Dbl(child, 1.0); break;
            case "primitive_odds": s.PrimitiveOdds = Dbl(child, 1.0); break;
            case "crisis_strength": s.CrisisStrength = Dbl(child, 1.0); break;
            case "num_nebulas": s.NumNebulas = Int(child, 2); break;
            case "nebula_size": s.NebulaSize = Int(child, 60); break;
            case "nebula_min_dist": s.NebulaMinDist = Int(child, 100); break;
            case "num_wormhole_pairs_default": s.NumWormholePairsDefault = Int(child, 1); break;
            case "num_gateways_default": s.NumGatewaysDefault = Int(child, 1); break;
            case "num_hyperlanes_default": s.NumHyperlanesDefault = Dbl(child, 1.0); break;
            case "cluster_radius": s.ClusterRadius = IntNullable(child); break;
            case "cluster_distance_from_core": s.ClusterDistanceFromCore = IntNullable(child); break;
            case "max_hyperlane_distance": s.MaxHyperlaneDistance = Int(child, 50); break;
            case "supports_shape": s.SupportedShapes.Add(Text(child)); break;
        }
    }

    private static void ApplyDynamicBlock(DynamicScenario s, AstNode child)
    {
        switch (child.Key)
        {
            case "num_empires": s.NumEmpires = IntRange(child); break;
            case "num_wormhole_pairs": s.NumWormholePairs = IntRange(child); break;
            case "num_gateways": s.NumGateways = IntRange(child); break;
            case "num_hyperlanes": s.NumHyperlanes = DoubleRange(child); break;
            case "extra_crisis_strength": s.ExtraCrisisStrength = DoubleList(child); break;
            case "cluster_count": s.ClusterCount = ParseCluster(child); break;
            case "home_system_partitions": s.HomeSystemPartitions = ParsePartitions(child); break;
            case "open_space_partitions": s.OpenSpacePartitions = ParsePartitions(child); break;
        }
    }

    private static ClusterSettings ParseCluster(AstNode block)
    {
        var c = new ClusterSettings();
        foreach (var child in block.Children)
        {
            if (child.Type == NodeType.Simple && child.Key != null)
            {
                switch (child.Key)
                {
                    case "method": c.Method = Text(child); break;
                    case "value": c.Value = Int(child, 1); break;
                    case "max": c.Max = IntNullable(child); break;
                }
            }
        }
        return c;
    }

    private static PartitionSettings ParsePartitions(AstNode block)
    {
        var p = new PartitionSettings();
        foreach (var child in block.Children)
        {
            if (child.Type == NodeType.Simple && child.Key != null)
            {
                switch (child.Key)
                {
                    case "max_systems": p.MaxSystems = Int(child, 15); break;
                    case "min_systems": p.MinSystems = Int(child, 8); break;
                    case "min_bridges": p.MinBridges = Int(child, 2); break;
                    case "max_bridges": p.MaxBridges = Int(child, 4); break;
                    case "method": p.Method = Text(child); break;
                }
            }
        }
        return p;
    }

    // ==================== 静态地图 ====================

    public static StaticScenario ParseStatic(AstNode block, string fileName)
    {
        var s = new StaticScenario { Name = fileName };
        var systems = new List<SystemEntry>();

        foreach (var child in block.Children)
        {
            if (child.Key == null) continue;
            switch (child.Type)
            {
                case NodeType.Simple:
                    ApplyStaticSimple(s, child);
                    // supports_shape 是 Simple 节点（键值对），在此一并解析
                    if (child.Key == "supports_shape")
                        s.SupportedShapes.Add(Text(child));
                    break;
                case NodeType.Block:
                case NodeType.List:
                    // 纯数字/列表块（如 extra_crisis_strength = { 10 25 ... }）解析为 List，
                    // 与 Block 同样按键分派
                    switch (child.Key)
                    {
                        case "num_empires": s.NumEmpires = IntRange(child); break;
                        case "extra_crisis_strength": s.ExtraCrisisStrength = DoubleList(child); break;
                        case "coordinate_transform": ParseTransform(child, s.Transform); break;
                        case "system": systems.Add(ParseSystem(child)); break;
                        case "add_hyperlane": s.Hyperlanes.Add(ParseHyperlane(child)); break;
                        case "prevent_hyperlane": s.PreventedHyperlanes.Add(ParseHyperlane(child)); break;
                        case "nebula": s.Nebulas.Add(ParseNebula(child)); break;
                    }
                    break;
            }
        }

        // 4.4a：加载时将坐标变换应用到所有星系坐标（写入内存，保存时固化）
        if (s.Transform.HasAny)
        {
            foreach (var sys in systems)
                ApplyTransform(sys.Position, s.Transform);
        }

        s.Systems = systems;
        if (s.Name.Length == 0)
            s.Name = fileName;
        return s;
    }

    private static void ApplyStaticSimple(StaticScenario s, AstNode child)
    {
        switch (child.Key)
        {
            case "name": s.Name = Text(child); break;
            case "priority": s.Priority = Int(child, 0); break;
            case "num_empire_default": s.NumEmpireDefault = Int(child, 5); break;
            case "advanced_empire_default": s.AdvancedEmpireDefault = Int(child, 0); break;
            case "fallen_empire_default": s.FallenEmpireDefault = Int(child, 0); break;
            case "fallen_empire_max": s.FallenEmpireMax = Int(child, 6); break;
            case "marauder_empire_default": s.MarauderEmpireDefault = Int(child, 0); break;
            case "marauder_empire_max": s.MarauderEmpireMax = Int(child, 3); break;
            case "nomad_empire_default": s.NomadEmpireDefault = Int(child, 0); break;
            case "nomad_empire_max": s.NomadEmpireMax = Int(child, 3); break;
            case "colonizable_planet_odds": s.ColonizablePlanetOdds = Dbl(child, 1.0); break;
            case "primitive_odds": s.PrimitiveOdds = Dbl(child, 1.0); break;
            case "crisis_strength": s.CrisisStrength = Dbl(child, 1.0); break;
            case "num_wormhole_pairs_default": s.NumWormholePairsDefault = Int(child, 1); break;
            case "num_wormhole_pairs": s.NumWormholePairs = IntRange(child); break;
            case "num_gateways_default": s.NumGatewaysDefault = Int(child, 1); break;
            case "num_gateways": s.NumGateways = IntRange(child); break;
            case "random_hyperlanes": s.RandomHyperlanes = child.Value is bool b
                ? b : string.Equals(child.RawText?.Trim(), "yes", StringComparison.OrdinalIgnoreCase); break;
        }
    }

    private static SystemEntry ParseSystem(AstNode block)
    {
        var entry = new SystemEntry();
        foreach (var child in block.Children)
        {
            if (child.Key == null) continue;
            switch (child.Type)
            {
                case NodeType.Simple:
                    switch (child.Key)
                    {
                        case "id": entry.Id = Text(child); break;
                        case "name": entry.Name = Text(child); break;
                        case "initializer": entry.Initializer = Text(child); break;
                        case "spawn_design": entry.SpawnDesign = Text(child); break;
                        case "category": entry.Category = Text(child); break;
                    }
                    break;
                case NodeType.Block:
                    switch (child.Key)
                    {
                        case "position": ParsePosition(child, entry.Position); break;
                        case "spawn_weight": entry.SpawnWeight = SystemEntry.DeepCloneAst(child); break;
                        case "effect": entry.Effect = SystemEntry.DeepCloneAst(child); break;
                    }
                    break;
            }
        }
        return entry;
    }

    private static void ParsePosition(AstNode block, SystemPosition pos)
    {
        foreach (var child in block.Children)
        {
            if (child.Key == null) continue;
            bool isX, isY;
            switch (child.Key)
            {
                case "x": isX = true; isY = false; break;
                case "y": isX = false; isY = true; break;
                case "z": isX = false; isY = false; break;
                default: continue;
            }

            if (child.Type == NodeType.Simple)
            {
                // 固定值
                double v = Dbl(child, 0);
                SetAxisValue(pos, isX, isY, v, false, (v, v));
            }
            else if (child.Type == NodeType.Block)
            {
                // 随机范围块 { min = .., max = .. }：加载时原样保留（规范 2.2）
                double min = Dbl(child, 0), max = min;
                foreach (var op in child.Children)
                {
                    if (op.Type == NodeType.Simple && op.Key != null)
                    {
                        if (op.Key == "min") min = Dbl(op, min);
                        else if (op.Key == "max") max = Dbl(op, max);
                    }
                }
                SetAxisValue(pos, isX, isY, min, true, (min, max));
            }
        }
    }

    private static void SetAxisValue(SystemPosition pos, bool isX, bool isY,
        double value, bool isRange, (double Min, double Max) range)
    {
        if (isX) { pos.X = value; pos.XIsRange = isRange; pos.XRange = range; }
        else if (isY) { pos.Y = value; pos.YIsRange = isRange; pos.YRange = range; }
        else { pos.Z = value; pos.ZIsRange = isRange; pos.ZRange = range; }
    }

    private static Hyperlane ParseHyperlane(AstNode block)
    {
        var h = new Hyperlane();
        foreach (var child in block.Children)
        {
            if (child.Type == NodeType.Simple && child.Key != null)
            {
                if (child.Key == "from") h.From = Text(child);
                else if (child.Key == "to") h.To = Text(child);
            }
        }
        return h;
    }

    private static NebulaDef ParseNebula(AstNode block)
    {
        var n = new NebulaDef();
        foreach (var child in block.Children)
        {
            if (child.Type != NodeType.Simple || child.Key == null) continue;
            switch (child.Key)
            {
                case "name": n.Name = Text(child); break;
                case "x": n.X = Dbl(child, 0); break;
                case "y": n.Y = Dbl(child, 0); break;
                case "z": n.Z = Dbl(child, 0); break;
                case "radius": n.Radius = Int(child, 60); break;
            }
        }
        return n;
    }

    private static void ParseTransform(AstNode block, CoordinateTransform transform)
    {
        foreach (var axisNode in block.Children)
        {
            if (axisNode.Type != NodeType.Block || axisNode.Key == null) continue;
            var axis = axisNode.Key switch
            {
                "x" => transform.X,
                "y" => transform.Y,
                "z" => transform.Z,
                _ => null
            };
            if (axis == null) continue;

            foreach (var op in axisNode.Children)
            {
                if (op.Type != NodeType.Simple || op.Key == null) continue;
                switch (op.Key)
                {
                    case "add": axis.Add = Dbl(op, 0); break;
                    case "sub": axis.Sub = Dbl(op, 0); break;
                    case "mul": axis.Mul = Dbl(op, 1); break;
                    case "div": axis.Div = Dbl(op, 1); break;
                }
            }
        }
    }

    private static void ApplyTransform(SystemPosition p, CoordinateTransform t)
    {
        if (p.XIsRange) p.XRange = (t.X.Apply(p.XRange.Min), t.X.Apply(p.XRange.Max));
        else p.X = t.X.Apply(p.X);

        if (p.YIsRange) p.YRange = (t.Y.Apply(p.YRange.Min), t.Y.Apply(p.YRange.Max));
        else p.Y = t.Y.Apply(p.Y);

        if (p.ZIsRange) p.ZRange = (t.Z.Apply(p.ZRange.Min), t.Z.Apply(p.ZRange.Max));
        else p.Z = t.Z.Apply(p.Z);
    }

    // ==================== 通用取值辅助 ====================

    private static string Text(AstNode n)
    {
        if (n.Value is ConstantValue) return n.RawText ?? string.Empty;
        return n.Value?.ToString() ?? string.Empty;
    }

    private static int Int(AstNode n, int fallback)
        => TryInt(n.Value, out int v) ? v : fallback;

    private static int? IntNullable(AstNode n)
        => TryInt(n.Value, out int v) ? v : null;

    private static bool TryInt(object? value, out int result)
    {
        if (value is int i) { result = i; return true; }
        if (value is long l) { result = (int)l; return true; }
        if (value is double d && d >= int.MinValue && d <= int.MaxValue) { result = (int)d; return true; }
        result = 0;
        return false;
    }

    private static double Dbl(AstNode n, double fallback)
    {
        if (n.Value is double d) return d;
        if (n.Value is int i) return i;
        if (n.Value is long l) return l;
        if (n.Value is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
        return fallback;
    }

    private static IntRange IntRange(AstNode block)
    {
        int min = 0, max = 0;
        foreach (var child in block.Children)
        {
            if (child.Type == NodeType.Simple && child.Key != null)
            {
                if (child.Key == "min") min = Int(child, min);
                else if (child.Key == "max") max = Int(child, max);
            }
        }
        return new IntRange(min, max);
    }

    private static RangeValue DoubleRange(AstNode block)
    {
        double min = 0.5, max = 3.0;
        foreach (var child in block.Children)
        {
            if (child.Type == NodeType.Simple && child.Key != null)
            {
                if (child.Key == "min") min = Dbl(child, min);
                else if (child.Key == "max") max = Dbl(child, max);
            }
        }
        return new RangeValue(min, max);
    }

    private static List<double> DoubleList(AstNode block)
    {
        var list = new List<double>();
        foreach (var child in block.Children)
        {
            if (child.Type == NodeType.Simple && child.Key == null)
                list.Add(Dbl(child, 0));
        }
        return list;
    }
}
