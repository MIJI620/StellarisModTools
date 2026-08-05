using System;
using System.Collections.Generic;
using System.Text;

namespace Stellaris.Parser;

/// <summary>
/// 手写词法分析器，严格遵循 ParserSpecification.md 规范。
/// 增强错误恢复：遇到非法字符时一次性跳过连续非法字符序列。
/// </summary>
public class Lexer
{
    private readonly string _text;
    private readonly int _length;
    private int _pos;
    private int _line;
    private int _column;

    public Lexer(string text)
    {
        _text = text ?? string.Empty;
        _length = _text.Length;
        _pos = 0;
        _line = 1;
        _column = 1;
    }

    public Token NextToken()
    {
        SkipWhitespace();

        if (_pos >= _length)
            return new Token(TokenType.Eof, null, _line, _column, _pos, _pos);

        char ch = _text[_pos];

        if (ch == '#')
            return ScanComment();
        if (ch == '"')
            return ScanString();
        if (ch == '@')
            return ScanVariable();

        // ===== 新增：识别 > 和 < 分隔符 =====
        if (ch == '>')
        {
            int startPos = _pos;
            _pos++; _column++;
            if (_pos < _length && _text[_pos] == '=')
            {
                _pos++; _column++;
                return new Token(TokenType.GreaterEqual, null, _line, _column - 2, startPos, _pos);
            }
            return new Token(TokenType.Greater, null, _line, _column - 1, startPos, _pos);
        }
        if (ch == '<')
        {
            int startPos = _pos;
            _pos++; _column++;
            if (_pos < _length && _text[_pos] == '=')
            {
                _pos++; _column++;
                return new Token(TokenType.LessEqual, null, _line, _column - 2, startPos, _pos);
            }
            return new Token(TokenType.Less, null, _line, _column - 1, startPos, _pos);
        }

        if (char.IsDigit(ch) || (ch == '.' && _pos + 1 < _length && char.IsDigit(_text[_pos + 1])))
            return ScanNumberOrIdent(ch);

        if (ch == '{') { int s = _pos; _pos++; _column++; return new Token(TokenType.Lbrace, null, _line, _column - 1, s, _pos); }
        if (ch == '}') { int s = _pos; _pos++; _column++; return new Token(TokenType.Rbrace, null, _line, _column - 1, s, _pos); }
        if (ch == '=') { int s = _pos; _pos++; _column++; return new Token(TokenType.Equals, null, _line, _column - 1, s, _pos); }

        if (IsIdentChar(ch))
            return ScanIdent();

        // 错误恢复：跳过连续非法字符
        return ScanIllegalSequence();
    }

    private void SkipWhitespace()
    {
        while (_pos < _length)
        {
            char ch = _text[_pos];
            if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r')
            {
                if (ch == '\n') { _line++; _column = 1; }
                else if (ch == '\r') { _column++; }
                else { _column++; }
                _pos++;
            }
            else break;
        }
    }

    private Token ScanComment()
    {
        int startLine = _line, startCol = _column;
        int startPos = _pos;
        _pos++; _column++;
        var content = new StringBuilder();
        while (_pos < _length && _text[_pos] != '\n' && _text[_pos] != '\r')
        {
            content.Append(_text[_pos]);
            _pos++; _column++;
        }
        return new Token(TokenType.Comment, content.ToString(), startLine, startCol, startPos, _pos);
    }

    private Token ScanString()
    {
        int startLine = _line, startCol = _column;
        int startPos = _pos;

        // 相邻双引号配对（用户实测原版规则）：从第一个引号读到**下一个**引号即终止。
        // 引号内除双引号和换行外的一切字符均合法（#、	、空格、= 等）。
        // 例：from = "07" to = "03" → "07" 是完整字符串，to = "03" 是下一个赋值。
        int endQuotePos = -1;
        for (int searchPos = startPos + 1; searchPos < _length; searchPos++)
        {
            char c = _text[searchPos];
            if (c == '\u000A' || c == '\u000A')
                break;               // 换行结束（未闭合）
            if (c == '"')
            {
                endQuotePos = searchPos;
                break;               // 相邻配对：下一个引号终止
            }
        }

        if (endQuotePos == -1)
        {
            // 未闭合字符串：跳过该行剩余字符，返回 Error Token
            while (_pos < _length && _text[_pos] != '\u000A' && _text[_pos] != '\u000A')
            {
                _pos++; _column++;
            }
            return new Token(TokenType.Error, $"Unclosed string starting at line {startLine}", startLine, startCol, startPos, _pos);
        }

        // 内容 = 第一个引号之后 到 下一个引号之前
        string value = _text.Substring(startPos + 1, endQuotePos - startPos - 1);

        // 推进位置到结束引号之后
        _pos = startPos;
        _line = startLine;
        _column = startCol;
        while (_pos <= endQuotePos)
        {
            _pos++; _column++;
        }
        return new Token(TokenType.String, value, startLine, startCol, startPos, _pos);
    }
    private Token ScanVariable()
    {
        int startLine = _line, startCol = _column;
        int startPos = _pos;
        _pos++; _column++;

        if (_pos >= _length)
            return new Token(TokenType.Error, "@ without following identifier", startLine, startCol, startPos, _pos);

        char ch = _text[_pos];
        if (ch == '[')
        {
            _pos++; _column++;
            int depth = 1;
            var expr = new StringBuilder();
            while (_pos < _length && depth > 0)
            {
                char c = _text[_pos];
                if (c == '[') { depth++; expr.Append(c); _pos++; _column++; }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) { _pos++; _column++; break; }
                    expr.Append(c); _pos++; _column++;
                }
                else if (c == '\n' || c == '\r')
                    return new Token(TokenType.Error, "Newline not allowed inside @[expr]", startLine, startCol, startPos, _pos);
                else
                {
                    if (c == '{' || c == '}' || c == '#' || c == '=' || c == '"' || c == '\'')
                        return new Token(TokenType.Error, $"Illegal character '{c}' inside @[expr]", startLine, startCol, startPos, _pos);
                    expr.Append(c); _pos++; _column++;
                }
            }
            if (depth != 0)
                return new Token(TokenType.Error, "Unclosed @[ expression", startLine, startCol, startPos, _pos);
            if (expr.ToString().Contains("@["))
                return new Token(TokenType.Error, "Nested @[ not allowed inside expression", startLine, startCol, startPos, _pos);
            return new Token(TokenType.Constant,
                new ConstantValue { Type = ConstantType.Expression, Text = expr.ToString() },
                startLine, startCol, startPos, _pos);
        }

        if (IsIdentChar(ch) && !IsIllegalChar(ch))
        {
            var name = new StringBuilder();
            while (_pos < _length && IsIdentChar(_text[_pos]) && !IsIllegalChar(_text[_pos]))
            {
                name.Append(_text[_pos]);
                _pos++; _column++;
            }
            if (name.Length == 0)
                return new Token(TokenType.Error, "Missing identifier after @", startLine, startCol, startPos, _pos);
            return new Token(TokenType.Constant,
                new ConstantValue { Type = ConstantType.Simple, Name = name.ToString() },
                startLine, startCol, startPos, _pos);
        }
        return new Token(TokenType.Error, $"Invalid character after @: '{ch}'", startLine, startCol, startPos, _pos);
    }

    private Token ScanNumberOrIdent(char firstChar)
    {
        int savedPos = _pos, savedLine = _line, savedColumn = _column;

        var numStr = new StringBuilder();
        bool hasDot = false, hasDigit = false;

        if (char.IsDigit(firstChar))
        {
            numStr.Append(firstChar);
            hasDigit = true;
            _pos++; _column++;
        }
        else if (firstChar == '.')
        {
            // 单独的点不能作为数字，回退并扫描标识符
            _pos = savedPos; _line = savedLine; _column = savedColumn;
            return ScanIdent();
        }

        while (_pos < _length)
        {
            char ch = _text[_pos];
            if (char.IsDigit(ch))
            {
                numStr.Append(ch);
                hasDigit = true;
                _pos++; _column++;
            }
            else if (ch == '.')
            {
                if (hasDot)
                {
                    // 多个点，回退为标识符
                    _pos = savedPos; _line = savedLine; _column = savedColumn;
                    return ScanIdent();
                }
                if (!hasDigit)
                {
                    _pos = savedPos; _line = savedLine; _column = savedColumn;
                    return ScanIdent();
                }
                hasDot = true;
                numStr.Append(ch);
                _pos++; _column++;
            }
            else
            {
                break;
            }
        }

        string numStrFinal = numStr.ToString();
        if (numStrFinal.EndsWith(".") || !hasDigit)
        {
            _pos = savedPos; _line = savedLine; _column = savedColumn;
            return ScanIdent();
        }

        // 如果后面紧跟标识符字符（字母、数字、下划线等），则视为标识符的一部分（如 "123abc"）
        if (_pos < _length && IsIdentChar(_text[_pos]) && !IsIllegalChar(_text[_pos]))
        {
            _pos = savedPos; _line = savedLine; _column = savedColumn;
            return ScanIdent();
        }

        if (hasDot)
        {
            if (double.TryParse(numStrFinal, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d))
                return new Token(TokenType.Number, d, savedLine, savedColumn, savedPos, _pos);
        }
        else
        {
            if (int.TryParse(numStrFinal, out int i))
                return new Token(TokenType.Number, i, savedLine, savedColumn, savedPos, _pos);
            if (long.TryParse(numStrFinal, out long l))
                return new Token(TokenType.Number, l, savedLine, savedColumn, savedPos, _pos);
        }

        // 如果数字解析失败，回退标识符
        _pos = savedPos; _line = savedLine; _column = savedColumn;
        return ScanIdent();
    }

    private Token ScanIdent()
    {
        int startLine = _line, startCol = _column;
        int startPos = _pos;
        var sb = new StringBuilder();
        while (_pos < _length)
        {
            char ch = _text[_pos];
            if (IsIdentChar(ch) && !IsIllegalChar(ch))
            {
                sb.Append(ch);
                _pos++; _column++;
            }
            else break;
        }
        if (sb.Length == 0)
            return new Token(TokenType.Error, "Empty identifier", startLine, startCol, startPos, _pos);
        return new Token(TokenType.Ident, sb.ToString(), startLine, startCol, startPos, _pos);
    }

    /// <summary>
    /// 跳过连续非法字符序列，直到遇到空白、换行或文件结束。
    /// 返回一个 Error Token 包含非法内容描述。
    /// </summary>
    private Token ScanIllegalSequence()
    {
        int startLine = _line, startCol = _column;
        int startPos = _pos;
        var illegalChars = new StringBuilder();

        while (_pos < _length)
        {
            char ch = _text[_pos];
            // 如果遇到空白、换行或合法字符，停止跳过
            if (char.IsWhiteSpace(ch) || ch == '\n' || ch == '\r')
                break;
            // 检查是否为合法标识符字符、双引号、@、数字等，若合法则停止（这些应该被其他扫描器处理）
            if (IsIdentChar(ch) || ch == '"' || ch == '@' || ch == '#' || ch == '=' || ch == '{' || ch == '}')
                break;
            // 否则为非法字符，记录并跳过
            illegalChars.Append(ch);
            _pos++; _column++;
        }

        string illegal = illegalChars.ToString();
        if (string.IsNullOrEmpty(illegal))
            return new Token(TokenType.Error, $"Illegal character at line {startLine}, column {startCol}", startLine, startCol, startPos, _pos);

        return new Token(TokenType.Error, $"Illegal character sequence: '{illegal}'", startLine, startCol, startPos, _pos);
    }

    private static bool IsIdentChar(char ch)
    {
        return ch != ' ' && ch != '\t' && ch != '\n' && ch != '\r'
            && ch != '=' && ch != '{' && ch != '}'
            && ch != '"' && ch != '#' && ch != '@'
            && ch != '[' && ch != ']'
            && ch != '(' && ch != ')' && ch != '<' && ch != '>';
    }

    private static bool IsIllegalChar(char ch)
    {
        return ch == '\0' || ch == '\r' || ch == '\n' || ch == '\t'
            || ch == '"' || ch == '#' || ch == '='
            || ch == '{' || ch == '}' || ch == '['
            || ch == ']' || ch == '(' || ch == ')'
            || ch == '<' || ch == '>';
    }
}

public class ConstantValue
{
    public ConstantType Type { get; set; }
    public string? Name { get; set; }
    public string? Text { get; set; }
}

public enum ConstantType
{
    Simple,
    Expression
}