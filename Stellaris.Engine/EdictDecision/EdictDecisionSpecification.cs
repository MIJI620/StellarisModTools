/*
 * ============================================================================
 * STELLARIS EDICT / DECISION EDITOR ENGINE STANDARD SPECIFICATION (v1.0)
 * ============================================================================
 * 本规范为法令/决议可视化引擎（EdictDecisionEngine）实现的唯一权威依据。
 * 本规范在逻辑上优先于任何现有代码实现，所有实现偏差均视为缺陷。
 * ============================================================================
 *
 * 术语定义
 * --------
 * - 法令（Edict）：common/edicts 下的顶层块条目。
 * - 星球决议（Decision）：common/decisions 下的顶层块条目。
 * - 本地化键（Localisation Key）：
 *   - 法令名字 = `edict_{key}`；法令描述 = `edict_{key}_desc`（edict_ 前缀——用户定义）。
 *   - 决议名字 = `{key}`（**无前缀**）；决议描述 = `{key}_desc`。
 * - 效果（Effect）：modifier 块内的基础名 + 数值（代码键——不带 mod_ 前缀）。
 * - 条件预设（Condition Preset）：可用条件的预设枚举（本期 4 个——专用预设引擎后续另做）。
 *
 *
 * 扫描与解析（强制）
 * ------------------
 * 1) 经 SA 读取 common/edicts 与 common/decisions 下全部文件（GetAllConfigs）。
 * 2) 顶层块 = 条目：`x = { ... }` 解析为 Block、`x = { }` 空块解析为 List——**两者都收**。
 * 3) 解析字段：icon（图标 gfx 名）、cost（花费）、
 *    modifier（效果——子键排除 add/mult/trigger_scope/factor/base/set/mode 语法成分与 $ 变量）。
 * 4) 类型过滤：GetItems(kind) **必须过滤扫描结果**（扫描结果按 Kind 分组后 Where）。
 *
 *
 * 本地化显示
 * ----------
 * 名字/描述读取：当前界面语言 → english → 回退 key/空。
 * 显示名规则见"本地化键"节（法令带 edict_ 前缀、决议无前缀、描述带 _desc 后缀）。
 *
 *
 * 本期不落盘（强制）——已扩展为字段级保存（2026-08）
 * ------------------------------------------------
 * 所有编辑仅停留内存（新建/修改/删除）；保存 = 用户显式触发（SaveRunner），
 * 经引擎 SaveAll：只写**改动登记**的项与字段（字段级脏追踪——改哪写哪，没改的保留原节点含注释），
 * 数据源 = SA GetConfig 合并 AST（不重建文件），写 = SA WriteFile（roots[-1] + 自动建目录）；
 * 本地化写 edicts_{ModPrefix}_l_{lang}.yml / decisions_{ModPrefix}_l_{lang}.yml
 * （键已存在 → 更新源文件；键不存在 → 写目标文件；新旧位置都登记待保存，writeIfEmpty 清理）。
 * 删除：登记 _removed → 保存时从文件 AST 移除块 + 配套删除本地化词条。
 *
 * 统一序列化/解析（强制，2026-08）：字段原文 → AST 节点统一经 SA.ParseSingleNode；
 * 块 → 文本统一经 SA.SerializeNodes（完整递归，嵌套块/注释不丢）。禁止自行 new Lexer/Parser。
 *
 * 删除（用户 2026-08：扫描项也可删——登记式，防数据丢失）：RemoveItem 登记 _removed
 * （新建项同时从内存移除），**不直接改内存**；保存时从文件 AST 移除块 + 配套删除本地化词条；
 * GetItems **过滤 _removed**（删除登记项不再显示）。
 *
 *
 * 条件预设（本期 4 个——枚举 ConditionPreset）
 * ---------------------------------------------
 * - AlwaysYes：无条件可用（always=yes）
 * - AlwaysNo ：无条件禁用（always=no）
 * - AiYes    ：仅限电脑（is_ai=yes）
 * - AiNo     ：仅限玩家（is_ai=no）
 * 专用预设引擎（告知不同级别可用哪些预设）为后续独立任务。
 *
 *
 * 效果编辑（加成字典联动）
 * ------------------------
 * 效果从加成字典（StaticModifierEngine.Search）选择基础——BaseModifier.Name 为代码键
 * （不带 mod_ 前缀）；添加时带数值。排除规则只对 AST 应用（与加成字典一致）。
 *
 *
 * ============================================================================
 * 附录 A：公开 API 索引
 * ============================================================================
 * 文件：Stellaris.Engine/EdictDecision/EdictDecisionEngine.cs
 * - GetItems(kind)            全部条目 = 扫描现有 + 内存新建（按类型过滤；**删除登记项过滤**）。
 * - AddItem(kind, key)        内存新建条目（不落盘）。
 * - RemoveItem(item)          **登记式删除**（新建项内存移除 + _removed 登记；保存时移出块、删本地化词条）。
 * - MarkDirty(item, field)    登记某字段被修改（字段级脏追踪——保存只写登记字段）。
 * - MarkItemDirty(item)       登记条目有改动（空字段集——只写文件不动字段；所属文件等非字段变化）。
 * - SaveAll(modPrefix)        统一保存（用户显式触发）：删除块 + 字段级应用 dirty 块 + 本地化 + 成功后清登记。
 * - TargetRelPath(item, prefix) 目标文件（SourceRelPath ?? 默认 common/edicts|decisions/00_{prefix}_*.txt）。
 * - HasDirty                   是否有待保存改动。
 * - LocalisationKey(item)     本地化键（法令 edict_{key}；决议 {key}）。
 * - DescKey(item)             描述键（+ _desc）。
 * - LocalisedName(item, …)    读取名字/描述本地化（当前语言 → english → 回退）并写回条目。
 * 数据类：EdictDecisionItem（Kind/Key/SourceRelPath/NameLogical/DescLogical/Icon/Cost/
 *         Effects(Base,Value)/Condition/SourceFile）。
 * 页面：Stellaris.Editor/Pages/EdictDecisionPage.xaml.cs（综合页 4 选项卡：法令/星球决议/静态加成/战略资源；
 *       列表 + 表单；底部"所属文件"行——只填文件名，前缀 common/edicts|decisions/ 自动隐藏/补；
 *       4 选项卡左列表宽度调整通用）。
 * 测试：Stellaris.Tests/EdictDecisionTests.cs（扫描解析/内存新建移除/条件预设）。
 *
 * ============================================================================
 * 规范结束
 * ============================================================================
 */

namespace Stellaris.Engine.EdictDecision;
