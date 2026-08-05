// 文件: Stellaris.Engine/ImageAsset/ImageSize.cs

using System;

namespace Stellaris.Engine.ImageAsset;

/// <summary>
/// 图像尺寸（宽度、高度）
/// </summary>
public readonly struct ImageSize : IEquatable<ImageSize>
{
    public int Width { get; }
    public int Height { get; }

    public ImageSize(int width, int height)
    {
        // 修正6：禁止零或负尺寸
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "宽度必须大于0");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "高度必须大于0");
        Width = width;
        Height = height;
    }

    public bool Equals(ImageSize other) => Width == other.Width && Height == other.Height;
    public override bool Equals(object? obj) => obj is ImageSize other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Width, Height);
    public static bool operator ==(ImageSize left, ImageSize right) => left.Equals(right);
    public static bool operator !=(ImageSize left, ImageSize right) => !left.Equals(right);
    public void Deconstruct(out int width, out int height) { width = Width; height = Height; }
}