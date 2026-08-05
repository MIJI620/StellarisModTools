/*
 * ============================================================================
 * STELLARIS IMAGE ASSET ENGINE STANDARD SPECIFICATION (REVISION 3.0)
 * ============================================================================
 * 本规范为图像素材引擎（ImageAssetEngine）实现的唯一权威依据。
 * 所有实现必须严格遵循本规范定义的接口签名、数据结构、算法与边界条件。
 * 本规范在逻辑上优先于任何现有代码实现，所有实现偏差均视为缺陷。
 * ============================================================================
 *
 *
 * 术语定义
 * --------
 * - 根目录（Root Directory）：模组文件系统的顶层目录路径列表。引擎按
 *   添加顺序维护，列表末尾（索引最大）的目录优先级最高：加载时从高
 *   优先级到低优先级查找文件，导出/删除时写入 Roots[-1]（最高优先级）。
 * - 相对路径（Relative Path）：相对于某个根目录的路径，使用正斜杠 '/'，
 *   不以 '/' 开头。加载路径必须包含文件后缀名；导出路径禁止包含后缀名。
 * - 像素集合（PixelSet）：像素数据的封装类，见 2.1。
 * - 逻辑绘制（无）：图像引擎所有操作均在像素坐标系（左上角为原点）进行。
 *
 *
 * 总体执行原则
 * ------------
 * 1. 所有公开方法执行后必须将结果写入 Status 属性，调用者不应依赖异常
 *    进行流程控制；但引擎内部组件（Loader/Processor/Exporter）通过抛出
 *    特定异常表达失败，由外观层（Engine）捕获并转换为 OperationStatus。
 * 2. 所有公开方法必须线程安全（内部使用 lock (_syncRoot) 串行化）。
 * 3. 耗时操作（加载/解码/编码）在同步方法内执行；引擎仅对 LoadImage
 *    提供 LoadImageAsync 异步重载（方法名加 Async 后缀，支持 CancellationToken）。
 * 4. 引擎不直接依赖任何其他引擎（第六章）。
 *
 *
 * 第一章：引擎概述与架构
 * ======================
 *
 * 1.1 引擎定位
 *     图像素材引擎是 Stellaris.Engine 层的基础组件，负责所有图像文件的
 *     读取、解码、缩放、变换、旋转、拼接、叠加、导出与删除。
 *
 * 1.2 内部组件
 *     - ImageLoader  ：文件查找、解码（PNG/DDS）、LRU 缓存、内存预检。
 *     - ImageProcessor：变换、旋转、拼接、叠加的纯函数实现（无共享状态）。
 *     - ImageExporter ：DDS/PNG 编码与原子写入、文件删除。
 *     - ImageAssetRenderer：静态纯函数（PixelSet ↔ SKBitmap、缩放、背景合成、
 *       变换、DDS 编码）。
 *     外部仅可见 ImageAssetEngine（外观），内部组件均为 internal。
 *
 * 1.3 公开状态属性
 *     - OperationStatus Status { get; private set; }：最近一次操作状态。
 *     - PixelSet? Result { get; private set; }：最近一次操作的输出像素集合。
 *     - (int X, int Y) RotatedCenter { get; private set; }：RotateImage 的
 *       旋转中心在新画布中的坐标。
 *     - bool EnableMemoryCheck { get; }：当前内存检查开关（只读查询）。
 *
 * 1.4 构造函数
 *     ImageAssetEngine(IReadOnlyList<string> roots, ILogger? logger = null,
 *                      bool enableMemoryCheck = true)
 *     - roots：根目录列表（按优先级升序，末尾最高）。
 *     - enableMemoryCheck：初始内存检查开关，默认 true。
 *
 *
 * 第二章：核心数据结构
 * ====================
 *
 * 2.1 像素集合（PixelSet）
 *     类型：public sealed class PixelSet : IDisposable
 *     成员：
 *       - int Width { get; }  ：宽度（像素列数）。
 *       - int Height { get; } ：高度（像素行数）。
 *       - int Channels { get; }：通道数，必须为 3（RGB）或 4（RGBA）。
 *       - byte[][][] Data { get; }：像素数据，索引为 [Height][Width][Channels]。
 *       - PixelSet(byte[][][] data)：构造函数。
 *       - PixelSet Clone()：深拷贝（逐像素复制）。
 *       - void Dispose()：标记释放（幂等）。
 *     构造校验（违反即抛 ArgumentException）：
 *       - data 为 null → ArgumentNullException。
 *       - data.Length == 0 → 空高度维度。
 *       - Channels 不是 3 或 4 → 无效通道数。
 *       - 任意行宽度与首行不一致，或任意像素通道数与 Channels 不一致 → 抛异常。
 *
 * 2.2 图像尺寸（ImageSize）
 *     类型：public readonly struct ImageSize
 *     成员：int Width、int Height；运算符 ==/!=、Equals、GetHashCode、Deconstruct。
 *     约束：构造时 Width 与 Height 必须 > 0，否则抛 ArgumentOutOfRangeException。
 *     所有公开方法中的输出尺寸参数均为 ImageSize?（可选），null 表示不缩放。
 *
 * 2.3 放置区域（Placement）
 *     类型：public readonly struct Placement
 *     成员：int Index（引用 pixelSets 的索引，0 起）、int Left、int Top、
 *           int Right、int Bottom；属性 Width = Right - Left，Height = Bottom - Top。
 *     约束（违反即抛异常）：
 *       - Index < 0 → ArgumentOutOfRangeException。
 *       - Left < 0 或 Top < 0 → ArgumentOutOfRangeException。
 *       - Right <= Left 或 Bottom <= Top → ArgumentException。
 *
 * 2.4 操作状态（OperationStatus）
 *     枚举值：Success、FileNotFound、UnsupportedFormat、InvalidParameter、
 *     IoError、OutOfMemory、UnknownError。
 *     触发映射（外观层 catch）：
 *       - ArgumentException / ArgumentNullException / ArgumentOutOfRangeException
 *         → InvalidParameter。
 *       - NotSupportedException（解码器不识别）→ UnsupportedFormat。
 *       - FileNotFoundException → FileNotFound（DeleteImage / LoadImage）。
 *       - OutOfMemoryException（含内存预检拒绝）→ OutOfMemory。
 *       - IOException（导出原子写入失败）→ IoError。
 *       - 其他异常 → UnknownError（记录 Error 日志）。
 *
 * 2.5 图像格式（ImageFormat）
 *     枚举值：Rgba8888（无压缩 32 位）、Dxt1（BC1，适合不透明）、
 *     Dxt5（BC3，适合带透明）。仅作用于 DDS 输出。
 *
 * 2.6 导出模式（ExportMode）
 *     枚举值：DdsOnly（仅 .dds）、PngOnly（仅 .png）、
 *     DdsAndPng（同时输出 .dds 与 .png）。
 *
 * 2.7 变换操作（TransformOperation）
 *     枚举值：FlipHorizontal、FlipVertical、ScaleProportional、ScaleExact、
 *     Rotate90、RotateMinus90、Rotate180、Rotate270、RotateMinus270。
 *     尺寸影响：
 *       - 翻转：尺寸不变。
 *       - ScaleProportional：等比例缩放至 outputSize 内（无 outputSize 时
 *         保持原尺寸）；缩放系数 = min(outW/srcW, outH/srcH)。
 *       - ScaleExact：拉伸至 outputSize（无 outputSize 时保持原尺寸）。
 *       - Rotate90 / RotateMinus90 / Rotate270 / RotateMinus270：宽高互换，
 *         不丢弃像素。
 *       - Rotate180：尺寸不变。
 *       - 未识别操作：返回原集合副本。
 *
 * 2.8 背景色（backgroundColor）
 *     类型：byte[]?（RGBA 四元组，长度必须为 4，否则抛 InvalidParameter）。
 *     null 表示透明背景。
 *
 * 2.9 路径后缀规则
 *     - 加载（LoadImage）：relativePath 必须包含后缀名（.dds / .png），
 *       否则抛 InvalidParameter；引擎按后缀选择解码器。
 *     - 导出（ExportImage）：relativePath 视为基础名，引擎按 mode 追加
 *       .dds / .png；若调用方传入的字符串本身带扩展名，引擎不会移除。
 *     - 删除（DeleteImage）：relativePath 必须为完整文件名（含后缀）。
 *
 *
 * 第三章：公开接口定义
 * ====================
 *
 * 3.1 图像加载（LoadImage）
 *     void LoadImage(string relativePath, ImageSize? outputSize = null,
 *                    byte[]? backgroundColor = null, bool forceReload = false)
 *     异步重载：Task LoadImageAsync(..., CancellationToken cancellationToken = default)
 *     处理流程：
 *       1) 参数校验（后缀名、backgroundColor 长度、outputSize > 0）。
 *       2) 若 forceReload 为 true：清除该路径的缓存条目（含 LRU 顺序）。
 *       3) 若缓存命中（forceReload 为 false）：更新 LRU 访问顺序，
 *          取缓存副本，依次应用 backgroundColor 与 outputSize，返回。
 *          —— 注意：缓存中保存的是原始尺寸、无背景的像素集合。
 *       4) 缓存未命中：从 Roots 末尾（最高优先级）向前查找文件；
 *          全部根目录均无 → 抛 FileNotFoundException。
 *       5) 按扩展名解码：.png 用 SkiaSharp SKBitmap.Decode；.dds 用
 *          Pfim 解码（Rgba32 直接拷贝；Rgb24 逐像素转 RGBA，Alpha=255；
 *          其他格式按数据长度直接拷贝为 Rgba8888）。
 *       6) 内存预检（见 4.2），失败抛 OutOfMemoryException。
 *       7) 转 PixelSet；将原始副本存入缓存（LRU，见 4.3）。
 *       8) 应用 backgroundColor（Alpha Over，见 3.1-a）与 outputSize
 *          （线性采样缩放），返回最终结果。
 *       9) 解码失败（非 png/dds 或 DDS 损坏）→ 抛 NotSupportedException。
 *
 * 3.1-a 背景合成算法（Alpha Over）
 *     对每个像素：dst = src 按 alpha 合成到背景上。
 *     src 为 4 通道：srcA = src[3]/255，bgA = dst[3]/255，
 *       dst[RGB] = src[RGB]*srcA + dst[RGB]*(1-srcA)，
 *       dst[3] = (srcA + bgA*(1-srcA)) * 255。
 *     src 为 3 通道：直接覆盖背景像素，Alpha 置 255。
 *
 * 3.2 图像变换（TransformImage）
 *     void TransformImage(PixelSet pixelSet, List<TransformOperation> operations,
 *                         ImageSize? outputSize = null)
 *     流程：pixelSet 与 operations 校验（任一为 null 或 operations 为空 →
 *     InvalidParameter）；克隆原集合为工作集；按 operations 顺序依次应用
 *     ApplyTransform；任一步骤失败抛 InvalidOperationException。
 *
 * 3.3 图像旋转（RotateImage）
 *     void RotateImage(PixelSet pixelSet, double angle,
 *                      (int X, int Y)? pivot = null, bool autoExpand = true,
 *                      byte[]? backgroundColor = null)
 *     流程：
 *       1) angle 取模至 [0, 360)：angle = angle % 360，负值加 360。
 *       2) 旋转中心：pivot 未提供时取图像中心（width/2f, height/2f）。
 *       3) autoExpand 为 true：将四角经旋转矩阵映射，取外接矩形尺寸
 *          （Math.Ceiling），计算平移量使旋转后图像居中（translate = -min）。
 *       4) autoExpand 为 false：画布尺寸与原图相同，超出部分截断。
 *       5) 内存预检后创建目标画布，填充背景色或透明。
 *       6) 绘制：canvas.Translate(translate) + RotateDegrees(angle, cx, cy)，
 *          线性采样绘制原图。
 *       7) RotatedCenter = 旋转矩阵映射旋转中心 + 平移量，取整存入公开属性。
 *     注意：pivot 语义为“相对于原始图像左上角的像素坐标”；旋转后新图像
 *     中该点的位置存于 RotatedCenter。
 *
 * 3.4 图像拼接（StitchImages）
 *     void StitchImages(List<PixelSet> pixelSets, int[][] grid, ImageSize cellSize,
 *                       byte[]? backgroundColor = null, ImageSize? outputSize = null)
 *     参数校验：
 *       - pixelSets / grid 为 null，或 grid 空行/空列 → InvalidParameter。
 *       - cellSize 宽高必须 > 0。
 *       - 网格值：0 表示空位；非零值必须 ∈ [1, pixelSets.Count]（索引 1 对应
 *         pixelSets[0]），否则 InvalidParameter。
 *     流程：
 *       1) 自然画布尺寸 = (grid[0].Length × cellSize.Width, grid.Length × cellSize.Height)。
 *       2) 内存预检，创建画布，填充背景色或透明。
 *       3) TileMerger.ComputeMaximalRectangles 计算合并区域（见 3.4-a）。
 *       4) 对每个区域：源 = pixelSets[Index-1]；目标区域尺寸 =
 *          (Width×cellSize.Width, Height×cellSize.Height)；将源线性采样
 *          缩放至目标尺寸后绘制到 (Row×cellSize.Height, Col×cellSize.Width)。
 *       5) 若提供 outputSize，整体缩放至输出尺寸；否则复制画布。
 *
 * 3.4-a 拼接区域合并算法（TileMerger）
 *     采用“连通分量 + 全局最大非重叠矩形优先”策略：
 *       1) 连通分量：对网格中所有非零单元格，按四邻域（上/下/左/右）将
 *          相同数值的单元格聚合为连通分量；不同数值即使相邻也互不干扰。
 *       2) 矩形枚举：对每个连通分量，枚举由任意两个单元格（含相同）为
 *          对角顶点的全部矩形，仅保留“内部所有单元格均属于该分量”的矩形。
 *       3) 去重：按 (Row, Col, Width, Height) 去重。
 *       4) 排序：按面积（Width×Height）降序；面积相同按 Row 升序，再按 Col 升序。
 *       5) 贪心选取：按排序依次选取，若与已选区域无重叠则采纳并标记占用，
 *          直至分量内所有单元格被分配。
 *       6) 最终全部区域按面积降序、行升序、列升序排序返回。
 *     权威示例见旧规范 2.7（L 形、竖条、复杂混合布局）。
 *
 * 3.5 图像叠加（CompositeImages）
 *     void CompositeImages(List<PixelSet> pixelSets, List<Placement> placements,
 *                          byte[]? backgroundColor = null, ImageSize? outputSize = null)
 *     校验：pixelSets / placements 为 null 或 placements 为空 →
 *     InvalidParameter；任一 placement.Index 越界 → InvalidParameter。
 *     流程：
 *       1) 画布尺寸：outputSize 提供时用之；否则取所有 placement 的
 *          (maxRight, maxBottom)（无 placement 时画布为 0×0）。
 *       2) 内存预检，创建画布，填充背景或透明。
 *       3) 按 placements 列表顺序绘制：源 = pixelSets[Index]；缩放至
 *          (Width, Height) 后绘制到 (Left, Top)。后绘制的覆盖先绘制的。
 *       4) 若 outputSize 提供且与自然画布尺寸不同，整体缩放；否则复制。
 *
 * 3.6 图像导出（ExportImage）
 *     void ExportImage(string relativePath, PixelSet pixelSet, ImageFormat format,
 *                      ExportMode mode = ExportMode.DdsOnly,
 *                      ImageSize? outputSize = null, byte[]? backgroundColor = null)
 *     流程：
 *       1) 校验（pixelSet 非空、路径非空、backgroundColor 长度 4、outputSize > 0）。
 *       2) 准备最终图像：先合成背景（Alpha Over），再按 outputSize 缩放。
 *       3) 目标目录：Roots[-1]（最高优先级）；目录不存在则创建。
 *       4) 原子写入：每个文件先写 "{path}.temp"，成功后删除旧文件并
 *          File.Move 重命名。
 *       5) DDS（mode 含 DDS）：BCnEncoder 编码；格式映射 Rgba8888→CompressionFormat.Rgba、
 *          Dxt1→Bc1、Dxt5→Bc3；输出 DDS 文件格式，GenerateMipMaps=false。
 *       6) PNG（mode 含 PNG）：SKImage.FromBitmap → Encode(Png, 100)。
 *       7) 结果判定：DdsOnly 要求 DDS 成功；PngOnly 要求 PNG 成功；
 *          DdsAndPng 要求两者均成功。任一失败抛 IOException
 *          （已成功写入的文件保留在磁盘，不回滚；错误信息含两种格式的
 *          各自成败）。
 *
 * 3.7 图像删除（DeleteImage）
 *     void DeleteImage(string relativePath)
 *     流程：目标 = Roots[-1]/relativePath；文件存在则删除（成功）；
 *     不存在 → 抛 FileNotFoundException（Status = FileNotFound）。
 *
 * 3.8 缓存管理（ClearCache）
 *     void ClearCache()：清空加载缓存与 LRU 顺序。
 *
 * 3.9 内存检查覆盖（OverrideMemoryCheck）
 *     IDisposable OverrideMemoryCheck(bool enabled)
 *     行为：临时将内存检查开关设为 enabled（同步更新 Loader 与 Processor），
 *     返回 IDisposable；Dispose 时恢复覆盖前的值。线程安全、支持嵌套
 *     （每个覆盖记录自己的旧值，恢复时还原上级状态）。
 *
 *
 * 第四章：性能与内存
 * ==================
 *
 * 4.1 线程安全
 *     外观层所有公开方法在 lock (_syncRoot) 内串行执行；内部 Loader 使用
 *     ConcurrentDictionary 缓存，Processor/Exporter/Renderer 无共享状态。
 *
 * 4.2 内存检查分级（仅在开关开启时执行）
 *     Loader.LoadImage 与 Processor 各操作在创建画布/位图前预检：
 *       - 像素数 ≤ 4096×4096（16,777,216）：安全尺寸，直接放行。
 *       - 4096×4096 < 像素数 ≤ 8192×8192（67,108,864）：使用
 *         MemoryFailPoint 预检（按 像素数×4 字节估算，向上取整 MB）；
 *         不足则抛 OutOfMemoryException。
 *       - 像素数 > 8192×8192：直接拒绝（抛 OutOfMemoryException）。
 *     注意：Loader 的预检为简化实现（仅 MemoryFailPoint 兜底）；
 *     Processor 严格按上述三级判定。开关关闭时跳过全部预检。
 *
 * 4.3 加载缓存（LRU）
 *     - 容量上限 50 个条目。
 *     - 缓存键：relativePath（原始字符串）。
 *     - 命中时更新访问顺序（TouchCache）。
 *     - 新增时若已达上限，淘汰最久未使用的条目。
 *     - 缓存条目为原始尺寸、无背景的像素集合副本；每次命中返回其克隆，
 *       再应用背景/缩放，防止外部修改污染缓存。
 *     - 引擎不自动检测磁盘文件变更；需要最新内容必须 forceReload=true。
 *
 *
 * 第五章：错误处理与日志
 * ======================
 * 5.1 非致命错误（格式不支持、文件不存在）记录 Error 级别（含路径）；
 *     参数错误不记日志（直接映射状态）。
 * 5.2 日志使用 Microsoft.Extensions.Logging 标准接口。
 *
 *
 * 第六章：与上层引擎的集成约定
 * ============================
 * 6.1 引擎独立，不依赖其他引擎。
 * 6.2 通过构造函数接收 Roots；上层引擎（GalaxyStyleEngine 等）在初始化时传入。
 * 6.3 引擎除缓存外无业务状态。
 * 6.4 公开接口支持依赖注入/模拟。
 *
 * ============================================================================
 * 规范结束
 * ============================================================================
 */

namespace Stellaris.Engine.ImageAsset;
