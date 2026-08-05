// 文件: Stellaris.Engine/GalaxyMap/GalaxyMapEngine_Lattice.cs
// 网格与晶格生成（规范 4.8）：三角形细分 / 正方形网格 / 六边形同心环。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.Logging;

namespace Stellaris.Engine.GalaxyMap;

public sealed partial class GalaxyMapEngine
{
    /// <summary>网格生成结果（预览用，不写入内存）。</summary>
    public sealed class LatticeResult
    {
        public List<Vector2> Points { get; } = new();
        public List<(int A, int B)> Edges { get; } = new();
    }

    /// <summary>预计算网格点集与航道（不写入内存，规范 4.8.4）。</summary>
    public LatticeResult PreviewLattice(LatticeGenerationOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.SideLength <= 0 || options.Spacing <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "SideLength 与 Spacing 必须为正数（规范 6.6）");
        if (options.Spacing > options.SideLength)
            throw new InvalidOperationException("Spacing 不能大于 SideLength，请减小间距或增大边长（规范 6.6）");

        var result = options.ShapeType switch
        {
            LatticeShape.Triangle => BuildTriangle(options),
            LatticeShape.Square => BuildSquare(options),
            LatticeShape.Hexagon => BuildHexagon(options),
            _ => throw new ArgumentOutOfRangeException(nameof(options.ShapeType))
        };

        // 6.3：整体缩放至 [-500,500]² 方形内（保持中心）
        ClampToBounds(result.Points);

        if (result.Points.Count > 10000)
            throw new InvalidOperationException(
                $"生成点数 {result.Points.Count} 超过 10000，请调整参数（规范 6.6）");

        return result;
    }

    /// <summary>正式将网格点与航道写入静态地图（规范 4.8.4 / 4.8.3 步骤 5-7）。</summary>
    public void ApplyLattice(string mapName, LatticeGenerationOptions options)
    {
        lock (_syncRoot)
        {
            SetTask(GalaxyMapTaskType.GeneratingLattice, mapName);
            try
            {
                var s = _staticScenarios.GetValueOrDefault(mapName)
                        ?? throw new KeyNotFoundException($"静态地图 '{mapName}' 不存在");

                var result = PreviewLattice(options);

                int startIndex = s.Systems.Count;
                var systemIndex = new int[result.Points.Count];
                for (int i = 0; i < systemIndex.Length; i++) systemIndex[i] = -1;

                // 4.8.3 步骤 7：与已有系统坐标重叠（距离 < 0.5）的点跳过并警告
                int skipped = 0, added = 0;
                for (int i = 0; i < result.Points.Count; i++)
                {
                    var p = result.Points[i];
                    bool overlap = s.Systems.Any(e =>
                    {
                        var (ex, ey) = SamplePosition(e);
                        double dx = ex - p.X, dy = ey - p.Y;
                        return dx * dx + dy * dy < 0.25;
                    });
                    if (overlap) { skipped++; continue; }

                    int newIndex = startIndex + added;
                    systemIndex[i] = newIndex;
                    s.Systems.Add(new SystemEntry
                    {
                        Id = $"__new_{newIndex}", // 保存时统一重编号（4.2）
                        Position = new SystemPosition { X = p.X, Y = p.Y }
                    });
                    added++;
                }

                // 航道：按生成的边连接新系统（两端都成功添加的边才写入）
                foreach (var (a, b) in result.Edges)
                {
                    if (systemIndex[a] < 0 || systemIndex[b] < 0) continue;
                    s.Hyperlanes.Add(new Hyperlane($"__new_{systemIndex[a]}", $"__new_{systemIndex[b]}"));
                }

                if (skipped > 0)
                    _logger.LogWarning("网格生成跳过 {Count} 个重叠点（距离 < 0.5）", skipped);

                _logger.LogInformation("网格生成完成: {Map}（新增 {Count} 点、{Edges} 条航道）",
                    mapName, added, result.Edges.Count);
            }
            finally
            {
                SetTask(GalaxyMapTaskType.Idle);
            }
        }
    }

    /// <summary>生成并写入（一步式，规范 4.8.2）。</summary>
    public void GenerateLattice(string mapName, LatticeGenerationOptions options)
        => ApplyLattice(mapName, options);

    // ===== 4.8.3 生成实现 =====

    private static LatticeResult BuildTriangle(LatticeGenerationOptions o)
    {
        var result = new LatticeResult();
        var pointIndex = new Dictionary<(long, long), int>();

        // 正三角形三个顶点（外接圆半径 = SideLength / sqrt(3)，顶点朝上）
        double R = o.SideLength / Math.Sqrt(3.0);
        var v0 = new Vector2((float)o.CenterX, (float)(o.CenterY + R));
        var v1 = new Vector2((float)(o.CenterX - R * 0.5), (float)(o.CenterY - R * Math.Sqrt(3) / 2.0));
        var v2 = new Vector2((float)(o.CenterX + R * 0.5), (float)(o.CenterY - R * Math.Sqrt(3) / 2.0));

        SubdivideTriangle(o, v0, v1, v2, result, pointIndex);
        return result;
    }

    private static void SubdivideTriangle(LatticeGenerationOptions o,
        Vector2 a, Vector2 b, Vector2 c,
        LatticeResult result, Dictionary<(long, long), int> pointIndex)
    {
        if (Vector2.Distance(a, b) <= (float)o.Spacing)
        {
            int ia = GetOrAdd(a, result, pointIndex);
            int ib = GetOrAdd(b, result, pointIndex);
            int ic = GetOrAdd(c, result, pointIndex);
            AddEdge(result, ia, ib);
            AddEdge(result, ib, ic);
            AddEdge(result, ic, ia);
            return;
        }

        var mAB = (a + b) * 0.5f;
        var mBC = (b + c) * 0.5f;
        var mCA = (c + a) * 0.5f;

        SubdivideTriangle(o, a, mAB, mCA, result, pointIndex);
        SubdivideTriangle(o, mAB, b, mBC, result, pointIndex);
        SubdivideTriangle(o, mCA, mBC, c, result, pointIndex);
        SubdivideTriangle(o, mAB, mBC, mCA, result, pointIndex);
    }

    private static LatticeResult BuildSquare(LatticeGenerationOptions o)
    {
        var result = new LatticeResult();
        int n = Math.Max(1, (int)Math.Floor(o.SideLength / o.Spacing));
        double step = o.SideLength / n;

        var index = new Dictionary<(int, int), int>();
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                double x = o.CenterX + (i - n / 2.0) * step;
                double y = o.CenterY + (j - n / 2.0) * step;
                index[(i, j)] = result.Points.Count;
                result.Points.Add(new Vector2((float)x, (float)y));
            }
        }

        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                if (i < n) AddEdge(result, index[(i, j)], index[(i + 1, j)]);
                if (j < n) AddEdge(result, index[(i, j)], index[(i, j + 1)]);
            }

        return result;
    }

    private static LatticeResult BuildHexagon(LatticeGenerationOptions o)
    {
        // 4.8.1-a：同心六边形环，cube 坐标 (x,y,z)，x+y+z=0，环 k = max(|x|,|y|,|z|)。
        // 按 y 分层（-k..k），行宽 k+1..2k+1..k+1（232 / 34543 / 4567654）。
        var result = new LatticeResult();
        int kmax = Math.Max(1, (int)Math.Floor(o.SideLength / o.Spacing));

        var points = new List<(int X, int Y, int Z)>();
        for (int k = 1; k <= kmax; k++)
        {
            for (int y = -k; y <= k; y++)
            {
                // x 满足 |x|<=k, |x+y|<=k
                int xMin = Math.Max(-k, -k - y);
                int xMax = Math.Min(k, k - y);
                for (int x = xMin; x <= xMax; x++)
                {
                    int z = -x - y;
                    if (Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)), Math.Abs(z)) == k)
                        points.Add((x, y, z));
                }
            }
        }

        // 中心点
        points.Insert(0, (0, 0, 0));

        // cube 坐标 → 逻辑坐标（相邻点距离 = Spacing）
        var index = new Dictionary<(int, int, int), int>();
        double sq3 = Math.Sqrt(3.0);
        foreach (var p in points)
        {
            double px = o.CenterX + o.Spacing * (p.X + p.Y * 0.5);
            double py = o.CenterY + o.Spacing * (sq3 / 2.0) * p.Y;
            index[p] = result.Points.Count;
            result.Points.Add(new Vector2((float)px, (float)py));
        }

        // 边：cube 6 邻居
        var dirs = new (int, int, int)[]
        {
            (1, -1, 0), (-1, 1, 0), (0, 1, -1), (0, -1, 1), (1, 0, -1), (-1, 0, 1)
        };
        foreach (var p in points)
        {
            foreach (var (dx, dy, dz) in dirs)
            {
                var n = (p.X + dx, p.Y + dy, p.Z + dz);
                if (index.TryGetValue(n, out int nb))
                    AddEdge(result, index[p], nb);
            }
        }

        return result;
    }

    private static int GetOrAdd(Vector2 p, LatticeResult result, Dictionary<(long, long), int> index)
    {
        var key = ((long)Math.Round(p.X * 100), (long)Math.Round(p.Y * 100));
        if (index.TryGetValue(key, out int i))
            return i;
        index[key] = result.Points.Count;
        result.Points.Add(p);
        return result.Points.Count - 1;
    }

    private static void AddEdge(LatticeResult result, int a, int b)
    {
        if (a == b) return;
        var key = a < b ? (a, b) : (b, a);
        if (!result.Edges.Contains(key))
            result.Edges.Add(key);
    }

    // ===== 6.3 边界缩放 =====

    private static void ClampToBounds(List<Vector2> points)
    {
        if (points.Count == 0) return;
        float maxAbs = 0f;
        foreach (var p in points)
        {
            maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
        }
        if (maxAbs <= 500f) return;

        float scale = 500f / maxAbs;
        for (int i = 0; i < points.Count; i++)
            points[i] = new Vector2(points[i].X * scale, points[i].Y * scale);
    }
}
