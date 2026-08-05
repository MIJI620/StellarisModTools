using System.Collections.Generic;

namespace Stellaris.Parser;

/// <summary>
/// 解析结果
/// </summary>
public class ParserResult
{
    /// <summary>
    /// 顶层 AST 节点列表
    /// </summary>
    public List<AstNode> RootNodes { get; set; } = new();

    /// <summary>
    /// 错误列表（错误行会记录，序列化时丢弃）
    /// </summary>
    public List<ErrorEntry> Errors { get; set; } = new();

    /// <summary>
    /// 是否解析成功（无任何错误行时设为 true）
    /// </summary>
    public bool Success => Errors.Count == 0;

    /// <summary>
    /// 原始文件路径（用于调试）
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// 原始文件内容行数组（用于错误定位）
    /// </summary>
    public string[]? Lines { get; set; }
}