// 文件: Stellaris.Engine/GalaxyStyle/GalaxyStyleOptions.cs

using System;
using System.Collections.Generic;

namespace Stellaris.Engine.GalaxyStyle;

public sealed class StarPreset
{
    public byte R, G, B, A;
    public byte GlowR, GlowG, GlowB, GlowA;
    public int Weight;

    public StarPreset(byte r, byte g, byte b, byte a, byte gr, byte gg, byte gb, byte ga, int weight)
    {
        R = r; G = g; B = b; A = a;
        GlowR = gr; GlowG = gg; GlowB = gb; GlowA = ga;
        Weight = weight;
    }
}

public sealed class PreviewOptions
{
    public int? OuterWidth { get; set; }
    public int? OuterHeight { get; set; }
    public int? InnerWidth { get; set; }
    public int? InnerHeight { get; set; }
    public byte[]? BackgroundColor { get; set; }
    public bool? GlowArms { get; set; }
    public bool? GlowCore { get; set; }
    public byte[]? CoreColor { get; set; }

    // ★★★ 修改点：键类型从 StarType 改为 string ★★★
    public Dictionary<string, StarPreset>? StarPresets { get; set; }

    public double? BgStarDensity { get; set; }
    public double? FillDensity { get; set; }

    public static PreviewOptions Default => new()
    {
        OuterWidth = 562,
        OuterHeight = 236,
        InnerWidth = 200,
        InnerHeight = 200,
        BackgroundColor = new byte[] { 0, 0, 0, 255 },
        GlowArms = true,
        GlowCore = true,
        CoreColor = new byte[] { 255, 255, 255, 255 },

        // ★★★ 修改点：键名改为字符串，与规范示例完全一致 ★★★
        StarPresets = new Dictionary<string, StarPreset>
        {
            ["wolf_rayet"] = new(59, 40, 204, 255, 59, 40, 204, 255, 1),
            ["blue_supergiant"] = new(58, 117, 196, 255, 58, 117, 196, 255, 4),
            ["white_dwarf"] = new(248, 249, 250, 255, 248, 249, 250, 255, 0),
            ["orange_giant"] = new(255, 127, 0, 255, 255, 127, 0, 255, 65),
            ["red_supergiant"] = new(178, 34, 34, 255, 178, 34, 34, 255, 25),
            ["blackhole"] = new(0, 0, 0, 255, 255, 255, 255, 255, 5)
        },

        BgStarDensity = 0.20,
        FillDensity = 0.25
    };

    public PreviewOptions Merge(PreviewOptions? external)
    {
        if (external == null) return this;
        var m = (PreviewOptions)MemberwiseClone();
        if (external.OuterWidth.HasValue) m.OuterWidth = external.OuterWidth;
        if (external.OuterHeight.HasValue) m.OuterHeight = external.OuterHeight;
        if (external.InnerWidth.HasValue) m.InnerWidth = external.InnerWidth;
        if (external.InnerHeight.HasValue) m.InnerHeight = external.InnerHeight;
        if (external.BackgroundColor != null) m.BackgroundColor = (byte[])external.BackgroundColor.Clone();
        if (external.GlowArms.HasValue) m.GlowArms = external.GlowArms;
        if (external.GlowCore.HasValue) m.GlowCore = external.GlowCore;
        if (external.CoreColor != null) m.CoreColor = (byte[])external.CoreColor.Clone();
        if (external.StarPresets != null) m.StarPresets = new Dictionary<string, StarPreset>(external.StarPresets);
        if (external.BgStarDensity.HasValue) m.BgStarDensity = external.BgStarDensity;
        if (external.FillDensity.HasValue) m.FillDensity = external.FillDensity;
        return m;
    }
}

public sealed class IconOptions
{
    public int? FrameWidth { get; set; }
    public int? FrameHeight { get; set; }
    public int? InnerWidth { get; set; }
    public int? InnerHeight { get; set; }
    public int? GlowRadius { get; set; }
    public byte[]? NormalColor { get; set; }
    public byte[]? HighlightColor { get; set; }
    public byte[]? PressedColor { get; set; }

    public static IconOptions Default => new()
    {
        FrameWidth = 110,
        FrameHeight = 59,
        InnerWidth = 35,
        InnerHeight = 35,
        GlowRadius = 9,
        NormalColor = new byte[] { 13, 200, 167, 255 },
        HighlightColor = new byte[] { 249, 161, 50, 255 },
        PressedColor = new byte[] { 108, 255, 224, 255 }
    };

    public IconOptions Merge(IconOptions? external)
    {
        if (external == null) return this;
        var m = (IconOptions)MemberwiseClone();
        if (external.FrameWidth.HasValue) m.FrameWidth = external.FrameWidth;
        if (external.FrameHeight.HasValue) m.FrameHeight = external.FrameHeight;
        if (external.InnerWidth.HasValue) m.InnerWidth = external.InnerWidth;
        if (external.InnerHeight.HasValue) m.InnerHeight = external.InnerHeight;
        if (external.GlowRadius.HasValue) m.GlowRadius = external.GlowRadius;
        if (external.NormalColor != null) m.NormalColor = (byte[])external.NormalColor.Clone();
        if (external.HighlightColor != null) m.HighlightColor = (byte[])external.HighlightColor.Clone();
        if (external.PressedColor != null) m.PressedColor = (byte[])external.PressedColor.Clone();
        return m;
    }
}