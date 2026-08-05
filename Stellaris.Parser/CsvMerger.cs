using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellaris.Parser;

/// <summary>
/// CSV 合并器：使用合并后的 CsvData 合并到 AST 块。
/// </summary>
public class CsvMerger
{
    private readonly CsvData _csvData;

    public CsvMerger(CsvData csvData)
    {
        _csvData = csvData ?? new CsvData();
    }

    public void MergeNode(AstNode node)
    {
        if (node == null)
            return;

        if (node.Type == NodeType.Block || node.Type == NodeType.InlineScript)
        {
            MergeBlock(node);
            foreach (var child in node.Children)
                MergeNode(child);
        }
        else if (node.Type == NodeType.List)
        {
            foreach (var child in node.Children)
                MergeNode(child);
        }
    }

    private void MergeBlock(AstNode blockNode)
    {
        if (blockNode.Children == null || blockNode.Children.Count == 0)
            return;

        // 遍历每个索引列名（通常为 "key"）
        string indexColumn = _csvData.HeaderColumns.FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(indexColumn))
            return;

        // 在块中查找键 == indexColumn 的简单节点
        AstNode? matchNode = null;
        foreach (var child in blockNode.Children)
        {
            if (child.Type == NodeType.Simple && child.Key == indexColumn)
            {
                matchNode = child;
                break;
            }
        }

        if (matchNode == null)
            return;

        string? indexValue = matchNode.Value?.ToString();
        if (string.IsNullOrEmpty(indexValue))
            return;

        if (!_csvData.Rows.TryGetValue(indexValue, out var rowData))
            return;

        // 收集块中已存在的键名
        var existingKeys = new HashSet<string>();
        foreach (var child in blockNode.Children)
        {
            if (child.Type == NodeType.Simple && !string.IsNullOrEmpty(child.Key))
                existingKeys.Add(child.Key);
        }

        // 按照表头顺序添加列（跳过索引列）
        foreach (string colName in _csvData.HeaderColumns)
        {
            if (colName == indexColumn)
                continue;
            if (existingKeys.Contains(colName))
                continue;

            if (rowData.TryGetValue(colName, out var val))
            {
                blockNode.Children.Add(new AstNode
                {
                    Type = NodeType.Simple,
                    Key = colName,
                    Value = val,
                    IsQuoted = false,
                    StartLine = blockNode.StartLine,
                    EndLine = blockNode.EndLine,
                    StartColumn = blockNode.StartColumn,
                    EndColumn = blockNode.EndColumn
                });
            }
        }
    }
}