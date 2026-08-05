// 文件: Stellaris.Engine/GalaxyMap/GalaxyMapEngine_Image.cs
// 图像转点阵（规范第五章）：像素权重公式 + 加权采样。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Stellaris.Engine.GalaxyMap;

public sealed partial class GalaxyMapEngine
{
    /// <summary>
    /// 将 PNG 图像转换为静态地图星系坐标点集（规范 5.1 / 5.5）。
    /// 创建模式：生成点**追加**到当前 Systems（不覆盖、不合并），随后触发 ID 重编号与边界校验。
    /// </summary>
    /// <param name="mapName">静态地图名</param>
    /// <param name="imagePath">PNG 图像路径（绝对路径或相对当前工作目录）</param>
    /// <param name="options">转点阵参数</param>
    public void GeneratePointsFromImage(string mapName, string imagePath, ImageGenerationOptions options)
    {
        lock (_syncRoot)
        {
            SetTask(GalaxyMapTaskType.GeneratingPointsFromImage, mapName);
            try
            {
                var s = _staticScenarios.GetValueOrDefault(mapName)
                        ?? throw new KeyNotFoundException($"静态地图 '{mapName}' 不存在");

                var points = ExtractPointsFromImage(imagePath, options);

                // 6.3：整体缩放至 [-500,500]² 方形内
                ClampToBounds(points);

                // 5.5：创建模式追加（不覆盖、不合并）
                int startIndex = s.Systems.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    s.Systems.Add(new SystemEntry
                    {
                        Id = $"__new_{startIndex + i}",
                        Position = new SystemPosition { X = points[i].X, Y = points[i].Y }
                    });
                }

                _logger.LogInformation("图像转点阵完成: {Map}（新增 {Count} 点）", mapName, points.Count);
            }
            finally
            {
                SetTask(GalaxyMapTaskType.Idle);
            }
        }
    }

    private List<Vector2> ExtractPointsFromImage(string imagePath, ImageGenerationOptions options)
    {
        if (options == null) options = ImageGenerationOptions.Default;
        if (string.IsNullOrEmpty(imagePath) || !System.IO.File.Exists(imagePath))
            throw new System.IO.FileNotFoundException($"图像文件不存在: {imagePath}");

        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap == null)
            throw new InvalidDataException($"图像解析失败（格式错误或损坏）: {imagePath}");

        int w = bitmap.Width, h = bitmap.Height;
        const double rMax = 255.0, gMax = 255.0, bMax = 255.0, aMax = 255.0;

        // 计算每个像素的权重 p（规范 5.2：p = (A/A_max) × ((R+G+B)/(R_max+G_max+B_max))，
        // 不用哪个通道就从公式中移除）
        var weights = new double[w * h];
        double totalWeight = 0;
        var validPixels = new List<(int X, int Y, double Weight)>();

        bool useR, useG, useB, useA;
        (useR, useG, useB, useA) = ResolveChannels(options);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = bitmap.GetPixel(x, y);
                double r = c.Red / rMax, g = c.Green / gMax, b = c.Blue / bMax, a = c.Alpha / aMax;

                double p = 1.0;
                double sum = 0, maxSum = 0;
                if (useR) { sum += c.Red; maxSum += rMax; }
                if (useG) { sum += c.Green; maxSum += gMax; }
                if (useB) { sum += c.Blue; maxSum += bMax; }
                if (useA) p *= a;

                if (sum > 0 && maxSum > 0)
                    p *= sum / maxSum;

                // 反向通道：p = 1 - 通道值/最大值（Invert 标志 或 Layer 反向枚举）
                if (HasInverseChannel(options))
                {
                    double inv = 0;
                    if (options.Invert)
                    {
                        // Invert：按所选通道组合取反（未选通道按 1 计）
                        double chosen = 0, chosenMax = 0;
                        if (useR) { chosen += c.Red; chosenMax += rMax; }
                        if (useG) { chosen += c.Green; chosenMax += gMax; }
                        if (useB) { chosen += c.Blue; chosenMax += bMax; }
                        if (useA) { chosen += c.Alpha; chosenMax += aMax; }
                        inv = chosenMax > 0 ? 1.0 - chosen / chosenMax : 1.0;
                    }
                    else
                    {
                        switch (options.Layer)
                        {
                            case LayerSelection.InverseR: inv = 1 - r; break;
                            case LayerSelection.InverseG: inv = 1 - g; break;
                            case LayerSelection.InverseB: inv = 1 - b; break;
                            case LayerSelection.InverseA: inv = 1 - a; break;
                        }
                    }
                    p = inv;
                }

                // ③ 透明/无效像素（p<=0）直接跳过——否则 p=0 也进候选占网格位，透明区也会出点（生成方形）
                if (p <= 0 || p < options.Threshold) continue;

                double weighted = Math.Pow(p, options.Gamma);
                weights[y * w + x] = weighted;
                totalWeight += weighted;
                validPixels.Add((x, y, weighted));
            }
        }

        if (validPixels.Count == 0)
        {
            _logger.LogWarning("图像无有效像素（全部低于阈值 {Threshold}），返回空点集", options.Threshold);
            return new List<Vector2>();
        }

        // 像素坐标 → 逻辑坐标（范围 = 用户设置的图像宽/高地图单位；未设回退全图 [-500,500]²；
        // 中心 = 图像所在位置 options.CenterX/Y——不再固定生成到地图中心）
        double spanX = options.TargetWidth > 0 ? options.TargetWidth : 1000.0;
        double spanY = options.TargetHeight > 0 ? options.TargetHeight : 1000.0;
        Vector2 ToLogical(int px, int py)
            => new(
                (float)(px / (double)(w - 1) * spanX - spanX / 2.0 + options.CenterX),
                (float)(spanY / 2.0 - py / (double)(h - 1) * spanY + options.CenterY));

        var result = new List<Vector2>();

        if (options.Mode == GenerationMode.Spacing)
        {
            // 5.4a（用户算法）：恒星点数 = 有效面积权重占比 × (范围面积 / 间隔²)，再按权重比例落点。
            // 间隔 = MinDistance（用户可设）；网格过滤保证点间最小间隔。
            double area = spanX * spanY;
            double interval = options.MinDistance > 0 ? options.MinDistance : 10.0;
            double areaRatio = (w * h) > 0 ? totalWeight / (double)(w * h) : 0.0;
            int desired = Math.Max(1, (int)(area / (interval * interval) * areaRatio * options.Density));
            desired = Math.Min(desired, validPixels.Count);

            var grid = new Grid2D(interval);
            var rngSp = new Random();
            int attempts = 0;
            while (result.Count < desired && attempts < options.MaxAttempts)
            {
                attempts++;
                double roll = rngSp.NextDouble() * totalWeight;
                double cum = 0;
                int idx = validPixels.Count - 1;
                foreach (var p in validPixels)
                {
                    cum += p.Weight;
                    if (roll <= cum) { idx = validPixels.IndexOf(p); break; }
                }
                var cand = ToLogical(validPixels[idx].X, validPixels[idx].Y);
                if (!grid.HasNearby(cand, interval))
                {
                    grid.Add(cand);
                    result.Add(cand);
                }
            }
            if (result.Count < desired)
                _logger.LogWarning("图像生成未达到目标数 {Target}（实际 {Actual}），已返回部分结果", desired, result.Count);
        }
        else
        {
            // 5.4b：按总数加权随机采样
            var rng = new Random();
            var grid = new Grid2D(options.MinDistance);
            int attempts = 0;
            while (result.Count < options.TotalCount && attempts < options.MaxAttempts)
            {
                attempts++;
                double roll = rng.NextDouble() * totalWeight;
                double cum = 0;
                int idx = validPixels.Count - 1;
                foreach (var p in validPixels)
                {
                    cum += p.Weight;
                    if (roll <= cum) { idx = validPixels.IndexOf(p); break; }
                }
                var cand = ToLogical(validPixels[idx].X, validPixels[idx].Y);
                if (!grid.HasNearby(cand, options.MinDistance))
                {
                    grid.Add(cand);
                    result.Add(cand);
                }
            }

            if (result.Count < options.TotalCount)
                _logger.LogWarning("图像采样未达到目标数 {Target}（实际 {Actual}），已返回部分结果",
                    options.TotalCount, result.Count);
        }

        return result;
    }

    private static (bool R, bool G, bool B, bool A) ResolveChannels(ImageGenerationOptions o)
    {
        // ARGB 任意组合（用户要求）：显式 UseR/G/B/A 优先
        if (o.UseR || o.UseG || o.UseB || o.UseA)
            return (o.UseR, o.UseG, o.UseB, o.UseA);
        if (o.Composite)
        {
            // 复合模式：默认全部图层
            return (true, true, true, true);
        }
        switch (o.Layer)
        {
            case LayerSelection.R: return (true, false, false, false);
            case LayerSelection.G: return (false, true, false, false);
            case LayerSelection.B: return (false, false, true, false);
            case LayerSelection.A: return (false, false, false, true);
            case LayerSelection.InverseR: return (true, false, false, false);
            case LayerSelection.InverseG: return (false, true, false, false);
            case LayerSelection.InverseB: return (false, false, true, false);
            case LayerSelection.InverseA: return (false, false, false, true);
            default: return (true, true, true, true); // None：全部图层
        }
    }

    private static bool HasInverseChannel(ImageGenerationOptions o)
        => o.Invert || HasInverseChannel(o.Layer);

    private static bool HasInverseChannel(LayerSelection layer)
        => layer is LayerSelection.InverseR or LayerSelection.InverseG
            or LayerSelection.InverseB or LayerSelection.InverseA;

    /// <summary>简易空间网格（相邻点最小距离检查）。</summary>
    private sealed class Grid2D
    {
        private readonly float _cell;
        private readonly Dictionary<(int, int), List<Vector2>> _grid = new();

        public Grid2D(double cellSize)
            => _cell = cellSize > 0 ? (float)cellSize : 1.0f;

        private (int, int) Key(Vector2 p)
            => ((int)Math.Floor(p.X / _cell), (int)Math.Floor(p.Y / _cell));

        public void Add(Vector2 p)
        {
            var k = Key(p);
            if (!_grid.TryGetValue(k, out var list))
            {
                list = new List<Vector2>();
                _grid[k] = list;
            }
            list.Add(p);
        }

        public bool HasNearby(Vector2 p, double minDist)
        {
            var k = Key(p);
            float d2 = (float)(minDist * minDist);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!_grid.TryGetValue((k.Item1 + dx, k.Item2 + dy), out var list)) continue;
                    foreach (var q in list)
                    {
                        float ex = p.X - q.X, ey = p.Y - q.Y;
                        if (ex * ex + ey * ey < d2) return true;
                    }
                }
            return false;
        }
    }
}
