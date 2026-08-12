using System;
using System.Collections.Generic;
using System.Linq;
using Stellaris.Parser;

namespace Stellaris.Engine.Deposit;

/// <summary>地形（deposit）条目：key + 本地化名（本地化键 = 顶层 block 的 key，无前缀）。</summary>
public sealed class DepositItem
{
    public string Key { get; set; } = "";
    public string LocName { get; set; } = "";
}

/// <summary>
/// 地形引擎：扫描 common/deposits 目录内所有文件的顶层 block，按顶层 block 的 key
/// 找本地化翻译，供 Effect 可视化的 add_deposit / remove_deposit 选择。
/// 读经 SA（GetFilesRecursive + GetConfig），不直接操作磁盘。
/// </summary>
public sealed class DepositEngine
{
    private readonly StellarisAdapter _adapter;
    private readonly string _lang;

    public DepositEngine(StellarisAdapter adapter, string lang = "english")
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _lang = lang;
    }

    /// <summary>全部地形（key + 本地化名，按 key 排序；本地化缺失时 LocName 为空）。</summary>
    public IReadOnlyList<DepositItem> GetDeposits()
    {
        var list = new List<DepositItem>();
        foreach (var rel in _adapter.GetFilesRecursive("common/deposits", "*.txt"))
        {
            var result = _adapter.GetConfig(rel);
            if (result == null)
                continue;
            foreach (var node in result.RootNodes)
            {
                if (node.Type == NodeType.Block && !string.IsNullOrEmpty(node.Key))
                {
                    var loc = _adapter.GetLocalisedText(node.Key, _lang) ?? "";
                    list.Add(new DepositItem { Key = node.Key, LocName = loc });
                }
            }
        }
        return list.OrderBy(d => d.Key, StringComparer.Ordinal).ToList();
    }
}
