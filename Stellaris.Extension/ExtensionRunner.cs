using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stellaris.Engine.StrategicResource;
using Stellaris.Parser;

namespace Stellaris.Extension;

/// <summary>
/// 执行状态：双通道——Values（值通道：有序去重字符串）/ Nodes（节点通道：匹配到的 AST 节点）。
/// 每轮部署新建（轮间状态独立）。
/// </summary>
public sealed class ExtensionState
{
    public List<string> Values { get; } = new();
    public List<AstNode> Nodes { get; } = new();

    /// <summary>命名集合（extract as 定义）：名字 → 值列表——供 set 步骤做布尔运算（subtract/union/intersect）。</summary>
    public Dictionary<string, List<string>> Sets { get; } = new(StringComparer.Ordinal);

    /// <summary>当前 foreach 轮的数值绑定（{expr:...} 求值用；数值范围迭代时设置 {As → 整数}）。</summary>
    public Dictionary<string, long> Bindings { get; } = new();
}

/// <summary>
/// 拓展执行器：顺序执行全部部署（每轮独立状态），每步按 rule 分发。
/// 写盘一律经 StellarisAdapter（WriteTextFile / WriteCollisionFile）——绝不直接 File 写产出文件。
/// </summary>
public sealed class ExtensionRunner
{
    private readonly StellarisAdapter _adapter;
    private readonly string _modRoot;
    private readonly ILogger _logger;

    public ExtensionRunner(StellarisAdapter adapter, string modRoot, ILogger logger)
    {
        _adapter = adapter;
        _modRoot = modRoot;
        _logger = logger;
    }

    /// <summary>执行结果报告：供命令行打印（用户双击运行时必须能在 console 看到错误）。</summary>
    public sealed class RunReport
    {
        public int Total { get; set; }
        public int Failed { get; set; }
        public List<string> Errors { get; } = new();
    }

    /// <summary>顺序执行全部部署；轮失败 → 记录错误继续下一轮（不中断整个运行）。</summary>
    public RunReport Run(ExtensionPlan plan)
    {
        var report = new RunReport { Total = plan.Deployments.Count };
        int depIndex = 0;
        foreach (var dep in plan.Deployments)
        {
            depIndex++;
            try
            {
                var state = ExecuteDeployment(dep);
                _logger.LogInformation("部署 {Index}/{Total} 结果：{V} 值 / {N} 节点",
                    depIndex, plan.Deployments.Count, state.Values.Count, state.Nodes.Count);
            }
            catch (Exception ex)
            {
                report.Failed++;
                report.Errors.Add($"部署 {depIndex}：{ex.Message}");
                _logger.LogError(ex, "部署 {Index} 失败——继续下一轮", depIndex);
            }
        }
        return report;
    }

    /// <summary>执行一轮部署（独立状态）并返回状态。步骤失败 → 直接抛出（中止本轮，由 Run 捕获）。
    /// deployment 声明 roots → 该轮独立 adapter（覆盖全局）；缺省 → 继承全局 adapter。</summary>
    public ExtensionState ExecuteDeployment(Deployment dep)
    {
        var adapter = BuildDeploymentAdapter(dep);
        var state = new ExtensionState();
        foreach (var step in dep.Steps)
            ExecuteStep(step, state, adapter);
        return state;
    }

    /// <summary>轮级 adapter：dep.Roots 非空 → 新建（AddRoot + ScanAll，完全覆盖）；否则返回全局。</summary>
    private StellarisAdapter BuildDeploymentAdapter(Deployment dep)
    {
        if (dep.Roots == null || dep.Roots.Count == 0)
            return _adapter;
        var owned = new StellarisAdapter();
        foreach (var root in dep.Roots)
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                owned.AddRoot(root);
        }
        owned.ScanAll();
        return owned;
    }

    private void ExecuteStep(StepConfig step, ExtensionState state, StellarisAdapter adapter)
    {
        switch (step.Rule)
        {
            case "extract": ExecuteExtract(step, state, adapter); break;
            case "modify": ExecuteModify(step, state, adapter); break;
            case "write": ExecuteWrite(step, state, adapter); break;
            case "add": ExecuteAdd(step, state, adapter); break;
            case "delete": ExecuteDelete(step, state, adapter); break;
            case "save": ExecuteSave(step, adapter); break;
            case "set": ExecuteSet(step, state); break;
            case "clear": ExecuteClear(state); break;
            case "foreach": ExecuteForEach(step, state, adapter); break;
            default:
                throw new InvalidOperationException($"未知规则类型: {step.Rule}");
        }
    }

    // ==================== foreach ====================

    /// <summary>
    /// 迭代执行：over 列表每轮把子步骤中所有 {As} 引用替换为当前值，逐条执行。
    /// 每轮开始自动清空双通道（轮间隔离）——子步骤的 extract/write 以当轮结果为准。
    /// 子步骤允许嵌套 foreach（递归）。
    /// </summary>
    private void ExecuteForEach(StepConfig step, ExtensionState state, StellarisAdapter adapter)
    {
        if (step.Over == null || step.Over.Count == 0)
            throw new InvalidOperationException("foreach 需要 over（迭代列表或数值范围）");
        if (string.IsNullOrEmpty(step.As))
            throw new InvalidOperationException("foreach 需要 as（当前值绑定名，如 {area}）");
        if (step.Steps == null || step.Steps.Count == 0)
            throw new InvalidOperationException("foreach 需要 steps（每轮子步骤）");
        var jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        // over 支持数值范围字符串 "0..1999"（单个元素且两侧为整数）→ 展开为连续整数；
        // over 支持 "values"（单个元素）→ 引用**上一步 extract 的 values 通道**（先拷贝——轮间隔离会 clear 通道）
        var over = step.Over;
        if (over.Count == 1 && TryParseRange(over[0], out var range))
            over = range;
        else if (over.Count == 1 && string.Equals(over[0], "values", StringComparison.OrdinalIgnoreCase))
            over = state.Values.ToList();
        for (int i = 0; i < over.Count; i++)
        {
            var value = over[i];
            // 轮间隔离：每轮从干净通道开始；数值轮写入 Bindings 供 {expr:} 求值
            state.Values.Clear();
            state.Nodes.Clear();
            state.Bindings.Clear();
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
                state.Bindings[step.As!] = numeric;
            foreach (var sub in step.Steps)
            {
                var cloned = CloneWithBinding(sub, step.As!, value, jsonOpts);
                ExecuteStep(cloned, state, adapter);
            }
            _logger.LogInformation("foreach[{As}={Value}] 轮 {I}/{Total} 完成：{V} 值 / {N} 节点",
                step.As, value, i + 1, over.Count, state.Values.Count, state.Nodes.Count);
        }
    }

    /// <summary>解析数值范围字符串 "0..1999"（两侧非负整数）→ 连续整数列表；否则返回 false。</summary>
    private static bool TryParseRange(string s, out List<string> items)
    {
        items = new List<string>();
        int sep = s.IndexOf("..", StringComparison.Ordinal);
        if (sep <= 0 || sep + 2 >= s.Length)
            return false;
        string lo = s.Substring(0, sep);
        string hi = s.Substring(sep + 2);
        if (!long.TryParse(lo, NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)
            || !long.TryParse(hi, NumberStyles.Integer, CultureInfo.InvariantCulture, out var to)
            || from < 0 || to < from)
            return false;
        // 防御性上限（防误写超大范围）：100 万以内
        if (to - from > 1_000_000)
            throw new InvalidOperationException($"foreach 数值范围过大: {from}..{to}（上限 100 万）");
        for (long v = from; v <= to; v++)
            items.Add(v.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>克隆子步骤并替换 JSON 文本中的 {绑定名} 引用（所有字段生效，含 match/file/format）。</summary>
    private static StepConfig CloneWithBinding(StepConfig sub, string binding, string value,
        System.Text.Json.JsonSerializerOptions opts)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(sub, opts);
        json = json.Replace("{" + binding + "}", value, StringComparison.Ordinal);
        return System.Text.Json.JsonSerializer.Deserialize<StepConfig>(json, opts)
            ?? throw new InvalidOperationException($"foreach 子步骤克隆失败: {sub.Rule}");
    }

    // ==================== add（v3.2：创建节点）====================

    /// <summary>add：text 模板（{绑定}/{expr:}）解析为单个 AST 节点，经 SA AddConfigNode 插入
    /// （position: Append 缺省——path 定位父节点；Before/After——path 定位目标节点本身同层前后），
    /// 随后 WriteFile 落盘（roots[-1]，统一保存规范）。</summary>
    private void ExecuteAdd(StepConfig step, ExtensionState state, StellarisAdapter adapter)
    {
        if (string.IsNullOrEmpty(step.File))
            throw new InvalidOperationException("add 需要 file（目标文件相对路径）");
        if (string.IsNullOrEmpty(step.Text))
            throw new InvalidOperationException("add 需要 text（节点文本模板）");
        if (!step.Path.HasValue || step.Path.Value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("add 需要 path（枝/叶数组——Append 定位父节点，Before/After 定位目标节点）");

        // path 支持 {expr:} 模板（如 After 定位上一个槽：keywords 里 "ap_{expr:n-1}"）
        var pathText = step.Path.Value.GetRawText();
        pathText = ExpandTemplate(pathText, state);
        var pathEl = JsonDocument.Parse(pathText).RootElement;
        var selPath = ToSelectorPath(pathEl);

        var expanded = ExpandTemplate(step.Text, state);
        var nodes = ParseNodes(expanded);
        var position = step.Position.ToLowerInvariant() switch
        {
            "before" => AddPosition.Before,
            "after" => AddPosition.After,
            _ => AddPosition.Append,
        };
        if (nodes.Count > 1 && position != AddPosition.Append)
            throw new InvalidOperationException("add 多根节点文本仅支持 Append（Before/After 请拆成单节点步骤）");

        // existing 谓词（可选）：父 children 中第一个满足条件（NodeMatches）的节点原地替换
        Func<AstNode, bool>? existingPredicate = null;
        if (step.Existing.HasValue)
        {
            var errs = new SelectResult();
            string checkRule = "And";
            List<object>? rule = null;
            if (step.Existing.Value.TryGetProperty("check_rule", out var cr))
                checkRule = cr.GetString() ?? "And";
            if (step.Existing.Value.TryGetProperty("rule", out var ruleEl) && ruleEl.ValueKind == JsonValueKind.Array)
                rule = ToSelectorPath(ruleEl);
            if (rule == null || rule.Count == 0)
                throw new InvalidOperationException("add existing 需要 rule（枝/叶数组）");
            var ruleCopy = rule;
            var checkCopy = checkRule;
            existingPredicate = n => SelectorResolver.NodeMatches(ruleCopy, checkCopy, n, errs);
        }

        // 多根节点文本（如 verticalScrollbar + margin + background 三节点）→ 逐节点 Append 循环
        if (nodes.Count > 1)
        {
            foreach (var n in nodes)
            {
                adapter.AddConfigNode(step.File, selPath, n, existingPredicate, position);
            }
            if (step.Save ?? true)
                adapter.WriteFile(step.File, _modRoot);
            _logger.LogInformation("add: {File} -> {Count} 个节点（{Position}）", step.File, nodes.Count, position);
            return;
        }

        var node = nodes[0];
        adapter.AddConfigNode(step.File, selPath, node, existingPredicate, position);
        if (step.Save ?? true)
            adapter.WriteFile(step.File, _modRoot);
        _logger.LogInformation("add: {File} -> {Key}（{Position}）", step.File, node.Key ?? "<无Key>", position);
    }
    /// <summary>模板展开：{expr:表达式} 用当前 Bindings 求值替换；其余文本（含已绑定的 {As}）原样保留。</summary>
    internal static string ExpandTemplate(string text, ExtensionState state)
    {
        if (!text.Contains("{expr:", StringComparison.Ordinal))
            return text;
        var sb = new System.Text.StringBuilder(text.Length + 32);
        int i = 0;
        while (i < text.Length)
        {
            int idx = text.IndexOf("{expr:", i, StringComparison.Ordinal);
            if (idx < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }
            sb.Append(text, i, idx - i);
            int close = text.IndexOf('}', idx + 6);
            if (close < 0)
                throw new InvalidOperationException("模板 {expr: 缺闭括号: " + text);
            string expr = text.Substring(idx + 6, close - idx - 6);
            sb.Append(TemplateMath.Evaluate(expr, state.Bindings));
            i = close + 1;
        }
        return sb.ToString();
    }

    /// <summary>把节点文本（可多根）解析为 AST 节点列表。</summary>
    private static List<AstNode> ParseNodes(string text)
    {
        var lexer = new Lexer(text);
        var tokens = new List<Token>();
        Token tok;
        while ((tok = lexer.NextToken()).Type != TokenType.Eof)
            tokens.Add(tok);
        var parser = new Stellaris.Parser.Parser(tokens, new[] { text }, "<add>", text);
        var roots = parser.Parse().RootNodes;
        if (roots.Count == 0)
            throw new InvalidOperationException($"add text 解析为空: {text}");
        return roots;
    }

    // ==================== delete（v3.2：删除节点）====================

    /// <summary>delete：path（枝/叶数组）定位节点后经 SA RemoveConfigNode 删除，随后 WriteFile 落盘。
    /// 定位到多个 → 抛异常（RemoveConfigNode 语义）；不存在 → 静默。</summary>
    private void ExecuteDelete(StepConfig step, ExtensionState state, StellarisAdapter adapter)
    {
        if (string.IsNullOrEmpty(step.File))
            throw new InvalidOperationException("delete 需要 file（目标文件相对路径）");
        if (!step.Path.HasValue || step.Path.Value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("delete 需要 path（枝/叶数组）");
        var pathText = step.Path.Value.GetRawText();
        pathText = ExpandTemplate(pathText, state);
        var pathEl = JsonDocument.Parse(pathText).RootElement;
        adapter.RemoveConfigNode(step.File, ToSelectorPath(pathEl));
        if (step.Save ?? true)
            adapter.WriteFile(step.File, _modRoot);
        _logger.LogInformation("delete: {File}", step.File);
    }

    // ==================== set（v3.4：集合布尔运算）====================

    /// <summary>set：对两个集合做布尔运算（subtract 差集 / union 并集 / intersect 交集），
    /// 结果**替换 values 通道**（write 直接输出）。操作数：字符串（命名集合名 / "$values"）或字符串数组（字面量集合）。</summary>
    private void ExecuteSet(StepConfig step, ExtensionState state)
    {
        var op = string.IsNullOrEmpty(step.Op) ? "subtract" : step.Op.ToLowerInvariant();
        var left = ResolveSet(step.Left, state);
        var right = ResolveSet(step.Right, state);
        List<string> result;
        switch (op)
        {
            case "union":
                result = left.Union(right, StringComparer.Ordinal).ToList();
                break;
            case "intersect":
                result = left.Intersect(right, StringComparer.Ordinal).ToList();
                break;
            default:   // subtract（缺省）
                result = left.Except(right, StringComparer.Ordinal).ToList();
                break;
        }
        state.Values.Clear();
        state.Values.AddRange(result);
        _logger.LogInformation("set[{Op}]: {L} ∘ {R} → {Count} 项", op, DescribeOperand(step.Left), DescribeOperand(step.Right), result.Count);
    }

    /// <summary>解析 set 操作数：JsonElement——字符串（"$values" → 默认通道；否则命名集合名）或数组（字面量集合）。</summary>
    private static List<string> ResolveSet(JsonElement? operand, ExtensionState state)
    {
        if (!operand.HasValue)
            return new List<string>();
        var el = operand.Value;
        if (el.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
                list.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString());
            return list;
        }
        if (el.ValueKind != JsonValueKind.String)
            return new List<string>();
        var name = el.GetString();
        if (string.Equals(name, "$values", StringComparison.OrdinalIgnoreCase))
            return state.Values;
        if (!string.IsNullOrEmpty(name) && state.Sets.TryGetValue(name, out var set))
            return set;
        return new List<string>();
    }

    private static string DescribeOperand(JsonElement? operand)
    {
        if (!operand.HasValue)
            return "(空)";
        return operand.Value.ValueKind == JsonValueKind.Array
            ? "[" + string.Join(",", operand.Value.EnumerateArray().Select(x => x.ToString())) + "]"
            : operand.Value.ToString();
    }

    // ==================== save（v3.2：显式落盘）====================

    /// <summary>save：把 file 的合并后内存 AST 经 SA WriteFile 落盘（roots[-1]）。
    /// 配合 add/delete 的 save=false——foreach 大量轮次改内存后统一写一次。</summary>
    private void ExecuteSave(StepConfig step, StellarisAdapter adapter)
    {
        if (string.IsNullOrEmpty(step.File))
            throw new InvalidOperationException("save 需要 file（目标文件相对路径）");
        adapter.WriteFile(step.File, _modRoot);
        _logger.LogInformation("save: {File}", step.File);
    }

    // ==================== clear ====================

    /// <summary>清空双通道（Values/Nodes）——一轮内多组独立的"提取→写出"之间用（避免上一组结果污染下一组）。</summary>
    private static void ExecuteClear(ExtensionState state)
    {
        state.Values.Clear();
        state.Nodes.Clear();
    }

    // ==================== extract ====================

    /// <summary>
    /// 扫描 Roots 下全部配置文件（合并 AST），递归遍历；满足 match 条件（枝/叶 rule + check_rule，
    /// mode 恒 Any）→ 匹配。path（目录前缀）限定文件范围。
    /// 指定 engine → 走引擎注册表（见 ExecuteEngineExtract）；否则通用 AST 提取。
    /// mode=values(默认)：List→子值 / Block→顶级 Simple 子键，去重保序；
    /// mode=nodes：匹配节点进 Nodes（引用去重）；
    /// mode=keys：顶层匹配节点自身的 Key 进 Values（去重保序）。
    /// </summary>
    private void ExecuteExtract(StepConfig step, ExtensionState state, StellarisAdapter adapter)
    {
        if (!string.IsNullOrEmpty(step.Engine))
        {
            ExecuteEngineExtract(step, state, adapter);
            return;
        }
        var mode = string.IsNullOrEmpty(step.Mode)
            ? "values"
            : step.Mode.ToLowerInvariant();
        var seenValues = new HashSet<string>(StringComparer.Ordinal);
        int valuesStart = state.Values.Count;   // as 命名集合：记录本次新增起点
        var seenNodes = new HashSet<AstNode>(ReferenceEqualityComparer.Instance);
        var depth = string.IsNullOrEmpty(step.Depth) ? "top" : step.Depth.ToLowerInvariant();
        // 节点条件（枝的 match 部分，mode 恒 Any）：{rule:[...], check_rule}
        var rule = new List<object>();
        string checkRule = "And";
        if (step.Match.HasValue && step.Match.Value.ValueKind == JsonValueKind.Object)
        {
            var m = step.Match.Value;
            rule = m.TryGetProperty("rule", out var r)
                ? ToSelectorPath(r)
                : new List<object>();
            checkRule = m.TryGetProperty("check_rule", out var cr) ? cr.GetString() ?? "And" : "And";
        }
        // 文件目录过滤（extract 的 dir = 字符串前缀）
        string? dirFilter = string.IsNullOrEmpty(step.Dir) ? null : step.Dir;
        // from（keys 收集范围起点）：枝/叶数组 → Resolve 出起点节点集（缺省 = 根层）
        var fromHits = ResolveFrom(step.From, adapter);
        int matched = 0;
        foreach (var (relPath, result) in adapter.GetAllConfigs())
        {
            if (!string.IsNullOrEmpty(dirFilter)
                && !IsPathUnder(relPath, dirFilter))
                continue;
            foreach (var root in result.RootNodes)
                WalkExtract(root, null, relPath, rule, checkRule, mode, fromHits, depth,
                    adapter, state, seenValues, seenNodes, ref matched);
        }
        // as 命名集合：本次提取新增的值存入 state.Sets（供 set 步骤做布尔运算）
        if (!string.IsNullOrEmpty(step.As))
            state.Sets[step.As!] = state.Values.Skip(valuesStart).ToList();
        _logger.LogInformation("extract[{Mode}]: {Matched} 个匹配节点 → {V} 值 / {N} 节点",
            mode, matched, state.Values.Count, state.Nodes.Count);
    }

    /// <summary>from（枝/叶数组）→ 起点节点集：Resolve 每个文件的 Roots；缺省返回 null（= 文件根层）。</summary>
    private static List<AstNode>? ResolveFrom(JsonElement? from, StellarisAdapter adapter)
    {
        if (!from.HasValue || from.Value.ValueKind != JsonValueKind.Array || from.Value.GetArrayLength() == 0)
            return null;
        var path = ToSelectorPath(from.Value);
        var hits = new List<AstNode>();
        foreach (var (_, result) in adapter.GetAllConfigs())
        {
            var r = SelectorResolver.Resolve(result.RootNodes, path);
            hits.AddRange(r.Hits);
        }
        return hits;
    }

    /// <summary>
    /// 引擎注册表分发：engine 名 → 调用对应引擎功能，结果填通道。
    /// 加引擎 = 此处加 case + 规范附录 A 登记（每个引擎单一统一登记）。
    /// 引擎参数：output="keys"(缺省，进 Values) / "ast"(预留，本期未实现)。
    /// </summary>
    private void ExecuteEngineExtract(StepConfig step, ExtensionState state, StellarisAdapter adapter)
    {
        switch (step.Engine)
        {
            case "strategic_resource":
            {
                var engine = new StrategicResourceEngine(adapter, _logger);
                engine.ScanAll();   // 撞击扫描（幂等；合并语义：同顶层 key 多 root → 一条）
                foreach (var key in engine.GetResourceKeys())
                    state.Values.Add(key);
                _logger.LogInformation("engine[strategic_resource]: {N} 个资源 key（顶层合并）", state.Values.Count);
                break;
            }
            default:
                throw new InvalidOperationException($"未知引擎: {step.Engine}（注册表见规范附录 A）");
        }
    }

    /// <summary>
    /// 递归遍历。nodes/keys 收集范围 = InKeysRange（from 起点 + depth 深度，缺省 top = 顶层块）——不混层级。
    /// mode=parent：收集匹配节点的**父节点**进 Nodes（通用——任意节点有父节点）。
    /// </summary>
    private void WalkExtract(AstNode node, AstNode? parent, string relPath,
        List<object> rule, string checkRule, string mode, List<AstNode>? fromHits, string depth, StellarisAdapter adapter,
        ExtensionState state, HashSet<string> seenValues, HashSet<AstNode> seenNodes, ref int matched, bool inFromSubtree = false)
    {
        var errs = new SelectResult();
        if (SelectorResolver.NodeMatches(rule, checkRule, node, errs))
        {
            matched++;
            if (mode == "nodes")
            {
                // 深拷贝（CloneNode）——节点是 SA 共享 AST 引用，直接收集会在后续 modify 时污染 SA 缓存。
                // 范围控制同 keys：缺省 top = 只收顶层块；depth=all 收任意层级。
                if (InKeysRange(parent, inFromSubtree, fromHits, depth) && seenNodes.Add(node))
                    state.Nodes.Add(adapter.CloneNode(node));
            }
            else if (mode == "parent")
            {
                // 收集匹配节点的父节点（父节点通用——任意节点都有）；同样深拷贝
                if (parent != null && seenNodes.Add(parent))
                    state.Nodes.Add(adapter.CloneNode(parent));
            }
            else if (mode == "keys")
            {
                // keys 模式：按 from/depth 限定收集范围（缺省 = 顶层，与旧行为一致）
                if (InKeysRange(parent, inFromSubtree, fromHits, depth)
                    && !string.IsNullOrEmpty(node.Key) && seenValues.Add(node.Key))
                    state.Values.Add(node.Key);
            }
            else // values —— 仅 Simple/List 有值（Block 无值跳过）
            {
                if (node.Type == NodeType.Simple)
                {
                    var v = node.Value?.ToString();
                    if (!string.IsNullOrEmpty(v) && seenValues.Add(v))
                        state.Values.Add(v);
                }
                else if (node.Type == NodeType.List)
                {
                    foreach (var child in node.Children)
                    {
                        var v = child.Value?.ToString();
                        if (!string.IsNullOrEmpty(v) && seenValues.Add(v))
                            state.Values.Add(v);
                    }
                }
            }
        }
        var childInSubtree = inFromSubtree || fromHits?.Contains(node) == true;
        foreach (var child in node.Children)
            WalkExtract(child, node, relPath, rule, checkRule, mode, fromHits, depth,
                adapter, state, seenValues, seenNodes, ref matched, childInSubtree);
    }

    /// <summary>
    /// keys/nodes 收集范围判定：from 定位的起点节点集（null = 文件根层）。
    /// - from 缺省：depth=top → 顶层节点（无父）；depth=all → 任意层级。
    /// - from 指定：depth=top → 父 ∈ fromHits（from 的直接子层）；depth=all → 自身在 from 子树内。
    /// </summary>
    private static bool InKeysRange(AstNode? parent, bool inFromSubtree, List<AstNode>? fromHits, string depth)
    {
        if (fromHits == null || fromHits.Count == 0)
            return depth == "all" || parent == null;
        if (depth == "all")
            return inFromSubtree;
        return parent != null && fromHits.Contains(parent);
    }

    // ==================== modify ====================

    /// <summary>
    /// source=nodes（缺省）：对 Nodes 每个节点按 field_path 嵌套路径定位字段，应用 op。
    /// source=values：对 Values 逐项应用 op（prefix/suffix/replace/set/add/mul）。
    /// 修改后必须清空 RawText（序列化优先用 RawText——不清空则旧文本仍输出）。
    /// </summary>
    private void ExecuteModify(StepConfig step, ExtensionState state, StellarisAdapter adapter)
    {
        var source = string.IsNullOrEmpty(step.Source) ? "nodes" : step.Source.ToLowerInvariant();
        if (source == "values")
        {
            if (step.Op == "resolve")
                throw new InvalidOperationException("modify op=resolve 仅支持 source=nodes");
            for (int i = 0; i < state.Values.Count; i++)
            {
                var v = ApplyValueOp(state.Values[i], step);
                if (v != null)
                    state.Values[i] = v;
            }
            return;
        }
        // source=nodes：必须提供 path（枝/叶数组——SelectorResolver 定位）
        if (!step.Path.HasValue || step.Path.Value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("modify source=nodes 需要 path（枝/叶数组）");
        var selPath = ToSelectorPath(step.Path.Value);
        foreach (var node in state.Nodes)
        {
            // 从 node 的子层开始定位（path 第一段匹配 node 的直接子——与旧 DrillPath 语义一致）
            var r = SelectorResolver.Resolve(node.Children.ToList(), selPath);
            if (r.Hits.Count > 0)
                ApplyOp(r.Hits[0], step, adapter);
        }
    }

    /// <summary>source=values 的 op 应用（对单个字符串值）；op 不支持时返回 null（跳过该项）。</summary>
    private static string? ApplyValueOp(string value, StepConfig step)
    {
        switch (step.Op)
        {
            case "set":
                return step.Value;
            case "prefix":
                return (step.Value ?? "") + value;
            case "suffix":
                return value + (step.Value ?? "");
            case "replace":
                return value.Replace(step.Value ?? "", step.With ?? "", StringComparison.Ordinal);
            case "add":
                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var oldNum)
                    && double.TryParse(step.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var addNum))
                    return (oldNum + addNum).ToString(CultureInfo.InvariantCulture);
                return null;
            case "mul":
                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var oldMul)
                    && double.TryParse(step.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var mulNum))
                    return (oldMul * mulNum).ToString(CultureInfo.InvariantCulture);
                return null;
            default:
                throw new InvalidOperationException($"未知 modify op: {step.Op}");
        }
    }

    // ==================== 标准搜索转换（JSON 枝/叶 ↔ SelectorResolver） ====================

    /// <summary>JSON 数组（枝/叶）→ SelectorResolver 路径（List&lt;object&gt; 字典选择器）。</summary>
    public static List<object> ToSelectorPath(JsonElement arr)
    {
        var path = new List<object>();
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                path.Add(JsonToObject(item));
        else
            path.Add(JsonToObject(arr));
        return path;
    }

    /// <summary>JsonElement → object（Dictionary&lt;string,object&gt; / List&lt;object&gt; / 标量）——喂 SelectorResolver。</summary>
    private static object JsonToObject(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var d = new Dictionary<string, object>();
                foreach (var p in el.EnumerateObject())
                    d[p.Name] = JsonToObject(p.Value);
                return d;
            case JsonValueKind.Array:
                var list = new List<object>();
                foreach (var i in el.EnumerateArray())
                    list.Add(JsonToObject(i));
                return list;
            case JsonValueKind.String:
                return el.GetString() ?? "";
            case JsonValueKind.Number:
                return el.TryGetInt64(out var lng) ? lng : el.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null!;   // Null
        }
    }

    /// <summary>目录边界匹配：relPath 等于 prefix，或前缀后紧跟 '/'（relPath 统一正斜杠）。</summary>
    private static bool IsPathUnder(string relPath, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return true;
        if (relPath.Length < prefix.Length)
            return false;
        if (string.Compare(relPath, 0, prefix, 0, prefix.Length, StringComparison.Ordinal) != 0)
            return false;
        return relPath.Length == prefix.Length || relPath[prefix.Length] == '/';
    }

    private void ApplyOp(AstNode target, StepConfig step, StellarisAdapter adapter)
    {
        var old = target.Value?.ToString();
        string? result = null;
        switch (step.Op)
        {
            case "set":
                if (target.Type == NodeType.Block || target.Type == NodeType.InlineScript)
                {
                    // set 定位到 Block：value 作为块内容重建——包成 "key = { value }" 重新解析，
                    // 用解析出的子节点整体替换原 Children（原地重建，节点身份保留；注释/旧内容丢弃）
                    RebuildBlock(target, step.Value ?? "");
                    return;
                }
                result = step.Value;
                break;
            case "add":
                if (old != null && double.TryParse(old, NumberStyles.Any, CultureInfo.InvariantCulture, out var oldNum)
                    && double.TryParse(step.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var addNum))
                    result = (oldNum + addNum).ToString(CultureInfo.InvariantCulture);
                else
                    return; // 数值解析失败 → 跳过该节点
                break;
            case "mul":
                // 数值乘法：值 × value（翻倍 = mul 2）
                if (old != null && double.TryParse(old, NumberStyles.Any, CultureInfo.InvariantCulture, out var oldMul)
                    && double.TryParse(step.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var mulNum))
                    result = (oldMul * mulNum).ToString(CultureInfo.InvariantCulture);
                else
                    return;
                break;
            case "resolve":
                // 常量展开：@常量 → 数值。SA 解析时 Value 已求值、RawText 保留 @ 原文，
                // 序列化优先用 RawText——resolve = 清 RawText 让序列化输出求值后的 Value；
                // 若 Value 仍是 @ 文本（未求值兜底）→ 经 SA ResolveConstantInput 解析。
                if (target.RawText == null || !target.RawText.TrimStart().StartsWith('@'))
                    return;
                if (target.Value?.ToString()?.TrimStart().StartsWith('@') == true)
                {
                    var resolvedVal = adapter.ResolveConstantInput(target.Value.ToString());
                    if (resolvedVal != null)
                        target.Value = resolvedVal;
                    else
                        return;   // 解析失败 → 保留原文（交由游戏端）
                }
                target.RawText = null;
                return;
            case "prefix":
                result = (step.Value ?? "") + old;
                break;
            case "suffix":
                result = old + (step.Value ?? "");
                break;
            case "replace":
                if (old != null)
                    result = old.Replace(step.Value ?? "", step.With ?? "", StringComparison.Ordinal);
                break;
            default:
                throw new InvalidOperationException($"未知 modify op: {step.Op}");
        }
        target.Value = result;
        target.RawText = null;   // 关键：清空原始文本，序列化才输出新值
    }

    /// <summary>
    /// Block set 重建：把 value 作为块内容，包成 "key = { value }"（无 key 的裸块 → "{ value }"）
    /// 经 Lexer+Parser 重新解析，用解析出的子节点整体替换原 Children（原地重建，Key/身份保留）。
    /// 解析失败（value 语法错误）→ 抛异常（配置错误，中止本轮）。
    /// </summary>
    private static void RebuildBlock(AstNode target, string content)
    {
        var text = string.IsNullOrEmpty(target.Key)
            ? "{ " + content + " }"
            : target.Key + " = { " + content + " }";
        var lexer = new Lexer(text);
        var tokens = new List<Token>();
        Token tok;
        while ((tok = lexer.NextToken()).Type != TokenType.Eof)
            tokens.Add(tok);
        var parser = new Stellaris.Parser.Parser(tokens, Array.Empty<string>(), null, text);
        var result = parser.Parse();
        var block = result.RootNodes.FirstOrDefault(n => n.Type == NodeType.Block || n.Type == NodeType.InlineScript);
        if (block == null || !result.Success)
            throw new InvalidOperationException(
                $"modify set 重建 Block 失败：value 未解析出块或语法错误（{text}）");
        target.Children.Clear();
        foreach (var c in block.Children)
            target.Children.Add(c);
        target.Value = null;
        target.RawText = null;
    }

    // ==================== write ====================

    /// <summary>
    /// 写盘（全量重新生成，经 SA 到 modRoot，自动建目录）。
    /// output=format（缺省）：行渲染——source=values（缺省）对 Values 逐项替换 {key}；
    ///   source=nodes 时对 Nodes 逐节点，path（嵌套路径）提取字段值替换 {key}（字段缺失跳过节点）。
    /// output=serialize：Nodes 每节点 SerializationHelper.Serialize 后节点间 separator 连接。
    /// encoding 可选："utf-8" / "utf-8-bom"；缺省按扩展名规则。
    /// 完整文件 = header + 行/节点内容 + footer（header/footer 字面原样）。
    /// </summary>
    private void ExecuteWrite(StepConfig step, ExtensionState state, StellarisAdapter adapter)
    {
        if (string.IsNullOrEmpty(step.File))
            throw new InvalidOperationException("write 缺少 file（目标相对路径）");
        var output = string.Equals(step.Output, "serialize", StringComparison.OrdinalIgnoreCase)
            ? "serialize"
            : "format";
        var source = string.IsNullOrEmpty(step.Source) ? "values" : step.Source.ToLowerInvariant();
        string content;
        if (output == "serialize")
        {
            var parts = new List<string>(state.Nodes.Count);
            foreach (var node in state.Nodes)
                parts.Add(SerializationHelper.Serialize(new List<AstNode> { node }));
            content = string.Join(step.Separator, parts);
        }
        else if (source == "nodes")
        {
            // source=nodes：逐节点，path（枝/叶数组）从 node 子层定位取字段替换 {key}；
            // target：value(缺省，取定位节点 Value) | key(取 Key) | simple(取节点整体序列化)
            // path 缺省 = 取**节点自身**（target=key → node.Key——如资源块 energy；value → node.Value）
            var target = string.IsNullOrEmpty(step.Target) ? "value" : step.Target.ToLowerInvariant();
            var hasPath = step.Path.HasValue && step.Path.Value.ValueKind == JsonValueKind.Array;
            var selPath = hasPath ? ToSelectorPath(step.Path.Value) : null;
            var lines = new List<string>(state.Nodes.Count);
            foreach (var node in state.Nodes)
            {
                AstNode t;
                if (hasPath)
                {
                    // 从 node 的子层开始取数（path 第一段匹配 node 的直接子）
                    var r = SelectorResolver.Resolve(node.Children.ToList(), selPath!);
                    if (r.Hits.Count == 0)
                        continue;   // 字段不存在 → 跳过该节点
                    t = r.Hits[0];
                }
                else
                {
                    t = node;   // path 缺省 → 节点自身
                }
                string? extracted;
                switch (target)
                {
                    case "key":
                        extracted = t.Key;
                        break;
                    case "simple":
                        extracted = SerializationHelper.Serialize(new List<AstNode> { t });
                        break;
                    default:   // value（缺省）
                        extracted = t.Value?.ToString();
                        break;
                }
                if (extracted == null)
                    continue;
                lines.Add(step.Format.Replace("{key}", extracted, StringComparison.Ordinal));
            }
            content = string.Join(step.Separator, lines);
        }
        else
        {
            // source=values（缺省）：Values 逐项替换 {key}
            var lines = new List<string>(state.Values.Count);
            foreach (var v in state.Values)
                lines.Add(step.Format.Replace("{key}", v, StringComparison.Ordinal));
            content = string.Join(step.Separator, lines);
        }
        // 头/尾（字面原样）——用于生成带包装结构/注释/yml 语言头的特殊文件
        if (!string.IsNullOrEmpty(step.Header))
            content = step.Header + content;
        if (!string.IsNullOrEmpty(step.Footer))
            content = content + step.Footer;
        bool ok = adapter.WriteTextFile(step.File, _modRoot, content, step.Encoding, step.Append ?? false);
        _logger.LogInformation("write[{Output}|{Source}]: {File}（{Count} 项）→ {Ok}",
            output, source, step.File, output == "serialize" ? state.Nodes.Count : state.Values.Count, ok ? "已写盘" : "失败");
    }
}
