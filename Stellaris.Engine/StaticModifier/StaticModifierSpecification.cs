/*
 * ============================================================================
 * STELLARIS STATIC MODIFIER DICTIONARY ENGINE STANDARD SPECIFICATION (v1.0)
 * ============================================================================
 * 本规范为加成字典引擎（StaticModifierEngine）实现的唯一权威依据。
 * 实现必须严格遵循本规范中的扫描顺序、排除规则与断言语义。
 * 本规范在逻辑上优先于任何现有代码实现，所有实现偏差均视为缺陷。
 * ============================================================================
 *
 * 术语定义
 * --------
 * - 基础（Base）：可在 modifier 块中引用的单个加成（如 pop_happiness）。由本地化
 *   `mod_{代码键}` 词条确认存在（用户规则：本地化 key 是在代码 key 前面加前缀）。
 *   普通基础无任何定义文件；被 scripted/static 断言后成为自定义基础。
 * - 静态定义（Static Definition）：common/static_modifiers 顶层块（原版约定：
 *   静态文件里的定义只影响显示，不产生代码引用语义）。
 * - 自定义（Custom）：common/static_modifiers 顶层块条目。
 * - 自定义基础（Custom Base）：scripted_modifiers 顶层块被代码引用断言后形成的基础。
 * - 引用（Reference）：任意文件中 modifier 块内的键（如 `modifier = { pop_happiness = 0.5 }`
 *   中的 pop_happiness）。引用即断言：该基础存在。
 * - 排除规则（Exclusion Rules）：只对 AST（WalkModifierRefs）应用，自定义块内键不应用。
 * - 回退（Fallback）：非自定义基础翻译时，找不到带前缀 key 可回退找不带前缀的 key。
 *
 *
 * 扫描流程（强制顺序，不可调整）
 * ------------------------------
 * 1) 自定义基础定义：common/static_modifiers 顶层块（静态）+ common/scripted_modifiers
 *    顶层块（自定义——代码里有就要显示——先创建基础）。
 * 2) 代码引用：全 AST 遍历，任意文件 modifier 块 → 引用（weight/ai_weight 父键跳过）。
 *    引用即断言（TryResolveBase 未命中 → 创建基础）。
 * 3) 本地化词条：mod_ 前缀词条 → 基础（最后才搜本地化）。
 *    - 普通属性回退：找不到带前缀 key 可回退找不带前缀的 key。
 *    - 自定义基础不回退（自定义翻译键 = 名字本身不带 mod_；scripted 翻译键 = mod_+代码 key）。
 *
 * 扫描幂等与并发：ScanAll 全程持锁 + 幂等（后台预热与 UI 查询并发安全）。
 *
 *
 * 排除规则（只对 AST 应用——来自 rules/modifier_exclusions.json，经 RulesReader）
 * ------------------------------------------------------------------------------
 * - exclude_keys：键 → 检查深度列表（0=自身、1=父、2=祖父……）——随机概率类祖先链。
 * - exclude_exact：modifier 块内引用键**精确等于**该列表（add/mult/trigger/factor 等语法成分）
 *   → 不当作引用。
 * - exclude_keywords：引用键包含该词（如 "$"）即排除（忽略大小写）。
 * - exclude_values：值为 yes/no 的默认无效。
 * - 键/值含 $...$（如 $MODIFIER$、$0x0x$）→ 内联变量引用替换——忽略（不当作真实引用）。
 *
 *
 * 本地化键规则（用户定义，强制）
 * ------------------------------
 * - 本地化 key = mod_ + 代码 key（无条件——scripted 代码 key mod_trade_league_3 →
 *   本地化 mod_mod_trade_league_3）。
 * - 引用键 = 代码 key 原样（mod_xxx 保留——不删前缀）。
 * - 引用即断言（TryResolveBase 未命中 → 创建基础）。
 * - scripted/static 定义本身不单独断言（登记 _definitions——被引用后标记 IsCustomBase）。
 * - 自定义基础翻译不回退不带前缀（仅非自定义属性可回退——4.5 段）。
 *
 *
 * 同 key 多文件（覆盖语义）
 * --------------------------
 * - static=最早文件启用、scripted=最晚文件启用（GetActiveFile）；详情标"使用中"。
 * - static/scripted 同 key 显示 2 行（静态+自定义），不合并。
 *
 *
 * 静态加成特殊字段（用户 2026-08：自身语义，不是引用）
 * ----------------------------------------------------
 * - icon / icon_frame / hide_from_country_list / important / custom_tooltip /
 *   show_only_custom_tooltip（+ format / is_custom_tooltip）——解析入 StaticModifierEntry
 *   （Icon/IconFrame/Hidden/Important/CustomTooltip/ShowOnlyCustomTooltip），**不当引用处理**。
 * - 引用键表 = 其余数值 Simple（BaseRefs）。
 *
 *
 * 真实本地化键（LocKey，用户 2026-08：不拼 mod_+Name）
 * ---------------------------------------------------
 * - BaseModifier.LocKey：扫描时记录**实际命中词条键原样大小写**（本地化前缀无视大小写，
 *   真实键可能是 `MOD_SHIP_SPEED_MULT`）；无 mod_ 词条时回退不带前缀键（无视大小写查找）。
 *   查询/显示一律用 LocKey（详情"本地化键"行 + 本地化组件），ModKey 仅作缺省。
 *
 *
 * 保存（用户 2026-08：参考法令——待保存索引 + 字段级 + 本地化；全部经 SA）
 * -----------------------------------------------------------------------
 * - 待保存登记：_dirty（条目 → 字段集，StaticField：Icon/IconFrame/Hidden/Important/
 *   CustomTooltip/ShowOnly/Refs）+ _removed（删除登记）+ MarkDirty/MarkItemDirty/SetEntryRefs。
 * - StaticModifierEntry.OriginalBlock：解析时保留块引用——保存**字段级**更新（只写脏字段，
 *   未编辑/未知字段保留 AST 原样）；新建 = 全字段写。
 * - 删除 = 登记式（RemoveItem(entry)：新建项内存移除 + 扫描项登记 _removed；保存时从文件 AST
 *   移除块 + 删本地化词条；GetItems 过滤 _removed）。
 * - SaveAll(modPrefix)：按所属文件分组（TargetRelPath ?? 默认 00_{prefix}_static_modifiers.txt）→
 *   删除块 + 字段级应用 → WriteFile；本地化目标 **modifiers_{ModPrefix}_l_{lang}.yml**（static_ 删掉）；
 *   成功后清登记。落盘唯一入口 = 用户显式触发（页面"保存"按钮 → SaveRunner）。
 *
 *
 * ============================================================================
 * 附录 A：公开 API 索引
 * ============================================================================
 * 文件：Stellaris.Engine/StaticModifier/StaticModifierEngine.cs
 * - GetBaseModifiersOf(customName)        按自定义名查其基础（含调用方统计）。
 * - GetAllBaseModifiers()                 全部基础。
 * - GetBaseModifier(name)                 按名查基础（引用即断言）。
 * - Search(keyword)               关键词搜索（基础名/本地化；* ? | 通配符；忽略大小写）。
 * - ScanAll()                     全量扫描（幂等；全程持锁）。
 * - GetItems()                    全部条目 = 扫描现有 + 内存新建（**删除登记项过滤**）。
 * - AddItem(key)                  内存新建（登记待保存）。
 * - RemoveItem(entry)             **登记式删除**（新建项内存移除 + _removed 登记）。
 * - MarkDirty(entry, field) / MarkItemDirty(entry)  字段级/条目级待保存登记。
 * - SetEntryRefs(entry, refs)     更新引用键表（页面加成表格 → BaseRefs + 登记 Refs）。
 * - UpdateItemMeta/UpdateItemIcon/UpdateItemSourceFile  内存更新（特殊字段/图标/所属文件）。
 * - SaveAll(modPrefix)            统一保存（用户触发）：删除块 + 字段级应用 + 本地化 + 清登记。
 * - TargetRelPath(entry, prefix)  目标文件（SourceFile ?? 默认）。
 * - HasDirty                      是否有待保存改动。
 * - StaticField                   可保存字段常量。
 * 数据类：BaseModifier（Name/ModKey/**LocKey**、Localisations/DefinitionSources/Users/Refs/IsCustomBase…）、
 *         StaticModifierEntry（Name/Icon/IconFrame/Hidden/Important/CustomTooltip/
 *         ShowOnlyCustomTooltip/BaseRefs/SourceFile/**OriginalBlock**——static_modifiers 顶层块=静态加成）。
 * 规则读取：Stellaris.Parser/Rules/RulesReader.cs（ExcludeKeys/ExcludeKeywords/ExcludeExact/
 *         ExcludeValues/GetOverwriteRule——SA 与引擎共用）。
 * 测试：Stellaris.Tests/ModifierDictionaryTests.cs（断言/排除/同 key 双定义等）。
 *
 * ============================================================================
 * 规范结束
 * ============================================================================
 */

namespace Stellaris.Engine.StaticModifier;
