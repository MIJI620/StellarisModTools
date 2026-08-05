// 文件: Stellaris.Engine/GalaxyMap/GalaxyMapTypes.cs
// 银河地图引擎核心数据结构（规范第二章）。

using System;
using System.Collections.Generic;
using System.Linq;
using Stellaris.Parser;

namespace Stellaris.Engine.GalaxyMap;

/// <summary>场景类型：动态地图 / 静态地图</summary>
public enum ScenarioKind
{
    Dynamic,
    Static
}

/// <summary>数值范围（min / max）</summary>
public readonly record struct RangeValue(double Min, double Max);

/// <summary>整数范围（min / max）</summary>
public readonly record struct IntRange(int Min, int Max);

/// <summary>
/// 分区设置（home_system_partitions / open_space_partitions，规范 2.1）。
/// </summary>
public sealed class PartitionSettings
{
    public int MaxSystems { get; set; } = 15;
    public int MinSystems { get; set; } = 8;
    public int MinBridges { get; set; } = 2;
    public int MaxBridges { get; set; } = 4;
    public string Method { get; set; } = "breadth_first"; // breadth_first | depth_first

    public PartitionSettings Clone() => (PartitionSettings)MemberwiseClone();
}

/// <summary>星团设置（cluster_count，规范 2.1）。</summary>
public sealed class ClusterSettings
{
    public string Method { get; set; } = "one_every_x_empire"; // one_every_x_empire | constant
    public int Value { get; set; } = 1;
    public int? Max { get; set; } // null 表示由引擎自动计算

    public ClusterSettings Clone() => (ClusterSettings)MemberwiseClone();
}

/// <summary>系统坐标：每个轴可为固定值或 { min, max } 随机范围块（规范 2.2）。</summary>
public sealed class SystemPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public bool XIsRange { get; set; }
    public bool YIsRange { get; set; }
    public bool ZIsRange { get; set; }

    public (double Min, double Max) XRange { get; set; }
    public (double Min, double Max) YRange { get; set; }
    public (double Min, double Max) ZRange { get; set; }

    private static readonly Random Rng = new();

    /// <summary>取 X：固定值或随机范围中随机取（规范 2.2：保留范围块，使用时随机取）。</summary>
    public double GetX() => XIsRange ? NextRange(XRange) : X;
    public double GetY() => YIsRange ? NextRange(YRange) : Y;
    public double GetZ() => ZIsRange ? NextRange(ZRange) : Z;

    private static double NextRange((double Min, double Max) r)
        => r.Min + (r.Max - r.Min) * Rng.NextDouble();

    public SystemPosition Clone() => (SystemPosition)MemberwiseClone();
}

/// <summary>超空间航道（add_hyperlane / prevent_hyperlane，仅含 ID 引用，无坐标）。</summary>
public sealed class Hyperlane
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;

    public Hyperlane() { }
    public Hyperlane(string from, string to) { From = from; To = to; }
    public Hyperlane Clone() => new(From, To);
}

/// <summary>星云定义（nebula 块，规范 2.2）。</summary>
public sealed class NebulaDef
{
    public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Radius { get; set; } = 60;

    public NebulaDef Clone() => (NebulaDef)MemberwiseClone();
}

/// <summary>单轴坐标变换（Add→Sub→Mul→Div，规范 4.4a）。</summary>
public sealed class AxisTransform
{
    public double Add { get; set; }
    public double Sub { get; set; }
    public double Mul { get; set; } = 1.0;
    public double Div { get; set; } = 1.0;

    public double Apply(double v)
        => (v + Add - Sub) * Mul / Div;

    public bool HasAny => Add != 0 || Sub != 0 || Mul != 1 || Div != 1;

    public AxisTransform Clone() => (AxisTransform)MemberwiseClone();
}

/// <summary>坐标变换规则（coordinate_transform 块，X/Y/Z 独立计算）。</summary>
public sealed class CoordinateTransform
{
    public AxisTransform X { get; } = new();
    public AxisTransform Y { get; } = new();
    public AxisTransform Z { get; } = new();

    public bool HasAny => X.HasAny || Y.HasAny || Z.HasAny;

    public CoordinateTransform Clone()
    {
        var c = new CoordinateTransform();
        CopyTo(c);
        return c;
    }

    private void CopyTo(CoordinateTransform target)
    {
        target.X.Add = X.Add; target.X.Sub = X.Sub; target.X.Mul = X.Mul; target.X.Div = X.Div;
        target.Y.Add = Y.Add; target.Y.Sub = Y.Sub; target.Y.Mul = Y.Mul; target.Y.Div = Y.Div;
        target.Z.Add = Z.Add; target.Z.Sub = Z.Sub; target.Z.Mul = Z.Mul; target.Z.Div = Z.Div;
    }
}

/// <summary>系统条目（system 块，规范 2.2）。</summary>
public sealed class SystemEntry
{
    public string Id { get; set; } = "0";
    public string Name { get; set; } = string.Empty;
    public SystemPosition Position { get; set; } = new();
    public string? Initializer { get; set; }
    public string? SpawnDesign { get; set; }

    /// <summary>spawn_weight 原始块（保留解析时的 AST 子树，保存时还原）。</summary>
    public AstNode? SpawnWeight { get; set; }

    /// <summary>effect 原始块（保留解析时的 AST 子树，保存时还原）。</summary>
    public AstNode? Effect { get; set; }

    public string Category { get; set; } = "normal";

    public SystemEntry Clone()
    {
        return new SystemEntry
        {
            Id = Id,
            Name = Name,
            Position = Position.Clone(),
            Initializer = Initializer,
            SpawnDesign = SpawnDesign,
            SpawnWeight = SpawnWeight != null ? DeepCloneAst(SpawnWeight) : null,
            Effect = Effect != null ? DeepCloneAst(Effect) : null,
            Category = Category
        };
    }

    /// <summary>深拷贝 AST 子树（避免共享可变节点）。</summary>
    internal static AstNode DeepCloneAst(AstNode node)
    {
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
            clone.Children.Add(DeepCloneAst(child));
        foreach (var comment in node.AssociatedComments)
            clone.AssociatedComments.Add(DeepCloneAst(comment));
        return clone;
    }
}

/// <summary>动态地图参数（setup_scenario 块，规范 2.1）。</summary>
public sealed class DynamicScenario
{
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; }

    // ---- 顶层 ----
    public int NumStars { get; set; } = 200;
    public int Radius { get; set; } = 200;

    // ---- 帝国 ----
    public IntRange NumEmpires { get; set; } = new(0, 10);
    public int NumEmpireDefault { get; set; } = 5;
    public int AdvancedEmpireDefault { get; set; }
    public int FallenEmpireDefault { get; set; }
    public int FallenEmpireMax { get; set; } = 6;
    public int MarauderEmpireDefault { get; set; }
    public int MarauderEmpireMax { get; set; } = 3;
    public int NomadEmpireDefault { get; set; }
    public int NomadEmpireMax { get; set; } = 3;

    // ---- 行星与危机 ----
    public double ColonizablePlanetOdds { get; set; } = 1.0;
    public double PrimitiveOdds { get; set; } = 1.0;
    public double CrisisStrength { get; set; } = 1.0;
    public List<double> ExtraCrisisStrength { get; set; } = new();

    // ---- 星云 ----
    public int NumNebulas { get; set; } = 2;
    public int NebulaSize { get; set; } = 60;
    public int NebulaMinDist { get; set; } = 100;

    // ---- 虫洞、星门、航道 ----
    public IntRange NumWormholePairs { get; set; } = new(0, 5);
    public int NumWormholePairsDefault { get; set; } = 1;
    public IntRange NumGateways { get; set; } = new(0, 5);
    public int NumGatewaysDefault { get; set; } = 1;
    public RangeValue NumHyperlanes { get; set; } = new(0.5, 3.0);
    public double NumHyperlanesDefault { get; set; } = 1.0;

    // ---- 星系生成 ----
    public ClusterSettings ClusterCount { get; set; } = new();
    public int? ClusterRadius { get; set; }
    public int? ClusterDistanceFromCore { get; set; }
    public int MaxHyperlaneDistance { get; set; } = 50;
    public PartitionSettings HomeSystemPartitions { get; set; } = new();
    public PartitionSettings OpenSpacePartitions { get; set; } = new();

    // ---- 支持形状 ----
    public List<string> SupportedShapes { get; set; } = new();
    /// <summary>锁定本地化：保存/规整化时不动该地图的本地化键（不迁移、不复制关联文件）。</summary>
    public bool LockLocalisation { get; set; }

    /// <summary>清空本地化：保存时将该地图的本地化键从文件中移除。</summary>
    public bool ClearFile { get; set; }

    public DynamicScenario Clone()
    {
        var c = (DynamicScenario)MemberwiseClone();
        c.ExtraCrisisStrength = new List<double>(ExtraCrisisStrength);
        c.ClusterCount = ClusterCount.Clone();
        c.HomeSystemPartitions = HomeSystemPartitions.Clone();
        c.OpenSpacePartitions = OpenSpacePartitions.Clone();
        c.SupportedShapes = new List<string>(SupportedShapes);
        return c;
    }
}

/// <summary>静态地图参数（static_galaxy_scenario 块，规范 2.2）。</summary>
public sealed class StaticScenario
{
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; }

    // ---- 与动态地图共有的字段（帝国、行星、危机等）----
    public IntRange NumEmpires { get; set; } = new(0, 10);
    public int NumEmpireDefault { get; set; } = 5;
    public int AdvancedEmpireDefault { get; set; }
    public int FallenEmpireDefault { get; set; }
    public int FallenEmpireMax { get; set; } = 6;
    public int MarauderEmpireDefault { get; set; }
    public int MarauderEmpireMax { get; set; } = 3;
    public int NomadEmpireDefault { get; set; }
    public int NomadEmpireMax { get; set; } = 3;
    public double ColonizablePlanetOdds { get; set; } = 1.0;
    public double PrimitiveOdds { get; set; } = 1.0;
    public double CrisisStrength { get; set; } = 1.0;
    public List<double> ExtraCrisisStrength { get; set; } = new();
    public int NumWormholePairsDefault { get; set; } = 1;
    public IntRange NumWormholePairs { get; set; } = new(0, 10);   // 照抄原版 static_galaxy_example { min=0 max=10 }
    public int NumGatewaysDefault { get; set; } = 1;
    public IntRange NumGateways { get; set; } = new(0, 10);
    public bool RandomHyperlanes { get; set; }

    // ---- 布局 ----
    public CoordinateTransform Transform { get; } = new();
    public List<SystemEntry> Systems { get; set; } = new();
    public List<Hyperlane> Hyperlanes { get; set; } = new();
    public List<Hyperlane> PreventedHyperlanes { get; set; } = new();
    public List<NebulaDef> Nebulas { get; set; } = new();

    // ---- 支持形状（与动态地图一致，作为样式排序基准）----
    public List<string> SupportedShapes { get; set; } = new();
    /// <summary>锁定本地化：保存/规整化时不动该地图的本地化键（不迁移、不复制关联文件）。</summary>
    public bool LockLocalisation { get; set; }

    /// <summary>清空本地化：保存时将该地图的本地化键从文件中移除。</summary>
    public bool ClearFile { get; set; }

    /// <summary>绑定的样式名（工具内部关联：保存时该样式的图标/预览用本图点集；不写入场景文件，映射写 galaxy.json）。</summary>
    public string? BoundStyleName { get; set; }

    /// <summary>伪样式核心半径比例（0~1；影响静态地图对应样式的核心半径，仅内存 + galaxy.json 映射）。</summary>
    public double CoreRadiusPerc { get; set; } = 0.2;

    public StaticScenario Clone()
    {
        var c = (StaticScenario)MemberwiseClone();
        c.ExtraCrisisStrength = new List<double>(ExtraCrisisStrength);
        c.Systems = new List<SystemEntry>(Systems.Select(s => s.Clone()));
        c.Hyperlanes = new List<Hyperlane>(Hyperlanes.Select(h => h.Clone()));
        c.PreventedHyperlanes = new List<Hyperlane>(PreventedHyperlanes.Select(h => h.Clone()));
        c.Nebulas = new List<NebulaDef>(Nebulas.Select(n => n.Clone()));
        c.SupportedShapes = new List<string>(SupportedShapes);
        return c;
    }
}

/// <summary>伪样式（规范 2.6 / 4.5）：合法样式占位，静态侧全权管理。</summary>
public sealed class PseudoStyle
{
    public string Name { get; set; } = string.Empty;
    public double CoreRadiusPerc { get; set; } = 0.2;

    // 自动生成（遵循 GalaxyStyle 规范 14.5 路径格式）
    public string PreviewIcon { get; set; } = string.Empty;
    public string ButtonIcon { get; set; } = string.Empty;
    public string DescKey { get; set; } = string.Empty;

    // 由引擎根据散点分布自动计算（用户不可修改）
    public double NumStarsCorePerc { get; set; }
    public double StarsMinDist { get; set; } = 10.0;

    public PseudoStyle Clone()
    {
        var c = (PseudoStyle)MemberwiseClone();
        return c;
    }
}

/// <summary>图像转点阵参数（规范 2.4 / 第五章）。</summary>
public enum LayerSelection
{
    None,       // 默认：全部图层（R、G、B 平均，A 乘数）
    R, G, B, A,
    InverseR, InverseG, InverseB, InverseA
}

public enum CompositeMode
{
    Add,
    Multiply,
    Average
}

public enum GenerationMode
{
    Spacing,    // 按最小间距
    Count       // 按总点数
}

public sealed class ImageGenerationOptions
{
    public LayerSelection Layer { get; set; } = LayerSelection.None;
    public bool Composite { get; set; }
    public CompositeMode CompositeMode { get; set; } = CompositeMode.Add;
    public double Threshold { get; set; }
    public double Gamma { get; set; } = 1.0;
    public GenerationMode Mode { get; set; } = GenerationMode.Spacing;
    public double MinDistance { get; set; } = 10.0;
    public int TotalCount { get; set; } = 100;
    public int MaxAttempts { get; set; } = 10000;
    // ARGB 任意组合（用户要求：选 2/3/4 个通道均可）+ 反向 + 密度%
    public bool UseR { get; set; } = true;
    public bool UseG { get; set; } = true;
    public bool UseB { get; set; } = true;
    public bool UseA { get; set; } = true;
    public bool Invert { get; set; }
    public double Density { get; set; } = 1.0;   // 0~1：候选点保留比例（网格×百分比）
    public double TargetWidth { get; set; }      // 生成范围宽（地图坐标；0 = 回退全图 1000）
    public double TargetHeight { get; set; }     // 生成范围高（地图坐标；0 = 回退全图 1000）
    public double CenterX { get; set; }          // 生成中心 X（图像所在位置；默认 0）
    public double CenterY { get; set; }          // 生成中心 Y

    public static ImageGenerationOptions Default => new();
    public ImageGenerationOptions Clone() => (ImageGenerationOptions)MemberwiseClone();
}

/// <summary>网格生成参数（规范 2.5 / 4.8）。</summary>
public enum LatticeShape
{
    Triangle,
    Square,
    Hexagon
}

public sealed class LatticeGenerationOptions
{
    public LatticeShape ShapeType { get; set; } = LatticeShape.Hexagon;
    public double SideLength { get; set; } = 100.0;
    public double Spacing { get; set; } = 10.0;
    public double CenterX { get; set; }
    public double CenterY { get; set; }

    public LatticeGenerationOptions Clone() => (LatticeGenerationOptions)MemberwiseClone();
}
