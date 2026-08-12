using System.Collections.Generic;
using System.Text.Json;

namespace Stellaris.Extension;

/// <summary>
/// extension_config.json（exe 同位置）：只告知处理/写入哪一个模组。
/// 属 App 层启动配置（同 Editor config/user_prefs.json 惯例），直接 File 读写。
/// </summary>
public sealed class ExtensionConfig
{
    /// <summary>写盘根——模组目录绝对路径（与 Roots 无关）。</summary>
    public string? ModRoot { get; set; }
}

/// <summary>
/// _extension.json（模组 .smt/ 下，半隐藏）：读取源 + 部署列表。
/// </summary>
public sealed class ExtensionPlan
{
    /// <summary>读取源（游戏本体等，经 SA 读取；不含写盘根也能工作）。</summary>
    public List<string> Roots { get; set; } = new();

    /// <summary>部署列表——一次运行顺序执行全部轮；轮间状态独立。</summary>
    public List<Deployment> Deployments { get; set; } = new();
}

/// <summary>
/// 一轮部署：独立 steps 序列（每轮新建执行状态、重新读取）。
/// Roots 可选：声明 = 该轮**完全覆盖**全局 roots（独立 adapter）；缺省 = 继承全局。
/// </summary>
public sealed class Deployment
{
    /// <summary>该轮专用读取源（可选；声明后覆盖全局 roots，缺省继承全局）。</summary>
    public List<string> Roots { get; set; } = new();

    public List<StepConfig> Steps { get; set; } = new();
}

/// <summary>
/// 单个步骤（规则实例）：rule 类型 + 该类型参数。
/// 规则：extract / modify / write / clear。
///
/// **统一定位语法 = SA 标准搜索（SelectorResolver 枝/叶）**——CLI 与 SA/引擎共用同一套：
/// - `path`（数组）= 枝序列，逐层下钻：枝 = {mode, match:{rule:[...], check_rule}} 或 {mode, index}
///   （match 与 index 互斥；index 1 起）；叶 = {target:key|value, keywords:[...], type, check_rule}
///   或 {index}（rule 内：候选节点 Children 第 N 个存在）。
/// - modify/write 的 path = 枝/叶数组（定位/取数）；extract 的 path = 字符串（文件目录过滤）。
/// </summary>
public sealed class StepConfig
{
    /// <summary>规则类型：extract / modify / write / clear。</summary>
    public string Rule { get; set; } = "";

    // ==================== extract ====================
    /// <summary>extract 模式："values"(默认，仅 Simple/List 有值) | "keys"(收集 Key) | "nodes"(节点) | "parent"(父节点)。</summary>
    public string Mode { get; set; } = "";

    /// <summary>extract 匹配条件（全树遍历评估，mode 恒 Any）：{ "rule": [叶或枝...], "check_rule": "And" }。
    /// null/空 = 匹配全部。节点满足 rule（check_rule 组合）→ 命中。</summary>
    public JsonElement? Match { get; set; }

    /// <summary>extract 文件范围（目录前缀，字符串）：仅扫描 relPath 以该前缀开头的文件；缺省 = 全部。</summary>
    public string? Dir { get; set; }

    /// <summary>keys 模式收集范围起点（枝/叶数组）；缺省 = 文件根（顶层）。</summary>
    public JsonElement? From { get; set; }

    /// <summary>keys 模式收集深度："top"(缺省，from 起点的直接子层) | "all"(任意层级)。</summary>
    public string Depth { get; set; } = "";

    /// <summary>引擎调用（缺省 null = 通用 AST 提取）：注册表见规范附录 A——如 "strategic_resource"。</summary>
    public string? Engine { get; set; }

    /// <summary>引擎参数（strategic_resource 的 output 缺省 "keys"）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("engine_args")]
    public JsonElement? EngineArgs { get; set; }

    // ==================== modify（source=nodes 用 path 定位；source=values 对通道项直接 op）====================
    /// <summary>数据源（modify/write 共用）："nodes"(modify 缺省，Nodes 节点) | "values"(modify 对 Values 逐项；write format 取数)。</summary>
    public string Source { get; set; } = "";

    /// <summary>定位路径（枝/叶数组，仅 modify source=nodes / write source=nodes）：从每个 Nodes 节点下钻。</summary>
    public JsonElement? Path { get; set; }

    /// <summary>操作：set | add | mul | resolve | prefix | suffix | replace（replace 用 with 作新值）。</summary>
    public string Op { get; set; } = "";

    /// <summary>op 参数值（add/mul 数值、prefix/suffix 文本、set 新值、replace 旧文本）。</summary>
    public string? Value { get; set; }

    /// <summary>replace 的新值（仅 op=replace）。</summary>
    public string? With { get; set; }

    // ==================== write ====================
    /// <summary>目标相对路径（写到 modRoot 下，全量覆盖 + 自动建目录）。</summary>
    public string? File { get; set; }

    /// <summary>输出方式："format"(缺省，行渲染) | "serialize"(节点合并)。</summary>
    public string Output { get; set; } = "";

    /// <summary>format 模板：仅 {key} 被替换，其余文本字面保留。</summary>
    public string Format { get; set; } = "";

    /// <summary>编码（可选）："utf-8"（无 BOM）/ "utf-8-bom"（带 BOM）；缺省按扩展名规则（.yml 带 BOM，其他无）。</summary>
    public string? Encoding { get; set; }

    /// <summary>行/节点分隔符（缺省 "\n"）。</summary>
    public string Separator { get; set; } = "\n";

    /// <summary>取数目标（可选，write source=nodes 时）："value"(缺省，path 定位节点取 Value) |
    /// "key"(取定位节点的 Key) | "simple"(取定位节点整体序列化文本)。</summary>
    public string Target { get; set; } = "";

    // ==================== foreach ====================
    /// <summary>迭代列表（字面量）；每轮迭代把 steps 中所有 {As} 引用替换为当前值后执行。
    /// 支持数值范围字符串 "0..1999"（含 .. 且两侧整数）——展开为连续整数，绑定值即整数。</summary>
    public List<string>? Over { get; set; }

    /// <summary>当前值绑定名（steps 中写 {绑定名} 引用，如 {area}；表达式里直接写绑定名，如 {expr:n*2}）。</summary>
    public string? As { get; set; }

    /// <summary>每轮执行的子步骤（每轮开始自动清空双通道——轮间隔离）。</summary>
    public List<StepConfig>? Steps { get; set; }

    // ==================== add ====================
    /// <summary>插入位置（add 用）："Append"(缺省，to 定位父节点，追加 children 末尾) |
    /// "Before"/"After"（to 定位目标节点本身，同层前/后插入）。</summary>
    public string Position { get; set; } = "";

    /// <summary>节点文本模板（add 用）：GUI/配置文本（支持 {绑定} 与 {expr:...} 内嵌），
    /// 解析为单个 AST 节点后插入。例：positionType = { name = "ap_{n}" position = { x = {expr:...} y = {expr:...} } }</summary>
    public string Text { get; set; } = "";

    /// <summary>已存在判定（add 用，可选）：{ "rule": [...], "check_rule": "And" }——父节点 children 中
    /// 第一个满足条件的节点视为已存在 → 原地替换（保留位置）；无则按 position 插入。
    /// 例：按 name 字段定位 positionType：{"rule":[{"target":"key","keywords":["name"]},{"target":"value","keywords":["ap_0"]}]}</summary>
    public JsonElement? Existing { get; set; }

    /// <summary>add/delete 是否落盘（可选，缺省 true）：foreach 大量轮次内设 false 只改内存，
    /// 部署末尾用 rule=save 统一 WriteFile 一次（避免每轮全文件序列化）。</summary>
    public bool? Save { get; set; }

    /// <summary>write 是否追加（可选，缺省 false）：true 时先经 SA ReadTextFile 读目标文件已有内容，
    /// 新内容接在后面再 WriteTextFile 写回（全经 SA，不直接 File 操作）——用于多轮生成追加同一文件。</summary>
    public bool? Append { get; set; }

    /// <summary>set 步骤：左操作数——字符串（命名集合名或 "$values"）或字符串数组（字面量集合）。
    /// （extract 的 as 命名集合 = 复用 foreach 的 As 字段；set 的 op = 复用 modify 的 Op 字段——不同步骤各自语义）</summary>
    public JsonElement? Left { get; set; }

    /// <summary>set 步骤：右操作数——字符串（命名集合名或 "$values"）或字符串数组（字面量集合，如 ["ap_x"]）。</summary>
    public JsonElement? Right { get; set; }

    /// <summary>文件头（可选，字面原样输出——如 yml 语言头/包装结构/注释）。</summary>
    public string? Header { get; set; }

    /// <summary>文件尾（可选，字面原样输出）。</summary>
    public string? Footer { get; set; }
}
