// 文件: Stellaris.Engine/ImageAsset/ImageExporter.cs

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using System;
using System.IO;
using System.Linq;

namespace Stellaris.Engine.ImageAsset;

/// <summary>
/// 负责导出图像文件（DDS/PNG）和删除文件。
/// 线程安全（无共享状态）。
/// </summary>
internal sealed class ImageExporter
{
    private readonly IReadOnlyList<string> _roots;
    private readonly ILogger _logger;

    public ImageExporter(IReadOnlyList<string> roots, ILogger? logger = null)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _logger = logger ?? NullLogger.Instance;
    }

    // ==================== ImageExporter.Export ====================
    public void Export(string relativePath, PixelSet pixelSet, ImageFormat format,
        ExportMode mode, ImageSize? outputSize, byte[]? backgroundColor)
    {
        if (pixelSet == null)
            throw new ArgumentException("像素集合为空");
        if (string.IsNullOrEmpty(relativePath))
            throw new ArgumentException("相对路径为空");
        if (backgroundColor != null && backgroundColor.Length != 4)
            throw new ArgumentException("backgroundColor 必须为长度为4的数组 (RGBA)");
        if (outputSize.HasValue && (outputSize.Value.Width <= 0 || outputSize.Value.Height <= 0))
            throw new ArgumentException("outputSize 宽高必须 > 0");

        string basePath = relativePath;
        string targetRoot = _roots.Count > 0 ? _roots[^1] : ".";

        // 准备最终图像
        using var srcBitmap = ImageAssetRenderer.PixelSetToBitmap(pixelSet);
        SKBitmap workingBitmap;
        if (backgroundColor != null)
        {
            var bgInfo = new SKImageInfo(srcBitmap.Width, srcBitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bgBitmap = new SKBitmap(bgInfo);
            using var canvas = new SKCanvas(bgBitmap);
            canvas.Clear(new SKColor(backgroundColor[0], backgroundColor[1], backgroundColor[2], backgroundColor[3]));
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            canvas.DrawBitmap(srcBitmap, 0, 0, sampling, null);
            workingBitmap = bgBitmap.Copy();
        }
        else
        {
            workingBitmap = srcBitmap.Copy();
        }

        if (outputSize.HasValue)
        {
            var resized = ImageAssetRenderer.ResizeBitmap(workingBitmap, outputSize.Value.Width, outputSize.Value.Height);
            workingBitmap.Dispose();
            workingBitmap = resized;
        }

        bool ddsSuccess = false, pngSuccess = false;
        string fullBase = Path.Combine(targetRoot, basePath);
        string? fullDir = Path.GetDirectoryName(fullBase);
        if (!string.IsNullOrEmpty(fullDir) && !Directory.Exists(fullDir))
            Directory.CreateDirectory(fullDir);

        // 写入 DDS
        if (mode == ExportMode.DdsOnly || mode == ExportMode.DdsAndPng)
        {
            string ddsPath = fullBase + ".dds";
            try
            {
                byte[] ddsData = ImageAssetRenderer.EncodeDds(workingBitmap, format);
                string tempPath = ddsPath + ".temp";
                File.WriteAllBytes(tempPath, ddsData);
                if (File.Exists(ddsPath)) File.Delete(ddsPath);
                File.Move(tempPath, ddsPath);
                _logger.LogDebug("DDS 导出成功: {Path}", ddsPath);
                ddsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DDS 导出失败: {Path}", ddsPath);
                ddsSuccess = false;
            }
        }

        // 写入 PNG
        if (mode == ExportMode.PngOnly || mode == ExportMode.DdsAndPng)
        {
            string pngPath = fullBase + ".png";
            try
            {
                using var image = SKImage.FromBitmap(workingBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                string tempPath = pngPath + ".temp";
                using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
                data.SaveTo(fs);
                fs.Flush();
                fs.Close();
                if (File.Exists(pngPath)) File.Delete(pngPath);
                File.Move(tempPath, pngPath);
                _logger.LogDebug("PNG 导出成功: {Path}", pngPath);
                pngSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PNG 导出失败: {Path}", pngPath);
                pngSuccess = false;
            }
        }

        workingBitmap.Dispose();

        // 根据 mode 检查成功状态
        bool overallSuccess = true;
        if (mode == ExportMode.DdsOnly && !ddsSuccess) overallSuccess = false;
        if (mode == ExportMode.PngOnly && !pngSuccess) overallSuccess = false;
        if (mode == ExportMode.DdsAndPng && (!ddsSuccess || !pngSuccess)) overallSuccess = false;

        if (!overallSuccess)
        {
            string errorMsg = $"导出失败: DDS={(ddsSuccess ? "成功" : "失败")}, PNG={(pngSuccess ? "成功" : "失败")}";
            throw new IOException(errorMsg);
        }
    }

    public void Delete(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            throw new ArgumentException("相对路径为空");

        string targetRoot = _roots.Count > 0 ? _roots[^1] : ".";
        string fullPath = Path.Combine(targetRoot, relativePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogDebug("删除图像: {Path}", relativePath);
        }
        else
        {
            throw new FileNotFoundException($"文件不存在: {relativePath}");
        }
    }
}