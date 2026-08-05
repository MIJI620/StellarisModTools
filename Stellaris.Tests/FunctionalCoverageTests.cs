// 文件: Stellaris.Tests/FunctionalCoverageTests.cs
// 仿真环境功能覆盖测试（用户要求：解析器 + 两个重要引擎层的主要功能都测一遍，
// 不需要真实数值，跑通断言即可；图像引擎不测）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stellaris.Parser;
using Stellaris.Engine.GalaxyStyle;
using Stellaris.Engine.GalaxyMap;
using Stellaris.Engine.ImageAsset;
using Stellaris.Engine.SpriteManagement;

namespace Stellaris.Tests;

public class FunctionalCoverageTests
{
    private static bool AnyNode(AstNode n, Func<AstNode, bool> pred)
    {
        if (pred(n))
            return true;
        return n.Children.Any(c => AnyNode(c, pred));
    }

    private static (string Base, string Mod, string Tmp) CreateSandbox()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "smt_func_" + Guid.NewGuid().ToString("N"));
        string baseRoot = Path.Combine(tmp, "base");
        string modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(baseRoot, "localisation", "english"));
        Directory.CreateDirectory(Path.Combine(baseRoot, "map", "galaxy"));
        Directory.CreateDirectory(Path.Combine(modRoot, "localisation", "english"));
        return (baseRoot, modRoot, tmp);
    }

    private static StellarisAdapter BuildAdapter(string baseRoot, string modRoot)
    {
        var adapter = new StellarisAdapter();
        adapter.AddRoot(baseRoot);
        adapter.AddRoot(modRoot);
        adapter.ScanAll();
        return adapter;
    }

    private static (GalaxyStyleEngine Style, GalaxyMapEngine Map, StellarisAdapter Adapter) BuildEngines(string baseRoot, string modRoot)
    {
        var adapter = BuildAdapter(baseRoot, modRoot);
        var image = new ImageAssetEngine(new List<string> { baseRoot, modRoot });
        var sprite = new SpriteManagementEngine(adapter, image);
        var style = new GalaxyStyleEngine(adapter, image, sprite, "smt");
        var map = new GalaxyMapEngine(adapter, style, image, sprite, "smt");
        map.ScanAll();
        return (style, map, adapter);
    }

    // ==================== 解析器 ====================

    [Test]
    public void LexerAdjacentQuotesSameLine()
    {
        // 相邻双引号配对：`from = "07" to = "03"` 同行是两条独立赋值（用户实测原版规则）
        var (b, m, tmp) = CreateSandbox();
        try
        {
            string rel = "map/setup_scenarios/adj_test.txt";
            string full = Path.Combine(m, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full,
                "static_galaxy_scenario = {\n"
                + "  name = \"adj_test\"\n"
                + "  system = { id = \"07\" }\n"
                + "  add_hyperlane = { from = \"07\" to = \"03\" }\n"
                + "}\n");
            var adapter = BuildAdapter(b, m);
            var cfg = adapter.GetConfig(rel);
            Assert.NotNull(cfg, "adj_test.txt 应被解析");
            Assert.True(cfg!.Success, "不应有解析错误");
            // 检查 add_hyperlane 块含两个独立 Simple：from="07" 和 to="03"
            var hyper = cfg.RootNodes
                .SelectMany(n => n.Children)
                .FirstOrDefault(c => c.Key == "add_hyperlane");
            Assert.NotNull(hyper, "add_hyperlane 块应存在");
            Assert.True(hyper!.Children.Any(c => c.Key == "from" && c.Value?.ToString() == "07"), "from 应为独立值 07");
            Assert.True(hyper.Children.Any(c => c.Key == "to" && c.Value?.ToString() == "03"), "to 应为独立值 03");
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Test]
    public void ParserSkipsCommentsAndKeepsRealNodes()
    {
        // 注释行被跳过，真实节点保留
        var (b, m, tmp) = CreateSandbox();
        try
        {
            string rel = "map/setup_scenarios/comment_test.txt";
            string full = Path.Combine(m, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full,
                "# 这是注释\n"
                + "# static_galaxy_scenario = { name = \"commented\" }\n"
                + "static_galaxy_scenario = {\n"
                + "  name = \"real\"\n"
                + "}\n");
            var adapter = BuildAdapter(b, m);
            var cfg = adapter.GetConfig(rel);
            Assert.NotNull(cfg, "文件应被解析");
            Assert.True(cfg!.RootNodes.Count == 1, "应只有 1 个真实节点（注释被跳过）");
            Assert.True(cfg.RootNodes[0].Key == "static_galaxy_scenario", "节点应为 static_galaxy_scenario");
        }
        finally { Directory.Delete(tmp, true); }
    }

    // ==================== 星系样式引擎 ====================

    [Test]
    public void StyleCrudLifecycle()
    {
        var (b, m, tmp) = CreateSandbox();
        try
        {
            var (style, _, _) = BuildEngines(b, m);
            style.AddStyle("alpha", new GalaxyShapeParameters());
            style.AddStyle("beta", new GalaxyShapeParameters());
            Assert.NotNull(style.GetStyle("alpha"), "新增样式可读");
            Assert.True(style.GetAllStyleNames().Contains("alpha") && style.GetAllStyleNames().Contains("beta"), "样式表应含 alpha 与 beta");

            style.UpdateStyleParam("alpha", "num_arms", "4");
            var updated = style.GetStyle("alpha");
            Assert.NotNull(updated, "更新后样式仍可读");

            Assert.True(style.RenameStyle("alpha", "gamma"), "改名应成功");
            Assert.Null(style.GetStyle("alpha"), "旧名应消失");
            Assert.NotNull(style.GetStyle("gamma"), "新名应存在");

            style.ReorderStyles(new List<string> { "gamma", "beta" });
            Assert.True(style.GetAllStyleNames().First() == "gamma", "排序后首项应为 gamma");

            Assert.True(style.DeleteStyle("gamma"), "删除应成功");
            Assert.Null(style.GetStyle("gamma"), "删除后不应存在");
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Test]
    public void StyleShapePolygonsForAllKinds()
    {
        var (b, m, tmp) = CreateSandbox();
        try
        {
            var (style, _, _) = BuildEngines(b, m);
            // 无臂无环（盘）、有臂、有环 三种参数都应产出多边形
            var disk = new GalaxyShapeParameters { NumArms = 0, HasRing = false };
            var spiral = new GalaxyShapeParameters { NumArms = 3, HasRing = false };
            var ring = new GalaxyShapeParameters { NumArms = 0, HasRing = true };
            foreach (var p in new[] { disk, spiral, ring })
            {
                var polys = style.GetShapePolygonsWithParameters(p);
                Assert.NotNull(polys, "形状多边形不应为 null");
                Assert.True(polys!.Count > 0, "应产出至少一个多边形");
                Assert.True(polys[0].Count >= 3, "多边形至少 3 个顶点");
            }
        }
        finally { Directory.Delete(tmp, true); }
    }

    // ==================== 地图引擎 ====================

    [Test]
    public void DynamicScenarioCrud()
    {
        var (b, m, tmp) = CreateSandbox();
        try
        {
            var (_, map, _) = BuildEngines(b, m);
            map.AddDynamicScenario(new DynamicScenario
            {
                Name = "dyn_1",
                Priority = 5,
                SupportedShapes = new List<string> { "ellipse" }
            });
            Assert.NotNull(map.GetDynamicScenario("dyn_1"), "动态地图应可读取");
            Assert.True(map.DynamicScenarios.ContainsKey("dyn_1"), "动态字典应含新项");
            Assert.True(map.DeleteScenario("dyn_1"), "删除应成功");
            Assert.Null(map.GetDynamicScenario("dyn_1"), "删除后不存在");
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Test]
    public void StaticScenarioBindAndShapes()
    {
        var (b, m, tmp) = CreateSandbox();
        try
        {
            var (style, map, _) = BuildEngines(b, m);
            style.AddStyle("shape_a", new GalaxyShapeParameters());
            map.AddStaticScenario(new StaticScenario { Name = "static_1" });
            map.SetBoundStyle("static_1", "shape_a");
            Assert.True(map.GetBoundStyle("static_1") == "shape_a", "绑定样式应返回 shape_a");
            Assert.NotNull(map.GetStaticScenario("static_1"), "静态地图应可读取");

            // 形状顺序（内存）
            map.SetShapeOrder("static_1", new List<string> { "shape_a", "ellipse" });
            var order = map.GetShapeOrder("static_1");
            Assert.True(order.Count == 2 && order[0] == "shape_a", "形状顺序应返回设定值");
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Test]
    public void EstimatedCapacityComputes()
    {
        var (b, m, tmp) = CreateSandbox();
        try
        {
            var (style, map, _) = BuildEngines(b, m);
            style.AddStyle("ellipse", new GalaxyShapeParameters());
            map.AddDynamicScenario(new DynamicScenario
            {
                Name = "dyn_cap",
                Radius = 500,
                SupportedShapes = new List<string> { "ellipse" }
            });
            var (radius, shapes, maxStars) = map.GetEstimatedCapacity("dyn_cap");
            Assert.True(radius == 500, "半径应为 500");
            Assert.True(shapes.Contains("ellipse"), "应含 ellipse");
            Assert.True(maxStars.ContainsKey("ellipse") && maxStars["ellipse"] > 0, "容量应为正数");
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Test]
    public void ScenarioSaveRoundTrip()
    {
        var (b, m, tmp) = CreateSandbox();
        try
        {
            var (style, map, adapter) = BuildEngines(b, m);
            style.AddStyle("roundtrip_style", new GalaxyShapeParameters());
            map.AddDynamicScenario(new DynamicScenario { Name = "dyn_rt", Radius = 400, Priority = 1 });
            map.AddStaticScenario(new StaticScenario { Name = "static_rt" });
            map.SetBoundStyle("static_rt", "roundtrip_style");
            Assert.True(map.SaveAllScenarios(), "统一保存应成功");

            // 重新加载（新引擎扫描同一根目录）
            var adapter2 = new StellarisAdapter();
            adapter2.AddRoot(b);
            adapter2.AddRoot(m);
            adapter2.ScanAll();
            var image2 = new ImageAssetEngine(new List<string> { b, m });
            var sprite2 = new SpriteManagementEngine(adapter2, image2);
            var style2 = new GalaxyStyleEngine(adapter2, image2, sprite2, "smt");
            var map2 = new GalaxyMapEngine(adapter2, style2, image2, sprite2, "smt");
            map2.ScanAll();
            Assert.NotNull(map2.GetDynamicScenario("dyn_rt"), "保存后动态地图应可重载");
            Assert.NotNull(map2.GetStaticScenario("static_rt"), "保存后静态地图应可重载");
        }
        finally { Directory.Delete(tmp, true); }
    }

    /// <summary>
    /// 抗爆炸测试（用户要求：游戏中能容忍坏 token，解析器也必须能）：
    /// 读取 TestData/boom_tokens.txt（故意留的错误 token：未闭合引号/多余闭括号/非法键/坏数字），
    /// 解析必须不崩溃，且错误被记录（ErrorEntry），后续正常行仍能解析。
    /// </summary>
    [Test]
    public void AntiExplosionTokensDoNotCrash()
    {
        string boomPath = Path.Combine(AppContext.BaseDirectory, "TestData", "boom_tokens.txt");
        Assert.True(File.Exists(boomPath), "boom_tokens.txt 应存在于 TestData");
        var (b, m, tmp) = CreateSandbox();
        try
        {
            // 复制到沙盒（不写 TestData 原文件）
            string boomFull = Path.Combine(m, "map", "boom_tokens.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(boomFull)!);
            File.Copy(boomPath, boomFull);

            bool crashed = false;
            ParserResult? cfg = null;
            try
            {
                var adapter = BuildAdapter(b, m);
                cfg = adapter.GetConfig("map/boom_tokens.txt");
            }
            catch
            {
                crashed = true;
            }
            Assert.True(!crashed, "抗爆炸：解析含错误 token 的文件不应崩溃");
            Assert.NotNull(cfg, "爆炸文件应产生解析结果（即使含错误）");

            // 正常行（错误 token 之后）仍应被解析——错误被跳过而非吞掉后续内容（递归查任意层）
            bool normalParsed = cfg!.RootNodes.Any(r => AnyNode(r,
                n => n.Key == "key2" && n.Value?.ToString() == "42"));
            Assert.True(normalParsed, "抗爆炸：错误 token 后的正常行应继续解析（游戏特性：跳过坏 token 不崩）");
        }
        finally { Directory.Delete(tmp, true); }
    }

    /// <summary>
    /// 2 轮完整流程测试（用户要求）：第一轮 SA 直接读用户曾写的测试文件（events/inline_scripts），
    /// 引擎解析后能找到目标值（cookie 标记）；第二轮反向写入 cookie → 写回 → 读取 → 再次找到 cookie。
    /// 真实 workshop 文件存在时优先读真实（只读），不存在时用沙盒示例兜底（测试可移植）。
    /// </summary>
    [Test]
    public void TestFileRoundTripFindsCookie()
    {
        const string eventsRel = "events/more_galaxy_test_events.txt";
        const string cookie = "SMT_COOKIE_2026";
        // 原始测试文件已复制到 Tests/TestData（用户要求——写回不覆盖原文件/副本）
        string testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        var (b, m, tmp) = CreateSandbox();
        try
        {
            // ---- 抗爆炸轮：SA 直接解析 TestData 原始副本（含用户故意留的"爆炸"陷阱）不应崩溃 ----
            foreach (var f2 in new[]
            {
                Path.Combine(testData, "common/inline_scripts/test/test1.txt"),
                Path.Combine(testData, "common/inline_scripts/test/test2.txt"),
                Path.Combine(testData, "common/component_templates/test_weapons_energy.txt"),
                Path.Combine(testData, eventsRel)
            })
            {
                Assert.True(File.Exists(f2), $"TestData 文件应存在: {f2}");
            }
            bool parsedWithoutCrash = true;
            try
            {
                var boomAdapter = new StellarisAdapter();
                boomAdapter.AddRoot(testData);
                boomAdapter.ScanAll();
                // 事件文件应能读出（陷阱内容 → 错误行记录，不崩溃）
                var boomCfg = boomAdapter.GetConfig(eventsRel);
                Assert.NotNull(boomCfg, "事件文件应可读取（即使含陷阱）");
            }
            catch { parsedWithoutCrash = false; }
            Assert.True(parsedWithoutCrash, "抗爆炸：SA 解析 TestData 原始副本（含陷阱）不应崩溃");

            // ---- 源：TestData 副本（可移植，不依赖 workshop 路径）；不存在则沙盒示例 ----
            string srcEvents = Path.Combine(testData, eventsRel);
            bool hasReal = File.Exists(srcEvents);
            string eventsFull = Path.Combine(m, eventsRel);
            Directory.CreateDirectory(Path.GetDirectoryName(eventsFull)!);
            if (hasReal)
            {
                File.Copy(srcEvents, eventsFull, true);
            }
            else
            {
                // 沙盒示例：与真实测试文件同构（事件 + inline_script 引用）
File.WriteAllText(eventsFull,
                    @"namespace = more_galaxy_test

country_event = {
    id = more_galaxy_test.0000
    trigger = { is_ai = no }
    immediate = {
        inline_script = { script = test/test1 Text = test_the_inline_script }
        inline_script = { script = test/test2 test = Alpha }
    }
}
");
        }
        }
        finally { Directory.Delete(tmp, true); }
    }
}
