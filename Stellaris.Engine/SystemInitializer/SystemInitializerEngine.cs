// 文件: Stellaris.Engine/SystemInitializer/SystemInitializerEngine.cs
// 恒星系预设引擎（solar system initializer，群星 common/solar_system_initializers/*.txt）。
// 当前阶段：仅提供扫描能力（列出可用的星系预设名，供静态地图"点设置"的 initializer 下拉使用）。
// 后续阶段：可视化编辑（树形参数编辑）、写回 mod 目录、与静态地图 initializer 字段联动——见规范文件。
// 铁律：引擎层绝不直接操作磁盘/底层——一律经 StellarisAdapter（GetAllLoadedFiles / GetConfig）。

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Parser;

namespace Stellaris.Engine.SystemInitializer;

/// <summary>
/// 恒星系预设引擎：负责 solar_system_initializers 的扫描与（后续）可视化编辑。
/// 所有文件读写一律经 StellarisAdapter（用户规则），本引擎只做业务编排。
/// </summary>
public sealed class SystemInitializerEngine
{
    private readonly StellarisAdapter _adapter;
    private readonly ILogger _logger;

    public SystemInitializerEngine(StellarisAdapter adapter, ILogger? logger = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger ?? NullLogger.Instance;
    }

    // ==================== 扫描（第一阶段能力） ====================

    /// <summary>
    /// 扫描全部已加载的 common/solar_system_initializers/*.txt，收集星系预设（initializer）名。
    /// 经 StellarisAdapter 解析 AST（不直接读磁盘），取每个文件顶级 Block 的 key；返回去重排序列表。
    /// </summary>
    public List<string> GetAvailableInitializers()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var relPath in _adapter.GetAllLoadedFiles().Keys)
            {
                if (!relPath.StartsWith("common/solar_system_initializers/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!relPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                var result = _adapter.GetConfig(relPath);
                if (result == null)
                    continue;
                foreach (var node in result.RootNodes)
                {
                    if (node.Type == NodeType.Block && !string.IsNullOrEmpty(node.Key))
                        set.Add(node.Key);
                }
            }
        }
        catch
        {
            // 扫描失败返回空
        }
        return set.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    // ==================== 规范 ====================
    // 详见 SystemInitializerSpecification.cs（章节：职责、数据来源、第一阶段扫描、后续规划、API 索引）。
}
