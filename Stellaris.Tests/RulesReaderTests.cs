using Stellaris.Parser.Rules;

namespace Stellaris.Tests;

/// <summary>规则专用读取器（RulesReader）：所有规则统一从这里读取——排除规则 + 覆盖规则。</summary>
public class RulesReaderTests
{
    private static string CreateRulesDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "smt_rules_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public void ReadsModifierExclusions()
    {
        string dir = CreateRulesDir();
        File.WriteAllText(Path.Combine(dir, "modifier_exclusions.json"),
            "{\n"
            + "  \"exclude_keys\": {\"weight\": [1], \"random_list\": [2]},\n"
            + "  \"exclude_keywords\": [\"$\"],\n"
            + "  \"exclude_exact\": [\"add\", \"factor\"],\n"
            + "  \"exclude_values\": [\"yes\", \"no\"]\n"
            + "}\n");
        var reader = new RulesReader(dir);

        Assert.True(reader.ExcludeKeys.ContainsKey("weight"), "exclude_keys 应含 weight");
        Assert.Equal(1, reader.ExcludeKeys["weight"][0], "weight 深度应为 1");
        Assert.Equal(2, reader.ExcludeKeys["random_list"][0], "random_list 深度应为 2");
        Assert.True(reader.ExcludeKeywords.Contains("$"), "exclude_keywords 应含 $");
        Assert.True(reader.ExcludeExact.Contains("add") && reader.ExcludeExact.Contains("factor"), "exclude_exact 应含 add/factor");
        Assert.True(reader.ExcludeValues.Contains("yes") && reader.ExcludeValues.Contains("no"), "exclude_values 应含 yes/no");
    }

    [Test]
    public void ReadsOverwriteRules()
    {
        string dir = CreateRulesDir();
        File.WriteAllText(Path.Combine(dir, "overwrite_rules.json"),
            "{\n"
            + "  \"overwrite_rules\": {\n"
            + "    \"static_modifiers\": \"只读一次\",\n"
            + "    \"scripted_modifiers\": \"后读覆盖\",\n"
            + "    \"agendas\": \"后读覆盖\",\n"
            + "    \"events\": \"只读一次\",\n"
            + "    \"interface\": \"后读覆盖\"\n"
            + "  }\n"
            + "}\n");
        var reader = new RulesReader(dir);

        Assert.Equal("只读一次", reader.GetOverwriteRule("static_modifiers"), "static_modifiers 应为只读一次");
        Assert.Equal("后读覆盖", reader.GetOverwriteRule("scripted_modifiers"), "scripted_modifiers 应为后读覆盖");
        Assert.Equal("后读覆盖", reader.GetOverwriteRule("agendas"), "agendas 应为后读覆盖");
        Assert.Equal("只读一次", reader.GetOverwriteRule("events"), "events 应为只读一次");
        Assert.Equal("后读覆盖", reader.GetOverwriteRule("interface"), "interface 应为后读覆盖");
        Assert.Null(reader.GetOverwriteRule("not_listed_folder"), "未配置文件夹应返回 null（= 自动整合）");
    }
}
