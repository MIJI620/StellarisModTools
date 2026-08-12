/*
 * ============================================================================
 * STELLARIS TECHNOLOGY ENGINE STANDARD SPECIFICATION (v1.0)
 * ============================================================================
 * 本规范为科技引擎（TechnologyEngine）实现的唯一权威依据。
 * 本规范在逻辑上优先于任何现有代码实现，所有实现偏差均视为缺陷。
 * ============================================================================
 *
 * 术语定义
 * --------
 * - 科技（TechNode）：common/technology/*.txt 顶层块条目（块 key 唯一，忽略大小写）。
 * - 所属文件（OwnerFile）：科技所在文件相对路径（扫描时记录；弹窗"所属文件"行可改；
 *   新建默认 `common/technology/00_{ModPrefix}_technologies.txt`）。
 * - 脏字段（DirtyFields）：弹窗提交时与打开快照比较，标记**修改过的字段**（TechField 常量，
 *   15 个：area/tier/cost/levels/cost_per_level/category/prerequisites/icon/weight/start_tech/
 *   potential/modifier/weight_modifier/ai_weight/prereqfor_desc）。
 * - 删除登记（RemovedKeys）：删除科技**只登记 key + 原文件**，不改内存、绘制跳过。
 * - 字段形态（原版实测，2026-08）：Block = ai_weight/weight_modifier/modifier/potential/prereqfor_desc
 *   （cost 可为块）；List = prerequisites/category；Simple = area/weight/cost/tier/levels/cost_per_level/
 *   icon/start_tech。allow 字段原版不存在；factor 非独立字段。
 *
 *
 * 扫描与解析（强制）
 * ------------------
 * 1) 经 SA GetFilesInDirectory("common/technology", "*.txt") 扫描全部文件 → GetConfig 合并 AST。
 * 2) 顶层 Block = TechNode；字段解析：
 *    - Simple：area/tier/cost/levels/cost_per_level/icon/weight/start_tech/is_rare/is_dangerous 等；
 *    - cost 为 **Block**（cost = { factor = ... } 动态花费）→ 原文存 CostRaw（弹窗"自定义"模式）；
 *    - Block 类原文（potential/weight_modifier/ai_weight/prereqfor_desc/cost 块）→ **经 SA.SerializeNodes
 *      完整递归序列化**存原文（嵌套块/注释/格式全保留——禁止简写嵌套块，曾因简写丢内容）；
 *    - 文本 → AST 节点（保存时回写）→ **统一经 SA.ParseSingleNode**（禁止自行 new Lexer/Parser）。
 * 3) 索引：byKey/byArea/byCategory/byTier + 后继反查（children）+ modifier 缓存 + 科技文件缓存。
 * 4) ScanAll 幂等（清索引重扫）。
 *
 *
 * 布局与渲染（简述——详见 TechnologyLayout / TechnologyRenderer）
 * ----------------------------------------------------------------
 * - 布局：ComputeLabelMode（文本标签模式，当前唯一在用；旧连线模式已隐藏）。
 * - 渲染：RenderLabel/RenderLabelTile（Skia 离屏；导出 PNG 分块拼合）。
 * - 页面：TechnologyGraphPage（缩放/平移/搜索/右键菜单）。
 *
 *
 * 内存编辑（强制）
 * ----------------
 * - AddItem/UpdateItem：改内存索引 + **登记 dirty + 所属文件到待保存索引**（不落盘）。
 * - UpdateItem：OwnerFile 改动 → 旧文件若除本科技外无其他内容 → **从待写索引移出**（用户规则）。
 * - RemoveItem：仅**保存落盘成功后**调用（删除登记兑现——防数据丢失）。
 * - 弹窗 Commit：MarkDirty（与打开时快照比较 15 字段）；新建全字段由引擎写。
 *
 *
 * 保存（强制——所有保存必须显式登记，用户触发才落盘）
 * ----------------------------------------------------
 * 1) **显式登记**：任何改动（创建/修改/删除/本地化）只改内存 + 登记到待保存索引
 *    （_pendingTechFiles/_dirtyTechKeys/_removedKeys/_removedFiles/_pendingLocFiles/_pendingLocClean）。
 *    落盘唯一入口 = 用户显式触发（右键菜单"保存"），经 SaveRunner（转圈进度 + 后台线程 +
 *    完成关闭 + 仅失败弹窗）。**禁止失焦即落盘/自动落盘**（曾实现被否决）。
 * 2) **删除不改内存**：删除只登记 key + 原文件，页面绘制用 IsRemoved 跳过；
 *    保存时才从文件 AST 移除块（经 SA WriteFile）；全部成功后才 RemoveItem + 清登记。
 * 3) **字段级落盘**（数据源 = SA GetConfig 合并 AST，不重建文件）：对 dirty 科技块
 *    只写 DirtyFields（未编辑字段/注释保留原样）；块不存在 → 新建全字段。
 * 4) **格式化省略规则**（NormalizeTechBlock——保存时对文件所有科技块应用，"就当格式化"）：
 *    icon 值 = 自身 key、levels = 1、cost_per_level 无循环（无 levels 或 levels=1）、
 *    空 prerequisites/modifier/prereqfor_desc → 自动省略。
 *    ⚠️ **只删上述白名单省略项**——任何**未知/新关键词**字段一律保留（解析不入 TechNode、
 *    保存绝不写入/删除——用户 2026-08："不能保证科技以后会不会有什么新的关键词"，规整化绝不丢未知字段；
 *    如原版已出现的 starting_potential / technology_swap / ai_update_type / weight_groups 等弹窗未覆盖字段）。
 *    法令/决议同规则（NormalizeEdictBlock：只删空 potential/allow/effect/modifier，未知字段保留）。
 *    注：hide_from_country_list/important/icon_frame/custom_tooltip/show_only_custom_tooltip 是
 *    **静态加成（static_modifiers）特殊字段**（StaticModifierEngine 处理），非科技字段。
 * 5) **本地化**：目标文件 `localisation/{lang}/technologies_{ModPrefix}_l_{lang}.yml`；
 *    弹窗失焦经 UpdateItemLocalisation 只写本地化引擎内存 + 登记文件（旧位置登记待清理，
 *    writeIfEmpty 防磁盘残留重复）；保存统一落盘；删除科技配套删除名称/描述词条。
 * 6) **格式化保存**：用户一个没改过时，右键某科技点"保存" → 该科技所在文件登记待保存，
 *    保存即写（登记即写，无条件 WriteFile）。
 * 7) **刷新 = 重载入**：Reload() 重扫 AST + 清空全部登记（未保存改动丢弃、删除恢复）。
 * 8) 写盘一律经 SA WriteFile / WriteLocalisation（roots[-1] + 自动建目录）；禁止自创写盘路径。
 *
 *
 * 字段形态与弹窗（强制）
 * ---------------------
 * - cost：下拉"基础/自定义"——基础 = Simple 数值（预填右键小列 cost）；自定义 = 多行输入
 *   （≥3 行），默认 factor = 1，保存为 cost = { ... } 块（CostRaw）。
 * - weight 与循环增长（cost_per_level）同行；cost 在权重行下一行。
 * - 初始科技勾选框在 key 输入框末端（原本 start_tech 自动勾上）。
 * - 图标下拉可输入过滤（包含匹配 key/本地化名）；图标 = 自身 key → 保存省略。
 * - 本地化编辑复用 LocalisationEditBox（名称键 = key、描述键 = {key}_desc）。
 *
 *
 * ============================================================================
 * 附录 A：公开 API 索引
 * ============================================================================
 * 文件：Stellaris.Engine/Technology/TechnologyEngine.cs
 * - ScanAll()                    重扫描（幂等）：common/technology 顶层块 → TechNode + 索引。
 * - GetAll()/Get(key)/GetByArea/GetByCategory/GetByTier/GetPrerequisites/GetChildren
 * - GetModifierLines(tech, lang) modifier 显示行（复用 StaticModifierEngine）。
 * - LocalisedName/LocalisedDesc  本地化读取（Adapter）。
 * - AddItem/UpdateItem/RemoveItem 内存编辑（Add/Update 登记 dirty + 文件）。
 * - RegisterRemoved(tech)        删除登记（不改内存；保存落盘成功后才移除内存）。
 * - IsRemoved(key)/RemovedKeys   删除登记查询（页面绘制过滤）。
 * - UpdateItemLocalisation(lang, key, text, modPrefix) 本地化内存写 + 登记（不落盘）。
 * - Reload()                     重载入：重扫 + 清空全部登记。
 * - SaveAll(modPrefix)           统一保存（用户触发）：删除块 + 字段级应用 dirty 块 +
 *                                 格式化省略规则 + 本地化落盘 + 成功后清登记。
 * - HasDirty                     是否有待保存改动。
 * 数据类：TechNode（Key/Area/Categories/Tier/Cost/CostRaw/Levels/CostPerLevel/Prerequisites/
 *         IsRare/IsDangerous/StartTech/Icon/Weight/PotentialRaw/ModifierEntries/
 *         WeightModifierRaw/AiWeightRaw/PrereqForDesc/OwnerFile/DirtyFields）。
 * 布局：Stellaris.Engine/Technology/TechnologyLayout.cs（ComputeLabelMode 当前唯一在用）。
 * 渲染：Stellaris.Engine/Technology/TechnologyRenderer.cs（RenderLabel/RenderLabelTile）。
 * 弹窗：Stellaris.Editor/Pages/TechEditDialog.cs（新建/修改共用；本地化直写内存+登记）。
 * 页面：Stellaris.Editor/Pages/TechnologyGraphPage.xaml.cs（右键 新建/修改/删除/保存/导出；
 *        刷新=重载入）。
 * 测试：Stellaris.Tests/（科技布局/引擎扫描在既有覆盖中）。
 *
 * ============================================================================
 * 规范结束
 * ============================================================================
 */

namespace Stellaris.Engine.Technology;
