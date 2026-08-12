using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellaris.Parser;

/// <summary>
/// 手写语法分析器，严格遵循 ParserSpecification.cs 规范。
/// </summary>
public class Parser
{
    private readonly List<Token> _tokens;
    private readonly string[] _lines;
    private readonly string? _filePath;
    private readonly string? _sourceText;
    private int _pos;

    private readonly List<AstNode> _rootNodes = new();
    private readonly List<ErrorEntry> _errors = new();
    private readonly Stack<List<AstNode>> _blockStack = new();

    private AstNode? _lastStatementNode;
    private readonly List<AstNode> _pendingComments = new();

    public Parser(List<Token> tokens, string[] lines, string? filePath = null, string? sourceText = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _lines = lines ?? Array.Empty<string>();
        _filePath = filePath;
        _sourceText = sourceText;
        _pos = 0;
        _blockStack.Push(_rootNodes);
    }

    public ParserResult Parse()
    {
        while (_pos < _tokens.Count)
        {
            Token tok = _tokens[_pos];
            if (tok.Type == TokenType.Eof)
                break;

            if (tok.Type == TokenType.Comment)
            {
                if (_lastStatementNode != null && _lastStatementNode.EndLine == tok.Line)
                {
                    var inlineComment = CreateCommentNode(tok);
                    _lastStatementNode.AssociatedComments.Add(inlineComment);
                    _pos++;
                    continue;
                }
                else
                {
                    var pendingComment = CreateCommentNode(tok);
                    _pendingComments.Add(pendingComment);
                    _pos++;
                    continue;
                }
            }

            if (tok.Type == TokenType.Error)
            {
                AddError(tok.Line, tok.Column, tok.Value?.ToString() ?? "Lexer error");
                _pos++;
                continue;
            }

            // 键只能是未加引号字符串：Ident、Number、Constant（不能是 String）
            if (tok.Type == TokenType.Ident || tok.Type == TokenType.Number || tok.Type == TokenType.Constant)
            {
                ParseKeyStatement(tok);
                continue;
            }

            if (tok.Type == TokenType.Rbrace)
            {
                AddError(tok.Line, tok.Column, $"Unexpected closing brace '}}' at top level");
                _pos++;
                continue;
            }

            AddError(tok.Line, tok.Column, $"Unexpected token: {tok.Type}");
            _pos++;
        }

        if (_pendingComments.Count > 0 && _lastStatementNode != null)
        {
            foreach (var c in _pendingComments)
                _lastStatementNode.AssociatedComments.Add(c);
            _pendingComments.Clear();
        }

        if (_blockStack.Count > 1)
            AddError(1, 1, "Unclosed block(s) detected");

        return new ParserResult
        {
            RootNodes = _rootNodes,
            Errors = _errors,
            FilePath = _filePath,
            Lines = _lines
        };
    }

    private AstNode CreateCommentNode(Token tok)
    {
        string content = tok.Value?.ToString() ?? string.Empty;
        return new AstNode
        {
            Type = NodeType.Comment,
            Value = content,
            StartLine = tok.Line,
            EndLine = tok.Line,
            StartColumn = tok.Column,
            EndColumn = tok.Column + content.Length + 1
        };
    }

    private void AttachPendingComments(AstNode node)
    {
        if (_pendingComments.Count > 0)
        {
            foreach (var c in _pendingComments)
                node.AssociatedComments.Add(c);
            _pendingComments.Clear();
        }
    }

    private bool IsSeparator(TokenType type)
    {
        return type == TokenType.Equals ||
               type == TokenType.Greater ||
               type == TokenType.Less ||
               type == TokenType.GreaterEqual ||
               type == TokenType.LessEqual;
    }

    /// <summary>
    /// 从输入源中提取 Token 对应的原始字符序列（规范 2.2 / 3.1）。
    /// 无原始文本或位置信息无效时返回 null。
    /// </summary>
    private string? GetRawText(Token tok)
    {
        if (_sourceText == null)
            return null;
        if (tok.StartIndex < 0 || tok.EndIndex < tok.StartIndex || tok.EndIndex > _sourceText.Length)
            return null;
        return _sourceText.Substring(tok.StartIndex, tok.EndIndex - tok.StartIndex);
    }

    private static string GetKeyText(Token keyToken)
    {
        // 常量声明/引用键：@NAME 或 @[expr]（规范 3.2），不能使用 ConstantValue.ToString()
        if (keyToken.Value is ConstantValue cv)
        {
            if (cv.Type == ConstantType.Simple && !string.IsNullOrEmpty(cv.Name))
                return "@" + cv.Name;
            if (cv.Type == ConstantType.Expression && cv.Text != null)
                return "@[" + cv.Text + "]";
        }
        return keyToken.Value?.ToString() ?? string.Empty;
    }

    // ==================== Parser.ParseKeyStatement ====================
    private void ParseKeyStatement(Token keyToken)
    {
        string key = GetKeyText(keyToken);
        int startLine = keyToken.Line, startCol = keyToken.Column;

        if (_pos + 1 >= _tokens.Count)
        {
            // 顶层最后一个 token 无后续 → 裸值行（清单文件末行）
            if (_blockStack.Count == 1)
            {
                AddBareValueNode(keyToken, startLine, startCol);
                return;
            }
            AddError(startLine, startCol, $"Incomplete statement after key '{key}'");
            _pos++;
            return;
        }

        Token next = _tokens[_pos + 1];

        if (IsSeparator(next.Type))
        {
            if (next.Line != startLine)
            {
                AddError(next.Line, next.Column, $"分隔符 '{next.Type}' 必须与 key '{key}' 在同一行");
                _pos += 2;
                return;
            }

            // ===== 新增：常量声明只允许 '=' =====
            if (key.StartsWith('@') && next.Type != TokenType.Equals)
            {
                AddError(next.Line, next.Column, $"常量声明键 '{key}' 后只允许 '=' 分隔符，不允许 '{next.Type}'");
                _pos += 2;
                return;
            }

            _pos += 2;
            ParseValueAssignment(key, startLine, startCol, next.Type);
            return;
        }

        // ===== 裸值行支持：key token 后无分隔符（且非块引导符）→ 顶层裸值 Simple（Key=null）。
        // 清单文件场景（inline script 内容如 shelter_all_building_set：每行一个值）。
        // 仅顶层上下文允许（块内裸值仍报错——块内 List 由 ParseBlockOrList 处理）。
        if (_blockStack.Count == 1 && next.Type != TokenType.Lbrace)
        {
            AddBareValueNode(keyToken, startLine, startCol);
            return;
        }

        AddError(startLine, startCol, $"期望分隔符 ( = > < >= <= )，实际得到 '{next.Type}'，键 '{key}' 后必须跟分隔符");
        _pos++;
    }

    /// <summary>裸值行：顶层无分隔符的词 → Key=null 的 Simple 节点（清单文件每行一个值）。</summary>
    private void AddBareValueNode(Token keyToken, int startLine, int startCol)
    {
        string bareValue = keyToken.Value?.ToString() ?? string.Empty;
        var bareNode = new AstNode
        {
            Type = NodeType.Simple,
            Key = null,
            Value = bareValue,
            StartLine = startLine,
            StartColumn = startCol,
            EndLine = startLine,
            EndColumn = startCol + bareValue.Length
        };
        AttachPendingComments(bareNode);
        _rootNodes.Add(bareNode);
        _lastStatementNode = bareNode;
        _pos++;
    }

    private void ParseValueAssignment(string key, int startLine, int startCol, TokenType separatorType)
    {
        if (_pos >= _tokens.Count)
        {
            AddError(startLine, startCol, $"缺少值，键 '{key}' 后分隔符 '{separatorType}' 无对应值");
            return;
        }

        Token valueToken = _tokens[_pos];
        int endLine = valueToken.Line;
        int endCol = valueToken.Column + (valueToken.Value?.ToString()?.Length ?? 0);

        if (valueToken.Line != startLine)
        {
            AddError(valueToken.Line, valueToken.Column, $"键 '{key}' 的值必须与分隔符在同一行");
            _pos++;
            return;
        }

        // 只有 '=' 允许块/列表
        if (valueToken.Type == TokenType.Lbrace)
        {
            if (separatorType != TokenType.Equals)
            {
                AddError(valueToken.Line, valueToken.Column, $"分隔符 '{separatorType}' 后不允许跟块或列表，只有 '=' 可以引导块");
                int depth = 1;
                _pos++;
                while (_pos < _tokens.Count && depth > 0)
                {
                    Token t = _tokens[_pos];
                    if (t.Type == TokenType.Lbrace) depth++;
                    else if (t.Type == TokenType.Rbrace) depth--;
                    _pos++;
                }
                return;
            }

            _pos++;
            var pendingForBlock = new List<AstNode>(_pendingComments);
            _pendingComments.Clear();
            var blockNode = ParseBlockOrList(key, startLine, startCol, pendingForBlock);
            if (blockNode != null)
            {
                AddNode(blockNode);
                _lastStatementNode = blockNode;
            }
            return;
        }

        // 处理简单值：对 '=' 允许所有，对非 '=' 禁止双引号字符串
        bool isValidSimple = false;
        if (separatorType == TokenType.Equals)
        {
            isValidSimple = (valueToken.Type == TokenType.String ||
                             valueToken.Type == TokenType.Number ||
                             valueToken.Type == TokenType.Constant ||
                             valueToken.Type == TokenType.Ident);
        }
        else // > < >= <=
        {
            isValidSimple = (valueToken.Type == TokenType.Number ||
                             valueToken.Type == TokenType.Constant ||
                             valueToken.Type == TokenType.Ident);
            if (valueToken.Type == TokenType.String)
            {
                AddError(valueToken.Line, valueToken.Column, $"分隔符 '{separatorType}' 后不允许双引号字符串，只允许数字、常量或未加引号字符串");
                _pos++;
                return;
            }
        }

        if (!isValidSimple)
        {
            AddError(startLine, startCol, $"无效的值类型 '{valueToken.Type}'，键 '{key}' 后必须为有效简单值");
            _pos++;
            return;
        }

        var node = new AstNode
        {
            Type = NodeType.Simple,
            Key = key,
            Value = valueToken.Value,
            IsQuoted = (valueToken.Type == TokenType.String),
            StartLine = startLine,
            EndLine = endLine,
            StartColumn = startCol,
            EndColumn = endCol,
            IndentWidth = 0,
            OriginalLayout = OriginalLayout.SingleLine,
            SeparatorType = separatorType,
            RawText = GetRawText(valueToken)
        };
        AddNode(node);
        AttachPendingComments(node);
        _lastStatementNode = node;
        _pos++;
    }

    // ==================== Parser.ParseBlockOrList ====================
    private AstNode ParseBlockOrList(string key, int startLine, int startCol, List<AstNode>? pendingComments = null)
    {
        // 消耗开头的 Lbrace（调用时 _pos 应指向 Lbrace）
        if (_pos < _tokens.Count && _tokens[_pos].Type == TokenType.Lbrace)
            _pos++;

        var children = new List<AstNode>();
        int endLine = startLine, endCol = startCol;

        bool isMultiLine = false;
        bool hasContent = false;

        var commentsToAttach = pendingComments ?? new List<AstNode>();

        while (_pos < _tokens.Count)
        {
            Token tok = _tokens[_pos];
            if (tok.Type == TokenType.Rbrace)
            {
                endLine = tok.Line;
                endCol = tok.Column;
                _pos++;
                break;
            }

            if (tok.Type == TokenType.Eof)
            {
                AddError(startLine, startCol, $"Unclosed block/list starting at line {startLine}");
                break;
            }

            if (tok.Type == TokenType.Comment)
            {
                if (children.Count > 0)
                {
                    var lastChild = children[^1];
                    if (lastChild.EndLine == tok.Line)
                    {
                        var inlineComment = CreateCommentNode(tok);
                        lastChild.AssociatedComments.Add(inlineComment);
                        _pos++;
                        continue;
                    }
                }
                var pendingComment = CreateCommentNode(tok);
                _pendingComments.Add(pendingComment);
                _pos++;
                continue;
            }

            if (tok.Type == TokenType.Error)
            {
                AddError(tok.Line, tok.Column, tok.Value?.ToString() ?? "Unknown error inside block");
                _pos++;
                continue;
            }

            if (!hasContent)
                hasContent = true;
            if (tok.Line > startLine)
                isMultiLine = true;

            // 处理键值对：键允许 Ident、Number、Constant；分隔符 = < > <= >=（块内同顶层——比较运算符保留）
            if ((tok.Type == TokenType.Ident || tok.Type == TokenType.Number || tok.Type == TokenType.Constant) &&
                _pos + 1 < _tokens.Count && IsSeparator(_tokens[_pos + 1].Type))
            {
                Token eqTok = _tokens[_pos + 1];
                TokenType innerSep = eqTok.Type;
                if (eqTok.Line != tok.Line)
                {
                    AddError(eqTok.Line, eqTok.Column, $"'=' must be on same line as key '{tok.Value}'");
                    _pos += 2;
                    continue;
                }

                string innerKey = GetKeyText(tok);
                int innerStartLine = tok.Line, innerStartCol = tok.Column;
                _pos += 2;

                if (_pos >= _tokens.Count)
                {
                    AddError(innerStartLine, innerStartCol, $"Missing value for '{innerKey}'");
                    continue;
                }

                Token valTok = _tokens[_pos];

                // 比较运算符（> < >= <=）后不允许双引号字符串（同顶层规则）
                if (innerSep != TokenType.Equals && valTok.Type == TokenType.String)
                {
                    AddError(valTok.Line, valTok.Column, $"分隔符 '{innerSep}' 后不允许双引号字符串，只允许数字、常量或未加引号字符串");
                    _pos++;
                    continue;
                }

                // 检查值是否为 Lbrace（嵌套块/列表）
                if (valTok.Type == TokenType.Lbrace)
                {
                    if (innerSep != TokenType.Equals)
                    {
                        AddError(valTok.Line, valTok.Column, $"分隔符 '{innerSep}' 后不允许跟块或列表，只有 '=' 可以引导块");
                        int depth = 1;
                        _pos++;
                        while (_pos < _tokens.Count && depth > 0)
                        {
                            Token t = _tokens[_pos];
                            if (t.Type == TokenType.Lbrace) depth++;
                            else if (t.Type == TokenType.Rbrace) depth--;
                            _pos++;
                        }
                        continue;
                    }
                    var innerPending = new List<AstNode>(_pendingComments);
                    _pendingComments.Clear();
                    var nestedChild = ParseBlockOrList(innerKey, innerStartLine, innerStartCol, innerPending);
                    children.Add(nestedChild);
                    continue;
                }

                // 普通简单值
                if (valTok.Line != eqTok.Line)
                {
                    AddError(valTok.Line, valTok.Column, $"Value for key '{innerKey}' must be on same line as '='");
                    _pos++;
                    continue;
                }
                int valEndLine = valTok.Line;
                int valEndCol = valTok.Column + (valTok.Value?.ToString()?.Length ?? 0);
                var childNode = new AstNode
                {
                    Type = NodeType.Simple,
                    Key = innerKey,
                    Value = valTok.Value,
                    IsQuoted = (valTok.Type == TokenType.String),
                    StartLine = innerStartLine,
                    EndLine = valEndLine,
                    StartColumn = innerStartCol,
                    EndColumn = valEndCol,
                    OriginalLayout = OriginalLayout.SingleLine,
                    SeparatorType = innerSep,
                    RawText = GetRawText(valTok)
                };
                children.Add(childNode);
                AttachPendingComments(childNode);
                _pos++;
                continue;
            }

            // 处理简单值（列表元素）
            if (tok.Type == TokenType.Ident || tok.Type == TokenType.String ||
                tok.Type == TokenType.Number || tok.Type == TokenType.Constant)
            {
                var childNode = new AstNode
                {
                    Type = NodeType.Simple,
                    Key = null,
                    Value = tok.Value,
                    IsQuoted = (tok.Type == TokenType.String),
                    StartLine = tok.Line,
                    EndLine = tok.Line,
                    StartColumn = tok.Column,
                    EndColumn = tok.Column + (tok.Value?.ToString()?.Length ?? 0),
                    OriginalLayout = OriginalLayout.SingleLine,
                    RawText = GetRawText(tok)
                };
                children.Add(childNode);
                AttachPendingComments(childNode);
                _pos++;
                continue;
            }

            if (tok.Type == TokenType.Lbrace)
            {
                AddError(tok.Line, tok.Column, "Nested block without key inside parent block");
                int depth = 1;
                _pos++;
                while (_pos < _tokens.Count && depth > 0)
                {
                    Token t = _tokens[_pos];
                    if (t.Type == TokenType.Lbrace) depth++;
                    else if (t.Type == TokenType.Rbrace) depth--;
                    _pos++;
                }
                continue;
            }

            AddError(tok.Line, tok.Column, $"Unexpected token '{tok.Type}' inside block");
            _pos++;
        }

        // 判定块/列表
        bool allSimpleNoKey = children.All(c => c.Type == NodeType.Simple && string.IsNullOrEmpty(c.Key));
        bool hasKeyInChildren = children.Any(c => !string.IsNullOrEmpty(c.Key));
        bool hasNoKeyInChildren = children.Any(c => string.IsNullOrEmpty(c.Key) && c.Type == NodeType.Simple);

        if (hasKeyInChildren && hasNoKeyInChildren)
            AddError(startLine, startCol, "Mixed simple values and key-value pairs inside block/list");

        NodeType finalType;
        if (allSimpleNoKey && !hasKeyInChildren)
            finalType = NodeType.List;
        else
            finalType = NodeType.Block;

        // 注意：解析器为通用解析器，不得按具体键名识别特殊类型
        // （如 "inline_script"）。特殊键名的语义识别由引擎层
        // （ScriptExpander）负责。

        var parentNode = new AstNode
        {
            Type = finalType,
            Key = key,
            Children = children,
            StartLine = startLine,
            EndLine = endLine,
            StartColumn = startCol,
            EndColumn = endCol,
            OriginalLayout = isMultiLine ? OriginalLayout.MultiLine : OriginalLayout.SingleLine
        };

        foreach (var comment in commentsToAttach)
            parentNode.AssociatedComments.Add(comment);

        if (children.Count == 0 && _pendingComments.Count > 0)
        {
            foreach (var comment in _pendingComments)
                parentNode.AssociatedComments.Add(comment);
            _pendingComments.Clear();
        }

        return parentNode;
    }

    private void AddNode(AstNode node)
    {
        _blockStack.Peek().Add(node);
    }

    private void AddError(int line, int column, string reason)
    {
        if (line < 1) line = 1;
        if (column < 1) column = 1;
        string content = (line >= 1 && line <= _lines.Length) ? _lines[line - 1] : "";
        _errors.Add(new ErrorEntry(line, column, content, reason));
    }
}