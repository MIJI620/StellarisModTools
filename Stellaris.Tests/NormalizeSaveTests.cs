// 文件: Stellaris.Tests/NormalizeSaveTests.cs
// 规整化/保存重构的集成测试：
//   1) 保存只写本 mod 目录——外部 root（游戏本体等）的本地化键绝不复制进 mod；
//   2) 新样式本地化写入合规文件 style_l_{lang}.yml；
//   3) 规整化把键从非合规文件迁移到合规文件。
// 全程使用临时目录，测试结束清理。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stellaris.Engine.GalaxyMap;
using Stellaris.Engine.GalaxyStyle;
using Stellaris.Engine.ImageAsset;
using Stellaris.Engine.SpriteManagement;
using Stellaris.Parser;

namespace Stellaris.Tests;

public class NormalizeSaveTests
{
    private static (string RootA, string RootB, string Tmp) CreateSandbox()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "smt_norm_" + Guid.NewGuid().ToString("N"));
        string rootA = Path.Combine(tmp, "base"); // 外部（模拟游戏本体/其他 mod）
        string rootB = Path.Combine(tmp, "mod");  // 本 mod（Roots[-1]）
        Directory.CreateDirectory(Path.Combine(rootA, "localisation", "english"));
        Directory.CreateDirectory(Path.Combine(rootA, "map", "galaxy"));
        Directory.CreateDirectory(Path.Combine(rootB, "localisation", "english"));
        return (rootA, rootB, tmp);
    }

    private static StellarisAdapter BuildAdapter(string rootA, string rootB)
    {
        var adapter = new StellarisAdapter();
        adapter.AddRoot(rootA);
        adapter.AddRoot(rootB);
        adapter.ScanAll();
        return adapter;
    }

    [Test]
    public void SaveWritesOnlyModLocalisation()
    {
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            // 外部 root 的本地化（不应被复制到 mod）
            File.WriteAllText(Path.Combine(rootA, "localisation", "english", "game_l_english.yml"),
                "l_english:\n game_key: \"Game Text\"\n");

            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var engine = new GalaxyStyleEngine(adapter, image, sprite, "smt");

            engine.AddStyle("test_style", new GalaxyShapeParameters());
            var result = engine.SaveAllStyles();

            // mod 目录应生成合规本地化文件，且含新样式键
            string styleLoc = Path.Combine(rootB, "localisation", "english", "smt_style_l_english.yml");
            Assert.True(File.Exists(styleLoc), "mod 应生成 smt_style_l_english.yml");
            string content = File.ReadAllText(styleLoc);
            Assert.Contains(content, "test_style", "合规本地化文件应含新样式键");

            // 外部 root 的本地化文件绝不能被复制进 mod
            Assert.False(File.Exists(Path.Combine(rootB, "localisation", "english", "game_l_english.yml")),
                "外部本地化文件不应被复制进 mod 目录");

            // mod 应生成 galaxy_shapes.txt（样式表写回）
            Assert.True(File.Exists(Path.Combine(rootB, "map", "galaxy", "galaxy_shapes.txt")),
                "mod 应生成 galaxy_shapes.txt");
            Assert.True(result.WriteSuccess, "SaveAllStyles.WriteSuccess 应为 true");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void StaticHyperlanesParseAndSaveReplace()
    {
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            // 静态文件：3 个系统 + 2 条航道
            string staticDir = Path.Combine(rootB, "map", "setup_scenarios");
            Directory.CreateDirectory(staticDir);
            File.WriteAllText(Path.Combine(staticDir, "test.txt"), """
static_galaxy_scenario = {
    name = "test"
    priority = 1
    system = { id = "0" name = "A" coordinate = { x = 1 y = 2 } }
    system = { id = "1" name = "B" coordinate = { x = 3 y = 4 } }
    system = { id = "2" name = "C" coordinate = { x = 5 y = 6 } }
    add_hyperlane = { from = "0" to = "1" }
    add_hyperlane = { from = "1" to = "2" }
}
""");

            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var engine = new GalaxyMapEngine(adapter, new GalaxyStyleEngine(adapter, image, sprite, "smt"),
                image, sprite, "smt");
            engine.ScanAll();

            var s = engine.GetStaticScenario("test");
            Assert.True(s != null, "静态场景应被扫描加载");
            Assert.True(s!.Systems.Count == 3, "应解析 3 个系统");
            Assert.True(s.Hyperlanes.Count == 2, $"应解析 2 条航道，实际 {s.Hyperlanes.Count}");

            // 保存替换：删掉 1 条航道后保存，文件里应是 1 条（替换而非累加）
            s.Hyperlanes.RemoveAt(0);
            engine.SaveAllScenarios();
            string saved = File.ReadAllText(Path.Combine(staticDir, "test.txt"));
            int laneCount = System.Text.RegularExpressions.Regex.Matches(saved, "add_hyperlane").Count;
            Assert.True(laneCount == 1, $"保存后文件应只有 1 条航道（替换），实际 {laneCount}");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void Rgba8888DdsIsTraditionalDx9()
    {
        // 预览/按钮 8888 DDS 必须是传统 DX9 未压缩格式（群星兼容，非 DX10/DXT）
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            // 2x2 像素 RGBA：R 值区分（0, 200, 200, 255）
            var data = new byte[][][]
            {
                new byte[][] { new byte[] { 0, 0, 0, 255 }, new byte[] { 200, 0, 0, 255 } },
                new byte[][] { new byte[] { 200, 0, 0, 255 }, new byte[] { 255, 0, 0, 255 } }
            };
            using var ps = new PixelSet(data);
            image.ExportImage("gfx/preview_test", ps, ImageFormat.Rgba8888,
                Stellaris.Engine.ImageAsset.ExportMode.DdsAndPng, new ImageSize(2, 2));
            string ddsPath = Path.Combine(rootB, "gfx", "preview_test.dds");
            Assert.True(File.Exists(ddsPath), "应导出 dds 文件");
            byte[] dds = File.ReadAllBytes(ddsPath);

            Assert.True(dds.Length >= 128 && dds[0] == (byte)'D' && dds[1] == (byte)'D' && dds[2] == (byte)'S' && dds[3] == (byte)' ',
                "DDS magic 应为 'DDS '");
            int fourCC = BitConverter.ToInt32(dds, 84);
            Assert.True(fourCC == 0, "未压缩 8888 DDS 的 FourCC 应为 0（非 DX10/DXT）");
            Assert.True(BitConverter.ToUInt32(dds, 92) == 0x00FF0000, "R 位掩码应为 0x00FF0000（A8R8G8B8）");
            Assert.True(BitConverter.ToUInt32(dds, 104) == 0xFF000000, "A 位掩码应为 0xFF000000（偏移 104）");
            Assert.True(dds[128] == 0 && dds[129] == 0 && dds[130] == 0 && dds[131] == 255,
                "首像素应为 RGBA(0,0,0,255)");
            Assert.True(dds[134] == 200, "第二像素 R=200 应写在 BGRA 的 R 位（偏移 134）");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void UpdateSpriteOverwriteKeepsBlockIntact()
    {
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            string gfx = "interface/game_setup/smt_shapes.gfx";
            adapter.CreateEmptyFileInMemory(gfx, Stellaris.Parser.FileCategory.Config);

            // 首次添加（含 noOfFrames）
            Assert.True(sprite.AddSprite(gfx, "GFX_galaxy_preview_test", "gfx/a.dds", 3, Stellaris.Engine.SpriteManagement.OperationMode.Overwrite),
                "首次添加应成功");

            // 再次覆盖（原 fullOverwrite 曾因空块无 name 定位失败 → 删块后重建残缺）
            Assert.True(sprite.AddSprite(gfx, "GFX_galaxy_preview_test", "gfx/b.dds", 3, Stellaris.Engine.SpriteManagement.OperationMode.Overwrite),
                "覆盖更新应成功且不抛异常");

            var result = adapter.GetConfig(gfx);
            Assert.True(result != null && result.RootNodes != null, "gfx AST 应存在");
            var st = result!.RootNodes.FirstOrDefault(n => n.Type == Stellaris.Parser.NodeType.Block && n.Key == "spriteTypes");
            Assert.True(st != null, "spriteTypes 块应存在");
            var spriteType = st!.Children.FirstOrDefault(c =>
                c.Type == Stellaris.Parser.NodeType.Block && c.Key == "spriteType"
                && c.Children.Any(k => k.Type == Stellaris.Parser.NodeType.Simple && k.Key == "name"
                    && string.Equals(k.Value?.ToString(), "GFX_galaxy_preview_test", StringComparison.Ordinal)));
            Assert.True(spriteType != null, "覆盖后 spriteType 块应完整保留");
            Assert.True(spriteType!.Children.Any(k => k.Type == Stellaris.Parser.NodeType.Simple && k.Key == "noOfFrames"),
                "noOfFrames 应保留");
            Assert.True(spriteType.Children.Any(k => k.Type == Stellaris.Parser.NodeType.Simple && k.Key == "texturefile"
                && string.Equals(k.Value?.ToString(), "gfx/b.dds", StringComparison.Ordinal)),
                "texturefile 应更新为新值");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void StyleReorderSurvivesSaveReload()
    {
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var engine = new GalaxyStyleEngine(adapter, image, sprite, "smt");

            engine.AddStyle("alpha", new GalaxyShapeParameters());
            engine.AddStyle("beta", new GalaxyShapeParameters());
            engine.AddStyle("gamma", new GalaxyShapeParameters());
            Assert.True(string.Join(",", engine.GetAllStyleNames()) == "alpha,beta,gamma", "初始顺序");

            // 拖拽重排：beta → alpha → gamma
            engine.ReorderStyles(new List<string> { "beta", "alpha", "gamma" });
            Assert.True(string.Join(",", engine.GetAllStyleNames()) == "beta,alpha,gamma", "重排后顺序");

            var result = engine.SaveAllStyles();
            Assert.True(result.WriteSuccess, "保存应成功");

            // 重新加载（新引擎实例 = 重启后重新扫描）
            var adapter2 = BuildAdapter(rootA, rootB);
            var image2 = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite2 = new SpriteManagementEngine(adapter2, image2);
            var engine2 = new GalaxyStyleEngine(adapter2, image2, sprite2, "smt");
            Assert.True(string.Join(",", engine2.GetAllStyleNames()) == "beta,alpha,gamma", "重排后顺序应保存到磁盘并保持");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void NormalizeMovesKeysToCompliantFile()
    {
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var engine = new GalaxyStyleEngine(adapter, image, sprite, "smt");

            engine.AddStyle("mystyle", new GalaxyShapeParameters());

            // 模拟"旧命名文件"状态：把键手动移到 localisation/english/old.yml（本 mod 内）
            string compliant = "localisation/english/smt_style_l_english.yml";
            string legacy = "localisation/english/old.yml";
            adapter.RemoveLocalisationEntry("english", compliant, "mystyle");
            adapter.RemoveLocalisationEntry("english", compliant, "mystyle_desc");
            adapter.AddLocalisationEntry("english", legacy, "mystyle", "My Style", rootB);
            adapter.AddLocalisationEntry("english", legacy, "mystyle_desc", "Desc", rootB);

            // 规整化（仅内存）：键应迁移回合规文件
            engine.NormalizeAllKeys();

            var index = adapter.GetLocalisationKeyFiles("english");
            Assert.True(index.TryGetValue("mystyle", out var nameFile)
                        && string.Equals(nameFile, compliant, StringComparison.OrdinalIgnoreCase),
                "规整化后样式键应在合规文件 style_l_english.yml");
            Assert.True(index.TryGetValue("mystyle_desc", out var descFile)
                        && string.Equals(descFile, compliant, StringComparison.OrdinalIgnoreCase),
                "规整化后 desc 键应在合规文件 style_l_english.yml");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void SaveSyncsSettingsToGalaxyConfig()
    {
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var config = new Stellaris.Engine.LocalConfigManager.LocalConfigManager(Path.Combine(rootB, ".smt"));
            var engine = new GalaxyStyleEngine(adapter, image, sprite, "smt", configManager: config);

            engine.AddStyle("test_style", new GalaxyShapeParameters());
            var result = engine.SaveAllStyles();
            Assert.True(result.WriteSuccess, "保存应成功");

            // 保存后 galaxy.json（银河类别）应生成，含导出参数与样式开关
            string cfgPath = Path.Combine(rootB, ".smt", "galaxy.json");
            Assert.True(File.Exists(cfgPath), "保存应生成 galaxy.json（设置归位银河类别）");
            string content = File.ReadAllText(cfgPath);
            Assert.Contains(content, "outer_width", "galaxy.json 应含 global.preview.outer_width");
            Assert.Contains(content, "test_style", "galaxy.json 应含样式开关节点");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void SaveMigratesSpritesToCompliantGfxFile()
    {
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            // 错误文件名的 .gfx 在 mod 目录（含正确内容的 spriteType——模拟历史遗留）
            string legacyGfx = Path.Combine(rootB, "interface", "game_setup", "more_galaxy_setup_xxc.gfx");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyGfx)!);
            File.WriteAllText(legacyGfx,
                "spriteTypes = {\n" +
                "\tspriteType = {\n" +
                "\t\tname = \"GFX_galaxy_preview_mystyle\"\n" +
                "\t\ttexturefile = \"gfx/interface/game_setup/galaxy_preview/smt_mystyle.dds\"\n" +
                "\t}\n" +
                "\tspriteType = {\n" +
                "\t\tname = \"GFX_galaxy_button_mystyle2\"\n" +
                "\t\ttexturefile = \"gfx/interface/game_setup/galaxy_button/smt_mystyle2.dds\"\n" +
                "\t\tnoOfFrames = 3\n" +
                "\t}\n" +
                "}\n");
            // 样式表（引擎加载出 mystyle 样式，其期望精灵名为 GFX_galaxy_preview_smt_mystyle）
            string galaxyShapesFull = Path.Combine(rootB, "map", "galaxy", "galaxy_shapes.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(galaxyShapesFull)!);
            File.WriteAllText(galaxyShapesFull, "mystyle = {\n\tcore_radius_perc = 0.2\n}\n");

            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var engine = new GalaxyStyleEngine(adapter, image, sprite, "smt");

            // 新行为：保存默认不做 gfx 位置迁移；先显式规整化（内存迁移），再保存
            sprite.NormalizeSpriteFiles("interface/game_setup/smt_galaxy_shapes.gfx");
            var result = engine.SaveAllStyles();
            Assert.True(result.WriteSuccess, "保存应成功");

            string compliantGfx = Path.Combine(rootB, "interface", "game_setup", "smt_galaxy_shapes.gfx");
            Assert.True(File.Exists(compliantGfx), "正确文件 smt_galaxy_shapes.gfx 应被创建");
            string compliantContent = File.ReadAllText(compliantGfx);
            // 批量合并：两个精灵都要保留（不能互相覆盖）
            Assert.Contains(compliantContent, "GFX_galaxy_preview_mystyle",
                "正确文件应含第一个精灵声明");
            Assert.Contains(compliantContent, "GFX_galaxy_button_mystyle2",
                "正确文件应含第二个精灵声明（批量合并不互相覆盖）");
            Assert.Contains(compliantContent, "noOfFrames = 3",
                "精灵的 noOfFrames 字段应保留");

            // 错误文件应被写空头（无 spriteType 残留）
            Assert.True(File.Exists(legacyGfx), "错误文件应保留（写空头而非删除）");
            string legacyContent = File.ReadAllText(legacyGfx);
            Assert.False(legacyContent.Contains("GFX_galaxy_preview_mystyle", StringComparison.Ordinal),
                "错误文件不应残留已迁移的精灵");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void RemoveSpriteTypeByTupleCondition()
    {
        // 纯内存验证：ResolvePath 的 ("name", value) 元组选择器应定位并删除 spriteType 子块
        var adapter = new StellarisAdapter();
        string gfx = "interface/game_setup/x.gfx";
        adapter.CreateEmptyFileInMemory(gfx, FileCategory.Config);
        adapter.AddConfigNode(gfx, new List<object> { "spriteTypes" },
            new AstNode
            {
                Type = NodeType.Block,
                Key = "spriteType",
                Children = new List<AstNode>
                {
                    new AstNode { Type = NodeType.Simple, Key = "name", Value = "GFX_a", IsQuoted = true },
                    new AstNode { Type = NodeType.Simple, Key = "texturefile", Value = "a.dds", IsQuoted = true }
                }
            });

        adapter.RemoveConfigNode(gfx, new List<object> { "spriteTypes", ("name", "GFX_a") });

        var r = adapter.GetConfig(gfx);
        int sprites = r?.RootNodes.Sum(n => n.Children.Count(c => c.Key == "spriteType")) ?? -1;
        Assert.Equal(0, sprites, "RemoveConfigNode 应按 (name, value) 删除 spriteType 子块");
    }

    [Test]
    public void AddConfigNodeWithPredicateAddsMultipleBlocks()
    {
        // 条件化 AddConfigNode：Block 第一层 name 字段判定"已存在"——
        // 两个 Key 相同（spriteType）但 name 不同的 Block 都应添加，互不覆盖。
        var adapter = new StellarisAdapter();
        string gfx = "interface/game_setup/x.gfx";
        adapter.CreateEmptyFileInMemory(gfx, FileCategory.Config);

        for (int i = 0; i < 2; i++)
        {
            string spriteName = $"GFX_{i}";
            var block = new AstNode
            {
                Type = NodeType.Block,
                Key = "spriteType",
                Children = new List<AstNode>
                {
                    new AstNode { Type = NodeType.Simple, Key = "name", Value = spriteName, IsQuoted = true },
                    new AstNode { Type = NodeType.Simple, Key = "texturefile", Value = $"{i}.dds", IsQuoted = true }
                }
            };
            adapter.AddConfigNode(gfx, new List<object> { "spriteTypes" }, block,
                existingPredicate: node => node.Type == NodeType.Block
                    && node.Children.Any(c => c.Type == NodeType.Simple && c.Key == "name" && Equals(c.Value, spriteName)));
        }

        var r = adapter.GetConfig(gfx);
        int count = r?.RootNodes.FirstOrDefault()?.Children.Count(c => c.Key == "spriteType") ?? -1;
        Assert.Equal(2, count, "条件化 AddConfigNode 应允许添加两个同名 Key 但不同 name 的 Block");
    }

    [Test]
    public void SaveKeepsUserCheckedExportSwitches()
    {
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var config = new Stellaris.Engine.LocalConfigManager.LocalConfigManager(Path.Combine(rootB, ".smt"));
            var engine = new GalaxyStyleEngine(adapter, image, sprite, "smt", configManager: config);

            engine.AddStyle("mystyle", new GalaxyShapeParameters());
            // 模拟用户勾选导出（写 galaxy.json）
            engine.SetStyleSwitch("mystyle", "preview", true);
            engine.SetStyleSwitch("mystyle", "icon", true);

            // 保存（即使 useLocalConfig=false，Sync 也不得用假 false 覆盖用户勾选）
            var result = engine.SaveAllStyles(useLocalConfig: false);
            Assert.True(result.WriteSuccess, "保存应成功");

            Assert.True(engine.GetStyleSwitch("mystyle", "preview") == true,
                "保存后 preview 开关应保持用户勾选的 true");
            Assert.True(engine.GetStyleSwitch("mystyle", "icon") == true,
                "保存后 icon 开关应保持用户勾选的 true");

            string cfgPath = Path.Combine(rootB, ".smt", "galaxy.json");
            string content = File.ReadAllText(cfgPath);
            Assert.Contains(content, "\"preview\": true", "galaxy.json 应保留 preview=true");
            Assert.Contains(content, "\"icon\": true", "galaxy.json 应保留 icon=true");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void SetBatchWritesColorArray()
    {
        // LocalConfigManager.SetBatch 应支持数组值（RGBA 颜色 int[] → JsonArray）
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            var config = new Stellaris.Engine.LocalConfigManager.LocalConfigManager(Path.Combine(rootB, ".smt"));
            config.SetBatch("galaxy", new Dictionary<string, object>
            {
                ["global.preview.background_color"] = new int[] { 12, 34, 56, 78 },
                ["global.preview.outer_width"] = 999
            });

            var got = config.Get("galaxy", "global.preview.background_color");
            Assert.NotNull(got, "颜色数组应可读回");
            var arr = got as System.Text.Json.Nodes.JsonArray;
            Assert.NotNull(arr, "颜色数组应为 JsonArray");
            Assert.Equal(12, (int)arr![0]!, "背景色 R 分量正确");
            Assert.Equal(78, (int)arr![3]!, "背景色 A 分量正确");

            // 保存后 galaxy.json 含颜色数组（Sync 的颜色写入也走此路径）
            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var engine = new GalaxyStyleEngine(adapter, image, sprite, "smt", configManager: config);
            engine.AddStyle("mystyle", new GalaxyShapeParameters());
            engine.SaveAllStyles(useLocalConfig: true);

            string content = File.ReadAllText(Path.Combine(rootB, ".smt", "galaxy.json"));
            Assert.Contains(content, "12", "galaxy.json 应含颜色数组 R 分量");
            Assert.Contains(content, "78", "galaxy.json 应含颜色数组 A 分量");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void LocalisationLogicalAndDisplayValues()
    {
        // 本地化逻辑值（原文含 $var$）与显示值（展开后）应分离
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            File.WriteAllText(Path.Combine(rootB, "localisation", "english", "sample_l_english.yml"),
                "l_english:\n galaxy_name: \"Galaxy\"\n greeting: \"Welcome to $galaxy_name$\"\n");

            var adapter = BuildAdapter(rootA, rootB);

            string display = adapter.GetLocalisedText("greeting", "english") ?? "";
            string logical = adapter.GetLocalisedLogicalText("greeting", "english") ?? "";
            Assert.Contains(display, "Welcome to Galaxy", "显示值应展开 $galaxy_name$");
            Assert.Contains(logical, "$galaxy_name$", "逻辑值应保留原文（含 $var$ 占位）");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void SaveKeepsLogicalValueOnDisk()
    {
        // 本地化落盘应写逻辑值（原文含 $var$），而非展开后的显示值
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            Directory.CreateDirectory(Path.Combine(rootB, "localisation", "english"));
            File.WriteAllText(Path.Combine(rootB, "localisation", "english", "smt_style_l_english.yml"),
                "l_english:\n mystyle: \"My $KIND$ Style\"\n KIND: \"Galaxy\"\n");
            string galaxyShapesFull = Path.Combine(rootB, "map", "galaxy", "galaxy_shapes.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(galaxyShapesFull)!);
            File.WriteAllText(galaxyShapesFull, "mystyle = {\n\tcore_radius_perc = 0.2\n}\n");

            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var engine = new GalaxyStyleEngine(adapter, image, sprite, "smt");

            var result = engine.SaveAllStyles();
            Assert.True(result.WriteSuccess, "保存应成功");

            string content = File.ReadAllText(Path.Combine(rootB, "localisation", "english", "smt_style_l_english.yml"));
            Assert.Contains(content, "$KIND$", "落盘应保留逻辑值（原文，含 $var$ 占位）");
            Assert.False(content.Contains("My Galaxy Style"), "落盘不应写展开后的显示值");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void ExtraCrisisStrengthParsed()
    {
        // setup_scenario 的 extra_crisis_strength 应被解析进 DynamicScenario.ExtraCrisisStrength
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            Directory.CreateDirectory(Path.Combine(rootB, "map", "setup_scenarios"));
            File.WriteAllText(Path.Combine(rootB, "map", "setup_scenarios", "00.txt"),
                "setup_scenario = {\n name = \"00\"\n extra_crisis_strength = { 10 25 50 100 }\n}\n");

            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var styleEngine = new Stellaris.Engine.GalaxyStyle.GalaxyStyleEngine(adapter, image, sprite, "smt");
            var mapEngine = new Stellaris.Engine.GalaxyMap.GalaxyMapEngine(adapter, styleEngine, image, sprite, "smt");
            mapEngine.ScanAll();

            var s = mapEngine.GetDynamicScenario("00");
            Assert.NotNull(s, "应能读取动态地图 00");
            Assert.Equal(4, s!.ExtraCrisisStrength.Count, "extra_crisis_strength 应解析出 4 个强度");
            Assert.True(Math.Abs(s.ExtraCrisisStrength[0] - 10) < 0.001, "第一个强度应为 10");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void AddStaticScenarioWorks()
    {
        // 新建静态地图：AddStaticScenario 后应存在于静态字典，且可读取（占位样式注册不应抛）
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var styleEngine = new Stellaris.Engine.GalaxyStyle.GalaxyStyleEngine(adapter, image, sprite, "smt");
            var mapEngine = new Stellaris.Engine.GalaxyMap.GalaxyMapEngine(adapter, styleEngine, image, sprite, "smt");
            mapEngine.ScanAll();

            mapEngine.AddStaticScenario(new Stellaris.Engine.GalaxyMap.StaticScenario { Name = "test_static", SupportedShapes = new List<string>() });

            Assert.NotNull(mapEngine.GetStaticScenario("test_static"), "新建静态地图应可读取");
            Assert.True(mapEngine.StaticScenarios.ContainsKey("test_static"), "静态字典应含新项");
            // 占位样式应注册（供预览/选择）
            Assert.True(styleEngine.GetStyle("test_static") != null, "占位样式应已注册");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void SaveRoundTripsCrisisStrength()
    {
        // 保存→重读：crisis_strength 与 extra_crisis_strength 不应丢失
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            Directory.CreateDirectory(Path.Combine(rootB, "map", "setup_scenarios"));
            File.WriteAllText(Path.Combine(rootB, "map", "setup_scenarios", "00.txt"),
                "setup_scenario = {\n name = \"00\"\n crisis_strength = 2.5\n extra_crisis_strength = { 10 25 50 }\n}\n");

            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var styleEngine = new Stellaris.Engine.GalaxyStyle.GalaxyStyleEngine(adapter, image, sprite, "smt");
            var mapEngine = new Stellaris.Engine.GalaxyMap.GalaxyMapEngine(adapter, styleEngine, image, sprite, "smt");
            mapEngine.ScanAll();
            var s = mapEngine.GetDynamicScenario("00");
            Assert.NotNull(s, "应能读取动态地图 00");
            Assert.True(Math.Abs(s!.CrisisStrength - 2.5) < 0.001, "crisis_strength 应解析为 2.5");
            Assert.Equal(3, s.ExtraCrisisStrength.Count, "extra 应为 3 个");

            // 保存（写入文件）
            Assert.True(mapEngine.SaveAllScenarios(), "保存应成功");
            string written = File.ReadAllText(Path.Combine(rootB, "map", "setup_scenarios", "00.txt"));
            Assert.Contains(written, "crisis_strength = 2.5", "写盘应含 crisis_strength");
            Assert.Contains(written, "extra_crisis_strength", "写盘应含 extra_crisis_strength");
            System.Console.WriteLine("WRITTEN>>>\n" + written + "\n<<<");

            // 重读（重建引擎 + 重新扫描磁盘）
            var adapter2 = BuildAdapter(rootA, rootB);
            var image2 = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite2 = new SpriteManagementEngine(adapter2, image2);
            var style2 = new Stellaris.Engine.GalaxyStyle.GalaxyStyleEngine(adapter2, image2, sprite2, "smt");
            var map2 = new Stellaris.Engine.GalaxyMap.GalaxyMapEngine(adapter2, style2, image2, sprite2, "smt");
            map2.ScanAll();
            var s2 = map2.GetDynamicScenario("00");
            Assert.NotNull(s2, "保存后应能重读地图");
            Assert.True(Math.Abs(s2!.CrisisStrength - 2.5) < 0.001, "重读后 crisis_strength 不应丢失");
            Assert.Equal(3, s2.ExtraCrisisStrength.Count, "重读后 extra_crisis_strength 不应丢失");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public void SaveCleansUpLegacyLocalisationFile()
    {
        var (rootA, rootB, tmp) = CreateSandbox();
        try
        {
            // 旧命名本地化文件在 mod 目录（磁盘真实存在，含样式键——模拟历史遗留）
            string legacyFull = Path.Combine(rootB, "localisation", "english", "legacy.yml");
            File.WriteAllText(legacyFull, "l_english:\n mystyle: \"My Style\"\n mystyle_desc: \"Desc\"\n");
            // 样式表在 mod 目录（样式块，引擎加载出 mystyle 样式）
            string galaxyShapesFull = Path.Combine(rootB, "map", "galaxy", "galaxy_shapes.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(galaxyShapesFull)!);
            File.WriteAllText(galaxyShapesFull, "mystyle = {\n\tcore_radius_perc = 0.2\n}\n");

            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var engine = new GalaxyStyleEngine(adapter, image, sprite, "smt");
            Assert.True(engine.GetAllStyleNames().Contains("mystyle"), "引擎应加载出 mystyle 样式");

            // 保存（无本地配置 → 全部样式规整化 → 迁移到合规文件 + 清理旧文件）
            var result = engine.SaveAllStyles();
            Assert.True(result.WriteSuccess, "保存应成功");

            string compliantFull = Path.Combine(rootB, "localisation", "english", "smt_style_l_english.yml");
            Assert.True(File.Exists(compliantFull), "合规文件应生成");
            Assert.Contains(File.ReadAllText(compliantFull), "mystyle: \"My Style\"",
                "合规文件应含迁移后的键与原文值");

            // 旧文件应被写空头清理（磁盘上不再有旧键）
            Assert.True(File.Exists(legacyFull), "旧文件应保留（写空头而非删除）");
            string legacyContent = File.ReadAllText(legacyFull);
            Assert.False(legacyContent.Contains("mystyle", StringComparison.Ordinal),
                "旧文件不应残留已迁移的键");
            Assert.Contains(legacyContent, "l_english", "旧文件应为空头（l_english:）");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }    }


        [Test]
        public void SaveRemovesDeletedStyleBlockFromDisk()
        {
            // 删除样式 → 保存 → 磁盘样式文件不再含该样式块（之前 bug：被删样式不在 _table.Keys，
            // SaveToAdapter 的"保留非样式块"逻辑把它的旧块也保留了 → 重启又出现）。
            var (rootA, rootB, tmp) = CreateSandbox();
            var adapter = BuildAdapter(rootA, rootB);
            var image = new ImageAssetEngine(new List<string> { rootA, rootB });
            var sprite = new SpriteManagementEngine(adapter, image);
            var engine = new GalaxyStyleEngine(adapter, image, sprite, "smt");

            engine.AddStyle("aaa", new GalaxyShapeParameters());
            engine.AddStyle("zzz", new GalaxyShapeParameters());
            engine.SaveAllStyles();
            Assert.True(adapter.GetConfig("map/galaxy/galaxy_shapes.txt")!.RootNodes.Any(n => n.Key == "aaa"), "aaa 已保存");

            engine.DeleteStyle("aaa");
            engine.SaveAllStyles();
            Assert.False(adapter.GetConfig("map/galaxy/galaxy_shapes.txt")!.RootNodes.Any(n => n.Key == "aaa"), "删除后保存，磁盘块应消失");
            Assert.True(adapter.GetConfig("map/galaxy/galaxy_shapes.txt")!.RootNodes.Any(n => n.Key == "zzz"), "其余样式块应保留");
        }

        [Test]
        public void FindStringValuesReturnsCrudPathThatRemovesLeaf()
        {
            // 底层泛用性验证：FindStringValues 返回的 targetPath（第 1 位=次数，后面=(文件,targetPath)）
            // 可直接用于 RemoveConfigNode 删除目标叶（CRUD 支持 string Key / 元组 / int 索引"第几个"）。
            // 仿 RemoveSpriteTypeByTupleCondition：spriteType 块 + name Simple（值 = 精灵名）
            var adapter = new StellarisAdapter();
            string gfx = "interface/game_setup/fv_test.gfx";
            adapter.CreateEmptyFileInMemory(gfx, FileCategory.Config);
            adapter.AddConfigNode(gfx, new List<object> { "spriteTypes" },
                new AstNode
                {
                    Type = NodeType.Block,
                    Key = "spriteType",
                    Children = new List<AstNode>
                    {
                        new AstNode { Type = NodeType.Simple, Key = "name", Value = "GFX_galaxy_preview_x", IsQuoted = true }
                    }
                });

            var hits = adapter.FindStringValues("GFX_galaxy_preview_x");
            Assert.True(hits.Count >= 2 && (int)hits[0] == 1, "应找到 1 次且第 1 位为次数");
            var (file, path) = ((string, List<object>))hits[1];
            Assert.True(path.Count >= 3 && path[^1] is int && path[^2] is string, "目标叶应为 Key+int（第几个）");

            // 混合链验证 A：标签 + 数值（第几个）+ 标签——加第 2 个 spriteType 块，
            // 用 spriteTypes -> spriteType -> 1 -> name（int 选第 2 个块后下钻 name）删除其 name。
            adapter.AddConfigNode(gfx, new List<object> { "spriteTypes" },
                new AstNode
                {
                    Type = NodeType.Block,
                    Key = "spriteType",
                    Children = new List<AstNode>
                    {
                        new AstNode { Type = NodeType.Simple, Key = "name", Value = "GFX_galaxy_preview_y", IsQuoted = true }
                    }
                });
            var mixed = new List<object> { "spriteTypes", "spriteType", 0, "name" };
            adapter.RemoveConfigNode(gfx, mixed);
            var mixedRemaining = adapter.FindStringValues("GFX_galaxy_preview_y");
            System.Console.WriteLine("y count after: " + (int)mixedRemaining[0]);
            Assert.True((int)mixedRemaining[0] == 0, "混合链（标签+数值+标签）应能定位删除");

            // 混合链验证 B：标签 + 元组（数值判断）——元组匹配"含 name=z 字段的子块"并删除该块。
            adapter.AddConfigNode(gfx, new List<object> { "spriteTypes" },
                new AstNode
                {
                    Type = NodeType.Block,
                    Key = "spriteType",
                    Children = new List<AstNode>
                    {
                        new AstNode { Type = NodeType.Simple, Key = "name", Value = "GFX_galaxy_preview_z", IsQuoted = true }
                    }
                });
            var mixedTuple = new List<object> { "spriteTypes", ("name", "GFX_galaxy_preview_z") };
            adapter.RemoveConfigNode(gfx, mixedTuple);
            var tupleRemaining = adapter.FindStringValues("GFX_galaxy_preview_z");
            Assert.True((int)tupleRemaining[0] == 0, "混合链（标签+元组）应能定位删除");
            System.Console.WriteLine("FV path: " + string.Join(" -> ", path));

            adapter.RemoveConfigNode(file, path);
            var remaining = adapter.FindStringValues("GFX_galaxy_preview_x");
            Assert.True((int)remaining[0] == 0, "用返回的 targetPath 删除后应再无该值");
        }
    }

