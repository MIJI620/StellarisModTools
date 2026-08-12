// 文件: Stellaris.Engine/Technology/TechnologyRenderer.cs
// 科技节点图渲染器（SkiaSharp 离屏绘制）。
//
// ⚠️ 旧"动态生成连线图"渲染（Render/RenderTile/DrawWorld 中的连线部分）= **失败的试验性产物，
//    已隐藏（2026-08）**：仅存档保留，页面/导出一律不再调用。
//
// ✅ 当前模式 = **文本标签模式渲染（RenderLabel/RenderLabelTile）**：
//    3 行学科色六边形密铺背景 + tier 列分隔线/标题 + 卡片（DrawCard 节点构造未改动）+
//    左右尖角框标签（左侧前置、右侧后继，替代连线）。
// 渲染结果 SKBitmap → 页面层转 WPF BitmapSource 显示，或导出 PNG。
// 数据来自 TechnologyEngine（本地化/modifier/图标路径）；图标经 ImageAssetEngine 加载（dds 用 Pfim 解码）。

using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Stellaris.Engine.Ship;

namespace Stellaris.Engine.Technology;

/// <summary>
/// 渲染器（无 WPF 依赖，可在控制台/测试中直接使用）。
/// 卡片：边框（危险红 > 稀有紫 > 白）+ 标题条（大类背景色 + 翻译名）+
/// 左图标 / 中上描述 / 中下效果（modifier）/ 右侧学科图标 + cost + levels/cost_per_level。
/// </summary>
public sealed class TechnologyRenderer
{
    private readonly TechnologyEngine _engine;
    private readonly Func<string, SKBitmap?> _iconLoader;
    private readonly Dictionary<string, SKBitmap?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(float, bool), SKFont> _fontCache = new();
    private static readonly SKTypeface Typeface =
        SKTypeface.FromFamilyName("Microsoft YaHei", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        ?? SKTypeface.Default;

    private static readonly SKColor BgColor = new(0xFF, 0xFF, 0xFF);          // 导出背景：白色（用户要求）
    private static readonly SKColor BandLineColor = new(0xD8, 0xD8, 0xD8);    // 列分隔线（白底浅灰）
    private static readonly SKColor BorderNormal = new(0x99, 0x99, 0x99);     // 普通边框（白底中灰）
    private static readonly SKColor BorderRare = new(0x9B, 0x59, 0xB6);
    private static readonly SKColor BorderDanger = new(0xC0, 0x39, 0x2B);
    private static readonly SKColor TitlePhysics = new(0x3A, 0x6E, 0xA5);
    private static readonly SKColor TitleSociety = new(0x3F, 0x7D, 0x51);
    private static readonly SKColor TitleEngineering = new(0xB0, 0x8D, 0x2E);
    private static readonly SKColor TextLight = new(0xEA, 0xEA, 0xEA);        // 卡片内文字（深灰卡板上浅色，与页面一致）
    private static readonly SKColor TextDim = new(0xAA, 0xAA, 0xAA);          // 卡片内次要文字
    private static readonly SKColor TitleText = new(0xFF, 0xFF, 0xFF);        // 标题条文字（彩色条上白色）
    private static readonly SKColor CanvasText = new(0x22, 0x22, 0x22);       // 画布上文字（白底深色：Tier 标题）

    /// <summary>构造。iconLoader：按相对路径加载 dds/图像（页面注入 ImageAssetEngine 包装），null = 不加载图标。</summary>
    public TechnologyRenderer(TechnologyEngine engine, Func<string, SKBitmap?>? iconLoader = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _iconLoader = iconLoader ?? (_ => null);
    }

    /// <summary>渲染全图（世界坐标位图）。整图渲染仅用于小布局/调试；大布局请用 RenderTile 分块。</summary>
    public SKBitmap Render(TechLayout layout, string lang = "english")
        => RenderRange(layout, 0, Math.Max(1, layout.Width), 0, Math.Max(1, layout.Height), lang);

    /// <summary>
    /// 分块渲染：渲染布局矩形范围 [xStart, xEnd) × [yStart, yEnd)（世界坐标）为独立 SKBitmap。
    /// 块之间共用同一世界坐标、无缝拼接（无缝隙无重叠）；超大布局按视口分批渲染，
    /// 避免一次性创建 195MB+ 位图导致 Skia 原生崩溃。
    /// </summary>
    public SKBitmap RenderTile(TechLayout layout, float xStart, float xEnd, float yStart, float yEnd, string lang = "english")
        => RenderRange(layout, xStart, xEnd, yStart, yEnd, lang);

    private SKBitmap RenderRange(TechLayout layout, float xStart, float xEnd, float yStart, float yEnd, string lang)
    {
        float hi = Math.Min(yEnd, layout.Height);
        float lo = Math.Max(0, yStart);
        float ri = Math.Min(xEnd, layout.Width);
        float le = Math.Max(0, xStart);
        if (lo >= hi)
            lo = Math.Max(0, hi - 1);
        if (le >= ri)
            le = Math.Max(0, ri - 1);

        int w = Math.Max(1, (int)Math.Ceiling(ri - le) + 2);
        int h = Math.Max(1, (int)Math.Ceiling(hi - lo));
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Translate(-le, -lo);
            canvas.ClipRect(new SKRect(le, lo, ri, hi));
            DrawWorld(canvas, layout, le, ri, lo, hi, lang);
        }
        return bmp;
    }

    /// <summary>世界坐标绘制（分块共用）：背景/竖带分隔线/Tier 标题/贝塞尔连线/卡片。
    /// 只绘制与 [xLo, xHi) × [yLo, yHi) 相交的内容（性能：超大布局分块时跳过块外卡片）。</summary>
    private void DrawWorld(SKCanvas canvas, TechLayout layout, float xLo, float xHi, float yLo, float yHi, string lang)
    {
        canvas.Clear(BgColor);
        var byKey = layout.Nodes.ToDictionary(n => n.Node.Key, StringComparer.OrdinalIgnoreCase);

        // 行带分隔线 + 行内 Tier 标题（标题位于行顶标题区，块裁剪后仅在对应块显示）
        var titleFont = Font(FontSizeScale + 8, true);   // Tier 带标题
        foreach (var band in layout.Bands)
        {
            float rowTop = layout.Rows.Count > band.Row ? layout.Rows[band.Row].Y : 0f;
            float rowBot = layout.Rows.Count > band.Row ? rowTop + layout.Rows[band.Row].Height : layout.Height;
            float lineX = band.X + band.Width;
            canvas.DrawLine(lineX, rowTop, lineX, rowBot, new SKPaint { Color = BandLineColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1 });
            string title = band.Tier >= 0 ? $"Tier {band.Tier}" : "?";
            canvas.DrawText(Blob(title, titleFont), band.X + 8, rowTop + 22, Fill(CanvasText));
        }

        // 与本块相交的卡片（含其前置连线；曲线跨块时由裁剪保留本块部分——无缝）
        var visible = layout.Nodes.Where(n =>
            n.X + TechnologyLayout.CardWidth >= xLo && n.X <= xHi &&
            n.Y + n.Height >= yLo && n.Y <= yHi).ToList();

        // 连线：遍历**全部**前置关系（不只可见卡）——曲线 bbox 与本块相交即画（Clip 保留块内部分）。
        // 修复：线穿过中间块（两端卡都不在本块）时缺失导致的拼接断裂/截断。
        var linePaint = new SKPaint { Color = new SKColor(0x77, 0x8A, 0xB5), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        var allPts = new List<(string Source, List<(float X, float Y)> Pts)>();   // 全部折线（同源相交点圆点用）
        var laneTable = new TechnologyLayout.TurnLaneTable();   // 垂直线转向分道登记（用户方案）
        // 预计算 tier 列左缘（该列最小 X）——主干拐弯点 = 列左缘前空隙（同列后继共享，在一起拐弯）
        var colLeft = new Dictionary<int, float>();
        foreach (var n in layout.Nodes)
        {
            if (!colLeft.TryGetValue(n.Node.Tier, out var v) || n.X < v)
                colLeft[n.Node.Tier] = n.X;
        }
        foreach (var node in layout.Nodes)
        {
            var (lx, ly) = TechnologyLayout.LeftCenter(node);
            foreach (var pre in node.Node.Prerequisites)
            {
                if (!byKey.TryGetValue(pre, out var preNode))
                    continue;
                var (rx, ry) = TechnologyLayout.RightCenter(preNode);
                // 主干：线先水平延伸，到"后继所在列左缘前空隙"再拐弯（同列后继共享拐弯 X——在一起拐弯）；
                // 该 X 被卡占或反向（后继在左侧）则退化到 P 右侧——页面/导出一致
                float turnX = colLeft.TryGetValue(node.Node.Tier, out var cl) ? cl - 80f : rx + 80f;   // 垂直线距节点 ≥ 80px（页面/导出一致）
                if (turnX < rx + 80f)   // 空隙不足：退化到距 A 右缘 80
                    turnX = rx + 80f;
                bool trunkOk = true;
                foreach (var n in layout.Nodes)
                {
                    if (n == node || n == preNode)
                        continue;
                    if (turnX >= n.X && turnX <= n.X + TechnologyLayout.CardWidth)
                    {
                        trunkOk = false;
                        break;
                    }
                }
                if (!trunkOk)
                    turnX = TechnologyLayout.RouteOrthogonalX(rx, lx, layout.Nodes, node, preNode);
                // 不同起点的线转向 X 错开（同源同 X，不同源在空隙内 ±20 错开——减少同点撞车）
                turnX += TechnologyLayout.LineVerticalOffset(preNode.Node.Key, "");
                // clamp：垂直线距节点 ≥ 80px（偏移不能突破；空隙不足时退到距 A 右缘 80）
                float maxTx = colLeft.TryGetValue(node.Node.Tier, out var cl2) ? cl2 - 80f : float.MaxValue;
                turnX = Math.Clamp(turnX, rx + 80f, Math.Max(rx + 80f, maxTx));
                // **转向表分道**（用户方案）：垂直线登记 Y 区间，冲突则优先选"Y 占用最多"的 X、
                // 再分道（40→20px 步进）；仍不够 → **跳线字母标记**（线延伸一段 + 方框 AA…ZZ，两端配对）
                float vMin = Math.Min(ry, ly), vMax = Math.Max(ry, ly);
                var laneX = laneTable.Register(turnX, vMin, vMax, preNode.Node.Key);
                if (laneX == null)
                {
                    string tag = laneTable.AllocJumpTag();
                    // 源端延伸段 + 目标端延伸段（不连到底）+ 方框字母
                    canvas.DrawLine(rx, ry, rx + 80f, ry, linePaint);
                    canvas.DrawLine(lx - 80f, ly, lx, ly, linePaint);
                    DrawTagBoxSkia(canvas, rx + 80f, ry, tag);
                    DrawTagBoxSkia(canvas, lx - 80f, ly, tag);
                    continue;
                }
                turnX = laneX.Value;
                // 穿卡绕行（RouteOrtho：折线任一段穿卡 → 竖-横-竖绕行；否则 横-竖-横）——页面/导出一致
                var pts = TechnologyLayout.RouteOrtho(rx, ry, turnX, lx, ly, layout.Nodes, node, preNode);
                allPts.Add((preNode.Node.Key, pts));
                // 折线 bbox（pts 范围）
                float bMinX = pts.Min(p => p.X), bMaxX = pts.Max(p => p.X);
                float bMinY = pts.Min(p => p.Y), bMaxY = pts.Max(p => p.Y);
                if (bMaxX < xLo || bMinX > xHi || bMaxY < yLo || bMinY > yHi)
                    continue;   // 折线与本块不相交
                var path = new SKPath();
                path.MoveTo(pts[0].X, pts[0].Y);
                for (int i = 1; i < pts.Count; i++)
                    path.LineTo(pts[i].X, pts[i].Y);
                canvas.DrawPath(path, linePaint);
            }
        }

        // 相交点圆点：有圆点 = 线在这里相交（撞车）；没有 = 线只是错开（用户规则）——页面/导出一致
        var dotPaint = new SKPaint { Color = new SKColor(0xFF, 0x5C, 0x38), Style = SKPaintStyle.Fill, IsAntialias = true };
        foreach (var (cx, cy) in TechnologyLayout.FindCrossings(allPts))
            canvas.DrawCircle(cx, cy, 3f, dotPaint);

        // 卡片
        foreach (var node in visible)
            DrawCard(canvas, node, lang);
    }

    // ==================== 文本标签模式渲染（当前唯一在用；旧连线模式已隐藏） ====================

    /// <summary>✅ 文本标签模式整图渲染（当前唯一在用）：3 行学科色六边形密铺背景 + tier 列 + 卡片 + 左右尖角框标签。
    /// 节点构造（DrawCard）复用未改动；旧连线模式 Render/DrawWorld 已隐藏（2026-08）。</summary>
    public SKBitmap RenderLabel(TechLayout layout, string lang = "english")
        => RenderLabelRange(layout, 0, Math.Max(1, layout.Width), 0, Math.Max(1, layout.Height), lang);

    /// <summary>✅ 文本标签模式分块渲染：超大布局按视口分批渲染，避免大位图崩溃（与旧 RenderTile 同思路）。</summary>
    public SKBitmap RenderLabelTile(TechLayout layout, float xStart, float xEnd, float yStart, float yEnd, string lang = "english")
        => RenderLabelRange(layout, xStart, xEnd, yStart, yEnd, lang);

    private SKBitmap RenderLabelRange(TechLayout layout, float xStart, float xEnd, float yStart, float yEnd, string lang)
    {
        float hi = Math.Min(yEnd, layout.Height);
        float lo = Math.Max(0, yStart);
        float ri = Math.Min(xEnd, layout.Width);
        float le = Math.Max(0, xStart);
        if (lo >= hi)
            lo = Math.Max(0, hi - 1);
        if (le >= ri)
            le = Math.Max(0, ri - 1);

        int w = Math.Max(1, (int)Math.Ceiling(ri - le) + 2);
        int h = Math.Max(1, (int)Math.Ceiling(hi - lo));
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Translate(-le, -lo);
            canvas.ClipRect(new SKRect(le, lo, ri, hi));
            DrawLabelWorld(canvas, layout, le, ri, lo, hi, lang);
        }
        return bmp;
    }

    /// <summary>文本标签模式世界坐标绘制（分块共用）：行背景六边形密铺 → 列分隔线/Tier 标题 → 左右标签 → 卡片。
    /// 节点卡片复用 DrawCard（节点构造未改动）。</summary>
    private void DrawLabelWorld(SKCanvas canvas, TechLayout layout, float xLo, float xHi, float yLo, float yHi, string lang)
    {
        canvas.Clear(BgColor);

        // 1) 行背景六边形网格线已删除（用户规则：背景网格线直接删了）——行内仅标题条/竖线/行底线

        // 1b) 每行底部学科色直线标记（画在内容底部 2px，行间无白色空行——用户规则）
        foreach (var row in layout.Rows)
        {
            if (row.Height <= 0f)
                continue;
            canvas.DrawRect(new SKRect(0f, row.Y + row.Height - 2f, layout.Width, row.Y + row.Height),
                new SKPaint { Color = GetRowColor(row.Row) });
        }

        // 2) 行顶学科色标题条（**贯穿整行**——用户规则：写阶数的一行占满整行）+ 阶数文本 + 学科色分隔竖线
        bool chinese = IsChineseLang(lang);
        var titleFont = Font(FontSizeScale + 7, true);
        foreach (var row in layout.Rows)
        {
            if (row.Height <= 0f)
                continue;
            canvas.DrawRect(new SKRect(0f, row.Y, layout.Width, row.Y + 34),   // 标题条 34（用户：加高、文字底部空间不够）
                new SKPaint { Color = GetRowColor(row.Row) });
        }
        foreach (var band in layout.Bands)
        {
            float rowTop = layout.Rows.Count > band.Row ? layout.Rows[band.Row].Y : 0f;
            float rowBot = layout.Rows.Count > band.Row ? rowTop + layout.Rows[band.Row].Height : layout.Height;
            var rowColor = GetRowColor(band.Row);
            string title = band.Tier >= 0 ? (chinese ? $"{band.Tier}阶" : $"Tier {band.Tier}") : "?";
            canvas.DrawText(Blob(title, titleFont), band.X + 10, rowTop + 24, Fill(new SKColor(0xFF, 0xFF, 0xFF)));   // 基线 24（34px 标题条内居中）
            // 列分隔竖线（学科色，明显；标题条下方起）
            canvas.DrawLine(band.X + band.Width, rowTop + 34, band.X + band.Width, rowBot,
                new SKPaint { Color = new SKColor(rowColor.Red, rowColor.Green, rowColor.Blue, 0x99), StrokeWidth = 1.5f });
        }

        var byKey = layout.Nodes.ToDictionary(n => n.Node.Key, StringComparer.OrdinalIgnoreCase);
        // 后继索引（反查——含跨学科，标签显示全部后继）
        var succ = new Dictionary<string, List<LayoutTech>>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in layout.Nodes)
        {
            foreach (var pk in n.Node.Prerequisites)
            {
                if (!byKey.TryGetValue(pk, out var p))
                    continue;
                if (!succ.TryGetValue(pk, out var l))
                    succ[pk] = l = new List<LayoutTech>();
                l.Add(n);
            }
        }

        var visible = layout.Nodes.Where(n =>
            n.X + TechnologyLayout.CardWidth >= xLo && n.X <= xHi &&
            n.Y + n.Height >= yLo && n.Y <= yHi).ToList();

        // 3) 左右尖角框标签（前置左侧、后继右侧——替代连线；**背景 = 节点卡片色 0x2A2A34**、
        //    文本色按稀有度（危险红/常规白/稀有紫）、边框/科技线 = 对应科技学科色；
        //    前置**左对齐**、后继**右对齐**（文字）；框宽随字符自适应；**标签跟着节点走**（不是背景））
        var tagFont = Font(Math.Max(8f, FontSizeScale - 1f), false);   // 用户：标签字体放大（至少 +1）
        foreach (var node in visible)
        {
            // 前置标签（节点左侧，框右缘固定 = 节点左缘 - 14，向左展开）
            var pres = node.Node.Prerequisites
                .Where(p => byKey.ContainsKey(p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (pres.Count > 0)
            {
                float stackH = TechnologyLayout.TagStackHeight(pres.Count);
                float top = node.Y + Math.Max(0f, (node.Height - stackH) / 2f);
                float right = node.X - 14f;
                for (int i = 0; i < pres.Count; i++)
                {
                    float ty = top + i * (TechnologyLayout.TagHeight + TechnologyLayout.TagGap);
                    var preNode = byKey[pres[i]];
                    string label = _engine.LocalisedName(pres[i], lang);
                    float w = Math.Min(tagFont.MeasureText(label) + 20f, TechnologyLayout.LabelZoneWidth - 8f);
                    float x = right - w;
                    var preColor = GetRowColor(TechnologyLayout.RowIndexOf(preNode.Node.Area));
                    // 科技线：框右缘（尖角）→ 节点左缘（颜色 = 前置科技学科色）
                    canvas.DrawLine(right, ty + TechnologyLayout.TagHeight / 2f,
                        node.X, ty + TechnologyLayout.TagHeight / 2f,
                        new SKPaint { Color = preColor, StrokeWidth = 1.5f });
                    // 文字自适应字号（长名缩小，完整显示不截断——用户：标签比文字小）
                    var preFont = FitTagFont(tagFont, label, w);
                    DrawLabelBoxSkia(canvas, x, ty, w, label, tipRight: true, preFont, preColor, TagTextColorSkia(preNode.Node));
                }
            }
            // 后继标签（节点右侧，框左缘固定 = 节点右缘 + 14，向右展开）
            if (succ.TryGetValue(node.Node.Key, out var kids) && kids.Count > 0)
            {
                var sorted = kids.OrderBy(k => k.Node.Key, StringComparer.Ordinal).ToList();
                float stackH = TechnologyLayout.TagStackHeight(sorted.Count);
                float top = node.Y + Math.Max(0f, (node.Height - stackH) / 2f);
                float left = node.X + TechnologyLayout.CardWidth + 14f;
                for (int i = 0; i < sorted.Count; i++)
                {
                    float ty = top + i * (TechnologyLayout.TagHeight + TechnologyLayout.TagGap);
                    string label = _engine.LocalisedName(sorted[i].Node.Key, lang);
                    float w = Math.Min(tagFont.MeasureText(label) + 20f, TechnologyLayout.LabelZoneWidth - 8f);
                    var kidColor = GetRowColor(TechnologyLayout.RowIndexOf(sorted[i].Node.Area));
                    // 科技线：节点右缘 → 框左缘（尖角）（颜色 = 后继科技学科色）
                    canvas.DrawLine(node.X + TechnologyLayout.CardWidth, ty + TechnologyLayout.TagHeight / 2f,
                        left, ty + TechnologyLayout.TagHeight / 2f,
                        new SKPaint { Color = kidColor, StrokeWidth = 1.5f });
                    // 文字自适应字号（长名缩小，完整显示不截断——用户：标签比文字小）
                    var kidFont = FitTagFont(tagFont, label, w);
                    DrawLabelBoxSkia(canvas, left, ty, w, label, tipRight: false, kidFont, kidColor, TagTextColorSkia(sorted[i].Node));
                }
            }
        }

        // 4) 卡片（节点构造未改动——复用 DrawCard）
        foreach (var node in visible)
            DrawCard(canvas, node, lang);
    }

    /// <summary>尖角框标签（Skia）：矩形 + 一侧三角尖角——**背景 = 节点卡片色 0x2A2A34**、边框 = 对应科技学科色、
    /// 文本色按稀有度（危险红/常规白/稀有紫）。前置标签（tipRight）文字左对齐、后继标签（tipLeft）文字右对齐；框宽随字符自适应。</summary>
    private void DrawLabelBoxSkia(SKCanvas canvas, float x, float y, float w, string text, bool tipRight, SKFont font, SKColor borderColor, SKColor textColor)
    {
        float h = TechnologyLayout.TagHeight;
        float tip = TechnologyLayout.TagTipSize;
        float bodyW = Math.Max(8f, w - tip);   // 矩形区宽（尖角另算）
        float cy = y + h / 2f;
        var pts = tipRight
            ? new[] { new SKPoint(x, y), new SKPoint(x + bodyW, y), new SKPoint(x + bodyW + tip, cy), new SKPoint(x + bodyW, y + h), new SKPoint(x, y + h) }
            : new[] { new SKPoint(x + bodyW, y), new SKPoint(x + bodyW, y + h), new SKPoint(x + tip, y + h), new SKPoint(x, cy), new SKPoint(x + tip, y) };
        using var path = new SKPath();
        path.MoveTo(pts[0]);
        for (int i = 1; i < pts.Length; i++)
            path.LineTo(pts[i]);
        path.Close();
        canvas.DrawPath(path, new SKPaint { Color = new SKColor(0x2A, 0x2A, 0x34), Style = SKPaintStyle.Fill, IsAntialias = true });
        canvas.DrawPath(path, new SKPaint { Color = borderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true });
        string disp = Truncate(text, font, bodyW - 10f);
        if (disp.Length == 0)
            return;
        // 前置文字左对齐（x+5 起）；后继文字右对齐（x + w - 5 - 文字宽）
        float textW = font.MeasureText(disp);
        float tx = tipRight ? x + 5f : x + w - 5f - textW;
        canvas.DrawText(Blob(disp, font), tx, cy + font.Size / 2f - 2f, Fill(textColor));
    }

    /// <summary>六边形网格密铺（矩形区域）：填充学科色半透明 + 轮廓线（交替行错位平铺，尖顶朝上）。</summary>
    private static void DrawHexTessellation(SKCanvas canvas, float x0, float y0, float w, float h, SKColor color)
    {
        const float side = 26f;                    // 六边形边长
        float hexW = side * 1.73205f;              // 水平中心距 = √3·s
        float hexH = side * 1.5f;                  // 垂直行距 = 1.5·s
        var stroke = new SKPaint { Color = new SKColor(color.Red, color.Green, color.Blue, 0x8C), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        // 只画网格线（不画填充）——用户规则：网格线稳定、填充色易丢（页面/导出一致）
        int rows = (int)(h / hexH) + 2;
        int cols = (int)(w / hexW) + 2;
        if (rows <= 0 || cols <= 0)
            return;
        var pts = new SKPoint[6];
        using var path = new SKPath();
        for (int r = 0; r < rows; r++)
        {
            float cy = y0 + side * 0.86603f + r * hexH;         // 首行中心 Y（sin60·s 内缩）
            float cx0 = x0 + (r % 2 == 0 ? 0f : hexW / 2f);     // 交替行水平错位
            for (int c = 0; c < cols; c++)
            {
                float cx = cx0 + c * hexW;
                for (int i = 0; i < 6; i++)
                {
                    double ang = Math.PI / 3.0 * i - Math.PI / 2.0;   // 尖顶朝上
                    pts[i] = new SKPoint(cx + side * (float)Math.Cos(ang), cy + side * (float)Math.Sin(ang));
                }
                path.Reset();
                path.MoveTo(pts[0]);
                for (int i = 1; i < 6; i++)
                    path.LineTo(pts[i]);
                path.Close();
                canvas.DrawPath(path, stroke);
            }
        }
    }

    /// <summary>标签文字自适应字号：文字超过框内可用宽（w-18）时逐级缩小（最小 7），完整显示不截断（用户：标签比文字小）。</summary>
    private SKFont FitTagFont(SKFont baseFont, string text, float w)
    {
        float avail = Math.Max(8f, w - 18f);
        if (baseFont.MeasureText(text) <= avail)
            return baseFont;
        for (float size = baseFont.Size - 1f; size >= 7f; size -= 1f)
        {
            var f = Font(size, false);
            if (f.MeasureText(text) <= avail)
                return f;
        }
        return Font(7f, false);
    }

    /// <summary>行号 → 学科色（Row: 0=physics 1=society 2=engineering 3=other）。</summary>
    private static SKColor GetRowColor(int row)
    {
        switch (row)
        {
            case 0: return TitlePhysics;
            case 1: return TitleSociety;
            case 2: return TitleEngineering;
            default: return new SKColor(0x55, 0x55, 0x60);
        }
    }

    /// <summary>是否中文界面语种（Tier 标题显示"1阶"而非"Tier 1"）。</summary>
    private static bool IsChineseLang(string lang)
        => lang.Contains("chinese", StringComparison.OrdinalIgnoreCase)
           || lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>标签文本色按科技稀有度（用户规则）：危险=红、稀有=紫、常规=白（与卡片文字色一致）。</summary>
    private static SKColor TagTextColorSkia(TechNode t)
    {
        if (t.IsDangerous) return new SKColor(0xC0, 0x39, 0x2B);
        if (t.IsRare) return new SKColor(0x9B, 0x59, 0xB6);
        return new SKColor(0xEA, 0xEA, 0xEA);
    }

    /// <summary>跳线方框字母（Skia）：白底方框 + 2 位大写字母（两端配对）。</summary>
    private void DrawTagBoxSkia(SKCanvas canvas, float cx, float cy, string tag)
    {
        using var fill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        using var border = new SKPaint { Color = new SKColor(0x66, 0x66, 0x66), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        canvas.DrawRect(new SKRect(cx - 12, cy - 8, cx + 12, cy + 8), fill);
        canvas.DrawRect(new SKRect(cx - 12, cy - 8, cx + 12, cy + 8), border);
        using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawText(Blob(tag, Font(9f, false)), cx - 10, cy + 3, textPaint);
    }

    private void DrawCard(SKCanvas canvas, LayoutTech node, string lang)
    {
        var tech = node.Node;
        float x = node.X, y = node.Y;
        float w = TechnologyLayout.CardWidth, h = node.Height;   // 高度自适应
        var rect = new SKRect(x, y, x + w, y + h);

        // 边框色：危险红 > 稀有紫 > 白（**最后画**——盖住底板边缘，描边随卡片扩展高度完整）
        var border = tech.IsDangerous ? BorderDanger : tech.IsRare ? BorderRare : BorderNormal;
        var borderPaint = new SKPaint { Color = border, Style = SKPaintStyle.Stroke, StrokeWidth = 4 };   // 用户：边框加粗到 4px

        // 标题条（**底色 = 学科色不变**——用户：底色不让改；标题**文字颜色**按稀有度：危险=红、稀有=紫、常规=白）
        var titleColor = GetAreaColor(tech.Area);
        var titleTextColor = tech.IsDangerous ? new SKColor(0xC0, 0x39, 0x2B)
            : tech.IsRare ? new SKColor(0x9B, 0x59, 0xB6)
            : TitleText;
        var titleRect = new SKRect(x, y, x + w, y + 26);
        canvas.DrawRoundRect(titleRect, 10, 10, new SKPaint { Color = titleColor });   // 圆角 10（顶部，标题色块自身）
        canvas.DrawRect(new SKRect(x, y + 16, x + w, y + 26), new SKPaint { Color = titleColor });
        var titleFont = Font(FontSizeScale + 1, true);   // 卡标题
        var title = _engine.LocalisedName(tech.Key, lang) ?? tech.Key;
        canvas.DrawText(Blob(Truncate(title, titleFont, w - 16), titleFont), x + 8, y + 18, Fill(titleTextColor));   // 文字颜色按稀有度

        // 卡片底板：内容区（标题条下方）深灰色（与页面卡片一致）——底部留 2px 描边位
        canvas.DrawRect(new SKRect(x, y + 26, x + w, y + h - 2), new SKPaint { Color = new SKColor(0x2A, 0x2A, 0x34) });

        // 内容区（y+26 以下）：左图标 / 中上描述 / 中下效果 / 右侧 cost
        float contentTop = y + 32;
        float leftIconSize = 44;
        var icon = LoadIcon(_engine.GetTechIconPath(tech));
        if (icon != null)
        {
            var irect = new SKRect(x + 6, contentTop + 2, x + 6 + leftIconSize, contentTop + 2 + leftIconSize);
            canvas.DrawBitmap(icon, irect, new SKPaint { IsAntialias = true });
        }
        else
        {
            canvas.DrawRoundRect(new SKRect(x + 6, contentTop + 2, x + 6 + leftIconSize, contentTop + 2 + leftIconSize),
                4, 4, new SKPaint { Color = new SKColor(0x33, 0x33, 0x40) });
        }

        // 先计算右侧占用（cost 数字宽 / 加成数值列最大宽 / 学科图标 24）——描述右边界**动态**，不用静态值
        var costFont = Font(FontSizeScale, true);
        string costStr = tech.Cost.ToString();
        float costW = costFont.MeasureText(costStr);
        var modFont = Font(FontSizeScale - 1, false);   // 与页面 mods 行字号一致（页面 fontSize-1）
        // mods = 数值加成 + 解锁行（与页面一致：全局非科技解锁 + 舰船/武器解锁）——导出图不缺"解锁了什么"
        var mods = _engine.GetModifierLines(tech, lang).Select(m => (m.Display, m.Value)).ToList();
        foreach (var uk in _engine.GetUnlockingBlocks(tech.Key))
        {
            if (_engine.Get(uk) == null)   // 排除"解锁的科技"（同页面 GetUnlockRows 规则）
                mods.Add((_engine.LocalisedName(uk, lang) ?? uk, UnlockTag ?? "解锁"));
        }
        if (ShipEngine != null)
        {
            foreach (var sk in ShipEngine.GetUnlockingBlocks(tech.Key))
                mods.Add((ShipEngine.LocalisedName(sk, lang) ?? sk, UnlockTag ?? "解锁"));
        }
        float maxValW = 0f;
        foreach (var m in mods)
            maxValW = Math.Max(maxValW, modFont.MeasureText(m.Value ?? ""));
        maxValW = Math.Max(maxValW, modFont.MeasureText(UnlockTag));   // 至少容纳"解锁"标签；数值按实际宽显示全（用户：数值显示不全——去掉 90 上限）
        float rightZone = UnifiedRightZone > 0f ? UnifiedRightZone : Math.Max(costW, Math.Max(maxValW, 24f)) + 12;   // 统一（全局 max）或逐卡

        float textLeft = x + 58;
        float textRight = x + w - rightZone;   // 描述右边界 = 卡片右缘 - 右侧最大占用
        // 行距与页面（WPF）一致：描述行 ≈ (字号-1)×1.4，加成行用页面实测（ModLineH）或估算 (字号-1)×1.5
        float descLineH = Math.Max(12f, (FontSizeScale - 1f) * 1.4f);
        float modLineH = ModLineH > 0f ? ModLineH : Math.Max(14f, (FontSizeScale - 1f) * 1.5f);
        var descFont = Font(FontSizeScale - 1, false);   // 描述
        var desc = _engine.LocalisedDesc(tech.Key, lang);
        if (string.IsNullOrEmpty(desc))
            desc = "—";
        desc = desc.Replace("\\n", "\n");   // 描述值：字面 \n 转义 → 真实换行
        // 描述多行自动换行（**完整显示，不截断 3 行**——卡高按页面完整描述计算，
        // 若导出只画 3 行则卡内下半部空出背景色）
        var descLines = WrapText(desc, descFont, textRight - textLeft);
        int descCount = descLines.Count;
        for (int i = 0; i < descCount; i++)
        {
            // 空行跳过：SKTextBlob.Create("") 返回 null → DrawText(null) 抛 "text"
            if (string.IsNullOrEmpty(descLines[i]))
                continue;
            canvas.DrawText(Blob(descLines[i], descFont), textLeft, contentTop + 12 + i * descLineH, Fill(TextLight));
        }

        // 分隔线 + 加成列表（描述下方，全部行）：**在图标下方开始**（不与上方图标冲突）——
        // 左侧 = 翻译或 key（从卡片左缘起，不留图标位），右侧 = 数值右对齐
        float iconBottom = contentTop + 46;   // 左图标 44 高 + 2 底 padding
        float modTop = Math.Max(contentTop + 12 + descCount * descLineH + 6, iconBottom + 4);
        if (mods.Count > 0)
        {
            canvas.DrawLine(x + 8, modTop - 4, x + w - 8, modTop - 4,
                new SKPaint { Color = new SKColor(0x55, 0x55, 0x66), StrokeWidth = 1 });
            float leftColRight = x + w - 8 - maxValW - 8;   // 左列右边界（数值列之前）
            // 右侧值/解锁标签可用宽度：至少容纳本卡最长值 + 解锁标签（否则"解锁"被截成省略号）
            float valW = Math.Max(Math.Max(20, maxValW - 8), modFont.MeasureText(UnlockTag) + 4);
            for (int i = 0; i < mods.Count; i++)
            {
                float ly = modTop + 12 + modLineH * i;
                string disp = Truncate(mods[i].Display ?? "", modFont, leftColRight - (x + 8));
                if (disp.Length > 0)   // 空文本跳过（Blob("") 返回 null → DrawText 抛）
                    canvas.DrawText(Blob(disp, modFont), x + 8, ly, Fill(TextDim));
                string val = Truncate(mods[i].Value ?? "", modFont, valW);
                if (val.Length > 0)
                    canvas.DrawText(val, x + w - 8, ly, SKTextAlign.Right, modFont, Fill(TextLight));   // 数值右对齐（长字符串才截断）
            }
        }

        // 右侧：学科小图标 + cost + levels/cost_per_level
        float rightX = x + w - 44;
        var catIcon = tech.Categories.Count > 0 ? _engine.GetCategoryIcon(tech.Categories[0]) : null;
        if (catIcon != null)
        {
            var cat = LoadIcon(catIcon);
            if (cat != null)
                canvas.DrawBitmap(cat, new SKRect(rightX + 8, contentTop + 2, rightX + 8 + 24, contentTop + 2 + 24),
                    new SKPaint { IsAntialias = true });
        }
        // cost：**数值右对齐**到卡片右缘（图标右侧），且下移一行不遮挡小图标
        canvas.DrawText(costStr, x + w - 8, contentTop + 42, SKTextAlign.Right, costFont, Fill(TextLight));

        float subY = contentTop + 56;
        if (tech.HasLevels && tech.Levels != 1)           // Levels = 1（默认值）不显示
        {
            var lvlFont = Font(FontSizeScale - 3, false);
            string lvl = tech.Levels < 0 ? "∞" : $"×{tech.Levels}";
            canvas.DrawText(Blob("levels " + lvl, lvlFont), rightX - 6, subY, Fill(TextDim));
            subY += 11;
        }
        if (tech.HasCostPerLevel)
        {
            var cplFont = Font(FontSizeScale - 3, false);
            string cpl = tech.CostPerLevel >= 0 ? "+" + tech.CostPerLevel : tech.CostPerLevel.ToString();
            canvas.DrawText(Blob(cpl, cplFont), rightX - 6, subY, Fill(TextDim));
        }

        // 边框最后画（覆盖底板边缘，四边描边完整且随卡片高度扩展）
        canvas.DrawRoundRect(rect, 10, 10, borderPaint);
    }

    private static SKColor GetAreaColor(string area)
    {
        if (string.Equals(area, "physics", StringComparison.OrdinalIgnoreCase)) return TitlePhysics;
        if (string.Equals(area, "society", StringComparison.OrdinalIgnoreCase)) return TitleSociety;
        if (string.Equals(area, "engineering", StringComparison.OrdinalIgnoreCase)) return TitleEngineering;
        return new SKColor(0x55, 0x55, 0x60);
    }

    private SKBitmap? LoadIcon(string relPath)
    {
        if (_iconCache.TryGetValue(relPath, out var cached))
            return cached;
        SKBitmap? result = null;
        try { result = _iconLoader(relPath); }
        catch { result = null; }
        _iconCache[relPath] = result;
        return result;
    }

    // ==================== 文本工具 ====================

    /// <summary>卡片字号基准（= 用户界面字号，缺省 12；各部分相对该值缩放）。</summary>
    public float FontSizeScale { get; set; } = 12f;

    /// <summary>右侧占用统一值（所有卡描述可用宽度一致）：由页面按全局 max(cost 宽, 数值宽, 图标) 设置；0 = 逐卡计算。</summary>
    public float UnifiedRightZone { get; set; }

    /// <summary>舰船/武器解锁索引（页面注入 ShipEngine）——导出图与页面一致，解锁行也绘制。</summary>
    public ShipEngine? ShipEngine { get; set; }

    /// <summary>解锁行右侧标签（页面传入本地化文本，如"解锁"）。</summary>
    public string UnlockTag { get; set; } = "解锁";

    /// <summary>加成行实际行高（页面 WPF Measure 传入——导出行距与页面一致）；0 = 按字号估算。</summary>
    public float ModLineH { get; set; }

    private SKFont Font(float size, bool bold)
    {
        var key = (size, bold);
        if (_fontCache.TryGetValue(key, out var f))
            return f;
        f = new SKFont(Typeface, size);
        if (bold)
            f.Embolden = true;
        _fontCache[key] = f;
        return f;
    }

    private static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true };
    private static SKTextBlob Blob(string text, SKFont font) => SKTextBlob.Create(text, font);

    /// <summary>按像素宽度自动换行（逐字符断行，中文/英文均适用）。</summary>
    private static List<string> WrapText(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add("");
            return lines;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (ch == '\n')   // 显式换行
            {
                lines.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            if (sb.Length > 0 && font.MeasureText(sb.ToString() + ch) > maxWidth)
            {
                lines.Add(sb.ToString());
                sb.Clear();
            }
            sb.Append(ch);
        }
        if (sb.Length > 0)
            lines.Add(sb.ToString());
        return lines;
    }

    /// <summary>按像素宽度截断文本（省略号）。</summary>
    private static string Truncate(string text, SKFont font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || font.MeasureText(text) <= maxWidth)
            return text;
        string result = text;
        while (result.Length > 1 && font.MeasureText(result + "…") > maxWidth)
            result = result[..^1];
        return result + "…";
    }
}
