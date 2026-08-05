using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Stellaris.Parser;

/// <summary>
/// 序列化与写回辅助类。
/// </summary>
public static class SerializationHelper
{
    public static string Serialize(List<AstNode> nodes)
    {
        if (nodes == null || nodes.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            WriteAssociatedComments(node, sb, 0);
            SerializeNode(node, sb, 0);

            if (i < nodes.Count - 1)
                sb.Append("\n\n");
        }
        return sb.ToString();
    }

    private static void SerializeNode(AstNode node, StringBuilder sb, int indentLevel)
    {
        string indent = new string('\t', indentLevel);

        switch (node.Type)
        {
            case NodeType.Comment:
                break;

            case NodeType.Simple:
                if (!string.IsNullOrEmpty(node.Key))
                {
                    string valueStr = FormatSimpleValue(node);
                    string separator = GetSeparatorString(node.SeparatorType);
                    sb.Append($"{indent}{node.Key} {separator} {valueStr}");
                    sb.AppendLine();
                }
                else
                {
                    // 裸值（列表项，如 extra_crisis_strength = { 10 25 50 } 中的 10/25/50）
                    string valueStr = FormatSimpleValue(node);
                    sb.Append($"{indent}{valueStr}");
                    sb.AppendLine();
                }
                break;

            case NodeType.List:
                SerializeList(node, sb, indentLevel);
                break;

            case NodeType.Block:
            case NodeType.InlineScript:
                SerializeBlock(node, sb, indentLevel);
                break;

            case NodeType.Error:
                break;
        }
    }

    private static string GetSeparatorString(TokenType? separatorType)
    {
        if (!separatorType.HasValue)
            return "="; // 默认兼容（旧数据或未记录）

        return separatorType.Value switch
        {
            TokenType.Equals => "=",
            TokenType.Greater => ">",
            TokenType.Less => "<",
            TokenType.GreaterEqual => ">=",
            TokenType.LessEqual => "<=",
            _ => "=" // fallback
        };
    }

    private static void SerializeList(AstNode node, StringBuilder sb, int indentLevel)
    {
        if (string.IsNullOrEmpty(node.Key))
            return;

        bool singleLine = ShouldBeSingleLine(node, indentLevel);
        string indent = new string('\t', indentLevel);
        string innerIndent = new string('\t', indentLevel + 1);

        sb.Append($"{indent}{node.Key} = {{");

        if (singleLine)
        {
            var elements = node.Children
                .Where(c => c.Type == NodeType.Simple && c.Key == null)
                .Select(FormatSimpleValue);
            sb.Append(' ');
            sb.Append(string.Join(' ', elements));
            sb.Append(" }");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine();
            foreach (var child in node.Children)
            {
                if (child.Type == NodeType.Simple && string.IsNullOrEmpty(child.Key))
                {
                    sb.Append($"{innerIndent}{FormatSimpleValue(child)}");
                    sb.AppendLine();
                }
            }
            sb.Append($"{indent}}}");
            sb.AppendLine();
        }
    }

    private static void SerializeBlock(AstNode node, StringBuilder sb, int indentLevel)
    {
        if (string.IsNullOrEmpty(node.Key))
            return;

        bool singleLine = ShouldBeSingleLine(node, indentLevel);
        string indent = new string('\t', indentLevel);

        if (singleLine && node.Children.Count > 0)
        {
            // 先构建单行内容，全部子节点可内联时才输出前缀，避免回退多行时前缀重复
            var parts = new List<string>();
            bool canInline = true;
            foreach (var child in node.Children.Where(c => c.Type != NodeType.Comment && c.Type != NodeType.Error))
            {
                if (child.Type == NodeType.Simple && !string.IsNullOrEmpty(child.Key))
                {
                    parts.Add($"{child.Key} = {FormatSimpleValue(child)}");
                }
                else if (child.Type == NodeType.Simple && string.IsNullOrEmpty(child.Key))
                {
                    // 裸值（列表项，如 extra_crisis_strength = { 10 25 50 }）
                    parts.Add(FormatSimpleValue(child));
                }
                else if (child.Type == NodeType.List)
                {
                    // 子列表递归序列化单行形式
                    var subContent = SerializeNodeSingleLine(child);
                    parts.Add($"{child.Key} = {{ {subContent} }}");
                }
                else if (child.Type == NodeType.Block || child.Type == NodeType.InlineScript)
                {
                    // 块不能内联，强制多行
                    canInline = false;
                    break;
                }
            }
            if (canInline)
            {
                sb.Append($"{indent}{node.Key} = {{ ");
                sb.Append(string.Join(" ", parts));
                sb.Append(" }");
                sb.AppendLine();
                return;
            }
        }

        // 多行输出
        sb.Append($"{indent}{node.Key} = {{");
        sb.AppendLine();

        foreach (var child in node.Children.Where(c => c.Type != NodeType.Comment && c.Type != NodeType.Error))
        {
            WriteAssociatedComments(child, sb, indentLevel + 1);
            SerializeNode(child, sb, indentLevel + 1);
        }

        sb.Append($"{indent}}}");
        sb.AppendLine();
    }

    // 辅助：生成列表的单行内容（无缩进、无键）
    private static string SerializeNodeSingleLine(AstNode node)
    {
        if (node.Type == NodeType.List)
        {
            var elements = node.Children
                .Where(c => c.Type == NodeType.Simple && c.Key == null)
                .Select(FormatSimpleValue);
            return string.Join(" ", elements);
        }
        return "";
    }

    private static void WriteAssociatedComments(AstNode node, StringBuilder sb, int indentLevel)
    {
        if (node == null || node.AssociatedComments.Count == 0)
            return;

        string indent = new string('\t', indentLevel);
        foreach (var comment in node.AssociatedComments)
        {
            if (comment.Type == NodeType.Comment && comment.Value != null)
            {
                sb.Append($"{indent}#{comment.Value}");
                sb.AppendLine();
            }
        }
    }

    private static bool ShouldBeSingleLine(AstNode node, int indentLevel)
    {
        var children = node.Children.Where(c => c.Type != NodeType.Comment && c.Type != NodeType.Error).ToList();
        if (children.Count == 0)
            return true;

        int totalWidth = CalculateContentWidth(node);
        int itemCount = children.Count;

        // 规范：块/列表子项 > 3 一律多行；≤3 项时总字符 >= 64 也需多行，否则单行
        if (itemCount > 3)
            return false;
        return totalWidth < 64;
    }

    /// <summary>
    /// 递归计算节点序列化后的单行内容宽度（不含缩进）
    /// </summary>
    private static int CalculateContentWidth(AstNode node)
    {
        if (node == null) return 0;

        int width = 0;
        if (node.Type == NodeType.List)
        {
            // "key = { v1 v2 ... }"
            if (!string.IsNullOrEmpty(node.Key))
                width += GetStringWidth(node.Key) + 3; // " = {"
            else
                width += 2; // "{"

            bool first = true;
            foreach (var child in node.Children)
            {
                if (child.Type == NodeType.Simple && string.IsNullOrEmpty(child.Key))
                {
                    if (!first) width += 1; // 空格
                    width += GetStringWidth(FormatSimpleValue(child));
                    first = false;
                }
                else if (child.Type == NodeType.List || child.Type == NodeType.Block)
                {
                    // 嵌套结构递归计算
                    if (!first) width += 1;
                    width += CalculateContentWidth(child);
                    first = false;
                }
                // 其他类型忽略（如注释）
            }
            width += 2; // " }"
        }
        else if (node.Type == NodeType.Block || node.Type == NodeType.InlineScript)
        {
            // "key = { child1 = val1 child2 = val2 ... }"
            if (!string.IsNullOrEmpty(node.Key))
                width += GetStringWidth(node.Key) + 3; // " = {"
            else
                width += 2;

            bool first = true;
            foreach (var child in node.Children)
            {
                if (child.Type == NodeType.Simple && !string.IsNullOrEmpty(child.Key))
                {
                    if (!first) width += 1;
                    width += GetStringWidth(child.Key) + 3; // " = "
                    width += GetStringWidth(FormatSimpleValue(child));
                    first = false;
                }
                else if (child.Type == NodeType.List || child.Type == NodeType.Block)
                {
                    if (!first) width += 1;
                    width += CalculateContentWidth(child);
                    first = false;
                }
            }
            width += 2; // " }"
        }
        return width;
    }

    private static int GetStringWidth(string str)
    {
        if (string.IsNullOrEmpty(str)) return 0;
        int width = 0;
        foreach (char c in str)
        {
            if (c == '\t') width += 4;
            else if (c >= 0x4E00 && c <= 0x9FFF) width += 2; // CJK 汉字
            else width += 1;
        }
        return width;
    }

    private static string FormatValue(object? value, bool isQuoted)
    {
        if (value == null) return "null";
        string str = value.ToString() ?? string.Empty;

        if (isQuoted) return $"\"{str}\"";

        if (value is int || value is long || value is float || value is double)
            return str;

        if (str.Any(ch => char.IsWhiteSpace(ch) || ch == '#' || ch == '=' || ch == '{' || ch == '}'))
            return $"\"{str}\"";

        return str;
    }

    /// <summary>
    /// 按规范 7.1 的值输出优先级规则格式化 Simple 节点：
    /// RawText 非 null 时直接输出 RawText，否则回退到 FormatValue(Value, IsQuoted)。
    /// </summary>
    private static string FormatSimpleValue(AstNode node)
    {
        if (node != null && node.RawText != null)
            return node.RawText;
        return FormatValue(node?.Value, node?.IsQuoted ?? false);
    }

    public static void WriteFile(string filePath, string content)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        // 编码规范：仅本地化 .yml 用带 BOM 的 UTF8；其他文本文件（.txt/.gfx/.json 等）用标准 UTF8（无 BOM）
        var encoding = filePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8
            : new UTF8Encoding(false);

        string tempPath = filePath + ".temp";
        File.WriteAllText(tempPath, content, encoding);
        if (File.Exists(filePath))
            File.Delete(filePath);
        File.Move(tempPath, filePath);
    }

    public static void SerializeToFile(string filePath, List<AstNode> nodes)
    {
        string content = Serialize(nodes);
        WriteFile(filePath, content);
    }
}