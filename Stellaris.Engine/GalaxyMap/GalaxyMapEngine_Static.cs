// 文件: Stellaris.Engine/GalaxyMap/GalaxyMapEngine_Static.cs
// 静态地图：ID 重编号（4.2）、单点编辑（4.9/4.10）、资产导出（4.6）。

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Stellaris.Engine.GalaxyMap;

public partial class GalaxyMapEngine
{
    // ===== 4.2 ID 重编号与航道同步 =====

    private void RenumberIds(StaticScenario s)
    {
        int n = s.Systems.Count;
        int padWidth = Math.Max(_minIdPadding, n > 0 ? (int)Math.Ceiling(Math.Log10(n + 1)) : 1);
        padWidth = Math.Max(1, padWidth);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < s.Systems.Count; i++)
        {
            string newId = i.ToString("D" + padWidth);
            map[s.Systems[i].Id] = newId;
            s.Systems[i].Id = newId;
        }

        foreach (var h in s.Hyperlanes)
        {
            h.From = map.GetValueOrDefault(h.From, h.From);
            h.To = map.GetValueOrDefault(h.To, h.To);
        }
        foreach (var h in s.PreventedHyperlanes)
        {
            h.From = map.GetValueOrDefault(h.From, h.From);
            h.To = map.GetValueOrDefault(h.To, h.To);
        }
    }

    // ===== 4.9 单点超空间航道创建（Connect To 3 Neighbors）=====

    /// <summary>
    /// 为指定系统创建到最近 3 个邻居的超空间航道。
    /// 点数不足 4 时抛 InvalidOperationException；系统不存在抛 KeyNotFoundException。
    /// </summary>
    public void ConnectToThreeNeighbors(string mapName, string systemId)
    {
        lock (_syncRoot)
        {
            var s = _staticScenarios.GetValueOrDefault(mapName)
                    ?? throw new KeyNotFoundException($"静态地图 '{mapName}' 不存在");

            var target = s.Systems.FirstOrDefault(x => x.Id == systemId)
                         ?? throw new KeyNotFoundException($"系统 '{systemId}' 不存在");

            if (s.Systems.Count < 4)
                throw new InvalidOperationException(
                    $"无法生成 3 条航道：地图 '{mapName}' 当前只有 {s.Systems.Count} 个点，建议先生成更多点（规范 6.7）");

            var (tx, ty) = SamplePosition(target);
            var candidates = s.Systems
                .Where(x => x.Id != systemId)
                .Select(x => (Entry: x, Dist: DistanceSq(tx, ty, x)))
                .Where(x => x.Dist > 0.01) // 距离 > 0.1 防自连（平方阈值 0.01）
                .OrderBy(x => x.Dist)
                .Take(3)
                .ToList();

            foreach (var (entry, _) in candidates)
            {
                var h = new Hyperlane(systemId, entry.Id);
                bool exists = s.Hyperlanes.Any(x =>
                    (x.From == h.From && x.To == h.To) || (x.From == h.To && x.To == h.From));
                if (exists)
                {
                    _logger.LogDebug("航道已存在，跳过: {From} -> {To}", h.From, h.To);
                    continue;
                }
                s.Hyperlanes.Add(h);
            }

            _logger.LogInformation("为系统 {Id} 连接最近 3 个邻居", systemId);
        }
    }

    private static double DistanceSq(double x1, double y1, SystemEntry e)
    {
        var (x2, y2) = (e.Position.GetX(), e.Position.GetY());
        double dx = x1 - x2, dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    // ===== 4.10 单点类别设置 =====

    /// <summary>
    /// 设置系统类别（category 子节点）。类别为 null/空白时重置为 "normal"（规范 6.8）。
    /// </summary>
    public void SetSystemCategory(string mapName, string systemId, string category)
    {
        lock (_syncRoot)
        {
            var s = _staticScenarios.GetValueOrDefault(mapName)
                    ?? throw new KeyNotFoundException($"静态地图 '{mapName}' 不存在");

            var entry = s.Systems.FirstOrDefault(x => x.Id == systemId)
                        ?? throw new KeyNotFoundException($"系统 '{systemId}' 不存在");

            entry.Category = string.IsNullOrWhiteSpace(category) ? "normal" : category;
        }
    }

    // ===== 4.6 资产导出 =====

    /// <summary>
    /// 导出静态地图伪样式的预览与图标（伪样式已在 GalaxyStyleEngine 注册）。
    /// 使用默认 PreviewOptions / IconOptions。失败返回对应 OperationStatus。
    /// </summary>
    public Stellaris.Engine.ImageAsset.OperationStatus ExportAssets(string mapName, bool forceRebuild = false)
    {
        lock (_syncRoot)
        {
            SetTask(GalaxyMapTaskType.ExportingAssets, mapName);
            try
            {
                if (!_staticScenarios.ContainsKey(mapName))
                    return Stellaris.Engine.ImageAsset.OperationStatus.FileNotFound;

                // 确保伪样式占位已注册且参数最新
                RegisterPseudoStyleInternal(mapName);

                var preview = _styleEngine.ExportSinglePreview(mapName);
                if (preview != Stellaris.Engine.ImageAsset.OperationStatus.Success)
                {
                    _logger.LogError("伪样式预览导出失败: {Name} -> {Status}", mapName, preview);
                    return preview;
                }

                var icon = _styleEngine.ExportSingleIcon(mapName);
                if (icon != Stellaris.Engine.ImageAsset.OperationStatus.Success)
                {
                    _logger.LogError("伪样式图标导出失败: {Name} -> {Status}", mapName, icon);
                    return icon;
                }

                _logger.LogInformation("伪样式资产导出完成: {Name}", mapName);
                return Stellaris.Engine.ImageAsset.OperationStatus.Success;
            }
            finally
            {
                SetTask(GalaxyMapTaskType.Idle);
            }
        }
    }
}
