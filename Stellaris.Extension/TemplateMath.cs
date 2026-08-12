// 文件: Stellaris.Extension/TemplateMath.cs
// CLI v3.2 模板表达式求值器：整数算术 + 数组字面量索引 + 三元 + 绑定变量。
// 用途：foreach 数值范围每轮生成 {expr:...} 内嵌表达式（如 2000 飞升槽坐标公式）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Stellaris.Extension
{
    /// <summary>
    /// 整数表达式求值器（无正则、无动态编译——手写词法 + 递归下降）。
    /// 支持：整数常量、绑定变量（vars）、+ - * / %、括号、数组字面量 [a,b,...] 与索引 a[i]、
    /// 比较 == != &lt; &lt;= &gt; &gt;=、三元 c ? a : b。除零/索引越界 → 抛 InvalidOperationException。
    /// </summary>
    public static class TemplateMath
    {
        public static long Evaluate(string expression, IReadOnlyDictionary<string, long>? vars = null)
        {
            if (string.IsNullOrWhiteSpace(expression))
                throw new InvalidOperationException("表达式为空");
            var p = new ExprParser(expression, vars ?? new Dictionary<string, long>());
            long result = p.ParseExpr();
            p.SkipWs();
            if (!p.AtEnd)
                throw new InvalidOperationException($"表达式意外结尾: '{expression}' (位置 {p.Pos})");
            return result;
        }

        private sealed class ExprParser
        {
            private readonly string _s;
            private readonly IReadOnlyDictionary<string, long> _vars;
            public int Pos;

            public ExprParser(string s, IReadOnlyDictionary<string, long> vars)
            {
                _s = s;
                _vars = vars;
            }

            public bool AtEnd => Pos >= _s.Length;

            public void SkipWs()
            {
                while (Pos < _s.Length && char.IsWhiteSpace(_s[Pos])) Pos++;
            }

            private char Peek()
            {
                SkipWs();
                return Pos < _s.Length ? _s[Pos] : '\0';
            }

            private bool TryTake(string word)
            {
                SkipWs();
                if (Pos + word.Length <= _s.Length && _s.Substring(Pos, word.Length) == word)
                {
                    Pos += word.Length;
                    return true;
                }
                return false;
            }

            /// <summary>expr := ternary</summary>
            public long ParseExpr() => ParseTernary();

            private long ParseTernary()
            {
                long cond = ParseCompare();
                if (TryTake("?"))
                {
                    long a = ParseExpr();
                    if (!TryTake(":"))
                        throw new InvalidOperationException($"三元表达式缺 ':' (位置 {Pos})");
                    long b = ParseExpr();
                    return cond != 0 ? a : b;
                }
                return cond;
            }

            private long ParseCompare()
            {
                long left = ParseAdditive();
                while (true)
                {
                    if (TryTake("==")) { var r = ParseAdditive(); left = left == r ? 1 : 0; }
                    else if (TryTake("!=")) { var r = ParseAdditive(); left = left != r ? 1 : 0; }
                    else if (TryTake("<=")) { var r = ParseAdditive(); left = left <= r ? 1 : 0; }
                    else if (TryTake(">=")) { var r = ParseAdditive(); left = left >= r ? 1 : 0; }
                    else if (TryTake("<")) { var r = ParseAdditive(); left = left < r ? 1 : 0; }
                    else if (TryTake(">")) { var r = ParseAdditive(); left = left > r ? 1 : 0; }
                    else return left;
                }
            }

            private long ParseAdditive()
            {
                long left = ParseMultiplicative();
                while (true)
                {
                    SkipWs();
                    if (Pos < _s.Length && _s[Pos] == '+') { Pos++; left += ParseMultiplicative(); }
                    else if (Pos < _s.Length && _s[Pos] == '-') { Pos++; left -= ParseMultiplicative(); }
                    else return left;
                }
            }

            private long ParseMultiplicative()
            {
                long left = ParseUnary();
                while (true)
                {
                    SkipWs();
                    if (Pos < _s.Length && _s[Pos] == '*') { Pos++; left *= ParseUnary(); }
                    else if (Pos < _s.Length && _s[Pos] == '/')
                    {
                        Pos++;
                        long r = ParseUnary();
                        if (r == 0) throw new InvalidOperationException("表达式除零");
                        left /= r;
                    }
                    else if (Pos < _s.Length && _s[Pos] == '%')
                    {
                        Pos++;
                        long r = ParseUnary();
                        if (r == 0) throw new InvalidOperationException("表达式取模零");
                        left %= r;
                    }
                    else return left;
                }
            }

            private long ParseUnary()
            {
                SkipWs();
                if (Pos < _s.Length && _s[Pos] == '-')
                {
                    Pos++;
                    return -ParseUnary();
                }
                return ParsePostfix();
            }

            private long ParsePostfix()
            {
                long value = ParseAtom();
                // 数组索引：arr[i]（链式 a[i][j]）
                while (true)
                {
                    SkipWs();
                    if (Pos < _s.Length && _s[Pos] == '[')
                    {
                        Pos++;
                        long index = ParseExpr();
                        SkipWs();
                        if (Pos >= _s.Length || _s[Pos] != ']')
                            throw new InvalidOperationException($"数组索引缺 ']' (位置 {Pos})");
                        Pos++;
                        // 索引要求原子是数组字面量或数组变量——见 _lastArray
                        if (_lastArray == null)
                            throw new InvalidOperationException($"表达式 '{_s}' 中索引对象不是数组字面量");
                        if (index < 0 || index >= _lastArray.Count)
                            throw new InvalidOperationException($"数组索引越界: {index} (长度 {_lastArray.Count})");
                        value = _lastArray[(int)index];
                    }
                    else return value;
                }
            }

            private List<long>? _lastArray;

            private long ParseAtom()
            {
                SkipWs();
                if (Pos >= _s.Length)
                    throw new InvalidOperationException($"表达式意外结束: '{_s}'");
                char c = _s[Pos];
                if (c == '(')
                {
                    Pos++;
                    long v = ParseExpr();
                    SkipWs();
                    if (Pos >= _s.Length || _s[Pos] != ')')
                        throw new InvalidOperationException($"缺 ')' (位置 {Pos})");
                    Pos++;
                    return v;
                }
                if (c == '[')
                {
                    Pos++;
                    var arr = new List<long>();
                    SkipWs();
                    if (Pos < _s.Length && _s[Pos] == ']')
                    {
                        Pos++;
                        _lastArray = arr;
                        throw new InvalidOperationException("空数组字面量不支持（无法索引）");
                    }
                    while (true)
                    {
                        arr.Add(ParseExpr());
                        SkipWs();
                        if (Pos >= _s.Length)
                            throw new InvalidOperationException($"数组字面量缺 ']' (位置 {Pos})");
                        if (_s[Pos] == ',') { Pos++; continue; }
                        if (_s[Pos] == ']') { Pos++; break; }
                        throw new InvalidOperationException($"数组字面量意外字符 '{_s[Pos]}' (位置 {Pos})");
                    }
                    _lastArray = arr;
                    // 数组字面量本身作为值返回第一个元素（仅用于随后索引）——直接索引路径用 _lastArray
                    return arr.Count > 0 ? arr[0] : 0;
                }
                if (char.IsDigit(c))
                {
                    int start = Pos;
                    while (Pos < _s.Length && char.IsDigit(_s[Pos])) Pos++;
                    return long.Parse(_s.Substring(start, Pos - start), CultureInfo.InvariantCulture);
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = Pos;
                    while (Pos < _s.Length && (char.IsLetterOrDigit(_s[Pos]) || _s[Pos] == '_')) Pos++;
                    string name = _s.Substring(start, Pos - start);
                    if (!_vars.TryGetValue(name, out long v))
                        throw new InvalidOperationException($"表达式未绑定变量: '{name}'");
                    return v;
                }
                throw new InvalidOperationException($"表达式意外字符 '{c}' (位置 {Pos})");
            }
        }
    }
}
