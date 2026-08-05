// 文件: Stellaris.Engine/ImageAsset/PixelSet.cs

using System;

namespace Stellaris.Engine.ImageAsset;

/// <summary>
/// 像素集合：三维字节数组 [height, width, channels]
/// </summary>
public sealed class PixelSet : IDisposable
{
    private bool _disposed;
    public int Height { get; }
    public int Width { get; }
    public int Channels { get; }
    public byte[][][] Data { get; }

    public PixelSet(byte[][][] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (data.Length == 0) throw new ArgumentException("空高度维度");

        Height = data.Length;
        Width = data[0].Length;
        Channels = data[0][0].Length;

        // 修正6：验证通道数必须为3或4
        if (Channels != 3 && Channels != 4)
            throw new ArgumentException($"无效通道数: {Channels}。必须为3 (RGB) 或4 (RGBA)。");

        // 修正6：验证每行宽度一致、每行通道数一致
        for (int y = 0; y < Height; y++)
        {
            if (data[y].Length != Width)
                throw new ArgumentException($"第 {y} 行宽度 ({data[y].Length}) 与首行宽度 ({Width}) 不一致");
            for (int x = 0; x < Width; x++)
            {
                if (data[y][x].Length != Channels)
                    throw new ArgumentException($"第 {y} 行第 {x} 列通道数 ({data[y][x].Length}) 与预期 ({Channels}) 不一致");
            }
        }

        Data = data;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    public PixelSet Clone()
    {
        var newData = new byte[Height][][];
        for (int y = 0; y < Height; y++)
        {
            newData[y] = new byte[Width][];
            for (int x = 0; x < Width; x++)
            {
                newData[y][x] = (byte[])Data[y][x].Clone();
            }
        }
        return new PixelSet(newData);
    }
}