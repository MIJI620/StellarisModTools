# Stellaris.Tests

零第三方依赖的测试项目（控制台可执行），用于固化**边缘/非常规但合法**的解析样例，
防止后续改动破坏行为。运行方式：

```bash
dotnet run --project Stellaris.Tests
```

任一测试失败时进程返回非零退出码，便于脚本/CI 判断。

## 如何新增一个样例测试

1. 将样例文件放入 `Stellaris.Tests/TestData/` 目录（构建时自动复制到输出目录）。
2. 在某个测试类（如 `EdgeCaseTests.cs`）中新增 `[Test]` 方法：

```csharp
[Test]
public void MyWeirdSample()
{
    string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData/my_file.txt"));
    var lexer = new Lexer(source);
    var tokens = new List<Token>();
    Token tok;
    while ((tok = lexer.NextToken()).Type != TokenType.Eof) tokens.Add(tok);
    var result = new Parser(tokens, source.Split('\n'), "my_file.txt", source).Parse();
    Assert.True(result.Errors.Count == 0, "该样例应为合法");
    // ... 按需断言 AST 结构、RawText、Value 等
}
```

3. 运行测试验证。

## 已固化的样例

- `TestData/sample_legal.txt` —— 用户提供的合法样例，覆盖：
  - 顶层常量声明 `@1221 = 1515.5` 与常量引用 `xx = @1221`（RawText 保留原文）。
  - **block 内嵌列表**：`c = { "cker" "dyyx" }` 判定为 List。
  - **双引号贪婪匹配**：字符串从行内第一个 `"` 延伸到该行最后一个 `"`，中间全部内容
    （含其他引号、`=`、`#`）都属于字符串。因此 `x="1" assa="2275 # 57775"` 中
    x 的值是 `1" assa="2275 # 57775`，`assa` 不是独立键。
  - 数字键 `37 = 52` 与字母数字混合键 `22z = 14`。
  - 注释 `# 77648` 关联到后续块 `blck`。
  - 序列化后重新解析（往返）关键信息不变。
