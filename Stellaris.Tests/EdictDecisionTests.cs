using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Engine.EdictDecision;
using Stellaris.Engine.StrategicResource;
using Stellaris.Parser;

namespace Stellaris.Tests;

/// <summary>法令/决议引擎扫描与解析测试（沙盒文件 + 内存新建）。</summary>
public sealed class EdictDecisionTests
{
    private static (string Base, string Mod, string Tmp) CreateSandbox()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "smt_edict_" + Guid.NewGuid().ToString("N"));
        string baseRoot = Path.Combine(tmp, "base");
        string modRoot = Path.Combine(tmp, "mod");
        Directory.CreateDirectory(Path.Combine(baseRoot, "localisation", "english"));
        Directory.CreateDirectory(Path.Combine(modRoot, "localisation", "english"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "edicts"));
        Directory.CreateDirectory(Path.Combine(modRoot, "common", "decisions"));
        return (baseRoot, modRoot, tmp);
    }

    private static (StellarisAdapter, EdictDecisionEngine) Build(string baseRoot, string modRoot)
    {
        var adapter = new StellarisAdapter();
        adapter.AddRoot(baseRoot);
        adapter.AddRoot(modRoot);
        adapter.ScanAll();
        var engine = new EdictDecisionEngine(adapter, NullLogger.Instance);
        return (adapter, engine);
    }
    [Test]
    public void SaveNewItemWritesDefaultFile()
    {
        var (b, m, _) = CreateSandbox();
        var (adapter, engine) = Build(b, m);
        var item = engine.AddItem(EdictDecisionKind.Edict, "new_edict_123456");
        item.Icon = "GFX_edict_test";
        item.LengthIsInfinite = false;
        item.LengthValue = 30;
        item.Potential = ConditionPreset.AiYes;
        item.Effects.Add(("pop_happiness", 0.25));
        engine.MarkDirty(item, EdictField.Icon);
        engine.MarkDirty(item, EdictField.Length);
        engine.MarkDirty(item, EdictField.Potential);
        engine.MarkDirty(item, EdictField.Bonuses);

        var (saved, errors) = engine.SaveAll("smt");
        Assert.Equal(0, errors.Count, "保存错误: " + string.Join("|", errors));
        Assert.Equal(1, saved, "1 个文件写入");
        string outFile = Path.Combine(m, "common", "edicts", "00_smt_edicts.txt");
        Assert.True(File.Exists(outFile), "默认文件 00_smt_edicts.txt 生成");
        var text = File.ReadAllText(outFile);
        Assert.True(text.Contains("new_edict_123456 = {", StringComparison.Ordinal), "新法令块存在");
        Assert.True(text.Contains("icon = \"GFX_edict_test\"", StringComparison.Ordinal), "icon 写入");
        Assert.True(text.Contains("length = 30", StringComparison.Ordinal), "length 写入");
        Assert.True(text.Contains("is_ai = yes", StringComparison.Ordinal), "potential AiYes 写入");
        Assert.True(text.Contains("pop_happiness = 0.25", StringComparison.Ordinal), "modifier 加成写入");
        Assert.True(text.Contains("resources = {", StringComparison.Ordinal) && text.Contains("category = edicts", StringComparison.Ordinal),
            "resources 总是生成（含 category = edicts）——即使没选具体资源");
        Assert.True(text.Contains("ai_weight = {", StringComparison.Ordinal) && text.Contains("weight = 0", StringComparison.Ordinal),
            "ai_weight 总是生成（默认 weight = 0）——即使没填");
    }

    [Test]
    public void SaveAiNoWritesPotential()
    {
        // 用户反馈：选"仅限玩家可见"（AiNo）后 potential 必须写入
        var (b, m, _) = CreateSandbox();
        var (adapter, engine) = Build(b, m);
        var item = engine.AddItem(EdictDecisionKind.Edict, "player_only_edict");
        item.Potential = ConditionPreset.AiNo;
        engine.MarkDirty(item, EdictField.Potential);
        var (saved, errors) = engine.SaveAll("smt");
        Assert.Equal(0, errors.Count, string.Join("|", errors));
        Assert.Equal(1, saved, "仅限玩家可见保存 1 个文件");
        var text = File.ReadAllText(Path.Combine(m, "common", "edicts", "00_smt_edicts.txt"));
        Assert.True(text.Contains("potential = {", StringComparison.Ordinal) && text.Contains("is_ai = no", StringComparison.Ordinal),
            "仅限玩家可见 → potential = { is_ai = no } 写入");
    }

    [Test]
    public void SaveModifiedFieldKeepsUnchanged()
    {
        var (b, m, _) = CreateSandbox();
        WriteConfig(m, "common/edicts/my_edict.txt",
            "my_edict = {\n # 注释保留\n icon = \"GFX_edict_1\"\n resources = { category = edicts cost = { influence = 100 } }\n modifier = { pop_amenities = 0.5 }\n}\n");
        var (adapter, engine) = Build(b, m);
        var e = engine.GetItems(EdictDecisionKind.Edict).First(x => x.Key == "my_edict");
        e.Icon = "GFX_edict_new";
        engine.MarkDirty(e, EdictField.Icon);

        var (saved, _) = engine.SaveAll("smt");
        Assert.Equal(1, saved, "写回 1 个文件");
        string outFile = Path.Combine(m, "common", "edicts", "my_edict.txt");
        var text = File.ReadAllText(outFile);
        Assert.True(text.Contains("icon = \"GFX_edict_new\"", StringComparison.Ordinal), "icon 已更新");
        Assert.True(text.Contains("# 注释保留", StringComparison.Ordinal), "块内注释保留（只改 icon）");
        Assert.True(text.Contains("influence = 100", StringComparison.Ordinal), "resources 未动");
        Assert.True(text.Contains("pop_amenities = 0.5", StringComparison.Ordinal), "modifier 未动");
    }

    [Test]
    public void SaveKeyRenameMovesOldBlockOut()
    {
        var (b, m, _) = CreateSandbox();
        WriteConfig(m, "common/edicts/my_edict.txt", "my_edict = {\n icon = \"GFX_edict_1\"\n}\nother_edict = {\n icon = \"GFX_edict_2\"\n}\n");
        var (adapter, engine) = Build(b, m);
        var e = engine.GetItems(EdictDecisionKind.Edict).First(x => x.Key == "my_edict");
        e.Key = "renamed_edict";
        engine.MarkDirty(e, EdictField.Key);

        var (saved, _) = engine.SaveAll("smt");
        Assert.Equal(1, saved, "Key 改名写回 1 个文件");
        string outFile = Path.Combine(m, "common", "edicts", "my_edict.txt");
        var text = File.ReadAllText(outFile);
        Assert.False(text.Contains("my_edict = {", StringComparison.Ordinal), "旧 key 块移出");
        Assert.True(text.Contains("renamed_edict = {", StringComparison.Ordinal), "新 key 块移入");
        Assert.True(text.Contains("other_edict = {", StringComparison.Ordinal), "其他块保留");
        Assert.True(text.Contains("GFX_edict_1", StringComparison.Ordinal), "块内容保留");
    }

    [Test]
    public void SaveCreatesLocalisationEntries()
    {
        // 保存时自动创建本地化（名称/描述词条——没填名称用 key；落盘 mod 本地化文件）
        var (b, m, _) = CreateSandbox();
        var (adapter, engine) = Build(b, m);
        var item = engine.AddItem(EdictDecisionKind.Edict, "loc_edict_1");
        item.NameLogical = "我的法令";
        item.DescLogical = "描述内容";
        engine.MarkDirty(item, EdictField.Icon);
        var (saved, errors) = engine.SaveAll("smt");
        Assert.Equal(0, errors.Count, "保存错误: " + string.Join("|", errors));
        Assert.Equal(1, saved, "保存 1 个文件");
        // 本地化文件生成（english——测试 sandbox 有 localisation/english）
        string locFile = Path.Combine(m, "localisation", "english", "edicts_smt_l_english.yml");
        Assert.True(File.Exists(locFile), "本地化文件自动创建");
        var text = File.ReadAllText(locFile);
        Assert.True(text.Contains("edict_loc_edict_1:", StringComparison.Ordinal), "名称词条创建");
        Assert.True(text.Contains("我的法令", StringComparison.Ordinal), "名称值写入");
        Assert.True(text.Contains("edict_loc_edict_1_desc:", StringComparison.Ordinal), "描述词条创建");
        Assert.True(text.Contains("描述内容", StringComparison.Ordinal), "描述值写入");
    }

    [Test]
    public void AiConditionWrappedInSolarSystemOwner()
    {
        // 仅限玩家/AI 条件套 solar_system > owner 壳（不然判定不到国家）；回读能识别
        var (b, m, _) = CreateSandbox();
        WriteConfig(m, "common/edicts/wrap_edict.txt",
            "wrap_edict = {\n"
            + " potential = {\n"
            + "  solar_system = {\n"
            + "   owner = {\n"
            + "    is_ai = yes\n"
            + "   }\n"
            + "  }\n"
            + " }\n"
            + "}\n");
        WriteConfig(m, "common/edicts/owner_only.txt",
            "owner_only = {\n"
            + " potential = {\n"
            + "  owner = {\n"
            + "   is_ai = no\n"
            + "  }\n"
            + " }\n"
            + "}\n");
        var (adapter, engine) = Build(b, m);
        var e = engine.GetItems(EdictDecisionKind.Edict).First(x => x.Key == "wrap_edict");
        Assert.Equal(ConditionPreset.AiYes, e.Potential, "套壳 potential 回读 → AiYes");
        var e2 = engine.GetItems(EdictDecisionKind.Edict).First(x => x.Key == "owner_only");
        Assert.Equal(ConditionPreset.AiNo, e2.Potential, "owner-only 格式回读 → AiNo");
        // 保存侧：AiNo 生成套壳格式
        var item = engine.AddItem(EdictDecisionKind.Edict, "wrap2");
        item.Potential = ConditionPreset.AiNo;
        engine.MarkDirty(item, EdictField.Potential);
        var (saved, errors) = engine.SaveAll("smt");
        Assert.Equal(0, errors.Count, string.Join("|", errors));
        var text = File.ReadAllText(Path.Combine(m, "common", "edicts", "00_smt_edicts.txt"));
        Assert.True(text.Contains("solar_system = {", StringComparison.Ordinal)
            && text.Contains("owner = {", StringComparison.Ordinal)
            && text.Contains("is_ai = no", StringComparison.Ordinal),
            "AiNo 保存为 solar_system > owner > is_ai = no 套壳");
    }

    [Test]
    public void DecisionFlagsAndEnactmentTimeSave()
    {
        // 决议：important/owned_planets_only 勾选写 yes；enactment_time = 0 不写
        var (b, m, _) = CreateSandbox();
        var (adapter, engine) = Build(b, m);
        var item = engine.AddItem(EdictDecisionKind.Decision, "flag_dec");
        item.Important = true;
        item.OwnedPlanetsOnly = true;
        item.EnactmentTime = 30;
        engine.MarkDirty(item, EdictField.Important);
        engine.MarkDirty(item, EdictField.OwnedPlanetsOnly);
        engine.MarkDirty(item, EdictField.EnactmentTime);
        var (saved, errors) = engine.SaveAll("smt");
        Assert.Equal(0, errors.Count, "保存错误: " + string.Join("|", errors));
        var text = File.ReadAllText(Path.Combine(m, "common", "decisions", "00_smt_decisions.txt"));
        Assert.True(text.Contains("important = yes", StringComparison.Ordinal)
            && text.Contains("owned_planets_only = yes", StringComparison.Ordinal)
            && text.Contains("enactment_time = 30", StringComparison.Ordinal),
            "决议 important/owned_planets_only/enactment_time 写入");
        // enactment_time = 0 → 不写
        var item2 = engine.AddItem(EdictDecisionKind.Decision, "zero_dec");
        engine.MarkDirty(item2, EdictField.EnactmentTime);
        var (s2, e2) = engine.SaveAll("smt");
        Assert.Equal(0, e2.Count, string.Join("|", e2));
        var text2 = File.ReadAllText(Path.Combine(m, "common", "decisions", "00_smt_decisions.txt"));
        Assert.False(text2.Contains("enactment_time = 0", StringComparison.Ordinal), "enactment_time = 0 不写");
    }

    [Test]
    public void SaveNothingWhenNoDirty()
    {
        var (b, m, _) = CreateSandbox();
        WriteConfig(m, "common/edicts/my_edict.txt", "my_edict = {\n icon = \"GFX_edict_1\"\n}\n");
        var (adapter, engine) = Build(b, m);
        var (saved, _) = engine.SaveAll("smt");
        Assert.Equal(0, saved, "无脏登记 → 不写任何文件");
    }

    [Test]
    public void RemoveItemSavesBlockRemovalAndDeletesLocalisation()
    {
        var (b, m, _) = CreateSandbox();
        var (adapter, engine) = Build(b, m);
        var item = engine.AddItem(EdictDecisionKind.Edict, "del_edict_1");
        item.NameLogical = "要删的法令";
        item.Icon = "GFX_edict_del";
        engine.MarkDirty(item, EdictField.Icon);
        var (s1, e1) = engine.SaveAll("smt");
        Assert.Equal(0, e1.Count, "首存错误: " + string.Join("|", e1));
        string outFile = Path.Combine(m, "common", "edicts", "00_smt_edicts.txt");
        Assert.True(File.Exists(outFile), "默认法令文件生成");
        Assert.True(File.ReadAllText(outFile).Contains("del_edict_1 = {", StringComparison.Ordinal), "块已写入文件");
        string locFile = Path.Combine(m, "localisation", "english", "edicts_smt_l_english.yml");
        Assert.True(File.Exists(locFile), "本地化文件已创建");
        Assert.True(File.ReadAllText(locFile).Contains("edict_del_edict_1:", StringComparison.Ordinal), "名称词条存在");

        // 删除 → 保存：块从文件移出 + 配套本地化词条删除
        engine.RemoveItem(item);
        var (s2, e2) = engine.SaveAll("smt");
        Assert.Equal(0, e2.Count, "删除保存错误: " + string.Join("|", e2));
        Assert.False(File.ReadAllText(outFile).Contains("del_edict_1 = {", StringComparison.Ordinal), "块已从文件移出");
        var locText = File.ReadAllText(locFile);
        Assert.False(locText.Contains("edict_del_edict_1:", StringComparison.Ordinal), "名称词条已删除");
        Assert.False(locText.Contains("edict_del_edict_1_desc:", StringComparison.Ordinal), "描述词条已删除");
    }

    [Test]
    public void SaveMovesLocalisationToEdictsAndCleansSource()
    {
        // 词条已存在于其他本地化文件（旧位置）→ 保存：键**移动**到 edicts_ 目标文件（新位置），
        // **旧位置登记待保存**（写剩余/空头清理）——新旧位置都写，不留磁盘残留重复
        var (b, m, _) = CreateSandbox();
        WriteConfig(m, "localisation/english/existing_l_english.yml",
            "l_english:\n edict_legacy_edict: \"旧名字\"\n edict_legacy_edict_desc: \"旧描述\"\n");
        WriteConfig(m, "common/edicts/legacy_edict.txt", "legacy_edict = {\n icon = \"GFX_edict_x\"\n}\n");
        var (adapter, engine) = Build(b, m);
        var e = engine.GetItems(EdictDecisionKind.Edict).First(x => x.Key == "legacy_edict");
        e.NameLogical = "新名字";
        e.DescLogical = "新描述";
        engine.MarkDirty(e, EdictField.Icon);
        var (saved, errors) = engine.SaveAll("smt");
        Assert.Equal(0, errors.Count, "保存错误: " + string.Join("|", errors));
        // 新位置（edicts_ 目标文件）有词条（新值）
        string edictsLoc = Path.Combine(m, "localisation", "english", "edicts_smt_l_english.yml");
        Assert.True(File.Exists(edictsLoc), "edicts_ 目标文件生成");
        var edictsText = File.ReadAllText(edictsLoc);
        Assert.True(edictsText.Contains("edict_legacy_edict: \"新名字\"", StringComparison.Ordinal), "新位置名称词条已写");
        Assert.True(edictsText.Contains("edict_legacy_edict_desc: \"新描述\"", StringComparison.Ordinal), "新位置描述词条已写");
        // 旧位置（源文件）被清理：词条移走后写剩余/空头——磁盘不再有旧词条
        string srcFile = Path.Combine(m, "localisation", "english", "existing_l_english.yml");
        var srcText = File.ReadAllText(srcFile);
        Assert.False(srcText.Contains("edict_legacy_edict", StringComparison.Ordinal), "旧位置词条已被清理（不残留重复）");
    }

    [Test]
    public void RemoveItemBeforeSaveDoesNotResurrectBlock()
    {
        // 防复活：新建项被标记修改（未保存）就删除 → 保存时块不得写回文件
        var (b, m, _) = CreateSandbox();
        var (adapter, engine) = Build(b, m);
        var item = engine.AddItem(EdictDecisionKind.Edict, "doomed_edict");
        item.Icon = "GFX_edict_x";
        engine.MarkDirty(item, EdictField.Icon);
        engine.RemoveItem(item);
        var (saved, errors) = engine.SaveAll("smt");
        Assert.Equal(0, errors.Count, "保存错误: " + string.Join("|", errors));
        string outFile = Path.Combine(m, "common", "edicts", "00_smt_edicts.txt");
        Assert.False(File.Exists(outFile) && File.ReadAllText(outFile).Contains("doomed_edict = {", StringComparison.Ordinal),
            "被删项块不得写回文件");
    }

    [Test]
    public void ScansEdictsAndDecisionsWithEffects()
    {
        var (b, m, _) = CreateSandbox();
        WriteConfig(m, "common/edicts/my_edict.txt", "my_edict = {\n icon = \"GFX_edict_1\"\n resources = { category = edicts cost = { influence = 100 } }\n modifier = { pop_amenities = 0.5 } \n}\n");
        WriteConfig(m, "common/decisions/my_decision.txt", "my_decision = {\n cost = 200\n modifier = { pop_happiness = 0.1 } \n}\n");

        var (adapter, engine) = Build(b, m);

        var edicts = engine.GetItems(EdictDecisionKind.Edict);
        Assert.Equal(1, edicts.Count, "扫描到 1 个法令（实际 keys: " + string.Join(",", edicts.Select(x => x.Key)) + "；sources: " + string.Join(",", edicts.Select(x => x.SourceRelPath)) + "）");
        var e = edicts[0];
        Assert.Equal("my_edict", e.Key, "法令 key");
        Assert.Equal("GFX_edict_1", e.Icon, "法令图标");
        Assert.Equal(100.0, e.Cost.Groups[0].Amounts.GetValueOrDefault("influence", 0), "法令花费（resources.cost.influence）");
        Assert.Equal(1, e.Effects.Count, "法令效果 1 个");
        Assert.Equal("pop_amenities", e.Effects[0].Base, "效果基础名（不带 mod_ 前缀）");
        Assert.Equal(0.5, e.Effects[0].Value, "效果数值");

        var decisions = engine.GetItems(EdictDecisionKind.Decision);
        Assert.Equal(1, decisions.Count, "扫描到 1 个决议");
        Assert.Equal("my_decision", decisions[0].Key, "决议 key");
        Assert.Equal("pop_happiness", decisions[0].Effects[0].Base, "决议效果");
    }

    [Test]
    public void ParsesFullEdictFields()
    {
        var (b, m, _) = CreateSandbox();
        WriteConfig(m, "common/edicts/full.txt",
            "full_edict = {\n length = -1\n resources = { category = edicts cost = { influence = 10 } upkeep = { energy = 2.5 } }\n potential = { is_ai = yes }\n allow = { has_country_flag = x }\n ai_weight = { weight = 15 }\n modifier = { pop_happiness = 1 }\n}\n");
        var (adapter, engine) = Build(b, m);
        var e = engine.GetItems(EdictDecisionKind.Edict).First(x => x.Key == "full_edict");
        Assert.True(e.LengthIsInfinite, "length = -1 → 无限");
        Assert.Equal(10.0, e.Cost.Groups[0].Amounts["influence"], "启动消耗 influence = 10");
        Assert.Equal(2.5, e.Upkeep.Groups[0].Amounts["energy"], "每月消耗 energy = 2.5");
        Assert.Equal(ConditionPreset.AiYes, e.Potential, "potential 含 is_ai = yes → AiYes");
        Assert.True(e.PotentialCustom.Contains("is_ai"), "potential 原文保留");
        Assert.Equal(ConditionPreset.Custom, e.Allow, "allow 自定义内容 → Custom");
        Assert.Equal(15.0, e.AiWeight, "ai_weight.weight = 15");
        Assert.True(e.AiWeightRaw.Contains("weight = 15"), "ai_weight 块原文保留（有什么显示什么）");
        Assert.True(e.EffectRaw.Length == 0, "无 effect 块 → 空");
    }

    [Test]
    public void ParsesAiWeightRawAndEffectRaw()
    {
        var (b, m, _) = CreateSandbox();
        WriteConfig(m, "common/edicts/raw.txt",
            "raw_edict = {\n ai_weight = { factor = 1000 modifier = { pop_happiness = 0.5 } }\n effect = { country_event = { id = test.1 } }\n}\n");
        var (adapter, engine) = Build(b, m);
        var e = engine.GetItems(EdictDecisionKind.Edict).First(x => x.Key == "raw_edict");
        Assert.True(e.AiWeightRaw.Contains("factor = 1000"), "ai_weight 显示 factor（有什么显示什么）");
        Assert.True(e.AiWeightRaw.Contains("modifier"), "ai_weight 含 modifier 子块");
        Assert.False(e.AiWeightRaw.Contains("ai_weight = {"), "ai_weight 无外壳");
        Assert.True(e.EffectRaw.Contains("country_event"), "effect 块原文（写事件）");
        Assert.True(e.EffectRaw.Contains("test.1"), "effect 含事件 id");
        Assert.False(e.EffectRaw.Contains("effect = {"), "effect 无外壳（只显示块内内容）");
    }

    [Test]
    public void ClassifyConditionMatchesParseHeuristic()
    {
        Assert.Equal(ConditionPreset.AlwaysYes, EdictDecisionEngine.ClassifyCondition(""), "空 → AlwaysYes");
        Assert.Equal(ConditionPreset.AlwaysYes, EdictDecisionEngine.ClassifyCondition("   "), "空白 → AlwaysYes");
        Assert.Equal(ConditionPreset.AiYes, EdictDecisionEngine.ClassifyCondition("is_ai = yes"), "is_ai=yes → AiYes");
        Assert.Equal(ConditionPreset.AiNo, EdictDecisionEngine.ClassifyCondition("is_ai = no"), "is_ai=no → AiNo");
        Assert.Equal(ConditionPreset.Custom, EdictDecisionEngine.ClassifyCondition("has_country_flag = x"), "其他 → Custom");
        Assert.Equal(ConditionPreset.Custom, EdictDecisionEngine.ClassifyCondition("is_ai = yes\nhas_country_flag = x"), "含 is_ai 还有别的 → Custom（预设只代表纯这一条）");
        Assert.Equal(ConditionPreset.Custom, EdictDecisionEngine.ClassifyCondition("is_ai = yes\nhas_global_flag = more_galaxy_AI_modifier_2"), "用户场景：is_ai + has_global_flag → Custom");
    }

    [Test]
    public void AddItemStaysInMemoryAndRemoves()
    {
        var (b, m, _) = CreateSandbox();
        var (adapter, engine) = Build(b, m);

        var item = engine.AddItem(EdictDecisionKind.Edict, "brand_new");
        item.Cost.Groups.Add(new StrategicResourceEngine.ResourceGroup());
        item.Cost.Groups[0].Amounts["influence"] = 42;
        item.Upkeep.Groups.Add(new StrategicResourceEngine.ResourceGroup());
        item.Upkeep.Groups[0].Amounts["energy"] = 2.5;
        item.Effects.Add(("pop_growth_speed", 0.2));
        item.Potential = ConditionPreset.AiYes;

        var all = engine.GetItems(EdictDecisionKind.Edict);
        Assert.Equal(1, all.Count, "内存新建出现在列表");
        Assert.Equal("brand_new", all[0].Key, "新建 key");
        Assert.Equal(42.0, all[0].Cost.Groups[0].Amounts["influence"], "新建启动花费");
        Assert.Equal(2.5, all[0].Upkeep.Groups[0].Amounts["energy"], "新建每月消耗");
        Assert.Equal(0.2, all[0].Effects[0].Value, "新建效果数值");
        Assert.Equal(ConditionPreset.AiYes, all[0].Potential, "条件预设");

        engine.RemoveItem(item);
        Assert.Equal(0, engine.GetItems(EdictDecisionKind.Edict).Count, "移除后列表空");
    }

    [Test]
    public void ParsesDuplicateBucketsMultiplierAndTrigger()
    {
        // 用户例子的合法 resources：同桶重复块（可重复添加）+ produces（非 product）+ multiplier 变量 + trigger
        var (b, m, _) = CreateSandbox();
        WriteConfig(m, "common/edicts/full2.txt",
            "full2 = {\n resources = {\n" +
            " category = edicts\n" +
            " cost = { influence = 2 energy = 1 multiplier = value:edict_size_effect }\n" +
            " cost = { energy = 5 trigger = { is_ai = no } multiplier = value:edict_size_effect }\n" +
            " upkeep = { alloys = 7 trigger = { is_ai = yes } multiplier = value:edict_size_effect }\n" +
            " produces = { alloys = 1 }\n" +
            " produces = { trigger = { is_ai = yes } alloys = 1 }\n" +
            "}\n}\n");
        var (adapter, engine) = Build(b, m);
        var e = engine.GetItems(EdictDecisionKind.Edict).First(x => x.Key == "full2");
        // cost 两组（各自组）
        Assert.Equal(2, e.Cost.Groups.Count, "cost 两组（重复添加）");
        Assert.Equal(2.0, e.Cost.Groups[0].Amounts["influence"], "组 1 influence");
        Assert.Equal(1.0, e.Cost.Groups[0].Amounts["energy"], "组 1 energy");
        Assert.Equal("value:edict_size_effect", e.Cost.Groups[0].Multiplier, "倍率保留变量原文");
        Assert.Null(e.Cost.Groups[0].Trigger, "组 1 无条件");
        Assert.Equal(5.0, e.Cost.Groups[1].Amounts["energy"], "组 2 energy");
        Assert.NotNull(e.Cost.Groups[1].Trigger, "组 2 有条件");
        // upkeep 一组 + produces 两组
        Assert.Equal(7.0, e.Upkeep.Groups[0].Amounts["alloys"], "upkeep");
        Assert.Equal(2, e.Produces.Groups.Count, "produces 两组（游戏语法）");
        Assert.Equal(1.0, e.Produces.Groups[1].Amounts["alloys"], "produces 组 2");
        Assert.NotNull(e.Produces.Groups[1].Trigger, "produces 组 2 有条件");
        // category 忽略（不进任何桶）
        Assert.Equal(0, e.Cost.Groups.SelectMany(g => g.Amounts).Count(g => g.Key == "category"), "category 不当作资源");
    }

    [Test]
    public void BuildResourcesBlockIntegratesGroupsAndRoundTrips()
    {
        // 整合：相同 (倍率, 条件) 的组合并（资源加总）；不同的各自成块；往返一致
        var cost = new StrategicResourceEngine.ResourceBucket();
        var g1 = new StrategicResourceEngine.ResourceGroup();
        g1.Amounts["influence"] = 2;
        g1.Amounts["energy"] = 1;
        g1.Multiplier = "value:edict_size_effect";
        cost.Groups.Add(g1);
        var g2 = new StrategicResourceEngine.ResourceGroup();
        g2.Amounts["energy"] = 5;
        g2.Multiplier = "value:edict_size_effect";
        cost.Groups.Add(g2);   // 与 g1 相同 multiplier、无 trigger → 合并
        var g3 = new StrategicResourceEngine.ResourceGroup();
        g3.Amounts["energy"] = 5;
        g3.Multiplier = "value:edict_size_effect";
        var trigger = new Stellaris.Parser.AstNode { Type = Stellaris.Parser.NodeType.Block, Key = "trigger" };
        trigger.Children.Add(new Stellaris.Parser.AstNode { Type = Stellaris.Parser.NodeType.Simple, Key = "is_ai", Value = "no" });
        g3.Trigger = trigger;
        cost.Groups.Add(g3);   // trigger 不同 → 独立块
        var produces = new StrategicResourceEngine.ResourceBucket();
        var p1 = new StrategicResourceEngine.ResourceGroup();
        p1.Amounts["alloys"] = 1;
        produces.Groups.Add(p1);

        var resourcesBlock = StrategicResourceEngine.BuildResourcesBlock(cost, null, produces);
        var text = SerializationHelper.Serialize(new System.Collections.Generic.List<Stellaris.Parser.AstNode> { resourcesBlock });
        // g1+g2 合并 → cost 出现 2 次（合并组 + trigger 组）
        Assert.Equal(2, CountOccurrences(text, "cost = {"), "g1+g2 合并成 1 块 + g3 独立 = 2 块 cost");
        Assert.True(text.Contains("influence = 2", StringComparison.Ordinal), "合并组保留 influence");
        Assert.True(text.Contains("energy = 6", StringComparison.Ordinal), "合并组 energy 加总（1+5=6）");
        Assert.True(text.Contains("trigger = {", StringComparison.Ordinal), "独立组含 trigger");
        Assert.True(text.Contains("produces = {", StringComparison.Ordinal), "produces 块（游戏语法）");
        Assert.False(text.Contains("product", StringComparison.Ordinal), "不使用 product 命名");
        Assert.True(text.Contains("multiplier = value:edict_size_effect", StringComparison.Ordinal), "倍率变量原文输出");

        // 往返：解析回来结构一致
        var (c2, u2, p2) = StrategicResourceEngine.ParseResources(resourcesBlock);
        Assert.Equal(2, c2.Groups.Count, "往返：cost 2 组（合并后）");
        Assert.Equal(6.0, c2.Groups[0].Amounts["energy"], "往返合并组 energy");
        Assert.Equal("value:edict_size_effect", c2.Groups[0].Multiplier, "往返倍率");
        Assert.NotNull(c2.Groups[1].Trigger, "往返 trigger 组");
        Assert.Equal(1, p2.Groups.Count, "往返 produces");
    }

    [Test]
    public void TriggerHashIsStableFourHex()
    {
        // 4 位稳定哈希（显示用）——同一文本稳定、不同文本大概率不同
        var a = StrategicResourceEngine.TriggerHash("is_ai = no");
        var b = StrategicResourceEngine.TriggerHash("is_ai = no");
        var c = StrategicResourceEngine.TriggerHash("is_ai = yes");
        Assert.Equal(4, a.Length, "4 位 hex");
        Assert.Equal(a, b, "同一文本哈希稳定");
        Assert.False(a == c, "不同文本不同哈希");
        Assert.True(a.All(char.IsAsciiHexDigit), "hex 字符");
    }

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

    private static void WriteConfig(string root, string relPath, string content)
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
