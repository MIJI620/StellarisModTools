// 文件: Stellaris.Engine/Technology/TechnologyLayout.cs
// 科技节点图布局。
//
// ⚠️ 旧"动态生成连线图"模式（Compute + 全部连线辅助算法）= **失败的试验性产物，已隐藏（2026-08）**：
//    用户判定连线式科技图不可用，代码仅保留存档（含 Kahn 传播/绕行/转向分道表/跳线标记/让位等），
//    页面与导出一律不再调用，勿再使用。见 ComputeLabelMode（当前唯一在用布局）。
//
// ✅ 当前模式 = **文本标签模式**（ComputeLabelMode）：
//    **3 行（物理/社会/工程各一行，行序固定不互相挤占）+ 行内 tier 列（横向，分列标准保留）**；
//    行内列内：卡片竖排（Y），**按"前置总数 + 后继总数"降序（越多的越靠上）**；
//    每个节点**左右两侧预留标签区**（左侧放前置科技尖角框标签、右侧放后继标签，替代连线）。
// 布局为纯坐标计算（世界坐标），渲染/平移/缩放由页面层负责。

using System;
using System.Collections.Generic;
using System.Linq;

namespace Stellaris.Engine.Technology;

/// <summary>布局后节点（世界坐标，页面渲染时经平移缩放映射到屏幕）。</summary>
public sealed class LayoutTech
{
    public TechNode Node { get; init; } = null!;
    public float X { get; init; }
    public float Y { get; init; }

    /// <summary>卡片高度（描述换行后自适应；默认 CardHeight）。</summary>
    public float Height { get; init; } = TechnologyLayout.CardHeight;
}

/// <summary>科技节点图布局结果。</summary>
public sealed class TechLayout
{
    /// <summary>全部节点布局（含坐标）。</summary>
    public List<LayoutTech> Nodes { get; } = new();

    /// <summary>横向行：行号 → (Row, Y 顶, 行高)。Row: 0=physics 1=society 2=engineering 3=other。</summary>
    public List<(int Row, float Y, float Height)> Rows { get; } = new();

    /// <summary>tier 竖带（行内列）：(Row, Tier, X 左缘, 列宽)。</summary>
    public List<(int Row, int Tier, float X, float Width)> Bands { get; } = new();

    /// <summary>内容总宽/总高（世界坐标）。</summary>
    public float Width { get; internal set; }
    public float Height { get; internal set; }
}

/// <summary>
/// 布局计算（纯函数，无状态）：
/// - Y 方向 3 行：physics → society → engineering（+ other 尾行），**行序固定**，
///   某行内容少不把其他行上移（每行独立起排，行间固定间距）；
/// - 行内 X 方向：tier 竖带（升序，-1 归最后），列宽按内容动态；
/// - 列内：卡片竖排（Y），cost 升序横向阶梯（相对顺序，cost100 必在 cost0 之后，
///   但 cost 差值不映射像素——cost 顺序体现在 Y 竖排，X 只由前置约束推挤）；
/// - 前置约束（同行内）：A 有前置 B → A 左缘 ≥ B 右缘 + 1/2 张 B 卡片宽度
///   （Kahn 拓扑传播 + 段内顺移，避免约束环放大）。
/// </summary>
public static class TechnologyLayout
{
    /// <summary>行顺序（物理 → 社会 → 工程 → 其他）。行序固定，不因内容多少而移动。</summary>
    private static readonly string[] RowAreas = { "physics", "society", "engineering", "other" };

    /// <summary>卡片宽度（用户可设最小宽度；科技页启动时按偏好配置，缺省 400）。</summary>
    public static float CardWidth { get; set; } = 400f;

    /// <summary>卡片高度基础值（用户可设最小高度；描述换行自适应不小于此值，缺省 96）。</summary>
    public static float CardHeight { get; set; } = 96f;

    private const float BandPaddingX = 20f;       // 列左右内边距
    private const float BandTitleHeight = 34f;    // 列顶 Tier 标题区
    private const float NodeTopPad = 10f;         // 标题条下沿 → 节点区顶部留白（用户：节点与类标签太近，10px 够）
    private const float NodeBottomPad = 10f;      // 节点区底部 → 行底留白（行间仍无空白，行底直线随之下移）
    private const float BandGap = 90f;            // 行内列与列之间空隙（竖分隔线区；增大=线有更多转向空间）
    private const float RowGap = 28f;             // 列内卡片行距（用户反馈：高卡（解锁行多）与邻近卡 14px 太挤像被遮住 → 加大）
    private const float RowGapBetween = 0f;       // 行与行之间间距（用户规则：不要白色空行——行底直线画在内容底部，下行标题条紧贴）

    // ===== 文本标签模式（ComputeLabelMode）专用常量 =====
    /// <summary>左右标签区宽度（节点左侧放前置标签、右侧放后继标签；文本超宽截断）。</summary>
    public const float LabelZoneWidth = 190f;
    /// <summary>标签框高度（竖直堆叠的单个尖角框）。</summary>
    public const float TagHeight = 22f;
    /// <summary>标签间垂直间距。</summary>
    public const float TagGap = 4f;
    /// <summary>尖角长度（指向节点方向的三角）。</summary>
    public const float TagTipSize = 8f;
    /// <summary>列与列之间空隙（相邻两列标签区间距）。</summary>
    private const float ColumnGap = 24f;
    /// <summary>tier 列之间空隙（tier 列 = 同 tier 的 cost 小列横向累积）。</summary>
    private const float TierGap = 60f;
    /// <summary>行起点左边距。</summary>
    private const float ColumnMargin = 12f;

    /// <summary>前置约束最小间距：后继卡左缘 ≥ 前置卡右缘 + 半张前置卡宽度（用户规则）。</summary>
    private static float PrereqGap => CardWidth * 1.5f;

    private static int RowIndex(string area)
    {
        for (int i = 0; i < RowAreas.Length; i++)
            if (string.Equals(RowAreas[i], area, StringComparison.OrdinalIgnoreCase))
                return i;
        return RowAreas.Length - 1;
    }

    /// <summary>学科 → 行号（0=physics 1=society 2=engineering 3=other）。供渲染层取行背景色/标签色。</summary>
    public static int RowIndexOf(string area) => RowIndex(area);

    /// <summary>
    /// ⚠️ **旧"动态生成连线图"布局 = 失败的试验性产物，已隐藏（2026-08）**。
    /// 仅存档保留，页面/导出一律不再调用（当前唯一在用 = <see cref="ComputeLabelMode"/>）。
    /// 计算全部科技布局（3 行 × 行内 tier 列）——为连线服务的大量算法（Kahn 传播/绕行/分道/让位）全部废弃。
    /// **Tier 是位置的唯一基准**：科技按自身 tier 分列（不修正不改写）；
    /// 前置约束只在"前置 tier ≤ 本科技 tier"时影响定位（前置在左/同 tier 列）；
    /// 跨 tier 反向（前置 tier 更高）不影响定位，仅作连接曲线表述。
    /// heightProvider：按科技计算卡片高度（描述换行自适应）；null = 固定 CardHeight。
    /// </summary>
    public static TechLayout Compute(IReadOnlyList<TechNode> techs, Func<TechNode, float>? heightProvider = null)
    {
        var result = new TechLayout();
        var byKey = techs.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
        var placedX = new Dictionary<TechNode, float>();
        var yOf = new Dictionary<TechNode, float>();          // 行内 Y（barycenter 后；行外生成节点用）
        var colHeights = new Dictionary<TechNode, float>();   // 卡高（行外生成节点用）
        var allCards = new List<TechNode>();                  // 全部科技（全局跨行 X 传播 + 生成节点）
        float globalMaxX = 0f;
        float rowY = 0f;

        foreach (var rowArea in RowAreas)
        {
            var rowTechs = string.Equals(rowArea, "other", StringComparison.OrdinalIgnoreCase)
                ? techs.Where(t => !RowAreas.Take(3).Any(a => string.Equals(t.Area, a, StringComparison.OrdinalIgnoreCase))).ToList()
                : techs.Where(t => string.Equals(t.Area, rowArea, StringComparison.OrdinalIgnoreCase)).ToList();
            int rowIdx = RowIndex(rowArea);
            if (rowTechs.Count == 0)
            {
                result.Rows.Add((rowIdx, rowY, 0f));   // 空行占位（行序固定）
                continue;
            }

            // 行内 tier 列（升序；-1 归最后）
            var tiers = rowTechs.Select(t => t.Tier).Distinct().OrderBy(t => t).ToList();
            if (tiers.Contains(-1))
            {
                tiers.Remove(-1);
                tiers.Add(-1);
            }

            float rowX = 0f;
            float rowHeight = 0f;
            var rowLayout = new List<TechNode>();        // 行内全部卡（X 传播）
            var rowBands = new List<(float ColLeft, int Tier, float Width)>();

            foreach (var tier in tiers)
            {
                var colTechs = rowTechs.Where(t => t.Tier == tier)
                    .OrderBy(t => t.Cost).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
                if (colTechs.Count == 0)
                    continue;

                float colLeft = rowX;

                // 1) **cost 小列**：列内按 cost 值细分为横向小列（同 cost 卡同小列竖排 Y；
                //    不同 cost 一定不在同一小列）；小列间空隙 = 小列宽（卡宽）50%。
                //    前置约束（半卡宽 480）< 相邻小列间距（卡宽 400 + 空隙 200 = 600）→ 自动满足。
                float colX = colLeft + BandPaddingX;
                foreach (var grp in colTechs.GroupBy(t => t.Cost).OrderBy(g => g.Key))
                {
                    var cards = grp.OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
                    float yCursor = rowY + BandTitleHeight;
                    foreach (var t in cards)
                    {
                        float h = heightProvider?.Invoke(t) ?? CardHeight;
                        yOf[t] = yCursor;
                        colHeights[t] = h;
                        placedX[t] = colX;   // 同小列同 X
                        yCursor += h + RowGap;
                    }
                    rowLayout.AddRange(cards);
                    colX += CardWidth * 2f;   // 小列间空隙 = 1 小列宽（用户规则：两小列隔 1 小列宽）
                }

                // 2) Kahn 拓扑传播前置约束（同行内：前置同列（拓扑）或前列（已定）；
                //    cost 升序小列下前置多在左小列——间距自动满足；前置 cost 更大的罕见情况推右）
                PropagateRow(rowLayout, byKey, placedX, rowArea);
                // 3) 补一轮传播（前置卡可能被前列推右后更新）
                PropagateRow(rowLayout, byKey, placedX, rowArea);

                // 4) 列宽（按内容，X 已定）
                float colMaxX = colLeft;
                foreach (var t in rowLayout.Where(t => t.Tier == tier))
                    colMaxX = Math.Max(colMaxX, placedX[t] + CardWidth);
                float colWidth = colMaxX - colLeft + BandPaddingX;
                rowBands.Add((colLeft, tier, colWidth));
                rowX = colLeft + colWidth + BandGap * 2;   // 大列间空隙 ≈ 2×BandGap（规整列距）
            }

            // 5) 行内初始竖排完成（cost 组连续竖排）；Y 最终排序在行外"逐列构建"统一处理
            //   （有后继上/无后继下 + 连续追加 + 级联回调）——不再用 barycenter 迭代

            // 6) 行高 + Rows/Bands（Nodes 生成移到全局跨行 X 传播之后——LayoutTech.X 只读）
            float rowMaxY = rowY + BandTitleHeight;
            foreach (var t in rowLayout)
                rowMaxY = Math.Max(rowMaxY, yOf[t] + colHeights[t]);
            foreach (var (colLeft, tier, colWidth) in rowBands)
                result.Bands.Add((rowIdx, tier, colLeft, colWidth));
            rowHeight = rowMaxY - rowY;
            result.Rows.Add((rowIdx, rowY, rowHeight));
            allCards.AddRange(rowLayout);
            globalMaxX = Math.Max(globalMaxX, rowX);
            rowY += rowHeight + RowGapBetween;
        }

        // 9) **tier 列宽共享**：所有行的同 tier 列取全局最大宽度（视觉对齐，竖线完整）
        var tierMaxWidth = result.Bands.GroupBy(b => b.Tier).ToDictionary(g => g.Key, g => g.Max(b => b.Width));

        // 10) **全局列 X 统一**（"共用全局小列分配"）：跨行同 tier 列共享全局列 X——
        //     消除各行独立 rowX（每行从 0 累积）导致的跨行列错位（society 行偏左）。
        //     整列平移：同列内相对位置不变（前置/竖排/重叠关系保持）；全局列距 = 共享列宽 + 固定间隙 ≥ 卡宽。
        var tierOrder = result.Bands.Select(b => b.Tier).Distinct().OrderBy(t => t).ToList();
        if (tierOrder.Contains(-1))
        {
            tierOrder.Remove(-1);
            tierOrder.Add(-1);
        }
        var globalColX = new Dictionary<int, float>();
        float gx = 0f;
        foreach (var tier in tierOrder)
        {
            globalColX[tier] = gx;
            gx += tierMaxWidth[tier] + BandGap * 2;
        }
        var colShift = new Dictionary<(int Row, int Tier), float>();
        foreach (var b in result.Bands)
            colShift[(b.Row, b.Tier)] = globalColX[b.Tier] - b.X;
        foreach (var t in allCards)
            placedX[t] += colShift[(RowIndex(t.Area), t.Tier)];
        for (int i = 0; i < result.Bands.Count; i++)
        {
            var (row, tier, x, w) = result.Bands[i];
            result.Bands[i] = (row, tier, globalColX[tier], tierMaxWidth[tier]);
        }
        float sharedMaxX = 0f;
        foreach (var b in result.Bands)
            sharedMaxX = Math.Max(sharedMaxX, b.X + b.Width);

        // 11) **全局跨行 X 传播**（在全局列 X 平移之后——基于最终列 X，跨学科前置强制
        //     "前置在左、后继在右"；前置 tier ≤ 后继才约束，跨 tier 反向只作连线）
        PropagateGlobalX(allCards, byKey, placedX);

        // 11b) **网格吸附**：被传播推右的卡 X = 前置.X + 600 可能落在列小列网格之间
        //     （相对列左缘非 600 整数倍）→ 向上取整到网格（≥ 需求、与同列小列对齐、不重叠）
        float gridStep = CardWidth * 2f;   // 网格 = 小列间距（2 卡宽）——网格吸附一致
        foreach (var t in allCards)
        {
            int rowIdx = RowIndex(t.Area);
            float colLeft = globalColX[t.Tier];
            float rel = placedX[t] - (colLeft + BandPaddingX);
            if (rel < 0f)
                continue;   // 列左缘内（初始小列位置，已在网格上）不动
            float k = (float)Math.Ceiling(rel / gridStep);
            placedX[t] = colLeft + BandPaddingX + k * gridStep;
        }

        // 12) **逐列构建**（用户规则）：每行按 tier 列（升序）逐列——
        //     列内排序：**有后继的卡在上、无后继的卡在下**（组内 key 序）；**连续竖排（追加式，不留空位）**。
        //     然后**从右往左级联回调**：有后继的卡对齐其后继 Y 均值 → 该列**整体下移**（列内相对不变），
        //     更左列再对齐（级联）——为后面留出空间（用户规则）
        var succ = new Dictionary<TechNode, List<TechNode>>();
        foreach (var s in allCards)
            foreach (var pk in s.Prerequisites)
                if (byKey.TryGetValue(pk, out var p) && p != s && RowIndex(p.Area) == RowIndex(s.Area))
                {
                    if (!succ.TryGetValue(p, out var l))
                        succ[p] = l = new List<TechNode>();
                    if (!l.Contains(s))
                        l.Add(s);
                }
        // 前驱索引（同行内）——"有前"判断（用户规则）
        var pred = new Dictionary<TechNode, List<TechNode>>();
        foreach (var s in allCards)
            foreach (var pk in s.Prerequisites)
                if (byKey.TryGetValue(pk, out var p) && p != s && RowIndex(p.Area) == RowIndex(s.Area))
                {
                    if (!pred.TryGetValue(s, out var l))
                        pred[s] = l = new List<TechNode>();
                    if (!l.Contains(p))
                        l.Add(p);
                }

        // 列内竖排（用户规则）：**有前的一起比较**（第一档）——主键=前代越靠上越上
        // （"前代靠上永远是高于有没有后代的"——前代位置永远优先）；次键=前代相同时有后高于没后；
        // 没前的排最后（第二档，key 序）。前代在更左列已排（tier 升序逐列），Y 已知。
        float PredTopY(TechNode c)
        {
            if (!pred.TryGetValue(c, out var pl) || pl.Count == 0)
                return float.MaxValue;
            float minY = float.MaxValue;
            foreach (var p in pl)
                if (yOf.TryGetValue(p, out var y) && y < minY)
                    minY = y;
            return minY;
        }
        float rowYFinal = 0f;
        for (int r = 0; r < result.Rows.Count; r++)
        {
            var (rowIdx, _, _) = result.Rows[r];
            var rowCards = allCards.Where(t => RowIndex(t.Area) == rowIdx).ToList();
            if (rowCards.Count == 0)
                continue;
            var colByTier = rowCards.GroupBy(t => t.Tier).ToDictionary(g => g.Key, g => g.ToList());
            var tierList = colByTier.Keys.OrderBy(t => t).ToList();
            if (tierList.Contains(-1))
            {
                tierList.Remove(-1);
                tierList.Add(-1);
            }
            float rowTop = rowYFinal;

            // **动态增量布局**（用户规则）：每列"源（有后继）优先放置 + 主干通道预留"——
            // 源先放（最靠上、占当时最优位置），其**主干带（中心 Y±40）登记**；同列其余卡
            // 放置时**跳过主干带**（通道给线走——"正后方留通道"）；冲突/被带占则往下找空位
            // （新科技先下移，不打扰已有）。
            var laneBands = new Dictionary<int, List<(float Y0, float Y1)>>();   // tier → 已登记主干带
            var placedInCol = new Dictionary<int, List<TechNode>>();             // tier → 已放卡

            // 动态放置（找空槽：避开同列卡 + 已登记主干带，从列顶往下；槽位 = 各卡实际高 + 行距）
            float FindSlot(int tier, TechNode c)
            {
                float baseY = rowTop + BandTitleHeight;
                float h = colHeights[c];
                float slot = h + RowGap;
                for (int i = 0; i < 2000; i++)
                {
                    float y = baseY + i * slot;
                    bool clash = placedInCol.TryGetValue(tier, out var list)
                        && list.Any(p => y < yOf[p] + colHeights[p] && yOf[p] < y + h);
                    if (clash)
                        continue;
                    bool bandClash = laneBands.TryGetValue(tier, out var bands)
                        && bands.Any(b => y < b.Y1 && b.Y0 < y + h);
                    if (bandClash)
                        continue;
                    return y;
                }
                return baseY;   // 防御
            }

            foreach (var tier in tierList)
            {
                var cards = colByTier[tier];
                // 源（有更右列后继）优先放置（内部按前代靠上等现有排序），其余（无后继）后放
                var sources = cards
                    .Where(c => succ.TryGetValue(c, out var l)
                        && l.Any(x => x.Tier > c.Tier))
                    .OrderByDescending(c => pred.TryGetValue(c, out var pl) && pl.Count > 0)
                    .ThenBy(c => pred.TryGetValue(c, out var pl) && pl.Count > 0 ? PredTopY(c) : 0f)
                    .ThenByDescending(c => pred.TryGetValue(c, out var pl) && pl.Count > 0
                        && succ.TryGetValue(c, out var sl) && sl.Count > 0)
                    .ThenBy(c => c.Key, StringComparer.Ordinal)
                    .ToList();
                var others = cards
                    .Where(c => !sources.Contains(c))
                    .OrderByDescending(c => pred.TryGetValue(c, out var pl) && pl.Count > 0)
                    .ThenBy(c => pred.TryGetValue(c, out var pl) && pl.Count > 0 ? PredTopY(c) : 0f)
                    .ThenBy(c => c.Key, StringComparer.Ordinal)
                    .ToList();
                // 源逐个放置（最靠上、避已有卡/已有源的带）——放置后立即登记其主干带
                foreach (var c in sources)
                {
                    float y = FindSlot(tier, c);
                    yOf[c] = y;
                    if (!placedInCol.TryGetValue(tier, out var list))
                        placedInCol[tier] = list = new List<TechNode>();
                    list.Add(c);
                    // 主干带登记（源中心 Y ±20——后续源/其余卡放置时避开，"正后方留通道"给线走）
                    float bandCenter = y + colHeights[c] / 2f;
                    if (!laneBands.TryGetValue(tier, out var bl))
                        laneBands[tier] = bl = new List<(float, float)>();
                    bl.Add((bandCenter - 20f, bandCenter + 20f));
                }
                // 其余（无后继，含孤立卡）后放——FindSlot 避开卡 + 已登记主干带（孤立卡撞击检查）
                foreach (var c in others)
                {
                    float y = FindSlot(tier, c);
                    yOf[c] = y;
                    if (!placedInCol.TryGetValue(tier, out var list))
                        placedInCol[tier] = list = new List<TechNode>();
                    list.Add(c);
                }
            }

            // 级联回调（从右往左）：有后继的卡对齐后继均值 → 整列下移（列内相对不变）
            for (int ti = tierList.Count - 1; ti >= 0; ti--)
            {
                var cards = colByTier[tierList[ti]];
                float shift = 0f;
                foreach (var c in cards)
                {
                    if (!succ.TryGetValue(c, out var l) || l.Count == 0)
                        continue;
                    float avg = l.Average(x => yOf[x] + colHeights[x] / 2f);
                    float target = avg - colHeights[c] / 2f;
                    shift = Math.Max(shift, target - yOf[c]);
                }
                if (shift > 0f)
                    foreach (var c in cards)
                        yOf[c] += shift;
            }

            // 行高
            float rowMaxY = rowTop + BandTitleHeight;
            foreach (var t in rowCards)
                rowMaxY = Math.Max(rowMaxY, yOf[t] + colHeights[t]);
            float rowHeight = rowMaxY - rowTop;
            result.Rows[r] = (rowIdx, rowTop, rowHeight);
            rowYFinal = rowTop + rowHeight + RowGapBetween;
        }
        result.Height = rowYFinal;

        // 14) 生成节点（最终 X/Y）
        float finalMaxX = 0f;
        foreach (var t in allCards)
        {
            result.Nodes.Add(new LayoutTech { Node = t, X = placedX[t], Y = yOf[t], Height = colHeights[t] });
            finalMaxX = Math.Max(finalMaxX, placedX[t] + CardWidth);
        }
        globalMaxX = Math.Max(globalMaxX, finalMaxX);

        result.Width = Math.Max(globalMaxX, sharedMaxX);
        return result;
    }

    // ==================== 文本标签模式布局（当前唯一在用） ====================

    /// <summary>标签组堆叠高度（count 个标签竖直排列的总高）。</summary>
    public static float TagStackHeight(int count)
        => count <= 0 ? 0f : count * TagHeight + (count - 1) * TagGap;

    /// <summary>
    /// ✅ **文本标签模式布局（当前唯一在用；旧连线模式 Compute 已隐藏）**：
    /// - **3 行**（物理 → 社会 → 工程 → other 尾行，行序固定不互相挤占）；
    /// - 行内 **tier 分列保留**（现有分列标准：tier 升序，-1 归最后）；**各阶 tier 列等宽**
    ///   （全局取最大 cost 小列数统一列宽，同 tier 列跨行同 X 对齐——用户规则：3 行不宽窄不一）；
    /// - tier 列内 **cost 小列保留**（原有先后次序：同 cost 同小列竖排、不同 cost 横向阶梯，cost 升序从左到右）；
    /// - 小列内按 **"前置总数 + 后继总数"降序竖排**（越多的越靠上；并列 key 序）；
    /// - 每个节点左右预留标签区（小列宽 = 卡片宽 + 左右标签区）：
    ///   左侧放前置科技尖角框标签、右侧放后继科技标签（**替代连线**，跨学科关系同样以文本标签表达）；
    /// - 节点行距自适应 = max(卡片高, 左右标签组堆叠高)——标签不互相遮挡；
    /// - 节点构造（LayoutTech X/Y/Height + 卡片绘制）**完全不动**，仅位置由本方法计算。
    /// </summary>
    public static TechLayout ComputeLabelMode(IReadOnlyList<TechNode> techs, Func<TechNode, float>? heightProvider = null)
    {
        var result = new TechLayout();
        var byKey = techs.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

        // 后继索引（反查全部科技——含跨学科，标签显示全部后继）
        var succ = new Dictionary<string, List<TechNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in techs)
        {
            foreach (var pk in t.Prerequisites)
            {
                if (string.IsNullOrEmpty(pk) || !byKey.TryGetValue(pk, out var p) || p == t)
                    continue;
                if (!succ.TryGetValue(pk, out var l))
                    succ[pk] = l = new List<TechNode>();
                if (!l.Contains(t))
                    l.Add(t);
            }
        }

        // 排序键 = 前置总数 + 后继总数（全部关系，含跨学科）
        int LinkCount(TechNode t)
            => t.Prerequisites.Count + (succ.TryGetValue(t.Key, out var sl) ? sl.Count : 0);

        var yOf = new Dictionary<TechNode, float>();
        var cardHeights = new Dictionary<TechNode, float>();
        var cardXOf = new Dictionary<TechNode, float>();   // 卡片左缘（小列左缘 + 标签区宽）
        var allCards = new List<TechNode>();
        float rowY = 0f;

        // 第一遍：每行的科技列表 + tier 列表（保留分列标准：升序，-1 归最后）
        var rowPlan = new List<(int Row, List<TechNode> Techs, List<int> Tiers)>();
        foreach (var rowArea in RowAreas)
        {
            var rowTechs = string.Equals(rowArea, "other", StringComparison.OrdinalIgnoreCase)
                ? techs.Where(t => !RowAreas.Take(3).Any(a => string.Equals(t.Area, a, StringComparison.OrdinalIgnoreCase))).ToList()
                : techs.Where(t => string.Equals(t.Area, rowArea, StringComparison.OrdinalIgnoreCase)).ToList();
            int rowIdx = RowIndex(rowArea);
            if (rowTechs.Count == 0)
                continue;   // 空行占位在第二遍按行序统一添加
            var tiers = rowTechs.Select(t => t.Tier).Distinct().OrderBy(t => t).ToList();
            if (tiers.Contains(-1))
            {
                tiers.Remove(-1);
                tiers.Add(-1);
            }
            rowPlan.Add((rowIdx, rowTechs, tiers));
        }

        // 预计算每行每 tier 的小列（行内 cost 升序 + 同 tier 前置最小右移）
        var rowCols = new Dictionary<(int Row, int Tier), List<List<TechNode>>>();
        foreach (var (rowIdx, rowTechs, tiers) in rowPlan)
            foreach (var tier in tiers)
            {
                var tierTechs = rowTechs.Where(t => t.Tier == tier).ToList();
                if (tierTechs.Count > 0)
                    rowCols[(rowIdx, tier)] = MinRightShiftCols(tierTechs, byKey, tier);
            }

        // **全局跨学科列序**（用户：ta≥tb 且 b 是 a 的前置，**哪怕跨学科** a 都要在 b 右侧一列）：
        // 同 tier 全局列 index 对齐（行内第 j 小列 = 全局第 j 列）；跨行前置 p→t 若 p 列 ≥ t 列
        // → t 所在行在该 tier 的 index pIdx+1 处插入**空列**（t 及之后右移；空列占宽保持全局对齐）
        bool gMoved = true;
        int gGuard = 0;
        while (gMoved && gGuard++ < 200)
        {
            gMoved = false;
            foreach (var (rowIdx, rowTechs, tiers) in rowPlan)
            {
                foreach (var t in rowTechs)
                {
                    foreach (var pk in t.Prerequisites)
                    {
                        if (string.IsNullOrEmpty(pk) || !byKey.TryGetValue(pk, out var p) || p == t || p.Tier != t.Tier)
                            continue;
                        if (string.Equals(p.Area, t.Area, StringComparison.OrdinalIgnoreCase))
                            continue;   // 同行由行内 MinRightShiftCols 处理
                        int pRow = RowIndex(p.Area);
                        int tRow = RowIndex(t.Area);
                        if (pRow < 0 || tRow < 0 || pRow == tRow)
                            continue;
                        if (!rowCols.TryGetValue((pRow, t.Tier), out var pCols) || !rowCols.TryGetValue((tRow, t.Tier), out var tCols))
                            continue;
                        int pIdx = pCols.FindIndex(c => c.Contains(p));
                        int tIdx = tCols.FindIndex(c => c.Contains(t));
                        if (pIdx < 0 || tIdx < 0 || pIdx < tIdx)
                            continue;   // 前置已在左侧 → 满足
                        // ⚠️ 修复（原版 679 科技崩溃点 2026-08）：原实现每轮只 Insert(pIdx+1) 一个空列——
                        // ① p 所在行列数远超 t 行时越界 ArgumentOutOfRangeException（布局计算失败）；
                        // ② pIdx>tIdx 时插一个空列无法让 t 右移到 p 右侧，guard 200 轮空列爆炸（W 290 万 px）。
                        // 改为**一次性补足 t 行列数到 pIdx+2，再把 t 移到 pIdx+1 列**（t 到 p 右侧一列，收敛不膨胀）
                        while (tCols.Count < pIdx + 2)
                            tCols.Add(new List<TechNode>());
                        var tCol = tCols[tIdx];
                        tCol.Remove(t);
                        if (tCol.Count == 0)
                            tCols.RemoveAt(tIdx);   // 删空列（删列后若不足再补）
                        while (tCols.Count < pIdx + 2)
                            tCols.Add(new List<TechNode>());
                        tCols[pIdx + 1].Add(t);   // t 放到 p 右侧一列（该列已有其它科技则同列竖排）
                        gMoved = true;
                    }
                }
            }
        }

        // 全局 **tier 列"同阶同宽"**（用户澄清：同 tier 跨行等宽同 X，不同阶可不同宽——3 行不宽窄不一）：
        // 每 tier 取跨行最大"小列总数"（调整后）→ 该 tier 的列宽
        float colW = CardWidth + LabelZoneWidth * 2f;   // 小列宽 = 卡片 + 左右标签区
        var maxColsByTier = new Dictionary<int, int>();
        foreach (var (rowIdx, rowTechs, tiers) in rowPlan)
        {
            foreach (var tier in tiers)
            {
                if (!rowCols.TryGetValue((rowIdx, tier), out var cols) || cols.Count == 0)
                    continue;
                int n = cols.Count;
                if (!maxColsByTier.TryGetValue(tier, out var m) || n > m)
                    maxColsByTier[tier] = n;
            }
        }
        var tierWidth = new Dictionary<int, float>();
        foreach (var (tier, cols) in maxColsByTier)
            tierWidth[tier] = cols * colW + (cols - 1) * ColumnGap;

        // 全局 tier 列 X（同 tier 同 X）
        var allTiers = rowPlan.SelectMany(r => r.Tiers).Distinct().OrderBy(t => t).ToList();
        if (allTiers.Contains(-1))
        {
            allTiers.Remove(-1);
            allTiers.Add(-1);
        }
        var tierX = new Dictionary<int, float>();
        float gx = ColumnMargin;
        foreach (var tier in allTiers)
        {
            tierX[tier] = gx;
            gx += tierWidth[tier] + TierGap;
        }
        float rowWidth = gx - TierGap + ColumnMargin;

        // 第二遍：布局每行（列 X 固定为全局 tier X）；**按行序 0..3 遍历**——空行占位插在对应位置，
        // 保证 result.Rows 索引 = Row 序号（否则渲染 Rows[band.Row] 错位）
        for (int rowIdx = 0; rowIdx < RowAreas.Length; rowIdx++)
        {
            // ⚠️ 空行判断用 plan.Techs == null（值元组 FirstOrDefault 缺省返回 (0, null, null)——
            // 原 plan.Row != rowIdx 在"该行无科技且 rowIdx==0"时恰好成立（都是 0），会跳过空行占位
            // 并继续 foreach (var tier in tiers) 遍历 null → NullReferenceException → 页面"布局计算失败"）
            var plan = rowPlan.FirstOrDefault(p => p.Row == rowIdx);
            if (plan.Techs == null)
            {
                result.Rows.Add((rowIdx, rowY, 0f));   // 空行占位（行序固定）
                continue;
            }
            var (_, rowTechs, tiers) = plan;
            float rowMaxY = rowY + BandTitleHeight + NodeTopPad;
            foreach (var tier in tiers)
            {
                // tier 列内：**cost 升序小列为主 + 同 tier 前置关系最小右移**（用户规则：T_A≥T_B 且 B 是 A 的前置
                // → A 右移一列到 B 右侧；**不要拓扑层推到最右**——cost0 不该比 cost500 靠后；反应堆增压器同 cost 也横向）
                var tierTechs = rowTechs.Where(t => t.Tier == tier).ToList();
                if (tierTechs.Count == 0)
                    continue;
                var cols = rowCols[(rowIdx, tier)];   // 预计算（行内 + 全局跨学科调整后）
                float colX = tierX[tier];
                foreach (var col in cols)
                {
                    // 小列内按"前置总数 + 后继总数"降序竖排（越多的越靠上；并列 key 序）
                    var cards = col.OrderByDescending(t => LinkCount(t))
                        .ThenBy(t => t.Key, StringComparer.Ordinal)
                        .ToList();
                    float yCursor = rowY + BandTitleHeight + NodeTopPad;   // 标题条下 10px 才放节点（用户：太近）
                    foreach (var t in cards)
                    {
                        float h = heightProvider?.Invoke(t) ?? CardHeight;
                        int preN = t.Prerequisites.Count;
                        int succN = succ.TryGetValue(t.Key, out var sl) ? sl.Count : 0;
                        // **节点高度自动延长**（用户：能解锁的科技多、标签多——卡片高度容纳左右标签堆叠，标签不连到空白）
                        float stackH = Math.Max(TagStackHeight(preN), TagStackHeight(succN));
                        float slotH = Math.Max(h, stackH);
                        yOf[t] = yCursor;
                        cardHeights[t] = slotH;
                        cardXOf[t] = colX + LabelZoneWidth;   // 卡片在列内居中：小列左缘 + 标签区宽
                        yCursor += slotH + RowGap;
                        allCards.Add(t);
                    }
                    rowMaxY = Math.Max(rowMaxY, yCursor - RowGap + NodeBottomPad);   // 节点区底部留白 10px
                    colX += colW + ColumnGap;   // 小列横向排列（cost 阶梯）
                }
                result.Bands.Add((rowIdx, tier, tierX[tier], tierWidth[tier]));   // 同阶同宽
            }

            float rowHeight = rowMaxY - rowY;
            result.Rows.Add((rowIdx, rowY, rowHeight));
            rowY += rowHeight + RowGapBetween;
        }

        // 生成节点（最终 X/Y——卡片 X 已按 cost 小列记录）
        foreach (var t in allCards)
            result.Nodes.Add(new LayoutTech { Node = t, X = cardXOf[t], Y = yOf[t], Height = cardHeights[t] });

        result.Width = Math.Max(rowWidth, 1f);
        result.Height = rowY;
        return result;
    }

    /// <summary>tier 列内小列排序：**cost 升序为主**（同 cost 同小列竖排）+ 同 tier 前置关系**最小右移**
    /// （用户规则：T_A≥T_B 且 B 是 A 的前置 → A 右移一列到 B 右侧；**不推到最右**——cost0 不该比 cost500 靠后；
    /// 同 cost 前置也拆列横向，如反应堆增压器 fission_power/boosters_1 同 cost 0）。环防御（guard 上限）。</summary>
    private static List<List<TechNode>> MinRightShiftCols(List<TechNode> techs, Dictionary<string, TechNode> byKey, int tier)
    {
        var set = techs.ToHashSet();
        // 初始：cost 升序小列（同 cost 一组，key 序）
        var cols = techs.GroupBy(t => t.Cost).OrderBy(g => g.Key)
            .Select(g => g.OrderBy(t => t.Key, StringComparer.Ordinal).ToList()).ToList();
        if (cols.Count <= 1)
            return cols;
        int FindCol(TechNode c) => cols.FindIndex(x => x.Contains(c));
        // 用户算法（明确）：同 tier 内 cost 升序为主；前置关系右移时——"前置右侧 COST 跟它不一样才创建"：
        // 若 p 右侧相邻列 cost 相同 → 合并（不新建）；否则**跳过 cost ≤ 该科技 cost 的列**，插到首个 cost 更大的列前
        // （"如果 COST 是 5，你的 COST 是 100，你应该再往后面找，而不是原地踏步或者插在前面"）
        int FindInsertPos(int pIdx, int cost)
        {
            int i = pIdx + 1;
            while (i < cols.Count && cols[i][0].Cost < cost)
                i++;
            return i;
        }
        void InsertOrMerge(int pIdx, List<TechNode> col)
        {
            int at = FindInsertPos(pIdx, col[0].Cost);
            if (at < cols.Count && cols[at][0].Cost == col[0].Cost)
                cols[at].AddRange(col);   // 同 cost 列：合并（不新建）
            else
                cols.Insert(at, col);
        }
        // 迭代：每条同 tier 前置关系 p→t，保证 p 列在 t 列左侧；否则**整列**右移（同 cost 一起移动，不拆列——
        // 用户规则：同 cost 同源不该开 2 列）；仅同列内存在前置关系时才拆（拆出的后继放**同一新列**）
        bool moved = true;
        int guard = 0;
        while (moved && guard++ < 200)
        {
            moved = false;
            foreach (var t in techs)
            {
                foreach (var pk in t.Prerequisites)
                {
                    if (string.IsNullOrEmpty(pk) || !byKey.TryGetValue(pk, out var p))
                        continue;
                    if (!set.Contains(p) || p.Tier != tier || p == t)
                        continue;
                    int pIdx = FindCol(p);
                    int tIdx = FindCol(t);
                    if (pIdx < 0 || tIdx < 0 || pIdx < tIdx)
                        continue;   // 前置已在左侧 → 满足
                    if (pIdx == tIdx)
                    {
                        // 同列前置：把 t 移到 p 的右侧——**先检查 p 右侧**（用户规则：拆页先检查右侧——
                        // 右侧已有同 cost 列 → 合并（不创建）；否则按 cost 定位**往后面找**（跳过更小 cost，
                        // 插到首个更大 cost 列前——"不原地踏步、不插在前面"）。同源兄弟随后合并到同一列。
                        cols[tIdx].Remove(t);
                        if (cols[tIdx].Count == 0)
                            cols.RemoveAt(tIdx);
                        int pi = FindCol(p);
                        InsertOrMerge(pi, new List<TechNode> { t });
                        moved = true;
                        break;
                    }
                    // pIdx > tIdx：**逐科技移动**（用户算法：只移 t 到前置右侧，不整列连带——整列移动会造成
                    // 列间互相依赖的交替死循环，如 starbase_1/starbase_2 与 solar_panel；同 cost 兄弟靠
                    // "先检查右侧合并"自然同列）；同样先查右侧合并 / 按 cost 定位
                    cols[tIdx].Remove(t);
                    if (cols[tIdx].Count == 0)
                        cols.RemoveAt(tIdx);
                    int pi2 = FindCol(p);
                    InsertOrMerge(pi2, new List<TechNode> { t });
                    moved = true;
                    break;
                }
                if (moved)
                    break;
            }
        }
        return cols;
    }

    /// <summary>Kahn 拓扑传播：同行内 A.X ≥ 各前置 B.X + PrereqGap（前置同列先处理，前列已布局；
    /// 跨行前置不参与——Y 已分离无重叠）。</summary>
    private static void PropagateRow(List<TechNode> sameRowCards, Dictionary<string, TechNode> byKey,
        Dictionary<TechNode, float> placedX, string rowArea)
    {
        var set = sameRowCards.ToHashSet();
        var succ = new Dictionary<TechNode, List<TechNode>>();
        foreach (var t in sameRowCards)
        {
            foreach (var pKey in t.Prerequisites)
            {
                // 前置仅在“tier ≤ 本科技”时影响定位（跨 tier 反向只作连线，不约束）
                if (byKey.TryGetValue(pKey, out var pn) && set.Contains(pn)
                    && pn.Tier <= t.Tier
                    && string.Equals(pn.Area, rowArea, StringComparison.OrdinalIgnoreCase))
                {
                    if (!succ.TryGetValue(pn, out var list))
                        succ[pn] = list = new List<TechNode>();
                    list.Add(t);
                }
            }
        }
        var indeg = sameRowCards.ToDictionary(t => t, t =>
            t.Prerequisites.Count(p => byKey.TryGetValue(p, out var pn) && set.Contains(pn)
                && pn.Tier <= t.Tier
                && string.Equals(pn.Area, rowArea, StringComparison.OrdinalIgnoreCase)));
        var queue = new Queue<TechNode>();
        foreach (var t in sameRowCards)
            if (indeg[t] == 0)
                queue.Enqueue(t);
        var done = new HashSet<TechNode>();
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            done.Add(t);
            float x = placedX[t];
            foreach (var pKey in t.Prerequisites)
            {
                if (byKey.TryGetValue(pKey, out var pn) && pn.Tier <= t.Tier && placedX.TryGetValue(pn, out var px))
                    x = Math.Max(x, px + PrereqGap);
            }
            placedX[t] = x;
            if (succ.TryGetValue(t, out var children))
                foreach (var c in children)
                    if (--indeg[c] == 0 && !done.Contains(c))
                        queue.Enqueue(c);
        }
        // 数据环防御（环内互相不推，仅应用环外已处理前置）
        foreach (var t in sameRowCards)
        {
            if (done.Contains(t))
                continue;
            float x = placedX[t];
            foreach (var pKey in t.Prerequisites)
            {
                if (byKey.TryGetValue(pKey, out var pn) && pn.Tier <= t.Tier && placedX.TryGetValue(pn, out var px) && done.Contains(pn))
                    x = Math.Max(x, px + PrereqGap);
            }
            placedX[t] = x;
        }
    }

    /// <summary>cost 小列组（同一 X 的卡片竖列；barycenter 重排对象）。</summary>
    private sealed class ColGroup
    {
        public List<TechNode> Cards = new();
    }

    /// <summary>
    /// barycenter（重心）减交叉：行内列内卡片 Y 按"相连节点平均 Y"迭代重排。
    /// 保留 cost 小列横排结构（X 不动）；每组 = 组内稳定排序 + 组整体垂直偏移（最多上移到列顶，允许下移空行）。
    /// 4 轮左右交替扫描收敛（R→L 时右列 Y 已更新、L→R 时左列 Y 已更新）。
    /// 只调 Y 不动 X——Kahn X 约束与 tier 列结构完全保留。
    /// </summary>
    private static void BarycenterSweep(List<ColGroup> groups, Dictionary<string, TechNode> byKey,
        Dictionary<TechNode, float> yOf, Dictionary<TechNode, float> colHeights, float colTop)
    {
        if (groups.Count == 0)
            return;
        var all = groups.SelectMany(g => g.Cards).ToHashSet();
        // 后继索引（行内：前置 → 后继）
        var succ = new Dictionary<TechNode, List<TechNode>>();
        foreach (var t in all)
        {
            foreach (var pKey in t.Prerequisites)
            {
                if (byKey.TryGetValue(pKey, out var pn) && all.Contains(pn))
                {
                    if (!succ.TryGetValue(pn, out var list))
                        succ[pn] = list = new List<TechNode>();
                    if (!list.Contains(t))
                        list.Add(t);
                }
            }
        }
        var targetY = new Dictionary<TechNode, float>();
        for (int round = 0; round < 4; round++)
        {
            bool rightToLeft = (round & 1) == 0;
            var ordered = rightToLeft ? groups.AsEnumerable().Reverse() : groups;
            foreach (var g in ordered)
            {
                foreach (var t in g.Cards)
                {
                    float sum = 0f;
                    int n = 0;
                    foreach (var pKey in t.Prerequisites)
                        if (byKey.TryGetValue(pKey, out var pn) && all.Contains(pn)) { sum += yOf[pn]; n++; }
                    if (succ.TryGetValue(t, out var kids))
                        foreach (var k in kids) { sum += yOf[k]; n++; }
                    targetY[t] = n > 0 ? sum / n : yOf[t];
                }
                // 组内稳定排序（按目标 Y）
                var sorted = g.Cards.OrderBy(t => targetY[t]).ToList();
                g.Cards.Clear();
                g.Cards.AddRange(sorted);
                // 组垂直偏移：目标中心 − 当前中心（**只允许下移**——组顶不高于列顶，
                // 防止卡片上移超出列顶被标题/上行内容遮住；下移即用户允许的"空行"）
                float avgTarget = g.Cards.Average(t => targetY[t]);
                float avgCurrent = g.Cards.Average(t => yOf[t]);
                float offset = Math.Max(0f, avgTarget - avgCurrent);
                // 重新竖排（列顶 + 偏移）
                float yCursor = colTop + offset;
                foreach (var t in g.Cards)
                {
                    yOf[t] = yCursor;
                    yCursor += colHeights[t] + RowGap;
                }
            }
        }
    }

    /// <summary>
    /// 全局跨行 X 传播：所有行布局完后调用——**跨学科前置也强制"前置在左、后继在右"**
    /// （前置 tier ≤ 后继才约束；跨 tier 反向只作连线不约束）。Kahn 拓扑 + 环防御，
    /// 与行内 PropagateRow 的 X 应用逻辑一致，但前置查找用全局 placedX（含其他行）。
    /// </summary>
    private static void PropagateGlobalX(List<TechNode> allCards, Dictionary<string, TechNode> byKey,
        Dictionary<TechNode, float> placedX)
    {
        if (allCards.Count == 0)
            return;
        var set = allCards.ToHashSet();
        var succ = new Dictionary<TechNode, List<TechNode>>();
        foreach (var t in allCards)
        {
            foreach (var pKey in t.Prerequisites)
            {
                if (byKey.TryGetValue(pKey, out var pn) && set.Contains(pn) && pn.Tier <= t.Tier)
                {
                    if (!succ.TryGetValue(pn, out var list))
                        succ[pn] = list = new List<TechNode>();
                    if (!list.Contains(t))
                        list.Add(t);
                }
            }
        }
        var indeg = allCards.ToDictionary(t => t, t =>
            t.Prerequisites.Count(p => byKey.TryGetValue(p, out var pn) && set.Contains(pn) && pn.Tier <= t.Tier));
        var queue = new Queue<TechNode>();
        foreach (var t in allCards)
            if (indeg[t] == 0)
                queue.Enqueue(t);
        var done = new HashSet<TechNode>();
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            done.Add(t);
            float x = placedX[t];
            foreach (var pKey in t.Prerequisites)
            {
                if (byKey.TryGetValue(pKey, out var pn) && pn.Tier <= t.Tier && placedX.TryGetValue(pn, out var px))
                    x = Math.Max(x, px + PrereqGap);
            }
            placedX[t] = x;
            if (succ.TryGetValue(t, out var children))
                foreach (var c in children)
                    if (--indeg[c] == 0 && !done.Contains(c))
                        queue.Enqueue(c);
        }
        // 数据环防御（环内互相不推，仅应用环外已处理前置）
        foreach (var t in allCards)
        {
            if (done.Contains(t))
                continue;
            float x = placedX[t];
            foreach (var pKey in t.Prerequisites)
            {
                if (byKey.TryGetValue(pKey, out var pn) && pn.Tier <= t.Tier
                    && placedX.TryGetValue(pn, out var px) && done.Contains(pn))
                    x = Math.Max(x, px + PrereqGap);
            }
            placedX[t] = x;
        }
    }

    /// <summary>
    /// 跳 tier 直连让位（用户规则）：对每条"跳 tier ≥2"的同行线（前置 P tier t1 → 后继 S tier t2, t2-t1≥2），
    /// 中间列（tier 介于 t1/t2）中 X 在 [P.X, S.X] 且 Y 与"P中心→S中心"线带相交的卡，**垂直移出线带**
    /// （先上移、失败再下移，移到与同 X 区间卡不重叠的空位——允许纵向空隙）。
    /// 反向线（前置 tier 更高）不走长线（字符标记），无需让位。
    /// </summary>
    private static void ClearJumpTierLanes(List<TechNode> allCards, Dictionary<string, TechNode> byKey,
        Dictionary<TechNode, float> yOf, Dictionary<TechNode, float> colHeights,
        Dictionary<TechNode, float> placedX)
    {
        const float laneMargin = 12f;   // 线带两侧留白
        for (int pass = 0; pass < 3; pass++)
        {
            bool moved = false;
            foreach (var s in allCards)
            {
                foreach (var pk in s.Prerequisites)
                {
                    if (!byKey.TryGetValue(pk, out var p) || p == s)
                        continue;
                    if (s.Tier - p.Tier < 2)
                        continue;                       // 只处理跳 tier ≥ 2
                    if (RowIndex(p.Area) != RowIndex(s.Area))
                        continue;                       // 跨行线不穿同行卡（行间 Y 分离）
                    float pCy = yOf[p] + colHeights[p] / 2f;
                    float sCy = yOf[s] + colHeights[s] / 2f;
                    float lo = Math.Min(pCy, sCy) - laneMargin;
                    float hi = Math.Max(pCy, sCy) + laneMargin;
                    foreach (var c in allCards)
                    {
                        if (c == p || c == s)
                            continue;
                        if (c.Tier <= p.Tier || c.Tier >= s.Tier)
                            continue;                   // 中间列
                        if (RowIndex(c.Area) != RowIndex(p.Area))
                            continue;
                        if (placedX[c] < placedX[p] || placedX[c] > placedX[s])
                            continue;                   // X 在线段范围
                        float c0 = yOf[c], c1 = c0 + colHeights[c];
                        if (c1 <= lo || c0 >= hi)
                            continue;                   // 不与线带相交
                        // 挡线：先上移（到线带上方空位），失败则下移
                        float up = MoveOutOfLane(c, placedX, yOf, colHeights, lo - colHeights[c], downward: false);
                        if (up < c0)
                        {
                            yOf[c] = up;
                            moved = true;
                        }
                        else
                        {
                            float down = MoveOutOfLane(c, placedX, yOf, colHeights, hi, downward: true);
                            if (down > c1 - colHeights[c])
                            {
                                yOf[c] = down;
                                moved = true;
                            }
                        }
                    }
                }
            }
            if (!moved)
                break;
        }
    }

    /// <summary>从起始 Y 开始沿方向找"与同 X 区间卡不重叠"的位置；**最多移 ~2 卡位**，超出则不动（卡片不飞远）。</summary>
    private static float MoveOutOfLane(TechNode c, Dictionary<TechNode, float> placedX,
        Dictionary<TechNode, float> yOf, Dictionary<TechNode, float> colHeights,
        float startY, bool downward)
    {
        float h = colHeights[c];
        float y = startY;
        float maxTravel = h * 2f + RowGap * 2f;   // 上限 ~2 卡位（防卡片飞远）
        float travel = 0f;
        for (int guard = 0; guard < 200; guard++)
        {
            bool clash = false;
            foreach (var o in placedX.Keys)
            {
                if (o == c)
                    continue;
                if (Math.Abs(placedX[o] - placedX[c]) >= TechnologyLayout.CardWidth)
                    continue;   // 不同 X 区间不冲突
                float o0 = yOf[o], o1 = o0 + colHeights[o];
                if (y < o1 && o0 < y + h)
                {
                    clash = true;
                    break;
                }
            }
            if (!clash)
                return y;
            float step = h + RowGap;
            y += (downward ? 1f : -1f) * step;
            travel += step;
            if (travel > maxTravel)
                return yOf[c];   // 移太远：不动（保持原位）
        }
        return yOf[c];   // 找不到空位：不动（保持原位）
    }

    /// <summary>
    /// 正交路由（横-竖-横）：起点 P 右缘 (rx,ry)、终点 S 左缘 (lx,ly)。
    /// 垂直段 X 选在 P 与 S 之间的**最大卡间 X 空隙中心**（该 X 处无卡 → 垂直段不穿卡）；
    /// 返回 (mx, vHit)——vHit 恒 false（空隙处无卡）；水平段是否穿卡由调用方用 EdgeHitsCards 复核。
    /// 供页面（WPF）与导出（Skia）共用。
    /// </summary>
    public static float RouteOrthogonalX(float rx, float lx,
        IReadOnlyList<LayoutTech> nodes, LayoutTech self, LayoutTech pre)
    {
        // 收集 (rx, lx) 之间其他卡的 X 区间
        var spans = nodes
            .Where(n => n != self && n != pre && n.X + CardWidth > rx && n.X < lx)
            .Select(n => (Left: n.X, Right: n.X + CardWidth))
            .OrderBy(v => v.Left)
            .ToList();
        float bestCenter = (rx + lx) / 2f;
        if (spans.Count > 0)
        {
            float prevRight = rx;
            float bestGap = 0f;
            foreach (var (lo, hi) in spans)
            {
                float gap = lo - prevRight;
                if (gap > bestGap)
                {
                    bestGap = gap;
                    bestCenter = (lo + prevRight) / 2f;
                }
                if (hi > prevRight)
                    prevRight = hi;
            }
            float tail = lx - prevRight;
            if (tail > bestGap)
                bestCenter = (lx + prevRight) / 2f;
        }
        return bestCenter;
    }

    /// <summary>垂直段在空隙内的小偏移（-20~+20px）：不同起点的线不错开则不重叠在同一竖直线上。</summary>
    public static float LineVerticalOffset(string a, string b)
    {
        int sum = 0;
        foreach (var ch in a + "|" + b)
            sum = sum * 31 + ch;
        return (sum % 40) - 20f;
    }

    /// <summary>
    /// 卡避让线（用户规则）：对每个"源"（同行直接后继 ≥2 的科技），其**同 tier 列**的直接后继
    /// **均分上下**（前 ceil(N/2) 个上、其余下，按当前 Y 序）——列内按 [上组 → 未分配 → 下组] 重排，
    /// 源的 Y 高度附近让出主干通道。多源冲突（同一卡被多个源要求上下）按"源直接后继总数"优先。
    /// 只调 Y 不动 X；不重叠（组内连续竖排）。
    /// </summary>
    private static void ArrangeAroundSources(List<TechNode> allCards, Dictionary<string, TechNode> byKey,
        Dictionary<TechNode, float> yOf, Dictionary<TechNode, float> colHeights,
        Dictionary<TechNode, float> placedX)
    {
        if (allCards.Count < 3)
            return;
        // 后继索引（同行内：前置 → 后继）
        var succ = new Dictionary<TechNode, List<TechNode>>();
        foreach (var s in allCards)
        {
            foreach (var pk in s.Prerequisites)
            {
                if (byKey.TryGetValue(pk, out var p) && p != s
                    && RowIndex(p.Area) == RowIndex(s.Area))
                {
                    if (!succ.TryGetValue(p, out var l))
                        succ[p] = l = new List<TechNode>();
                    if (!l.Contains(s))
                        l.Add(s);
                }
            }
        }
        // 方向分配：卡 → true=上 false=下；冲突按源后继数优先
        var dir = new Dictionary<TechNode, bool>();
        var priority = new Dictionary<TechNode, int>();
        foreach (var (a, children) in succ)
        {
            if (children.Count < 2)
                continue;   // 只处理多后继源
            foreach (var grp in children.GroupBy(c => c.Tier))
            {
                var list = grp.OrderBy(c => yOf[c]).ToList();
                if (list.Count < 2)
                    continue;
                int upCount = (list.Count + 1) / 2;   // 均分：上 ceil(N/2)
                for (int i = 0; i < list.Count; i++)
                {
                    bool up = i < upCount;
                    if (!dir.ContainsKey(list[i]) || children.Count > priority[list[i]])
                    {
                        dir[list[i]] = up;
                        priority[list[i]] = children.Count;
                    }
                }
            }
        }
        if (dir.Count == 0)
            return;
        // 记录"决定源"（方向由哪个源分配）——移动目标 = 该源的 Y 通道
        var decideSource = new Dictionary<TechNode, TechNode>();
        foreach (var (a, children) in succ)
        {
            if (children.Count < 2)
                continue;
            foreach (var c in children)
            {
                if (dir.ContainsKey(c)
                    && (!decideSource.ContainsKey(c) || children.Count > succ[decideSource[c]].Count))
                    decideSource[c] = a;
            }
        }
        // 移动：上组卡移到"源 Y 上方通道外"、下组卡移到"源 Y 下方通道外"——
        // 源 Y 高度留出主干通道；未分配卡保持原位（不往中间塞）；限 ~2 卡位（不飞）
        const float chanHalf = 45f;
        foreach (var (c, a) in decideSource)
        {
            float aCy = yOf[a] + colHeights[a] / 2f;
            bool up = dir[c];
            float target = up ? (aCy - chanHalf - colHeights[c]) : (aCy + chanHalf);
            float newY = SlideToChannel(c, target, up, yOf, colHeights, placedX);
            if (newY != yOf[c])
                yOf[c] = newY;
        }
    }

    /// <summary>
    /// 布局回调（用户规则）：**从右往左多轮**——多后继源的 Y 对齐其后继的 Y 均值（上移/下移腾空间），
    /// 连锁影响更前列；限距移动（不重叠、不飞）。让"后面节点多"的科技反过来为后继留出空间。
    /// </summary>
    private static void BackPropagateY(List<TechNode> allCards, Dictionary<string, TechNode> byKey,
        Dictionary<TechNode, float> yOf, Dictionary<TechNode, float> colHeights,
        Dictionary<TechNode, float> placedX)
    {
        if (allCards.Count < 3)
            return;
        var succ = new Dictionary<TechNode, List<TechNode>>();
        foreach (var s in allCards)
        {
            foreach (var pk in s.Prerequisites)
            {
                if (byKey.TryGetValue(pk, out var p) && p != s
                    && RowIndex(p.Area) == RowIndex(s.Area))
                {
                    if (!succ.TryGetValue(p, out var l))
                        succ[p] = l = new List<TechNode>();
                    if (!l.Contains(s))
                        l.Add(s);
                }
            }
        }
        for (int pass = 0; pass < 4; pass++)
        {
            bool moved = false;
            foreach (var a in allCards.OrderByDescending(t => t.Tier))   // 右→左（高 tier 先）
            {
                if (!succ.TryGetValue(a, out var children) || children.Count < 2)
                    continue;   // 只处理多后继源
                float avg = children.Average(c => yOf[c] + colHeights[c] / 2f);   // 后继中心 Y 均值
                float target = avg - colHeights[a] / 2f;   // 源顶对齐后继均值
                if (Math.Abs(target - yOf[a]) < 10f)
                    continue;   // 已对齐
                float newY = SlideToChannel(a, target, target < yOf[a], yOf, colHeights, placedX);
                if (newY != yOf[a])
                {
                    yOf[a] = newY;
                    moved = true;
                }
            }
            if (!moved)
                break;
        }
    }

    /// <summary>
    /// 线遮挡清理（用户规则）：每条同行线（P→S）检测"水平→垂直→水平"折线是否被其他卡挡住
    /// （含源 P 的其他后继——如 B 挡 C 的线），挡线卡上下移开（限距 + 防超最上方）。
    /// </summary>
    private static void ClearLineBlockers(List<TechNode> allCards, Dictionary<string, TechNode> byKey,
        Dictionary<TechNode, float> yOf, Dictionary<TechNode, float> colHeights,
        Dictionary<TechNode, float> placedX)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            bool moved = false;
            foreach (var s in allCards)
            {
                foreach (var pk in s.Prerequisites)
                {
                    if (!byKey.TryGetValue(pk, out var p) || p == s)
                        continue;
                    if (RowIndex(p.Area) != RowIndex(s.Area))
                        continue;   // 跨行线不穿同行卡（行间 Y 分离）
                    float rx = placedX[p] + CardWidth;
                    float ry = yOf[p] + colHeights[p] / 2f;
                    float lx = placedX[s];
                    float ly = yOf[s] + colHeights[s] / 2f;
                    float mx = rx + 50f;   // 简化：主干转向（P 右侧）
                    var segs = new (float x0, float y0, float x1, float y1)[]
                    {
                        (rx, ry, mx, ry),
                        (mx, ry, mx, ly),
                        (mx, ly, lx, ly)
                    };
                    foreach (var c in allCards)
                    {
                        if (c == p || c == s)
                            continue;
                        if (RowIndex(c.Area) != RowIndex(p.Area))
                            continue;
                        if (!SegHitsCard(c, segs, placedX, yOf, colHeights))
                            continue;
                        // 挡线：上下移开（远离线高）
                        float cy = yOf[c] + colHeights[c] / 2f;
                        bool up = cy < ry;
                        float target = up ? (ry - 40f - colHeights[c]) : (ry + 40f);
                        float newY = SlideToChannel(c, target, up, yOf, colHeights, placedX);
                        if (newY != yOf[c])
                        {
                            yOf[c] = newY;
                            moved = true;
                        }
                    }
                }
            }
            if (!moved)
                break;
        }
    }

    /// <summary>卡 c 是否被任一线段（水平/垂直）穿过。</summary>
    private static bool SegHitsCard(TechNode c, (float x0, float y0, float x1, float y1)[] segs,
        Dictionary<TechNode, float> placedX, Dictionary<TechNode, float> yOf,
        Dictionary<TechNode, float> colHeights)
    {
        float cx = placedX[c], cy0 = yOf[c], cy1 = cy0 + colHeights[c];
        foreach (var (x0, y0, x1, y1) in segs)
        {
            if (Math.Abs(y0 - y1) < 0.01f)   // 水平段
            {
                if (y0 >= cy0 && y0 <= cy1
                    && Math.Min(x0, x1) < cx + CardWidth && Math.Max(x0, x1) > cx)
                    return true;
            }
            else   // 垂直段
            {
                if (x0 >= cx && x0 <= cx + CardWidth
                    && Math.Min(y0, y1) < cy1 && Math.Max(y0, y1) > cy0)
                    return true;
            }
        }
        return false;
    }

    /// <summary>从目标 Y 向方向滑动（检查同 X 区间重叠，重叠继续滑）；限 ~2 卡位，上移不低于 minY（防超出最上方），超限不动。</summary>
    private static float SlideToChannel(TechNode c, float target, bool up,
        Dictionary<TechNode, float> yOf, Dictionary<TechNode, float> colHeights,
        Dictionary<TechNode, float> placedX, float minY = 34f)
    {
        float h = colHeights[c];
        float y = target;
        if (up && y < minY)
            y = minY;
        float maxTravel = h * 2f + RowGap * 2f;
        float travel = 0f;
        for (int guard = 0; guard < 200; guard++)
        {
            bool clash = false;
            foreach (var o in placedX.Keys)
            {
                if (o == c)
                    continue;
                if (Math.Abs(placedX[o] - placedX[c]) >= TechnologyLayout.CardWidth)
                    continue;
                float o0 = yOf[o], o1 = o0 + colHeights[o];
                if (y < o1 && o0 < y + h)
                {
                    clash = true;
                    break;
                }
            }
            if (!clash)
                return y;
            float step = h + RowGap;
            y += (up ? -1f : 1f) * step;
            if (up && y < minY)
                y = minY;
            travel += step;
            if (travel > maxTravel)
                return yOf[c];
        }
        return yOf[c];
    }

    /// <summary>
    /// 正交折线路由：默认"横-竖-横"（起点水平 → 垂直 → 终点水平）。
    /// 若任一段穿过卡片，则绕行：从起点先垂直离开（到被穿卡群上方/下方空隙带），
    /// 水平绕过，再垂直进入终点——4 段"竖-横-竖"，全程不穿卡（起点/终点附近无卡）。
    /// 返回点序列（供 WPF Path / Skia SKPath 共用）。
    /// </summary>
    public static List<(float X, float Y)> RouteOrtho(float rx, float ry, float mx, float lx, float ly,
        IReadOnlyList<LayoutTech> nodes, LayoutTech self, LayoutTech pre)
    {
        // 3 段折线
        var segments = new (float x0, float y0, float x1, float y1)[]
        {
            (rx, ry, mx, ry),   // 水平段 1
            (mx, ry, mx, ly),   // 垂直段
            (mx, ly, lx, ly)    // 水平段 2
        };
        const float margin = 80f;   // 绕行空隙带 = 1/5 小列宽（垂直线距节点 ≥ 80px，用户规则）
        float top = float.MaxValue, bottom = float.MinValue;
        bool hit = false;
        foreach (var (x0, y0, x1, y1) in segments)
        {
            foreach (var n in nodes)
            {
                if (n == self || n == pre)
                    continue;
                // 线段与卡矩形相交（轴对齐线段）
                bool segHit;
                if (Math.Abs(y0 - y1) < 0.01f)   // 水平段
                    segHit = y0 >= n.Y && y0 <= n.Y + n.Height
                        && Math.Min(x0, x1) < n.X + CardWidth && Math.Max(x0, x1) > n.X;
                else                              // 垂直段
                    segHit = x0 >= n.X && x0 <= n.X + CardWidth
                        && Math.Min(y0, y1) < n.Y + n.Height && Math.Max(y0, y1) > n.Y;
                if (segHit)
                {
                    hit = true;
                    if (n.Y < top) top = n.Y;
                    if (n.Y + n.Height > bottom) bottom = n.Y + n.Height;
                }
            }
        }
        if (!hit)
            return new List<(float, float)> { (rx, ry), (mx, ry), (mx, ly), (lx, ly) };
        // 绕行：空隙带（被穿卡群上方/下方），选离线中点近的一侧
        float midY = (ry + ly) / 2f;
        float upLane = top - margin;
        float downLane = bottom + margin;
        float laneY = Math.Abs(upLane - midY) <= Math.Abs(downLane - midY) ? upLane : downLane;
        // 绕行：竖-横-竖，但垂直段**距节点 ≥ 80px**（A 右缘外 80、B 左缘外 80——用户规则：
        // "先 x 变 y 不变 → y 变 x 不变 → x 变 y 不变"，A 端先水平 80 再垂直，B 端垂直到 80 处再水平接 B）
        float ax = rx + 80f;   // A 端垂直段 X（距 A 右缘 80）
        float bx = lx - 80f;   // B 端垂直段 X（距 B 左缘 80）
        if (ax > bx)
            ax = bx = (rx + lx) / 2f;   // 空隙不足 160px：两垂直段合一（中点兜底）
        return new List<(float, float)> { (rx, ry), (ax, ry), (ax, laneY), (bx, laneY), (bx, ly), (lx, ly) };
    }

    /// <summary>
    /// 检测**同来源线的转向点**（用户规则）：同一前置（源）分出的多条线，若在**同一 X 垂直转向**
    /// （主干 1 条 → 多分支，"1 个变 2 个"），在该转向点返回圆点标记；
    /// 不同来源的线交叉不标记（正常交叉）。跳过单线（count&lt;2 不画）。
    /// </summary>
    public static List<(float X, float Y)> FindCrossings(List<(string Source, List<(float X, float Y)> Pts)> polylines)
    {
        var pts = new List<(float, float)>();
        foreach (var grp in polylines.GroupBy(p => p.Source))
        {
            var turnCount = new Dictionary<int, (float X, float Y, int Count)>();
            foreach (var (_, pl) in grp)
            {
                if (pl.Count < 3)
                    continue;
                float trunkY = pl[0].Y;   // 主干 Y（起点）
                for (int i = 0; i + 1 < pl.Count; i++)
                {
                    var a = pl[i];
                    var b = pl[i + 1];
                    if (Math.Abs(a.X - b.X) < 0.01f && Math.Abs(a.Y - b.Y) > 0.01f)   // 垂直段
                    {
                        int key = (int)Math.Round(a.X);
                        if (turnCount.TryGetValue(key, out var t))
                            turnCount[key] = (a.X, trunkY, t.Count + 1);
                        else
                            turnCount[key] = (a.X, trunkY, 1);
                    }
                }
            }
            foreach (var (_, (x, y, count)) in turnCount)
                if (count >= 2)
                    pts.Add((x, y));
        }
        return pts;
    }

    /// <summary>卡片中心点（画贝塞尔连线用：前置卡右缘中点 → 本卡左缘中点，Y 取实际卡高中点）。</summary>
    public static (float X, float Y) RightCenter(LayoutTech n) => (n.X + CardWidth, n.Y + n.Height / 2f);
    public static (float X, float Y) LeftCenter(LayoutTech n) => (n.X, n.Y + n.Height / 2f);

    /// <summary>
    /// 检测贝塞尔连线（前置右缘 px,py → 后继左缘 sx,sy，控制点 px+60/py、sx-60/sy）是否穿过其他卡片。
    /// 穿则返回绕行通道 Y（被穿卡群上方/下方空隙，自动选离线中点近的一侧——先直后弯）。
    /// 供页面（WPF）与导出（Skia）共用。
    /// </summary>
    public static bool EdgeHitsCards(float px, float py, float sx, float sy,
        IReadOnlyList<LayoutTech> nodes, LayoutTech self, LayoutTech pre, out float laneY)
    {
        laneY = 0f;
        const float margin = 80f;   // 垂直线距节点 ≥ 1/5 小列宽（400/5=80，用户规则）
        float top = float.MaxValue, bottom = float.MinValue;
        bool hit = false;
        for (int i = 1; i <= 24; i++)
        {
            float t = i / 24f;
            float mt = 1f - t;
            float x = mt * mt * mt * px + 3 * mt * mt * t * (px + 60) + 3 * mt * t * t * (sx - 60) + t * t * t * sx;
            float y = mt * mt * mt * py + 3 * mt * mt * t * py + 3 * mt * t * t * sy + t * t * t * sy;
            foreach (var n in nodes)
            {
                if (n == self || n == pre)
                    continue;
                if (x >= n.X && x <= n.X + CardWidth && y >= n.Y && y <= n.Y + n.Height)
                {
                    hit = true;
                    if (n.Y < top) top = n.Y;
                    if (n.Y + n.Height > bottom) bottom = n.Y + n.Height;
                }
            }
        }
        if (!hit)
            return false;
        float midY = (py + sy) / 2f;
        float upLane = top - margin;
        float downLane = bottom + margin;
        laneY = Math.Abs(upLane - midY) <= Math.Abs(downLane - midY) ? upLane : downLane;
        return true;
    }

    /// <summary>
    /// 垂直线转向登记表（用户方案）：每个转向 X 维护已占用的 Y 区间（含来源）。
    /// **同源（同前置科技）线允许 Y 撞击**（尽量同 X，节约信道）；不同源冲突时：
    /// **先选"Y 占用最多"且不冲突的 X（8 路内优先）→ 40px±8 → 仍不够 20px±19**；
    /// 全不够返回 null（调用方改用**跳线字母标记**，AA…ZZ）。
    /// </summary>
    public sealed class TurnLaneTable
    {
        private readonly Dictionary<int, List<(float Y0, float Y1, string Source)>> _lanes = new();
        private readonly Dictionary<string, int> _sourceX = new();   // 源 → 已绑定的转向 X（同源线统一转向）
        private int _jumpSeq;

        /// <summary>登记垂直线段（候选 X、Y 区间、来源）。返回实际使用的 X；null = 需要跳线标记。</summary>
        public float? Register(float x, float y0, float y1, string source)
        {
            // **同源统一转向**：同源（同前置）线复用第一次登记的转向 X（不重新分道）
            if (_sourceX.TryGetValue(source, out var sx))
            {
                Add(sx, y0, y1, source);
                return sx;
            }
            int key = (int)Math.Round(x);
            // 1) 8 路（40px ±8）范围内：原 X → "Y 占用最多"且不冲突（含空信道）
            var cand8 = new List<int> { key };
            for (int i = 1; i <= 8; i++)
            {
                cand8.Add(key + i * 40);
                cand8.Add(key - i * 40);
            }
            var best = PickBest(cand8, y0, y1, source);
            if (best.HasValue)
            {
                _sourceX[source] = best.Value;
                return best;
            }
            // 2) 8 路不够 → 19 路（20px ±19）
            var cand19 = new List<int>();
            for (int i = 1; i <= 19; i++)
            {
                cand19.Add(key + i * 20);
                cand19.Add(key - i * 20);
            }
            best = PickBest(cand19, y0, y1, source);
            if (best.HasValue)
            {
                _sourceX[source] = best.Value;
                return best;
            }
            return null;   // 需要跳线标记
        }

        /// <summary>分配跳线 2 位大写字母（AA…ZZ，676 个）。</summary>
        public string AllocJumpTag()
        {
            int n = _jumpSeq++;
            return "" + (char)('A' + n / 26) + (char)('A' + n % 26);
        }

        private int? PickBest(List<int> candidates, float y0, float y1, string source)
        {
            int bestKey = -1, bestCount = -1;
            foreach (var k in candidates)
            {
                if (Conflict(k, y0, y1, source))
                    continue;
                int count = _lanes.TryGetValue(k, out var segs) ? segs.Count : 0;   // 空信道 count=0 也接受
                if (count > bestCount)
                {
                    bestCount = count;
                    bestKey = k;
                }
            }
            if (bestKey >= 0)
            {
                Add(bestKey, y0, y1, source);
                return bestKey;
            }
            return null;
        }

        /// <summary>冲突 = 存在**不同来源**的线段 Y 区间重叠（同源允许撞击——节约信道）。</summary>
        private bool Conflict(int x, float y0, float y1, string source)
            => _lanes.TryGetValue(x, out var segs)
               && segs.Any(s => s.Source != source && y0 < s.Y1 && s.Y0 < y1);

        private void Add(int x, float y0, float y1, string source)
        {
            if (!_lanes.TryGetValue(x, out var segs))
                _lanes[x] = segs = new List<(float, float, string)>();
            segs.Add((Math.Min(y0, y1), Math.Max(y0, y1), source));
        }
    }
}
