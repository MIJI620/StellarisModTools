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
  - **相邻双引号配对**（用户实测原版确认）：字符串从第一个 `"` 读到**下一个** `"` 即终止。
    `x="1" assa="2275 # 57775"` 中 `"1"` 是 x 的完整字符串，**`assa` 是独立键**
    （引号内 `#` 合法，不触发注释）。本地化 yml 才是行贪婪（`LocalisationParser`），互不干扰。
  - 数字键 `37 = 52` 与字母数字混合键 `22z = 14`。
  - 注释 `# 77648` 关联到后续块 `blck`。
  - 序列化后重新解析（往返）关键信息不变。

## 新增的功能覆盖测试（FunctionalCoverageTests）

- **解析器**：相邻引号同行多赋值、注释跳过、错误行记录。
- **星系样式引擎**：Add/Get/Update/Rename/Reorder/Delete 全链路、三种形状参数多边形产出。
- **地图引擎**：动态/静态场景 CRUD、绑定样式、形状顺序、容量预估、保存往返重载。
- **2 轮完整流程**（TestFileRoundTripFindsCookie）：SA 读 TestData 原始测试文件 → 引擎解析找到目标值；
  反向写入 cookie → 写回沙盒副本（不覆盖原文件）→ 重读再次找到。
- **抗爆炸**（AntiExplosionTokensDoNotCrash）：`TestData/boom_tokens.txt` 故意留错误 token
  （未闭合引号/多余闭括号/非法键/坏数字）——解析不崩溃、错误被记录、错误后的正常行继续解析
  （与游戏行为一致）。

`TestData/` 现含：`sample_legal.txt`、`boom_tokens.txt`、`events/more_galaxy_test_events.txt`、
`common/inline_scripts/test/test1.txt|test2.txt`、`common/component_templates/...`（用户曾经的测试文件副本）。
