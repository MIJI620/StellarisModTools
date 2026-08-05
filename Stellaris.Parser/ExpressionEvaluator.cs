using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Stellaris.Parser
{
    /// <summary>
    /// 表达式求值器，处理 @var 和 @[expr]。
    /// 使用独立的 ConstantResolver，保证局部常量不污染其他文件。
    /// </summary>
    public class ExpressionEvaluator
    {
        private readonly ConstantResolver _resolver;
        private readonly ILogger _logger;

        public ExpressionEvaluator(ConstantResolver resolver, ILogger? logger = null)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _logger = logger ?? NullLogger.Instance;
        }

        public void EvaluateNode(AstNode node)
        {
            if (node == null) return;

            if (node.Type == NodeType.Simple)
            {
                object? oldValue = node.Value;
                object? newValue = EvaluateValue(node.Value);
                if (!Equals(oldValue, newValue))
                {
                    node.Value = newValue;
                    // 如果键是 @常量定义，更新局部表（仅当前作用域）
                    if (node.Key != null && node.Key.StartsWith('@') && newValue != null)
                    {
                        string constName = node.Key[1..];
                        _resolver.SetLocal(constName, newValue);
                    }
                }
            }

            if (node.Type == NodeType.Block || node.Type == NodeType.List)
            {
                foreach (var child in node.Children)
                    EvaluateNode(child);
            }
        }

        public object? EvaluateValue(object? value)
        {
            if (value is ConstantValue constVal)
            {
                if (constVal.Type == ConstantType.Simple && !string.IsNullOrEmpty(constVal.Name))
                {
                    var resolved = _resolver.Resolve(constVal.Name);
                    if (resolved == null)
                        _logger.LogWarning("未找到常量: @{Name}", constVal.Name);
                    return resolved ?? value;
                }

                if (constVal.Type == ConstantType.Expression && !string.IsNullOrEmpty(constVal.Text))
                {
                    try
                    {
                        if (ContainsIllegalChars(constVal.Text))
                        {
                            _logger.LogError("表达式包含非法字符: @[{Expr}]", constVal.Text);
                            return value;
                        }
                        return EvaluateExpression(constVal.Text);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "表达式求值失败: @[{Expr}]", constVal.Text);
                        return value;
                    }
                }
            }

            return value;
        }

        private bool ContainsIllegalChars(string expr)
        {
            foreach (char c in expr)
                if (c == '{' || c == '}' || c == '#' || c == '=' || c == '"' || c == '\'')
                    return true;
            return false;
        }

        private object? EvaluateExpression(string expr)
        {
            string processed = ReplaceAtConstantsInExpression(expr);
            processed = ReplaceBareIdentifiers(processed);
            var tokens = TokenizeExpression(processed);
            var pos = 0;
            var result = ParseExpression(tokens, ref pos);

            if (result is double d && Math.Abs(d - Math.Truncate(d)) < 1e-9)
                return (int)d;
            return result;
        }

        private string ReplaceAtConstantsInExpression(string expr)
        {
            string result = expr;
            int i = 0;
            while (i < result.Length)
            {
                if (result[i] == '@')
                {
                    int start = i;
                    i++;
                    if (i < result.Length && result[i] == '[')
                    {
                        int depth = 1;
                        int j = i + 1;
                        while (j < result.Length && depth > 0)
                        {
                            if (result[j] == '[') depth++;
                            else if (result[j] == ']') depth--;
                            j++;
                        }
                        if (depth == 0)
                        {
                            string innerExpr = result.Substring(i + 1, j - i - 2);
                            if (innerExpr.Contains("@["))
                            {
                                _logger.LogWarning("表达式中禁止嵌套 @[ : {Expr}", innerExpr);
                                return result;
                            }
                            try
                            {
                                object? val = EvaluateExpression(innerExpr);
                                string valStr = val?.ToString() ?? "0";
                                result = result.Remove(start, j - start).Insert(start, valStr);
                                i = start + valStr.Length;
                                continue;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "嵌套表达式求值失败: @[{Inner}]", innerExpr);
                                return result;
                            }
                        }
                    }
                    else if (i < result.Length && (char.IsLetter(result[i]) || result[i] == '_'))
                    {
                        while (i < result.Length && (char.IsLetterOrDigit(result[i]) || result[i] == '_'))
                            i++;
                        string constName = result.Substring(start + 1, i - start - 1);
                        var val = _resolver.Resolve(constName);
                        if (val != null)
                        {
                            string valStr = val.ToString() ?? "0";
                            result = result.Remove(start, i - start).Insert(start, valStr);
                            i = start + valStr.Length;
                            continue;
                        }
                        else
                        {
                            _logger.LogWarning("表达式中未找到常量: @{Name}", constName);
                        }
                    }
                }
                i++;
            }
            return result;
        }

        private string ReplaceBareIdentifiers(string expr)
        {
            string result = expr;
            int i = 0;
            while (i < result.Length)
            {
                char ch = result[i];
                if (char.IsLetter(ch) || ch == '_')
                {
                    int start = i;
                    while (i < result.Length && (char.IsLetterOrDigit(result[i]) || result[i] == '_'))
                        i++;
                    string name = result.Substring(start, i - start);
                    object? val = _resolver.Resolve(name);
                    if (val != null)
                    {
                        string valStr = val.ToString() ?? "0";
                        result = result.Remove(start, i - start).Insert(start, valStr);
                        i = start + valStr.Length;
                        continue;
                    }
                    else
                    {
                        _logger.LogWarning("表达式中未找到常量: {Name}，将保持原样", name);
                    }
                }
                else
                {
                    i++;
                }
            }
            return result;
        }

        private List<string> TokenizeExpression(string expr)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < expr.Length)
            {
                char ch = expr[i];
                if (char.IsWhiteSpace(ch)) { i++; continue; }
                if (ch == '+' || ch == '-' || ch == '*' || ch == '/' || ch == '(' || ch == ')')
                {
                    tokens.Add(ch.ToString());
                    i++;
                    continue;
                }
                if (char.IsDigit(ch) || ch == '.')
                {
                    string num = "";
                    while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.'))
                    {
                        num += expr[i];
                        i++;
                    }
                    tokens.Add(num);
                    continue;
                }
                _logger.LogWarning("表达式包含无法识别的字符: '{Char}' at position {Pos}", ch, i);
                i++;
            }
            return tokens;
        }

        private double ParseExpression(List<string> tokens, ref int pos)
        {
            double left = ParseTerm(tokens, ref pos);
            while (pos < tokens.Count)
            {
                string op = tokens[pos];
                if (op == "+") { pos++; left += ParseTerm(tokens, ref pos); }
                else if (op == "-") { pos++; left -= ParseTerm(tokens, ref pos); }
                else break;
            }
            return left;
        }

        private double ParseTerm(List<string> tokens, ref int pos)
        {
            double left = ParseFactor(tokens, ref pos);
            while (pos < tokens.Count)
            {
                string op = tokens[pos];
                if (op == "*") { pos++; left *= ParseFactor(tokens, ref pos); }
                else if (op == "/") { pos++; double right = ParseFactor(tokens, ref pos); if (right == 0) throw new DivideByZeroException(); left /= right; }
                else break;
            }
            return left;
        }

        private double ParseFactor(List<string> tokens, ref int pos)
        {
            if (pos >= tokens.Count) throw new InvalidOperationException("Unexpected end of expression");
            string token = tokens[pos];
            if (token == "(") { pos++; double result = ParseExpression(tokens, ref pos); if (pos >= tokens.Count || tokens[pos] != ")") throw new InvalidOperationException("Missing closing parenthesis"); pos++; return result; }
            if (token.EndsWith(".")) throw new FormatException($"Invalid number ending with dot: {token}");
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) { pos++; return num; }
            pos++;
            return 0;
        }
    }
}