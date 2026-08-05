namespace Stellaris.Parser;

/// <summary>
/// 词法单元类型
/// </summary>
public enum TokenType
{
    Ident,      // 未加引号字符串（标识符、键名、值）
    String,     // 双引号字符串
    Number,     // 数字（int 或 double）
    Constant,   // @var 或 @[expr]
    Lbrace,     // {
    Rbrace,     // }
    Equals,     // =
    Greater,    // >
    Less,       // <
    GreaterEqual,// >=
    LessEqual,  // <=
    Eof,        // 文件结束
    Error,      // 词法错误
    Comment     // 注释
}

/// <summary>
/// 词法单元
/// </summary>
public class Token
{
    public TokenType Type { get; }
    public object? Value { get; }
    public int Line { get; }
    public int Column { get; }

    /// <summary>该 Token 在输入文本中的起始绝对位置（含），无位置信息时为 -1</summary>
    public int StartIndex { get; }

    /// <summary>该 Token 在输入文本中的结束绝对位置（不含），无位置信息时为 -1</summary>
    public int EndIndex { get; }

    public Token(TokenType type, object? value, int line, int column, int startIndex = -1, int endIndex = -1)
    {
        Type = type;
        Value = value;
        Line = line;
        Column = column;
        StartIndex = startIndex;
        EndIndex = endIndex;
    }

    public override string ToString()
        => $"{Type} (Line {Line}, Col {Column}) = {Value}";
}