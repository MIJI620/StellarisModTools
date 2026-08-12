using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Parser;

namespace Stellaris.Tests;

/// <summary>加成字典引擎（StaticModifierEngine）：基础 ↔ 自定义双向索引、modifier 引用、忽略规则。</summary>
public class ModifierDictionaryTests
{
    private static (string Base, string Mod, string Tmp) CreateSandbox()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "smt_mod_" + Guid.NewGuid().ToString("N"));
        string baseRoot = Path.Combine(tmp, "base");
        string modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(baseRoot, "localisation", "english"));
        Directory.CreateDirectory(Path.Combine(modRoot, "localisation", "english"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "static_modifiers"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "planet_classes"));
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

    /// <summary>测试排除配置（与分发资源 Resources/modifier_exclusions.json 一致——测试沙盒无该资源，显式传入）。
    /// exclude_keys：key → 深度数组（[1] 查父、[1,2] 查父+祖父、[0] 查自身）。</summary>
    private static readonly Dictionary<string, List<int>> ExcludeKeys = new()
    {
        ["weight"] = new List<int> { 1 },
        ["ai_weight"] = new List<int> { 1 },
        ["ai_chance"] = new List<int> { 1 },
        ["random_list"] = new List<int> { 1, 2 },
        ["weights"] = new List<int> { 1 },
        ["subject_weight"] = new List<int> { 1 },
        ["overlord_weight"] = new List<int> { 1 },
        ["strategy_weight"] = new List<int> { 1 },
        ["network_weight"] = new List<int> { 1 },
        ["event_weight"] = new List<int> { 1 },
        ["custom_storm_ai_weight"] = new List<int> { 1 },
        ["drop_weight"] = new List<int> { 1 },
        ["random_weight"] = new List<int> { 1 },
        ["leader_background_job_weight"] = new List<int> { 1 },
        ["ai_hiring_weight"] = new List<int> { 1 },
        ["ship_selection_weight"] = new List<int> { 1 },
        ["selectable_weight"] = new List<int> { 1 },
        ["country_attraction"] = new List<int> { 1 },
        ["cost"] = new List<int> { 1 },
        ["resource_max"] = new List<int> { 1 },
        ["resource_min"] = new List<int> { 1 },
        ["weight_modifier"] = new List<int> { 1 },
        ["monthly_progress"] = new List<int> { 1 },
        ["tradition_swap"] = new List<int> { 1 },
        ["desired_min"] = new List<int> { 1 },
        ["desired_max"] = new List<int> { 1 },
        ["mean_time_to_happen"] = new List<int> { 1 },
        ["is_difficulty"] = new List<int> { 1 },
        ["ai_acceptance"] = new List<int> { 1 },
        ["weight_multiplier"] = new List<int> { 1 },
        ["AI_wait_days"] = new List<int> { 1 }
    };

    private static readonly string[] ExcludeKeywords = { "$" };

    private static readonly string[] ExcludeExact =
    {
        "always", "add", "mult", "weight", "mode", "trigger_scope", "trigger", "base", "factor", "set",
        "weight_modifier", "days", "min", "max", "icon_frame", "subtract", "important"
    };

    private static (StellarisAdapter Adapter, Engine.StaticModifier.StaticModifierEngine Engine) Build(string baseRoot, string modRoot)
    {
        var adapter = BuildAdapter(baseRoot, modRoot);
        var engine = new Engine.StaticModifier.StaticModifierEngine(adapter, NullLogger.Instance,
            ExcludeKeys, ExcludeKeywords, ExcludeExact, new[] { "yes", "no" });
        return (adapter, engine);
    }

    private static void WriteLocalisation(string modRoot, string fileName, string content)
    {
        File.WriteAllText(Path.Combine(modRoot, "localisation", "english", fileName), content);
    }

    private static void WriteCommon(string modRoot, string dir, string fileName, string content)
    {
        Directory.CreateDirectory(Path.Combine(modRoot, "common", dir));
        File.WriteAllText(Path.Combine(modRoot, "common", dir, fileName), content);
    }

    // ==================== 基础 + 自定义 扫描 ====================

    [Test]
    public void ScanFindsBasesFromLocalisation()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_country_produces_mult: \"生产加成\"\n mod_ship_speed_mult: \"舰船速度\"\n mod_ship_evasion_mult: \"闪避\"\n mod_genocide_trait: \"灭绝特质\"\n");
        var (_, engine) = Build(b, m);

        var bases = engine.GetAllBaseModifiers();
        Assert.True(bases.Any(x => x.Name == "country_produces_mult" && x.Localisations.Values.Contains("生产加成")), "country_produces_mult 基础缺失或翻译缺失");
        Assert.True(bases.Any(x => x.Name == "ship_speed_mult"), "ship_speed_mult 缺失");
        Assert.True(bases.Any(x => x.Name == "genocide_trait"), "genocide_trait（自定义也有 mod_ 本地化）应同时是基础");
        Assert.Equal(4, bases.Count, "应恰好 4 个 mod_ 词条基础");
    }

    [Test]
    public void BaseLocalisationPrefixCaseInsensitiveAndCustomsCaseSensitive()
    {
        // 用户规则：基础加成本地化前缀无视大小写（MOD_SHIP_SPEED_MULT → ship_speed_mult）；
        // 自定义大小写敏感（MyCustom ≠ mycustom）
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n MOD_SHIP_SPEED_MULT: \"舰船速度（大写词条）\"\n mod_country_produces_mult: \"生产加成\"\n");
        WriteCommon(m, "static_modifiers", "test.txt",
            "MyCustom = {\n"
            + " country_produces_mult = 0.5\n"
            + "}\n"
            + "mycustom = {\n"
            + " ship_speed_mult = 0.2\n"
            + "}\n");
        var (_, engine) = Build(b, m);

        // 1. MOD_ 大写词条 → 关联到小写基础 ship_speed_mult（大小写不敏感）
        var ship = engine.GetBaseModifier("ship_speed_mult");
        Assert.True(ship != null, "ship_speed_mult 基础存在");
        Assert.True(ship!.Localisations.Values.Contains("舰船速度（大写词条）"), "MOD_SHIP_SPEED_MULT 词条关联到 ship_speed_mult");

        // 2. 自定义大小写敏感：MyCustom 与 mycustom 是两个不同条目
        var c1 = engine.GetCustom("MyCustom");
        var c2 = engine.GetCustom("mycustom");
        Assert.True(c1 != null && c2 != null, "MyCustom 与 mycustom 都独立存在（大小写敏感）");
        Assert.False(ReferenceEquals(c1, c2), "大小写不同的自定义不是同一条目");
        // 自定义本地化键 = 小写 mod_ + key 原样（大小写敏感词条）
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n MOD_SHIP_SPEED_MULT: \"舰船速度（大写词条）\"\n mod_country_produces_mult: \"生产加成\"\n mod_MyCustom: \"我的自定义\"\n");
        var (_, engine2) = Build(b, m);
        var c1b = engine2.GetCustom("MyCustom");
        Assert.True(c1b != null && c1b.Localisations.Values.Contains("我的自定义"), "mod_MyCustom 词条精确匹配 MyCustom（大小写敏感）");
        var c2b = engine2.GetCustom("mycustom");
        Assert.True(c2b != null && !c2b.Localisations.Values.Contains("我的自定义"), "mycustom（小写）不应拿到 mod_MyCustom 词条");
    }

    [Test]
    public void ScanFindsCustomsFromStaticModifiers()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_country_produces_mult: \"生产加成\"\n mod_ship_speed_mult: \"舰船速度\"\n mod_ship_evasion_mult: \"闪避\"\n");
        WriteCommon(m, "static_modifiers", "test.txt",
            "test_custom = {\n"
            + " icon = \"gfx/interface/icons/modifiers/test.dds\"\n"
            + " icon_frame = 2\n"
            + " hide_from_country_list = yes\n"
            + " country_produces_mult = 1\n"
            + " ship_speed_mult = 0.5\n"
            + " weird_unknown_thing = 0\n"
            + "}\n"
            + "genocide_trait = {\n ship_evasion_mult = 1000\n }\n");
        var (_, engine) = Build(b, m);

        var customs = engine.GetStaticModifiers();
        Assert.Equal(2, customs.Count, "应有 2 个自定义（test_custom / genocide_trait）");
        var tc = customs.FirstOrDefault(x => x.Name == "test_custom");
        Assert.NotNull(tc, "test_custom 缺失");
        Assert.Equal("gfx/interface/icons/modifiers/test.dds", tc!.Icon, "图标路径不匹配");
        Assert.Equal(2, tc.IconFrame, "期望 2 == tc.IconFrame");
        Assert.True(tc.Hidden, "hide_from_country_list=yes 应解析为隐藏");
        Assert.True(engine.GetBaseModifier("weird_unknown_thing") != null, "引用即断言——weird_unknown_thing 应成为基础");
    }

    [Test]
    public void CustomToBasesForwardLink()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_country_produces_mult: \"生产加成\"\n mod_ship_speed_mult: \"舰船速度\"\n mod_ship_evasion_mult: \"闪避\"\n");
        WriteCommon(m, "static_modifiers", "test.txt",
            "test_custom = {\n icon = \"gfx/x.dds\"\n country_produces_mult = 1\n ship_speed_mult = 0.5\n }\n");
        var (_, engine) = Build(b, m);

        var bases = engine.GetBasesOf("test_custom");
        Assert.Equal(2, bases.Count, "test_custom 应引用 2 个基础");
        Assert.True(bases.Any(x => x.Name == "country_produces_mult"), "缺 country_produces_mult");
        Assert.True(bases.Any(x => x.Name == "ship_speed_mult"), "缺 ship_speed_mult");
    }

    [Test]
    public void BaseToCustomsReverseLink()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_country_produces_mult: \"生产加成\"\n mod_ship_speed_mult: \"舰船速度\"\n");
        WriteCommon(m, "static_modifiers", "test.txt",
            "test_custom = {\n country_produces_mult = 1\n ship_speed_mult = 0.5\n }\n"
            + "other_custom = {\n country_produces_mult = 2\n }\n");
        var (_, engine) = Build(b, m);

        var users = engine.GetCustomsOf("country_produces_mult");
        Assert.Equal(2, users.Count, "country_produces_mult 应被 2 个自定义调用");
    }

    // ==================== modifier 引用 + 忽略规则 ====================

    [Test]
    public void WeightConditionModifierIgnoredButOuterCollected()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_country_produces_mult: \"生产加成\"\n");
        WriteCommon(m, "planet_classes", "shelter_planet_classes.txt",
            "pc_shelter = {\n"
            + " ai_weight = {\n"
            + "   weight = 0\n"
            + "   modifier = {\n"
            + "     has_country_flag = country_shelter_flag\n"
            + "     weight = 20000\n"
            + "   }\n"
            + " }\n"
            + " modifier = {\n"
            + "   country_produces_mult = 0.5\n"
            + " }\n"
            + "}\n");
        var (_, engine) = Build(b, m);

        // ai_weight 内的 modifier 是条件——忽略；外层 modifier 块应记录引用文件
        var be = engine.GetBaseModifier("country_produces_mult");
        Assert.NotNull(be, "country_produces_mult 基础缺失");
        Assert.True(be!.ExternalFiles.Count > 0, "外层 modifier 块应记录引用文件");
        Assert.True(be.ExternalFiles.Any(f => f.EndsWith("shelter_planet_classes.txt", StringComparison.OrdinalIgnoreCase)),
            "引用文件应为 planet_classes 文件，实际: " + string.Join(",", be.ExternalFiles));
        Assert.Null(engine.GetBaseModifier("has_country_flag"), "has_country_flag 无 mod_ 本地化，不应成为基础");
    }

    [Test]
    public void WeightModifierNotExtractedAsParentOrSelf()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_real_mod: \"真加成\"\n");
        WriteCommon(m, "planet_classes", "shelter_planet_classes.txt",
            "pc_shelter = {\n"
            + " weight_modifier = {\n"
            + "   modifier = {\n"
            + "     real_mod = 1\n"           // weight_modifier 下的 modifier → 不应提取
            + "   }\n"
            + " }\n"
            + " modifier = {\n"
            + "   weight_modifier = 5\n"      // weight_modifier 自身 → 不应成为引用
            + "   real_mod = 0.5\n"           // 外层正常 modifier → 应提取
            + " }\n"
            + "}\n");
        var (_, engine) = Build(b, m);

        var be = engine.GetBaseModifier("real_mod");
        Assert.NotNull(be, "real_mod 基础应存在");
        // weight_modifier 内的 modifier 块被跳过；外层 modifier 块提取 real_mod（值 0.5 那条）
        Assert.True(be!.ExternalFiles.Any(f => f.EndsWith("shelter_planet_classes.txt", StringComparison.OrdinalIgnoreCase)),
            "real_mod 应从外层 modifier 块记录引用");
        Assert.Null(engine.GetBaseModifier("weight_modifier"), "weight_modifier 自身不应成为基础");
    }

    [Test]
    public void ProgressSwapDesiredParentsExcludeModifier()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_real_mod: \"真加成\"\n");
        WriteCommon(m, "planet_classes", "t.txt",
            "pc_x = {\n"
            + " monthly_progress = {\n"          // 父键排除——其下 modifier 不提取
            + "   modifier = { real_mod = 1 }\n"
            + " }\n"
            + " tradition_swap = {\n"
            + "   modifier = { real_mod = 2 }\n"  // 父键排除
            + " }\n"
            + " desired_min = {\n"
            + "   modifier = { real_mod = 3 }\n"  // 父键排除
            + " }\n"
            + " mean_time_to_happen = {\n"
            + "   modifier = { real_mod = 4 }\n"  // 父键排除
            + " }\n"
            + " modifier = {\n"
            + "   real_mod = 0.5\n"               // 外层正常 → 提取
            + " }\n"
            + "}\n");
        var (_, engine) = Build(b, m);

        var be = engine.GetBaseModifier("real_mod");
        Assert.NotNull(be, "real_mod 基础应存在（外层 modifier 提取）");
        // 只有外层 modifier 块记录引用——4 个排除父键下的 modifier 全部跳过
        Assert.Equal(1, be!.ExternalFiles.Count(f => f.EndsWith("t.txt", StringComparison.OrdinalIgnoreCase)),
            "引用文件只记 1 次（外层块）——排除父键下的 modifier 不提取");
    }

    [Test]
    public void ScriptValuesFolderExcluded()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_sv_mod: \"脚本值\"\n mod_normal_mod: \"正常\"\n");
        // common/script_values 里的 modifier 块 → 不提取
        Directory.CreateDirectory(Path.Combine(m, "common", "script_values"));
        File.WriteAllText(Path.Combine(m, "common", "script_values", "sv.txt"),
            "sv_x = {\n modifier = { sv_mod = 1 }\n}\n");
        // 正常目录里的 modifier 块 → 提取
        Directory.CreateDirectory(Path.Combine(m, "common", "planet_classes"));
        File.WriteAllText(Path.Combine(m, "common", "planet_classes", "pc.txt"),
            "pc_x = {\n modifier = { normal_mod = 0.5 }\n}\n");
        var (_, engine) = Build(b, m);

        // script_values 文件夹内的 modifier 块不记录引用（基础可因本地化词条存在，但无引用文件）
        var sv = engine.GetBaseModifier("sv_mod");
        Assert.NotNull(sv, "sv_mod 因本地化词条存在基础");
        Assert.False(sv!.ExternalFiles.Any(f => f.Contains("script_values", StringComparison.OrdinalIgnoreCase)),
            "script_values 文件夹内 modifier 不记录引用");
        var nm = engine.GetBaseModifier("normal_mod");
        Assert.NotNull(nm, "正常文件夹 modifier 照常提取");
        Assert.True(nm!.ExternalFiles.Any(f => f.EndsWith("pc.txt", StringComparison.OrdinalIgnoreCase)),
            "正常文件夹引用被记录");
    }

    [Test]
    public void ConnectorAndSyntaxKeysNotBases()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_pop_happiness: \"幸福\"\n mod_days_x: \"天数\"\n");
        // static_modifiers 顶层块内：important=连接符不入引用；pop_happiness=真实引用
        Directory.CreateDirectory(Path.Combine(m, "common", "static_modifiers"));
        File.WriteAllText(Path.Combine(m, "common", "static_modifiers", "sm.txt"),
            "shelter_bonus = {\n"
            + " important = yes\n"          // 连接符 → 不成为基础
            + " icon_frame = 3\n"           // 语法成分 → 不成为引用
            + " pop_happiness = 0.5\n"      // 真实引用 → 记录
            + "}\n");
        // modifier 块内：days/min/max/subtract 不提取；is_difficulty 外层排除
        Directory.CreateDirectory(Path.Combine(m, "common", "decisions"));
        File.WriteAllText(Path.Combine(m, "common", "decisions", "d.txt"),
            "decision_x = {\n"
            + " modifier = {\n"
            + "   pop_happiness = 0.1\n"
            + "   days = 30\n"              // 连接符/语法 → 不提取
            + "   min = 1\n"
            + "   max = 10\n"
            + "   subtract = 2\n"
            + " }\n"
            + " is_difficulty = {\n"        // 外层排除——其下 modifier 不提取
            + "   modifier = { pop_happiness = 0.2 }\n"
            + " }\n"
            + "}\n");
        var (_, engine) = Build(b, m);

        Assert.Null(engine.GetBaseModifier("important"), "important 连接符不成为基础");
        Assert.Null(engine.GetBaseModifier("days"), "days 不提取");
        Assert.Null(engine.GetBaseModifier("min"), "min 不提取");
        Assert.Null(engine.GetBaseModifier("max"), "max 不提取");
        Assert.Null(engine.GetBaseModifier("subtract"), "subtract 不提取");
        Assert.Null(engine.GetBaseModifier("icon_frame"), "icon_frame 不提取");
        var be = engine.GetBaseModifier("pop_happiness");
        Assert.NotNull(be, "pop_happiness 基础存在");
        // static_modifiers 顶层块引用记录在 Users；正常 modifier 块引用记录在 ExternalFiles
        Assert.True(be!.Users.Any(u => u.Name == "shelter_bonus"),
            "static_modifiers 顶层块引用（Users）被记录");
        Assert.True(be.ExternalFiles.Any(f => f.EndsWith("d.txt", StringComparison.OrdinalIgnoreCase)),
            "正常 modifier 块引用（ExternalFiles）被记录");
        // is_difficulty 外层排除——其下 modifier 的独立键不提取
        Assert.Null(engine.GetBaseModifier("difficulty_mod"), "is_difficulty 外层下 modifier 不提取");
    }

    [Test]
    public void AiWaitDaysParentExcludedAndSeparatorMustBeEquals()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_real_mod: \"真加成\"\n");   // gt/lt 不给词条——纯验证提取排除
        WriteCommon(m, "decisions", "d.txt",
            "decision_x = {\n"
            + " AI_wait_days = {\n"        // 外层排除——其下 modifier 不提取
            + "   modifier = { ai_mod = 1 }\n"
            + " }\n"
            + " modifier = {\n"
            + "   real_mod = 0.5\n"        // = 连接符 → 提取
            + "   gt_mod >= 2\n"           // >= 连接符 → 非法，不提取
            + "   lt_mod < 3\n"            // < 连接符 → 非法，不提取
            + " }\n"
            + "}\n");
        var (_, engine) = Build(b, m);

        Assert.Null(engine.GetBaseModifier("ai_mod"), "AI_wait_days 外层下 modifier 不提取");
        Assert.NotNull(engine.GetBaseModifier("real_mod"), "= 连接符基础应提取");
        Assert.Null(engine.GetBaseModifier("gt_mod"), ">= 连接符基础非法不提取");
        Assert.Null(engine.GetBaseModifier("lt_mod"), "< 连接符基础非法不提取");
    }

    [Test]
    public void StaticTopBlockSimpleGetsFullChecks()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_real_mod: \"真加成\"\n mod_flag_ref: \"标志\"\n");
        Directory.CreateDirectory(Path.Combine(m, "common", "static_modifiers"));
        File.WriteAllText(Path.Combine(m, "common", "static_modifiers", "sm.txt"),
            "shelter_bonus = {\n"
            + " real_mod = 0.5\n"        // 数值 = 连接符 → 引用
            + " flag_ref = some_flag\n"  // 标识符值 → 不入引用（值类型判断，不在静态内跳过）
            + " gt_ref >= 2\n"           // 非 = 连接符 → 不入引用
            + "}\n");
        var (_, engine) = Build(b, m);

        var be = engine.GetBaseModifier("real_mod");
        Assert.NotNull(be, "real_mod 基础存在");
        Assert.True(be!.Users.Any(u => u.Name == "shelter_bonus"), "real_mod 被顶层块引用");
        // flag_ref 无基础（值非数值）——但不给词条则无基础；此处给了词条 → 基础存在但无 Users 引用
        var fr = engine.GetBaseModifier("flag_ref");
        Assert.True(fr == null || fr.Users.Count == 0, "flag_ref 标识符值不入引用");
        var gr = engine.GetBaseModifier("gt_ref");
        Assert.True(gr == null || gr.Users.Count == 0, "gt_ref 非 = 连接符不入引用");
    }

    [Test]
    public void BaseRequiresNumericOrConstantValue()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_real_mod: \"真加成\"\n mod_flag_mod: \"标志加成\"\n");
        WriteCommon(m, "planet_classes", "t.txt",
            "@my_const = 2\npc_x = {\n"
            + " modifier = {\n"
            + "   real_mod = 0.5\n"          // 数值 → 提取
            + "   flag_mod = @my_const\n"    // @ 常量引用 → 提取
            + "   country_flag = some_flag\n" // 标识符值 → 不提取
            + " }\n"
            + "}\n");
        var (_, engine) = Build(b, m);

        Assert.NotNull(engine.GetBaseModifier("real_mod"), "数值基础应提取");
        Assert.NotNull(engine.GetBaseModifier("flag_mod"), "@ 常量引用基础应提取");
        Assert.Null(engine.GetBaseModifier("country_flag"), "标识符值基础不提取");
    }

    // ==================== scripted_modifiers + 前缀回退 + 引用值 ====================

    [Test]
    public void ScriptedModifiersAreCustomBasesOnlyWhenAsserted()
    {
        var (b, m, _) = CreateSandbox();
        // scripted 代码 key = mod_trade_league_3 → 本地化 key = mod_ + 代码 key = mod_mod_trade_league_3
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_mod_trade_league_3: \"贸易联盟 III（scripted）\"\n");
        Directory.CreateDirectory(Path.Combine(m, "common", "scripted_modifiers"));
        File.WriteAllText(Path.Combine(m, "common", "scripted_modifiers", "test.txt"),
            "mod_trade_league_3 = {\n country_produces_mult = 0.1\n }\n");
        var (_, engine) = Build(b, m);

        // scripted 定义 + 本地化 mod_mod_trade_league_3 → 断言为基础（自定义基础，名 = 代码 key 原样）
        var be = engine.GetBaseModifier("mod_trade_league_3");
        Assert.NotNull(be, "本地化 mod_+代码 key 词条应断言 scripted 基础");
        Assert.True(be!.IsCustomBase, "scripted 定义应标记为自定义基础");
        Assert.True(be.DefinitionSources.Contains("scripted"), "定义来源应为 scripted");
        Assert.True(be.DefinitionFiles.TryGetValue("scripted", out var sf) && sf.Any(f2 => f2.Contains("scripted_modifiers/test.txt", StringComparison.OrdinalIgnoreCase)),
            "来源文件应为声明处（scripted_modifiers 文件）");
        Assert.True(be.Localisations.Values.Contains("贸易联盟 III（scripted）"), "翻译 = mod_ + 代码 key 词条");
    }

    [Test]
    public void ScriptedAloneShownAsCustomBase()
    {
        var (b, m, _) = CreateSandbox();
        // scripted 定义存在（代码里有）→ 就要显示为"自定义基础"（即使本地化/引用都没有）
        Directory.CreateDirectory(Path.Combine(m, "common", "scripted_modifiers"));
        File.WriteAllText(Path.Combine(m, "common", "scripted_modifiers", "test.txt"),
            "mod_trade_league_3 = {\n country_produces_mult = 0.1\n }\n");
        var (_, engine) = Build(b, m);

        var be = engine.GetBaseModifier("mod_trade_league_3");
        Assert.NotNull(be, "scripted 代码里有定义就要显示");
        Assert.True(be!.IsCustomBase, "scripted 定义 → 自定义基础");
        Assert.True(be.DefinitionSources.Contains("scripted"), "定义来源 scripted");
        // 自定义基础不回退不带前缀词条
        Assert.True(be.Localisations.Count == 0, "无 mod_+代码 key 词条时翻译应为空（不回退）");
    }

    [Test]
    public void PrefixedReferenceAssertsScriptedBase()
    {
        var (b, m, _) = CreateSandbox();
        Directory.CreateDirectory(Path.Combine(m, "common", "scripted_modifiers"));
        File.WriteAllText(Path.Combine(m, "common", "scripted_modifiers", "test.txt"),
            "mod_trade_league_3 = {\n country_produces_mult = 0.1\n }\n");
        WriteCommon(m, "static_modifiers", "t.txt",
            "my_custom = {\n mod_trade_league_3 = 1\n }\n");
        var (_, engine) = Build(b, m);

        // 引用 mod_trade_league_3 = 1 → 引用即断言（名字 = mod_trade_league_3，原样不删前缀）
        var be = engine.GetBaseModifier("mod_trade_league_3");
        Assert.NotNull(be, "引用 mod_trade_league_3 应断言为基础");
        Assert.True(be!.IsCustomBase, "scripted 定义存在 → 自定义基础");
        Assert.True(be.DefinitionSources.Contains("scripted"), "定义来源 scripted");
        var users = engine.GetCustomsOf("mod_trade_league_3");
        Assert.Equal(1, users.Count, "my_custom 应引用它");
        var ce = engine.GetCustom("my_custom");
        Assert.NotNull(ce, "my_custom 缺失");
        Assert.Equal(1, ce!.BaseRefs.Count, "应记录 1 条引用");
        Assert.Equal("mod_trade_league_3", ce.BaseRefs[0].Key, "引用键保留原文（带前缀不删）");
        Assert.Equal("1", ce.BaseRefs[0].Value, "引用值");
        Assert.Equal("mod_trade_league_3", ce.BaseRefs[0].Base.Name, "基础名 = 代码 key 原样");
    }

    [Test]
    public void BaseRefsCarryValues()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_ship_speed_mult: \"舰船速度\"\n mod_ship_evasion_mult: \"闪避\"\n");
        WriteCommon(m, "static_modifiers", "test.txt",
            "test_custom = {\n ship_speed_mult = 0.5\n ship_evasion_mult = 1000\n }\n");
        var (_, engine) = Build(b, m);

        var ce = engine.GetCustom("test_custom");
        Assert.NotNull(ce, "test_custom 缺失");
        Assert.Equal(2, ce!.BaseRefs.Count, "应记录 2 条引用");
        Assert.True(ce.BaseRefs.Any(r => r.Key == "ship_speed_mult" && r.Value == "0.5"), "值应为 0.5");
        Assert.True(ce.BaseRefs.Any(r => r.Key == "ship_evasion_mult" && r.Value == "1000"), "值应为 1000");
    }

    // ==================== 随机概率排除 + 内联引用忽略 ====================

    [Test]
    public void AiChanceAndRandomListModifiersExcluded()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_country_produces_mult: \"生产加成\"\n mod_ship_speed_mult: \"舰船速度\"\n");
        WriteCommon(m, "planet_classes", "t.txt",
            "pc_x = {\n"
            + " ai_chance = {\n   modifier = { country_produces_mult = 1 }\n }\n"
            + " random_list = {\n   1 = {\n     modifier = { ship_speed_mult = 0.1 }\n   }\n }\n"
            + "}\n");
        var (_, engine) = Build(b, m);

        // ai_chance / random_list 内的 modifier 是随机概率——排除（不记录外部引用）
        var be = engine.GetBaseModifier("country_produces_mult");
        Assert.NotNull(be, "基础存在（本地化词条断言）");
        Assert.True(be!.ExternalFiles.Count == 0, "ai_chance 内 modifier 不应记录引用，实际 " + be.ExternalFiles.Count);
        var be2 = engine.GetBaseModifier("ship_speed_mult");
        Assert.True(be2!.ExternalFiles.Count == 0, "random_list 内 modifier 不应记录引用");
    }

    [Test]
    public void DollarVariableRefsIgnored()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml", "l_english:\n mod_x: \"x\"\n");
        WriteCommon(m, "planet_classes", "t.txt",
            "pc_x = {\n modifier = {\n   $0x0x$ = 1\n   country_produces_mult = $MODIFIER$\n }\n }\n");
        var (_, engine) = Build(b, m);

        // **key** 含 $...$（$0x0x$）内联变量引用替换——忽略（不创建基础）
        Assert.Null(engine.GetBaseModifier("$0x0x$"), "$0x0x$ 是 key——应忽略，不创建基础");
        // 值 = $MODIFIER$（内联变量）：扫描时内联已展开为实际值（数值则提取）；未展开的 $ 值
        // 非数值非 @ 常量 → 不作为基础（值类型限定：数值 或 @ 常量引用）
        Assert.Null(engine.GetBaseModifier("country_produces_mult"), "$MODIFIER$ 值未展开（非数值/非 @）→ 不提取");
    }

    // ==================== 任意深度祖先 + 关键词排除 ====================

    [Test]
    public void DeepAncestorExcluded()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml", "l_english:\n mod_ship_speed_mult: \"舰船速度\"\n");
        // random_list > 1 > modifier——random_list 配深度 [1,2]（父+祖父——祖父 = random_list 命中）
        WriteCommon(m, "planet_classes", "t.txt",
            "pc_x = {\n random_list = {\n   1 = {\n     modifier = { ship_speed_mult = 0.1 }\n   }\n }\n }\n");
        var (_, engine) = Build(b, m);

        var be = engine.GetBaseModifier("ship_speed_mult");
        Assert.NotNull(be, "基础存在（本地化词条断言）");
        Assert.True(be!.ExternalFiles.Count == 0, "三层祖先 random_list 内 modifier 应排除，实际 " + be.ExternalFiles.Count);
    }

    [Test]
    public void ExactMatchRefKeysExcluded()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml", "l_english:\n mod_ship_speed_mult: \"舰船速度\"\n");
        // modifier 块内 factor/base 是语法成分——完全匹配排除（不当作引用）
        WriteCommon(m, "planet_classes", "t.txt",
            "pc_x = {\n modifier = {\n   factor = 0.5\n   base = 1\n   ship_speed_mult = 0.1\n }\n }\n");
        var (_, engine) = Build(b, m);

        Assert.Null(engine.GetBaseModifier("factor"), "factor 完全匹配排除——不创建基础");
        Assert.Null(engine.GetBaseModifier("base"), "base 完全匹配排除——不创建基础");
        var be = engine.GetBaseModifier("ship_speed_mult");
        Assert.NotNull(be, "ship_speed_mult 正常引用");
        Assert.True(be!.ExternalFiles.Count > 0, "ship_speed_mult 应记录引用");
    }

    [Test]
    public void YesNoValueRejected()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml", "l_english:\n mod_x: \"x\"\n");
        // 值 yes/no 是开关/标志——默认无效（不创建基础）
        WriteCommon(m, "planet_classes", "t.txt",
            "pc_x = {\n modifier = {\n   some_flag = yes\n   another_flag = no\n }\n }\n");
        var (_, engine) = Build(b, m);

        Assert.Null(engine.GetBaseModifier("some_flag"), "值 yes → 无效，不创建基础");
        Assert.Null(engine.GetBaseModifier("another_flag"), "值 no → 无效，不创建基础");
    }

    // ==================== 静态/自定义重复 key ====================

    [Test]
    public void SameKeyStaticAndScriptedBothKept()
    {
        var (b, m, _) = CreateSandbox();
        // 同一 key 在 static_modifiers 和 scripted_modifiers 都有定义 → 视为 2 个（不覆盖、不混合）
        Directory.CreateDirectory(Path.Combine(m, "common", "scripted_modifiers"));
        File.WriteAllText(Path.Combine(m, "common", "scripted_modifiers", "s.txt"),
            "shared_mod = {\n country_produces_mult = 0.1\n }\n");
        WriteCommon(m, "static_modifiers", "t.txt",
            "shared_mod = {\n ship_speed_mult = 0.5\n }\n");
        var (_, engine) = Build(b, m);

        var be = engine.GetBaseModifier("shared_mod");
        Assert.NotNull(be, "shared_mod 应存在");
        Assert.True(be!.DefinitionSources.Contains("static"), "应有 static 定义");
        Assert.True(be.DefinitionSources.Contains("scripted"), "应有 scripted 定义（不覆盖）");
        Assert.True(be.DefinitionFiles.ContainsKey("static") && be.DefinitionFiles.ContainsKey("scripted"),
            "两个来源的声明文件都应保留");
        Assert.Equal(2, be.DefinitionSources.Count, "应为 2 个独立定义");
    }

    [Test]
    public void ActiveFileStaticFirstScriptedLast()
    {
        var (b, m, _) = CreateSandbox();
        // static 同 key 两个文件（升序：a 早于 b）→ 启用 a（最早）；scripted 两个文件 → 启用 z（最晚）
        WriteCommon(m, "static_modifiers", "a_static.txt", "shared_mod = {\n x = 1\n }\n");
        WriteCommon(m, "static_modifiers", "b_static.txt", "shared_mod = {\n y = 2\n }\n");
        Directory.CreateDirectory(Path.Combine(m, "common", "scripted_modifiers"));
        File.WriteAllText(Path.Combine(m, "common", "scripted_modifiers", "a_scripted.txt"),
            "shared_mod = {\n x = 1\n }\n");
        File.WriteAllText(Path.Combine(m, "common", "scripted_modifiers", "z_scripted.txt"),
            "shared_mod = {\n y = 2\n }\n");
        var (_, engine) = Build(b, m);

        var be = engine.GetBaseModifier("shared_mod");
        Assert.NotNull(be, "shared_mod 应存在");
        Assert.True(be!.GetActiveFile("static")!.EndsWith("a_static.txt", StringComparison.OrdinalIgnoreCase),
            "static 只读一次——升序最早启用，实际: " + be.GetActiveFile("static"));
        Assert.True(be.GetActiveFile("scripted")!.EndsWith("z_scripted.txt", StringComparison.OrdinalIgnoreCase),
            "scripted 后读覆盖——升序最晚启用，实际: " + be.GetActiveFile("scripted"));
    }

    // ==================== 图标 / 隐藏 / 搜索 ====================

    [Test]
    public void IconLookupAndHiddenFilter()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml", "l_english:\n mod_x: \"x\"\n");
        WriteCommon(m, "static_modifiers", "test.txt",
            "a_icon = {\n icon = \"gfx/i/a.dds\"\n hide_from_country_list = yes\n }\n"
            + "b_icon = {\n icon = \"gfx/i/a.dds\"\n }\n"
            + "c_visible = {\n }\n");
        var (_, engine) = Build(b, m);

        var byIcon = engine.GetByIcon("gfx/i/a.dds");
        Assert.Equal(2, byIcon.Count, "图标 gfx/i/a.dds 应被 2 个自定义使用");
        Assert.True(engine.FilterByHidden(true).Any(x => x.Name == "a_icon"), "隐藏筛选应含 a_icon");
        Assert.False(engine.FilterByHidden(true).Any(x => x.Name == "b_icon"), "b_icon 未隐藏");
        Assert.True(engine.FilterByHidden(false).Any(x => x.Name == "c_visible"), "c_visible 应在未隐藏列表");
    }

    [Test]
    public void SearchKeywordFindsBoth()
    {
        var (b, m, _) = CreateSandbox();
        WriteLocalisation(m, "mods_l_english.yml",
            "l_english:\n mod_produces_mult: \"生产加成\"\n mod_speed: \"速度\"\n");
        WriteCommon(m, "static_modifiers", "test.txt",
            "produces_custom = {\n produces_mult = 1\n }\n");
        var (_, engine) = Build(b, m);

        var hits = engine.Search("produces");
        Assert.True(hits.Count >= 2, "关键词 produces 应同时命中基础和自定义，实际 " + hits.Count);
        Assert.True(hits.Any(x => x is Engine.StaticModifier.StaticModifierEngine.BaseModifier be2 && be2.Name == "produces_mult"), "搜索应命中基础 produces_mult");
        Assert.True(hits.Any(x => x is Engine.StaticModifier.StaticModifierEngine.StaticModifierEntry ce && ce.Name == "produces_custom"), "搜索应命中自定义 produces_custom");
    }
}
