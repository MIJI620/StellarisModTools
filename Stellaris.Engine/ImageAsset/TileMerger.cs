// 文件: Stellaris.Engine/ImageAsset/TileMerger.cs

using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellaris.Engine.ImageAsset;

/// <summary>
/// 拼接算法 - 纯静态辅助类
/// 职责：连通分量提取、全局最大非重叠矩形优先选取
/// 符合规范 2.7。
/// </summary>
internal static class TileMerger
{
    private class TileRegion
    {
        public int Index { get; set; }
        public int Row { get; set; }
        public int Col { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>
    /// 使用“全局最大非重叠矩形优先”策略提取拼接区域。
    /// </summary>
    public static List<(int Index, int Row, int Col, int Width, int Height)> ComputeMaximalRectangles(
        int[][] grid, int rows, int cols)
    {
        var allRegions = new List<TileRegion>();
        var visited = new bool[rows, cols];
        var components = new List<HashSet<(int r, int c)>>();

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (visited[r, c] || grid[r][c] == 0) continue;
                int value = grid[r][c];
                var component = GetConnectedComponent(grid, visited, r, c, value, rows, cols);
                if (component.Count > 0)
                {
                    components.Add(component);
                }
            }
        }

        foreach (var component in components)
        {
            var componentRegions = ExtractMaximalRectanglesFromComponent(grid, component, rows, cols);
            allRegions.AddRange(componentRegions);
        }

        return allRegions
            .OrderByDescending(r => r.Width * r.Height)
            .ThenBy(r => r.Row)
            .ThenBy(r => r.Col)
            .Select(r => (r.Index, r.Row, r.Col, r.Width, r.Height))
            .ToList();
    }

    private static HashSet<(int r, int c)> GetConnectedComponent(int[][] grid, bool[,] visited,
        int startR, int startC, int value, int rows, int cols)
    {
        var result = new HashSet<(int, int)>();
        var stack = new Stack<(int, int)>();
        stack.Push((startR, startC));

        while (stack.Count > 0)
        {
            var (r, c) = stack.Pop();
            if (r < 0 || r >= rows || c < 0 || c >= cols) continue;
            if (visited[r, c] || grid[r][c] != value) continue;
            if (result.Contains((r, c))) continue;

            result.Add((r, c));
            visited[r, c] = true;
            stack.Push((r - 1, c));
            stack.Push((r + 1, c));
            stack.Push((r, c - 1));
            stack.Push((r, c + 1));
        }

        return result;
    }

    /// <summary>
    /// 枚举连通分量内所有可能的矩形，按面积排序后贪心选取无重叠矩形。
    /// 符合规范 2.7 的“全局最大非重叠矩形优先”策略。
    /// </summary>
    private static List<TileRegion> ExtractMaximalRectanglesFromComponent(int[][] grid,
        HashSet<(int r, int c)> component, int rows, int cols)
    {
        var allCandidates = new List<TileRegion>();
        var cells = component.ToList();

        // 1. 枚举所有可能的矩形
        for (int i = 0; i < cells.Count; i++)
        {
            var (r1, c1) = cells[i];
            for (int j = i; j < cells.Count; j++)
            {
                var (r2, c2) = cells[j];
                int top = Math.Min(r1, r2);
                int bottom = Math.Max(r1, r2);
                int left = Math.Min(c1, c2);
                int right = Math.Max(c1, c2);

                bool fullyContained = true;
                for (int r = top; r <= bottom && fullyContained; r++)
                {
                    for (int c = left; c <= right; c++)
                    {
                        if (!component.Contains((r, c)))
                        {
                            fullyContained = false;
                            break;
                        }
                    }
                }

                if (fullyContained)
                {
                    int width = right - left + 1;
                    int height = bottom - top + 1;
                    allCandidates.Add(new TileRegion
                    {
                        Index = grid[r1][c1],
                        Row = top,
                        Col = left,
                        Width = width,
                        Height = height
                    });
                }
            }
        }

        // 2. 去重
        var unique = allCandidates
            .GroupBy(r => (r.Row, r.Col, r.Width, r.Height))
            .Select(g => g.First())
            .ToList();

        // 3. 按面积降序排序，平局先行后列
        var sorted = unique
            .OrderByDescending(r => r.Width * r.Height)
            .ThenBy(r => r.Row)
            .ThenBy(r => r.Col)
            .ToList();

        // 4. 贪心选取无重叠矩形
        var finalRegions = new List<TileRegion>();
        var used = new bool[rows, cols];

        foreach (var region in sorted)
        {
            bool overlap = false;
            for (int r = region.Row; r < region.Row + region.Height && !overlap; r++)
            {
                for (int c = region.Col; c < region.Col + region.Width; c++)
                {
                    if (used[r, c]) { overlap = true; break; }
                }
            }

            if (!overlap)
            {
                for (int r = region.Row; r < region.Row + region.Height; r++)
                    for (int c = region.Col; c < region.Col + region.Width; c++)
                        used[r, c] = true;
                finalRegions.Add(region);
            }
        }

        return finalRegions;
    }
}