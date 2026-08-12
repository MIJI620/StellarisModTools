/*
 * ============================================================================
 * STELLARIS STRATEGIC RESOURCE ENGINE STANDARD SPECIFICATION (v1.0)
 * ============================================================================
 * 本规范为战略资源引擎（StrategicResourceEngine）实现的唯一权威依据。
 * 本规范在逻辑上优先于任何现有代码实现，所有实现偏差均视为缺陷。
 * ============================================================================
 *
 * 术语定义
 * --------
 * - 资源路径（Resource Path）：固定相对路径
 *   `common/strategic_resources/00_strategic_resources.txt`——**唯一**，引擎只处理这一个路径。
 * - 撞击扫描（Collision Scan）：SA 的 GetCollisionAsts——对指定相对路径，每个 root 的
 *   绝对路径**各自独立解析**成独立 AST（不合并——与常规覆盖规则隔离）。
 * - 顶层 key（Top-level Key）：资源文件顶层块名（如 sr_zro）。
 * - 字段行（Field Row）：资源块内的一个 Simple（key = value）或 Block（key = { ... }）。
 *
 *
 * 初始化（强制）
 * --------------
 * 引擎初始化（App 挂载）时**立即重扫描一次**：对资源路径做撞击扫描。
 * ScanAll 幂等（内部缓存；数据变更可显式重扫）。
 *
 * 单 root 回退：路径未撞击（只有一个 root 或路径不存在于多个 root）时，
 * 回退读取常规 _configResults（GetConfig）——保证单 root 也能显示。
 *
 *
 * 合并规则（强制）
 * ----------------
 * 1) 按**顶层 key 合并**成超大表：同一 key 出现在多个 root → 合并为**一条**资源，
 *    记录出现过的 root 列表（去重）。
 * 2) 块内 Simple/Block 字段**同 key 合并为一行**（不是每 root 一行！）——三列：
 *    - 第 1 列：字段 key。
 *    - 第 2 列：值下拉（选项 = 使用的值；**同值无下拉能力**直接显示；含"自定义"特殊选项）。
 *    - 第 3 列：非自定义 → 显示来源（选中方案的 root）；自定义 → 输入框（填希望的值）。
 * 3) **细节不省**：Block 显示完整子内容（{ … } 内展开——统一经 SA.SerializeNodes，
 *    完整递归序列化含嵌套/注释；禁止自行拼文本/简写嵌套块，2026-08 起各引擎统一）。
 * 4) 本地化：资源名 = {key}（无前缀），描述 = {key}_desc（相对路径替换——键后缀标准修改）；
 *    本地化区**语种切换在顶**（默认当前界面语言），下面一行一个（label+输入框同排）：
 *    名字逻辑值 / 名字显示值 / 描述逻辑值 / 描述显示值（纯名字标题不重复显示）。
 * 5) 默认选中方案：选 **Roots 更靠后**的（后读覆盖语义——同值/不同值统一）。
 * 6) 显示最少分隔符：连续空白（含回车/多空格）压缩为 1 个空格（保存仍用原文）。
 * 7) 扩展接口 GetResourceKeys()：外部查询目前有哪些资源种类（key 表格）。
 *
 * 隔离性：撞击扫描只读、不写入 SA 内部状态（不影响常规扫描/覆盖合并）。
 *
 *
 * 保存（强制——保存整个文件，非每行）
 * ------------------------------------
 * 1) **搜索框左侧**一个"保存"按钮（所有页保存按钮文案统一只叫"保存"）。
 * 2) SaveAll：**所有行**按当前选中方案（含自定义值 CustomValue）分组到各 root，
 *    在对应 root 的撞击 AST 中替换字段（CloneNode 深拷贝），经 SA
 *    WriteCollisionFile 写盘（引擎不直接操作底层）。失败收集错误（仅失败弹窗）。
 * 3) **自定义值重新 AST 解析**：以 `{` 开头的内容直接解析（块——含全部子值，
 *    避免包装后裸值被拆散）；否则包装成块再取第一个子节点（支持 Simple 值/引号）。
 *    解析失败回退 Simple 原文。
 *
 *
 * ============================================================================
 * 附录 A：公开 API 索引
 * ============================================================================
 * 文件：Stellaris.Engine/StrategicResource/StrategicResourceEngine.cs
 * - ResourceRelPath     固定资源路径常量。
 * - ScanAll()           初始化重扫描（幂等——撞击扫描 + 顶层 key 合并）。
 * - GetEntries()        超大表（StrategicResourceEntry：Key + Rows + Roots + 本地化）。
 * - LoadLocalisation()  名字 {key} / 描述 {key}_desc 本地化读取。
 * - MarkRowForSave()    行保存登记。
 * - SaveAll()           统一保存（按选中方案写对应 root——经 SA WriteCollisionFile）。
 * 数据类：ResourceFieldRow（FieldKey/IsBlock/Variants/SelectedIndex/CustomValue）、
 *         FieldVariant（Root/ValueNode/DisplayValue/SourceLabel）。
 * 页面：Stellaris.Editor/Pages/StrategicResourcePage.xaml.cs（搜索栏 + 保存按钮 +
 *       资源列表 + 字段行 DataGrid（3 列模板——列宽可调）+ 本地化区（语种切换 + 4 行）。
 * 测试：Stellaris.Tests/StrategicResourceTests.cs（合并/方案/单 root 回退/保存写盘）。
 *
 * ============================================================================
 * 规范结束
 * ============================================================================
 */

namespace Stellaris.Engine.StrategicResource;
