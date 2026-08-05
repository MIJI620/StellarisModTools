namespace Stellaris.Parser;

/// <summary>
/// 错误行记录
/// </summary>
public class ErrorEntry
{
    public int Line { get; }
    public int Column { get; }
    public string Content { get; }
    public string Reason { get; }

    public ErrorEntry(int line, int column, string content, string reason)
    {
        Line = line;
        Column = column;
        Content = content;
        Reason = reason;
    }

    public override string ToString()
        => $"[Error] Line {Line}, Col {Column}: {Reason} -> '{Content}'";
}