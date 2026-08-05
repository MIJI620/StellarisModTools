// 文件: Stellaris.Engine/ImageAsset/ImageAssetRenderer.cs

using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using SkiaSharp;
using System;
using System.Runtime.InteropServices;

namespace Stellaris.Engine.ImageAsset;

/// <summary>
/// 图像渲染器 - 纯静态辅助类
/// 职责：PixelSet 与 SKBitmap 互转、缩放、变换、背景合成、DDS 编码
/// 不依赖引擎状态，所有方法为纯函数。
/// </summary>
internal static class ImageAssetRenderer
{
    // ==================== PixelSet ↔ SKBitmap ====================

    public static PixelSet BitmapToPixelSet(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        int byteCount = w * h * 4;
        byte[] pixelBytes = new byte[byteCount];
        IntPtr pixels = bitmap.GetPixels();
        Marshal.Copy(pixels, pixelBytes, 0, byteCount);

        var data = new byte[h][][];
        for (int y = 0; y < h; y++)
        {
            data[y] = new byte[w][];
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                data[y][x] = new byte[4];
                data[y][x][0] = pixelBytes[idx];
                data[y][x][1] = pixelBytes[idx + 1];
                data[y][x][2] = pixelBytes[idx + 2];
                data[y][x][3] = pixelBytes[idx + 3];
            }
        }
        return new PixelSet(data);
    }

    public static SKBitmap PixelSetToBitmap(PixelSet pixelSet)
    {
        int w = pixelSet.Width, h = pixelSet.Height;
        int byteCount = w * h * 4;
        byte[] pixelBytes = new byte[byteCount];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                var pixel = pixelSet.Data[y][x];
                pixelBytes[idx] = pixel[0];
                pixelBytes[idx + 1] = pixel[1];
                pixelBytes[idx + 2] = pixel[2];
                pixelBytes[idx + 3] = pixel.Length >= 4 ? pixel[3] : (byte)255;
            }
        }

        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bmp = new SKBitmap(info);
        IntPtr dst = bmp.GetPixels();
        Marshal.Copy(pixelBytes, 0, dst, byteCount);
        return bmp;
    }

    // ==================== 缩放 ====================

    public static SKBitmap ResizeBitmap(SKBitmap src, int newWidth, int newHeight)
    {
        if (src.Width == newWidth && src.Height == newHeight)
            return src.Copy();
        var info = new SKImageInfo(newWidth, newHeight, src.ColorType, src.AlphaType);
        var dst = new SKBitmap(info);
        src.ScalePixels(dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return dst;
    }

    public static PixelSet ResizePixelSet(PixelSet src, int newWidth, int newHeight)
    {
        using var srcBitmap = PixelSetToBitmap(src);
        using var resized = ResizeBitmap(srcBitmap, newWidth, newHeight);
        return BitmapToPixelSet(resized);
    }

    // ==================== 背景合成 ====================

    /// <summary>
    /// 标准 Alpha Over 合成：将 pixelSet 绘制到背景色上。
    /// 符合规范 2.3 和 3.1。
    /// </summary>
    public static PixelSet ApplyBackground(PixelSet pixelSet, byte[] bgColor)
    {
        int w = pixelSet.Width, h = pixelSet.Height;
        var newData = new byte[h][][];

        for (int y = 0; y < h; y++)
        {
            newData[y] = new byte[w][];
            for (int x = 0; x < w; x++)
            {
                newData[y][x] = (byte[])bgColor.Clone();
            }
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte[] src = pixelSet.Data[y][x];
                byte[] dst = newData[y][x];

                if (src.Length == 4)
                {
                    float srcA = src[3] / 255f;
                    float bgA = dst[3] / 255f;
                    float oneMinusSrcA = 1f - srcA;

                    dst[0] = (byte)(src[0] * srcA + dst[0] * oneMinusSrcA);
                    dst[1] = (byte)(src[1] * srcA + dst[1] * oneMinusSrcA);
                    dst[2] = (byte)(src[2] * srcA + dst[2] * oneMinusSrcA);
                    dst[3] = (byte)((srcA + bgA * oneMinusSrcA) * 255);
                }
                else
                {
                    dst[0] = src[0];
                    dst[1] = src[1];
                    dst[2] = src[2];
                    dst[3] = 255;
                }
            }
        }

        return new PixelSet(newData);
    }

    // ==================== 变换 ====================

    public static PixelSet? ApplyTransform(PixelSet src, TransformOperation op, ImageSize? outputSize)
    {
        using var srcBmp = PixelSetToBitmap(src);
        SKBitmap result;
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        switch (op)
        {
            case TransformOperation.FlipHorizontal:
                var flippedInfo = new SKImageInfo(srcBmp.Width, srcBmp.Height, srcBmp.ColorType, srcBmp.AlphaType);
                var flipped = new SKBitmap(flippedInfo);
                using (var canvas = new SKCanvas(flipped))
                {
                    canvas.Scale(-1, 1, srcBmp.Width / 2f, srcBmp.Height / 2f);
                    canvas.DrawBitmap(srcBmp, 0, 0, sampling, null);
                }
                result = flipped;
                break;

            case TransformOperation.FlipVertical:
                var flippedVInfo = new SKImageInfo(srcBmp.Width, srcBmp.Height, srcBmp.ColorType, srcBmp.AlphaType);
                var flippedV = new SKBitmap(flippedVInfo);
                using (var canvas = new SKCanvas(flippedV))
                {
                    canvas.Scale(1, -1, srcBmp.Width / 2f, srcBmp.Height / 2f);
                    canvas.DrawBitmap(srcBmp, 0, 0, sampling, null);
                }
                result = flippedV;
                break;

            case TransformOperation.ScaleProportional:
                if (!outputSize.HasValue) return src.Clone();
                float ratio = Math.Min((float)outputSize.Value.Width / srcBmp.Width, (float)outputSize.Value.Height / srcBmp.Height);
                int newW = (int)(srcBmp.Width * ratio);
                int newH = (int)(srcBmp.Height * ratio);
                result = ResizeBitmap(srcBmp, newW, newH);
                break;

            case TransformOperation.ScaleExact:
                if (!outputSize.HasValue) return src.Clone();
                result = ResizeBitmap(srcBmp, outputSize.Value.Width, outputSize.Value.Height);
                break;

            case TransformOperation.Rotate90:
                var rot90Info = new SKImageInfo(srcBmp.Height, srcBmp.Width, srcBmp.ColorType, srcBmp.AlphaType);
                var rot90 = new SKBitmap(rot90Info);
                using (var canvas = new SKCanvas(rot90))
                {
                    canvas.Translate(srcBmp.Height, 0);
                    canvas.RotateDegrees(90);
                    canvas.DrawBitmap(srcBmp, 0, 0, sampling, null);
                }
                result = rot90;
                break;

            case TransformOperation.RotateMinus90:
                var rotM90Info = new SKImageInfo(srcBmp.Height, srcBmp.Width, srcBmp.ColorType, srcBmp.AlphaType);
                var rotM90 = new SKBitmap(rotM90Info);
                using (var canvas = new SKCanvas(rotM90))
                {
                    canvas.Translate(0, srcBmp.Width);
                    canvas.RotateDegrees(-90);
                    canvas.DrawBitmap(srcBmp, 0, 0, sampling, null);
                }
                result = rotM90;
                break;

            case TransformOperation.Rotate180:
                var rot180Info = new SKImageInfo(srcBmp.Width, srcBmp.Height, srcBmp.ColorType, srcBmp.AlphaType);
                var rot180 = new SKBitmap(rot180Info);
                using (var canvas = new SKCanvas(rot180))
                {
                    canvas.RotateDegrees(180, srcBmp.Width / 2f, srcBmp.Height / 2f);
                    canvas.DrawBitmap(srcBmp, 0, 0, sampling, null);
                }
                result = rot180;
                break;

            case TransformOperation.Rotate270:
                var rot270Info = new SKImageInfo(srcBmp.Height, srcBmp.Width, srcBmp.ColorType, srcBmp.AlphaType);
                var rot270 = new SKBitmap(rot270Info);
                using (var canvas = new SKCanvas(rot270))
                {
                    canvas.Translate(0, srcBmp.Width);
                    canvas.RotateDegrees(-90);
                    canvas.DrawBitmap(srcBmp, 0, 0, sampling, null);
                }
                result = rot270;
                break;

            case TransformOperation.RotateMinus270:
                var rotM270Info = new SKImageInfo(srcBmp.Height, srcBmp.Width, srcBmp.ColorType, srcBmp.AlphaType);
                var rotM270 = new SKBitmap(rotM270Info);
                using (var canvas = new SKCanvas(rotM270))
                {
                    canvas.Translate(srcBmp.Height, 0);
                    canvas.RotateDegrees(90);
                    canvas.DrawBitmap(srcBmp, 0, 0, sampling, null);
                }
                result = rotM270;
                break;

            default:
                return src.Clone();
        }
        return BitmapToPixelSet(result);
    }

    // ==================== DDS 编码 ====================

    public static byte[] EncodeDds(SKBitmap bitmap, ImageFormat format)
    {
        // 群星"预览/图标"区域只支持未压缩 8888（实测 DXT 压缩该区域显示不了）——走自写 DX9 头。
        // DXT1/DXT5（BC1/BC3）压缩保留：群星其他区域支持压缩（如 BC3），按 format 分支。
        if (format == ImageFormat.Rgba8888)
            return EncodeRgba8888Dds(bitmap);

        int width = bitmap.Width, height = bitmap.Height;
        int byteCount = width * height * 4;
        byte[] pixelData = new byte[byteCount];
        IntPtr ptr = bitmap.GetPixels();
        Marshal.Copy(ptr, pixelData, 0, byteCount);

        CompressionFormat bcFormat = format switch
        {
            ImageFormat.Dxt1 => CompressionFormat.Bc1,
            _ => CompressionFormat.Bc3
        };

        var encoder = new BcEncoder();
        encoder.OutputOptions.Format = bcFormat;
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        encoder.OutputOptions.GenerateMipMaps = false;

        using var ms = new System.IO.MemoryStream();
        encoder.EncodeToStream(pixelData, width, height, PixelFormat.Rgba32, ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 自写传统 DX9 未压缩 8888 DDS（预览/按钮专用）：
    /// 群星老引擎不认 BCnEncoder 的 DX10 头（显示不出/颜色错），
    /// 传统 DDS_HEADER + 原始 RGBA 像素与 PNG 字节序完全一致。
    /// </summary>
    private static byte[] EncodeRgba8888Dds(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        byte[] pixelData = new byte[w * h * 4];
        Marshal.Copy(bitmap.GetPixels(), pixelData, 0, pixelData.Length);

        using var ms = new System.IO.MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("DDS ".ToCharArray());
        // DDS_HEADER
        bw.Write(124);              // dwSize
        bw.Write(0x1007);           // dwFlags: CAPS|HEIGHT|WIDTH|PIXELFORMAT|PITCH
        bw.Write(h);                // dwHeight
        bw.Write(w);                // dwWidth
        bw.Write(w * 4);            // dwPitchOrLinearSize
        bw.Write(0);                // dwDepth
        bw.Write(0);                // dwMipMapCount
        for (int i = 0; i < 11; i++) bw.Write(0); // dwReserved1
        // DDS_PIXELFORMAT
        bw.Write(32);               // dwSize
        bw.Write(0x41);             // dwFlags: ALPHAPIXELS | RGB
        bw.Write(0);                // dwFourCC
        bw.Write(32);               // dwRGBBitCount
        // 标准 A8R8G8B8 位掩码（群星对未压缩 DDS 固定按 BGRA 字节序解析，掩码仅供参考）
        bw.Write(0x00FF0000);       // dwRBitMask
        bw.Write(0x0000FF00);       // dwGBitMask
        bw.Write(0x000000FF);       // dwBBitMask
        bw.Write(0xFF000000);       // dwABitMask
        // DDS_HEADER 剩余
        bw.Write(0x1000);           // dwCaps: TEXTURE
        bw.Write(0);                // dwCaps2
        bw.Write(0);                // dwCaps3
        bw.Write(0);                // dwCaps4
        bw.Write(0);                // dwReserved2
        // 群星未压缩 DDS 固定按 BGRA（A8R8G8B8）解析——像素须写 B,G,R,A，否则 R/B 交换（橙变蓝）
        for (int i = 0; i + 3 < pixelData.Length; i += 4)
        {
            byte r = pixelData[i];
            pixelData[i] = pixelData[i + 2];   // B 位
            pixelData[i + 2] = r;              // R 位
        }
        bw.Write(pixelData);
        bw.Flush();
        return ms.ToArray();
    }
}