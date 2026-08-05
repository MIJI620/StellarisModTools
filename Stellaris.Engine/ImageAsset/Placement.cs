// 文件: Stellaris.Engine/ImageAsset/Placement.cs

using System;

namespace Stellaris.Engine.ImageAsset;

/// <summary>
/// 叠加放置区域
/// </summary>
public readonly struct Placement
{
    public int Index { get; }
    public int Left { get; }
    public int Top { get; }
    public int Right { get; }
    public int Bottom { get; }

    public Placement(int index, int left, int top, int right, int bottom)
    {
        // 修正6：验证区域有效性
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "索引必须 ≥ 0");
        if (left < 0) throw new ArgumentOutOfRangeException(nameof(left), "Left 必须 ≥ 0");
        if (top < 0) throw new ArgumentOutOfRangeException(nameof(top), "Top 必须 ≥ 0");
        if (right <= left) throw new ArgumentException($"Right ({right}) 必须大于 Left ({left})");
        if (bottom <= top) throw new ArgumentException($"Bottom ({bottom}) 必须大于 Top ({top})");

        Index = index;
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}