using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Engine.StrategicResource;
using Stellaris.Parser;

namespace Stellaris.Tests;

/// <summary>战略资源引擎：固定路径撞击扫描 + 顶层 key 合并超大表 + 同 key 字段多行（标注 root）。</summary>
public sealed class StrategicResourceTests
{
    [Test]
    public void MergesByTopLevelKeyWithPerRootFieldRows()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "smt_res_" + Guid.NewGuid().ToString("N"));
        var baseRoot = Path.Combine(tmp, "base");
        var modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(baseRoot, "common", "strategic_resources"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "strategic_resources"));

        // 两个 root 撞同一路径——同名资源（sr_zro）+ 各自独有资源
        Write(baseRoot, "common/strategic_resources/00_strategic_resources.txt",
            "sr_zro = {\n color = { 0.1 0.2 0.3 }\n cost = 10\n}\nsr_base_only = {\n cost = 5\n}\n");
        Write(modRoot, "common/strategic_resources/00_strategic_resources.txt",
            "sr_zro = {\n cost = 20\n}\nsr_mod_only = {\n cost = 7\n}\n");

        var adapter = new StellarisAdapter();
        adapter.AddRoot(baseRoot);
        adapter.AddRoot(modRoot);
        adapter.ScanAll();
        var engine = new StrategicResourceEngine(adapter, NullLogger.Instance);
        engine.ScanAll();

        var entries = engine.GetEntries();
        // 顶层 key 合并：sr_zro（两 root）+ sr_base_only + sr_mod_only = 3 条
        Assert.Equal(3, entries.Count, "超大表按顶层 key 合并为 3 条（同 key 合一条）");

        var zro = entries.First(e => e.Key == "sr_zro");
        Assert.Equal(2, zro.Roots.Count, "sr_zro 出现在 2 个 root");
        // 同 key 字段合并为一行：cost 1 行（2 个方案各记 root）——color 仅 base 有（1 方案）
        var costRow = zro.Rows.First(r => r.FieldKey == "cost");
        Assert.Equal(2, costRow.Variants.Count, "cost 合并 1 行 + 2 个方案");
        Assert.True(costRow.Variants.Any(v => v.Root.Contains("base") && v.DisplayValue == "10"), "base 方案 cost=10");
        Assert.True(costRow.Variants.Any(v => v.Root.Contains("mod") && v.DisplayValue == "20"), "mod 方案 cost=20");
        Assert.Equal(1, zro.Rows.Count(r => r.FieldKey == "color"), "color 仅 base 有——1 方案");
        Assert.True(zro.Rows.First(r => r.FieldKey == "color").IsBlock, "color 是 Block");
        // Block 细节不省：color 显示子内容
        Assert.True(zro.Rows.First(r => r.FieldKey == "color").Variants[0].DisplayValue.Contains("0.1"), "Block 细节不省——显示子内容");

        Assert.Equal(1, entries.First(e => e.Key == "sr_base_only").Roots.Count, "sr_base_only 仅 base");
        Assert.Equal(1, entries.First(e => e.Key == "sr_mod_only").Roots.Count, "sr_mod_only 仅 mod");
    }

    [Test]
    public void SingleRootFallsBackToRegularConfig()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "smt_res2_" + Guid.NewGuid().ToString("N"));
        var modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "strategic_resources"));
        Write(modRoot, "common/strategic_resources/00_strategic_resources.txt",
            "sr_alpha = {\n cost = 3\n}\n");

        var adapter = new StellarisAdapter();
        adapter.AddRoot(modRoot);
        adapter.ScanAll();
        var engine = new StrategicResourceEngine(adapter, NullLogger.Instance);
        engine.ScanAll();

        var entries = engine.GetEntries();
        Assert.Equal(1, entries.Count, "单 root 常规回退也能读出资源");
        Assert.Equal("sr_alpha", entries[0].Key, "资源 key");
        Assert.Equal("3", entries[0].Rows.First(r => r.FieldKey == "cost").Variants[0].DisplayValue, "cost 值");
    }


    [Test]
    public void SaveAllWritesSelectedVariantToItsRoot()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "smt_res3_" + Guid.NewGuid().ToString("N"));
        var baseRoot = Path.Combine(tmp, "base");
        var modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(baseRoot, "common", "strategic_resources"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "strategic_resources"));
        var baseFile = Path.Combine(baseRoot, "common", "strategic_resources", "00_strategic_resources.txt");
        var modFile = Path.Combine(modRoot, "common", "strategic_resources", "00_strategic_resources.txt");
        Write(baseRoot, "common/strategic_resources/00_strategic_resources.txt", "sr_zro = {\n cost = 10 \n}\n");
        Write(modRoot, "common/strategic_resources/00_strategic_resources.txt", "sr_zro = {\n cost = 20 \n}\n");

        var adapter = new StellarisAdapter();
        adapter.AddRoot(baseRoot);
        adapter.AddRoot(modRoot);
        adapter.ScanAll();
        var engine = new StrategicResourceEngine(adapter, NullLogger.Instance);
        engine.ScanAll();

        var zro = engine.GetEntries().First(e => e.Key == "sr_zro");
        var costRow = zro.Rows.First(r => r.FieldKey == "cost");
        // 选中 base 方案（cost=10）→ 保存写入**最后一个 root（mod）**（用户定：保存写到 Roots 最后一位）
        var baseIdx = costRow.Variants.FindIndex(v => v.Root.Contains("base"));
        costRow.SelectedIndex = baseIdx;
        var (saved, errors) = engine.SaveAll();
        Assert.Equal(1, saved, "保存 1 个文件（最后 root）");
        Assert.Equal(0, errors.Count, "无错误");
        // mod 文件（最后 root）被写入选中方案的值 cost = 10
        Assert.True(File.ReadAllText(modFile).Contains("cost = 10"), "mod 文件被写入选中方案");
    }

    [Test]
    public void GetEntriesInitializesAstsSoSaveWorks()
    {
        // 用户实测：App 未预热 ScanAll 时 GetEntries 只设 _entries 不设 _asts → SaveAll 直接跳过。
        // 回归：仅调 GetEntries（不调 ScanAll）→ SaveAll 必须真正写盘。
        var tmp = Path.Combine(Path.GetTempPath(), "smt_res4_" + Guid.NewGuid().ToString("N"));
        var baseRoot = Path.Combine(tmp, "base");
        var modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(baseRoot, "common", "strategic_resources"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "strategic_resources"));
        var baseFile = Path.Combine(baseRoot, "common", "strategic_resources", "00_strategic_resources.txt");
        var modFile = Path.Combine(modRoot, "common", "strategic_resources", "00_strategic_resources.txt");
        Write(baseRoot, "common/strategic_resources/00_strategic_resources.txt", "sr_zro = {\n cost = 10 \n}\n");
        Write(modRoot, "common/strategic_resources/00_strategic_resources.txt", "sr_zro = {\n cost = 20 \n}\n");

        var adapter = new StellarisAdapter();
        adapter.AddRoot(baseRoot);
        adapter.AddRoot(modRoot);
        adapter.ScanAll();
        var engine = new StrategicResourceEngine(adapter, NullLogger.Instance);
        // 只调 GetEntries（不调 ScanAll）——触发 Lazy 初始化
        var zro = engine.GetEntries().First(e => e.Key == "sr_zro");
        var costRow = zro.Rows.First(r => r.FieldKey == "cost");
        var baseIdx = costRow.Variants.FindIndex(v => v.Root.Contains("base"));
        costRow.SelectedIndex = baseIdx;
        var (saved, errors) = engine.SaveAll();
        Assert.Equal(1, saved, "仅 GetEntries 后 SaveAll 也写盘");
        Assert.Equal(0, errors.Count, "无错误");
        Assert.True(File.ReadAllText(modFile).Contains("cost = 10"), "mod 文件（最后 root）被写入");
    }

    [Test]
    public void SaveAllCreatesFileWhenModHasNoResourceFile()
    {
        // 用户场景：mod 没有 common/strategic_resources（游戏才有）——保存必须创建 mod 文件。
        var tmp = Path.Combine(Path.GetTempPath(), "smt_res5_" + Guid.NewGuid().ToString("N"));
        var baseRoot = Path.Combine(tmp, "base");
        var modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(baseRoot, "common", "strategic_resources"));
        Directory.CreateDirectory(modRoot);
        Write(baseRoot, "common/strategic_resources/00_strategic_resources.txt", "sr_zro = {\n cost = 10 \n}\n");
        // mod 目录完全无 strategic_resources

        var adapter = new StellarisAdapter();
        adapter.AddRoot(baseRoot);
        adapter.AddRoot(modRoot);
        adapter.ScanAll();
        var engine = new StrategicResourceEngine(adapter, NullLogger.Instance);
        engine.GetEntries(); // 触发初始化（mod 无撞击——_asts 不含 mod）
        var (saved, errors) = engine.SaveAll();
        Assert.Equal(1, saved, "保存创建 mod 文件");
        Assert.Equal(0, errors.Count, "无错误");
        var modFile = Path.Combine(modRoot, "common", "strategic_resources", "00_strategic_resources.txt");
        Assert.True(File.Exists(modFile), "mod 文件已创建");
        Assert.True(File.ReadAllText(modFile).Contains("sr_zro"), "mod 文件含资源条目");
    }

    [Test]
    public void SaveAllParsesCustomValueAsAstBlock()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "smt_res4_" + Guid.NewGuid().ToString("N"));
        var modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "strategic_resources"));
        var file = Path.Combine(modRoot, "common", "strategic_resources", "00_strategic_resources.txt");
        Write(modRoot, "common/strategic_resources/00_strategic_resources.txt", "sr_zro = {\n color = { 0.1 0.2 }\n}\n");

        var adapter = new StellarisAdapter();
        adapter.AddRoot(modRoot);
        adapter.ScanAll();
        var engine = new StrategicResourceEngine(adapter, NullLogger.Instance);
        engine.ScanAll();

        var zro = engine.GetEntries().First(e => e.Key == "sr_zro");
        var colorRow = zro.Rows.First(r => r.FieldKey == "color");
        // 自定义值 = 块（重新 AST 解析 → Block 节点，而不是 Simple 原文）
        colorRow.CustomValue = "{ 0.9 0.8 0.7 }";
        var (saved, errors) = engine.SaveAll();
        Assert.Equal(1, saved, "保存 1 个 root 文件");
        Assert.Equal(0, errors.Count, "无错误");
        var content = File.ReadAllText(file);
        Assert.True(content.Contains("0.9"), "自定义块值被解析写入");
        Assert.True(content.Contains("0.8") && content.Contains("0.7"), "块子值完整（实际内容: [" + content + "]）");
    }

    [Test]
    public void SaveAllInvalidCustomValueReportsErrorWithoutWriting()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "smt_res5_" + Guid.NewGuid().ToString("N"));
        var modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "strategic_resources"));
        var file = Path.Combine(modRoot, "common", "strategic_resources", "00_strategic_resources.txt");
        Write(modRoot, "common/strategic_resources/00_strategic_resources.txt", "sr_zro = {\n cost = 10\n}\n");

        var adapter = new StellarisAdapter();
        adapter.AddRoot(modRoot);
        adapter.ScanAll();
        var engine = new StrategicResourceEngine(adapter, NullLogger.Instance);
        engine.ScanAll();

        var zro = engine.GetEntries().First(e => e.Key == "sr_zro");
        var costRow = zro.Rows.First(r => r.FieldKey == "cost");
        // 非法输入（无法扫出合法 Simple/Block——接 key= 后不合法）
        costRow.CustomValue = "((((( 乱写";
        var (saved, errors) = engine.SaveAll();
        // 用户要求：写入的 AST 必须由 UI 内容拼出且合规——不合规绝不写入 → 报错跳过
        Assert.Equal(0, saved, "不合规值 → 保存失败（不写入）");
        Assert.Equal(1, errors.Count, "报出 1 个不合规错误");
        Assert.True(errors[0].Contains("不合规"), "错误信息说明不合规");
        // 文件保持原值（没有写入非法内容）
        Assert.True(File.ReadAllText(file).Contains("cost = 10"), "文件保留原值，未写入非法内容");
        Assert.False(File.ReadAllText(file).Contains("乱写"), "文件不含非法输入");
    }

    private static void Write(string root, string relPath, string content)
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
