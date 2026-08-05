// 文件: Stellaris.Engine/GalaxyStyle/GalaxyAssetExporter.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Stellaris.Engine.ImageAsset;
using Stellaris.Engine.SpriteManagement;
using Stellaris.Parser;

namespace Stellaris.Engine.GalaxyStyle;

internal sealed class GalaxyAssetExporter
{
    private readonly StellarisAdapter _adapter;
    private readonly ImageAssetEngine _imageEngine;
    private readonly SpriteManagementEngine _spriteEngine;
    private readonly ILogger _logger;
    private readonly string _modPrefix;

    private const float EndRadius = 500.0f;           // 规范 3.1（与坐标映射 ±500 一致，银河填满画布）
    private const int LogicalCanvasSize = 500;        // 规范 1.6
    private const int GlowRadius = 30;                // 规范 8.4
    private const int StarGlowRadius = 8;             // 规范 8.5
    private const int StarCoreRadius = 2;             // 规范 8.5
    private const float CoreGlowBlur = 12.5f;          // 规范 8.3
    private const float ArmNebulaBlur = 15.0f;        // 规范 8.4
    private const float BgStarBlur = 0.5f;            // 规范 8.6

    public GalaxyAssetExporter(StellarisAdapter adapter, ImageAssetEngine imageEngine,
        SpriteManagementEngine spriteEngine, string modPrefix, ILogger? logger = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _imageEngine = imageEngine ?? throw new ArgumentNullException(nameof(imageEngine));
        _spriteEngine = spriteEngine ?? throw new ArgumentNullException(nameof(spriteEngine));
        _modPrefix = modPrefix ?? throw new ArgumentNullException(nameof(modPrefix));
        _logger = logger ?? NullLogger.Instance;
    }

    // ===== 公共导出方法 =====
    public void ExportPreview(string styleName, GalaxyShapeParameters parameters, PreviewOptions opts, List<Vector2>? staticPoints = null)
    {
        // 绑定样式：用静态地图点集合渲染（坐标 ±500，与生成点同范围）；否则按参数生成形状点
        var points = staticPoints != null && staticPoints.Count > 0
            ? staticPoints
            : GalaxyPointGenerator.GeneratePoints(parameters, EndRadius, 1);
        var pixelSet = RenderPreview(points, parameters, opts);

        // 导出路径：优先精灵表记载的 texturefile 相对路径；未声明 → 默认路径
        string relPath = _spriteEngine.GetSpriteDefinition($"GFX_galaxy_preview_{styleName}")?
            .TextureFile?.Replace(".dds", "", StringComparison.OrdinalIgnoreCase)
            ?? $"gfx/interface/game_setup/galaxy_preview/{_modPrefix}_{styleName}";
        _imageEngine.ExportImage(relPath, pixelSet, ImageFormat.Rgba8888, ExportMode.DdsAndPng,
            new ImageSize(opts.OuterWidth!.Value, opts.OuterHeight!.Value),
            opts.BackgroundColor);

        if (_imageEngine.Status != OperationStatus.Success)
            throw new InvalidOperationException($"预览导出失败: {_imageEngine.Status}");
        // 导出只生成图片；gfx 精灵表由保存/规整化统一处理（不在此注册）
    }

    public void ExportIcon(string styleName, GalaxyShapeParameters parameters, IconOptions opts, List<Vector2>? staticPoints = null)
    {
        // 绑定样式：用静态地图点集合渲染；否则按参数生成形状点
        var points = staticPoints != null && staticPoints.Count > 0
            ? staticPoints
            : GalaxyPointGenerator.GeneratePoints(parameters, EndRadius, 1);
        var pixelSet = RenderButtonIcon(points, parameters, opts);

        // 导出路径：优先精灵表记载的 texturefile 相对路径；未声明 → 默认路径
        string relPath = _spriteEngine.GetSpriteDefinition($"GFX_galaxy_button_{styleName}")?
            .TextureFile?.Replace(".dds", "", StringComparison.OrdinalIgnoreCase)
            ?? $"gfx/interface/game_setup/galaxy_button/{_modPrefix}_{styleName}";
        _imageEngine.ExportImage(relPath, pixelSet, ImageFormat.Rgba8888, ExportMode.DdsAndPng,
            new ImageSize(opts.FrameWidth!.Value * 3, opts.FrameHeight!.Value),
            null);

        if (_imageEngine.Status != OperationStatus.Success)
            throw new InvalidOperationException($"图标导出失败: {_imageEngine.Status}");
        // 导出只生成图片；gfx 精灵表由保存/规整化统一处理（不在此注册）
    }

    // ========================================================================
    // 第八章：预览渲染（按照规范 8.1 a→l 顺序）
    // ========================================================================
    private PixelSet RenderPreview(List<Vector2> points, GalaxyShapeParameters parameters, PreviewOptions opts)
    {
        var (totalArea, ringArea, armsTotal, _) = GalaxyPointGenerator.ComputeAreas(parameters, EndRadius, 5.0f);
        float minDist = (float)parameters.StarsMinDist;
        double density = opts.FillDensity ?? 0.25;
        int totalTarget = (int)(totalArea / (minDist * minDist) * density);
        totalTarget = Math.Clamp(totalTarget, 10, 20000);

        int ringTarget = 0, armsTarget = 0;
        if (parameters.HasRing && parameters.NumArms > 0)
        {
            armsTarget = (int)(totalTarget * (armsTotal / totalArea));
            ringTarget = (int)(totalTarget * (ringArea / totalArea));
        }
        else if (parameters.HasRing) ringTarget = totalTarget;
        else if (parameters.NumArms > 0) armsTarget = totalTarget;

        var sampledPoints = new List<Vector2>();
        // **静态点集优先**：调用方传入 points（staticPoints——静态地图恒星点）时直接使用，
        // 不再按样式参数生成形状点（之前 points 参数被忽略 → 点集覆盖不生效，预览渲染成形状）
        if (points != null && points.Count > 0)
        {
            sampledPoints = points;
        }
        else
        {
            // 原形状生成逻辑（arms/ring/disk 三个独立分支可叠加——不能改成 else-if 链）
            if (parameters.NumArms > 0 && armsTarget > 0)
            {
                float r0 = (float)(parameters.CoreRadiusPerc * EndRadius);
                var candidates = GalaxyPointGenerator.GenerateSpiralPoints(
                    parameters.NumArms, r0, EndRadius, (float)parameters.Tightness, 1,
                    (float)parameters.WidthDeg, (float)parameters.ArmAngleDeg, minDist);
                sampledPoints.AddRange(GalaxyPointGenerator.SamplePointsByArea(candidates, armsTarget, minDist));
            }
            if (parameters.HasRing && ringTarget > 0)
            {
                var candidates = GalaxyPointGenerator.GenerateRingPoints(
                    (float)parameters.RingWidth, (float)parameters.RingOffset, minDist, EndRadius);
                sampledPoints.AddRange(GalaxyPointGenerator.SamplePointsByArea(candidates, ringTarget, minDist));
            }
            if (parameters.NumArms == 0 && !parameters.HasRing)
            {
                float r0 = (float)(parameters.CoreRadiusPerc * EndRadius);
                var candidates = GalaxyPointGenerator.GenerateDiskPoints(r0, EndRadius, minDist);
                sampledPoints = GalaxyPointGenerator.SamplePointsByArea(candidates, totalTarget, minDist);
            }
        }

        var starTypes = AssignStarTypes(sampledPoints.Count, opts.StarPresets!);

        // 8.1-e: 外部画布 + 背景星光
        int outerW = opts.OuterWidth!.Value;
        int outerH = opts.OuterHeight!.Value;
        var outerCanvas = CreateTransparentPixelSet(outerW, outerH);
        if (opts.BackgroundColor != null)
            outerCanvas = ImageAssetRenderer.ApplyBackground(outerCanvas, opts.BackgroundColor);

        if (opts.BgStarDensity > 0)
        {
            double densityStar = opts.BgStarDensity.Value;
            int numStars = (int)(outerW * outerH * densityStar * 0.02);
            numStars = Math.Min(5000, Math.Max(10, numStars));
            var rng = new Random();
            for (int i = 0; i < numStars; i++)
            {
                int x = rng.Next(outerW);
                int y = rng.Next(outerH);
                int brightness = rng.Next(63, 128);
                SetPixel(outerCanvas, x, y, (byte)brightness, (byte)brightness, (byte)brightness, 255);
            }
            outerCanvas = ApplyGaussianBlurFloat(outerCanvas, BgStarBlur);
        }

        // 8.1-f: 逻辑画布 500×500 + 核心辉光
        int logicW = LogicalCanvasSize;
        int logicH = LogicalCanvasSize;
        var logicCanvas = CreateTransparentPixelSet(logicW, logicH);
        if (opts.GlowCore == true)
            DrawCoreGlow(logicCanvas, parameters, opts.CoreColor!);

        // 8.1-g: 扩展至 620×620
        int ext = GlowRadius * 2;
        int extW = logicW + 2 * ext;
        int extH = logicH + 2 * ext;
        var extendedCanvas = CreateTransparentPixelSet(extW, extH);
        for (int y = 0; y < logicH; y++)
            for (int x = 0; x < logicW; x++)
                extendedCanvas.Data[y + ext][x + ext] = (byte[])logicCanvas.Data[y][x].Clone();

        // 8.1-h: 旋臂星云
        if (opts.GlowArms == true && sampledPoints.Count > 0)
        {
            var nebula = GenerateArmNebula(sampledPoints, extW, extH, minDist, GlowRadius);
            extendedCanvas = CompositeOver(extendedCanvas, nebula);
        }

        // 8.1-i: 恒星点阵
        DrawPointGlow(extendedCanvas, sampledPoints, starTypes, opts.StarPresets!, extW, extH, ext);

        // 8.1-j: 等比缩放（620 → 248，innerW/logicW 比例）
        int innerW = opts.InnerWidth!.Value;
        int innerH = opts.InnerHeight!.Value;
        float scaleX = innerW / (float)logicW;
        float scaleY = innerH / (float)logicH;
        int scaledW = (int)Math.Round(extW * scaleX);
        int scaledH = (int)Math.Round(extH * scaleY);
        var scaledCanvas = ImageAssetRenderer.ResizePixelSet(extendedCanvas, scaledW, scaledH);

        // 8.1-k: 只裁"超出预览 outer 的部分"（宽/高各自判断；不超出不裁，不额外缩放）
        int cropX = 0, cropY = 0;
        int cropW = scaledW, cropH = scaledH;
        if (scaledW > outerW) { cropX = (scaledW - outerW) / 2; cropW = outerW; }
        if (scaledH > outerH) { cropY = (scaledH - outerH) / 2; cropH = outerH; }
        var croppedCanvas = CropPixelSet(scaledCanvas, cropX, cropY, cropW, cropH);

        // 8.1-l: 合成到外部画布（背景铺满 + 内容居中）
        int offsetX = (outerW - cropW) / 2;
        int offsetY = (outerH - cropH) / 2;
        var placements = new List<Placement>
        {
            new Placement(0, 0, 0, outerW, outerH),
            new Placement(1, offsetX, offsetY, offsetX + cropW, offsetY + cropH)
        };
        _imageEngine.CompositeImages(new List<PixelSet> { outerCanvas, croppedCanvas }, placements, null, new ImageSize(outerW, outerH));
        if (_imageEngine.Status != OperationStatus.Success || _imageEngine.Result == null)
            return outerCanvas;
        return _imageEngine.Result;
    }

    // ========================================================================
    // 第九章：按钮图标生成
    // ========================================================================
    private PixelSet RenderButtonIcon(List<Vector2> points, GalaxyShapeParameters parameters, IconOptions opts)
    {
        int innerSize = opts.InnerWidth!.Value;
        int frameW = opts.FrameWidth!.Value;
        int frameH = opts.FrameHeight!.Value;

        var mask = GeneratePointMask(points, parameters, innerSize, innerSize);

        var colors = new[] { opts.NormalColor!, opts.HighlightColor!, opts.PressedColor! };
        var frames = new List<PixelSet>();
        for (int i = 0; i < 3; i++)
        {
            var frame = ApplyColorMaskAndExpand(mask, colors[i], frameW, frameH);
            if (i == 1 || i == 2)
            {
                var blurred = ApplyGaussianBlurFloat(frame, opts.GlowRadius!.Value);
                frame = CompositeOver(frame, blurred);
            }
            frames.Add(frame);
        }
        return StitchHorizontal(frames);
    }

    // ========================================================================
    // 辅助方法
    // ========================================================================

    #region 8.3 核心辉光
    private void DrawCoreGlow(PixelSet canvas, GalaxyShapeParameters parameters, byte[] coreColor)
    {
        int width = canvas.Width, height = canvas.Height;
        float endR = EndRadius;
        float coreRatio = (float)Math.Min(parameters.CoreRadiusPerc, 0.5);
        float coreRadiusPx = coreRatio * endR * 0.5f;
        int cx = width / 2, cy = height / 2;
        float r1 = 0.25f * coreRadiusPx;
        float r2 = 0.50f * coreRadiusPx;
        float r3 = 1.00f * coreRadiusPx;
        float r4 = 2.00f * coreRadiusPx;

        var alphaData = new byte[height][][];
        for (int y = 0; y < height; y++)
        {
            alphaData[y] = new byte[width][];
            for (int x = 0; x < width; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float alpha;
                if (d <= r1) alpha = 1.0f;
                else if (d <= r2)
                {
                    float t = (d - r1) / (r2 - r1);
                    alpha = 1.0f - t * (1.0f - 191f / 255f);
                }
                else if (d <= r3)
                {
                    float t = (d - r2) / (r3 - r2);
                    alpha = 191f / 255f - t * ((191f - 128f) / 255f);
                }
                else if (d <= r4)
                {
                    float t = (d - r3) / (r4 - r3);
                    alpha = (128f / 255f) * (1f - t);
                }
                else alpha = 0f;
                alpha = Math.Clamp(alpha, 0, 1);
                byte a = (byte)(alpha * 255);
                alphaData[y][x] = new byte[] { coreColor[0], coreColor[1], coreColor[2], a };
            }
        }
        var alphaPs = new PixelSet(alphaData);
        var blurred = ApplyGaussianBlurFloat(alphaPs, CoreGlowBlur);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var src = blurred.Data[y][x];
                var dst = canvas.Data[y][x];
                float srcA = src[3] / 255f;
                float dstA = dst[3] / 255f;
                float outA = srcA + dstA * (1 - srcA);
                if (outA > 0.001f)
                {
                    dst[0] = (byte)((src[0] * srcA + dst[0] * dstA * (1 - srcA)) / outA);
                    dst[1] = (byte)((src[1] * srcA + dst[1] * dstA * (1 - srcA)) / outA);
                    dst[2] = (byte)((src[2] * srcA + dst[2] * dstA * (1 - srcA)) / outA);
                    dst[3] = (byte)(outA * 255);
                }
            }
    }
    #endregion

    #region 8.4 旋臂星云（修正二维数组索引）
    private PixelSet GenerateArmNebula(List<Vector2> points, int extW, int extH, float minDist, int glowRadius)
    {
        // C# 在 500 逻辑坐标系 = 输出(200) × 2.5；demo 值 × 2.5 = C# 值
        // grid = demo max(8, step*1.2)=9.6 × 2.5 = 24
        int gridSize = Math.Max(20, (int)(minDist * 3.0f));
        int gridNX = Math.Max(1, extW / gridSize);
        int gridNY = Math.Max(1, extH / gridSize);
        var density = new int[gridNY, gridNX];   // 二维数组

        // 恒星 ±500 → 扩展画布中央（偏移 ext = glowRadius*2），与 8.1-g / DrawPointGlow 一致
        int ext = glowRadius * 2;
        float scaleX = 500.0f / 1000.0f;
        float scaleY = 500.0f / 1000.0f;
        foreach (var p in points)
        {
            float px = ext + (p.X + 500) * scaleX;
            float py = (extH - 1 - ext) - (p.Y + 500) * scaleY;
            int gx = (int)(px / gridSize);
            int gy = (int)(py / gridSize);
            if (gx >= 0 && gx < gridNX && gy >= 0 && gy < gridNY)
                density[gy, gx]++;          // 修正为 [,] 语法
        }

        var candidates = new List<(int x, int y, SKColor color, int size)>();
        var colors = new SKColor[] { SKColors.White, new SKColor(200, 200, 200), new SKColor(230, 210, 180) };
        var rng = new Random();
        float densityScale = 0.01f;

        for (int gy = 0; gy < gridNY; gy++)
        {
            for (int gx = 0; gx < gridNX; gx++)
            {
                int d = density[gy, gx];    // 修正为 [,]
                if (d < 1) continue;
                float area = gridSize * gridSize;
                // 平方密度：辉光数量与恒星密度 d 的平方相关（密处更聚）
                int num = (int)(d * d * densityScale * area / (minDist * minDist)) * 16;
                num = Math.Clamp(num, 1, 120);
                for (int i = 0; i < num; i++)
                {
                    float ox = (float)(rng.NextDouble() - 0.5) * gridSize * 0.4f;
                    float oy = (float)(rng.NextDouble() - 0.5) * gridSize * 0.4f;
                    float cx = (gx + 0.5f) * gridSize + ox;
                    float cy = (gy + 0.5f) * gridSize + oy;
                    cx = Math.Clamp(cx, 0, extW - 1);
                    cy = Math.Clamp(cy, 0, extH - 1);
                    int size = rng.Next(8, 19);
                    var color = colors[rng.Next(colors.Length)];
                    candidates.Add(((int)cx, (int)cy, color, size));
                }
            }
        }

        float maxDist = 40f; // 过滤距离 40（逻辑坐标；用户指定）
        var filtered = new List<(int x, int y, SKColor color, int size)>();
        foreach (var (cx, cy, color, size) in candidates)
        {
            // 反算回逻辑坐标（与正向映射一致：py = (extH-1-ext) - (y+500)*scaleY）
            float origX = (cx - ext) / scaleX - 500;
            float origY = ((extH - 1 - ext) - cy) / scaleY - 500;
            bool near = false;
            foreach (var p in points)
            {
                if (Vector2.DistanceSquared(new Vector2(origX, origY), p) <= maxDist * maxDist)
                { near = true; break; }
            }
            if (near) filtered.Add((cx, cy, color, size));
        }

        if (filtered.Count == 0)
            return CreateTransparentPixelSet(extW, extH);

        var nebulaData = new byte[extH][][];
        for (int y = 0; y < extH; y++)
        {
            nebulaData[y] = new byte[extW][];
            for (int x = 0; x < extW; x++)
                nebulaData[y][x] = new byte[] { 0, 0, 0, 0 };
        }

        foreach (var (cx, cy, color, size) in filtered)
        {
            int radius = size / 2;
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > radius * radius) continue;
                    int px = cx + dx, py = cy + dy;
                    if (px < 0 || px >= extW || py < 0 || py >= extH) continue;
                    if (nebulaData[py][px][3] < 102)
                    {
                        nebulaData[py][px][0] = color.Red;
                        nebulaData[py][px][1] = color.Green;
                        nebulaData[py][px][2] = color.Blue;
                        nebulaData[py][px][3] = 102;
                    }
                }
            int r = glowRadius;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist > r) continue;
                    int px = cx + dx, py = cy + dy;
                    if (px < 0 || px >= extW || py < 0 || py >= extH) continue;
                    byte alpha = (byte)(102 * (1 - dist / r));
                    if (alpha > nebulaData[py][px][3])
                    {
                        nebulaData[py][px][0] = color.Red;
                        nebulaData[py][px][1] = color.Green;
                        nebulaData[py][px][2] = color.Blue;
                        nebulaData[py][px][3] = alpha;
                    }
                }
        }

        var nebulaPs = new PixelSet(nebulaData);
        return ApplyGaussianBlurFloat(nebulaPs, ArmNebulaBlur);
    }
    #endregion

    #region 8.5 恒星点绘制
    private void DrawPointGlow(PixelSet canvas, List<Vector2> points, List<string> types,
    Dictionary<string, StarPreset> presets, int width, int height, int offset)
    {
        // 恒星 ±500 → 逻辑 500 区，偏移 offset（ext）放入扩展画布中央（60..560），给恒星光晕留空间
        float scaleX = 500.0f / 1000.0f;
        float scaleY = 500.0f / 1000.0f;

        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var typeKey = types[i];
            if (!presets.TryGetValue(typeKey, out var preset))
            {
                // 若字典中无此键，跳过或使用默认（这里跳过）
                continue;
            }
            var coreColor = new SKColor(preset.R, preset.G, preset.B, preset.A);
            var glowColor = new SKColor(preset.GlowR, preset.GlowG, preset.GlowB, preset.GlowA);

            int px = offset + (int)((p.X + 500) * scaleX);
            int py = height - 1 - offset - (int)((p.Y + 500) * scaleY);
            if (px < 0 || px >= width || py < 0 || py >= height) continue;

            // 光晕
            for (int dy = -StarGlowRadius; dy <= StarGlowRadius; dy++)
                for (int dx = -StarGlowRadius; dx <= StarGlowRadius; dx++)
                {
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist > StarGlowRadius) continue;
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                    float alpha = 1 - (dist / StarGlowRadius);
                    byte a = (byte)(alpha * 255);
                    var existing = canvas.Data[ny][nx];
                    if (a > existing[3])
                    {
                        existing[0] = glowColor.Red;
                        existing[1] = glowColor.Green;
                        existing[2] = glowColor.Blue;
                        existing[3] = a;
                    }
                }

            // 核心
            for (int dy = -StarCoreRadius; dy <= StarCoreRadius; dy++)
                for (int dx = -StarCoreRadius; dx <= StarCoreRadius; dx++)
                {
                    if (dx * dx + dy * dy > StarCoreRadius * StarCoreRadius) continue;
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                    var dst = canvas.Data[ny][nx];
                    dst[0] = coreColor.Red;
                    dst[1] = coreColor.Green;
                    dst[2] = coreColor.Blue;
                    dst[3] = 255;
                }
        }
    }
    #endregion

    #region 9.1 灰度掩码
    private PixelSet GeneratePointMask(List<Vector2> points, GalaxyShapeParameters parameters, int width, int height)
    {
        // 密度图（原始算法）：每个像素统计落入的点数 → 归一化透明度，点最密处不透明
        var counts = new int[height][];
        for (int y = 0; y < height; y++)
            counts[y] = new int[width];
        float scaleX = (width - 1) / 1000.0f;
        float scaleY = (height - 1) / 1000.0f;

        foreach (var p in points)
        {
            int px = (int)((p.X + 500) * scaleX);
            int py = height - 1 - (int)((p.Y + 500) * scaleY);
            if (px < 0 || px >= width || py < 0 || py >= height) continue;
            counts[py][px]++;
        }

        int maxCount = 1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (counts[y][x] > maxCount) maxCount = counts[y][x];

        var data = new byte[height][][];
        for (int y = 0; y < height; y++)
        {
            data[y] = new byte[width][];
            for (int x = 0; x < width; x++)
            {
                int a = counts[y][x] == 0 ? 0 : (int)(255.0 * counts[y][x] / maxCount);
                data[y][x] = new byte[] { (byte)a, (byte)a, (byte)a, (byte)a };
            }
        }

        bool fillCore = false;
        if (parameters.NumArms > 0)
            fillCore = true;
        else if (parameters.HasRing)
        {
            float ringInner = (float)(parameters.RingOffset * EndRadius);
            float startRadius = (float)(parameters.CoreRadiusPerc * EndRadius);
            if (Math.Abs(ringInner - startRadius) > 0.001f)
                fillCore = true;
        }

        if (fillCore)
        {
            float coreRatio = (float)Math.Min(parameters.CoreRadiusPerc, 0.5);
            int corePx = (int)(coreRatio * width / 2);
            int cx = width / 2, cy = height / 2;
            for (int dy = -corePx; dy <= corePx; dy++)
                for (int dx = -corePx; dx <= corePx; dx++)
                {
                    if (dx * dx + dy * dy > corePx * corePx) continue;
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                    int v = Math.Max((int)data[ny][nx][0], 128); // 透明度取最高值（不叠加）
                    data[ny][nx][0] = (byte)Math.Min(255, v);
                    data[ny][nx][1] = data[ny][nx][0];
                    data[ny][nx][2] = data[ny][nx][0];
                    data[ny][nx][3] = 255;
                }
        }

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte v = data[y][x][0];
                int newV;
                if (v <= 128)
                    newV = (int)(v * (191f / 128f));
                else
                    newV = (int)(191 + (v - 128) * (64f / 127f));
                newV = Math.Clamp(newV, 0, 255);
                data[y][x][0] = (byte)newV;
                data[y][x][1] = (byte)newV;
                data[y][x][2] = (byte)newV;
            }

        return new PixelSet(data);
    }
    #endregion

    #region 9.2 着色与边框
    private PixelSet ApplyColorMaskAndExpand(PixelSet mask, byte[] color, int targetW, int targetH)
    {
        int mw = mask.Width, mh = mask.Height;
        var canvas = CreateTransparentPixelSet(targetW, targetH);
        // 35 就是最终结果：mask 原尺寸 1:1 放入 frame 中央（禁止额外缩放）
        int offX = (targetW - mw) / 2, offY = (targetH - mh) / 2;

        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                byte brightness = mask.Data[y][x][0];
                if (brightness == 0) continue;
                int dx = offX + x, dy = offY + y;
                if (dx < 0 || dx >= targetW || dy < 0 || dy >= targetH) continue;
                // 密度图决定 alpha（透明度），RGB 用纯色
                float alpha = brightness / 255.0f;
                canvas.Data[dy][dx][0] = color[0];
                canvas.Data[dy][dx][1] = color[1];
                canvas.Data[dy][dx][2] = color[2];
                canvas.Data[dy][dx][3] = (byte)(color[3] * alpha);
            }

        // 外边框
        for (int x = 0; x < targetW; x++)
        {
            SetPixel(canvas, x, 0, color[0], color[1], color[2], color[3]);
            SetPixel(canvas, x, targetH - 1, color[0], color[1], color[2], color[3]);
        }
        for (int y = 0; y < targetH; y++)
        {
            SetPixel(canvas, 0, y, color[0], color[1], color[2], color[3]);
            SetPixel(canvas, targetW - 1, y, color[0], color[1], color[2], color[3]);
        }

        return canvas;
    }
    #endregion

    #region 通用工具
    private List<string> AssignStarTypes(int count, Dictionary<string, StarPreset> presets)
    {
        var types = new List<string>(count);
        var weighted = presets.Where(kv => kv.Value.Weight > 0)
            .Select(kv => (kv.Key, kv.Value.Weight)).ToList();
        int totalWeight = weighted.Sum(w => w.Weight);
        if (totalWeight == 0)
        {
            // 若所有权重为0，则全部分配第一种（但应避免这种情况）
            for (int i = 0; i < count; i++) types.Add(presets.Keys.FirstOrDefault() ?? "unknown");
            return types;
        }
        var rng = new Random();
        for (int i = 0; i < count; i++)
        {
            int roll = rng.Next(totalWeight);
            int cum = 0;
            foreach (var (key, weight) in weighted)
            {
                cum += weight;
                if (roll < cum) { types.Add(key); break; }
            }
            if (types.Count == i) types.Add(weighted[0].Key); // fallback
        }
        return types;
    }

    private PixelSet ApplyGaussianBlurFloat(PixelSet src, float radius)
    {
        using var bmp = PixelSetToSKBitmap(src);
        var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var blurred = new SKBitmap(info);
        using var canvas = new SKCanvas(blurred);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(radius, radius) };
        canvas.DrawBitmap(bmp, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.Flush();
        return SKBitmapToPixelSet(blurred);
    }

    private PixelSet CompositeOver(PixelSet bottom, PixelSet top)
    {
        int w = bottom.Width, h = bottom.Height;
        var data = new byte[h][][];
        for (int y = 0; y < h; y++)
        {
            data[y] = new byte[w][];
            for (int x = 0; x < w; x++)
            {
                var b = bottom.Data[y][x];
                var t = top.Data[y][x];
                float ba = b[3] / 255f;
                float ta = t[3] / 255f;
                float outA = ba + ta * (1 - ba);
                if (outA < 0.001f)
                    data[y][x] = new byte[] { 0, 0, 0, 0 };
                else
                {
                    data[y][x] = new byte[]
                    {
                        (byte)((b[0] * ba + t[0] * ta * (1 - ba)) / outA),
                        (byte)((b[1] * ba + t[1] * ta * (1 - ba)) / outA),
                        (byte)((b[2] * ba + t[2] * ta * (1 - ba)) / outA),
                        (byte)(outA * 255)
                    };
                }
            }
        }
        return new PixelSet(data);
    }

    private PixelSet StitchHorizontal(List<PixelSet> frames)
    {
        if (frames.Count == 0) throw new ArgumentException("至少一帧");
        int h = frames[0].Height;
        int totalW = frames.Sum(f => f.Width);
        var data = new byte[h][][];
        for (int y = 0; y < h; y++)
        {
            data[y] = new byte[totalW][];
            for (int x = 0; x < totalW; x++)
                data[y][x] = new byte[] { 0, 0, 0, 0 };
        }

        int xOff = 0;
        foreach (var frame in frames)
        {
            int fw = frame.Width, fh = frame.Height;
            for (int y = 0; y < fh; y++)
                for (int x = 0; x < fw; x++)
                    data[y][xOff + x] = (byte[])frame.Data[y][x].Clone();
            xOff += fw;
        }
        return new PixelSet(data);
    }

    private PixelSet CropPixelSet(PixelSet src, int x, int y, int width, int height)
    {
        var data = new byte[height][][];
        for (int row = 0; row < height; row++)
        {
            data[row] = new byte[width][];
            for (int col = 0; col < width; col++)
                data[row][col] = (byte[])src.Data[y + row][x + col].Clone();
        }
        return new PixelSet(data);
    }

    private PixelSet CreateTransparentPixelSet(int w, int h)
    {
        var data = new byte[h][][];
        for (int y = 0; y < h; y++)
        {
            data[y] = new byte[w][];
            for (int x = 0; x < w; x++)
                data[y][x] = new byte[] { 0, 0, 0, 0 };
        }
        return new PixelSet(data);
    }

    private void SetPixel(PixelSet ps, int x, int y, byte r, byte g, byte b, byte a)
    {
        if (x < 0 || x >= ps.Width || y < 0 || y >= ps.Height) return;
        ps.Data[y][x][0] = r;
        ps.Data[y][x][1] = g;
        ps.Data[y][x][2] = b;
        ps.Data[y][x][3] = a;
    }

    private SKBitmap PixelSetToSKBitmap(PixelSet ps)
    {
        int w = ps.Width, h = ps.Height;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, new SKColor(ps.Data[y][x][0], ps.Data[y][x][1], ps.Data[y][x][2], ps.Data[y][x][3]));
        return bmp;
    }

    private PixelSet SKBitmapToPixelSet(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = new byte[h][][];
        for (int y = 0; y < h; y++)
        {
            data[y] = new byte[w][];
            for (int x = 0; x < w; x++)
            {
                var color = bmp.GetPixel(x, y);
                data[y][x] = new byte[] { color.Red, color.Green, color.Blue, color.Alpha };
            }
        }
        return new PixelSet(data);
    }
    #endregion
}