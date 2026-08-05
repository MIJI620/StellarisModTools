// 文件: Stellaris.Engine/ImageAsset/ImageLoader.cs

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pfim;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Stellaris.Engine.ImageAsset;

/// <summary>
/// 负责图像加载、解码、缓存和文件查找。
/// 线程安全。
/// </summary>
internal sealed class ImageLoader : IDisposable
{
    private readonly IReadOnlyList<string> _roots;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, PixelSet> _cache = new();
    private readonly List<string> _cacheOrder = new();
    private readonly object _cacheLock = new();
    private readonly int _cacheLimit = 50;
    private bool _disposed;

    // 运行时可变的内存检查开关
    private bool _enableMemoryCheck;

    public ImageLoader(IReadOnlyList<string> roots, ILogger logger, bool enableMemoryCheck = true)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
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

    /// <summary>
    /// 加载图像，返回 PixelSet。若失败则抛出异常（调用方捕获并设置状态）。
    /// </summary>
    public PixelSet LoadImage(string relativePath, ImageSize? outputSize,
        byte[]? backgroundColor, bool forceReload)
    {
        if (string.IsNullOrEmpty(relativePath) || !Path.HasExtension(relativePath))
            throw new ArgumentException("相对路径必须包含后缀名 (.dds 或 .png)", nameof(relativePath));

        if (backgroundColor != null && backgroundColor.Length != 4)
            throw new ArgumentException("backgroundColor 必须为长度为4的数组 (RGBA)");

        if (outputSize.HasValue && (outputSize.Value.Width <= 0 || outputSize.Value.Height <= 0))
            throw new ArgumentException("outputSize 宽高必须 > 0");

        // 强制重载时清除缓存
        if (forceReload)
        {
            if (_cache.TryRemove(relativePath, out _))
            {
                lock (_cacheLock) { _cacheOrder.Remove(relativePath); }
                _logger.LogDebug("forceReload: 已清除缓存条目: {Path}", relativePath);
            }
        }

        // 尝试从缓存获取
        if (_cache.TryGetValue(relativePath, out var cached))
        {
            TouchCache(relativePath);
            _logger.LogDebug("缓存命中: {Path}", relativePath);
            var working = cached.Clone();
            if (backgroundColor != null)
                working = ImageAssetRenderer.ApplyBackground(working, backgroundColor);
            if (outputSize.HasValue)
                working = ImageAssetRenderer.ResizePixelSet(working, outputSize.Value.Width, outputSize.Value.Height);
            return working;
        }

        // 从磁盘加载
        var fullPath = FindFile(relativePath);
        if (fullPath == null)
            throw new FileNotFoundException($"文件未找到: {relativePath}");

        var bitmap = DecodeImage(fullPath);
        if (bitmap == null)
            throw new NotSupportedException($"不支持的图像格式: {relativePath}");

        // 内存预检（使用当前开关状态）
        if (_enableMemoryCheck)
        {
            if (!TryReserveMemory(bitmap.Width, bitmap.Height))
                throw new OutOfMemoryException($"图像过大，内存不足: {relativePath} ({bitmap.Width}x{bitmap.Height})");
        }

        var raw = ImageAssetRenderer.BitmapToPixelSet(bitmap);
        bitmap.Dispose();

        // 存入缓存（原始尺寸，无背景）
        CacheImage(relativePath, raw.Clone());

        // 应用背景和缩放
        var result = raw;
        if (backgroundColor != null)
            result = ImageAssetRenderer.ApplyBackground(result, backgroundColor);
        if (outputSize.HasValue)
            result = ImageAssetRenderer.ResizePixelSet(result, outputSize.Value.Width, outputSize.Value.Height);

        _logger.LogDebug("加载图像成功: {Path} ({W}x{H})", relativePath, result.Width, result.Height);
        return result;
    }

    private string? FindFile(string relativePath)
    {
        for (int i = _roots.Count - 1; i >= 0; i--)
        {
            string full = Path.Combine(_roots[i], relativePath);
            if (File.Exists(full))
                return full;
        }
        return null;
    }

    private SKBitmap? DecodeImage(string fullPath)
    {
        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext == ".png")
        {
            using var stream = File.OpenRead(fullPath);
            return SKBitmap.Decode(stream);
        }
        else if (ext == ".dds")
        {
            return DecodeDds(fullPath);
        }
        return null;
    }

    private SKBitmap? DecodeDds(string fullPath)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            using var image = Pfim.Pfimage.FromStream(stream);
            if (image == null) return null;

            var data = image.Data;
            int width = image.Width;
            int height = image.Height;
            int stride = image.Stride;

            if (image.Format == Pfim.ImageFormat.Rgba32)
            {
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                var bitmap = new SKBitmap(info);
                IntPtr ptr = bitmap.GetPixels();
                Marshal.Copy(data, 0, ptr, data.Length);
                return bitmap;
            }
            else if (image.Format == Pfim.ImageFormat.Rgb24)
            {
                int byteCount = width * height * 4;
                byte[] rgba = new byte[byteCount];
                for (int y = 0; y < height; y++)
                {
                    int srcIdx = y * stride;
                    int dstIdx = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        rgba[dstIdx++] = data[srcIdx++];
                        rgba[dstIdx++] = data[srcIdx++];
                        rgba[dstIdx++] = data[srcIdx++];
                        rgba[dstIdx++] = 255;
                    }
                }
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                var bitmap = new SKBitmap(info);
                IntPtr ptr = bitmap.GetPixels();
                Marshal.Copy(rgba, 0, ptr, byteCount);
                return bitmap;
            }
            else
            {
                int byteCount = data.Length;
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                var bitmap = new SKBitmap(info);
                IntPtr ptr = bitmap.GetPixels();
                Marshal.Copy(data, 0, ptr, byteCount);
                return bitmap;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DDS解码失败: {Path}", fullPath);
            return null;
        }
    }

    private bool TryReserveMemory(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        try
        {
            long bytes = (long)width * height * 4;
            int mb = (int)(bytes / (1024 * 1024)) + 1;
            using var _ = new System.Runtime.MemoryFailPoint(mb);
            return true;
        }
        catch (InsufficientMemoryException)
        {
            return false;
        }
    }

    // ===== 缓存管理 =====

    private void CacheImage(string key, PixelSet pixelSet)
    {
        if (pixelSet == null) return;
        lock (_cacheLock)
        {
            if (_cache.Count >= _cacheLimit && _cacheOrder.Count > 0)
            {
                string oldKey = _cacheOrder[0];
                _cacheOrder.RemoveAt(0);
                _cache.TryRemove(oldKey, out _);
            }
            _cache[key] = pixelSet;
            _cacheOrder.Remove(key);
            _cacheOrder.Add(key);
        }
    }

    private void TouchCache(string key)
    {
        lock (_cacheLock)
        {
            if (_cacheOrder.Remove(key))
                _cacheOrder.Add(key);
        }
    }

    public void ClearCache()
    {
        _cache.Clear();
        lock (_cacheLock) { _cacheOrder.Clear(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearCache();
    }
}