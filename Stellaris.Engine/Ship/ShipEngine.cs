// 文件: Stellaris.Engine/Ship/ShipEngine.cs
// 舰船引擎：专门处理舰船相关文件夹（component_sets / component_templates / section_templates /
// ship_sizes / global_ship_designs）根 block 的"解锁"索引与本地化名。
// 命名/本地化规则特殊（用户定义）：
//   - component_sets / component_templates / section_templates：标识 = 根 block 内 `key` 字段的 value
//   - ship_sizes：标识 = 根 block key 本身
//   - global_ship_designs：标识 = 根 block 内 `name` 字段的 value（无描述）
// 本地化名键 = 标识本身（描述键 = 标识 + "_desc"——通用规则）。
// 数据源全经 StellarisAdapter（GetFilesInDirectory / GetConfig / GetLocalisedText）。

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Parser;

namespace Stellaris.Engine.Ship;

/// <summary>舰船引擎：舰船文件夹根 block 的解锁索引 + 本地化名（命名规则特殊）。</summary>
public sealed class ShipEngine
{
    private readonly StellarisAdapter _adapter;
    private readonly ILogger _logger;

    // 标识提取规则：component 类用 key 字段 value；ship_sizes 用根 block key；designs 用 name 字段 value（无描述）
    private static readonly string[] KeyValueDirs = { "component_sets", "component_templates", "section_templates" };
    private static readonly string[] KeySelfDirs = { "ship_sizes" };
    private static readonly string[] NameValueDirs = { "global_ship_designs" };

    /// <summary>前置 key → 含该前置的舰船 block 标识列表（解锁反查）。</summary>
    private Dictionary<string, List<string>> _unlocks = new(StringComparer.OrdinalIgnoreCase);
    private bool _scanned;

    public ShipEngine(StellarisAdapter adapter, ILogger? logger = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>扫描舰船文件夹（幂等）：根 block → 标识 + prerequisites 解锁索引。</summary>
    public void ScanAll()
    {
        var unlocks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in KeyValueDirs)
            ScanDir(dir, ScanMode.KeyField, unlocks);
        foreach (var dir in KeySelfDirs)
            ScanDir(dir, ScanMode.BlockKey, unlocks);
        foreach (var dir in NameValueDirs)
            ScanDir(dir, ScanMode.NameField, unlocks);
        _unlocks = unlocks;
        _scanned = true;
        _logger.LogInformation("舰船引擎索引完成（解锁条目 {Count}）", unlocks.Count);
    }

    private enum ScanMode { KeyField, BlockKey, NameField }

    private void ScanDir(string dir, ScanMode mode, Dictionary<string, List<string>> unlocks)
    {
        foreach (var relPath in _adapter.GetFilesInDirectory("common/" + dir, "*.txt"))
        {
            ParserResult? result;
            try
            {
                result = _adapter.GetConfig(relPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "舰船文件夹读取失败: {Path}", relPath);
                continue;
            }
            if (result?.RootNodes == null)
                continue;
            foreach (var node in result.RootNodes)
            {
                if (node.Type != NodeType.Block || string.IsNullOrEmpty(node.Key))
                    continue;
                string ident = ExtractIdent(node, mode);
                if (string.IsNullOrEmpty(ident))
                    continue;
                // prerequisites → 解锁反查
                var pre = node.Children.FirstOrDefault(c =>
                    (c.Type == NodeType.Block || c.Type == NodeType.List)
                    && string.Equals(c.Key, "prerequisites", StringComparison.OrdinalIgnoreCase));
                if (pre != null)
                {
                    foreach (var p in pre.Children)
                    {
                        var pk = p.Value?.ToString() ?? p.Key;
                        if (string.IsNullOrEmpty(pk))
                            continue;
                        if (!unlocks.TryGetValue(pk, out var list))
                            unlocks[pk] = list = new List<string>();
                        if (!list.Contains(ident))
                            list.Add(ident);
                    }
                }
            }
        }
    }

    /// <summary>按文件夹规则提取标识：KeyField = 根 block 内 key 字段 value；BlockKey = 根 block key；NameField = name 字段 value。</summary>
    private static string ExtractIdent(AstNode block, ScanMode mode)
    {
        switch (mode)
        {
            case ScanMode.KeyField:
                return block.Children.FirstOrDefault(c => c.Type == NodeType.Simple
                    && string.Equals(c.Key, "key", StringComparison.OrdinalIgnoreCase))?.Value?.ToString() ?? "";
            case ScanMode.NameField:
                return block.Children.FirstOrDefault(c => c.Type == NodeType.Simple
                    && string.Equals(c.Key, "name", StringComparison.OrdinalIgnoreCase))?.Value?.ToString() ?? "";
            default:
                return block.Key ?? "";
        }
    }

    /// <summary>解锁某科技的舰船 block 标识列表（舰船文件夹根 block 的 prerequisites 含该科技）。</summary>
    public IReadOnlyList<string> GetUnlockingBlocks(string techKey)
    {
        if (!_scanned)
            ScanAll();
        return _unlocks.TryGetValue(techKey, out var list)
            ? list
            : (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>舰船 block 标识 → 本地化名（名字键 = 标识本身，无翻译回退标识）。</summary>
    public string LocalisedName(string ident, string lang = "english")
    {
        var text = _adapter.GetLocalisedText(ident, lang);
        return string.IsNullOrEmpty(text) ? ident : text;
    }

    /// <summary>舰船 block 标识 → 本地化描述（描述键 = 标识 + "_desc"，无翻译返回空串——global_ship_designs 无描述）。</summary>
    public string LocalisedDescription(string ident, string lang = "english")
    {
        return _adapter.GetLocalisedText(ident + "_desc", lang) ?? "";
    }
}
