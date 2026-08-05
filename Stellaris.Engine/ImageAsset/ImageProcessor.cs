// 文件: Stellaris.Engine/ImageAsset/ImageProcessor.cs

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellaris.Engine.ImageAsset;

/// <summary>
/// 图像处理核心（无状态、纯函数），负责变换、旋转、拼接、合成。
/// 线程安全（无共享状态）。
/// </summary>
internal sealed class ImageProcessor
{
    private readonly ILogger _logger;

    // 运行时可变的内存检查开关
    private bool _enableMemoryCheck;

    public ImageProcessor(ILogger? logger = null, bool enableMemoryCheck = true)
    {
        _logger = logger ?? NullLogger.Instance;
        _enableMemoryCheck = enableMemoryCheck;
    }

    /// <summary>
    /// 由引擎调用，动态更新内存检查开关。
    /// </summary>
    internal void SetMemoryCheck(bool enabled)
    {
        _enableMemoryCheck = enabled;
    }

    // ===== Transform =====

    public PixelSet Transform(PixelSet pixelSet, List<TransformOperation> operations, ImageSize? outputSize)
    {
        if (pixelSet == null || operations == null || operations.Count == 0)
            throw new ArgumentException("像素集合或操作列表无效");

        if (outputSize.HasValue && (outputSize.Value.Width <= 0 || outputSize.Value.Height <= 0))
            throw new ArgumentException("outputSize 宽高必须 > 0");

        var current = pixelSet.Clone();
        foreach (var op in operations)
        {
            current = ImageAssetRenderer.ApplyTransform(current, op, outputSize);
            if (current == null)
                throw new InvalidOperationException($"变换 {op} 失败");
        }
        return current;
    }

    // ===== Rotate =====

    public (PixelSet Result, (int X, int Y) Center) Rotate(
        PixelSet pixelSet, double angle, (int X, int Y)? pivot,
        bool autoExpand, byte[]? backgroundColor)
    {
        if (pixelSet == null)
            throw new ArgumentException("像素集合为空");
        if (backgroundColor != null && backgroundColor.Length != 4)
            throw new ArgumentException("backgroundColor 必须为长度为4的数组 (RGBA)");

        angle = angle % 360.0;
        if (angle < 0) angle += 360.0;

        using var bitmap = ImageAssetRenderer.PixelSetToBitmap(pixelSet);
        if (bitmap == null)
            throw new InvalidOperationException("转换为SKBitmap失败");

        float cx = pivot?.X ?? (bitmap.Width / 2f);
        float cy = pivot?.Y ?? (bitmap.Height / 2f);

        var matrix = SKMatrix.CreateRotationDegrees((float)angle, cx, cy);

        SKSizeI newSize;
        float translateX = 0, translateY = 0;
        if (autoExpand)
        {
            var corners = new[]
            {
                matrix.MapPoint(0, 0),
                matrix.MapPoint(bitmap.Width, 0),
                matrix.MapPoint(0, bitmap.Height),
                matrix.MapPoint(bitmap.Width, bitmap.Height)
            };
            float minX = corners.Min(p => p.X);
            float maxX = corners.Max(p => p.X);
            float minY = corners.Min(p => p.Y);
            float maxY = corners.Max(p => p.Y);
            newSize = new SKSizeI((int)Math.Ceiling(maxX - minX), (int)Math.Ceiling(maxY - minY));
            translateX = -minX;
            translateY = -minY;
        }
        else
        {
            newSize = new SKSizeI(bitmap.Width, bitmap.Height);
        }

        // 内存预检（使用当前开关状态）
        if (_enableMemoryCheck)
        {
            if (!TryReserveMemory(newSize.Width, newSize.Height))
                throw new OutOfMemoryException($"旋转后图像内存不足: {newSize.Width}x{newSize.Height}");
        }

        var targetInfo = new SKImageInfo(newSize.Width, newSize.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var target = new SKBitmap(targetInfo);
        using var canvas = new SKCanvas(target);

        if (backgroundColor != null)
            canvas.Clear(new SKColor(backgroundColor[0], backgroundColor[1], backgroundColor[2], backgroundColor[3]));
        else
            canvas.Clear(SKColors.Transparent);

        canvas.Save();
        canvas.Translate(translateX, translateY);
        canvas.RotateDegrees((float)angle, cx, cy);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        canvas.DrawBitmap(bitmap, 0, 0, sampling, null);
        canvas.Restore();

        var origCenter = new SKPoint(cx, cy);
        var rotatedCenter = matrix.MapPoint(origCenter);
        var center = ((int)(rotatedCenter.X + translateX), (int)(rotatedCenter.Y + translateY));

        var result = ImageAssetRenderer.BitmapToPixelSet(target);
        return (result, center);
    }

    // ===== Stitch =====

    public PixelSet Stitch(List<PixelSet> pixelSets, int[][] grid, ImageSize cellSize,
        byte[]? backgroundColor, ImageSize? outputSize)
    {
        if (pixelSets == null || grid == null || grid.Length == 0 || grid[0].Length == 0)
            throw new ArgumentException("无效的像素集合或网格");
        if (cellSize.Width <= 0 || cellSize.Height <= 0)
            throw new ArgumentException("cellSize 宽高必须 > 0");
        if (backgroundColor != null && backgroundColor.Length != 4)
            throw new ArgumentException("backgroundColor 必须为长度为4的数组 (RGBA)");
        if (outputSize.HasValue && (outputSize.Value.Width <= 0 || outputSize.Value.Height <= 0))
            throw new ArgumentException("outputSize 宽高必须 > 0");

        // 验证网格索引
        var validIndices = new HashSet<int>(Enumerable.Range(1, pixelSets.Count));
        foreach (var row in grid)
            foreach (int val in row)
                if (val != 0 && !validIndices.Contains(val))
                    throw new ArgumentException($"网格中包含无效索引: {val}");

        int rows = grid.Length;
        int cols = grid[0].Length;
        int naturalWidth = cols * cellSize.Width;
        int naturalHeight = rows * cellSize.Height;

        // 内存预检（使用当前开关状态）
        if (_enableMemoryCheck)
        {
            if (!TryReserveMemory(naturalWidth, naturalHeight))
                throw new OutOfMemoryException($"拼接图像所需内存不足: {naturalWidth}x{naturalHeight}");
        }

        var canvasInfo = new SKImageInfo(naturalWidth, naturalHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var canvasBitmap = new SKBitmap(canvasInfo);
        using var canvas = new SKCanvas(canvasBitmap);
        if (backgroundColor != null)
            canvas.Clear(new SKColor(backgroundColor[0], backgroundColor[1], backgroundColor[2], backgroundColor[3]));
        else
            canvas.Clear(SKColors.Transparent);

        var regions = TileMerger.ComputeMaximalRectangles(grid, rows, cols);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        foreach (var region in regions)
        {
            int idx = region.Index;
            if (idx <= 0 || idx > pixelSets.Count) continue;
            var src = pixelSets[idx - 1];
            if (src == null) continue;

            int dstX = region.Col * cellSize.Width;
            int dstY = region.Row * cellSize.Height;
            int dstW = region.Width * cellSize.Width;
            int dstH = region.Height * cellSize.Height;

            using var srcBitmap = ImageAssetRenderer.PixelSetToBitmap(src);
            using var scaled = ImageAssetRenderer.ResizeBitmap(srcBitmap, dstW, dstH);
            canvas.DrawBitmap(scaled, dstX, dstY, sampling, null);
        }

        SKBitmap finalBitmap;
        if (outputSize.HasValue)
        {
            // 内存预检（使用当前开关状态）
            if (_enableMemoryCheck)
            {
                if (!TryReserveMemory(outputSize.Value.Width, outputSize.Value.Height))
                    throw new OutOfMemoryException($"输出图像内存不足: {outputSize.Value.Width}x{outputSize.Value.Height}");
            }
            finalBitmap = ImageAssetRenderer.ResizeBitmap(canvasBitmap, outputSize.Value.Width, outputSize.Value.Height);
        }
        else
        {
            finalBitmap = canvasBitmap.Copy();
        }

        return ImageAssetRenderer.BitmapToPixelSet(finalBitmap);
    }

    // ===== Composite =====

    public PixelSet Composite(List<PixelSet> pixelSets, List<Placement> placements,
        byte[]? backgroundColor, ImageSize? outputSize)
    {
        if (pixelSets == null || placements == null || placements.Count == 0)
            throw new ArgumentException("无效的像素集合或放置列表");
        if (backgroundColor != null && backgroundColor.Length != 4)
            throw new ArgumentException("backgroundColor 必须为长度为4的数组 (RGBA)");
        if (outputSize.HasValue && (outputSize.Value.Width <= 0 || outputSize.Value.Height <= 0))
            throw new ArgumentException("outputSize 宽高必须 > 0");

        foreach (var p in placements)
        {
            if (p.Index < 0 || p.Index >= pixelSets.Count)
                throw new ArgumentException($"放置中引用了无效索引: {p.Index}");
        }

        int maxRight = 0, maxBottom = 0;
        foreach (var p in placements)
        {
            if (p.Right > maxRight) maxRight = p.Right;
            if (p.Bottom > maxBottom) maxBottom = p.Bottom;
        }

        int canvasWidth = outputSize?.Width ?? maxRight;
        int canvasHeight = outputSize?.Height ?? maxBottom;

        // 内存预检（使用当前开关状态）
        if (_enableMemoryCheck)
        {
            if (!TryReserveMemory(canvasWidth, canvasHeight))
                throw new OutOfMemoryException($"叠加图像所需内存不足: {canvasWidth}x{canvasHeight}");
        }

        var canvasInfo = new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var canvasBitmap = new SKBitmap(canvasInfo);
        using var canvas = new SKCanvas(canvasBitmap);
        if (backgroundColor != null)
            canvas.Clear(new SKColor(backgroundColor[0], backgroundColor[1], backgroundColor[2], backgroundColor[3]));
        else
            canvas.Clear(SKColors.Transparent);

        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        foreach (var placement in placements)
        {
            int idx = placement.Index;
            var src = pixelSets[idx];
            if (src == null) continue;

            int dstX = placement.Left;
            int dstY = placement.Top;
            int dstW = placement.Width;
            int dstH = placement.Height;

            using var srcBitmap = ImageAssetRenderer.PixelSetToBitmap(src);
            using var scaled = ImageAssetRenderer.ResizeBitmap(srcBitmap, dstW, dstH);
            canvas.DrawBitmap(scaled, dstX, dstY, sampling, null);
        }

        SKBitmap finalBitmap;
        if (outputSize.HasValue && (outputSize.Value.Width != canvasWidth || outputSize.Value.Height != canvasHeight))
        {
            // 内存预检（使用当前开关状态）
            if (_enableMemoryCheck)
            {
                if (!TryReserveMemory(outputSize.Value.Width, outputSize.Value.Height))
                    throw new OutOfMemoryException($"输出图像内存不足: {outputSize.Value.Width}x{outputSize.Value.Height}");
            }
            finalBitmap = ImageAssetRenderer.ResizeBitmap(canvasBitmap, outputSize.Value.Width, outputSize.Value.Height);
        }
        else
        {
            finalBitmap = canvasBitmap.Copy();
        }

        return ImageAssetRenderer.BitmapToPixelSet(finalBitmap);
    }

    private bool TryReserveMemory(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (!_enableMemoryCheck) return true;

        long pixelCount = (long)width * height;
        long threshold1 = 4096L * 4096; // 16,777,216
        long threshold2 = 8192L * 8192; // 67,108,864

        // 1. 安全尺寸，直接放行
        if (pixelCount <= threshold1)
            return true;

        // 2. 超大尺寸，直接拒绝（符合规范 4.3）
        if (pixelCount > threshold2)
            return false;

        // 3. 中间尺寸，使用 MemoryFailPoint 预检
        try
        {
            long bytes = pixelCount * 4;
            int mb = (int)(bytes / (1024 * 1024)) + 1;
            using var _ = new System.Runtime.MemoryFailPoint(mb);
            return true;
        }
        catch (InsufficientMemoryException)
        {
            return false;
        }
    }
}