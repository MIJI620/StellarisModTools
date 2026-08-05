using System.Collections.Generic;

namespace Stellaris.Parser;

/// <summary>
/// 原始布局（仅用于序列化时的单行/多行判定）
/// </summary>
public enum OriginalLayout
{
    Unknown,   // 新生成节点，无原始信息
    SingleLine,
    MultiLine
}

/// <summary>
/// AST 节点类型
/// </summary>
public enum NodeType
{
    Simple,
    Block,
    List,
    Comment,
    Error,
    InlineScript
}

/// <summary>
/// AST 节点
/// </summary>
public class AstNode
{
    public NodeType Type { get; set; }
    public string? Key { get; set; }
    public object? Value { get; set; }
    public List<AstNode> Children { get; set; } = new();
    public bool IsQuoted { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int StartColumn { get; set; }
    public int EndColumn { get; set; }
    public List<AstNode> AssociatedComments { get; set; } = new();
    public int IndentWidth { get; set; }

    /// <summary>
    /// 原始布局（解析时记录，序列化时用于中间情况）
    /// </summary>
    public OriginalLayout OriginalLayout { get; set; } = OriginalLayout.Unknown;
    public TokenType? SeparatorType { get; set; }

    /// <summary>
    /// 原始文本（Raw Text）：节点在源文件中对应的原始字符序列，未经任何求值或格式化。
    /// 仅对 Simple 类型节点有效；Block/List/InlineScript/Comment/Error 节点必须保持 null。
    /// 序列化时优先使用此字段；常量求值严禁修改此字段。
    /// </summary>
    public string? RawText { get; set; }

    public override string ToString()
        => $"{Type} [{Key}] Line {StartLine}-{EndLine}";
}