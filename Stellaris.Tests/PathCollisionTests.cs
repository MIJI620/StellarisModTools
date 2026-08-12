using System;
using System.IO;
using System.Linq;
using Stellaris.Parser;

namespace Stellaris.Tests;

/// <summary>SA 相对路径撞击扫描：撞击表在 ScanAll 时制作；撞击扫描（GetCollisionAsts）上层主动开启、
/// 每个 root 独立解析（与常规 _configResults 隔离——常规仍按覆盖规则合并）。</summary>
public sealed class PathCollisionTests
{
    [Test]
    public void ScanAllBuildsCollisionTableAndCollisionAstsAreIndependent()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "smt_coll_" + Guid.NewGuid().ToString("N"));
        var baseRoot = Path.Combine(tmp, "base");
        var modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(baseRoot, "common", "edicts"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "edicts"));

        // 两个 root 撞同一个相对路径——内容不同
        Write(baseRoot, "common/edicts/test.txt", "base_edict = {\n icon = \"GFX_base\" \n}\n");
        Write(modRoot, "common/edicts/test.txt", "mod_edict = {\n icon = \"GFX_mod\" \n}\n");

        var adapter = new StellarisAdapter();
        adapter.AddRoot(baseRoot);
        adapter.AddRoot(modRoot);
        adapter.ScanAll();

        // 1) 撞击表在 ScanAll 时制作：该相对路径有 2 个 root
        Assert.True(adapter.PathCollisions.ContainsKey("common/edicts/test.txt"), "撞击表应记录撞同一相对路径的条目");
        var entries = adapter.PathCollisions["common/edicts/test.txt"];
        Assert.Equal(2, entries.Count, "撞击条目 = 2 个 root");
        Assert.True(entries.Any(e => e.FullPath.Contains("base")), "含 base root 绝对路径");
        Assert.True(entries.Any(e => e.FullPath.Contains("mod")), "含 mod root 绝对路径");

        // 2) 撞击扫描：上层主动开启——每个 root 独立解析（不合并）
        var asts = adapter.GetCollisionAsts("common/edicts/test.txt");
        Assert.Equal(2, asts.Count, "撞击扫描返回 2 个独立 AST");
        var baseAst = asts.First(a => a.Root == baseRoot).Ast;
        var modAst = asts.First(a => a.Root == modRoot).Ast;
        Assert.True(baseAst.RootNodes.Any(n => n.Key == "base_edict"), "base AST 含 base_edict");
        Assert.True(modAst.RootNodes.Any(n => n.Key == "mod_edict"), "mod AST 含 mod_edict");
        Assert.False(baseAst.RootNodes.Any(n => n.Key == "mod_edict"), "base AST 不含 mod_edict（独立解析不合并）");
    }

    [Test]
    public void RegularScanStillMergesByOverwriteRule()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "smt_coll2_" + Guid.NewGuid().ToString("N"));
        var baseRoot = Path.Combine(tmp, "base");
        var modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(baseRoot, "common", "edicts"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "edicts"));
        Write(baseRoot, "common/edicts/test.txt", "base_edict = {}\n");
        Write(modRoot, "common/edicts/test.txt", "mod_edict = {}\n");

        var adapter = new StellarisAdapter();
        adapter.AddRoot(baseRoot);
        adapter.AddRoot(modRoot);
        adapter.ScanAll();

        // 常规 _configResults 仍按覆盖规则：后 root（mod）覆盖——只保留 1 个 AST
        var merged = adapter.GetConfig("common/edicts/test.txt");
        Assert.NotNull(merged, "常规扫描仍按覆盖规则记录 AST");
        Assert.True(merged!.RootNodes.Any(n => n.Key == "mod_edict"), "常规合并取后 root（mod）");
        Assert.False(merged.RootNodes.Any(n => n.Key == "base_edict"), "常规合并不含 base 内容");
        Assert.Equal(1, adapter.GetAllConfigs()["common/edicts/test.txt"].RootNodes.Count(n => n.Key == "mod_edict" || n.Key == "base_edict"),
            "常规结果只含 1 个条目（隔离——撞击扫描不影响常规）");
    }

    [Test]
    public void CollisionAstsDoNotMutateInternalState()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "smt_coll3_" + Guid.NewGuid().ToString("N"));
        var baseRoot = Path.Combine(tmp, "base");
        var modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(baseRoot, "common", "edicts"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "edicts"));
        Write(baseRoot, "common/edicts/test.txt", "base_edict = {}\n");
        Write(modRoot, "common/edicts/test.txt", "mod_edict = {}\n");

        var adapter = new StellarisAdapter();
        adapter.AddRoot(baseRoot);
        adapter.AddRoot(modRoot);
        adapter.ScanAll();

        var before = adapter.GetAllConfigs().Count;
        var asts = adapter.GetCollisionAsts("common/edicts/test.txt");
        Assert.Equal(2, asts.Count, "撞击扫描返回 2 个");
        Assert.Equal(before, adapter.GetAllConfigs().Count, "撞击扫描不写入常规状态（隔离）");

        // 不存在的相对路径 → 空列表
        Assert.Equal(0, adapter.GetCollisionAsts("common/edicts/nope.txt").Count, "未撞击路径返回空");
    }

    private static void Write(string root, string relPath, string content)
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
