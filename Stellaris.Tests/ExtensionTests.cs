using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Extension;
using Stellaris.Parser;

namespace Stellaris.Tests;

/// <summary>
/// 拓展工具：条件树求值 + extract/transform/modify/write/generate_yml + deployments 多轮。
/// </summary>
public sealed class ExtensionTests
{
    // ============ 辅助 ============

    private static void Write(string root, string rel, string content)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string Read(string root, string rel)
        => File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    private static JsonElement? Cond(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ============ 标准搜索（枝/叶）JSON 辅助 ============

    /// <summary>match 条件：rule 数组 + check_rule（extract 全树遍历评估，mode 恒 Any）。</summary>
    private static JsonElement? MatchOf(string ruleJson, string checkRule = "And")
        => Cond($"{{\"rule\":[{ruleJson}],\"check_rule\":\"{checkRule}\"}}");

    /// <summary>枝：mode + rule 条件数组（JSON 片段；ruleJson = 条件列表内容，逗号分隔）。</summary>
    private static string BranchJson(string mode, string ruleJson, string checkRule = "And")
        => $"{{\"mode\":\"{mode}\",\"match\":{{\"rule\":[{ruleJson}],\"check_rule\":\"{checkRule}\"}}}}";

    /// <summary>叶：target + keywords（JSON 片段）。</summary>
    private static string LeafJson(string target, params string[] kws)
        => $"{{\"target\":\"{target}\",\"keywords\":[{string.Join(",", kws.Select(k => $"\"{k}\""))}]}}";


    /// <summary>叶：target + keywords + check_rule（keywords 多值按 check_rule 组合）。</summary>
    private static string LeafJsonC(string target, string checkRule, params string[] kws)
        => $"{{\"target\":\"{target}\",\"keywords\":[{string.Join(",", kws.Select(k => $"\"{k}\""))}],\"check_rule\":\"{checkRule}\"}}";

    /// <summary>叶：target + type + keywords（JSON 片段）。</summary>
    private static string LeafJsonT(string target, string type, params string[] kws)
        => $"{{\"target\":\"{target}\",\"keywords\":[{(string.Join(",", kws.Select(k => $"\"{k}\"")))}],\"type\":\"{type}\"}}";
    /// <summary>定位路径（modify/write path / extract from）：枝/叶数组 JSON 片段。</summary>
    private static string PathJson(string json)
        => $"[{json}]";

    private static (StellarisAdapter Adapter, string GameRoot, string ModRoot) Setup(string content)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "smt_ext_" + Guid.NewGuid().ToString("N"));
        var gameRoot = Path.Combine(tmp, "game");
        var modRoot = Path.Combine(tmp, "mod");
        Write(gameRoot, "common/inline_scripts/zones/zone_a.txt", content);
        Directory.CreateDirectory(modRoot);
        var adapter = new StellarisAdapter();
        adapter.AddRoot(gameRoot);   // roots 仅读取源——CLI 写盘根 = _modRoot（Runner 构造），与 roots 无关
        adapter.ScanAll();
        return (adapter, gameRoot, modRoot);
    }

    private static ExtensionRunner Runner(StellarisAdapter adapter, string modRoot)
        => new(adapter, modRoot, NullLogger.Instance);

    // ============ 条件求值（SelectorResolver.NodeMatches 新语法） ============

    /// <summary>rule 数组 JSON 片段 → SelectorResolver rule（List&lt;object&gt;）。</summary>
    private static List<object> RuleOf(string ruleJson)
    {
        using var doc = JsonDocument.Parse("{\"rule\":[" + ruleJson + "]}");
        return ExtensionRunner.ToSelectorPath(doc.RootElement.GetProperty("rule"));
    }

    [Test]
    public void MatchConditionEvaluatesAllAnyNot()
    {
        var (adapter, _, _) = Setup("zone_a = {\n included_building_sets = { entertainment ark }\n hidden = no\n}\n");
        var nodes = adapter.GetAllConfigs().Values.First().RootNodes;
        var zone = nodes.First(n => n.Key == "zone_a");

        // 叶 key 匹配
        var m1 = new SelectResult();
        Assert.True(SelectorResolver.NodeMatches(RuleOf(LeafJson("key", "zone_a")), "And", zone, m1), "key 100% 匹配");
        var m2 = new SelectResult();
        Assert.False(SelectorResolver.NodeMatches(RuleOf(LeafJson("key", "zone_b")), "And", zone, m2), "key 不匹配");

        // has 直接子层字段=值（新规范：rule 里枝 = Children 层存在性）
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(BranchJson("Any", LeafJson("key", "hidden") + "," + LeafJson("value", "no"))), "And", zone, new SelectResult()),
            "内容含 hidden=no 命中");
        Assert.False(SelectorResolver.NodeMatches(
            RuleOf(BranchJson("Any", LeafJson("key", "hidden") + "," + LeafJson("value", "yes"))), "And", zone, new SelectResult()),
            "值不匹配");

        // check_rule=And（缺省）：多条件全真
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJson("key", "zone_a") + "," + BranchJson("Any", LeafJson("key", "hidden") + "," + LeafJson("value", "no"))),
            "And", zone, new SelectResult()), "And 全真");
        Assert.False(SelectorResolver.NodeMatches(
            RuleOf(LeafJson("key", "zone_a") + "," + BranchJson("Any", LeafJson("key", "hidden") + "," + LeafJson("value", "yes"))),
            "And", zone, new SelectResult()), "And 一假则假");

        // check_rule=Or：任一命中
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJson("key", "zone_b") + "," + BranchJson("Any", LeafJson("key", "hidden") + "," + LeafJson("value", "no"))),
            "Or", zone, new SelectResult()), "Or 一真则真");
        Assert.False(SelectorResolver.NodeMatches(
            RuleOf(LeafJson("key", "zone_b") + "," + BranchJson("Any", LeafJson("key", "hidden") + "," + LeafJson("value", "yes"))),
            "Or", zone, new SelectResult()), "Or 全假则假");

        // check_rule=Nor：单条件取反（not 语义）
        Assert.True(SelectorResolver.NodeMatches(RuleOf(LeafJson("key", "zone_b")), "Nor", zone, new SelectResult()), "Nor 取反命中");
        Assert.False(SelectorResolver.NodeMatches(RuleOf(LeafJson("key", "zone_a")), "Nor", zone, new SelectResult()), "Nor 取反不命中");

        // 空条件 = 匹配全部
        Assert.True(SelectorResolver.NodeMatches(new List<object>(), "And", zone, new SelectResult()), "空条件匹配全部");
    }

    [Test]
    public void ExtractDirUsesDirectoryBoundary()
    {
        // extract 的 dir（文件目录前缀）用目录边界：同前缀非子目录不匹配
        var tmp = Path.Combine(Path.GetTempPath(), "smt_ext_" + Guid.NewGuid().ToString("N"));
        var gameRoot = Path.Combine(tmp, "game");
        var modRoot = Path.Combine(tmp, "mod");
        Write(gameRoot, "common/strategic_resources/00.txt", "sr_zro = {\n cost = 10\n}\n");
        Write(gameRoot, "common/strategic_resources_backup/x.txt", "sr_backup = {\n cost = 1\n}\n");
        Write(gameRoot, "common/strategic_resources2/x.txt", "sr_two = {\n cost = 2\n}\n");
        var adapter = new StellarisAdapter();
        adapter.AddRoot(gameRoot);
        adapter.ScanAll();
        var runner = Runner(adapter, modRoot);

        // dir = common/strategic_resources：只收该目录下（不含同前缀兄弟目录）
        var plan = PlanWith(new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/strategic_resources" });
        var state = RunSingleDeployment(runner, plan);
        Assert.Equal(1, state.Values.Count, "目录边界：只收 strategic_resources 下");
        Assert.True(state.Values.Contains("sr_zro"), "sr_zro 在该目录");
        Assert.False(state.Values.Contains("sr_backup"), "同前缀兄弟目录不匹配");
        Assert.False(state.Values.Contains("sr_two"), "同前缀兄弟目录 2 不匹配");
        // dir 缺省 = 全部文件
        var planAll = PlanWith(new StepConfig { Rule = "extract", Mode = "keys" });
        var stateAll = RunSingleDeployment(runner, planAll);
        Assert.Equal(3, stateAll.Values.Count, "dir 缺省扫描全部");
    }

    // ============ extract ============

    [Test]
    public void ExtractValuesModeCollectsListValuesDedupOrdered()
    {
        // 文件 1 场景：included_building_sets 的 list 值去重保序
        var (adapter, _, modRoot) = Setup(
            "zone_a = {\n included_building_sets = { entertainment ark industrial }\n}\n" +
            "zone_b = {\n included_building_sets = { entertainment mining }\n}\n");
        var plan = new ExtensionPlan
        {
            Roots = new System.Collections.Generic.List<string>(),
            Deployments =
            {
                new Deployment { Steps =
                {
                    new StepConfig { Rule = "extract", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"included_building_sets\"]}") }
                } }
            }
        };
        var runner = Runner(adapter, modRoot);
        var state = RunSingleDeployment(runner, plan);
        Assert.Equal(4, state.Values.Count, "去重后 4 个值");
        Assert.Equal("entertainment", state.Values[0], "保序第 1 个");
        Assert.Equal("ark", state.Values[1], "保序第 2 个");
        Assert.Equal("industrial", state.Values[2], "保序第 3 个");
        Assert.Equal("mining", state.Values[3], "保序第 4 个");
    }

    [Test]
    public void ExtractKeysModeCollectsTopLevelBlockKeys()
    {
        // 文件 2 场景：顶层块 key（资源种类）
        var (adapter, _, modRoot) = Setup(
            "sr_zro = {\n cost = 10\n}\nsr_alpha = {\n cost = 5\n}\nsr_zro = {\n cost = 20\n}\n");
        var plan = PlanWith(new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones" });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        Assert.Equal(2, state.Values.Count, "顶层 key 去重为 2");
        Assert.Equal("sr_zro", state.Values[0], "保序");
        Assert.Equal("sr_alpha", state.Values[1], "保序");
    }

    [Test]
    public void ExtractNodesModeWithMatchTree()
    {
        var (adapter, _, modRoot) = Setup(
            "component_template = {\n type = utility\n hidden = no\n}\n" +
            "component_template = {\n type = weapon\n hidden = no\n}\n" +
            "component_template = {\n type = utility\n hidden = yes\n}\n");
        var step = new StepConfig
        {
            Rule = "extract",
            Mode = "nodes",
            Dir = "common/inline_scripts/zones",
            // 条件（新语法）：key=component_template AND 内容含 hidden=no AND (type=utility OR type=support)
            Match = MatchOf(
                LeafJson("key", "component_template") + "," +
                BranchJson("Any", LeafJson("key", "hidden") + "," + LeafJson("value", "no")) + "," +
                BranchJson("Any",
                    LeafJson("key", "type") + "," +
                    LeafJsonC("value", "Or", "utility", "support")))
        };
        var state = RunSingleDeployment(Runner(adapter, modRoot), PlanWith(step));
        Assert.Equal(1, state.Nodes.Count, "条件命中 1 个节点（utility 且非 hidden）");
        Assert.Equal("component_template", state.Nodes[0].Key, "命中节点 key");
    }

    [Test]
    public void EngineStrategicResourceOutputsMergedKeys()
    {
        // 两个 root 撞同一路径：同 key 合并 + 各自独有 → 引擎合并语义（非通用提取）
        var tmp = Path.Combine(Path.GetTempPath(), "smt_ext_" + Guid.NewGuid().ToString("N"));
        var baseRoot = Path.Combine(tmp, "base");
        var modRoot = Path.Combine(tmp, "mod");
        Write(baseRoot, "common/strategic_resources/00_strategic_resources.txt",
            "sr_zro = {\n cost = 10\n}\nsr_base_only = {\n cost = 5\n}\n");
        Write(modRoot, "common/strategic_resources/00_strategic_resources.txt",
            "sr_zro = {\n cost = 20\n}\nsr_mod_only = {\n cost = 7\n}\n");
        var adapter = new StellarisAdapter();
        adapter.AddRoot(baseRoot);
        adapter.AddRoot(modRoot);
        adapter.ScanAll();

        var plan = PlanWith(new StepConfig { Rule = "extract", Engine = "strategic_resource" });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        Assert.Equal(3, state.Values.Count, "引擎合并：同顶层 key 一条 + 各自独有 = 3 个 key");
        Assert.True(state.Values.Contains("sr_zro"), "sr_zro 合并为一条");
        Assert.True(state.Values.Contains("sr_base_only"), "base 独有");
        Assert.True(state.Values.Contains("sr_mod_only"), "mod 独有");
    }

    [Test]
    public void KeysModeFromLimitsToPath()
    {
        // 只收集 country.buildings 的直接子 block key（不含 country 下其他层级）
        var (adapter, _, modRoot) = Setup(
            "country = {\n" +
            " buildings = {\n  a = { type = x }\n  b = { type = y }\n }\n" +
            " other = { type = z }\n" +
            "}\n");
        var plan = PlanWith(new StepConfig
        {
            Rule = "extract",
            Mode = "keys",
            From = Cond("[{\"mode\":\"Block\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"country\"]}],\"check_rule\":\"And\"}},{\"mode\":\"Block\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"buildings\"]}],\"check_rule\":\"And\"}}]"),
            Match = MatchOf("{\"mode\":\"Any\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"type\"]}]}}")
        });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        Assert.Equal(2, state.Values.Count, "只收 buildings 直接子层：a、b");
        Assert.True(state.Values.Contains("a"), "含 a");
        Assert.True(state.Values.Contains("b"), "含 b");
        Assert.False(state.Values.Contains("other"), "不含 other（路径外）");
    }

    [Test]
    public void KeysModeAnyDepthCollectsNestedBlocks()
    {
        // 任意层级 + has 省略 value（只查字段存在）
        var (adapter, _, modRoot) = Setup(
            "country = {\n" +
            " buildings = {\n  a = { type = x }\n }\n" +
            " b = { type = y }\n" +
            "}\n");
        var plan = PlanWith(new StepConfig
        {
            Rule = "extract",
            Mode = "keys",
            Depth = "all",
            Match = MatchOf("{\"mode\":\"Any\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"type\"]}]}}")
        });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        // 新规范语义（rule 里枝 = 直接子层存在性）：a、b 直接含 type；buildings/country 不含（type 在孙层）
        Assert.Equal(2, state.Values.Count, "任意层级 + 直接子层含 type：a、b");
        Assert.True(state.Values.Contains("a"), "嵌套层 a");
        Assert.True(state.Values.Contains("b"), "浅层 b");
    }

    [Test]
    public void HasLeafWithoutValueChecksExistence()
    {
        // has 省略 value：块内有 type 字段（值任意）→ 收集；没有 → 不收集
        var (adapter, _, modRoot) = Setup(
            "zone_a = {\n type = utility\n}\n" +
            "zone_b = {\n cost = 5\n}\n");
        var plan = PlanWith(new StepConfig
        {
            Rule = "extract",
            Mode = "keys",
            Match = MatchOf("{\"mode\":\"Any\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"type\"]}]}}")
        });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        Assert.Equal(1, state.Values.Count, "只收集含 type 字段的块");
        Assert.Equal("zone_a", state.Values[0], "zone_a 含 type");
    }

    [Test]
    public void ValuesModeCollectsSimpleValuesOnly()
    {
        // 收紧语义：values 仅 Simple/List 有值——Simple 取节点值；Block 无值跳过
        var (adapter, _, modRoot) = Setup("zone_a = {\n type = utility\n}\n");
        // Simple 匹配 → 收集其值
        var planSimple = PlanWith(new StepConfig { Rule = "extract", Mode = "values", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"type\"]}") });
        var state1 = RunSingleDeployment(Runner(adapter, modRoot), planSimple);
        Assert.Equal(1, state1.Values.Count, "Simple 匹配收集值");
        Assert.Equal("utility", state1.Values[0], "type 的值");
        // Block 匹配 → 无值跳过（不收集子键）
        var planBlock = PlanWith(new StepConfig { Rule = "extract", Mode = "values", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"zone_a\"]}") });
        var state2 = RunSingleDeployment(Runner(adapter, modRoot), planBlock);
        Assert.Equal(0, state2.Values.Count, "Block 无值——跳过");
    }

    [Test]
    public void ParentModeCollectsParentNodes()
    {
        // parent 模式：匹配 type 节点 → 父 block 进 Nodes（供后续 modify/serialize）
        var (adapter, _, modRoot) = Setup(
            "zone_a = {\n type = utility\n}\n" +
            "zone_b = {\n cost = 5\n}\n");
        var plan = PlanWith(new StepConfig { Rule = "extract", Mode = "parent", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"type\"]}") });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        Assert.Equal(1, state.Nodes.Count, "1 个父节点");
        Assert.Equal("zone_a", state.Nodes[0].Key, "type 的父块是 zone_a");
    }

    [Test]
    public void ExtractNodesClonesSoModifyDoesNotPollute()
    {
        // 深拷贝验证：extract nodes + modify 改副本，SA 共享 AST 不被污染
        var (adapter, _, modRoot) = Setup("shelter = {\n cost = 10\n}\n");
        var runner = Runner(adapter, modRoot);
        // 第一轮：extract + modify set cost=99
        var plan1 = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"shelter\"]}") },
            new StepConfig { Rule = "modify", Path = Cond("[{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"cost\"]}],\"check_rule\":\"And\"}}]"), Op = "set", Value = "99" });
        var state1 = RunSingleDeployment(runner, plan1);
        Assert.True(SerializationHelper.Serialize(state1.Nodes).Contains("cost = 99", StringComparison.Ordinal),
            "第一轮副本已改为 99");
        // 第二轮：同 adapter 重新 extract——SA 里仍是原始 10（未被污染）
        var plan2 = PlanWith(new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"shelter\"]}") });
        var state2 = RunSingleDeployment(runner, plan2);
        Assert.True(SerializationHelper.Serialize(state2.Nodes).Contains("cost = 10", StringComparison.Ordinal),
            "SA 未被污染——仍为 10");
    }

    [Test]
    public void ModifyMulMultipliesNumericValue()
    {
        // 翻倍：upkeep.energy 0.22 × 2 → 0.44
        var (adapter, _, modRoot) = Setup("shelter = {\n upkeep = {\n  energy = 0.22\n }\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"shelter\"]}") },
            new StepConfig { Rule = "modify", Path = Cond("[{\"mode\":\"Block\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"upkeep\"]}],\"check_rule\":\"And\"}},{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"energy\"]}],\"check_rule\":\"And\"}}]"), Op = "mul", Value = "2" });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        var serialized = SerializationHelper.Serialize(state.Nodes);
        Assert.True(serialized.Contains("energy = 0.44", StringComparison.Ordinal), "0.22 × 2 = 0.44");
        Assert.False(serialized.Contains("energy = 0.22", StringComparison.Ordinal), "旧值不输出");
    }

    [Test]
    public void ModifyResolveExpandsConstant()
    {
        // 常量展开：power = @corvette_reactor_power_4 → 340（SA 求值后清 RawText 输出数值）
        var (adapter, _, modRoot) = Setup(
            "@corvette_reactor_power_4 = 340\n" +
            "shelter = {\n power = @corvette_reactor_power_4\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"shelter\"]}") },
            new StepConfig { Rule = "modify", Path = Cond("[{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"power\"]}],\"check_rule\":\"And\"}}]"), Op = "resolve" });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        var serialized = SerializationHelper.Serialize(state.Nodes);
        Assert.True(serialized.Contains("power = 340", StringComparison.Ordinal), "常量展开为数值 340");
        Assert.False(serialized.Contains("@corvette_reactor_power_4", StringComparison.Ordinal), "@ 原文不输出");
    }

    [Test]
    public void DeploymentRootsOverrideGlobal()
    {
        // 全局 roots = rootA；deployment 2 声明 roots = [rootB]（完全覆盖——只读 B）
        var tmp = Path.Combine(Path.GetTempPath(), "smt_ext_" + Guid.NewGuid().ToString("N"));
        var rootA = Path.Combine(tmp, "a");
        var rootB = Path.Combine(tmp, "b");
        var modRoot = Path.Combine(tmp, "mod");
        Write(rootA, "common/data/x.txt", "only_a = {\n cost = 1\n}\n");
        Write(rootB, "common/data/x.txt", "only_b = {\n cost = 2\n}\n");
        var globalAdapter = new StellarisAdapter();
        globalAdapter.AddRoot(rootA);
        globalAdapter.ScanAll();
        var runner = Runner(globalAdapter, modRoot);

        // deployment 1：缺省 roots → 继承全局（读 A，找到 only_a）
        var dep1 = new Deployment { Steps = { new StepConfig { Rule = "extract", Mode = "keys", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"only_a\"]}") } } };
        var state1 = runner.ExecuteDeployment(dep1);
        Assert.Equal(1, state1.Values.Count, "继承全局 roots：找到 only_a");
        Assert.True(state1.Values.Contains("only_a"), "only_a 在全局 A 中");

        // deployment 2：声明 roots=[B] → 完全覆盖（读 B，找到 only_b，找不到 only_a）
        var dep2 = new Deployment
        {
            Roots = new System.Collections.Generic.List<string> { rootB },
            Steps = { new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/data" } }
        };
        var state2 = runner.ExecuteDeployment(dep2);
        Assert.Equal(1, state2.Values.Count, "覆盖 roots：只读 B");
        Assert.True(state2.Values.Contains("only_b"), "only_b 在 B 中");
        Assert.False(state2.Values.Contains("only_a"), "覆盖语义——only_a（仅 A）不可见");
    }

    [Test]
    public void InlineScriptThreeFormsParseAndSerializeFaithfully()
    {
        // 三种合法 inline_script 形式——按 key 判定都要展开；
        // 脚本文件不存在 → 展开失败 → 原节点全部保留（序列化保真）
        var content =
            "inline_script = \"shelter_all_original_building_set\"\n" +             // 形式1：simple 直接引用
            "inline_script = {\n script = shelter_all_original_resources\n VALUE = 100000000\n}\n" +  // 形式2：带参数 block
            "inline_script = {\n script = shelter_all_original_resources\n}\n";      // 形式3：无参数 block（1 的复杂写法）
        var (adapter, _, _) = Setup(content);
        var nodes = adapter.GetAllConfigs().Values.First().RootNodes;
        var serialized = SerializationHelper.Serialize(nodes);
        Assert.True(serialized.Contains("inline_script = \"shelter_all_original_building_set\"", StringComparison.Ordinal),
            "形式1 simple 直接引用（脚本不存在 → 保留原样）");
        Assert.True(serialized.Contains("script = shelter_all_original_resources", StringComparison.Ordinal),
            "形式2/3 的 script 字段保留");
        Assert.True(serialized.Contains("VALUE = 100000000", StringComparison.Ordinal),
            "形式2 的参数保留");
        Assert.Equal(3, CountOccurrences(serialized, "inline_script"), "三个引用节点都在");
    }

    [Test]
    public void InlineScriptBlockExpandsWhenScriptExists()
    {
        // block 形式 + 脚本文件存在 → EnableInlineScript=true（默认）扫描时展开成内容
        var tmp = Path.Combine(Path.GetTempPath(), "smt_ext_" + Guid.NewGuid().ToString("N"));
        var gameRoot = Path.Combine(tmp, "game");
        var modRoot = Path.Combine(tmp, "mod");
        Write(gameRoot, "common/inline_scripts/shelter_test.txt", "alloys = 68\n");
        Write(gameRoot, "common/data/z.txt", "inline_script = { script = shelter_test\n VALUE = 5\n}\n");
        var adapter = new StellarisAdapter();
        adapter.AddRoot(gameRoot);
        adapter.ScanAll();
        var nodes = adapter.GetAllConfigs()["common/data/z.txt"].RootNodes;
        var serialized = SerializationHelper.Serialize(nodes);
        Assert.False(serialized.Contains("inline_script", StringComparison.Ordinal), "block 引用被展开替换");
        Assert.True(serialized.Contains("alloys = 68", StringComparison.Ordinal), "展开出脚本内容");
    }

    [Test]
    public void InlineScriptSimpleFormExpandsWhenScriptExists()
    {
        // simple 形式（inline_script = "xxx"）脚本存在 → 也展开（按 key 判定，与游戏一致）
        // 脚本内容为裸值清单（每行一个值）——Parser 已支持顶层裸值行
        var tmp = Path.Combine(Path.GetTempPath(), "smt_ext_" + Guid.NewGuid().ToString("N"));
        var gameRoot = Path.Combine(tmp, "game");
        var modRoot = Path.Combine(tmp, "mod");
        Write(gameRoot, "common/inline_scripts/shelter_buildings.txt", "entertainment\nindustrial\n");
        Write(gameRoot, "common/data/z.txt", "inline_script = \"shelter_buildings\"\n");
        var adapter = new StellarisAdapter();
        adapter.AddRoot(gameRoot);
        adapter.ScanAll();
        var nodes = adapter.GetAllConfigs()["common/data/z.txt"].RootNodes;
        var serialized = SerializationHelper.Serialize(nodes);
        Assert.False(serialized.Contains("inline_script", StringComparison.Ordinal), "simple 引用也被展开");
        Assert.True(serialized.Contains("entertainment", StringComparison.Ordinal), "展开出裸值 1");
        Assert.True(serialized.Contains("industrial", StringComparison.Ordinal), "展开出裸值 2");
    }

    [Test]
    public void BareValueListFileParsesAndSerializes()
    {
        // 裸值清单文件（每行一个值，如 shelter_all_building_set.txt）解析 + 序列化保真
        var (adapter, _, _) = Setup("entertainment\nindustrial\nmining\n");
        var nodes = adapter.GetAllConfigs().Values.First().RootNodes;
        var serialized = SerializationHelper.Serialize(nodes);
        Assert.True(serialized.Contains("entertainment", StringComparison.Ordinal), "裸值 1");
        Assert.True(serialized.Contains("industrial", StringComparison.Ordinal), "裸值 2");
        Assert.True(serialized.Contains("mining", StringComparison.Ordinal), "裸值 3");
        Assert.Equal(3, nodes.Count, "三个裸值节点（Key=null）");
        Assert.True(nodes.All(n => n.Type == NodeType.Simple && n.Key == null), "全部为裸值 Simple");
    }

    [Test]
    public void ClearRuleEmptiesChannelsBetweenGroups()
    {
        // extract 追加语义：clear 前两组结果会混在一起；clear 后第二组干净
        var (adapter, _, modRoot) = Setup(
            "sr_zro = {\n cost = 10\n}\nsr_alpha = {\n cost = 5\n}\n");
        // 组 1：extract keys（sr_zro、sr_alpha）→ 不 clear → 组 2 extract keys（只有 sr_zro）会追加
        var planNoClear = PlanWith(
            new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones" },
            new StepConfig { Rule = "extract", Mode = "keys", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"sr_zro\"]}") });
        var stateNoClear = RunSingleDeployment(Runner(adapter, modRoot), planNoClear);
        Assert.Equal(3, stateNoClear.Values.Count, "无 clear：跨 extract 追加不去重（sr_zro,sr_alpha,sr_zro）");

        // 组 1 → clear → 组 2：第二组只含自己的结果
        var planClear = PlanWith(
            new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones" },
            new StepConfig { Rule = "clear" },
            new StepConfig { Rule = "extract", Mode = "keys", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"sr_zro\"]}") });
        var stateClear = RunSingleDeployment(Runner(adapter, modRoot), planClear);
        Assert.Equal(1, stateClear.Values.Count, "clear 后只有第二组结果");
        Assert.True(stateClear.Values.Contains("sr_zro"), "第二组：sr_zro");
        Assert.False(stateClear.Values.Contains("sr_alpha"), "第一组已被清空");
    }

    [Test]
    public void ExpressionEvaluatorArithmeticAndArrayIndex()
    {
        var vars = new System.Collections.Generic.Dictionary<string, long> { ["n"] = 1999 };
        // 2000 飞升槽坐标公式：x=[10,68,126,184,39,97,155,213][n%8]，y=(n%8<4?5:54)+98*(n/8)
        Assert.Equal(213, TemplateMath.Evaluate("[10,68,126,184,39,97,155,213][n%8]", vars), "x 公式");
        Assert.Equal(24456, TemplateMath.Evaluate("(n%8<4?5:54)+98*(n/8)", vars), "y 公式（ap_1999）");
        Assert.Equal(5, TemplateMath.Evaluate("(0%8<4?5:54)+98*(0/8)", vars), "y 公式（ap_0）");
        // 基本算术 / 三元 / 比较
        Assert.Equal(340, TemplateMath.Evaluate("68*5"), "乘");
        Assert.Equal(3, TemplateMath.Evaluate("10/3"), "整除");
        Assert.Equal(1, TemplateMath.Evaluate("7%2"), "取模");
        Assert.Equal(7, TemplateMath.Evaluate("(2+5)*1"), "括号");
        Assert.Equal(100, TemplateMath.Evaluate("n<2000 ? 100 : 200", vars), "三元真");
        Assert.Equal(5, TemplateMath.Evaluate("n==1999 ? 5 : 9", vars), "比较");
    }

    [Test]
    public void ForEachNumericRangeBindsInteger()
    {
        // 数值范围 2..5：每轮 add 一个节点（简单 key=slot{n} value={expr:n*10}）
        var (adapter, _, modRoot) = Setup("root = {\n existing = 1\n}\n");
        var plan = PlanWith(new StepConfig
        {
            Rule = "foreach",
            Over = new System.Collections.Generic.List<string> { "2..5" },
            As = "n",
            Steps = new System.Collections.Generic.List<StepConfig>
            {
                new StepConfig
                {
                    Rule = "add",
                    File = "common/inline_scripts/zones/zone_a.txt",
                    Path = System.Text.Json.JsonSerializer.SerializeToElement(P(Branch("Block", new System.Collections.Generic.List<object> { Leaf("key", "root") }))),
                    Text = "slot_{n} = {expr:n*10}"
                }
            }
        });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        var hits = adapter.SelectNodes("common/inline_scripts/zones/zone_a.txt",
            P(Branch("Block", new System.Collections.Generic.List<object> { Leaf("key", "root") })));
        var root = hits.Hits[0];
        Assert.Equal(4, root.Children.Count(n => n.Key.StartsWith("slot_", StringComparison.Ordinal)), "4 个 slot");
        Assert.Equal("30", root.Children.First(n => n.Key == "slot_3").Value?.ToString(), "slot_3");
        Assert.Equal("50", root.Children.First(n => n.Key == "slot_5").Value?.ToString(), "slot_5");
        // 落盘（WriteFile roots[-1]）
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(modRoot, "common/inline_scripts/zones/zone_a.txt")), "add 已落盘");
    }

    [Test]
    public void AddRelativeBeforeAfterInsertsSibling()
    {
        var (adapter, _, modRoot) = Setup(
            "top = {\n a = { x = 1 }\n b = { y = 2 }\n}\n");
        // Before：a 前插 z
        RunSingleDeployment(Runner(adapter, modRoot), PlanWith(new StepConfig
        {
            Rule = "add",
            File = "common/inline_scripts/zones/zone_a.txt",
            Path = System.Text.Json.JsonSerializer.SerializeToElement(P(
                Branch("Block", new System.Collections.Generic.List<object> { Leaf("key", "top") }),
                Branch("Block", new System.Collections.Generic.List<object> { Leaf("key", "a") }))),
            Position = "Before",
            Text = "z = 9"
        }));
        var root = adapter.SelectNodes("common/inline_scripts/zones/zone_a.txt",
            P(Branch("Block", new System.Collections.Generic.List<object> { Leaf("key", "top") }))).Hits[0];
        System.Console.WriteLine("DEBUG AddRelative children=" + root.Children.Count + " keys=" + string.Join(",", root.Children.Select(c => c.Key)));
        Assert.Equal("z", root.Children[0].Key, "Before：z 在 a 前");
        Assert.Equal("a", root.Children[1].Key, "a 紧跟");
    }

    [Test]
    public void ExtractAsAndSetSubtractUnionIntersect()
    {
        // extract as 命名集合 + set 布尔运算（subtract/union/intersect）——结果替换 values 通道
        var (adapter, _, modRoot) = Setup("a = {}\nb = {}\nc = {}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones", As = "all" },
            new StepConfig { Rule = "clear" },
            new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones",
                Match = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["rule"] = new List<object> { new Dictionary<string, object> { ["target"] = "key", ["keywords"] = new List<object> { "b" } } }
                }),
                As = "exclude" },
            // subtract：all − exclude → [a, c]
            new StepConfig { Rule = "set", Op = "subtract", Left = J("all"), Right = J("exclude") },
            new StepConfig { Rule = "write", File = "common/inline_scripts/shelter_sub.txt", Format = "sub_{key}" },
            // union：all ∪ exclude → [a, b, c]
            new StepConfig { Rule = "set", Op = "union", Left = J("all"), Right = J("exclude") },
            new StepConfig { Rule = "write", File = "common/inline_scripts/shelter_union.txt", Format = "uni_{key}" },
            // intersect：all ∩ exclude → [b]
            new StepConfig { Rule = "set", Op = "intersect", Left = J("all"), Right = J("exclude") },
            new StepConfig { Rule = "write", File = "common/inline_scripts/shelter_inter.txt", Format = "int_{key}" });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        string baseDir = System.IO.Path.Combine(modRoot, "common/inline_scripts");
        var sub = System.IO.File.ReadAllLines(System.IO.Path.Combine(baseDir, "shelter_sub.txt"));
        var uni = System.IO.File.ReadAllLines(System.IO.Path.Combine(baseDir, "shelter_union.txt"));
        var inter = System.IO.File.ReadAllLines(System.IO.Path.Combine(baseDir, "shelter_inter.txt"));
        Assert.True(sub.Contains("sub_a") && sub.Contains("sub_c") && !sub.Contains("sub_b"), "差集 [a,c]");
        Assert.True(uni.Contains("uni_a") && uni.Contains("uni_b") && uni.Contains("uni_c"), "并集 [a,b,c]");
        Assert.True(inter.Contains("int_b") && !inter.Contains("int_a") && !inter.Contains("int_c"), "交集 [b]");
    }

    [Test]
    public void SetLiteralArrayOperand()
    {
        // set 的 left/right 支持字面量数组：all − ["b"] → [a, c]
        var (adapter, _, modRoot) = Setup("a = {}\nb = {}\nc = {}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones", As = "all" },
            new StepConfig
            {
                Rule = "set",
                Op = "subtract",
                Left = J("all"),
                Right = System.Text.Json.JsonSerializer.SerializeToElement(new System.Collections.Generic.List<string> { "b" })
            },
            new StepConfig { Rule = "write", File = "common/inline_scripts/shelter_lit.txt", Format = "lit_{key}" });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        var lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(modRoot, "common/inline_scripts/shelter_lit.txt"));
        Assert.True(lines.Contains("lit_a") && lines.Contains("lit_c") && !lines.Contains("lit_b"), "字面量排除 [a,c]");
    }

    public void ForEachOverValuesRefersToExtract()
    {
        // extract 收集值 → foreach over "values" 引用该结果逐轮处理（每轮绑定当前值）
        var (adapter, _, modRoot) = Setup("a = { x = 1 }\nb = { x = 2 }\nc = { x = 3 }\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones" },
            new StepConfig
            {
                Rule = "foreach",
                Over = new System.Collections.Generic.List<string> { "values" },
                As = "k",
                Steps = new System.Collections.Generic.List<StepConfig>
                {
                    new StepConfig
                    {
                        Rule = "extract",
                        Mode = "keys",
                        Dir = "common/inline_scripts/zones",
                        Match = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object>
                        {
                            ["rule"] = new List<object> { new Dictionary<string, object> { ["target"] = "key", ["keywords"] = new List<object> { "{k}" } } }
                        })
                    },
                    new StepConfig
                    {
                        Rule = "write",
                        File = "common/inline_scripts/shelter_keys.txt",
                        Format = "key_{k} = yes",
                        Append = true
                    }
                }
            });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        string outFile = System.IO.Path.Combine(modRoot, "common/inline_scripts/shelter_keys.txt");
        var content = System.IO.File.ReadAllText(outFile);
        Assert.True(content.Contains("key_a = yes", System.StringComparison.Ordinal), "key_a");
        Assert.True(content.Contains("key_b = yes", System.StringComparison.Ordinal), "key_b");
        Assert.True(content.Contains("key_c = yes", System.StringComparison.Ordinal), "key_c");
    }

    [Test]
    public void WriteAppendModeAccumulates()
    {
        // append: true → 两次 write 同一文件，内容拼接（第二次接在第一次后）
        var (adapter, _, modRoot) = Setup("a = {}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones" },
            new StepConfig { Rule = "write", File = "common/inline_scripts/shelter_append.txt", Format = "first = {key}" },
            new StepConfig { Rule = "clear" },
            new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones" },
            new StepConfig { Rule = "write", File = "common/inline_scripts/shelter_append.txt", Format = "second = {key}", Append = true });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        string outFile = System.IO.Path.Combine(modRoot, "common/inline_scripts/shelter_append.txt");
        var content = System.IO.File.ReadAllText(outFile);
        Assert.True(content.Contains("first = a", System.StringComparison.Ordinal), "第一段");
        Assert.True(content.Contains("second = a", System.StringComparison.Ordinal), "第二段（追加）");
        Assert.True(content.IndexOf("first", System.StringComparison.Ordinal) < content.IndexOf("second", System.StringComparison.Ordinal), "顺序：first 在 second 前");
    }

    [Test]
    public void WriteNodesNoPathTakesSelfKey()
    {
        // write source=nodes 且无 path → 取节点自身 key（资源块 energy → "energy = $VALUE$"）
        var (adapter, _, modRoot) = Setup("energy = {\n tradable = yes\n category = basic\n}\nminerals = {\n category = strategic\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Dir = "common/inline_scripts/zones" },
            new StepConfig
            {
                Rule = "write",
                Source = "nodes",
                Target = "key",
                File = "common/inline_scripts/shelter_all_resources.txt",
                Format = "{key} = $VALUE$"
            });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        string outFile = System.IO.Path.Combine(modRoot, "common/inline_scripts/shelter_all_resources.txt");
        var lines = System.IO.File.ReadAllLines(outFile);
        Assert.True(lines.Contains("energy = $VALUE$", System.StringComparer.Ordinal), "energy = $VALUE$");
        Assert.True(lines.Contains("minerals = $VALUE$", System.StringComparer.Ordinal), "minerals = $VALUE$");
        Assert.Equal(2, lines.Count(l => l.EndsWith(" = $VALUE$", System.StringComparison.Ordinal)), "全部为 key = $VALUE$");
    }

    public void AddExistingReplacesInPlaceAndPathExpr()
    {
        // 阶段 1：Append + existing（按 name=ap_{n} 命中）→ 原地替换保留位置（8 槽→新坐标）
        // 阶段 2：After 定位 ap_{expr:n-1}（path 模板）→ 顺序追加新槽（1992 槽）
        var (adapter, _, modRoot) = Setup(
            "gui = {\n positionType = { name = \"ap_0\" position = { x = 15 y = 50 } }\n positionType = { name = \"ap_1\" position = { x = 73 y = 50 } }\n }\n");
        var existing = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["rule"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["mode"] = "Any",
                    ["match"] = new Dictionary<string, object>
                    {
                        ["rule"] = new List<object>
                        {
                            new Dictionary<string, object> { ["target"] = "key", ["keywords"] = new List<object> { "name" } },
                            new Dictionary<string, object> { ["target"] = "value", ["keywords"] = new List<object> { "ap_{n}" } }
                        }
                    }
                }
            }
        });
        var guiPath = System.Text.Json.JsonSerializer.SerializeToElement(P(
            Branch("Block", new System.Collections.Generic.List<object> { Leaf("key", "gui") })));
        // 阶段 1：n=0/1 existing 命中 → 原地替换（保留第 1/2 位）
        RunSingleDeployment(Runner(adapter, modRoot), PlanWith(new StepConfig
        {
            Rule = "foreach",
            Over = new System.Collections.Generic.List<string> { "0..1" },
            As = "n",
            Steps = new System.Collections.Generic.List<StepConfig>
            {
                new StepConfig
                {
                    Rule = "add",
                    File = "common/inline_scripts/zones/zone_a.txt",
                    Path = guiPath,
                    Existing = existing,
                    Text = "positionType = { name = \"ap_{n}\" position = { x = {expr:10+58*(n%4)} y = {expr:5} } }"
                }
            }
        }));
        // 阶段 2：n=2/3 After ap_{expr:n-1} → 追加（顺序）
        RunSingleDeployment(Runner(adapter, modRoot), PlanWith(new StepConfig
        {
            Rule = "foreach",
            Over = new System.Collections.Generic.List<string> { "2..3" },
            As = "n",
            Steps = new System.Collections.Generic.List<StepConfig>
            {
                new StepConfig
                {
                    Rule = "add",
                    File = "common/inline_scripts/zones/zone_a.txt",
                    Path = System.Text.Json.JsonSerializer.SerializeToElement(P(
                        Branch("Block", new System.Collections.Generic.List<object> { Leaf("key", "gui") }),
                        Branch("Block", new System.Collections.Generic.List<object>
                        {
                            Branch("Any", new System.Collections.Generic.List<object>
                            {
                                Leaf("key", "name"), Leaf("value", "ap_{expr:n-1}")
                            })
                        }))),
                    Position = "After",
                    Text = "positionType = { name = \"ap_{n}\" position = { x = {expr:10+58*(n%4)} y = {expr:5} } }"
                }
            }
        }));
        var gui = adapter.SelectNodes("common/inline_scripts/zones/zone_a.txt",
            P(Branch("Block", new System.Collections.Generic.List<object> { Leaf("key", "gui") }))).Hits[0];
        Assert.Equal(4, gui.Children.Count, "4 个槽");
        Assert.Equal("ap_0", gui.Children[0].Children.First(c => c.Key == "name").Value?.ToString(), "第 1 位 ap_0");
        Assert.Equal("10", gui.Children[0].Children.First(c => c.Key == "position").Children.First(c => c.Key == "x").Value?.ToString(), "ap_0 替换为新坐标 x=10");
        Assert.Equal("ap_1", gui.Children[1].Children.First(c => c.Key == "name").Value?.ToString(), "第 2 位 ap_1");
        Assert.Equal("68", gui.Children[1].Children.First(c => c.Key == "position").Children.First(c => c.Key == "x").Value?.ToString(), "ap_1 新坐标 x=68");
        Assert.Equal("ap_2", gui.Children[2].Children.First(c => c.Key == "name").Value?.ToString(), "第 3 位 ap_2（After ap_1 追加）");
        Assert.Equal("ap_3", gui.Children[3].Children.First(c => c.Key == "name").Value?.ToString(), "第 4 位 ap_3（After ap_2 追加）");
    }

    public void ForEachIteratesValuesWithBinding()
    {
        // 科技场景：3 组几乎一样的 extract→write，只有 area 值不同——foreach 合并成一组
        var (adapter, _, modRoot) = Setup(
            "tech_physics_1 = {\n area = physics\n}\n" +
            "tech_engineering_1 = {\n area = engineering\n}\n" +
            "tech_society_1 = {\n area = society\n}\n" +
            "tech_weapon = {\n area = weapons\n}\n");
        var plan = PlanWith(new StepConfig
        {
            Rule = "foreach",
            Over = new System.Collections.Generic.List<string> { "physics", "engineering", "society" },
            As = "area",
            Steps = new System.Collections.Generic.List<StepConfig>
            {
                new StepConfig
                {
                    Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones",
                    Match = MatchOf(BranchJson("Any",
                        LeafJson("key", "area") + "," + LeafJson("value", "{area}")))
                },
                new StepConfig
                {
                    Rule = "write",
                    File = "common/inline_scripts/shelter_give_{area}_technology.txt",
                    Format = "give_technology = { tech = {key} }"
                }
            }
        });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        // 每轮独立：三个文件各自只含对应 area 的科技
        var physics = Read(modRoot, "common/inline_scripts/shelter_give_physics_technology.txt");
        Assert.True(physics.Contains("tech_physics_1", StringComparison.Ordinal), "physics 文件含 physics 科技");
        Assert.False(physics.Contains("tech_weapon", StringComparison.Ordinal), "physics 文件不含 weapons");
        var engineering = Read(modRoot, "common/inline_scripts/shelter_give_engineering_technology.txt");
        Assert.True(engineering.Contains("tech_engineering_1", StringComparison.Ordinal), "engineering 文件含 engineering 科技");
        var society = Read(modRoot, "common/inline_scripts/shelter_give_society_technology.txt");
        Assert.True(society.Contains("tech_society_1", StringComparison.Ordinal), "society 文件含 society 科技");
    }

    [Test]
    public void NorSingleConditionNegatesExistence()
    {
        // 用户 #4 完整场景：4 条件 And（key=utility ∧ 存在CORVETTE_ ∧ 不存在BIO ∧ 存在power_core）
        var (adapter, _, modRoot) = Setup(
            "utility_component_template = {\n key = \"CORVETTE_BIO_WEAPON\"\n component_set = power_core\n}\n" +
            "utility_component_template = {\n key = \"CORVETTE_REACTOR\"\n component_set = power_core\n}\n" +
            "utility_component_template = {\n key = \"DESTROYER_REACTOR\"\n component_set = power_core\n}\n" +
            "utility_component_template = {\n key = \"CORVETTE_BIO_SHIELD\"\n component_set = power_core\n}\n");
        var plan = PlanWith(new StepConfig
        {
            Rule = "extract", Mode = "nodes",
            Match = Cond("{\"rule\":[{\"target\":\"key\",\"keywords\":[\"utility_component_template\"]},{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"key\"]},{\"target\":\"value\",\"type\":\"start\",\"keywords\":[\"CORVETTE_\"]}]}},{\"mode\":\"Any\",\"match\":{\"rule\":[{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"key\"]},{\"target\":\"value\",\"type\":\"start\",\"keywords\":[\"CORVETTE_BIO_\"]}]}}],\"check_rule\":\"Nor\"}},{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"component_set\"]},{\"target\":\"value\",\"keywords\":[\"power_core\"]}]}}]}")
        });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        // BIO 的 block 被排除：剩余 1 个（CORVETTE_REACTOR——CORVETTE_ 前缀 + 非BIO + power_core）
        Assert.Equal(1, state.Nodes.Count, "排除 BIO 后剩 1 个");
        Assert.False(state.Nodes.Any(n => n.Children.Any(c => c.Key == "key" && c.Value?.ToString()?.StartsWith("CORVETTE_BIO_", System.StringComparison.Ordinal) == true)),
            "BIO 的 block 被排除");
        Assert.True(state.Nodes.Any(n => n.Children.Any(c => c.Key == "key" && c.Value?.ToString() == "CORVETTE_REACTOR")),
            "CORVETTE_REACTOR 保留");
    }

    [Test]
    public void WriteTargetKeyAndSimple()
    {
        // write target：value(缺省) / key / simple——"最终选择某一个"
        var (adapter, _, modRoot) = Setup(
            "shelter_reactor = {\n key = \"CORVETTE_REACTOR\"\n}\n");
        // target=key：取定位节点的 Key（字段名）
        var planKey = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf(LeafJson("key", "shelter_reactor")) },
            new StepConfig
            {
                Rule = "write", Source = "nodes", Target = "key",
                Path = Cond("[{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"key\"]}],\"check_rule\":\"And\"}}]"),
                Format = "field={key}",
                File = "common/inline_scripts/test_target_key.txt"
            });
        RunSingleDeployment(Runner(adapter, modRoot), planKey);
        Assert.Equal("field=key", Read(modRoot, "common/inline_scripts/test_target_key.txt"), "target=key 取字段名");
        // target=simple：取定位节点整体序列化（key = \"CORVETTE_REACTOR\"）
        var planSimple = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf(LeafJson("key", "shelter_reactor")) },
            new StepConfig
            {
                Rule = "write", Source = "nodes", Target = "simple",
                Path = Cond("[{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"key\"]}],\"check_rule\":\"And\"}}]"),
                Format = "{key}",
                File = "common/inline_scripts/test_target_simple.txt"
            });
        RunSingleDeployment(Runner(adapter, modRoot), planSimple);
        var simple = Read(modRoot, "common/inline_scripts/test_target_simple.txt");
        Assert.True(simple.Contains("key = \"CORVETTE_REACTOR\"", StringComparison.Ordinal), "target=simple 输出整个 Simple 节点");
        // target 缺省 = value：取字段值
        var planValue = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf(LeafJson("key", "shelter_reactor")) },
            new StepConfig
            {
                Rule = "write", Source = "nodes",
                Path = Cond("[{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"key\"]}],\"check_rule\":\"And\"}}]"),
                Format = "val={key}",
                File = "common/inline_scripts/test_target_value.txt"
            });
        RunSingleDeployment(Runner(adapter, modRoot), planValue);
        Assert.Equal("val=CORVETTE_REACTOR", Read(modRoot, "common/inline_scripts/test_target_value.txt"), "缺省 target 取值");
    }

    [Test]
    public void WriteHeaderFooterWrapsContent()
    {
        // 头/尾字面原样：生成带包装结构的特殊文件
        var (adapter, _, modRoot) = Setup("sr_zro = {\n cost = 10\n}\nsr_alpha = {\n cost = 5\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones" },
            new StepConfig
            {
                Rule = "write",
                File = "common/inline_scripts/shelter_resources_wrapped.txt",
                Format = "{key} = $VALUE$",
                Header = "planet = {\n  resources = {\n",
                Footer = "  }\n}"
            });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        var content = Read(modRoot, "common/inline_scripts/shelter_resources_wrapped.txt");
        Assert.True(content.StartsWith("planet = {\n  resources = {\n", StringComparison.Ordinal), "头部原样");
        Assert.True(content.EndsWith("  }\n}", StringComparison.Ordinal), "尾部原样");
        Assert.True(content.Contains("sr_zro = $VALUE$", StringComparison.Ordinal), "行内容在中间");
        Assert.True(content.Contains("sr_alpha = $VALUE$", StringComparison.Ordinal), "第二行");
    }

    [Test]
    public void NestedBranchConditionMatchesPathUnderBlock()
    {
        // 嵌套枝：potential → from → country_uses_bio_ships = no（rule 里枝 = Children 层存在性）
        var (adapter, _, _) = Setup(
            "zone_a = {\n potential = {\n  from = {\n   country_uses_bio_ships = no\n  }\n }\n}\n" +
            "zone_b = {\n potential = {\n  from = {\n   country_uses_bio_ships = yes\n  }\n }\n}\n" +
            "zone_c = {\n potential = {\n  other = {\n   country_uses_bio_ships = no\n  }\n }\n}\n");
        var nodes = adapter.GetAllConfigs().Values.First().RootNodes;
        var zoneA = nodes.First(n => n.Key == "zone_a");
        var zoneB = nodes.First(n => n.Key == "zone_b");
        var zoneC = nodes.First(n => n.Key == "zone_c");

        // potential 块 → from 块 → Simple(country_uses_bio_ships=no)——单枝三层嵌套（存在性链）
        string rule = BranchJson("Block",
            LeafJson("key", "potential") + "," +
            BranchJson("Block",
                LeafJson("key", "from") + "," +
                BranchJson("Simple", LeafJson("key", "country_uses_bio_ships") + "," + LeafJson("value", "no"))));
        Assert.True(SelectorResolver.NodeMatches(RuleOf(rule), "And", zoneA, new SelectResult()), "嵌套路径命中（potential→from→=no）");
        Assert.False(SelectorResolver.NodeMatches(RuleOf(rule), "And", zoneB, new SelectResult()), "值不同不命中（=yes）");
        Assert.False(SelectorResolver.NodeMatches(RuleOf(rule), "And", zoneC, new SelectResult()), "路径不同不命中（在 other 块下）");

        // 只查字段存在（省略 value 条件）
        string ruleExists = BranchJson("Block",
            LeafJson("key", "potential") + "," +
            BranchJson("Block",
                LeafJson("key", "from") + "," +
                BranchJson("Simple", LeafJson("key", "country_uses_bio_ships"))));
        Assert.True(SelectorResolver.NodeMatches(RuleOf(ruleExists), "And", zoneA, new SelectResult()), "只查存在（A 有）");
        Assert.True(SelectorResolver.NodeMatches(RuleOf(ruleExists), "And", zoneB, new SelectResult()), "只查存在（B 有，不管值）");
    }

    [Test]
    public void KeyPatternMatchingStartsContainsEnds()
    {
        var (adapter, _, _) = Setup(
            "shelter_reactor_core = {\n cost = 10\n}\n" +
            "weapon_laser = {\n cost = 5\n}\n" +
            "shelter_shield = {\n cost = 7\n}\n");
        var nodes = adapter.GetAllConfigs().Values.First().RootNodes;
        var shelterCore = nodes.First(n => n.Key == "shelter_reactor_core");
        var laser = nodes.First(n => n.Key == "weapon_laser");
        var shield = nodes.First(n => n.Key == "shelter_shield");

        // 开头匹配（type=start）
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("key", "start", "shelter_")), "And", shelterCore, new SelectResult()), "shelter_reactor_core 以 shelter_ 开头");
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("key", "start", "shelter_")), "And", shield, new SelectResult()), "shelter_shield 以 shelter_ 开头");
        Assert.False(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("key", "start", "shelter_")), "And", laser, new SelectResult()), "weapon_laser 不以 shelter_ 开头");

        // 包含匹配（type=contains）
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("key", "contains", "reactor")), "And", shelterCore, new SelectResult()), "含 reactor");
        Assert.False(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("key", "contains", "reactor")), "And", shield, new SelectResult()), "shield 不含 reactor");

        // 结尾匹配（type=end）
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("key", "end", "_core")), "And", shelterCore, new SelectResult()), "以 _core 结尾");
        Assert.False(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("key", "end", "_core")), "And", laser, new SelectResult()), "laser 不以 _core 结尾");

        // 与精确 key 共存（AND）：contains + end
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("key", "contains", "shelter") + "," + LeafJsonT("key", "end", "_core")),
            "And", shelterCore, new SelectResult()), "组合匹配");
        Assert.False(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("key", "contains", "shelter") + "," + LeafJsonT("key", "end", "_core")),
            "And", shield, new SelectResult()), "shield 不满足 _core 结尾");
    }

    [Test]
    public void ValuePatternMatchingStartsContainsEnds()
    {
        // 值匹配：key 的 value（Simple 节点的值）开头/包含/结尾
        var (adapter, _, _) = Setup(
            "key = \"shelter_CORVETTE_ANTIMATTER_REACTOR\"\n" +
            "upgrades_to = \"CORVETTE_ZERO_POINT_REACTOR\"\n" +
            "size = small\n");
        var nodes = adapter.GetAllConfigs().Values.First().RootNodes;
        var keyNode = nodes.First(n => n.Key == "key");
        var upgradesNode = nodes.First(n => n.Key == "upgrades_to");

        // 值开头匹配
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("value", "start", "shelter_")), "And", keyNode, new SelectResult()), "值以 shelter_ 开头");
        Assert.False(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("value", "start", "shelter_")), "And", upgradesNode, new SelectResult()), "值不以 shelter_ 开头");

        // 值包含匹配
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("value", "contains", "REACTOR")), "And", keyNode, new SelectResult()), "值含 REACTOR");
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("value", "contains", "REACTOR")), "And", upgradesNode, new SelectResult()), "值含 REACTOR");

        // 值结尾匹配
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJsonT("value", "end", "REACTOR")), "And", upgradesNode, new SelectResult()), "值以 REACTOR 结尾");

        // 组合：key 精确 + 值前缀（如 key 字段且值为 shelter_ 开头）
        Assert.True(SelectorResolver.NodeMatches(
            RuleOf(LeafJson("key", "key") + "," + LeafJsonT("value", "start", "shelter_")),
            "And", keyNode, new SelectResult()), "key 字段 + 值前缀组合");
        Assert.False(SelectorResolver.NodeMatches(
            RuleOf(LeafJson("key", "key") + "," + LeafJsonT("value", "start", "shelter_")),
            "And", upgradesNode, new SelectResult()), "upgrades_to 不是 key 字段");
    }

    [Test]
    public void WriteFormatKeyExtractsFieldFromNodes()
    {
        // 用户场景：extract nodes → write format_key 提取 key 字段值 → 生成 yml 引用行
        var (adapter, _, modRoot) = Setup(
            "utility_component_template = {\n key = \"CORVETTE_ANTIMATTER_REACTOR\"\n size = small\n}\n" +
            "utility_component_template = {\n key = \"CORVETTE_ZERO_POINT_REACTOR\"\n size = small\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"utility_component_template\"]}") },
            new StepConfig
            {
                Rule = "write",
                File = "common/inline_scripts/test_ref.yml",
                Source = "nodes",
                Path = Cond("[{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"key\"]}],\"check_rule\":\"And\"}}]"),
                Format = " shelter_{key}: \"${key}$\"\n shelter_{key}_desc: \"${key}$_desc\"",
                Header = "l_english\n"
            });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        var content = Read(modRoot, "common/inline_scripts/test_ref.yml");
        Assert.True(content.StartsWith("l_english\n", StringComparison.Ordinal), "header 保留");
        Assert.True(content.Contains("shelter_CORVETTE_ANTIMATTER_REACTOR: \"$CORVETTE_ANTIMATTER_REACTOR$\"", StringComparison.Ordinal),
            "节点 1 提取 key 字段值替换 {key}");
        Assert.True(content.Contains("shelter_CORVETTE_ANTIMATTER_REACTOR_desc: \"$CORVETTE_ANTIMATTER_REACTOR$_desc\"", StringComparison.Ordinal),
            "节点 1 desc 行（{key} 出现多次全替换）");
        Assert.True(content.Contains("shelter_CORVETTE_ZERO_POINT_REACTOR", StringComparison.Ordinal), "节点 2 也生成");
    }

    [Test]
    public void WriteFormatKeySkipsNodeWithoutField()
    {
        // 节点缺 format_key 字段 → 跳过该节点（不输出行）
        var (adapter, _, modRoot) = Setup(
            "template_a = {\n key = \"AAA\"\n}\n" +
            "template_b = {\n size = small\n}\n");   // 无 key 字段
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf(LeafJsonT("key", "start", "template_")) },
            new StepConfig
            {
                Rule = "write",
                File = "common/inline_scripts/test_skip.txt",
                Source = "nodes",
                Path = Cond("[{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"key\"]}],\"check_rule\":\"And\"}}]"),
                Format = "give_tech = { tech = {key} }"
            });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        var content = Read(modRoot, "common/inline_scripts/test_skip.txt");
        Assert.True(content.Contains("tech = AAA", StringComparison.Ordinal), "有 key 字段的节点输出");
        Assert.False(content.Contains("template_b", StringComparison.Ordinal), "无 key 字段的节点被跳过");
    }

    [Test]
    public void WriteFormatKeyNestedObjectPath()
    {
        // 嵌套对象路径：a块→b块→c字段 的值（避免点分隔歧义）
        var (adapter, _, modRoot) = Setup(
            "template_x = {\n a = {\n  b = {\n   c = \"DEEP_VALUE\"\n  }\n }\n key = \"SHALLOW\"\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"template_x\"]}") },
            new StepConfig
            {
                Rule = "write",
                File = "common/inline_scripts/test_nested.txt",
                Source = "nodes",
                Path = Cond("[{\"mode\":\"Block\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"a\"]}],\"check_rule\":\"And\"}},{\"mode\":\"Block\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"b\"]}],\"check_rule\":\"And\"}},{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"c\"]}],\"check_rule\":\"And\"}}]"),
                Format = "val = {key}"
            });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        var content = Read(modRoot, "common/inline_scripts/test_nested.txt");
        Assert.True(content.Contains("val = DEEP_VALUE", StringComparison.Ordinal), "嵌套路径取到深层字段值");
        Assert.False(content.Contains("SHALLOW", StringComparison.Ordinal), "没取浅层 key 字段");
    }

    [Test]
    public void ModifyPathNestedObject()
    {
        // path 枝/叶数组：Block upkeep → Simple energy 定位深层字段（避免点分隔歧义）
        var (adapter, _, modRoot) = Setup("shelter = {\n upkeep = {\n  energy = 0.22\n }\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"shelter\"]}") },
            new StepConfig { Rule = "modify", Path = Cond("[{\"mode\":\"Block\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"upkeep\"]}],\"check_rule\":\"And\"}},{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"energy\"]}],\"check_rule\":\"And\"}}]"), Op = "mul", Value = "2" });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        var serialized = SerializationHelper.Serialize(state.Nodes);
        Assert.True(serialized.Contains("energy = 0.44", StringComparison.Ordinal), "嵌套路径修改深层字段（0.22×2=0.44）");
        Assert.False(serialized.Contains("energy = 0.22", StringComparison.Ordinal), "旧值不输出");
    }

    // ============ modify ============

    [Test]
    public void ModifyValuesPrefixSuffixReplace()
    {
        // 原 transform 并入 modify：source=values 对 Values 逐项 op
        var (adapter, _, modRoot) = Setup("sr_zro = {\n cost = 10\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "keys", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"sr_zro\"]}") },
            new StepConfig { Rule = "modify", Source = "values", Op = "prefix", Value = "mod_" },
            new StepConfig { Rule = "modify", Source = "values", Op = "suffix", Value = "_x" },
            new StepConfig { Rule = "modify", Source = "values", Op = "replace", Value = "zro", With = "ZRO" });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        Assert.Equal(1, state.Values.Count, "1 个值");
        Assert.Equal("mod_sr_ZRO_x", state.Values[0], "前缀+后缀+替换依次生效");
    }

    // ============ modify ============

    [Test]
    public void ModifySetAddPrefixAndClearsRawText()
    {
        var (adapter, _, modRoot) = Setup("shelter = {\n upkeep = {\n  energy = 5\n  minerals = 3\n }\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"shelter\"]}") },
            new StepConfig { Rule = "modify", Path = Cond("[{\"mode\":\"Block\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"upkeep\"]}],\"check_rule\":\"And\"}},{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"energy\"]}],\"check_rule\":\"And\"}}]"), Op = "add", Value = "10" },
            new StepConfig { Rule = "modify", Path = Cond("[{\"mode\":\"Block\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"upkeep\"]}],\"check_rule\":\"And\"}},{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"minerals\"]}],\"check_rule\":\"And\"}}]"), Op = "set", Value = "99" });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        Assert.Equal(1, state.Nodes.Count, "1 个节点");
        var serialized = SerializationHelper.Serialize(state.Nodes);
        Assert.True(serialized.Contains("energy = 15", StringComparison.Ordinal), "add 后 energy=15（原 5+10）");
        Assert.True(serialized.Contains("minerals = 99", StringComparison.Ordinal), "set 后 minerals=99");
        Assert.False(serialized.Contains("energy = 5", StringComparison.Ordinal), "RawText 已清空——不输出旧值");
    }

    // ============ write ============

    [Test]
    public void WriteFormatModeKeepsLiteralAndReplacesKey()
    {
        var (adapter, _, modRoot) = Setup("sr_zro = {\n cost = 10\n}\nsr_alpha = {\n cost = 5\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/inline_scripts/zones" },
            new StepConfig { Rule = "write", File = "common/inline_scripts/shelter_all_resources.txt", Format = "{key} = $VALUE$" });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        var content = Read(modRoot, "common/inline_scripts/shelter_all_resources.txt");
        Assert.True(content.Contains("sr_zro = $VALUE$", StringComparison.Ordinal), "{key} 替换 + $VALUE$ 字面保留");
        Assert.True(content.Contains("sr_alpha = $VALUE$", StringComparison.Ordinal), "第二行");
    }

    [Test]
    public void WriteSerializeModeMergesNodesIntoBigFile()
    {
        var (adapter, _, modRoot) = Setup(
            "component_template = {\n type = utility\n}\ncomponent_template = {\n type = support\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"component_template\"]}") },
            new StepConfig { Rule = "write", Output = "serialize", File = "common/inline_scripts/all_components.txt", Separator = "\n\n" });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        var content = Read(modRoot, "common/inline_scripts/all_components.txt");
        // 序列化格式：≤3 子项且宽度 <64 的块单行输出（ShouldBeSingleLine 规范）
        Assert.True(content.Contains("type = utility", StringComparison.Ordinal), "节点 1 内容");
        Assert.True(content.Contains("type = support", StringComparison.Ordinal), "节点 2 内容");
        Assert.Equal(2, CountOccurrences(content, "component_template = {"), "两个节点都序列化进大文件");
    }

    // ============ write 编码 ============

    [Test]
    public void WriteYmlDefaultBomAndExplicitEncoding()
    {
        // 缺省：.yml 自动带 BOM（编码规范）；encoding 显式 utf-8 → 无 BOM；utf-8-bom → 带 BOM
        var (adapter, _, modRoot) = Setup("sr_zro = {\n cost = 10\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "keys", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"sr_zro\"]}") },
            new StepConfig { Rule = "write", File = "localisation/english/zzz_default.yml", Format = " {key}:0 \"{key}\"", Header = "l_english:\n" },
            new StepConfig { Rule = "write", File = "localisation/english/zzz_nobom.txt", Format = " {key}:0 \"{key}\"", Header = "l_english:\n", Encoding = "utf-8" },
            new StepConfig { Rule = "write", File = "common/inline_scripts/zzz_bom.yml", Format = "{key} = $VALUE$", Encoding = "utf-8-bom" });
        RunSingleDeployment(Runner(adapter, modRoot), plan);

        var defBytes = File.ReadAllBytes(Path.Combine(modRoot, "localisation", "english", "zzz_default.yml"));
        Assert.Equal(0xEF, defBytes[0], "缺省 .yml 自动带 BOM");
        var defText = Encoding.UTF8.GetString(defBytes, 3, defBytes.Length - 3);
        Assert.True(defText.StartsWith("l_english:", StringComparison.Ordinal), "header 头（lang 已删，用 header 通用）");
        Assert.True(defText.Contains("sr_zro:0 \"sr_zro\"", StringComparison.Ordinal), "format 行");

        var noBomBytes = File.ReadAllBytes(Path.Combine(modRoot, "localisation", "english", "zzz_nobom.txt"));
        Assert.False(noBomBytes.Length >= 3 && noBomBytes[0] == 0xEF && noBomBytes[1] == 0xBB && noBomBytes[2] == 0xBF,
            "encoding=utf-8 显式 → 无 BOM（即使 .txt 之外规则无关）");

        var bomBytes = File.ReadAllBytes(Path.Combine(modRoot, "common", "inline_scripts", "zzz_bom.yml"));
        Assert.Equal(0xEF, bomBytes[0], "encoding=utf-8-bom 显式 → 带 BOM（即使非本地化路径）");
    }

    // ============ nodes 深度控制 ============

    [Test]
    public void NodesDepthTopCollectsOnlyRootBlocks()
    {
        // 缺省 top：只收顶层块；depth=all：内层块也收
        var (adapter, _, modRoot) = Setup(
            "resource = {\n inner = {\n  target = 1\n }\n}\n");
        var planTop = PlanWith(new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"inner\"]}") });
        var stateTop = RunSingleDeployment(Runner(adapter, modRoot), planTop);
        Assert.Equal(0, stateTop.Nodes.Count, "缺省 top：内层块不收集");

        var planAll = PlanWith(new StepConfig { Rule = "extract", Mode = "nodes", Depth = "all", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"inner\"]}") });
        var stateAll = RunSingleDeployment(Runner(adapter, modRoot), planAll);
        Assert.Equal(1, stateAll.Nodes.Count, "depth=all：内层块收集");
    }

    [Test]
    public void ModifySkipsNodesWithoutFieldAndSerializeKeepsThem()
    {
        // 用户场景：混合 max/无 max 的根 block——有 max ×100，无 max 不崩溃、保留原样写回
        var (adapter, _, modRoot) = Setup(
            "sr_zro = {\n max = 10\n}\n" +
            "sr_lux = {\n cost = 5\n}\n");   // 无 max 字段
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Dir = "common/inline_scripts/zones" },
            new StepConfig { Rule = "modify", Source = "nodes", Path = Cond("[{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"max\"]}],\"check_rule\":\"And\"}}]"), Op = "mul", Value = "100" },
            new StepConfig { Rule = "write", Output = "serialize", File = "common/strategic_resources/00_strategic_resources.txt" });
        RunSingleDeployment(Runner(adapter, modRoot), plan);
        var content = Read(modRoot, "common/strategic_resources/00_strategic_resources.txt");
        Assert.True(content.Contains("max = 1000", StringComparison.Ordinal), "有 max 的块 ×100（10×100=1000）");
        Assert.True(content.Contains("sr_lux", StringComparison.Ordinal), "无 max 的块保留输出");
        Assert.True(content.Contains("cost = 5", StringComparison.Ordinal), "无 max 块内容原样");
    }

    [Test]
    public void JsonDeserializationMapsSnakeCaseFieldNames()
    {
        // 真实 JSON 反序列化（用户配置文件走此路径）：带下划线的字段必须映射到 C# 属性
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var step = JsonSerializer.Deserialize<StepConfig>(
            "{\"rule\":\"modify\",\"source\":\"nodes\",\"path\":[{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"max\"]}]}}],\"op\":\"mul\",\"value\":\"100\"}",
            opts)!;
        Assert.Equal("modify", step.Rule, "rule 映射");
        Assert.Equal("nodes", step.Source, "source 映射");
        Assert.True(step.Path.HasValue, "path（枝/叶数组）映射成功");
        Assert.Equal(JsonValueKind.Array, step.Path.Value.ValueKind, "path 为数组");

        var eng = JsonSerializer.Deserialize<StepConfig>(
            "{\"rule\":\"extract\",\"engine\":\"strategic_resource\",\"engine_args\":{\"output\":\"keys\"}}",
            opts)!;
        Assert.True(eng.EngineArgs.HasValue, "engine_args 映射成功（下划线命名）");
    }

    // ============ has 值模式匹配 ============

    [Test]
    public void HasValueStartsMatchesFieldValuePrefix()
    {
        var (adapter, _, _) = Setup(
            "utility_component_template = {\n key = \"CORVETTE_ANTIMATTER_REACTOR\"\n size = small\n}\n" +
            "utility_component_template = {\n key = \"DESTROYER_THRUSTER\"\n size = small\n}\n");
        var nodes = adapter.GetAllConfigs().Values.First().RootNodes;
        var corvette = nodes.First(n => n.Key == "utility_component_template" && n.Children.Any(c => c.Key == "key" && c.Value?.ToString() == "CORVETTE_ANTIMATTER_REACTOR"));

        var cond = Cond("{\"has\":{\"key\":\"key\",\"value_starts\":\"CORVETTE_\"}}");
        Assert.True(MatchCondition.Eval(cond, corvette, "common/component_templates/x.txt"), "CORVETTE_ 前缀命中");
        var destroyer = nodes.First(n => n.Key == "utility_component_template" && n.Children.Any(c => c.Key == "key" && c.Value?.ToString() == "DESTROYER_THRUSTER"));
        Assert.False(MatchCondition.Eval(cond, destroyer, "common/component_templates/x.txt"), "非 CORVETTE_ 前缀不命中");
    }

    [Test]
    public void ComponentTemplateScenarioExtractAndBothOutputs()
    {
        // 用户场景：path + 根 key + key 字段值前缀 CORVETTE_ + component_set ∈ {power_core, thruster_components}
        // 输出 A：命中块 serialize；输出 B：命中块 key 字段值 format
        var tmp = Path.Combine(Path.GetTempPath(), "smt_ext_" + Guid.NewGuid().ToString("N"));
        var gameRoot = Path.Combine(tmp, "game");
        var modRoot = Path.Combine(tmp, "mod");
        Write(gameRoot, "common/component_templates/comp.txt",
            "utility_component_template = {\n key = \"CORVETTE_ANTIMATTER_REACTOR\"\n component_set = \"power_core\"\n size = small\n}\n" +
            "utility_component_template = {\n key = \"DESTROYER_THRUSTER\"\n component_set = \"thruster_components\"\n size = medium\n}\n" +   // key 前缀不匹配
            "utility_component_template = {\n key = \"CORVETTE_SHIELD\"\n component_set = \"shields\"\n size = small\n}\n" +   // component_set 不在候选
            "other_template = {\n key = \"CORVETTE_WEAPON\"\n component_set = \"power_core\"\n}\n");   // 根 key 不匹配
        var adapter = new StellarisAdapter();
        adapter.AddRoot(gameRoot);
        adapter.ScanAll();

        var match = MatchOf(
            LeafJson("key", "utility_component_template") + "," +
            BranchJson("Any", LeafJson("key", "key") + "," + LeafJsonT("value", "start", "CORVETTE_")) + "," +
            BranchJson("Any",
                LeafJson("key", "component_set") + "," +
                LeafJsonC("value", "Or", "power_core", "thruster_components")));
        var runner = Runner(adapter, modRoot);
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Dir = "common/component_templates", Match = match },
            new StepConfig { Rule = "write", Output = "serialize", File = "common/inline_scripts/shelter_corvette_components.txt" },
            new StepConfig { Rule = "clear" },
            new StepConfig { Rule = "extract", Mode = "nodes", Match = match },
            new StepConfig { Rule = "write", Source = "nodes", Path = Cond("[{\"mode\":\"Simple\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"key\"]}],\"check_rule\":\"And\"}}]"), Format = "{key}", File = "common/inline_scripts/shelter_corvette_keys.txt" });
        runner.Run(plan);

        var serialized = Read(modRoot, "common/inline_scripts/shelter_corvette_components.txt");
        Assert.True(serialized.Contains("CORVETTE_ANTIMATTER_REACTOR", StringComparison.Ordinal), "输出 A：命中块 serialize");
        Assert.False(serialized.Contains("DESTROYER_THRUSTER", StringComparison.Ordinal), "key 前缀不匹配 → 不输出");
        Assert.False(serialized.Contains("CORVETTE_SHIELD", StringComparison.Ordinal), "component_set 不匹配 → 不输出");
        Assert.False(serialized.Contains("other_template", StringComparison.Ordinal), "根 key 不匹配 → 不输出");
        Assert.Equal("CORVETTE_ANTIMATTER_REACTOR", Read(modRoot, "common/inline_scripts/shelter_corvette_keys.txt"), "输出 B：key 字段值");
    }

    [Test]
    public void ModifyBlockSetRebuildsContent()
    {
        // set 定位到 Block：value 作为块内容重建（key = { value } 重新解析，整体替换旧内容）
        var (adapter, _, modRoot) = Setup(
            "shelter = {\n potential = {\n  old_flag = yes\n }\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"shelter\"]}") },
            new StepConfig { Rule = "modify", Source = "nodes", Path = Cond("[{\"mode\":\"Block\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"potential\"]}],\"check_rule\":\"And\"}}]"), Op = "set",
                Value = "ship_uses_corvette_components = no\nship_uses_destroyer_components = no" });
        var state = RunSingleDeployment(Runner(adapter, modRoot), plan);
        var serialized = SerializationHelper.Serialize(state.Nodes);
        Assert.True(serialized.Contains("ship_uses_corvette_components = no", StringComparison.Ordinal), "新内容写入");
        Assert.True(serialized.Contains("ship_uses_destroyer_components = no", StringComparison.Ordinal), "多行内容写入");
        Assert.False(serialized.Contains("old_flag", StringComparison.Ordinal), "旧内容被整体替换");
        Assert.True(serialized.Contains("potential = {", StringComparison.Ordinal), "Block 身份（key）保留");
    }

    [Test]
    public void ModifyBlockSetWithBrokenValueThrows()
    {
        // value 语法错误 → 抛异常（配置错误，中止本轮）
        var (adapter, _, modRoot) = Setup("shelter = {\n potential = {\n  old_flag = yes\n }\n}\n");
        var plan = PlanWith(
            new StepConfig { Rule = "extract", Mode = "nodes", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"shelter\"]}") },
            new StepConfig { Rule = "modify", Source = "nodes", Path = Cond("[{\"mode\":\"Block\",\"match\":{\"rule\":[{\"target\":\"key\",\"keywords\":[\"potential\"]}],\"check_rule\":\"And\"}}]"), Op = "set",
                Value = "ship_uses_x = no\n{{{" });
        bool threw = false;
        try
        {
            RunSingleDeployment(Runner(adapter, modRoot), plan);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Assert.True(threw, "非法 value → 抛异常");
    }

    // ============ deployments 多轮 ============

    [Test]
    public void DeploymentsRunIndependentlyMultiFilePerRound()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "smt_ext_" + Guid.NewGuid().ToString("N"));
        var gameRoot = Path.Combine(tmp, "game");
        var modRoot = Path.Combine(tmp, "mod");
        Write(gameRoot, "common/inline_scripts/zones/a.txt",
            "included_building_sets = { entertainment ark }\n");
        Write(gameRoot, "common/strategic_resources/00_strategic_resources.txt",
            "sr_zro = {\n cost = 10\n}\n");
        var adapter = new StellarisAdapter();
        adapter.AddRoot(gameRoot);
        adapter.ScanAll();

        var plan = new ExtensionPlan
        {
            Roots = new System.Collections.Generic.List<string>(),
            Deployments =
            {
                new Deployment { Steps =
                {
                    new StepConfig { Rule = "extract", Match = MatchOf("{\"target\":\"key\",\"keywords\":[\"included_building_sets\"]}") },
                    new StepConfig { Rule = "write", File = "common/inline_scripts/shelter_all_building_set.txt", Format = "{key}" }
                } },
                new Deployment { Steps =
                {
                    new StepConfig { Rule = "extract", Mode = "keys", Dir = "common/strategic_resources" },
                    new StepConfig { Rule = "write", File = "common/inline_scripts/shelter_all_resources.txt", Format = "{key} = $VALUE$" },
                    new StepConfig { Rule = "write", File = "common/inline_scripts/shelter_resources_alt.txt", Format = "{key}" }
                } }
            }
        };
        Runner(adapter, modRoot).Run(plan);

        Assert.Equal("entertainment\nark", Read(modRoot, "common/inline_scripts/shelter_all_building_set.txt"), "轮 1 文件");
        Assert.Equal("sr_zro = $VALUE$", Read(modRoot, "common/inline_scripts/shelter_all_resources.txt"), "轮 2 文件 1");
        Assert.Equal("sr_zro", Read(modRoot, "common/inline_scripts/shelter_resources_alt.txt"), "轮 2 文件 2（单轮多文件）");
    }

    // ============ 辅助：单轮执行取状态 ============

    private static ExtensionState RunSingleDeployment(ExtensionRunner runner, ExtensionPlan plan)
        => runner.ExecuteDeployment(plan.Deployments[0]);

    private static ExtensionPlan PlanWith(params StepConfig[] steps)
        => new()
        {
            Roots = new System.Collections.Generic.List<string>(),
            Deployments = { new Deployment { Steps = steps.ToList() } }
        };

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ==================== 标准选择路径 helper（与 DictSelectorTests 同构）====================
    private static List<object> P(params object[] selectors) => selectors.ToList();
    private static System.Text.Json.JsonElement J(string s) => System.Text.Json.JsonSerializer.SerializeToElement(s);
    private static Dictionary<string, object> Branch(string mode, List<object> rule, string checkRule = "And")
        => new() { ["mode"] = mode, ["match"] = new Dictionary<string, object> { ["rule"] = rule, ["check_rule"] = checkRule } };
    private static Dictionary<string, object> Leaf(string target, params string[] keywords)
        => new() { ["target"] = target, ["keywords"] = keywords.ToList() };
}
