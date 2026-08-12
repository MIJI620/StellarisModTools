using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stellaris.Parser;

namespace Stellaris.Tests;

/// <summary>SA 标准搜索（SelectorResolver）测试——新规范：枝（mode 必填 + match.rule 数组 / index 1 起互斥）
/// + 叶（target / index）+ SelectResult（Hits+Errors 内存告知）+ 逐层推进不跳层。</summary>
public sealed class DictSelectorTests
{
    private static StellarisAdapter Build(string content, string relPath = "common/edicts/test.txt")
    {
        string tmp = Path.Combine(Path.GetTempPath(), "smt_sel_" + Guid.NewGuid().ToString("N"));
        string baseRoot = Path.Combine(tmp, "base");
        string full = Path.Combine(baseRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        var adapter = new StellarisAdapter();
        adapter.AddRoot(baseRoot);
        adapter.ScanAll();
        return adapter;
    }

    private static Dictionary<string, object> D(params (string, object)[] kv)
        => kv.ToDictionary(k => k.Item1, k => k.Item2);

    private static List<object> P(params object[] selectors) => selectors.ToList();

    // 常用构造：mode + rule（条件数组）
    private static Dictionary<string, object> Branch(string mode, List<object> rule, string checkRule = "And")
        => D(("mode", mode), ("match", D(("rule", rule), ("check_rule", checkRule))));

    private static Dictionary<string, object> KeyLeaf(string key)
        => D(("target", "key"), ("keywords", new List<object> { key }));

    private static Dictionary<string, object> KeyValueLeaf(string key, string value)
        => D(("target", "key"), ("keywords", new List<object> { key }),
             ("target2", null), ("type", "equals"));   // 占位——不用

    [Test]
    public void BranchModeFiltersNodeTypes()
    {
        var sa = Build(
            "my_edict = {\n resources = {\n  cost = { influence = 2 }\n }\n}\n");
        // 顶层：mode 过滤（空 rule = 仅类型）
        Assert.Equal(1, sa.SelectNodes("common/edicts/test.txt", P(Branch("Block", new()))).Hits.Count, "顶层 Block");
        Assert.Equal(0, sa.SelectNodes("common/edicts/test.txt", P(Branch("Simple", new()))).Hits.Count, "顶层无 Simple");
        // 逐层推进：my_edict → resources → cost
        Assert.Equal(1, sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("my_edict") }))).Hits.Count, "my_edict");
        Assert.Equal(1, sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("my_edict") }),
            Branch("Block", new List<object> { KeyLeaf("resources") }))).Hits.Count, "resources");
        Assert.Equal(1, sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("my_edict") }),
            Branch("Block", new List<object> { KeyLeaf("resources") }),
            Branch("Block", new List<object> { KeyLeaf("cost") }))).Hits.Count, "cost");
        // resources 子层 Block = cost
        Assert.Equal(1, sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("my_edict") }),
            Branch("Block", new List<object> { KeyLeaf("resources") }),
            Branch("Block", new()))).Hits.Count, "resources 子层 Block");
    }

    [Test]
    public void MultiConditionSameNodeFieldAndValue()
    {
        // "字段 c 且值 k"：两个叶条件（key + value）作用于同一候选节点
        var sa = Build(
            "shelter = {\n c = k\n c = l\n}\n");
        var hit = sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("shelter") }),
            Branch("Simple", new List<object>
            {
                D(("target", "key"), ("keywords", new List<object> { "c" })),
                D(("target", "value"), ("keywords", new List<object> { "k" }))
            })));
        Assert.Equal(1, hit.Hits.Count, "c=k 命中");
        Assert.Equal("c", hit.Hits[0].Key, "命中 c");
        Assert.False(hit.HasErrors, "无错误");
    }

    [Test]
    public void NestedRuleConditionAndCheckRule()
    {
        // 用户例子：a = { b = {c=k, d={e=12}} b = {c=l, d={e=22}} b = {c=k, d={e=22}} }
        // 选 c=k 且 d.e=12 的 b 块——目标 = b 块本身
        var sa = Build(
            "a = {\n" +
            " b = { c = k d = { e = 12 } }\n" +
            " b = { c = l d = { e = 22 } }\n" +
            " b = { c = k d = { e = 22 } }\n" +
            "}\n");
        var r = sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("a") }),
            Branch("Block", new List<object>
            {
                // 条件1：b 内容里存在 Simple（c=k）
                Branch("Simple", new List<object>
                {
                    D(("target", "key"), ("keywords", new List<object> { "c" })),
                    D(("target", "value"), ("keywords", new List<object> { "k" }))
                }),
                // 条件2：b 内容里存在 d 块、其内存在 Simple（e=12）
                Branch("Block", new List<object>
                {
                    KeyLeaf("d"),
                    Branch("Simple", new List<object>
                    {
                        D(("target", "key"), ("keywords", new List<object> { "e" })),
                        D(("target", "value"), ("keywords", new List<object> { "12" }))
                    })
                })
            }, checkRule: "And")));
        Assert.Equal(1, r.Hits.Count, "命中 1 个 b 块");
        Assert.True(r.Hits[0].Children.Any(c => c.Key == "d" && c.Children.Any(x => x.Key == "e" && x.Value?.ToString() == "12")),
            "命中 c=k 且 e=12 的 b（第一个）");
        Assert.False(r.HasErrors, "无错误");
    }

    [Test]
    public void BranchIndexOneBasedAndMode()
    {
        var sa = Build(
            "a = {\n b = { x = 1 }\n c = { x = 2 }\n b = { x = 3 }\n}\n");
        // 枝 index 1 起：a 子层第 1 个 Block = b（第 1 个 b）
        var first = sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("a") }),
            D(("mode", "Block"), ("index", 1))));
        Assert.Equal(1, first.Hits.Count, "第 1 个 Block");
        Assert.True(first.Hits[0].Children.Any(c => c.Key == "x" && c.Value?.ToString() == "1"), "第 1 个是 x=1 的 b");
        // index 2 数类型：第 2 个 Block = c（跳过 Simple 影响——本层无 Simple，直接验证 b/c 序）
        var second = sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("a") }),
            D(("mode", "Block"), ("index", 2))));
        Assert.Equal(1, second.Hits.Count, "第 2 个 Block");
        Assert.True(second.Hits[0].Children.Any(c => c.Key == "x" && c.Value?.ToString() == "2"), "第 2 个是 x=2 的 c");
        // 越界 → Errors 告知（不抛异常）
        var over = sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("a") }),
            D(("mode", "Block"), ("index", 9))));
        Assert.Equal(0, over.Hits.Count, "越界无命中");
        Assert.True(over.HasErrors, "越界记错误");
    }

    [Test]
    public void LeafIndexChecksPositionInChildren()
    {
        var sa = Build(
            "a = {\n b = { c = k d = { e = 12 } }\n b = { c = l d = { e = 22 } }\n}\n");
        // 叶 index：b 的 Children 第 1 个存在 + d 内 e=22 → c=l 的 b
        var r = sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("a") }),
            Branch("Block", new List<object>
            {
                D(("index", 1)),   // 叶 index：Children 第 1 个存在（1 起）
                Branch("Block", new List<object>
                {
                    KeyLeaf("d"),
                    Branch("Simple", new List<object>
                    {
                        D(("target", "key"), ("keywords", new List<object> { "e" })),
                        D(("target", "value"), ("keywords", new List<object> { "22" }))
                    })
                })
            })));
        Assert.Equal(1, r.Hits.Count, "c=l 的 b（第 1 个孩子存在 + e=22）");
        Assert.True(r.Hits[0].Children.Any(c => c.Key == "c" && c.Value?.ToString() == "l"), "命中 c=l");
        // 叶 index=5（Children 没有第 5 个）→ false（该条件不满足，但非错误）
        var noFifth = sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("a") }),
            Branch("Block", new List<object> { D(("index", 5)) })));
        Assert.Equal(0, noFifth.Hits.Count, "第 5 个孩子不存在 → 不命中");
        Assert.False(noFifth.HasErrors, "叶 index 是判断（false），非错误");
    }

    [Test]
    public void ErrorsAreCollectedNotThrown()
    {
        var sa = Build(
            "a = {\n b = { c = k }\n}\n");
        // match 与 index 互斥 → 错误
        var mutual = sa.SelectNodes("common/edicts/test.txt", P(
            D(("mode", "Block"), ("match", D(("rule", new List<object> { KeyLeaf("a") }))), ("index", 1))));
        Assert.True(mutual.HasErrors, "match+index 互斥 → 错误");
        Assert.Equal(0, mutual.Hits.Count, "无命中");
        // match 枝 mode 缺省 → 错误
        var noMode = sa.SelectNodes("common/edicts/test.txt", P(
            D(("match", D(("rule", new List<object> { KeyLeaf("a") }))))));
        Assert.True(noMode.HasErrors, "mode 缺省 → 错误");
        // 枝既无 match 也无 index → 错误
        var empty = sa.SelectNodes("common/edicts/test.txt", P(D(("mode", "Block"))));
        Assert.True(empty.HasErrors, "无 match/index → 错误");
        // 错误不抛异常、不连带上层——正常查询仍工作
        var ok = sa.SelectNodes("common/edicts/test.txt", P(
            Branch("Block", new List<object> { KeyLeaf("a") })));
        Assert.Equal(1, ok.Hits.Count, "正常查询不受影响");
        Assert.False(ok.HasErrors, "无错误");
    }

    [Test]
    public void ValueTargetListContainsElementsAndBlockContainsKeys()
    {
        // 用户定稿语义：target=value——List = 元素集合包含 keywords；Block = 内容里含该 key
        var sa = Build(
            "component = {\n" +
            " prerequisites = { \"tech_antimatter_power\" \"tech_foo\" }\n" +
            " potential = {\n" +
            "  ship_uses_corvette_reactors = yes\n" +
            "  from = { country_uses_bio_ships = no }\n" +
            " }\n" +
            "}\n");
        string path = "common/edicts/test.txt";
        // List 包含元素：prerequisites 命中（含 tech_antimatter_power）
        var listHit = sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("component") }),
            Branch("List", new List<object>
            {
                D(("target", "value"), ("keywords", new List<object> { "tech_antimatter_power" }))
            })));
        Assert.Equal(1, listHit.Hits.Count, "prerequisites 命中（包含元素）");
        Assert.Equal("prerequisites", listHit.Hits[0].Key, "命中 List 节点");
        // List 多值 = 全部包含（AND）
        var listAnd = sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("component") }),
            Branch("List", new List<object>
            {
                D(("target", "value"), ("keywords", new List<object> { "tech_antimatter_power", "tech_foo" }))
            })));
        Assert.Equal(1, listAnd.Hits.Count, "两个元素都在 → 命中");
        var listMissing = sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("component") }),
            Branch("List", new List<object>
            {
                D(("target", "value"), ("keywords", new List<object> { "tech_antimatter_power", "tech_missing" }))
            })));
        Assert.Equal(0, listMissing.Hits.Count, "缺一个元素 → 不命中");
        // Block 含 key：potential 命中（含 ship_uses_corvette_reactors）
        var blockHit = sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("component") }),
            Branch("Block", new List<object>
            {
                D(("target", "value"), ("keywords", new List<object> { "ship_uses_corvette_reactors" }))
            })));
        Assert.Equal(1, blockHit.Hits.Count, "potential 命中（含该 key）");
        Assert.Equal("potential", blockHit.Hits[0].Key, "命中 Block 节点");
        // Block 多值 = 全部存在
        var blockBoth = sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("component") }),
            Branch("Block", new List<object>
            {
                D(("target", "value"), ("keywords", new List<object> { "ship_uses_corvette_reactors", "from" }))
            })));
        Assert.Equal(1, blockBoth.Hits.Count, "两个 key 都在 → 命中");
    }

    [Test]
    public void AddBeforeAfterInsertsRelativeToTarget()
    {
        var sa = Build(
            "a = {\n b = { x = 1 }\n c = { y = 2 }\n d = 3\n}\n");
        string path = "common/edicts/test.txt";
        var blockB = P(Branch("Block", new List<object> { KeyLeaf("a") }), Branch("Block", new List<object> { KeyLeaf("b") }));
        var simpleD = P(Branch("Block", new List<object> { KeyLeaf("a") }), Branch("Simple", new List<object> { KeyLeaf("d") }));
        // Before：block b 前插新块
        sa.AddConfigNode(path, blockB, new AstNode { Type = NodeType.Block, Key = "z", Children = new() }, position: AddPosition.Before);
        var after = sa.SelectNodes(path, P(Branch("Block", new List<object> { KeyLeaf("a") }))).Hits[0];
        Assert.Equal("z", after.Children[0].Key, "Before：新块在 b 前（第 1 位）");
        Assert.Equal("b", after.Children[1].Key, "b 紧跟其后");
        // After：block b 后插新块
        sa.AddConfigNode(path, blockB, new AstNode { Type = NodeType.Block, Key = "w", Children = new() }, position: AddPosition.After);
        var after2 = sa.SelectNodes(path, P(Branch("Block", new List<object> { KeyLeaf("a") }))).Hits[0];
        int idxB = System.Array.FindIndex(after2.Children.ToArray(), n => n.Key == "b");
        Assert.Equal("w", after2.Children[idxB + 1].Key, "After：新块在 b 后");
        // After：simple d 后插 simple（同层）
        sa.AddConfigNode(path, simpleD, new AstNode { Type = NodeType.Simple, Key = "e", Value = "9" }, position: AddPosition.After);
        var after3 = sa.SelectNodes(path, P(Branch("Block", new List<object> { KeyLeaf("a") }))).Hits[0];
        int idxD = System.Array.FindIndex(after3.Children.ToArray(), n => n.Key == "d");
        Assert.Equal("e", after3.Children[idxD + 1].Key, "After：simple 后插 simple");
        // Append 缺省不受影响（c 之后追加）
        sa.AddConfigNode(path, P(Branch("Block", new List<object> { KeyLeaf("a") })), new AstNode { Type = NodeType.Simple, Key = "tail", Value = "1" });
        var after4 = sa.SelectNodes(path, P(Branch("Block", new List<object> { KeyLeaf("a") }))).Hits[0];
        Assert.Equal("tail", after4.Children[after4.Children.Count - 1].Key, "Append 缺省：末尾");
    }

    [Test]
    public void LeafCheckRuleCombinesKeywords()
    {
        // 叶内 check_rule（用户原始设计）：组合 keywords 的命中结果——And/Or/Nor/Nand，缺省 And
        var sa = Build(
            "component = {\n" +
            " prerequisites = { \"tech_antimatter_power\" \"tech_foo\" }\n" +
            " from = { a = 1 }\n" +
            "}\n");
        string path = "common/edicts/test.txt";
        // key Or：key = from 或 prerequisites（任一命中）
        var keyOr = sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("component") }),
            Branch("Any", new List<object>
            {
                D(("target", "key"), ("keywords", new List<object> { "from", "prerequisites" }), ("check_rule", "Or"))
            })));
        Assert.Equal(2, keyOr.Hits.Count, "key Or：from 和 prerequisites 都命中");
        // key And（缺省）：key 同时是 from 和 prerequisites → 不可能 → 0
        var keyAnd = sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("component") }),
            Branch("Any", new List<object>
            {
                D(("target", "key"), ("keywords", new List<object> { "from", "prerequisites" }))
            })));
        Assert.Equal(0, keyAnd.Hits.Count, "key And：无命中");
        // List Or：包含 a 或 b（任一元素）
        var listOr = sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("component") }),
            Branch("List", new List<object>
            {
                D(("target", "value"), ("keywords", new List<object> { "tech_antimatter_power", "tech_zzz" }), ("check_rule", "Or"))
            })));
        Assert.Equal(1, listOr.Hits.Count, "List Or：含任一 → 命中");
        // List Nand：不是全含（缺一个）→ 命中
        var listNand = sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("component") }),
            Branch("List", new List<object>
            {
                D(("target", "value"), ("keywords", new List<object> { "tech_antimatter_power", "tech_zzz" }), ("check_rule", "Nand"))
            })));
        Assert.Equal(1, listNand.Hits.Count, "List Nand：非全含 → 命中");
        // 空 List：任何 check_rule 都不命中
        var sa2 = Build(
            "empty = {\n prerequisites = { }\n}\n");
        var emptyList = sa2.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("empty") }),
            Branch("List", new List<object>
            {
                D(("target", "value"), ("keywords", new List<object> { "x" }), ("check_rule", "Or"))
            })));
        Assert.Equal(0, emptyList.Hits.Count, "空 List 不命中");
    }

    [Test]
    public void RenameKeyUpdatesKeyAndClearsRawText()
    {
        var sa = Build(
            "a = {\n power = @reactor_power_4\n c = k\n}\n");
        string path = "common/edicts/test.txt";
        // Simple 改名（带 RawText 场景：power = @reactor_power_4 原始文本）
        var p = P(
            Branch("Block", new List<object> { KeyLeaf("a") }),
            Branch("Simple", new List<object> { KeyLeaf("power") }));
        sa.RenameKey(path, p, "shelter_power");
        // 新 key 命中、旧 key 不命中
        Assert.Equal(1, sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("a") }),
            Branch("Simple", new List<object> { KeyLeaf("shelter_power") }))).Hits.Count, "新 key 命中");
        Assert.Equal(0, sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("a") }),
            Branch("Simple", new List<object> { KeyLeaf("power") }))).Hits.Count, "旧 key 不再命中");
        // RawText 只记录值的原始文本（不含 Key）——改名不影响值，RawText 保留
        var renamed = sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("a") }),
            Branch("Simple", new List<object> { KeyLeaf("shelter_power") }))).Hits[0];
        Assert.Equal("@reactor_power_4", renamed.RawText, "改名后 RawText 保留（值未变）");
        Assert.True(renamed.Value is ConstantValue cv && cv.Name == "reactor_power_4", "Value 保留（常量引用）");
        // Block 改名
        sa.RenameKey(path, P(Branch("Block", new List<object> { KeyLeaf("a") })), "shelter_a");
        Assert.Equal(1, sa.SelectNodes(path, P(
            Branch("Block", new List<object> { KeyLeaf("shelter_a") }))).Hits.Count, "Block 新 key 命中");
        // 定位不到 → 静默；多节点 → 抛
        sa.RenameKey(path, P(Branch("Block", new List<object> { KeyLeaf("不存在") })), "x");   // 不抛
        bool threw = false;
        try
        {
            sa.RenameKey(path, P(Branch("Any", new List<object> { KeyLeaf("shelter_a") }), Branch("Simple", new())), "y");
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Assert.True(threw, "多节点定位抛异常");
    }

    [Test]
    public void LegacyResolverParsesOldFormatWithDeprecationNotice()
    {
        var sa = Build(
            "a = {\n b = { c = k }\n}\n");
        // 旧格式（string + 元组 + int 0 起）→ 解析成功 + 提示废弃
        var legacy = LegacySelectorResolver.ResolveLegacy(
            sa.GetConfig("common/edicts/test.txt")!.RootNodes,
            new List<object> { "a", ("c", "k") });
        Assert.True(legacy.HasErrors, "含废弃提示");
        Assert.True(legacy.Errors.Any(e => e.Message.Contains("已废弃", StringComparison.Ordinal)), "提示已废弃");
        Assert.Equal(1, legacy.Hits.Count, "旧数据仍输出解析结果");
        Assert.Equal("b", legacy.Hits[0].Key, "命中 b 块");
        // 非法旧选择器 → 报错
        var bad = LegacySelectorResolver.ResolveLegacy(
            sa.GetConfig("common/edicts/test.txt")!.RootNodes,
            new List<object> { 3.14 });   // 不支持的类型
        Assert.True(bad.HasErrors, "非法选择器报错");
    }
}
