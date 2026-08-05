// 文件: Stellaris.Engine/GalaxyStyle/GalaxyPointGenerator.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Stellaris.Engine.GalaxyStyle;

/// <summary>
/// 星系点阵生成器 - 纯数学计算，无 IO/图像依赖
/// </summary>
public static class GalaxyPointGenerator
{
    // ===== 公共接口 =====

    public static List<Vector2> GeneratePoints(GalaxyShapeParameters param, float endRadius, int direction = 1)
    {
        var points = new List<Vector2>();

        bool hasArms = param.NumArms > 0;
        bool hasRing = param.HasRing;

        if (hasArms)
        {
            float r0 = (float)(param.CoreRadiusPerc * endRadius);
            var armPoints = GenerateSpiralPoints(
                param.NumArms,
                r0,
                endRadius,
                (float)param.Tightness,
                direction,
                (float)param.WidthDeg,
                (float)param.ArmAngleDeg,
                (float)param.StarsMinDist
            );
            points.AddRange(armPoints);
        }

        if (hasRing)
        {
            var ringPoints = GenerateRingPoints(
                (float)param.RingWidth,
                (float)param.RingOffset,
                (float)param.StarsMinDist,
                endRadius
            );
            points.AddRange(ringPoints);
        }

        if (!hasArms && !hasRing)
        {
            float r0 = (float)(param.CoreRadiusPerc * endRadius);
            var diskPoints = GenerateDiskPoints(
                r0,
                endRadius,
                (float)param.StarsMinDist
            );
            points.AddRange(diskPoints);
        }

        return points;
    }

    public static (double Total, double RingArea, double ArmsTotal, List<double> ArmAreas) ComputeAreas(
        GalaxyShapeParameters param,
        float endRadius,
        float step = 5.0f)
    {
        double ringArea = 0.0;
        double armsTotal = 0.0;
        var armAreas = new List<double>();

        bool hasRing = param.HasRing;
        bool hasArms = param.NumArms > 0;
        float r0 = (float)(param.CoreRadiusPerc * endRadius);

        if (hasRing)
        {
            float rMin = endRadius * (float)param.RingOffset;
            float rMax = endRadius * ((float)param.RingOffset + (float)param.RingWidth);
            if (rMax > rMin)
            {
                ringArea = Math.PI * (rMax * rMax - rMin * rMin);
            }
        }

        if (hasArms)
        {
            if (hasRing)
            {
                float rMin = endRadius * (float)param.RingOffset;
                float rMax = endRadius * ((float)param.RingOffset + (float)param.RingWidth);

                if (r0 < rMin)
                {
                    var innerPolys = GetArmPolygonsInRange(
                        param.NumArms,
                        r0,
                        endRadius,
                        r0,
                        rMin,
                        (float)param.Tightness,
                        1,
                        (float)param.WidthDeg,
                        (float)param.ArmAngleDeg,
                        step
                    );
                    foreach (var poly in innerPolys)
                    {
                        double a = PolygonArea(poly);
                        armAreas.Add(a);
                        armsTotal += a;
                    }
                }

                if (rMax < endRadius)
                {
                    var outerPolys = GetArmPolygonsInRange(
                        param.NumArms,
                        r0,
                        endRadius,
                        rMax,
                        endRadius,
                        (float)param.Tightness,
                        1,
                        (float)param.WidthDeg,
                        (float)param.ArmAngleDeg,
                        step
                    );
                    foreach (var poly in outerPolys)
                    {
                        double a = PolygonArea(poly);
                        armAreas.Add(a);
                        armsTotal += a;
                    }
                }
            }
            else
            {
                var polys = GetArmPolygons(
                    param.NumArms,
                    r0,
                    endRadius,
                    (float)param.Tightness,
                    1,
                    (float)param.WidthDeg,
                    (float)param.ArmAngleDeg,
                    step
                );
                foreach (var poly in polys)
                {
                    double a = PolygonArea(poly);
                    armAreas.Add(a);
                    armsTotal += a;
                }
            }
        }

        double total = ringArea + armsTotal;

        if (!hasArms && !hasRing)
        {
            total = Math.PI * (endRadius * endRadius - r0 * r0);
            ringArea = total;
            armsTotal = 0.0;
            armAreas.Clear();
        }

        return (total, ringArea, armsTotal, armAreas);
    }

    public static int ComputeRecommendedStars(
        GalaxyShapeParameters param,
        float endRadius,
        double densityFactor = 0.8,
        float step = 5.0f)
    {
        var (total, _, _, _) = ComputeAreas(param, endRadius, step);
        double d = param.StarsMinDist;
        int stars = (int)(total / (d * d) * densityFactor);
        return Math.Max(0, stars);
    }

    // ===== 螺旋臂点阵 =====

    public static List<Vector2> GenerateSpiralPoints(
        int numArms,
        float r0,
        float endR,
        float tightness,
        int dirSign,
        float widthDeg,
        float armAngleDeg,
        float step)
    {
        var points = new List<Vector2>();

        if (numArms <= 0 || r0 >= endR || step <= 0)
            return points;

        var radii = SampleRadii(r0, endR, step);

        double totalTheta = tightness * 2.0 * Math.PI;

        double startRatio = r0 / endR;
        double autoOffsetDeg = -dirSign * tightness * 360.0 * startRatio;
        double autoOffsetRad = autoOffsetDeg * Math.PI / 180.0;

        double halfWidthRad = (widthDeg / 2.0) * Math.PI / 180.0;

        for (int armIdx = 0; armIdx < numArms; armIdx++)
        {
            double baseAngle = autoOffsetRad + armIdx * armAngleDeg * Math.PI / 180.0;

            foreach (float r in radii)
            {
                double theta = totalTheta * (r / endR);
                double centerPhi = baseAngle + dirSign * theta;

                double arcLength = r * 2.0 * halfWidthRad;
                int numSteps;
                if (arcLength <= 0)
                {
                    numSteps = 1;
                }
                else
                {
                    numSteps = Math.Max(1, (int)(arcLength / step) + 1);
                }

                double halfArc = halfWidthRad;
                if (numSteps == 1)
                {
                    float x = r * (float)Math.Cos(centerPhi);
                    float y = r * (float)Math.Sin(centerPhi);
                    points.Add(new Vector2(x, y));
                }
                else
                {
                    for (int k = 0; k < numSteps; k++)
                    {
                        double t = (double)k / (numSteps - 1);
                        double phi = centerPhi - halfArc + t * 2.0 * halfArc;
                        float x = r * (float)Math.Cos(phi);
                        float y = r * (float)Math.Sin(phi);
                        points.Add(new Vector2(x, y));
                    }
                }
            }
        }

        return points;
    }

    // ===== 环点阵 =====

    public static List<Vector2> GenerateRingPoints(
        float ringWidth,
        float ringOffset,
        float step,
        float maxRadius)
    {
        var points = new List<Vector2>();

        float rMin = maxRadius * ringOffset;
        float rMax = maxRadius * (ringOffset + ringWidth);

        if (rMin >= rMax || step <= 0)
            return points;

        var radii = SampleRadii(rMin, rMax, step);

        foreach (float r in radii)
        {
            double circumference = 2.0 * Math.PI * r;
            // 按周长与目标间隔四舍五入取点数，再等分周长（首尾闭合）
            int numAngles = Math.Max(1, (int)Math.Round(circumference / step));
            for (int k = 0; k <= numAngles; k++)
            {
                double theta = 2.0 * Math.PI * k / numAngles;
                float x = r * (float)Math.Cos(theta);
                float y = r * (float)Math.Sin(theta);
                points.Add(new Vector2(x, y));
            }
        }

        return points;
    }

    // ===== 圆盘点阵 =====

    public static List<Vector2> GenerateDiskPoints(
        float r0,
        float endR,
        float step)
    {
        if (r0 >= endR || step <= 0)
            return new List<Vector2>();

        var radii = SampleRadii(r0, endR, step);
        var points = new List<Vector2>(radii.Length * 16);

        foreach (float r in radii)
        {
            double circumference = 2.0 * Math.PI * r;
            // 按周长与目标间隔四舍五入取点数，再等分周长（首尾闭合）
            int numAngles = Math.Max(1, (int)Math.Round(circumference / step));
            for (int k = 0; k <= numAngles; k++)
            {
                double theta = 2.0 * Math.PI * k / numAngles;
                float x = r * (float)Math.Cos(theta);
                float y = r * (float)Math.Sin(theta);
                points.Add(new Vector2(x, y));
            }
        }

        return points;
    }

    // ===== 采样半径序列 =====

    private static float[] SampleRadii(float start, float end, float step)
    {
        if (start >= end || step <= 0)
            return Array.Empty<float>();

        var list = new List<float>();
        float r = start;
        while (r <= end + step * 0.5f)
        {
            if (r > end)
                break;
            list.Add(r);
            r += step;
        }

        if (list.Count > 0 && Math.Abs(list[^1] - end) > step * 0.5f)
        {
            list.Add(end);
        }

        return list.ToArray();
    }

    // ===== 旋臂多边形（面积计算和几何查询） =====

    public static List<List<Vector2>> GetArmPolygons(
        int numArms,
        float r0,
        float endR,
        float tightness,
        int dirSign,
        float widthDeg,
        float armAngleDeg,
        float step = 5.0f)
    {
        return GetArmPolygonsInRange(numArms, r0, endR, r0, endR, tightness, dirSign, widthDeg, armAngleDeg, step);
    }

    public static List<List<Vector2>> GetArmPolygonsInRange(
        int numArms,
        float r0,
        float endR,
        float rMin,
        float rMax,
        float tightness,
        int dirSign,
        float widthDeg,
        float armAngleDeg,
        float step = 5.0f)
    {
        var polygons = new List<List<Vector2>>();

        if (numArms <= 0 || rMin >= rMax || r0 >= endR)
            return polygons;

        float startR = Math.Max(r0, rMin);
        float endR_adj = Math.Min(endR, rMax);
        if (startR >= endR_adj)
            return polygons;

        var radii = SampleRadii(startR, endR_adj, step);

        double totalTheta = tightness * 2.0 * Math.PI;
        double startRatio = r0 / endR;
        double autoOffsetDeg = -dirSign * tightness * 360.0 * startRatio;
        double autoOffsetRad = autoOffsetDeg * Math.PI / 180.0;
        double halfWidthRad = (widthDeg / 2.0) * Math.PI / 180.0;

        for (int armIdx = 0; armIdx < numArms; armIdx++)
        {
            double baseAngle = autoOffsetRad + armIdx * armAngleDeg * Math.PI / 180.0;

            double thetaStart = totalTheta * (radii[0] / endR);
            double centerStart = baseAngle + dirSign * thetaStart;
            double leftStart = centerStart - halfWidthRad;
            double rightStart = centerStart + halfWidthRad;
            int startNum = Math.Max(2, (int)(radii[0] * 2.0 * halfWidthRad / step) + 1);
            var startArc = new List<Vector2>(startNum);
            for (int k = 0; k < startNum; k++)
            {
                double t = (double)k / (startNum - 1);
                double phi = leftStart + t * (rightStart - leftStart);
                float x = radii[0] * (float)Math.Cos(phi);
                float y = radii[0] * (float)Math.Sin(phi);
                startArc.Add(new Vector2(x, y));
            }

            double thetaEnd = totalTheta * (radii[^1] / endR);
            double centerEnd = baseAngle + dirSign * thetaEnd;
            double leftEnd = centerEnd - halfWidthRad;
            double rightEnd = centerEnd + halfWidthRad;
            int endNum = Math.Max(2, (int)(radii[^1] * 2.0 * halfWidthRad / step) + 1);
            var endArc = new List<Vector2>(endNum);
            for (int k = endNum - 1; k >= 0; k--)
            {
                double t = (double)k / (endNum - 1);
                double phi = leftEnd + t * (rightEnd - leftEnd);
                float x = radii[^1] * (float)Math.Cos(phi);
                float y = radii[^1] * (float)Math.Sin(phi);
                endArc.Add(new Vector2(x, y));
            }

            var leftPoints = new List<Vector2>();
            var rightPoints = new List<Vector2>();
            for (int i = 1; i < radii.Length - 1; i++)
            {
                float r = radii[i];
                double theta = totalTheta * (r / endR);
                double center = baseAngle + dirSign * theta;
                double phiLeft = center - halfWidthRad;
                double phiRight = center + halfWidthRad;
                leftPoints.Add(new Vector2(r * (float)Math.Cos(phiLeft), r * (float)Math.Sin(phiLeft)));
                rightPoints.Add(new Vector2(r * (float)Math.Cos(phiRight), r * (float)Math.Sin(phiRight)));
            }

            var poly = new List<Vector2>(startArc.Count + rightPoints.Count + endArc.Count + leftPoints.Count);
            poly.AddRange(startArc);
            poly.AddRange(rightPoints);
            poly.AddRange(endArc);
            for (int i = leftPoints.Count - 1; i >= 0; i--)
                poly.Add(leftPoints[i]);

            polygons.Add(poly);
        }

        return polygons;
    }

    // ===== 多边形面积（鞋带公式） =====

    public static double PolygonArea(List<Vector2> poly)
    {
        if (poly.Count < 3)
            return 0.0;

        double area = 0.0;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            area += poly[i].X * poly[j].Y;
            area -= poly[j].X * poly[i].Y;
        }
        return Math.Abs(area) / 2.0;
    }

    // ===== 空间哈希网格 =====

    public sealed class SpatialGrid
    {
        private readonly float _cellSize;
        private readonly Dictionary<(int, int), List<Vector2>> _grid;

        public SpatialGrid(float cellSize)
        {
            _cellSize = cellSize > 0 ? cellSize : 1.0f;
            _grid = new Dictionary<(int, int), List<Vector2>>();
        }

        private (int X, int Y) GetKey(Vector2 p)
        {
            return ((int)Math.Floor(p.X / _cellSize), (int)Math.Floor(p.Y / _cellSize));
        }

        public void Add(Vector2 p)
        {
            var key = GetKey(p);
            if (!_grid.TryGetValue(key, out var list))
            {
                list = new List<Vector2>();
                _grid[key] = list;
            }
            list.Add(p);
        }

        public bool HasNearby(Vector2 p, float minDist)
        {
            var key = GetKey(p);
            float minDistSq = minDist * minDist;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    var neighborKey = (key.X + dx, key.Y + dy);
                    if (_grid.TryGetValue(neighborKey, out var list))
                    {
                        foreach (var q in list)
                        {
                            float dx2 = p.X - q.X;
                            float dy2 = p.Y - q.Y;
                            if (dx2 * dx2 + dy2 * dy2 < minDistSq)
                                return true;
                        }
                    }
                }
            }
            return false;
        }
    }

    // ===== 面积加权采样 =====

    public static List<Vector2> SamplePointsByArea(
        List<Vector2> candidates,
        int targetCount,
        float minDist)
    {
        if (candidates.Count == 0 || targetCount <= 0)
            return new List<Vector2>();

        var shuffled = candidates.OrderBy(_ => Guid.NewGuid()).ToList();

        var grid = new SpatialGrid(minDist);
        var result = new List<Vector2>(Math.Min(targetCount, shuffled.Count));

        foreach (var p in shuffled)
        {
            if (result.Count >= targetCount)
                break;
            if (!grid.HasNearby(p, minDist))
            {
                result.Add(p);
                grid.Add(p);
            }
        }

        return result;
    }

    // =========================================================================
    // 新增几何查询专用多边形方法（§10.3）
    // =========================================================================

    /// <summary>
    /// 生成环状多边形的顶点列表（外弧正向 + 内弧反向），用于几何查询。
    /// 外弧/内弧均精确闭合到 0°（含 2π 采样点），使 0° 处两条径向断面完全重合，
    /// 消除圆环在 0° 方向的微小楔形缺口（细缝）与重合叠加。
    /// </summary>
    public static List<Vector2> GetRingPolygon(
        float ringWidth,
        float ringOffset,
        float maxRadius,
        float step = 5.0f)
    {
        float Rmin = maxRadius * ringOffset;
        float Rmax = maxRadius * (ringOffset + ringWidth);
        if (Rmin >= Rmax)
            return new List<Vector2>();

        // 外弧：0° → 2π（含闭合点，i=outerCount 时 t=2π，回到 0°）
        int outerCount = Math.Max(4, (int)(2 * Math.PI * Rmax / step) + 1);
        var outerPoints = new List<Vector2>(outerCount + 1);
        for (int i = 0; i <= outerCount; i++)
        {
            double t = (double)i / outerCount * 2 * Math.PI;
            outerPoints.Add(new Vector2(Rmax * (float)Math.Cos(t), Rmax * (float)Math.Sin(t)));
        }

        // 内弧：2π → 0°（含闭合点，i=0 时 t=0，回到 0°）
        int innerCount = Math.Max(4, (int)(2 * Math.PI * Rmin / step) + 1);
        var innerPoints = new List<Vector2>(innerCount + 1);
        for (int i = innerCount; i >= 0; i--)
        {
            double t = (double)i / innerCount * 2 * Math.PI;
            innerPoints.Add(new Vector2(Rmin * (float)Math.Cos(t), Rmin * (float)Math.Sin(t)));
        }

        var polygon = new List<Vector2>(outerPoints.Count + innerPoints.Count);
        polygon.AddRange(outerPoints);
        polygon.AddRange(innerPoints);
        return polygon;
    }

    /// <summary>
    /// 生成圆盘多边形（相当于内径为 startRadius 的环）。
    /// </summary>
    public static List<Vector2> GetDiskPolygon(
        float startRadius,
        float endRadius,
        float step = 5.0f)
    {
        if (startRadius >= endRadius)
            return new List<Vector2>();

        float ringWidth = (endRadius - startRadius) / endRadius;
        float ringOffset = startRadius / endRadius;
        return GetRingPolygon(ringWidth, ringOffset, endRadius, step);
    }
}