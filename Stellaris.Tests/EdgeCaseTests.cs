// 文件: Stellaris.Tests/EdgeCaseTests.cs
// 用户提出的"非常不正常"但合法的边缘样例测试。
// 新增样例的方法：将样例文件放入 TestData/ 目录（复制到输出目录），
// 然后在此处新增一个 [Test] 方法读取并断言。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stellaris.Parser;

// 类型 Parser 与命名空间 Stellaris.Parser 重名，需别名消歧
using StellarisParser = Stellaris.Parser.Parser;

namespace Stellaris.Tests;

/// <summary>
/// 用户提供的合法样例（TestData/sample_legal.txt）完整解析 + 往返测试。
/// 覆盖：block 内嵌列表、双引号贪婪匹配（行内第一个引号到最后一个引号）、
/// 数字键、常量声明与引用、注释关联。
/// </summary>
public sealed class EdgeCaseTests
{
    private const string SamplePath = "TestData/sample_legal.txt";

    private static ParserResult ParseSample()
    {
        string path = Path.Combine(AppContext.BaseDirectory, SamplePath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到样例文件: {path}");

        string source = File.ReadAllText(path);
        var lexer = new Lexer(source);
        var tokens = new List<Token>();
        Token tok;
        while ((tok = lexer.NextToken()).Type != TokenType.Eof)
            tokens.Add(tok);

        var parser = new StellarisParser(tokens, source.Split('\n'), SamplePath, source);
        return parser.Parse();
    }

    private static List<AstNode> GetBlockChildren(ParserResult result, string blockKey)
    {
        var block = result.RootNodes.FirstOrDefault(n => n.Key == blockKey);
        Assert.NotNull(block, $"顶层块 '{blockKey}' 存在");
        return block!.Children;
    }

    // ==================== 样例整体 ====================

    [Test]
    public void UserSample_IsFullyLegal()
    {
        var result = ParseSample();
        Assert.True(result.Errors.Count == 0,
            $"用户样例应为完全合法（当前错误 {result.Errors.Count} 个：" +
            string.Join(" | ", result.Errors.Select(e => $"[L{e.Line}:{e.Column}] {e.Reason}")));
    }

    [Test]
    public void UserSample_TopLevelStructure()
    {
        var result = ParseSample();
        Assert.Equal(2, result.RootNodes.Count, "顶层节点数");
        Assert.Equal("@1221", result.RootNodes[0].Key, "第一个节点为常量声明键");
        Assert.Equal(NodeType.Simple, result.RootNodes[0].Type, "常量声明为 Simple");
        Assert.Equal(NodeType.Block, result.RootNodes[1].Type, "a 为 Block");
        Assert.Equal("a", result.RootNodes[1].Key, "a 键");
    }

    [Test]
    public void UserSample_ConstantDeclarationAndReference()
    {
        var result = ParseSample();

        // 常量声明 @1221 = 1515.5
        var constNode = result.RootNodes[0];
        Assert.True(constNode.Value is double d && Math.Abs(d - 1515.5) < 1e-9, "常量值解析为 1515.5");
        Assert.Equal("1515.5", constNode.RawText, "常量值 RawText 保留原文");

        // 常量引用 xx = @1221
        var xx = GetBlockChildren(result, "a").First(ch => ch.Key == "xx");
        Assert.True(xx.Value is ConstantValue cv && cv.Type == ConstantType.Simple && cv.Name == "1221",
            "xx 的值是常量引用 @1221");
        Assert.Equal("@1221", xx.RawText, "xx 的 RawText 保留 @1221 原文");
    }

    // ==================== 用户强调：block 内可以出现列表 ====================

    [Test]
    public void UserSample_BlockContainsList()
    {
        var result = ParseSample();
        var a = GetBlockChildren(result, "a");

        var c = a.FirstOrDefault(ch => ch.Key == "c");
        Assert.NotNull(c, "c 节点存在");
        Assert.Equal(NodeType.List, c!.Type, "c 是 List（block 内嵌列表合法）");
        Assert.Equal(2, c.Children.Count, "c 有 2 个元素");
        Assert.True(c.Children.All(ch => ch.Type == NodeType.Simple && ch.Key == null),
            "c 的所有元素为无键 Simple");

        Assert.Equal("\"cker\"", c.Children[0].RawText, "列表元素保留引号原文");
        Assert.Equal("\"dyyx\"", c.Children[1].RawText, "列表元素保留引号原文");
    }

    // ==================== 用户强调：双引号贪婪匹配（行内第一个引号到最后一个引号） ====================

    [Test]
    public void UserSample_StringGreedyToLastQuoteOnLine()
    {
        var result = ParseSample();
        var inline = GetBlockChildren(result, "a").First(ch => ch.Key == "inline_script");
        Assert.Equal(NodeType.Block, inline.Type, "解析器为通用解析器：inline_script 块为普通 Block（内联识别由引擎层 ScriptExpander 完成）");

        // x="1" assa="2275 # 57775"：原版规则为**相邻双引号配对**（用户实测）——
        // "1" 是 x 的完整字符串；assa 是独立键（引号内 # 合法）
        var x = inline.Children.First(ch => ch.Key == "x");
        Assert.Equal("1", x.Value?.ToString(), "x 的值 = 相邻配对的第一对引号内容");
        var assa = inline.Children.FirstOrDefault(ch => ch.Key == "assa");
        Assert.True(assa != null, "assa 是独立键（相邻配对）");
        Assert.Equal("2275 # 57775", assa?.Value?.ToString(), "assa 的值含 #（引号内 # 合法）");
    }

    // ==================== 数字键 ====================

    [Test]
    public void UserSample_NumericKey()
    {
        var result = ParseSample();
        var inline = GetBlockChildren(result, "a").First(ch => ch.Key == "inline_script");

        var key37 = inline.Children.FirstOrDefault(ch => ch.Key == "37");
        Assert.NotNull(key37, "数字键 37 存在");
        Assert.True(Equals(key37!.Value, 52), "37 的值是 52");
        Assert.Equal("52", key37.RawText, "数字值 RawText");

        var key22z = inline.Children.FirstOrDefault(ch => ch.Key == "22z");
        Assert.NotNull(key22z, "字母数字混合键 22z 存在");
        Assert.True(Equals(key22z!.Value, 14), "22z 的值是 14");
    }

    // ==================== 注释关联 ====================

    [Test]
    public void UserSample_CommentAssociatedToFollowingBlock()
    {
        var result = ParseSample();
        var blck = GetBlockChildren(result, "a").FirstOrDefault(ch => ch.Key == "blck");
        Assert.NotNull(blck, "blck 块存在");
        Assert.Equal(NodeType.Block, blck!.Type, "blck 为 Block");

        Assert.Equal(1, blck.AssociatedComments.Count, "# 77648 关联到 blck");
        Assert.Equal(" 77648", blck.AssociatedComments[0].Value?.ToString(), "注释内容保留（不含 #）");

        var ax = blck.Children.FirstOrDefault(ch => ch.Key == "ax");
        Assert.NotNull(ax, "ax 键存在");
        Assert.Equal("zzqp", ax!.Value?.ToString(), "ax 的值为 zzqp");
        Assert.Equal("zzqp", ax.RawText, "ax 的 RawText");
    }

    // ==================== 序列化往返 ====================

    [Test]
    public void RootSeparatorRule_UserDefined()
    {
        // 用户定义（2026-08）：根节点之间只有"2 个不同类型节点"或"2 个 Block"才空一行；
        // 相同类型（如两个 Simple @变量）紧挨一行。
        var source = "@a = 2\n@b = 3\nc = {}\n";
        var lexer = new Lexer(source);
        var tokens = new List<Token>();
        Token tok;
        while ((tok = lexer.NextToken()).Type != TokenType.Eof)
            tokens.Add(tok);
        var parser = new StellarisParser(tokens, source.Split('\n'), null, source);
        var roots = parser.Parse().RootNodes;

        var ser = SerializationHelper.Serialize(roots);
        ser = ser.Replace("\r", "");   // Windows AppendLine 输出 \r\n——断言用 \n 归一
        // @a/@b 同 Simple → 紧挨；@b → c={} 不同类型 → 空一行
        Assert.True(ser.Contains("@a = 2\n@b = 3\n\nc = {", StringComparison.Ordinal), "Simple→Block 空一行：" + ser.Replace("\n", "|"));
        Assert.False(ser.Contains("@a = 2\n\n@b = 3", StringComparison.Ordinal), "Simple-Simple 紧挨");
    }

    [Test]
    public void UserSample_RoundTripPreservesContent()
    {
        var result = ParseSample();

        string serialized = SerializationHelper.Serialize(result.RootNodes);

        // 重新解析序列化结果
        var lexer = new Lexer(serialized);
        var tokens = new List<Token>();
        Token tok;
        while ((tok = lexer.NextToken()).Type != TokenType.Eof)
            tokens.Add(tok);
        var parser = new StellarisParser(tokens, serialized.Split('\n'), null, serialized);
        var result2 = parser.Parse();

        Assert.True(result2.Errors.Count == 0, "往返后无解析错误");
        Assert.Equal(2, result2.RootNodes.Count, "往返后顶层节点数不变");

        var inline = GetBlockChildren(result2, "a").First(ch => ch.Key == "inline_script");
        var x = inline.Children.First(ch => ch.Key == "x");
        Assert.Equal("1", x.Value?.ToString(), "往返后相邻配对字符串值不变");

        var xx = GetBlockChildren(result2, "a").First(ch => ch.Key == "xx");
        Assert.Equal("@1221", xx.RawText, "往返后常量引用 @1221 保留");

        var c = GetBlockChildren(result2, "a").First(ch => ch.Key == "c");
        Assert.Equal(NodeType.List, c.Type, "往返后 c 仍为 List");
    }
}
