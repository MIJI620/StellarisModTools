using Stellaris.Parser;
using Stellaris.Parser.Rules;

namespace Stellaris.Tests;

/// <summary>解析层覆盖规则：同名文件（多 root）——只读一次 → 最早 root 生效；其他 → 后读覆盖（后 root）。</summary>
public class AdapterOverwriteRuleTests
{
    private static (string Base, string Mod, string Tmp) CreateSandbox()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "smt_ow_" + Guid.NewGuid().ToString("N"));
        string baseRoot = Path.Combine(tmp, "base");
        string modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(baseRoot, "common", "static_modifiers"));
        Directory.CreateDirectory(Path.Combine(baseRoot, "common", "buildings"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "static_modifiers"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "buildings"));
        return (baseRoot, modRoot, tmp);
    }

    private static RulesReader CreateRules(string tmp)
    {
        string rulesDir = Path.Combine(tmp, "rules");
        Directory.CreateDirectory(rulesDir);
        File.WriteAllText(Path.Combine(rulesDir, "overwrite_rules.json"),
            "{\n  \"overwrite_rules\": {\n    \"static_modifiers\": \"只读一次\",\n    \"buildings\": \"后读覆盖\"\n  }\n}\n");
        return new RulesReader(rulesDir);
    }

    [Test]
    public void ReadOnceUsesEarliestRoot()
    {
        var (b, m, tmp) = CreateSandbox();
        File.WriteAllText(Path.Combine(b, "common", "static_modifiers", "00_test.txt"), "base_origin = {\n x = 1\n }\n");
        File.WriteAllText(Path.Combine(m, "common", "static_modifiers", "00_test.txt"), "mod_origin = {\n y = 2\n }\n");
        var adapter = new StellarisAdapter { Rules = CreateRules(tmp) };
        adapter.AddRoot(b);
        adapter.AddRoot(m);
        adapter.ScanAll();

        // 只读一次 → 最早 root（base）生效——GetFileRoot 应为 base
        Assert.Equal(b, adapter.GetFileRoot("common/static_modifiers/00_test.txt"), "只读一次应取最早 root（base）");
        var cfg = adapter.GetConfig("common/static_modifiers/00_test.txt");
        Assert.NotNull(cfg, "文件应被解析");
    }

    [Test]
    public void GameMarkedRootSkippedForReadOnce()
    {
        var (b, m, tmp) = CreateSandbox();
        // base 标记为"游戏"——只读一次（static_modifiers）跳过它——mod 优先
        File.WriteAllText(Path.Combine(b, "common", "static_modifiers", "00_test.txt"), "game_origin = {\n x = 1\n }\n");
        File.WriteAllText(Path.Combine(m, "common", "static_modifiers", "00_test.txt"), "mod_origin = {\n y = 2\n }\n");
        var adapter = new StellarisAdapter { Rules = CreateRules(tmp), GameRoot = b };
        adapter.AddRoot(b);
        adapter.AddRoot(m);
        adapter.ScanAll();

        Assert.Equal(m, adapter.GetFileRoot("common/static_modifiers/00_test.txt"),
            "标记为游戏不算最早——只读一次应优先读它之后的 root（mod）");
    }

    [Test]
    public void GameMarkedRootFallsBackWhenNoOtherRoot()
    {
        var (b, m, tmp) = CreateSandbox();
        // 只有游戏 root（标记）——无其他 root 的同名文件——回退到游戏
        File.WriteAllText(Path.Combine(b, "common", "static_modifiers", "00_test.txt"), "game_origin = {\n x = 1\n }\n");
        var adapter = new StellarisAdapter { Rules = CreateRules(tmp), GameRoot = b };
        adapter.AddRoot(b);
        adapter.ScanAll();

        Assert.Equal(b, adapter.GetFileRoot("common/static_modifiers/00_test.txt"),
            "只有游戏 root 时只读一次应回退到游戏");
    }

    [Test]
    public void LastOverwriteUsesLastRoot()
    {
        var (b, m, tmp) = CreateSandbox();
        File.WriteAllText(Path.Combine(b, "common", "buildings", "00_test.txt"), "base_b = {\n x = 1\n }\n");
        File.WriteAllText(Path.Combine(m, "common", "buildings", "00_test.txt"), "mod_b = {\n y = 2\n }\n");
        var adapter = new StellarisAdapter { Rules = CreateRules(tmp) };
        adapter.AddRoot(b);
        adapter.AddRoot(m);
        adapter.ScanAll();

        // 后读覆盖 → 最后 root（mod）生效
        Assert.Equal(m, adapter.GetFileRoot("common/buildings/00_test.txt"), "后读覆盖应取最后 root（mod）");
    }
}
