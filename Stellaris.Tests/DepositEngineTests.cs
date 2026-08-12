using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Engine.Deposit;
using Stellaris.Parser;

namespace Stellaris.Tests;

public sealed class DepositEngineScansTopLevelBlocksWithLocalisation
{
    [Test]
    public void Run()
    {
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "deposit_test_" + Guid.NewGuid().ToString("N")));
        root.Create();
        try
        {
            var depositsDir = Directory.CreateDirectory(Path.Combine(root.FullName, "common", "deposits")).FullName;
            File.WriteAllText(Path.Combine(depositsDir, "00_test.txt"),
                "# 注释\n d_alpha = {\n category = deposit_cat_energy\n }\n d_beta = {\n category = deposit_cat_minerals\n }\n not_a_deposit = 5\n");
            File.WriteAllText(Path.Combine(depositsDir, "01_more.txt"), "d_gamma = {\n category = deposit_cat_food\n }\n");
            var locDir = Directory.CreateDirectory(Path.Combine(root.FullName, "localisation", "english")).FullName;
            File.WriteAllText(Path.Combine(locDir, "test_l_english.yml"),
                "l_english:\n d_alpha: \"Alpha Deposit\"\n d_beta: \"Beta Deposit\"\n");

            var adapter = new StellarisAdapter();
            adapter.AddRoot(root.FullName);
            adapter.ScanAll();
            var engine = new DepositEngine(adapter, "english");

            var deposits = engine.GetDeposits();
            Assert.Equal(3, deposits.Count, "3 个顶层 block（d_alpha/d_beta/d_gamma）");
            Assert.True(deposits.Any(d => d.Key == "d_alpha" && d.LocName == "Alpha Deposit"), "d_alpha 本地化");
            Assert.True(deposits.Any(d => d.Key == "d_beta" && d.LocName == "Beta Deposit"), "d_beta 本地化");
            Assert.True(deposits.Any(d => d.Key == "d_gamma" && d.LocName == ""), "d_gamma 无本地化 → LocName 空");
            Assert.False(deposits.Any(d => d.Key == "not_a_deposit"), "顶层 Simple 不算 deposit");
            Assert.True(deposits[0].Key == "d_alpha", "按 key 排序");
        }
        finally
        {
            try { root.Delete(true); } catch { }
        }
    }
}
