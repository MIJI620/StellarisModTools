using System;
using System.IO;
using System.Linq;
using Stellaris.Parser;

namespace Stellaris.Tests;

/// <summary>SA root 抗爆炸：AddRoot 遇到不存在的路径 → 跳过（不抛异常、不加入）。</summary>
public sealed class AddRootArmorTests
{
    [Test]
    public void AddRootSkipsMissingDirectoryWithoutThrowing()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "smt_armor_" + Guid.NewGuid().ToString("N"));
        var realRoot = Path.Combine(tmp, "real");
        Directory.CreateDirectory(realRoot);
        var missing = Path.Combine(tmp, "nope_missing_dir");

        var adapter = new StellarisAdapter();
        // 不存在的路径不抛异常
        adapter.AddRoot(missing);
        adapter.AddRoot(realRoot);

        Assert.Equal(1, adapter.Roots.Count, "只有真实存在的 root 被加入");
        Assert.False(adapter.Roots.Any(r => r.Contains("nope_missing_dir")), "不存在的路径被跳过");
        Assert.True(adapter.Roots.Any(r => r.Contains("real")), "真实路径保留");
    }

    [Test]
    public void AddRootSkipsNullOrWhitespace()
    {
        var adapter = new StellarisAdapter();
        adapter.AddRoot("");
        adapter.AddRoot("   ");
        Assert.Equal(0, adapter.Roots.Count, "空/空白路径跳过");
    }
}
