// 文件: Stellaris.Engine/GalaxyStyle/GalaxyStyleTypes.cs

using System;
using System.Collections.Generic;
using Stellaris.Engine.ImageAsset;

namespace Stellaris.Engine.GalaxyStyle;

/// <summary>
/// 恒星类型（用于颜色映射和权重分配）
/// </summary>
public enum StarType
{
    WolfRayet,
    BlueSupergiant,
    WhiteDwarf,
    OrangeGiant,
    RedSupergiant,
    BlackHole
}

/// <summary>
/// 恒星颜色映射表（RGB 分量，0-255）
/// </summary>
public static class StarColorMap
{
    public static IReadOnlyDictionary<StarType, (byte R, byte G, byte B)> Colors { get; } =
        new Dictionary<StarType, (byte, byte, byte)>
        {
            [StarType.WolfRayet] = (59, 40, 204),
            [StarType.BlueSupergiant] = (58, 117, 196),
            [StarType.WhiteDwarf] = (248, 249, 250),
            [StarType.OrangeGiant] = (255, 127, 0),
            [StarType.RedSupergiant] = (178, 34, 34),
            [StarType.BlackHole] = (0, 0, 0)
        };

    /// <summary>
    /// 黑洞的光晕颜色（白色）
    /// </summary>
    public static (byte R, byte G, byte B) BlackHoleGlowColor => (255, 255, 255);
}

/// <summary>
/// 星系形状参数，对应 galaxy_shapes.txt 中单个样式块的内容
/// </summary>
public sealed class GalaxyShapeParameters
{
    public double CoreRadiusPerc { get; set; } = 0.2;
    public double StarsMinDist { get; set; } = 8.0;
    public double NumStarsCorePerc { get; set; } = 0.0;

    public int CountriesIdealDist { get; set; } = 5625;
    public int CountriesMinDist { get; set; } = 900;
    public int FallenIdealDist { get; set; } = 15625;
    public int FallenMinDist { get; set; } = 2500;

    public int NumArms { get; set; } = 0;
    public double Tightness { get; set; } = 0.2;
    public double WidthDeg { get; set; } = 30.0;
    public double Fuzz { get; set; } = 10.0;      // 保留兼容，实际渲染未使用
    public double ArmAngleDeg { get; set; } = 180.0;

    public bool HasRing { get; set; } = false;
    public double RingWidth { get; set; } = 0.5;
    public double RingOffset { get; set; } = 0.3;

    public string PreviewIcon { get; set; } = string.Empty;
    public string ButtonIcon { get; set; } = string.Empty;
    public string DescKey { get; set; } = string.Empty;

    /// <summary>
    /// 原始输入映射：参数路径（如 "core_radius_perc"、"countries.ideal_sq_dist_between"、
    /// "preview_icon"）-> 用户输入的原始文本（常量引用 "@foo" / "@[foo + 1]" 等）。
    /// 引擎内部求值后的实际值保存在对应强类型属性中（渲染/计算使用）；
    /// 序列化写回时优先使用此处的原文，将 "@" 引用原样填回 galaxy_shapes.txt。
    /// </summary>
    public Dictionary<string, string> RawInputs { get; internal set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 深拷贝（含 RawInputs 字典——MemberwiseClone 会共享同一字典实例，必须换新字典）
    /// </summary>
    public GalaxyShapeParameters Clone()
    {
        var clone = (GalaxyShapeParameters)MemberwiseClone();
        clone.RawInputs = new Dictionary<string, string>(RawInputs, StringComparer.Ordinal);
        return clone;
    }
}

/// <summary>
/// 内存样式定义（样式表条目）
/// </summary>
public sealed class GalaxyStyleDefinition
{
    public string Name { get; }
    public GalaxyShapeParameters Parameters { get; private set; }

    // 本地化字段（由引擎从 SA 读取时填充，只读）
    public string LocalisedName { get; internal set; } = string.Empty;
    public string LocalisedDescription { get; internal set; } = string.Empty;

    public GalaxyStyleDefinition(string name, GalaxyShapeParameters parameters)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Parameters = parameters?.Clone() ?? throw new ArgumentNullException(nameof(parameters));
    }

    /// <summary>
    /// 更新参数（内部使用，保证线程安全由调用方保证）
    /// </summary>
    internal void UpdateParameters(GalaxyShapeParameters newParams)
    {
        Parameters = newParams?.Clone() ?? throw new ArgumentNullException(nameof(newParams));
    }
}