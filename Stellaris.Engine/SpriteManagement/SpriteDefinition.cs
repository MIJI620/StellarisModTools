// 文件: Stellaris.Engine/SpriteManagement/SpriteDefinition.cs
using System;
using System.Collections.Generic;
using Stellaris.Engine.ImageAsset;
using Stellaris.Parser;

namespace Stellaris.Engine.SpriteManagement;

/// <summary>
/// 表示一个 spriteType 条目的完整信息（内存索引）。
/// </summary>
public class SpriteDefinition
{
    /// <summary>spriteType 的 name 字段值（唯一键）</summary>
    public string Name { get; }

    /// <summary>texturefile 字段值（.dds 相对路径）</summary>
    public string TextureFile { get; }

    /// <summary>noOfFrames 字段值，若不存在则为 null（视为 1）</summary>
    public int? NoOfFrames { get; }

    /// <summary>该定义所在的 .gfx 文件相对路径</summary>
    public string SourceFile { get; }

    /// <summary>该定义所在的 .gfx 文件所属根目录（相对路径 + 所属目录，供保存时告知底层双写）</summary>
    public string? SourceRoot { get; }

    /// <summary>额外子节点（非标准字段、Block、List 等）</summary>
    public IReadOnlyList<AstNode>? AdditionalChildren { get; }

    public SpriteDefinition(string name, string textureFile, int? noOfFrames, string sourceFile, string? sourceRoot = null)
        : this(name, textureFile, noOfFrames, sourceFile, null, sourceRoot)
    {
    }

    public SpriteDefinition(string name, string textureFile, int? noOfFrames, string sourceFile, IReadOnlyList<AstNode>? additionalChildren, string? sourceRoot = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        TextureFile = textureFile ?? throw new ArgumentNullException(nameof(textureFile));
        NoOfFrames = noOfFrames;
        SourceFile = sourceFile ?? throw new ArgumentNullException(nameof(sourceFile));
        SourceRoot = sourceRoot;
        AdditionalChildren = additionalChildren;
    }

    /// <summary>获取有效帧数（若 NoOfFrames 有值则返回，否则返回 1）</summary>
    public int GetEffectiveFrameCount() => NoOfFrames ?? 1;
}

/// <summary>
/// 表示切分后的一帧图像数据。
/// 实现 IDisposable 以释放内部的 PixelSet 资源。
/// </summary>
public sealed class SpriteFrame : IDisposable
{
    /// <summary>帧索引（从 0 开始）</summary>
    public int Index { get; }

    /// <summary>该帧的像素集合（RGBA，若源无 Alpha 则 Alpha=255）</summary>
    public PixelSet PixelData { get; }

    /// <summary>帧宽度</summary>
    public int Width { get; }

    /// <summary>帧高度</summary>
    public int Height { get; }

    private bool _disposed;

    public SpriteFrame(int index, PixelSet pixelData)
    {
        if (pixelData == null)
            throw new ArgumentNullException(nameof(pixelData));

        Index = index;
        PixelData = pixelData;
        Width = pixelData.Width;
        Height = pixelData.Height;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PixelData?.Dispose();
    }
}

/// <summary>
/// 全局查询结果，包含所有帧数据。
/// 实现 IDisposable 以释放内部的所有 SpriteFrame。
/// </summary>
public sealed class SpriteQueryResult : IDisposable
{
    /// <summary>是否找到匹配项</summary>
    public bool Found { get; }

    /// <summary>查询的键</summary>
    public string Name { get; }

    /// <summary>找到的 .gfx 文件路径（若 Found 为 true）</summary>
    public string? SourceFile { get; }

    /// <summary>对应的 .dds 文件路径（若 Found 为 true）</summary>
    public string? TextureFile { get; }

    /// <summary>实际帧数（至少为 1）</summary>
    public int FrameCount { get; }

    /// <summary>帧列表，按索引升序排列</summary>
    public IReadOnlyList<SpriteFrame> Frames { get; }

    /// <summary>额外子节点（与 SpriteDefinition.AdditionalChildren 同一引用）</summary>
    public IReadOnlyList<AstNode>? AdditionalChildren { get; }

    private bool _disposed;

    private SpriteQueryResult(bool found, string name, string? sourceFile, string? textureFile,
                              int frameCount, IReadOnlyList<SpriteFrame> frames, IReadOnlyList<AstNode>? additionalChildren)
    {
        Found = found;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        SourceFile = sourceFile;
        TextureFile = textureFile;
        FrameCount = frameCount;
        Frames = frames ?? Array.Empty<SpriteFrame>();
        AdditionalChildren = additionalChildren;
    }

    /// <summary>创建未找到的查询结果</summary>
    public static SpriteQueryResult NotFound(string name)
    {
        return new SpriteQueryResult(false, name, null, null, 0, Array.Empty<SpriteFrame>(), null);
    }

    /// <summary>创建成功的查询结果</summary>
    public static SpriteQueryResult Success(string name, string sourceFile, string textureFile,
                                            IReadOnlyList<SpriteFrame> frames, IReadOnlyList<AstNode>? additionalChildren = null)
    {
        if (string.IsNullOrEmpty(sourceFile))
            throw new ArgumentException("源文件路径不能为空", nameof(sourceFile));
        if (string.IsNullOrEmpty(textureFile))
            throw new ArgumentException("纹理文件路径不能为空", nameof(textureFile));
        if (frames == null || frames.Count == 0)
            throw new ArgumentException("帧列表不能为空", nameof(frames));

        return new SpriteQueryResult(true, name, sourceFile, textureFile, frames.Count, frames, additionalChildren);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var frame in Frames)
            frame?.Dispose();
    }
}