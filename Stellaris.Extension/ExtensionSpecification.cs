/*
 * ============================================================================
 * STELLARIS EXTENSION TOOL STANDARD SPECIFICATION (v3.2)
 * ============================================================================
 * 本规范为拓展工具（Stellaris.Extension）实现的唯一权威依据。
 * 本规范在逻辑上优先于任何现有代码实现，所有实现偏差均视为缺陷。
 *
 * v3.4 变更（相对 v3.3，2026-08——集合布尔运算，用户确认）：
 * - **extract 加 as（命名集合）**：`{ "rule":"extract", ..., "as": "名字" }`——本次提取新增的值
 *   同时存 `state.Sets[名字]`（与 foreach 的 As 绑定名共用字段，不同步骤各自语义）。
 * - **新步骤 set（集合布尔运算）**：`{ "rule":"set", "op":"subtract|union|intersect",
 *   "left":"集合名|$values|[\"字面量\",...]", "right":"集合名|$values|[\"字面量\",...]" }`——
 *   对两个集合做差集/并集/交集，**结果替换 values 通道**（write 直接输出）。操作数：
 *   字符串（命名集合名 / `"$values"` 默认通道）或**字符串数组（字面量集合，如
 *   `["ap_defender_of_the_galaxy"]`——显式排除项直接写）**。典型：
 *   `extract(as=all_perks) → extract(as=exclude) → set(subtract, all_perks, exclude) →
 *   set(subtract, "$values", ["ap_x"]) → write`（根 key 排除指定列表 + 字面量项）。
 * - 撤掉 v3.3 的 extract exclude 特例（统一 as + set）。
 *
 * v3.3 变更（相对 v3.2）：
 * - foreach over 支持 `"values"`（引用上一步 extract 的 values 通道，先拷贝）。
 * - write 加 `append`（SA 层拼接追加——多轮生成同一文件）。
 *
 * v3.2 变更（相对 v3.1，2026-08——GUI 文件自动化，真实场景：原版
 * topbar_traditions_view.gui → mod 版，8→2000 飞升槽 + 结构改造）：
 * - **add 步骤**（创建节点）：`{ "rule":"add", "file", "path", "position":
 *   "Append|Before|After", "text", "existing"? , "save"? }`——text 文本模板
 *   （{绑定} 与 {expr:...}）解析为 AST 节点（可多根 → 逐节点 Append），经 SA
 *   AddConfigNode 插入（Append=父 children 末尾；Before/After=目标同层前/后）。
 *   existing（可选）={rule:[...]}：父 children 中第一个 NodeMatches 命中的节点
 *   原地替换（保留位置）——8 槽→新坐标用；未命中则按 position 插入。
 * - **delete 步骤**：`{ "rule":"delete", "file", "path" }`——RemoveConfigNode。
 * - **save 步骤**：`{ "rule":"save", "file" }`——WriteFile 落盘 roots[-1]。
 * - **foreach 数值范围**：over 支持 `"0..1999"`（单元素字符串，两侧非负整数，
 *   上限 100 万）——展开为连续整数；每轮绑定 As → 整数（Bindings 供 {expr:}）。
 * - **模板表达式 {expr:...}**：TemplateMath（整数算术 + - * / %、括号、数组字面量
 *   [a,b,...] 与索引、比较、三元 c?a:b、绑定变量）——用于 text/path 中数值计算
 *   （如槽坐标 `x = {expr:[10,68,126,184,39,97,155,213][n%8]}`）。
 * - **add/delete save 开关**：大量轮次（如 2000 槽）内设 save=false 只改内存，
 *   部署末尾 save 步骤统一落盘一次（避免每轮全文件序列化）。
 *
 * v3.1 变更（相对 v3.0）：
 * - **foreach 迭代执行**：`{ "rule":"foreach", "over":[字面量或数值范围], "as":
 *   "绑定名", "steps":[子步骤] }`——每轮把子步骤 JSON 文本中的 {绑定名} 替换为
 *   当前值后执行（所有字段生效，含 match/file/format）；**轮间隔离**（每轮开始
 *   清空双通道）。- write 加 target（source=nodes 取数）：
 *   `"value"(缺省) | "key" | "simple"(节点整体序列化)`。
 *
 * v3.0 变更（相对 v2.0）：
 * - **定位/匹配统一为 SA 标准搜索（SelectorResolver 枝/叶语法）**——CLI 与 SA/引擎
 *   共用同一套：modify/write 的 path = 枝/叶数组（定位/取数）；extract 的 match =
 *   {rule:[...], check_rule}（全树遍历评估，mode 恒 Any）；extract 文件范围 = dir
 *   （目录前缀字符串，从旧 match.path 提升为独立字段）。旧嵌套对象语法（DrillPath /
 *   FromNestedToDot / 条件树 all/any/not + key_* / value_* / has）**全部删除**。
 * - 删 MatchCondition（条件树求值）——extract 条件用 SelectorResolver.NodeMatches。
 * - write 的 path 由嵌套对象改为枝/叶数组（从 node 子层开始定位取数）。
 * ============================================================================
 *
 * 术语定义
 * --------
 * - 拓展（Extension）：**半隐藏**的配置驱动自动化维护工具——双击运行的 console
 *   程序（无 GUI），按配置自动处理目标文件，命令行打印结果。**不是主程序功能**，
 *   不影响 Editor 正常流程；Editor 的 `_key_extract` 半隐藏功能保留，两条线并存。
 * - 写盘根（Write Root）：`extension_config.json` 指定的模组路径（modRoot）——
 *   所有产出文件写入该根，**与 Roots 无关**（Roots 只是读取源）。
 * - 部署（Deployment）：一次运行中的一个独立处理轮次——独立 steps 序列。
 *   一次运行 = 按顺序执行全部部署；**轮间状态独立**（每轮新建执行状态、重新读取，
 *   不共享提取结果）。一轮内可有多个 write 步骤（单轮多文件）。
 * - 执行状态（Execution State）：双通道——`Values`（有序去重字符串列表，值通道）
 *   与 `Nodes`（匹配到的 AST 节点列表，节点通道）。
 * - 枝/叶（Branch/Leaf）：SA SelectorResolver 标准搜索的两种选择器（见下）。
 *
 *
 * 统一定位语法（强制 = SA SelectorResolver 枝/叶）
 * -------------------------------------------------
 * 权威文档：Stellaris.Parser/SelectorResolver.cs 头部注释。CLI 只是它的 JSON 化。
 * - **枝**（路径元素，可继续下钻；match 与 index **互斥**）：
 *   `{ "mode": "Block|Simple|List|Any", "match": { "rule": [枝或叶...], "check_rule":
 *   "And|Or|Nor|Nand" } }`——match 枝 **mode 必填**；
 *   `{ "mode": "...", "index": 2 }`——index 枝抽第 N 个（**1 起**；mode 可选：
 *   无 mode 数全部、有 mode 数该类型；越界 = 记错误）。
 * - **叶**（判断终止点，二选一）：
 *   `{ "target": "key|value", "keywords": [...], "type": "equals|start|end|contains",
 *   "check_rule": "And|Or|Nor|Nand" }`——检查节点自身 key/value（keywords 多值按
 *   check_rule 组合，缺省 And）；target=value 分类型：Simple=字面值 / List=元素集合
 *   包含 keywords（每个 kw 至少一个元素命中）/ Block=内容里含该 key；
 *   `{ "index": 2 }`——候选节点 Children 第 N 个存在（1 起）→ yes/false。
 * - rule 里的枝 = **存在性检查**：候选节点 Children 层存在满足该枝的节点。
 * - **逐层推进不跳层**：第 1 枝在当前层匹配，命中后下一枝在命中的 Children 层。
 *
 * CLI 用法约定（JSON 直接解析成 Dictionary 喂 SelectorResolver）：
 * - `path`（数组）= 枝序列——modify 定位 / write 取数（从每个 Nodes 节点**子层**开始）。
 * - extract `match` = `{ "rule": [...], "check_rule": "And" }`——对 Roots 全树**每个节点**
 *   评估（mode 恒 Any，不限定类型）；空 = 匹配全部。
 * - extract `dir` = 目录前缀字符串（文件范围，目录边界匹配）。
 * - extract `from` = 枝/叶数组（keys/nodes 收集范围起点；缺省 = 文件根层）。
 *
 *
 * 两层配置（强制）
 * ----------------
 * 1) `extension_config.json`（**exe 同位置**，App 层启动配置——同 Editor
 *    `config/user_prefs.json` 惯例，直接 File 读写）：
 *    `{ "modRoot": "<模组目录绝对路径>" }`——只告知处理/写入哪一个模组（一个路径）。
 * 2) `_extension.json`（**模组 .smt/ 下**，半隐藏——下划线开头）：
 *    `{ "roots": [读取源...], "deployments": [...] }`。
 *    - `roots`：读取源（游戏本体等，经 SA 读取；**不含写盘根也能工作**）。
 *    - `deployments`：部署列表，每轮 = `{ "roots": [可选，覆盖全局], "steps": [...] }`。
 *
 *
 * 执行模型（强制）
 * ----------------
 * 启动：读 extension_config.json → 得 modRoot → 读 `{modRoot}/.smt/_extension.json`
 * → 建 StellarisAdapter（roots 注入）→ 顺序执行全部部署 → 命令行打印每轮结果。
 *
 * 每轮：新建 ExecutionState（Values/Nodes 清空）→ 顺序执行该轮 steps。
 * **deployment 级 roots（覆盖语义）**：deployment 声明 `roots` → 该轮新建独立
 * StellarisAdapter（完全覆盖全局 roots，只读该轮 roots）；缺省 → 继承全局 roots
 * 的共享 adapter。轮间本就状态独立，roots 独立后彻底解耦。
 * 每步一个规则类型，按 `rule` 字段 switch 分发；新规则 = 新 case + handler，
 * 不动已有步骤。步骤失败：打印错误并**中止本轮**（其余轮继续）；关键错误写 exe
 * 同位置 `error.log`。
 *
 * 中间数据（双通道）：
 * - `Values: List<string>`——值通道（extract values/keys/engine 产出；modify source=values
 *   / write format 消费）。
 * - `Nodes: List<AstNode>`——节点通道（extract nodes/parent 产出；modify source=nodes
 *   / write serialize 消费）。
 *
 *
 * 规则 schema（强制）
 * -------------------
 * extract（匹配搜索——全树遍历 + 节点条件）
 *   { "rule": "extract",
 *     "mode": "values" | "keys" | "nodes" | "parent",  // 缺省 "values"
 *     "dir": "common/inline_scripts/zones",        // 可选：文件范围（目录前缀）
 *     "match": { "rule": [叶或枝...], "check_rule": "And" },  // 可选：节点条件（缺省匹配全部）
 *     "from": [枝/叶数组],                          // 可选：keys/nodes 收集范围起点
 *     "depth": "top" | "all",                     // 收集深度（缺省 "top"）
 *     "engine": "strategic_resource" }            // 可选：缺省 = 通用 AST 提取
 *   - 指定 `engine` → **引擎注册表调用**（见附录 A）：跳过通用 AST 提取，直接调对应引擎
 *     的综合能力（如战略资源引擎的顶层 key 合并），产出进 Values。
 *   - 通用提取：遍历 Roots 全部配置文件（合并 AST）递归每个节点，`match.rule` 条件
 *     （check_rule 组合，mode 恒 Any）命中 → 按 mode 收集。
 *   - `mode=values`（默认）：**仅 Simple/List 有值**——Simple 匹配取节点 Value；List 匹配
 *     取子值；Block 匹配**无值跳过**；去重保序。
 *   - `mode=keys`：收集匹配节点的 Key（去重保序）——块名/资源种类等场景；
 *     **收集范围由 from/depth 控制**（缺省 = 顶层，不混层级）：
 *     - `from`（枝/叶数组）可选：范围起点（SelectorResolver 定位）——只收该路径下子树；
 *       缺省 = 文件根层。depth=top = 起点的直接子层（父 ∈ 起点命中集）；all = 子树任意层。
 *     - 例：收 country.buildings 下直接子块名 → keys + from=[枝(Block,country), 枝(Block,buildings)]。
 *   - `mode=nodes`：匹配节点本身进 Nodes（**深拷贝 CloneNode**——节点是 SA 共享 AST
 *     引用，拷贝后 modify 不污染 SA 缓存；引用去重）。**收集范围由 from/depth 控制**
 *     （与 keys 同一套语义）。
 *   - `mode=parent`：匹配节点的**父节点**进 Nodes（通用——任意节点都有父节点；
 *     顶层节点无父 → 跳过；同样深拷贝）。
 *   - 条件表达（match.rule 里的叶/枝）——全树遍历对每个节点评估：
 *     - key 匹配：`{ "target": "key", "keywords": ["utility_component_template"] }`
 *     - key 模式：`{ "target": "key", "type": "start|end|contains", "keywords": [...] }`
 *     - 值匹配（Simple）：`{ "target": "value", "keywords": ["no"] }`
 *     - 内容含字段=值：`{ "mode": "Any", "match": { "rule": [
 *         {"target":"key","keywords":["hidden"]},{"target":"value","keywords":["no"]}] } }`
 *     - 嵌套路径链（potential→from→xxx=no）：单枝多层嵌套（存在性链）
 *     - 组合：rule 数组 + check_rule（And/Or/Nor/Nand）；叶内 keywords 多值 + 叶
 *       check_rule（如 `{"target":"value","keywords":["a","b"],"check_rule":"Or"}`）
 *
 * modify（数据通道处理——source 切换两个通道）
 *   { "rule": "modify",
 *     "source": "nodes" | "values",            // 缺省 "nodes"
 *     "path": [枝/叶数组],                      // 仅 source=nodes：从每个 Nodes 节点**子层**
 *                                                // 开始定位目标字段（最后一段命中节点应用 op）
 *     "op": "set" | "add" | "mul" | "resolve" | "prefix" | "suffix" | "replace",
 *     "value": "...", "with": "..." }          // replace 用 with 作新值
 *   - `source=nodes`（缺省）：对 Nodes 每个节点，按 path（枝/叶数组）从节点子层定位字段
 *     应用 op——set：整体替换 value（字符串）；**set 定位到 Block = 用 value 作为块内容重建**
 *     （包成 "key = { value }" 重新解析，整体替换原 Children——组件模板 potential 块重建
 *     场景；value 语法错误抛异常中止本轮）；add：数值加；mul：数值乘（翻倍 = mul 2）；
 *     resolve：常量展开（@常量 → 数值：SA 解析时 Value 已求值、RawText 保留原文，resolve
 *     清 RawText 让序列化输出求值结果；Value 仍是 @ 文本时经 SA ResolveConstantInput
 *     兜底；解析失败保留原文）；prefix/suffix：字符串拼接；replace：Ordinal 替换
 *     （with 作新值）。数值 op 用 double.TryParse + InvariantCulture，失败跳过该字段。
 *   - `source=values`：对 Values 逐项应用 op（set/prefix/suffix/replace/add/mul；
 *     resolve 仅 nodes——对字符串值无意义，配置 resolve 报错）。
 *   - **修改后必须清空节点 RawText**（序列化优先用 RawText——不清空则旧文本仍输出）。
 *   - **Nodes 来自 extract 深拷贝**（CloneNode）——修改不污染 SA 共享 AST。
 *
 * clear（清空双通道——一轮内多组独立的"提取→写出"之间用）
 *   { "rule": "clear" }
 *   - extract 是**追加**语义（不清空）：上一组提取结果会混入下一组 write。
 *     每组独立的提取→写出之间加 clear，避免污染。Values 与 Nodes 全部清空。
 *
 * write（写盘——**全量重新生成**，经 SA 写盘根，自动建目录）
 *   { "rule": "write",
 *     "file": "common/inline_scripts/xxx.txt",   // 相对路径，写到 modRoot 下
 *     "output": "format" | "serialize",          // 缺省 "format"
 *     "source": "values" | "nodes",              // format 取数源（缺省 "values"）
 *     "format": "{key} = $VALUE$",               // format 模板（仅 {key} 被替换）
 *     "path": [枝/叶数组],                       // 仅 source=nodes：从节点子层定位取数字段
 *     "target": "value" | "key" | "simple",      // 可选：最终取什么（缺省 "value"）
 *     "encoding": "utf-8" | "utf-8-bom",         // 可选：显式编码（缺省按扩展名规则）
 *     "separator": "\n",                         // 缺省 "\n"
 *     "header": "...", "footer": "..." ,         // 可选：文件头/尾（字面原样）
 *     "append": true }                           // 可选：追加（缺省 false）——SA 读旧内容拼接再写（多轮生成同一文件）
 *   - `output=format`（默认）：行渲染——`source=values`（缺省）对 Values 每项执行
 *     `format.Replace("{key}", 项, Ordinal)`——**只有 {key} 被替换**，其余文本
 *     （含 $VALUE$）字面保留；`source=nodes` 时遍历 Nodes，`path`（枝/叶数组）从节点
 *     子层定位提取字段，`target` 决定取什么（缺省取 Value；"key" = 定位节点 Key；
 *     "simple" = 定位节点整体序列化文本——"没有特殊说明就是之前找到什么写什么"）。
 *     **path 缺省（不写）= 取节点自身**（target=key → 节点自身 Key——如资源块 energy
 *     输出 "energy = $VALUE$"；target=value → 节点自身 Value）。
 *     字段不存在 → 跳过该节点。
 *   - `output=serialize`：对 Nodes 每个节点 `SerializationHelper.Serialize` 后，
 *     节点间用 separator 连接（整合大文件）。
 *   - 完整文件 = header + 行/节点内容 + footer（header/footer 字面原样，不替换占位符）。
 *   - `encoding`（可选）：`"utf-8"`（无 BOM）/ `"utf-8-bom"`（带 BOM）；缺省按扩展名
 *     规则（.yml 带 BOM，其他无 BOM）——要生成本地化文件就 `encoding: "utf-8-bom"` +
 *     `header: "l_english:\n"`。
 *   - 落盘：`adapter.WriteTextFile(file, modRoot, content, encoding)`（SA 接口——写指定
 *     root 文本，自动建目录，编码支持显式指定）。
 *
 *
 * foreach（迭代执行——避免复制粘贴"几乎一样"的步骤组）
 * ------------------------------------------------------
 *   { "rule": "foreach",
 *     "over": ["physics", "engineering", "society"],   // 迭代列表（字面量 / "0..1999" 数值范围 / "values"）
 *     "as": "area",                                    // 当前值绑定名
 *     "steps": [ extract/write/modify/... ] }          // 每轮执行的子步骤（可嵌套 foreach）
 *   - 每轮迭代把子步骤 JSON 中所有 `{绑定名}` 引用替换为当前值（match 值 / file 路径 /
 *     format / header / footer 等全部字段生效），再逐条执行。
 *   - **每轮开始自动清空双通道**（轮间隔离）——子步骤的 extract/write 以当轮结果为准，
 *     不需要手动 clear。
 *   - **over 特殊值**：单元素 `"0..1999"`（两侧整数）→ 展开连续整数（Bindings 供 {expr:}）；
 *     单元素 `"values"` → 引用**上一步 extract 的 values 通道**（先拷贝——每轮 clear 不影响
 *     迭代列表）——"提取结果驱动循环"（如按树/按组处理）。
 *   - 典型用途：科技 3 组（area = physics/engineering/society 只有值不同）→ 1 组 foreach。
 *
 *
 * 首批文件（示例配置）
 * --------------------
 * 文件 1 `common/inline_scripts/shelter_all_building_set.txt`：
 *   extract(mode=values, match=[{target:key,keywords:[included_building_sets]}])
 *   → write(format="{key}")。
 *   数据源：游戏全部配置递归（已验证该 key 只出现在 common/inline_scripts/zones/*.txt）。
 *
 * 文件 2 `common/inline_scripts/shelter_all_resources.txt`（数据源待用户自行定义）：
 *   extract(资源种类) → write(format="{key} = $VALUE$")。
 *
 * 文件 3（组件模板，后续）：extract(mode=nodes, match 条件) → modify(source=nodes,
 *   path/op) → write(output=serialize)。
 *
 *
 * 写盘规范（强制——沿用统一保存规范）
 * ------------------------------------
 * 产出文件**全量重新生成**（覆盖目标文件，不增量合并）；写盘**一律经 SA**
 * （WriteTextFile / WriteCollisionFile）——引擎层绝不直接 File 写产出文件；
 * 目标根 = modRoot，自动建目录。两个配置文件（extension_config.json /
 * _extension.json）属 App 层启动配置，直接 File 读写（Editor config/user_prefs.json 先例）。
 *
 *
 * ============================================================================
 * 附录 A：公开 API 索引
 * ============================================================================
 * ExtensionRunner.Run(ExtensionPlan plan, StellarisAdapter adapter, string modRoot,
 *                     ILogger logger) — 顺序执行全部部署（每轮独立状态）。
 * ExtensionRunner.ExecuteDeployment(Deployment dep) — 执行单轮并返回状态（测试走此 API）。
 * ExtensionRunner.ToSelectorPath(JsonElement arr) — JSON 枝/叶数组 → SelectorResolver
 *                     List<object> 路径（测试与引擎复用）。
 * SelectorResolver.Resolve(roots, path, autoCreateBlocks) — 标准搜索定位（枝/叶）。
 * SelectorResolver.NodeMatches(rule, checkRule, node, result) — 节点条件评估
 *                     （extract 全树遍历用）。
 * MatchCondition（已废弃，不再使用——v2.0 条件树，保留文件仅供历史参考）。
 * StellarisAdapter.WriteTextFile(relPath, root, content, encoding = null) — 写指定
 *                     root 文本（SA 接口；encoding 可选 utf-8/utf-8-bom，缺省按扩展名）。
 *
 * 引擎注册表（extract 的 engine 字段——每个引擎单一统一登记）
 * ------------------------------------------------------------
 * "strategic_resource" → StrategicResourceEngine（撞击扫描合并，顶层 key 一条）：
 *   GetResourceKeys() 进 Values（文件 2 资源种类场景）。无参数。
 * ============================================================================
 * 规范结束
 * ============================================================================
 */

namespace Stellaris.Extension;
